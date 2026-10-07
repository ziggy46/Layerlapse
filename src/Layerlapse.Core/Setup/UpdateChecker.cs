using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Layerlapse.Core.Setup;

/// <summary>
/// Looks for a newer release on a GitHub-style "latest release" feed and returns its page. Never downloads or
/// installs anything. Until the first release is published the feed answers 404, which counts as "no update".
/// </summary>
public sealed class UpdateChecker(HttpClient http, Uri? feed)
{
    /// <summary>The public repository's latest release.</summary>
    public static readonly Uri? DefaultFeed = new("https://api.github.com/repos/ziggy46/Layerlapse/releases/latest");

    public bool IsConfigured => feed is not null;

    /// <summary>This build's version, from the assembly (Directory.Build.props).</summary>
    public static Version CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>The newer release, or null when up to date, not configured, offline or the feed is unreadable.</summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (feed is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, feed);
            request.Headers.UserAgent.ParseAdd($"Layerlapse/{CurrentVersion}"); // GitHub's API requires a User-Agent
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var release = await response.Content.ReadFromJsonAsync<Release>(timeout.Token);
            return release is { TagName: { } tag, HtmlUrl: { } page } && TryParse(tag, out var latest) && latest > CurrentVersion
                ? new AvailableUpdate(latest, page)
                : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null; // update checks never get in the way
        }
    }

    public static bool TryParse(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out version!);

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] Uri? HtmlUrl);
}

public sealed record AvailableUpdate(Version Version, Uri Page);
