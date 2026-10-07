using Avalonia.Controls;
using Layerlapse.App.Themes;

namespace Layerlapse.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var content = (Control)Content!;
        Content = null; // detach before XpWindowChrome takes it
        Content = new XpWindowChrome(this, content);
    }
}
