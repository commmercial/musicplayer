using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class RiskLauncher
{
    [STAThread]
    private static void Main()
    {
        string runtime = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src", "risk");
        string player = Path.Combine(runtime, "Risk.exe");
        if (!File.Exists(player))
        {
            MessageBox.Show("Risk could not find its app files. Please reinstall Risk.",
                "Risk Music Player", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = player,
            WorkingDirectory = runtime,
            UseShellExecute = true
        });
    }
}
