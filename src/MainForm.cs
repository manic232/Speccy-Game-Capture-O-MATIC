using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SpeccyCapture
{
    sealed class MainForm : Form
    {
        const string AppName = "Speccy Game Capture O-MATIC";
        const string WindowTitle = AppName + " v" + Program.Version;
        const int HotkeyId = 0x5150;

        // Spectaculator already uses F1-F6, F9-F12, Pause and Insert, so these are kept clear of them.
        // (Fuse uses F7 and F8 for its tape controls; a warning is shown if those clash.)
        static readonly string[] HotkeyNames = { "F7", "F8", "Shift+F7", "Shift+F8", "Scroll Lock" };
        static readonly uint[] HotkeyKeys = { 0x76, 0x77, 0x76, 0x77, 0x91 };
        static readonly uint[] HotkeyMods = { 0, 0, Native.MOD_SHIFT, Native.MOD_SHIFT, 0 };

        static readonly string[] SizeNames = { "1x  (native)", "2x", "3x", "4x" };
        static readonly string[] RateNames = { "50 fps  (every frame)", "25 fps", "16.7 fps", "12.5 fps", "10 fps" };
        static readonly string[] BorderNames = { "None", "Small  (16 px)", "Normal  (32 px)", "Large  (48 px)" };

        readonly Settings settings;
        readonly CaptureEngine engine = new CaptureEngine();
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        readonly byte[] frameBuffer = new byte[CaptureEngine.PreviewBufferSize];
        Bitmap previewBitmap;
        int previewWidth = Speccy.Width, previewHeight = Speccy.Height;
        int frameVersion = -1;
        int savesInProgress;
        bool hotkeyRegistered;
        bool trimming;
        string lastSavedPath;

        PreviewPanel preview;
        Label statusDot, statusLabel, recordLabel, savedLabel, hotkeyWarning, sizeHint;
        Button launchButton, recordButton;
        LinkLabel openLink, folderLink;
        ThinProgress saveProgress;
        ComboBox themeBox;
        bool recordLabelActive;
        TextBox emuPathBox, saveFolderBox;
        ComboBox emulatorBox;
        Label emulatorHint;
        int[] previewPalette = Speccy.Palette;
        string lastEmulatorSeen = "";
        ComboBox hotkeyBox, sizeBox, rateBox, borderBox;
        CheckBox loopBox, soundBox, topBox, trimBox;

        public static Bitmap MakeIndexedBitmap(int width, int height, int[] palette)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
            ColorPalette pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = i < 16 ? Color.FromArgb(palette[i] | unchecked((int)0xFF000000)) : Color.Black;
            bmp.Palette = pal;
            return bmp;
        }

        public MainForm()
        {
            settings = Settings.Load();
            engine.BorderSize = settings.Border;
            Emulators.PictureSize(settings.Emulator, settings.Border, out previewWidth, out previewHeight);
            previewBitmap = MakeIndexedBitmap(previewWidth, previewHeight, previewPalette);
            Theme.SetMode(settings.Theme);

            BuildUi();
            LoadSettingsIntoUi();
            Theme.Apply(this);
            UpdateRecordButton();
            Theme.Changed += OnThemeChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnWindowsSettingsChanged;

            uiTimer.Interval = 40;
            uiTimer.Tick += delegate { RefreshUi(); };
        }

        // ---------------------------------------------------------------- layout

        void BuildUi()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = WindowTitle;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Padding = new Padding(12);
            root.Location = new Point(0, 0);

            // Status line
            var statusRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            statusDot = new Label { Tag = "custom", Text = "●", AutoSize = true, ForeColor = Color.Gray, Font = new Font("Segoe UI", 11F), Margin = new Padding(0, 0, 2, 0) };
            statusLabel = new Label { Text = "Starting…", AutoSize = true, Margin = new Padding(0, 3, 0, 0), MaximumSize = new Size(500, 0) };
            statusRow.Controls.Add(statusDot);
            statusRow.Controls.Add(statusLabel);
            root.Controls.Add(statusRow);

            // Live preview (exactly what will go into the GIF)
            preview = new PreviewPanel { Size = new Size(previewWidth * 2, previewHeight * 2), Margin = new Padding(0, 0, 0, 8), Image = previewBitmap, Anchor = AnchorStyles.Top };
            root.Controls.Add(preview);

            // Main buttons
            var buttons = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            launchButton = new Button { Text = "Launch a game…", Dock = DockStyle.Fill, Height = 40, Margin = new Padding(0, 0, 4, 0) };
            launchButton.Click += delegate { LaunchGame(); };
            recordButton = new Button { Tag = "custom", Dock = DockStyle.Fill, Height = 40, Margin = new Padding(4, 0, 0, 0), Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
            recordButton.Click += delegate { ToggleRecording(); };
            buttons.Controls.Add(launchButton, 0, 0);
            buttons.Controls.Add(recordButton, 1, 0);
            root.Controls.Add(buttons);

            recordLabel = new Label { Tag = "custom", AutoSize = true, Margin = new Padding(0, 8, 0, 0), Text = "Not recording" };
            root.Controls.Add(recordLabel);

            saveProgress = new ThinProgress { Width = 512, Height = 6, Visible = false, Margin = new Padding(0, 6, 0, 0) };
            root.Controls.Add(saveProgress);

            var savedRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
            savedLabel = new Label { AutoSize = true, Margin = new Padding(0, 0, 8, 0), MaximumSize = new Size(380, 0) };
            openLink = new LinkLabel { Text = "Open GIF", AutoSize = true, Visible = false, Margin = new Padding(0, 0, 8, 0) };
            openLink.LinkClicked += delegate { OpenPath(lastSavedPath); };
            folderLink = new LinkLabel { Text = "Show in folder", AutoSize = true, Visible = false };
            folderLink.LinkClicked += delegate { ShowInFolder(lastSavedPath); };
            savedRow.Controls.Add(savedLabel);
            savedRow.Controls.Add(openLink);
            savedRow.Controls.Add(folderLink);
            root.Controls.Add(savedRow);

            // Settings
            var heading = new Label { Text = "Settings", AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) };
            root.Controls.Add(heading);
            var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            emulatorBox = MakeCombo(Emulators.Names);
            emulatorBox.SelectedIndexChanged += delegate
            {
                if (emulatorBox.SelectedIndex < 0) return;
                string chosen = Emulators.Names[emulatorBox.SelectedIndex];
                if (chosen == settings.Emulator && emuPathBox.Text.Length > 0) return;
                StoreEmulatorPath();
                settings.Emulator = chosen;
                emuPathBox.Text = EmulatorPath(chosen);
                settings.Save();
                UpdateSizeHint();
                UpdateHotkeyWarning();
            };
            emulatorHint = new Label { Tag = "dim", AutoSize = true, Margin = new Padding(6, 6, 0, 0), Text = "for Launch a game" };
            AddRow(grid, 0, "Emulator:", emulatorBox, emulatorHint);

            emuPathBox = new TextBox { Dock = DockStyle.Fill, Width = 300 };
            emuPathBox.Leave += delegate { StoreEmulatorPath(); settings.Save(); };
            var emuBrowse = new Button { Text = "Browse…", AutoSize = true };
            emuBrowse.Click += delegate { BrowseEmulator(); };
            AddRow(grid, 1, "Emulator path:", emuPathBox, emuBrowse);

            saveFolderBox = new TextBox { Dock = DockStyle.Fill, Width = 300 };
            saveFolderBox.Leave += delegate { settings.SaveFolder = saveFolderBox.Text.Trim(); settings.Save(); };
            var folderButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var folderBrowse = new Button { Text = "Browse…", AutoSize = true };
            folderBrowse.Click += delegate { BrowseSaveFolder(); };
            var folderOpen = new Button { Text = "Open", AutoSize = true };
            folderOpen.Click += delegate { Directory.CreateDirectory(settings.SaveFolder); OpenPath(settings.SaveFolder); };
            folderButtons.Controls.Add(folderBrowse);
            folderButtons.Controls.Add(folderOpen);
            AddRow(grid, 2, "Save GIFs to:", saveFolderBox, folderButtons);

            hotkeyBox = MakeCombo(HotkeyNames);
            hotkeyBox.SelectedIndexChanged += delegate { settings.Hotkey = HotkeyNames[Math.Max(0, hotkeyBox.SelectedIndex)]; settings.Save(); RegisterHotkey(); UpdateRecordButton(); };
            hotkeyWarning = new Label { Tag = "warning", AutoSize = true, Margin = new Padding(6, 6, 0, 0) };
            AddRow(grid, 3, "Start/stop key:", hotkeyBox, hotkeyWarning);

            borderBox = MakeCombo(BorderNames);
            borderBox.SelectedIndexChanged += delegate
            {
                settings.Border = Speccy.BorderSizes[Math.Max(0, borderBox.SelectedIndex)];
                engine.BorderSize = settings.Border;
                settings.Save();
                UpdateSizeHint();
            };
            AddRow(grid, 4, "Border:", borderBox, null);

            sizeBox = MakeCombo(SizeNames);
            sizeBox.SelectedIndexChanged += delegate { settings.Scale = sizeBox.SelectedIndex + 1; settings.Save(); UpdateSizeHint(); };
            sizeHint = new Label { Tag = "dim", AutoSize = true, Margin = new Padding(6, 6, 0, 0) };
            AddRow(grid, 5, "GIF size:", sizeBox, sizeHint);

            rateBox = MakeCombo(RateNames);
            rateBox.SelectedIndexChanged += delegate { settings.FrameStep = rateBox.SelectedIndex + 1; settings.Save(); };
            AddRow(grid, 6, "Frame rate:", rateBox, null);

            themeBox = MakeCombo(Theme.ModeNames);
            themeBox.SelectedIndexChanged += delegate
            {
                settings.Theme = Theme.ModeNames[Math.Max(0, themeBox.SelectedIndex)];
                settings.Save();
                Theme.SetMode(settings.Theme);
            };
            AddRow(grid, 7, "Appearance:", themeBox, null);

            var checks = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
            loopBox = new ThemedCheckBox { Text = "Loop forever", AutoSize = true };
            loopBox.CheckedChanged += delegate { settings.Loop = loopBox.Checked; settings.Save(); };
            soundBox = new ThemedCheckBox { Text = "Beep on start/stop", AutoSize = true };
            soundBox.CheckedChanged += delegate { settings.Sounds = soundBox.Checked; settings.Save(); };
            topBox = new ThemedCheckBox { Text = "Keep this window on top", AutoSize = true };
            topBox.CheckedChanged += delegate { settings.TopMost = topBox.Checked; TopMost = topBox.Checked; settings.Save(); };
            trimBox = new ThemedCheckBox { Text = "Trim before saving", AutoSize = true };
            trimBox.CheckedChanged += delegate { settings.TrimBeforeSaving = trimBox.Checked; settings.Save(); };
            checks.Controls.Add(trimBox);
            checks.Controls.Add(loopBox);
            checks.Controls.Add(soundBox);
            checks.Controls.Add(topBox);
            grid.Controls.Add(checks, 0, 8);
            grid.SetColumnSpan(checks, 3);

            root.Controls.Add(grid);

            Controls.Add(root);
            ResumeLayout(true);
        }

        static ComboBox MakeCombo(string[] items)
        {
            var c = new ThemedComboBox { Width = 200, Anchor = AnchorStyles.Left };
            c.Items.AddRange(items);
            return c;
        }

        static void AddRow(TableLayoutPanel grid, int row, string label, Control main, Control extra)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, row);
            grid.Controls.Add(main, 1, row);
            if (extra != null) grid.Controls.Add(extra, 2, row);
        }

        void LoadSettingsIntoUi()
        {
            emuPathBox.Text = EmulatorPath(settings.Emulator);
            emulatorBox.SelectedIndex = Math.Max(0, Array.IndexOf(Emulators.Names, settings.Emulator));
            saveFolderBox.Text = settings.SaveFolder;
            int hk = Array.IndexOf(HotkeyNames, settings.Hotkey);
            hotkeyBox.SelectedIndex = hk >= 0 ? hk : 1;
            borderBox.SelectedIndex = Math.Max(0, Array.IndexOf(Speccy.BorderSizes, settings.Border));
            sizeBox.SelectedIndex = Math.Max(0, Math.Min(SizeNames.Length - 1, settings.Scale - 1));
            rateBox.SelectedIndex = Math.Max(0, Math.Min(RateNames.Length - 1, settings.FrameStep - 1));
            loopBox.Checked = settings.Loop;
            soundBox.Checked = settings.Sounds;
            topBox.Checked = settings.TopMost;
            trimBox.Checked = settings.TrimBeforeSaving;
            themeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Theme.ModeNames, settings.Theme));
            TopMost = settings.TopMost;
            UpdateRecordButton();
            UpdateSizeHint();
        }

        // The emulator the preview and size hint describe: the connected one, else the chosen one.
        string CurrentEmulator
        {
            get
            {
                string name = engine.Connected ? engine.EmulatorName : "";
                return name.Length > 0 ? name : settings.Emulator;
            }
        }

        void UpdateSizeHint()
        {
            int border = Speccy.BorderSizes[Math.Max(0, borderBox.SelectedIndex)];
            int scale = Math.Max(1, sizeBox.SelectedIndex + 1);
            string emulator = CurrentEmulator;
            int w, h;
            Emulators.PictureSize(emulator, border, out w, out h);
            string text = string.Format("GIF will be {0} × {1}", w * scale, h * scale);
            if (w != Speccy.Width + 2 * border || h != Speccy.Height + 2 * border)
                text += "  (" + emulator + "'s full border)"; // Fuse only draws 32 px at the sides, 24 above and below
            sizeHint.Text = text;
        }

        // Fuse uses F7 and F8 for opening and playing tapes; a global hotkey takes those keys over.
        void UpdateHotkeyWarning()
        {
            if (!hotkeyRegistered)
            {
                hotkeyWarning.Text = "In use by another program";
                return;
            }
            string key = HotkeyName;
            bool fuse = CurrentEmulator == Emulators.Fuse || settings.Emulator == Emulators.Fuse;
            if (fuse && key == "F7") hotkeyWarning.Text = "Takes over Fuse's F7 (open tape)";
            else if (fuse && key == "F8") hotkeyWarning.Text = "Takes over Fuse's F8 (play tape)";
            else hotkeyWarning.Text = "";
        }

        string EmulatorPath(string emulator)
        {
            return emulator == Emulators.Fuse ? settings.FusePath : settings.SpectaculatorPath;
        }

        // Saves what's in the path box as the path of the emulator it belongs to.
        void StoreEmulatorPath()
        {
            string path = emuPathBox.Text.Trim();
            if (settings.Emulator == Emulators.Fuse) settings.FusePath = path;
            else settings.SpectaculatorPath = path;
        }

        // ---------------------------------------------------------------- theme

        void OnThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            SuspendLayout();
            Theme.Apply(this);
            UpdateRecordButton();
            recordLabel.ForeColor = recordLabelActive ? Theme.Current.Recording : Theme.Current.Text;
            ResumeLayout(true);
            Invalidate(true);
        }

        // Follows Windows switching between light and dark while "Match Windows" is chosen.
        void OnWindowsSettingsChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (settings.Theme != "Match Windows") return;
            SafeInvoke(delegate { Theme.SetMode(settings.Theme); });
        }

        // ---------------------------------------------------------------- lifetime

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkey();
            Theme.ApplyTitleBar(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            engine.Start();
            uiTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (engine.IsRecording)
            {
                DialogResult r = ThemedDialog.Show(this, AppName, "You're still recording. Save the GIF before closing?",
                                                   new[] { "Save", "Don't save", "Cancel" },
                                                   new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel }, 0, 2);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                Recording rec = engine.StopRecording();
                if (r == DialogResult.Yes && rec.Frames.Count > 0)
                {
                    Cursor = Cursors.WaitCursor;
                    try
                    {
                        GifWriter.Save(MakeOutputPath(), rec, 0, rec.TotalFrames, settings.FrameStep, settings.Scale, settings.Loop, null);
                    }
                    catch (Exception ex)
                    {
                        ThemedDialog.Error(this, AppName, "Couldn't save the GIF: " + ex.Message);
                    }
                }
            }
            StoreEmulatorPath();
            settings.SaveFolder = saveFolderBox.Text.Trim();
            settings.Save();
            uiTimer.Stop();
            if (hotkeyRegistered) Native.UnregisterHotKey(Handle, HotkeyId);
            Theme.Changed -= OnThemeChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnWindowsSettingsChanged;
            engine.Stop();
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                ToggleRecording();
                return;
            }
            base.WndProc(ref m);
        }

        void RegisterHotkey()
        {
            if (!IsHandleCreated) return;
            if (hotkeyRegistered) Native.UnregisterHotKey(Handle, HotkeyId);
            int i = Math.Max(0, hotkeyBox.SelectedIndex);
            hotkeyRegistered = Native.RegisterHotKey(Handle, HotkeyId, HotkeyMods[i] | Native.MOD_NOREPEAT, HotkeyKeys[i]);
            UpdateHotkeyWarning();
        }

        string HotkeyName
        {
            get { return HotkeyNames[Math.Max(0, hotkeyBox.SelectedIndex)]; }
        }

        // ---------------------------------------------------------------- recording

        void ToggleRecording()
        {
            if (trimming) { System.Media.SystemSounds.Beep.Play(); return; } // finish trimming first
            if (engine.IsRecording) StopAndSave();
            else StartRecording();
        }

        void StartRecording()
        {
            if (!engine.Connected)
            {
                System.Media.SystemSounds.Hand.Play();
                recordLabel.Text = "Can't record yet: no emulator is connected.";
                return;
            }
            engine.StartRecording();
            borderBox.Enabled = false; // the border size is fixed for the length of a recording
            if (settings.Sounds) Beep(true);
            UpdateRecordButton();
        }

        void StopAndSave()
        {
            Recording rec = engine.StopRecording();
            borderBox.Enabled = true;
            if (settings.Sounds) Beep(false);
            UpdateRecordButton();

            if (rec.Frames.Count == 0)
            {
                recordLabel.Text = "Nothing was captured (no frames arrived from the emulator).";
                return;
            }

            long start = 0, end = rec.TotalFrames;
            if (settings.TrimBeforeSaving)
            {
                string title = engine.GameName.Length > 0 ? engine.GameName : "Spectrum";
                trimming = true;
                try
                {
                    using (var trim = new TrimForm(rec, title))
                    {
                        trim.TopMost = TopMost;
                        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                        Activate();
                        if (trim.ShowDialog(this) != DialogResult.OK)
                        {
                            savedLabel.Text = "Recording discarded.";
                            openLink.Visible = folderLink.Visible = false;
                            return;
                        }
                        start = trim.TrimStart;
                        end = trim.TrimEnd;
                    }
                }
                finally
                {
                    trimming = false;
                }
            }

            long totalFrames = end - start;
            string path;
            try
            {
                path = MakeOutputPath();
            }
            catch (Exception ex)
            {
                ThemedDialog.Error(this, AppName, "Can't use the save folder: " + ex.Message);
                return;
            }

            int step = settings.FrameStep, scale = settings.Scale;
            bool loop = settings.Loop;
            savesInProgress++;
            saveProgress.Value = 0;
            saveProgress.Visible = true;
            savedLabel.Text = "Saving " + Path.GetFileName(path) + "…";
            openLink.Visible = folderLink.Visible = false;

            var worker = new Thread(delegate ()
            {
                Exception error = null;
                try
                {
                    GifWriter.Save(path, rec, start, end, step, scale, loop, delegate (int done, int total)
                    {
                        SafeInvoke(delegate { if (total > 0) saveProgress.Value = Math.Min(100, done * 100 / total); });
                    });
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                SafeInvoke(delegate { SaveFinished(path, totalFrames, error); });
            });
            worker.IsBackground = false; // let a save finish even if the window is closed
            worker.Start();
        }

        void SaveFinished(string path, long frames, Exception error)
        {
            savesInProgress--;
            saveProgress.Visible = savesInProgress > 0;
            if (error != null)
            {
                savedLabel.Text = "Couldn't save the GIF: " + error.Message;
                return;
            }
            lastSavedPath = path;
            long bytes = new FileInfo(path).Length;
            savedLabel.Text = string.Format("Saved {0}  ({1}, {2:F1} s)", Path.GetFileName(path), FormatBytes(bytes), frames * Speccy.FrameMs / 1000.0);
            openLink.Visible = folderLink.Visible = true;
        }

        string MakeOutputPath()
        {
            string folder = saveFolderBox.Text.Trim();
            if (folder.Length == 0) folder = settings.SaveFolder;
            Directory.CreateDirectory(folder);

            string name = engine.GameName;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Trim().Length == 0) name = "spectrum";
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string path = Path.Combine(folder, name + "_" + stamp + ".gif");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, name + "_" + stamp + "_" + i + ".gif");
            return path;
        }

        static void Beep(bool starting)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (starting) { Console.Beep(880, 70); Console.Beep(1320, 90); }
                    else { Console.Beep(1320, 70); Console.Beep(880, 90); }
                }
                catch { }
            });
        }

        // ---------------------------------------------------------------- UI refresh

        void RefreshUi()
        {
            bool connected = engine.Connected;
            bool stale = connected && engine.Stale;
            statusDot.ForeColor = connected && !stale ? Color.FromArgb(0, 170, 70) : Color.FromArgb(220, 150, 0);
            string game = engine.GameName;
            string emulator = engine.EmulatorName;
            if (stale) statusLabel.Text = emulator + "'s display isn't updating. Is emulation paused?";
            else if (connected) statusLabel.Text = "Connected to " + emulator + (game.Length > 0 ? "  —  " + game : "");
            else statusLabel.Text = engine.Status;

            // Fuse and Spectaculator differ in border size and F-keys, so refresh those hints on a switch.
            string seen = connected ? emulator : "";
            if (seen != lastEmulatorSeen)
            {
                lastEmulatorSeen = seen;
                UpdateSizeHint();
                UpdateHotkeyWarning();
            }

            int w, h;
            int[] palette;
            if (engine.GetLatest(frameBuffer, ref frameVersion, out w, out h, out palette))
            {
                if (w != previewWidth || h != previewHeight || palette != previewPalette) ResizePreview(w, h, palette);
                BitmapData data = previewBitmap.LockBits(new Rectangle(0, 0, w, h),
                                                         ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                for (int y = 0; y < h; y++)
                    Marshal.Copy(frameBuffer, y * w, data.Scan0 + y * data.Stride, w);
                previewBitmap.UnlockBits(data);
                preview.HasFrame = true;
                preview.Invalidate();
            }

            string message = connected ? null : "Waiting for an emulator…\n\nLaunch a game with the button below,\nor start Spectaculator or Fuse yourself.";
            if (preview.Message != message || preview.ShowImage != connected)
            {
                preview.Message = message;
                preview.ShowImage = connected;
                preview.Invalidate();
            }

            bool recording = engine.IsRecording;
            if (preview.Recording != recording) { preview.Recording = recording; preview.Invalidate(); }

            if (recording)
            {
                long frames = engine.RecordedFrames;
                string time = FormatTime(frames * Speccy.FrameMs / 1000.0);
                recordLabel.Text = string.Format("● Recording   {0}   ({1} frames, {2} so far)", time, frames, FormatBytes(engine.RecordedBytes));
                if (!recordLabelActive)
                {
                    recordLabelActive = true;
                    recordLabel.ForeColor = Theme.Current.Recording;
                }
                Text = "● REC " + time + " – " + WindowTitle;
            }
            else
            {
                if (recordLabelActive)
                {
                    recordLabelActive = false;
                    recordLabel.ForeColor = Theme.Current.Text;
                    recordLabel.Text = "Not recording";
                }
                if (Text != WindowTitle) Text = WindowTitle;
            }
        }

        // The preview is always shown at 2x, so it grows or shrinks when the border changes.
        // It's rebuilt too when the emulator (and so the palette) changes.
        void ResizePreview(int width, int height, int[] palette)
        {
            Bitmap old = previewBitmap;
            previewPalette = palette;
            previewBitmap = MakeIndexedBitmap(width, height, palette);
            preview.Image = previewBitmap;
            old.Dispose();
            previewWidth = width;
            previewHeight = height;
            SizePreview();
        }

        void SizePreview()
        {
            float dpi = CurrentAutoScaleDimensions.Width / 96f;
            preview.Size = new Size((int)Math.Round(previewWidth * 2 * dpi), (int)Math.Round(previewHeight * 2 * dpi));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            SizePreview(); // the first layout pass can shrink it, so set it once the window exists
        }

        void UpdateRecordButton()
        {
            Theme t = Theme.Current;
            if (engine.IsRecording)
            {
                // Solid red while recording, in either theme.
                recordButton.Text = "■  Stop and save GIF  (" + HotkeyName + ")";
                recordButton.FlatStyle = FlatStyle.Flat;
                recordButton.UseVisualStyleBackColor = false;
                recordButton.BackColor = Color.FromArgb(200, 40, 40);
                recordButton.ForeColor = Color.White;
                recordButton.FlatAppearance.BorderColor = Color.FromArgb(160, 25, 25);
                recordButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(215, 55, 55);
                recordButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(175, 30, 30);
            }
            else
            {
                recordButton.Text = "●  Start recording  (" + HotkeyName + ")";
                Theme.StyleButton(recordButton, t);
                recordButton.ForeColor = t.IsDark ? Color.FromArgb(255, 95, 85) : Color.FromArgb(190, 30, 30);
            }
        }

        // ---------------------------------------------------------------- actions

        void LaunchGame()
        {
            string emulator = settings.Emulator;
            string exe = emuPathBox.Text.Trim();
            if (!File.Exists(exe))
            {
                ThemedDialog.Error(this, AppName, "Please choose where " + Emulators.ExeName(emulator) + " is first.");
                if (!BrowseEmulator()) return;
                exe = emuPathBox.Text.Trim();
            }

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose a ZX Spectrum game";
                dlg.Filter = "ZX Spectrum files|*.tzx;*.tap;*.z80;*.sna;*.szx;*.dsk;*.trd;*.scl;*.rzx;*.pzx;*.csw;*.mdr;*.udi;*.mgt;*.img;*.dck;*.zip|All files|*.*";
                if (Directory.Exists(settings.LastGameFolder)) dlg.InitialDirectory = settings.LastGameFolder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                settings.LastGameFolder = Path.GetDirectoryName(dlg.FileName);
                settings.Save();

                // Spectaculator loads the game into its open window. Fuse can't be told to do
                // that from outside, so each launch opens a new Fuse window.
                if (emulator == Emulators.Fuse && !CloseRunningFuse()) return;

                try
                {
                    var psi = new ProcessStartInfo(exe, "\"" + dlg.FileName + "\"");
                    psi.WorkingDirectory = Path.GetDirectoryName(exe);
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    ThemedDialog.Error(this, AppName, "Couldn't start " + emulator + ": " + ex.Message);
                }
            }
        }

        // Offers to close Fuse windows that are already open. Returns false to cancel the launch.
        bool CloseRunningFuse()
        {
            var running = new List<Process>();
            foreach (Process p in Process.GetProcessesByName("fuse"))
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) running.Add(p); } catch { }
            }
            if (running.Count == 0) return true;

            DialogResult r = ThemedDialog.Show(this, AppName,
                "Fuse is already open, and it can't load a game into an open window from outside.\n\nClose Fuse and open the game in a new window?",
                new[] { "Close and open", "Keep it, open another", "Cancel" },
                new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel }, 0, 2);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.No) return true;

            // The same as clicking Fuse's close button: Fuse asks "Exit Fuse?" first, so wait
            // (keeping this window responsive) while that's answered.
            foreach (Process p in running)
            {
                try { p.CloseMainWindow(); } catch { }
            }
            launchButton.Enabled = false;
            Cursor = Cursors.AppStarting;
            try
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 60000)
                {
                    bool allClosed = true, asking = false;
                    foreach (Process p in running)
                    {
                        try
                        {
                            if (p.HasExited) continue;
                            allClosed = false;
                            if (Native.HasDialogOpen(p.Id)) asking = true;
                        }
                        catch { }
                    }
                    if (allClosed) return true;
                    // No prompt showing and still running: the exit was cancelled.
                    if (!asking && clock.ElapsedMilliseconds > 1500) return false;
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
            }
            finally
            {
                Cursor = Cursors.Default;
                launchButton.Enabled = true;
            }
            return false;
        }

        bool BrowseEmulator()
        {
            string emulator = settings.Emulator;
            string exeName = Emulators.ExeName(emulator);
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Where is " + exeName + "?";
                dlg.Filter = emulator + "|" + exeName + "|Programs|*.exe";
                string current = emuPathBox.Text.Trim();
                try
                {
                    if (current.Length > 0 && Directory.Exists(Path.GetDirectoryName(current))) dlg.InitialDirectory = Path.GetDirectoryName(current);
                }
                catch (ArgumentException)
                {
                    // Not a valid path; start wherever the dialog likes.
                }
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                emuPathBox.Text = dlg.FileName;
                StoreEmulatorPath();
                settings.Save();
                return true;
            }
        }

        void BrowseSaveFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Where should GIFs be saved?";
                if (Directory.Exists(saveFolderBox.Text)) dlg.SelectedPath = saveFolderBox.Text;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                saveFolderBox.Text = dlg.SelectedPath;
                settings.SaveFolder = dlg.SelectedPath;
                settings.Save();
            }
        }

        static void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        static void ShowInFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }

        void SafeInvoke(MethodInvoker action)
        {
            try
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Window already closed.
            }
        }

        static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }

        static string FormatTime(double seconds)
        {
            int m = (int)(seconds / 60);
            return string.Format("{0:00}:{1:00.0}", m, seconds - m * 60);
        }
    }

    // Shows the captured frame scaled up with hard pixel edges.
    sealed class PreviewPanel : Control
    {
        public Bitmap Image;
        public bool HasFrame;
        public bool ShowImage;
        public bool Recording;
        public string Message;

        public PreviewPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Current.PreviewBack);

            if (ShowImage && HasFrame && Image != null)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(Image, ClientRectangle);
            }

            if (!string.IsNullOrEmpty(Message))
            {
                using (var brush = new SolidBrush(Color.FromArgb(175, 175, 175)))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(Message, Font, brush, ClientRectangle, fmt);
            }

            if (Recording)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = g.DpiX / 96f;
                var badge = new RectangleF(8 * s, 8 * s, 64 * s, 24 * s);
                using (var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(bg, badge);
                using (var red = new SolidBrush(Color.FromArgb(235, 40, 40))) g.FillEllipse(red, badge.X + 7 * s, badge.Y + 6 * s, 12 * s, 12 * s);
                using (var font = new Font("Segoe UI", 9F, FontStyle.Bold))
                    g.DrawString("REC", font, Brushes.White, badge.X + 24 * s, badge.Y + 3 * s);
            }
        }
    }
}
