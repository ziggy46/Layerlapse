using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Layerlapse.App.Themes;

namespace Layerlapse.App.Views;

public partial class MainWindow : Window
{
    private bool _xpChrome;
    private IDisposable? _background;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Track(this);
        ThemeManager.Changed += ApplyChrome;
        Closed += (_, _) => ThemeManager.Changed -= ApplyChrome;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                UpdateFrame();
            }
        };
        ApplyChrome(ThemeManager.Current);
    }

    /// <summary>
    /// With the XP theme on Windows the system frame is switched off and the window draws XP's title bar and blue
    /// frame itself (with resize grips). Every other theme and system keeps the normal frame and title bar.
    /// </summary>
    private void ApplyChrome(AppTheme theme)
    {
        _xpChrome = theme == AppTheme.XP && (OperatingSystem.IsWindows() || ThemeManager.AllThemesEverywhere);
        XpTitleBar.IsVisible = _xpChrome;
        WindowDecorations = _xpChrome ? WindowDecorations.None : WindowDecorations.Full;
        TransparencyLevelHint = _xpChrome ? [WindowTransparencyLevel.Transparent] : [];
        _background?.Dispose();
        _background = null;
        if (_xpChrome)
        {
            Background = Brushes.Transparent; // the rounded corners of the XP frame show the desktop
        }
        else
        {
            _background = Bind(BackgroundProperty, this.GetResourceObservable("BackgroundBrush"));
        }

        UpdateFrame();
    }

    private void UpdateFrame()
    {
        var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        Frame.Classes.Set("xp-frame", _xpChrome && !maximized);
        XpTitleBar.Classes.Set("square", maximized);
        ResizeGrips.IsVisible = _xpChrome && !maximized;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string edge } && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && Enum.TryParse<WindowEdge>(edge, out var windowEdge))
        {
            BeginResizeDrag(windowEdge, e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximized();

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
