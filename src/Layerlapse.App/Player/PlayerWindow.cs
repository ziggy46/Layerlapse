using Avalonia.Controls;
using Avalonia.Layout;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.App.Player;

/// <summary>A window playing one downloaded timelapse with the operating system's own video view.</summary>
public sealed class PlayerWindow : Window
{
    public PlayerWindow(string path, Control video)
    {
        Title = TimelapseNames.TryParseVideo(Path.GetFileName(path), out var start)
            ? "Timelapse " + start.ToString("ddd d MMM yyyy, HH:mm", System.Globalization.CultureInfo.CurrentCulture)
            : Path.GetFileName(path);
        Width = 960;
        Height = 600;
        MinWidth = 320;
        MinHeight = 240;
        Icon = AppIcon.Get();
        Video = video;

        var openElsewhere = new Button { Content = "Open in default player", Margin = new Avalonia.Thickness(8) };
        openElsewhere.Click += (_, _) =>
        {
            new DefaultAppVideoPlayer().Play(path);
            Close();
        };

        var bar = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(openElsewhere, Dock.Right);
        bar.Children.Add(openElsewhere);
        DockPanel.SetDock(bar, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(bar);
        root.Children.Add(video);
        bar.HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = root;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BackgroundBrush");
    }

    public Control Video { get; }
}
