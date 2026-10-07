using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Layerlapse.App.Themes;

namespace Layerlapse.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Track(this);
        ThemeManager.Changed += ApplyChrome;
        Closed += (_, _) => ThemeManager.Changed -= ApplyChrome;
        ApplyChrome(ThemeManager.Current);
    }

    /// <summary>
    /// With the XP theme on Windows, the system title bar is replaced by the XP one (the border stays, so the
    /// window can still be resized). Every other theme and system keeps the normal title bar.
    /// </summary>
    private void ApplyChrome(AppTheme theme)
    {
        var xpChrome = theme == AppTheme.XP && (OperatingSystem.IsWindows() || ThemeManager.AllThemesEverywhere);
        XpTitleBar.IsVisible = xpChrome;
        WindowDecorations = xpChrome ? WindowDecorations.BorderOnly : WindowDecorations.Full;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximized();

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
