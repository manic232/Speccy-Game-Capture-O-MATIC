using System;
using System.Collections.Generic;
using System.IO;

namespace SpeccyCapture
{
    // Portable key=value settings file kept next to the .exe (settings.ini).
    // Paths inside the app's own folder are stored relative to it, so the whole folder
    // can be moved or run from a drive with a different letter.
    // If the app's folder can't be written to, it falls back to %APPDATA%.
    sealed class Settings
    {
        public string Emulator = Emulators.Spectaculator;   // which emulator "Launch a game" uses
        public string SpectaculatorPath = "";
        public string FusePath = "";
        public string SaveFolder = "";
        public string LastGameFolder = "";
        public string Hotkey = "F8";
        public int Scale = 1;
        public int FrameStep = 1;
        public bool Loop = true;
        public bool Sounds = true;
        public bool TopMost = false;
        public int Border = 0;        // pixels per side: 0, 16, 32 or 48
        public bool TrimBeforeSaving = true;
        public string Theme = "Match Windows";   // "Match Windows", "Light" or "Dark"

        const string FileName = "settings.ini";

        public static string AppFolder
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); }
        }

        static string PortablePath { get { return Path.Combine(AppFolder, FileName); } }

        // Where older versions kept settings; also the fallback for read-only app folders.
        static string AppDataFolder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "Speccy Game Capture O-MATIC");
            }
        }

        static string AppDataPath { get { return Path.Combine(AppDataFolder, FileName); } }

        public static string DefaultSaveFolder { get { return Path.Combine(AppFolder, "GIFs"); } }

        // The save folder earlier versions used by default.
        static string OldDefaultSaveFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Speccy GIFs"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            bool fromAppData = false;
            try
            {
                string path = null;
                if (File.Exists(PortablePath)) path = PortablePath;
                else if (File.Exists(AppDataPath)) { path = AppDataPath; fromAppData = true; }
                if (path != null) s.Read(path);
            }
            catch
            {
                // Fall back to defaults if the file is unreadable.
            }

            if (string.IsNullOrEmpty(s.SpectaculatorPath)) s.SpectaculatorPath = GuessPath("Spectaculator", "Spectaculator.exe");
            if (string.IsNullOrEmpty(s.FusePath)) s.FusePath = GuessPath("Fuse", "fuse.exe");
            if (string.IsNullOrEmpty(s.SaveFolder)) s.SaveFolder = DefaultSaveFolder;

            // First run of the portable version: move settings out of AppData into the app folder.
            if (fromAppData && AppFolderWritable())
            {
                if (SamePath(s.SaveFolder, OldDefaultSaveFolder)) s.SaveFolder = DefaultSaveFolder;
                if (s.Save()) RemoveAppDataCopy();
            }
            else if (!fromAppData && File.Exists(PortablePath) && Directory.Exists(AppDataFolder))
            {
                RemoveAppDataCopy(); // stale copy from an older version; the portable file wins
            }
            return s;
        }

        void Read(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.TrimStart().StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            string v;
            int n;
            if (values.TryGetValue("Emulator", out v) && Array.IndexOf(Emulators.Names, v) >= 0) Emulator = v;
            if (values.TryGetValue("SpectaculatorPath", out v)) SpectaculatorPath = FromStored(v);
            if (values.TryGetValue("FusePath", out v)) FusePath = FromStored(v);
            if (values.TryGetValue("SaveFolder", out v)) SaveFolder = FromStored(v);
            if (values.TryGetValue("LastGameFolder", out v)) LastGameFolder = FromStored(v);
            if (values.TryGetValue("Hotkey", out v)) Hotkey = v;
            if (values.TryGetValue("Scale", out v) && int.TryParse(v, out n)) Scale = Math.Max(1, Math.Min(4, n));
            if (values.TryGetValue("FrameStep", out v) && int.TryParse(v, out n)) FrameStep = Math.Max(1, Math.Min(5, n));
            if (values.TryGetValue("Loop", out v)) Loop = v == "1";
            if (values.TryGetValue("Sounds", out v)) Sounds = v == "1";
            if (values.TryGetValue("TopMost", out v)) TopMost = v == "1";
            if (values.TryGetValue("Border", out v) && int.TryParse(v, out n)) Border = Array.IndexOf(Speccy.BorderSizes, n) >= 0 ? n : 0;
            if (values.TryGetValue("TrimBeforeSaving", out v)) TrimBeforeSaving = v == "1";
            if (values.TryGetValue("Theme", out v) && Array.IndexOf(SpeccyCapture.Theme.ModeNames, v) >= 0) Theme = v;
        }

        // Returns true if saved in the app folder, false if it had to use AppData (or failed).
        public bool Save()
        {
            string[] lines =
            {
                "; Speccy Game Capture O-MATIC settings. Paths without a drive letter are relative to this folder.",
                "Emulator=" + Emulator,
                "SpectaculatorPath=" + ToStored(SpectaculatorPath),
                "FusePath=" + ToStored(FusePath),
                "SaveFolder=" + ToStored(SaveFolder),
                "LastGameFolder=" + ToStored(LastGameFolder),
                "Hotkey=" + Hotkey,
                "Scale=" + Scale,
                "FrameStep=" + FrameStep,
                "Loop=" + (Loop ? "1" : "0"),
                "Sounds=" + (Sounds ? "1" : "0"),
                "TopMost=" + (TopMost ? "1" : "0"),
                "Border=" + Border,
                "TrimBeforeSaving=" + (TrimBeforeSaving ? "1" : "0"),
                "Theme=" + Theme,
            };
            try
            {
                File.WriteAllLines(PortablePath, lines);
                return true;
            }
            catch
            {
                try
                {
                    Directory.CreateDirectory(AppDataFolder);
                    File.WriteAllLines(AppDataPath, lines);
                }
                catch
                {
                    // Not being able to save settings shouldn't stop the app.
                }
                return false;
            }
        }

        // Paths inside the app folder are written relative to it ("GIFs" rather than "C:\...\GIFs").
        static string ToStored(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                string root = AppFolder + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full.Substring(root.Length);
                if (string.Equals(full, AppFolder, StringComparison.OrdinalIgnoreCase)) return ".";
                return full;
            }
            catch
            {
                return path;
            }
        }

        static string FromStored(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(AppFolder, value));
            }
            catch
            {
                return value;
            }
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                                     Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        static bool AppFolderWritable()
        {
            try
            {
                string probe = Path.Combine(AppFolder, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void RemoveAppDataCopy()
        {
            try
            {
                if (File.Exists(AppDataPath)) File.Delete(AppDataPath);
                // Windows can take a moment to finish a delete, so give the empty folder a few tries.
                // (A non-recursive delete simply fails if anything else is in there.)
                for (int attempt = 0; attempt < 6 && Directory.Exists(AppDataFolder); attempt++)
                {
                    try { Directory.Delete(AppDataFolder); }
                    catch (IOException) { System.Threading.Thread.Sleep(50); }
                }
            }
            catch
            {
                // Leaving the old copy behind is harmless.
            }
        }

        // Looks for <folder>\<exe> in the usual places.
        static string GuessPath(string folder, string exe)
        {
            var guesses = new List<string>
            {
                Path.Combine(@"X:\Emulators", folder, exe),
                Path.Combine(AppFolder, folder, exe),
                Path.Combine(Path.GetDirectoryName(AppFolder) ?? AppFolder, folder, exe),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), folder, exe),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folder, exe),
            };
            foreach (string g in guesses) if (File.Exists(g)) return g;
            return "";
        }
    }
}
