namespace TaskbarPlayer;

/// <summary>App settings. The title width can be overridden with --width=300.</summary>
internal static class AppConfig
{
    /// <summary>Width of the title area in DIP (= pixels at 100% scale); scaled by the taskbar DPI.</summary>
    public static int TextWidthDip { get; private set; } = 260;

    /// <summary>Width of one control button (there are three), DIP.</summary>
    public const int ButtonWidthDip = 30;

    /// <summary>Full plaque width: title + buttons.</summary>
    public static int TotalWidthDip => TextWidthDip + ButtonWidthDip * 3;

    /// <summary>Write diagnostics to %LOCALAPPDATA%\TaskbarPlayer\error.log (--log).</summary>
    public static bool LogToFile { get; private set; }

    /// <summary>Skip embedding and use the fallback topmost window right away (--fallback).</summary>
    public static bool ForceFallback { get; private set; }

    /// <summary>Never offer to install, just run from the current location (--portable).</summary>
    public static bool Portable { get; private set; }

    public const string FontFamily = "Segoe UI";
    /// <summary>Font size in pixels at 96 DPI (12 px = 9 pt, same as the taskbar clock).</summary>
    public const float FontSizePx = 12f;
    /// <summary>Horizontal text padding inside the plaque, DIP.</summary>
    public const float PaddingDip = 8f;
    /// <summary>Button icon size (Segoe MDL2 Assets) in pixels at 96 DPI.</summary>
    public const float GlyphSizePx = 12f;

    /// <summary>Taskbar geometry check (safety net for the WinEvent hooks).</summary>
    public const int LayoutCheckIntervalMs = 1000;
    /// <summary>Safety-net media poll (updates normally arrive as events).</summary>
    public const int MediaPollIntervalMs = 2000;
    /// <summary>How long "Copied" stays on screen.</summary>
    public const int CopiedFeedbackMs = 1000;
    /// <summary>Minimum interval between track switches with the mouse wheel.</summary>
    public const int WheelThrottleMs = 300;
    /// <summary>Duration of the title slide animation on a track switch.</summary>
    public const int SlideDurationMs = 220;
    /// <summary>How long to wait for the track to change after next/previous before giving up on the animation.</summary>
    public const int SkipPendingMs = 2500;

    public static void Parse(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--width=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg.Substring("--width=".Length), out int w))
            {
                TextWidthDip = Compat.Clamp(w, 80, 1000);
            }
            else if (arg.Equals("--log", StringComparison.OrdinalIgnoreCase))
            {
                LogToFile = true;
            }
            else if (arg.Equals("--fallback", StringComparison.OrdinalIgnoreCase))
            {
                ForceFallback = true;
            }
            else if (arg.Equals("--portable", StringComparison.OrdinalIgnoreCase))
            {
                Portable = true;
            }
        }
    }
}
