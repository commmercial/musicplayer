using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class RiskLauncher
{
    [STAThread]
    private static void Main()
    {
        DirectoryInfo appDirectory = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string installRoot = appDirectory == null ? AppDomain.CurrentDomain.BaseDirectory : appDirectory.FullName;
        string runtime = Path.Combine(installRoot, "Runtime");
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
