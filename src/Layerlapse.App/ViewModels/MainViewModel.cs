using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Layerlapse.App.ViewModels;

public partial class MainViewModel(ConnectionViewModel connection) : ViewModelBase
{
    public ConnectionViewModel Connection { get; } = connection;

    [ObservableProperty]
    public partial bool IsLightTheme { get; set; }

    public Task InitializeAsync() => Connection.InitializeAsync();

    partial void OnIsLightThemeChanged(bool value)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }
}
