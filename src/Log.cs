using System.Diagnostics;

namespace TaskbarPlayer;

/// <summary>Error log: always to the debugger, to a file only with --log or on a crash.</summary>
internal static class Log
{
    private static readonly object Sync = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarPlayer", "error.log");

    public static void Info(string message)
    {
        Debug.WriteLine("[TaskbarPlayer] " + message);
        if (AppConfig.LogToFile) Write("INFO  " + message);
    }

    public static void Error(string context, Exception? ex = null)
    {
        Debug.WriteLine($"[TaskbarPlayer] {context}: {ex}");
        if (AppConfig.LogToFile) Write($"ERROR {context}: {ex}");
    }

    /// <summary>Unhandled exception: always written to the file.</summary>
    public static void Fatal(string context, Exception? ex)
    {
        Debug.WriteLine($"[TaskbarPlayer] FATAL {context}: {ex}");
        Write($"FATAL {context}: {ex}");
    }

    private static void Write(string line)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
