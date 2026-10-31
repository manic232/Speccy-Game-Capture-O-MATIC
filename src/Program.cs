using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Speccy Game Capture O-MATIC")]
[assembly: AssemblyProduct("Speccy Game Capture O-MATIC")]
[assembly: AssemblyDescription("Records ZX Spectrum gameplay from Spectaculator or Fuse to animated GIF")]
[assembly: AssemblyVersion(SpeccyCapture.Program.Version + ".0.0")]
[assembly: AssemblyFileVersion(SpeccyCapture.Program.Version + ".0.0")]
[assembly: AssemblyInformationalVersion(SpeccyCapture.Program.Version)]

namespace SpeccyCapture
{
    static class Program
    {
        // The version shown in the title bar and in the .exe's file properties. Change it here only.
        public const string Version = "1.0";

        [STAThread]
        static void Main()
        {
            bool firstInstance;
            using (var mutex = new Mutex(true, "SpeccyGameCaptureOMatic.SingleInstance", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("Speccy Game Capture O-MATIC is already running.", "Speccy Game Capture O-MATIC",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
