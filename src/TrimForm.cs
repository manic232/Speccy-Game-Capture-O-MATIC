using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpeccyCapture
{
    // Shown after recording stops: pick the part of the clip to keep, then save or discard.
    sealed class TrimForm : Form
    {
        readonly Recording rec;
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

        public TrimForm(Recording recording, string title)
        {
            rec = recording;
            frameBitmap = MainForm.MakeIndexedBitmap(rec.Width, rec.Height, rec.Palette);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "Trim recording – " + title;
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
                       "Keys: Space play/pause · ← → step one frame (Shift: 1 second) · [ ] set start/end at the playhead",
            });

            view = new PreviewPanel { Size = new Size(rec.Width * 2, rec.Height * 2), Image = frameBitmap, HasFrame = true, ShowImage = true, Margin = new Padding(0, 0, 0, 8) };
            root.Controls.Add(view);

            bar = new TrimBar { Width = rec.Width * 2, Height = 46, Margin = new Padding(0) };
            bar.SetTotal(rec.TotalFrames);
            bar.Changed += delegate { StopPlaying(); ShowFrame(); UpdateTimes(); };
            root.Controls.Add(bar);

            timesLabel = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
            root.Controls.Add(timesLabel);

            var controls = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            playButton = MakeButton("▶  Play", delegate { TogglePlay(); });
            playButton.Width = 90;
            controls.Controls.Add(playButton);
            controls.Controls.Add(MakeButton("Set start  [", delegate { bar.Start = bar.Position; ShowFrame(); UpdateTimes(); }));
            controls.Controls.Add(MakeButton("Set end  ]", delegate { bar.End = bar.Position + 1; ShowFrame(); UpdateTimes(); }));
            controls.Controls.Add(MakeButton("Keep everything", delegate { bar.Start = 0; bar.End = rec.TotalFrames; ShowFrame(); UpdateTimes(); }));
            root.Controls.Add(controls);

            var bottom = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var discard = new Button { Text = "Discard recording", AutoSize = true, Height = 34, Padding = new Padding(8, 0, 8, 0), Margin = new Padding(0, 0, 8, 0) };
            discard.Click += delegate { Discard(); };
            var save = new Button { Text = "Save GIF", AutoSize = true, Height = 34, Padding = new Padding(16, 0, 16, 0), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(0) };
            save.Click += delegate { StopPlaying(); DialogResult = DialogResult.OK; Close(); };
            bottom.Controls.Add(new Label { AutoSize = true }, 0, 0);
            bottom.Controls.Add(discard, 1, 0);
            bottom.Controls.Add(save, 2, 0);
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK && !discardConfirmed)
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
                case Keys.Home: StopPlaying(); bar.Position = bar.Start; ShowFrame(); UpdateTimes(); return true;
                case Keys.End: StopPlaying(); bar.Position = bar.End - 1; ShowFrame(); UpdateTimes(); return true;
                case Keys.OemOpenBrackets: bar.Start = bar.Position; ShowFrame(); UpdateTimes(); return true;
                case Keys.OemCloseBrackets: bar.End = bar.Position + 1; ShowFrame(); UpdateTimes(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Step(long frames)
        {
            StopPlaying();
            bar.Position = bar.Position + frames;
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
            timesLabel.Text = string.Format("Start {0}    End {1}    Keeping {2:F2} s of {3:F2} s    ·    Playhead {4}",
                Time(bar.Start), Time(bar.End), length / 50.0, rec.TotalFrames / 50.0, Time(bar.Position));
        }

        static string Time(long frames)
        {
            double seconds = frames / 50.0;
            int m = (int)(seconds / 60);
            return string.Format("{0:00}:{1:00.00}", m, seconds - m * 60);
        }
    }

    // Timeline with a start handle, an end handle and a playhead, all in emulator frames.
    // The kept range is [Start, End).
    sealed class TrimBar : Control
    {
        enum DragMode { None, Start, End, Playhead }

        long total = 1, start, end = 1, position;
        DragMode drag;
        float dpi = 1f;

        public event EventHandler Changed;

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
                int pad = (int)(8 * dpi);
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

            DrawHandle(g, xs, r, true);
            DrawHandle(g, xe, r, false);

            // Playhead
            float xp = X(position) + Math.Max(1f, (X(position + 1) - X(position)) / 2);
            using (var pen = new Pen(t.Playhead, 2 * dpi)) g.DrawLine(pen, xp, r.Top - 6 * dpi, xp, r.Bottom);
            using (var brush = new SolidBrush(t.Playhead))
                g.FillPolygon(brush, new[] { new PointF(xp - 5 * dpi, 0), new PointF(xp + 5 * dpi, 0), new PointF(xp, 7 * dpi) });
        }

        void DrawHandle(Graphics g, float x, Rectangle r, bool isStart)
        {
            float w = 7 * dpi;
            var rect = new RectangleF(isStart ? x - w : x, r.Top - 3 * dpi, w, r.Height + 6 * dpi);
            using (var brush = new SolidBrush(Theme.Current.Handle)) g.FillRectangle(brush, rect);
            using (var pen = new Pen(Theme.Current.HandleGrip))
            {
                float cx = rect.X + rect.Width / 2;
                g.DrawLine(pen, cx, rect.Top + rect.Height * 0.3f, cx, rect.Bottom - rect.Height * 0.3f);
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
            ApplyDrag(e.X);
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
            long f = FrameAt(x);
            if (drag == DragMode.Start) Start = f;
            else if (drag == DragMode.End) End = f;
            else if (drag == DragMode.Playhead) Position = f;
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }
}
