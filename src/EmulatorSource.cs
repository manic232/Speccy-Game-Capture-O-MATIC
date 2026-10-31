using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace SpeccyCapture
{
    // A live link to one running emulator that hands over each finished frame
    // as palette indices (0-15, bright black folded onto 0).
    abstract class EmulatorSource : IDisposable
    {
        public readonly Process Process;

        protected EmulatorSource(Process process)
        {
            Process = process;
        }

        public abstract string Name { get; }

        // RGB colours for indices 0-15, matching the emulator's own colours.
        public abstract int[] Palette { get; }

        // Checks for a new frame. Returns -1 if the link is broken, 0 if there's nothing new,
        // or n > 0 after drawing the new frame into image: n is how many emulator frames
        // it covers since the previous one.
        public abstract int NextFrame(byte[] image, int border);

        // Called when no frames have arrived for a while. May return a fresh link to the same emulator.
        public virtual EmulatorSource Rescan()
        {
            return null;
        }

        // Name of the loaded game, for file names. Empty if unknown.
        public abstract string ReadGameName();

        public abstract void Dispose();
    }

    static class Emulators
    {
        public const string Spectaculator = "Spectaculator";
        public const string Fuse = "Fuse";

        public static readonly string[] Names = { Spectaculator, Fuse };

        public static string ExeName(string emulator)
        {
            return emulator == Fuse ? "fuse.exe" : "Spectaculator.exe";
        }

        // Picture size for a border setting. Fuse only draws 32 px of border at the sides
        // and 24 px above and below, so its borders are capped at that.
        public static void PictureSize(string emulator, int border, out int width, out int height)
        {
            int bx = border, by = border;
            if (emulator == Fuse)
            {
                bx = Math.Min(border, FuseSource.MaxBorderX);
                by = Math.Min(border, FuseSource.MaxBorderY);
            }
            width = Speccy.Width + 2 * bx;
            height = Speccy.Height + 2 * by;
        }

        // Which emulator a process is, or null.
        public static string Identify(Process p)
        {
            try
            {
                string name = p.ProcessName;
                if (name.Equals("Spectaculator", StringComparison.OrdinalIgnoreCase)) return Spectaculator;
                if (name.Equals("fuse", StringComparison.OrdinalIgnoreCase)) return Fuse;
            }
            catch
            {
                // Process has gone.
            }
            return null;
        }

        // Running emulators that have a window: the one in front first, then newest first.
        public static List<Process> FindRunning()
        {
            var list = new List<Process>();
            foreach (string processName in new[] { "Spectaculator", "fuse" })
            {
                foreach (Process p in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) list.Add(p);
                    }
                    catch
                    {
                        // Exited while we looked.
                    }
                }
            }
            int front = ForegroundProcessId();
            list.Sort(delegate (Process a, Process b)
            {
                if (a.Id == front) return -1;
                if (b.Id == front) return 1;
                return StartTime(b).CompareTo(StartTime(a));
            });
            return list;
        }

        // The emulator whose window is in front, or null if the front window isn't an emulator.
        public static Process ForegroundEmulator()
        {
            int pid = ForegroundProcessId();
            if (pid == 0) return null;
            try
            {
                Process p = Process.GetProcessById(pid);
                return Identify(p) != null ? p : null;
            }
            catch
            {
                return null;
            }
        }

        static int ForegroundProcessId()
        {
            IntPtr window = Native.GetForegroundWindow();
            if (window == IntPtr.Zero) return 0;
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            return (int)pid;
        }

        static DateTime StartTime(Process p)
        {
            try
            {
                return p.StartTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        public static EmulatorSource TryConnect(Process process, out string message)
        {
            string emulator = Identify(process);
            if (emulator == Spectaculator) return SpectaculatorSource.TryConnect(process, out message);
            if (emulator == Fuse) return FuseSource.TryConnect(process, out message);
            message = "Not a supported emulator.";
            return null;
        }

        // The game file given on an emulator's command line, or null.
        public static string FileFromCommandLine(Process process)
        {
            string commandLine = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        commandLine = o["CommandLine"] as string;
                        o.Dispose();
                    }
                }
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrEmpty(commandLine)) return null;

            List<string> args = SplitCommandLine(commandLine);
            for (int i = args.Count - 1; i >= 1; i--)
            {
                string a = args[i];
                if (a.StartsWith("-")) continue;
                try
                {
                    if (File.Exists(a)) return a;
                }
                catch
                {
                    // Not a usable path.
                }
            }
            return null;
        }

        static List<string> SplitCommandLine(string s)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in s)
            {
                if (c == '"') { quoted = !quoted; any = true; continue; }
                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any) result.Add(current.ToString());
                    current.Length = 0;
                    any = false;
                    continue;
                }
                current.Append(c);
                any = true;
            }
            if (any) result.Add(current.ToString());
            return result;
        }
    }
}
