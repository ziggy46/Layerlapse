using System.Diagnostics;

namespace Layerlapse.App;

/// <summary>
/// Opens a downloaded 3MF in Bambu Studio when it is installed, because the system's default app for .3mf
/// may be another slicer. Falls back to the default app.
/// </summary>
internal static class BambuStudioLauncher
{
    private const string MacBundleId = "com.bambulab.bambu-studio";

    public static void Open(string path, Action<string> fallback)
    {
        try
        {
            if (OperatingSystem.IsMacOS() && Run("open", "-b", MacBundleId, path))
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    var exe = Path.Combine(Environment.GetFolderPath(root), "Bambu Studio", "bambu-studio.exe");
                    if (File.Exists(exe) && Start(exe, path))
                    {
                        return;
                    }
                }
            }

            if (OperatingSystem.IsLinux() && (Start("bambu-studio", path) || Run("flatpak", "run", "com.bambulab.BambuStudio", path)))
            {
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not installed where we looked.
        }

        fallback(path);
    }

    /// <summary>Runs a launcher command and reports whether it succeeded (open -b fails when the app is missing).</summary>
    private static bool Run(string command, params string[] arguments)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        if (process is null || !process.WaitForExit(10_000))
        {
            return false;
        }

        return process.ExitCode == 0;
    }

    private static bool Start(string executable, string path)
    {
        var info = new ProcessStartInfo(executable) { ArgumentList = { path }, UseShellExecute = false };
        using var process = Process.Start(info);
        return process is not null;
    }
}
