using System.Diagnostics;
using Microsoft.Win32;
using static TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>
/// Per-user install without admin rights: the exe is copied to %LOCALAPPDATA%\Programs\TaskbarPlayer
/// and registered in HKCU\...\Run so it starts with Windows.
/// </summary>
internal static class Installer
{
    private const string AppTitle = "Taskbar Player";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TaskbarPlayer";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TaskbarPlayer");

    public static string InstalledExe => Path.Combine(InstallDir, "TaskbarPlayer.exe");

    public static string CurrentExe => Application.ExecutablePath;

    /// <summary>This process runs from the install folder.</summary>
    public static bool IsInstalledCopy => SamePath(CurrentExe, InstalledExe);

    /// <summary>
    /// Started from somewhere else (e.g. Downloads): offer to install or update.
    /// Returns true when this process should exit right away.
    /// </summary>
    public static bool OfferInstall(out bool declined)
    {
        declined = false;
        bool update = File.Exists(InstalledExe);
        string text = update
            ? "Taskbar Player is already installed.\n\n" +
              "Replace the installed version with this one?\n\n" +
              "Yes: update and restart\n" +
              "No: just run this copy, without installing"
            : "Install Taskbar Player?\n\n" +
              $"It will be copied to\n{InstallDir}\nand start automatically with Windows.\n\n" +
              "Yes: install\n" +
              "No: just run this copy, without installing";

        var answer = MessageBox.Show(text, AppTitle, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return true;
        if (answer == DialogResult.No)
        {
            declined = true;
            return false;
        }

        try
        {
            Install();
        }
        catch (Exception ex)
        {
            Log.Fatal("Install", ex);
            MessageBox.Show("Installation failed:\n\n" + ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return true;
    }

    private static void Install()
    {
        CloseRunningInstance();
        Directory.CreateDirectory(InstallDir);

        // Write a fresh file instead of File.Copy: alternate data streams of the download are not carried over
        byte[] image = File.ReadAllBytes(CurrentExe);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllBytes(InstalledExe, image);
                break;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(300); // the old instance may still be releasing the file
            }
        }

        SetAutostart(InstalledExe, true);
        Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir });
    }

    /// <summary>Asks a running instance to exit cleanly (it restores the taskbar) and waits for it.</summary>
    public static void CloseRunningInstance()
    {
        IntPtr hwnd = FindWindow(null, ControllerWindow.Title);
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            process.WaitForExit(5000);
        }
        catch
        {
            // already gone
        }
    }

    /// <summary>"Start with Windows" is on for this exe.</summary>
    public static bool IsAutostartEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) is string value && SamePath(value.Trim().Trim('"'), CurrentExe);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetAutostart(bool enable) => SetAutostart(CurrentExe, enable);

    private static void SetAutostart(string exePath, bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable) key.SetValue(RunValueName, $"\"{exePath}\"");
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Removes the autostart entry and deletes the install folder once this process has exited.
    /// The caller must exit right after.
    /// </summary>
    public static void ScheduleUninstall()
    {
        SetAutostart(false);
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarPlayer");
        // A running exe cannot delete itself: a hidden cmd waits ~2 s and removes the folders
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\" & rmdir /s /q \"{logDir}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            // Not the install folder: a folder that is some process's current directory cannot be removed
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
