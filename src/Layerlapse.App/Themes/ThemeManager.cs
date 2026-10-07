using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Layerlapse.App.Themes;

public enum AppTheme
{
    Dark,
    Light,

    /// <summary>A Windows XP look (after XP.css), offered on Windows only.</summary>
    XP,
}

/// <summary>
/// Applies the chosen theme. Dark is the default everywhere; XP is a custom Avalonia theme variant (based on
/// Light) whose extra styles are switched on with the "xp" class on each window.
/// </summary>
public static class ThemeManager
{
    /// <summary>The XP theme variant. Its colours live in Theme.axaml like the others.</summary>
    public static ThemeVariant XpVariant { get; } = new("XP", ThemeVariant.Light);

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action<AppTheme>? Changed;

    /// <summary>
    /// Debug builds can offer every theme on every system (LAYERLAPSE_DEV_ALL_THEMES=1), so the XP theme can be
    /// checked on a Mac. Release builds offer XP on Windows only.
    /// </summary>
    public static bool AllThemesEverywhere =>
#if DEBUG
        Environment.GetEnvironmentVariable("LAYERLAPSE_DEV_ALL_THEMES") == "1";
#else
        false;
#endif

    public static IReadOnlyList<AppTheme> Available =>
        OperatingSystem.IsWindows() || AllThemesEverywhere ? [AppTheme.Dark, AppTheme.Light, AppTheme.XP] : [AppTheme.Dark, AppTheme.Light];

    /// <summary>The saved name as a theme this system offers; anything else falls back to Dark.</summary>
    public static AppTheme FromName(string? name) =>
        Enum.TryParse<AppTheme>(name, ignoreCase: true, out var theme) && Available.Contains(theme) ? theme : AppTheme.Dark;

    public static string DisplayName(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light",
        AppTheme.XP => "Windows XP",
        _ => "Dark",
    };

    public static void Apply(AppTheme theme)
    {
        if (!Available.Contains(theme))
        {
            theme = AppTheme.Dark;
        }

        Current = theme;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.XP => XpVariant,
                _ => ThemeVariant.Dark,
            };
        }

        Changed?.Invoke(theme);
    }

    /// <summary>Keeps a window's "xp" class in step with the theme.</summary>
    public static void Track(Window window)
    {
        void Update(AppTheme theme) => window.Classes.Set("xp", theme == AppTheme.XP);
        Update(Current);
        Changed += Update;
        window.Closed += (_, _) => Changed -= Update;
    }
}
