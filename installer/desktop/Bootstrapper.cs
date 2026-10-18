using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class Bootstrapper
{
    [STAThread]
    private static int Main(string[] args)
    {
        string work = Path.Combine(Path.GetTempPath(), "RiskSetup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            // Never keep the installed folder as this process's working directory.
            // The uninstaller must be able to remove the directory containing itself.
            Directory.SetCurrentDirectory(work);
#if UNINSTALL
            string scriptName = "Uninstall.ps1";
            string resourceName = "RiskSetup.Uninstall.ps1";
#else
            string scriptName = "Setup.ps1";
            string resourceName = "RiskSetup.Setup.ps1";
            ExtractResource("RiskSetup.Payload.zip", Path.Combine(work, "RiskPayload.zip"));
            ExtractResource("RiskSetup.Icon.ico", Path.Combine(work, "Risk.ico"));
            ExtractResource("RiskSetup.Logo.png", Path.Combine(work, "RiskLogo.png"));
#endif
            string scriptPath = Path.Combine(work, scriptName);
            ExtractResource(resourceName, scriptPath);

            string powershell = Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe");
            string scriptArgs = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
#if UNINSTALL
            if (Array.Exists(args, delegate(string arg) { return string.Equals(arg, "/confirmed", StringComparison.OrdinalIgnoreCase); }))
                scriptArgs += " -Confirmed";
            string installRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            scriptArgs += " -InstallRoot \"" + installRoot + "\"";
#endif
            ProcessStartInfo start = new ProcessStartInfo(powershell, scriptArgs);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = work;
            Process child = Process.Start(start);
            if (child == null)
                throw new InvalidOperationException("Windows PowerShell could not be started.");
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Risk Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { }
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    private static void ExtractResource(string name, string path)
    {
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (input == null)
                throw new FileNotFoundException("A required Risk Setup resource is missing: " + name);
            using (FileStream output = File.Create(path))
                input.CopyTo(output);
        }
    }
}
