using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Layerlapse.App.Views;

/// <summary>
/// A small modal question with Cancel as the default button. The confirm button uses the danger colour for
/// destructive actions.
/// </summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirmLabel, bool destructive)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Get();
        this[!BackgroundProperty] = new DynamicResourceExtension("CardBrush");

        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
        var ok = new Button { Content = confirmLabel, FontWeight = Avalonia.Media.FontWeight.Bold };
        if (destructive)
        {
            ok[!Button.BackgroundProperty] = new DynamicResourceExtension("DangerBrush");
            ok.Foreground = Avalonia.Media.Brushes.White;
        }

        cancel.Click += (_, _) => Close(false);
        ok.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };
    }

    public static async Task<bool> AskAsync(Window owner, string title, string message, string confirmLabel, bool destructive = false) =>
        await new ConfirmDialog(title, message, confirmLabel, destructive).ShowDialog<bool>(owner);
}
