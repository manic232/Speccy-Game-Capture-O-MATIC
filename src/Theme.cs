using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SpeccyCapture
{
    // Light and dark colour schemes. The dark one is deliberately neutral grey/black.
    // Controls can carry a Tag to get a special colour: "dim", "warning" or "custom"
    // (custom = leave alone, the owner colours it).
    sealed class Theme
    {
        public bool IsDark;
        public Color Back;            // window background
        public Color Surface;         // text boxes, buttons, combo boxes
        public Color SurfaceHover;
        public Color SurfacePressed;
        public Color Border;
        public Color Text;
        public Color DimText;
        public Color Highlight;       // selected item in lists
        public Color Link;
        public Color Warning;
        public Color Recording;       // "● Recording" text
        public Color PreviewBack;     // around the preview image
        public Color TrackBack, TrackKept, TrackTicks, Handle, HandleGrip, Playhead;

        public static readonly Theme Light = new Theme
        {
            IsDark = false,
            Back = Color.FromArgb(245, 245, 247),
            Surface = SystemColors.Window,
            SurfaceHover = SystemColors.ControlLight,
            SurfacePressed = SystemColors.ControlDark,
            Border = Color.FromArgb(180, 180, 180),
            Text = SystemColors.ControlText,
            DimText = Color.DimGray,
            Highlight = SystemColors.Highlight,
            Link = Color.FromArgb(0, 102, 204),
            Warning = Color.Firebrick,
            Recording = Color.Firebrick,
            PreviewBack = Color.FromArgb(24, 24, 24),
            TrackBack = Color.FromArgb(205, 205, 212),
            TrackKept = Color.FromArgb(120, 175, 235),
            TrackTicks = Color.FromArgb(110, 110, 120),
            Handle = Color.FromArgb(0, 90, 180),
            HandleGrip = Color.FromArgb(200, 225, 255),
            Playhead = Color.FromArgb(220, 30, 30),
        };

        public static readonly Theme Dark = new Theme
        {
            IsDark = true,
            Back = Color.FromArgb(32, 32, 32),
            Surface = Color.FromArgb(48, 48, 48),
            SurfaceHover = Color.FromArgb(62, 62, 62),
            SurfacePressed = Color.FromArgb(75, 75, 75),
            Border = Color.FromArgb(85, 85, 85),
            Text = Color.FromArgb(228, 228, 228),
            DimText = Color.FromArgb(150, 150, 150),
            Highlight = Color.FromArgb(80, 80, 80),
            Link = Color.FromArgb(200, 200, 200),
            Warning = Color.FromArgb(255, 120, 110),
            Recording = Color.FromArgb(255, 95, 85),
            PreviewBack = Color.FromArgb(12, 12, 12),
            TrackBack = Color.FromArgb(52, 52, 52),
            TrackKept = Color.FromArgb(105, 105, 105),
            TrackTicks = Color.FromArgb(140, 140, 140),
            Handle = Color.FromArgb(215, 215, 215),
            HandleGrip = Color.FromArgb(60, 60, 60),
            Playhead = Color.FromArgb(235, 60, 50),
        };

        public static Theme Current = Light;

        // Raised after Current changes; open windows re-colour themselves.
        public static event EventHandler Changed;

        public static readonly string[] ModeNames = { "Match Windows", "Light", "Dark" };

        public static void SetMode(string mode)
        {
            Theme next;
            if (mode == "Dark") next = Dark;
            else if (mode == "Light") next = Light;
            else next = WindowsUsesDarkApps() ? Dark : Light;
            if (next == Current) return;
            Current = next;
            if (Changed != null) Changed(null, EventArgs.Empty);
        }

        public static bool WindowsUsesDarkApps()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------ applying

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // Dark or light title bar (Windows 10 2004+ / Windows 11).
        public static void ApplyTitleBar(Form form)
        {
            if (!form.IsHandleCreated) return;
            try
            {
                int on = Current.IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(form.Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(form.Handle, 19, ref on, 4); // older Windows 10 builds
                SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020); // redraw frame
            }
            catch
            {
                // Not supported on this Windows version; the title bar just stays light.
            }
        }

        public static void Apply(Control root)
        {
            Theme t = Current;
            Form form = root as Form;
            if (form != null)
            {
                form.BackColor = t.Back;
                form.ForeColor = t.Text;
                ApplyTitleBar(form);
            }
            ApplyTo(root, t);
        }

        static void ApplyTo(Control c, Theme t)
        {
            string role = c.Tag as string;
            if (role != "custom")
            {
                if (c is Button) StyleButton((Button)c, t);
                else if (c is TextBox) StyleTextBox((TextBox)c, t);
                else if (c is ComboBox) StyleCombo((ComboBox)c, t);
                else if (c is CheckBox) StyleCheckBox((CheckBox)c, t);
                else if (c is LinkLabel) StyleLink((LinkLabel)c, t);
                else if (c is Label)
                {
                    c.ForeColor = role == "dim" ? t.DimText : role == "warning" ? t.Warning : t.Text;
                }
                else if (c is Panel || c is TableLayoutPanel || c is FlowLayoutPanel)
                {
                    c.BackColor = t.Back;
                }
            }
            foreach (Control child in c.Controls) ApplyTo(child, t);
            c.Invalidate();
        }

        public static void StyleButton(Button b, Theme t)
        {
            if (t.IsDark)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = t.Surface;
                b.ForeColor = t.Text;
                b.FlatAppearance.BorderColor = t.Border;
                b.FlatAppearance.MouseOverBackColor = t.SurfaceHover;
                b.FlatAppearance.MouseDownBackColor = t.SurfacePressed;
            }
            else
            {
                b.FlatStyle = FlatStyle.Standard;
                b.BackColor = SystemColors.Control;
                b.UseVisualStyleBackColor = true;
                b.ForeColor = SystemColors.ControlText;
            }
        }

        static void StyleTextBox(TextBox box, Theme t)
        {
            box.BackColor = t.IsDark ? t.Surface : SystemColors.Window;
            box.ForeColor = t.IsDark ? t.Text : SystemColors.WindowText;
            box.BorderStyle = t.IsDark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
        }

        static void StyleCheckBox(CheckBox box, Theme t)
        {
            // ThemedCheckBox draws itself in dark mode; this just keeps colours in step.
            box.FlatStyle = FlatStyle.Standard;
            box.ForeColor = t.Text;
            box.BackColor = t.Back;
        }

        static void StyleLink(LinkLabel link, Theme t)
        {
            link.LinkColor = t.Link;
            link.ActiveLinkColor = t.Text;
            link.VisitedLinkColor = t.Link;
            link.LinkBehavior = t.IsDark ? LinkBehavior.AlwaysUnderline : LinkBehavior.SystemDefault;
        }

        // Combo boxes are drawn by us so the dark list doesn't fall back to a blue highlight.
        static void StyleCombo(ComboBox box, Theme t)
        {
            if (box.DrawMode != DrawMode.OwnerDrawFixed)
            {
                box.DrawMode = DrawMode.OwnerDrawFixed;
                box.ItemHeight = box.Font.Height + 4;
                box.DrawItem += DrawComboItem;
            }
            box.FlatStyle = t.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
            box.BackColor = t.IsDark ? t.Surface : SystemColors.Window;
            box.ForeColor = t.IsDark ? t.Text : SystemColors.WindowText;
        }

        static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            var box = (ComboBox)sender;
            Theme t = Current;
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool editArea = (e.State & DrawItemState.ComboBoxEdit) != 0;

            Color back, fore;
            if (t.IsDark)
            {
                back = selected && !editArea ? t.Highlight : t.Surface;
                fore = t.Text;
            }
            else
            {
                back = selected ? SystemColors.Highlight : SystemColors.Window;
                fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
            }
            using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.Bounds);
            Rectangle text = e.Bounds;
            text.Inflate(-3, 0);
            TextRenderer.DrawText(e.Graphics, box.Items[e.Index].ToString(), box.Font, text, fore,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // Drop-down list whose arrow button follows the theme. In dark mode Windows still
    // draws a white arrow strip, so we paint our own button and border on top of it.
    sealed class ThemedComboBox : ComboBox
    {
        const int WM_PAINT = 0x000F;

        public ThemedComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT && Theme.Current.IsDark) PaintDarkChrome();
        }

        protected override void OnDropDown(EventArgs e) { base.OnDropDown(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }

        void PaintDarkChrome()
        {
            Theme t = Theme.Current;
            using (Graphics g = Graphics.FromHwnd(Handle))
            {
                Rectangle r = ClientRectangle;
                int buttonWidth = SystemInformation.HorizontalScrollBarArrowWidth + 4;
                var button = new Rectangle(r.Right - buttonWidth, r.Top, buttonWidth, r.Height);
                using (var back = new SolidBrush(DroppedDown ? t.SurfacePressed : t.Surface)) g.FillRectangle(back, button);

                // Small down-pointing arrow
                float cx = button.Left + button.Width / 2f, cy = button.Top + button.Height / 2f;
                float s = Math.Max(3f, button.Height / 7f);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var arrow = new SolidBrush(t.DimText))
                    g.FillPolygon(arrow, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx + s, cy - s / 2), new PointF(cx, cy + s / 2 + 1) });
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

                using (var pen = new Pen(t.Border)) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
        }
    }

    // Tick box that is drawn by hand in dark mode so the tick stands out clearly:
    // ticked = solid light-grey box with a dark tick, unticked = dark box with a grey outline.
    sealed class ThemedCheckBox : CheckBox
    {
        bool hover;

        public ThemedCheckBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            if (!t.IsDark) { base.OnPaint(e); return; }

            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : t.Back);

            float dpi = g.DpiX / 96f;
            int size = (int)Math.Round(14 * dpi);
            int top = (Height - size) / 2;
            var box = new Rectangle(0, top, size, size);

            if (Checked)
            {
                using (var fill = new SolidBrush(hover ? Color.FromArgb(235, 235, 235) : Color.FromArgb(205, 205, 205))) g.FillRectangle(fill, box);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(20, 20, 20), 2f * dpi))
                {
                    pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.Left + size * 0.22f, box.Top + size * 0.52f),
                        new PointF(box.Left + size * 0.42f, box.Top + size * 0.72f),
                        new PointF(box.Left + size * 0.78f, box.Top + size * 0.30f),
                    });
                }
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            }
            else
            {
                using (var fill = new SolidBrush(t.Surface)) g.FillRectangle(fill, box);
                using (var pen = new Pen(hover ? Color.FromArgb(190, 190, 190) : Color.FromArgb(140, 140, 140)))
                    g.DrawRectangle(pen, box.X, box.Y, box.Width - 1, box.Height - 1);
            }

            var textArea = new Rectangle(box.Right + (int)(5 * dpi), 0, Width - box.Right - (int)(5 * dpi), Height);
            TextRenderer.DrawText(g, Text, Font, textArea, Enabled ? t.Text : t.DimText,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, new Rectangle(textArea.X - 2, 1, Math.Min(textArea.Width, TextRenderer.MeasureText(Text, Font).Width + 4), Height - 2), t.Text, t.Back);
        }
    }

    // A slim progress bar that follows the theme (the standard one is always light).
    sealed class ThinProgress : Control
    {
        int value;

        public ThinProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Tag = "custom";
        }

        public int Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            using (var back = new SolidBrush(t.TrackBack)) e.Graphics.FillRectangle(back, ClientRectangle);
            using (var fill = new SolidBrush(t.IsDark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(6, 176, 37)))
                e.Graphics.FillRectangle(fill, 0, 0, Width * value / 100, Height);
        }
    }

    // MessageBox replacement that follows the theme.
    sealed class ThemedDialog : Form
    {
        ThemedDialog(string title, string message, string[] buttons, DialogResult[] results, int defaultIndex, int cancelIndex)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(18, 16, 18, 14), Location = new Point(0, 0) };
            root.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 18) });
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                var b = new Button { Text = buttons[i], AutoSize = true, MinimumSize = new Size(80, 28), DialogResult = results[i], Margin = new Padding(8, 0, 0, 0) };
                row.Controls.Add(b);
                if (i == defaultIndex) AcceptButton = b;
                if (i == cancelIndex) CancelButton = b;
            }
            root.Controls.Add(row);
            Controls.Add(root);
            ResumeLayout(true);
            Theme.Apply(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var accept = AcceptButton as Button;
            if (accept != null) accept.Focus();
        }

        public static DialogResult Show(IWin32Window owner, string title, string message, string[] buttons,
                                        DialogResult[] results, int defaultIndex, int cancelIndex)
        {
            using (var d = new ThemedDialog(title, message, buttons, results, defaultIndex, cancelIndex))
                return d.ShowDialog(owner);
        }

        public static void Error(IWin32Window owner, string title, string message)
        {
            Show(owner, title, message, new[] { "OK" }, new[] { DialogResult.OK }, 0, 0);
        }

        public static bool Confirm(IWin32Window owner, string title, string message, string yes, string no)
        {
            // "No" is the default so an accidental Enter doesn't throw anything away.
            return Show(owner, title, message, new[] { yes, no }, new[] { DialogResult.Yes, DialogResult.No }, 1, 1) == DialogResult.Yes;
        }
    }
}
