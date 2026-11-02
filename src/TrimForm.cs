using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpeccyCapture
{
    // Pick the part of a clip to keep. Shown after recording stops (save or discard), or for
    // a GIF that was already saved (save a trimmed copy, replace the original, or cancel).
    sealed class TrimForm : Form
    {
        readonly Recording rec;
        readonly bool savedGif;
        readonly Bitmap frameBitmap;
        readonly PreviewPanel view;
        readonly TrimBar bar;
        readonly Label timesLabel;
        readonly Button playButton;
        readonly Timer playTimer = new Timer();
        readonly Stopwatch playClock = new Stopwatch();
        long playFrom;
        int shownIndex = -1;
        bool discardConfirmed;

        public long TrimStart { get { return bar.Start; } }
        public long TrimEnd { get { return bar.End; } }

        // For a saved GIF: true = replace the original file, false = save a trimmed copy.
        public bool ReplaceOriginal { get; private set; }

        public TrimForm(Recording recording, string title, bool editingSavedGif)
        {
            rec = recording;
            savedGif = editingSavedGif;
            frameBitmap = MainForm.MakeIndexedBitmap(rec.Width, rec.Height, rec.Palette);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = (savedGif ? "Trim GIF – " : "Trim recording – ") + title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            KeyPreview = true;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12), Location = new Point(0, 0) };

            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(rec.Width * 2, 0),
                Margin = new Padding(0, 0, 0, 8),
                Text = "Drag the handles to choose what to keep. Click the bar to look at any frame.\n" +
                       "To fine-tune, click a handle (or press Tab) and use ← → to move it one frame (Shift: 1 second).\n" +
                       "Space play/pause · [ ] set start/end at the playhead · Home/End jump to start/end",
            });

            view = new PreviewPanel { Size = new Size(rec.Width * 2, rec.Height * 2), Image = frameBitmap, HasFrame = true, ShowImage = true, Margin = new Padding(0, 0, 0, 8) };
            root.Controls.Add(view);

            bar = new TrimBar { Width = rec.Width * 2, Height = 46, Margin = new Padding(0) };
            bar.SetTotal(rec.TotalFrames);
            bar.Changed += delegate { StopPlaying(); ShowFrame(); UpdateTimes(); };
            root.Controls.Add(bar);

            // Two lines: the times, then what the arrow keys move. Kept to the preview's width so
            // the window doesn't change size as the text changes.
            timesLabel = new Label { AutoSize = true, MaximumSize = new Size(rec.Width * 2, 0), Margin = new Padding(0, 6, 0, 6) };
            root.Controls.Add(timesLabel);

            var controls = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            playButton = MakeButton("▶  Play", delegate { TogglePlay(); });
            playButton.Width = 90;
            controls.Controls.Add(playButton);
            controls.Controls.Add(MakeButton("Set start  [", delegate { SetStartHere(); }));
            controls.Controls.Add(MakeButton("Set end  ]", delegate { SetEndHere(); }));
            controls.Controls.Add(MakeButton("Keep everything", delegate
            {
                bar.Start = 0; bar.End = rec.TotalFrames; bar.Selected = TrimPart.Playhead; ShowFrame(); UpdateTimes();
            }));
            root.Controls.Add(controls);

            var bottom = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(new Label { AutoSize = true }, 0, 0);
            Button save;
            if (savedGif)
            {
                var cancel = MakeBottomButton("Cancel", false);
                cancel.Click += delegate { StopPlaying(); DialogResult = DialogResult.Cancel; Close(); };
                var copy = MakeBottomButton("Save as new GIF", false);
                copy.Click += delegate { Finish(false); };
                save = MakeBottomButton("Replace original", true);
                save.Click += delegate { Finish(true); };
                bottom.Controls.Add(cancel, 1, 0);
                bottom.Controls.Add(copy, 2, 0);
                bottom.Controls.Add(save, 3, 0);
                CancelButton = cancel;
            }
            else
            {
                var discard = MakeBottomButton("Discard recording", false);
                discard.Click += delegate { Discard(); };
                save = MakeBottomButton("Save GIF", true);
                save.Click += delegate { Finish(false); };
                bottom.Controls.Add(discard, 2, 0);
                bottom.Controls.Add(save, 3, 0);
            }
            root.Controls.Add(bottom);

            Controls.Add(root);
            AcceptButton = save;
            ResumeLayout(true);

            Theme.Apply(this);

            playTimer.Interval = 10;
            playTimer.Tick += delegate { PlayTick(); };

            ShowFrame();
            UpdateTimes();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Recording is usually stopped from inside Spectaculator, so pull focus over here
            // so the trim keys work straight away.
            Activate();
            bar.Focus();
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 30, Padding = new Padding(6, 0, 6, 0), Margin = new Padding(0, 0, 6, 0) };
            b.Click += click;
            return b;
        }

        static Button MakeBottomButton(string text, bool main)
        {
            return main
                ? new Button { Text = text, AutoSize = true, Height = 34, Padding = new Padding(16, 0, 16, 0), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(0) }
                : new Button { Text = text, AutoSize = true, Height = 34, Padding = new Padding(8, 0, 8, 0), Margin = new Padding(0, 0, 8, 0) };
        }

        void Finish(bool replace)
        {
            StopPlaying();
            ReplaceOriginal = replace;
            DialogResult = DialogResult.OK;
            Close();
        }

        void SetStartHere()
        {
            bar.Start = bar.Position;
            bar.Selected = TrimPart.Start;
            ShowFrame();
            UpdateTimes();
        }

        void SetEndHere()
        {
            bar.End = bar.Position + 1;
            bar.Selected = TrimPart.End;
            ShowFrame();
            UpdateTimes();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // A saved GIF is still on disk, so closing just cancels. A new recording would be lost.
            if (!savedGif && DialogResult != DialogResult.OK && !discardConfirmed)
            {
                // Closing the window is treated as discarding, so double-check.
                if (!ThemedDialog.Confirm(this, "Discard recording", "Throw this recording away without saving it?", "Discard", "Keep"))
                {
                    e.Cancel = true;
                    return;
                }
            }
            StopPlaying();
            base.OnFormClosing(e);
        }

        void Discard()
        {
            StopPlaying();
            if (!ThemedDialog.Confirm(this, "Discard recording", "Throw this recording away without saving it?", "Discard", "Keep")) return;
            discardConfirmed = true;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            long second = 50;
            switch (keyData)
            {
                case Keys.Space: TogglePlay(); return true;
                case Keys.Left: Step(-1); return true;
                case Keys.Right: Step(1); return true;
                case Keys.Shift | Keys.Left: Step(-second); return true;
                case Keys.Shift | Keys.Right: Step(second); return true;
                case Keys.Tab: Select((TrimPart)(((int)bar.Selected + 1) % 3)); return true;
                case Keys.Shift | Keys.Tab: Select((TrimPart)(((int)bar.Selected + 2) % 3)); return true;
                case Keys.Escape:
                    if (bar.Selected == TrimPart.Playhead) break; // closes, for a saved GIF
                    Select(TrimPart.Playhead);
                    return true;
                case Keys.Home: StopPlaying(); bar.Selected = TrimPart.Playhead; bar.Position = bar.Start; ShowFrame(); UpdateTimes(); return true;
                case Keys.End: StopPlaying(); bar.Selected = TrimPart.Playhead; bar.Position = bar.End - 1; ShowFrame(); UpdateTimes(); return true;
                case Keys.OemOpenBrackets: SetStartHere(); return true;
                case Keys.OemCloseBrackets: SetEndHere(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Moves the playhead or the selected handle.
        void Step(long frames)
        {
            StopPlaying();
            bar.Nudge(frames);
            ShowFrame();
            UpdateTimes();
        }

        // Chooses what the arrow keys move. Picking a handle puts the playhead on the frame at that cut.
        void Select(TrimPart part)
        {
            StopPlaying();
            bar.Selected = part;
            if (part == TrimPart.Start) bar.Position = bar.Start;
            else if (part == TrimPart.End) bar.Position = bar.End - 1;
            ShowFrame();
            UpdateTimes();
        }

        void TogglePlay()
        {
            if (playTimer.Enabled) { StopPlaying(); return; }
            if (bar.Position < bar.Start || bar.Position >= bar.End - 1) bar.Position = bar.Start;
            playFrom = bar.Position;
            playClock.Restart();
            playTimer.Start();
            playButton.Text = "❚❚  Pause";
        }

        void StopPlaying()
        {
            if (!playTimer.Enabled) return;
            playTimer.Stop();
            playButton.Text = "▶  Play";
        }

        void PlayTick()
        {
            long pos = playFrom + (long)(playClock.Elapsed.TotalMilliseconds / Speccy.FrameMs);
            if (pos >= bar.End)
            {
                // Loop the kept section, like the finished GIF will.
                playFrom = bar.Start;
                playClock.Restart();
                pos = bar.Start;
            }
            bar.Position = pos;
            ShowFrame();
            UpdateTimes();
        }

        void ShowFrame()
        {
            int index = rec.IndexAt(bar.Position);
            if (index == shownIndex) return;
            shownIndex = index;
            byte[] pixels = rec.Decode(index);
            BitmapData data = frameBitmap.LockBits(new Rectangle(0, 0, rec.Width, rec.Height), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            for (int y = 0; y < rec.Height; y++)
                Marshal.Copy(pixels, y * rec.Width, data.Scan0 + y * data.Stride, rec.Width);
            frameBitmap.UnlockBits(data);
            view.Invalidate();
        }

        void UpdateTimes()
        {
            long length = bar.End - bar.Start;
            string moves = bar.Selected == TrimPart.Start ? "the start handle   (Esc: back to the playhead)"
                         : bar.Selected == TrimPart.End ? "the end handle   (Esc: back to the playhead)"
                         : "the playhead";
            timesLabel.Text = string.Format("Start {0}    End {1}    Keeping {2:F2} s of {3:F2} s    ·    Playhead {4}\n← → move {5}",
                Time(bar.Start), Time(bar.End), length / 50.0, rec.TotalFrames / 50.0, Time(bar.Position), moves);
        }

        static string Time(long frames)
        {
            double seconds = frames / 50.0;
            int m = (int)(seconds / 60);
            return string.Format("{0:00}:{1:00.00}", m, seconds - m * 60);
        }
    }

    // Which part of the timeline the arrow keys move.
    enum TrimPart { Playhead, Start, End }

    // Timeline with a start handle, an end handle and a playhead, all in emulator frames.
    // The kept range is [Start, End).
    sealed class TrimBar : Control
    {
        enum DragMode { None, Start, End, Playhead }

        long total = 1, start, end = 1, position;
        DragMode drag;
        float grabOffset; // pixels between the mouse and the handle being dragged
        TrimPart selected = TrimPart.Playhead;
        float dpi = 1f;

        public event EventHandler Changed;

        public TrimPart Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        // Moves whichever part is selected. Moving a handle also moves the playhead onto
        // the frame at the cut, so the preview shows exactly where the clip will start or end.
        public void Nudge(long frames)
        {
            if (selected == TrimPart.Start) Start = start + frames;
            else if (selected == TrimPart.End) End = end + frames;
            else Position = position + frames;
        }

        public TrimBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public bool Dragging { get { return drag != DragMode.None; } }

        public void SetTotal(long frames)
        {
            total = Math.Max(1, frames);
            start = 0;
            end = total;
            position = 0;
            Invalidate();
        }

        public long Start
        {
            get { return start; }
            set { start = Clamp(value, 0, end - 1); position = start; Invalidate(); }
        }

        public long End
        {
            get { return end; }
            set { end = Clamp(value, start + 1, total); position = end - 1; Invalidate(); }
        }

        public long Position
        {
            get { return position; }
            set { position = Clamp(value, 0, total - 1); Invalidate(); }
        }

        static long Clamp(long v, long lo, long hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        Rectangle Track
        {
            get
            {
                int pad = (int)(12 * dpi); // room for a handle and its selection outline at either end
                int top = (int)(10 * dpi);
                return new Rectangle(pad, top, Width - 2 * pad, Height - top - (int)(14 * dpi));
            }
        }

        float X(long t)
        {
            Rectangle r = Track;
            return r.Left + (float)t * r.Width / total;
        }

        long FrameAt(int x)
        {
            Rectangle r = Track;
            return (long)Math.Round((double)(x - r.Left) * total / r.Width);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            dpi = g.DpiX / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : SystemColors.Control);
            Rectangle r = Track;

            Theme t = Theme.Current;
            using (var trackBrush = new SolidBrush(t.TrackBack)) g.FillRectangle(trackBrush, r);
            float xs = X(start), xe = X(end);
            using (var keepBrush = new SolidBrush(t.TrackKept)) g.FillRectangle(keepBrush, xs, r.Top, xe - xs, r.Height);

            // Second ticks along the bottom, spaced so they don't crowd.
            double seconds = total / 50.0;
            int stepSec = seconds <= 10 ? 1 : seconds <= 30 ? 2 : seconds <= 60 ? 5 : seconds <= 180 ? 10 : 30;
            using (var tickPen = new Pen(t.TrackTicks))
            using (var tickFont = new Font("Segoe UI", 7F))
            using (var tickBrush = new SolidBrush(t.TrackTicks))
            {
                for (int s = 0; s <= seconds; s += stepSec)
                {
                    float x = X(s * 50L);
                    g.DrawLine(tickPen, x, r.Bottom, x, r.Bottom + 3 * dpi);
                    g.DrawString(s + "s", tickFont, tickBrush, x - 6 * dpi, r.Bottom + 2 * dpi);
                }
            }

            DrawHandle(g, xs, r, true, selected == TrimPart.Start);
            DrawHandle(g, xe, r, false, selected == TrimPart.End);

            // Playhead
            float xp = X(position) + Math.Max(1f, (X(position + 1) - X(position)) / 2);
            using (var pen = new Pen(t.Playhead, 2 * dpi)) g.DrawLine(pen, xp, r.Top - 6 * dpi, xp, r.Bottom);
            using (var brush = new SolidBrush(t.Playhead))
                g.FillPolygon(brush, new[] { new PointF(xp - 5 * dpi, 0), new PointF(xp + 5 * dpi, 0), new PointF(xp, 7 * dpi) });
        }

        void DrawHandle(Graphics g, float x, Rectangle r, bool isStart, bool isSelected)
        {
            float w = 7 * dpi;
            var rect = new RectangleF(isStart ? x - w : x, r.Top - 3 * dpi, w, r.Height + 6 * dpi);
            using (var brush = new SolidBrush(Theme.Current.Handle)) g.FillRectangle(brush, rect);
            using (var pen = new Pen(Theme.Current.HandleGrip))
            {
                float cx = rect.X + rect.Width / 2;
                g.DrawLine(pen, cx, rect.Top + rect.Height * 0.3f, cx, rect.Bottom - rect.Height * 0.3f);
            }
            if (isSelected)
            {
                // Outline to show that the arrow keys move this handle.
                using (var pen = new Pen(Theme.Current.Text, 2 * dpi))
                    g.DrawRectangle(pen, rect.X - 2.5f * dpi, rect.Y - 2.5f * dpi, rect.Width + 5 * dpi, rect.Height + 5 * dpi);
            }
        }

        DragMode HitTest(int x)
        {
            float xs = X(start), xe = X(end);
            float grab = 9 * dpi;
            bool nearStart = Math.Abs(x - (xs - 3 * dpi)) <= grab;
            bool nearEnd = Math.Abs(x - (xe + 3 * dpi)) <= grab;
            if (nearStart && nearEnd) return x < (xs + xe) / 2 ? DragMode.Start : DragMode.End;
            if (nearStart) return DragMode.Start;
            if (nearEnd) return DragMode.End;
            return DragMode.Playhead;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            drag = HitTest(e.X);
            // Grabbing a handle selects it for the arrow keys. It only moves once the mouse
            // does, so a plain click doesn't nudge it off the frame you chose.
            if (drag == DragMode.Start) { selected = TrimPart.Start; grabOffset = X(start) - e.X; position = start; }
            else if (drag == DragMode.End) { selected = TrimPart.End; grabOffset = X(end) - e.X; position = end - 1; }
            else { selected = TrimPart.Playhead; grabOffset = 0; ApplyDrag(e.X); }
            Invalidate();
            if (drag != DragMode.Playhead && Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (drag != DragMode.None) ApplyDrag(e.X);
            else Cursor = HitTest(e.X) == DragMode.Playhead ? Cursors.Hand : Cursors.SizeWE;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            drag = DragMode.None;
        }

        void ApplyDrag(int x)
        {
            long f = FrameAt((int)Math.Round(x + grabOffset));
            if (drag == DragMode.Start) Start = f;
            else if (drag == DragMode.End) End = f;
            else if (drag == DragMode.Playhead) Position = f;
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }
}
