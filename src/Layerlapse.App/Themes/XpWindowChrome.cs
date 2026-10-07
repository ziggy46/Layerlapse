using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Layerlapse.App.Themes;

/// <summary>
/// A window's content wrapped in XP's title bar and blue frame. With the XP theme on Windows the system frame
/// is switched off (it leaves a white strip above a custom title bar) and this draws XP's own, with resize
/// grips when the window can be resized. Every other theme and system keeps the normal frame and title bar.
/// Use it as the window's <see cref="ContentControl.Content"/>.
/// </summary>
public sealed class XpWindowChrome : Panel
{
    private static readonly Lazy<Bitmap?> TitleIcon = new(() =>
    {
        try
        {
            return new Bitmap(AssetLoader.Open(new Uri("avares://Layerlapse/Assets/layerlapse.ico")));
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException)
        {
            return null;
        }
    });

    private readonly Window _window;
    private readonly string _backgroundKey;
    private readonly Border _frame;
    private readonly Border _titleBar;
    private readonly Button _maximize;
    private readonly Panel _grips;
    private bool _xpChrome;
    private IDisposable? _background;

    /// <param name="window">The window this fills.</param>
    /// <param name="content">The window's own content, shown under the title bar.</param>
    /// <param name="backgroundKey">Resource key of the window's background brush.</param>
    public XpWindowChrome(Window window, Control content, string backgroundKey = "BackgroundBrush")
    {
        _window = window;
        _backgroundKey = backgroundKey;

        var title = new TextBlock { Classes = { "xp-title" } };
        title[!TextBlock.TextProperty] = window[!Window.TitleProperty];
        var shadow = new DropShadowEffect { OffsetX = 1, OffsetY = 1, BlurRadius = 0, Opacity = 1 };
        shadow.Bind(DropShadowEffect.ColorProperty, title.GetResourceObservable("XpTitleShadowColor")); // effects are not in the tree
        title.Effect = shadow;

        var minimize = Caption("Minimize", "M 0,0 H 7 V 3 H 0 Z", new Thickness(0, 8, 2, 0));
        minimize.Click += (_, _) => _window.WindowState = WindowState.Minimized;
        _maximize = Caption("Maximize", "M 0,0 H 11 V 11 H 0 Z M 1,3 V 10 H 10 V 3 Z", default);
        _maximize.Click += (_, _) => ToggleMaximized();
        var close = Caption("Close", "M 0,1 L 1,0 L 4.5,3.5 L 8,0 L 9,1 L 5.5,4.5 L 9,8 L 8,9 L 4.5,5.5 L 1,9 L 0,8 L 3.5,4.5 Z", default);
        close.Classes.Add("xp-close");
        close.Click += (_, _) => _window.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.AddRange([minimize, _maximize, close]);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(new Image { Source = TitleIcon.Value, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);

        _titleBar = new Border { Classes = { "xp-titlebar" }, IsVisible = false, Child = row };
        _titleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed && e.Source is not Button)
            {
                _window.BeginMoveDrag(e);
            }
        };
        _titleBar.DoubleTapped += (_, _) => ToggleMaximized();

        var dock = new DockPanel();
        DockPanel.SetDock(_titleBar, Dock.Top);
        dock.Children.Add(_titleBar);
        dock.Children.Add(content);
        _frame = new Border { Classes = { "window-frame" }, Child = dock };
        _frame[!Border.BackgroundProperty] = new DynamicResourceExtension(backgroundKey);

        _grips = new Panel { IsVisible = false };
        Grip(WindowEdge.North, height: 5, v: VerticalAlignment.Top, margin: new Thickness(8, 0), cursor: StandardCursorType.TopSide);
        Grip(WindowEdge.South, height: 5, v: VerticalAlignment.Bottom, margin: new Thickness(8, 0), cursor: StandardCursorType.BottomSide);
        Grip(WindowEdge.West, width: 5, h: HorizontalAlignment.Left, margin: new Thickness(0, 8), cursor: StandardCursorType.LeftSide);
        Grip(WindowEdge.East, width: 5, h: HorizontalAlignment.Right, margin: new Thickness(0, 8), cursor: StandardCursorType.RightSide);
        Grip(WindowEdge.NorthWest, 10, 10, HorizontalAlignment.Left, VerticalAlignment.Top, default, StandardCursorType.TopLeftCorner);
        Grip(WindowEdge.NorthEast, 10, 10, HorizontalAlignment.Right, VerticalAlignment.Top, default, StandardCursorType.TopRightCorner);
        Grip(WindowEdge.SouthWest, 10, 10, HorizontalAlignment.Left, VerticalAlignment.Bottom, default, StandardCursorType.BottomLeftCorner);
        Grip(WindowEdge.SouthEast, 10, 10, HorizontalAlignment.Right, VerticalAlignment.Bottom, default, StandardCursorType.BottomRightCorner);

        Children.Add(_frame);
        Children.Add(_grips);

        ThemeManager.Track(window);
        ThemeManager.Changed += Apply;
        window.Closed += (_, _) => ThemeManager.Changed -= Apply;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Window.CanResizeProperty)
            {
                UpdateFrame();
            }
        };
        Apply(ThemeManager.Current);
    }

    private void Apply(AppTheme theme)
    {
        _xpChrome = theme == AppTheme.XP && (OperatingSystem.IsWindows() || ThemeManager.AllThemesEverywhere);
        _titleBar.IsVisible = _xpChrome;
        _window.WindowDecorations = _xpChrome ? WindowDecorations.None : WindowDecorations.Full;
        _window.TransparencyLevelHint = _xpChrome ? [WindowTransparencyLevel.Transparent] : [];
        _background?.Dispose();
        _background = null;
        if (_xpChrome)
        {
            _window.Background = Brushes.Transparent; // the rounded corners of the XP frame show the desktop
        }
        else
        {
            _background = _window.Bind(Window.BackgroundProperty, _window.GetResourceObservable(_backgroundKey));
        }

        UpdateFrame();
    }

    private void UpdateFrame()
    {
        var maximized = _window.WindowState is WindowState.Maximized or WindowState.FullScreen;
        _frame.Classes.Set("xp-frame", _xpChrome && !maximized);
        _titleBar.Classes.Set("square", maximized);
        _grips.IsVisible = _xpChrome && !maximized && _window.CanResize;
        _maximize.IsVisible = _window.CanResize;
    }

    private void ToggleMaximized()
    {
        if (_window.CanResize)
        {
            _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    private static Button Caption(string tip, string glyph, Thickness glyphMargin)
    {
        var button = new Button { Classes = { "xp-caption" }, Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(glyph), Margin = glyphMargin } };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private void Grip(
        WindowEdge edge,
        double width = double.NaN,
        double height = double.NaN,
        HorizontalAlignment h = HorizontalAlignment.Stretch,
        VerticalAlignment v = VerticalAlignment.Stretch,
        Thickness margin = default,
        StandardCursorType cursor = StandardCursorType.Arrow)
    {
        var grip = new Border
        {
            Width = width,
            Height = height,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Margin = margin,
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor),
        };
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed)
            {
                _window.BeginResizeDrag(edge, e);
            }
        };
        _grips.Children.Add(grip);
    }
}
