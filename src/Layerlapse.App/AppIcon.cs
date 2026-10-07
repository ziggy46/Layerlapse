using Avalonia.Controls;
using Avalonia.Platform;

namespace Layerlapse.App;

/// <summary>The Layerlapse window icon, for windows created in code.</summary>
internal static class AppIcon
{
    private static readonly Lazy<WindowIcon?> Icon = new(() =>
    {
        try
        {
            return new WindowIcon(AssetLoader.Open(new Uri("avares://Layerlapse/Assets/layerlapse.ico")));
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException)
        {
            return null; // no asset loader (tests) or missing resource: use the default icon
        }
    });

    public static WindowIcon? Get() => Icon.Value;
}
