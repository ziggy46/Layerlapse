using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Tests;

public class UnixListingParserTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parses_recent_entry_with_time()
    {
        var entries = UnixListingParser.Parse(
            "-rw-r--r--    1 1002     1002     32922829 Oct 04 23:44 video_2026-10-04_09-44-43.mp4\r\n", "/timelapse/", Now);

        var entry = Assert.Single(entries);
        Assert.Equal("video_2026-10-04_09-44-43.mp4", entry.Name);
        Assert.Equal("/timelapse/video_2026-10-04_09-44-43.mp4", entry.FullPath);
        Assert.Equal(32922829, entry.Size);
        Assert.Equal(new DateTime(2026, 10, 4, 23, 44, 0, DateTimeKind.Utc), entry.Modified);
        Assert.Equal(DateTimeKind.Utc, entry.Modified.Kind);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void Parses_older_entry_with_year()
    {
        var entry = Assert.Single(UnixListingParser.Parse(
            "-rw-r--r--    1 1002     1002     18398135 Jul 07  2025 video_2025-07-07_07-17-11.mp4", "/timelapse", Now));

        Assert.Equal(new DateTime(2025, 7, 7), entry.Modified);
        Assert.Equal("/timelapse/video_2025-07-07_07-17-11.mp4", entry.FullPath);
    }

    [Fact]
    public void Time_in_the_future_belongs_to_previous_year()
    {
        var entry = Assert.Single(UnixListingParser.Parse(
            "-rw-r--r--    1 1002     1002     10 Dec 30 10:00 a.mp4", "/", new DateTime(2027, 1, 2)));

        Assert.Equal(new DateTime(2026, 12, 30, 10, 0, 0), entry.Modified);
    }

    [Fact]
    public void Parses_directories()
    {
        var entry = Assert.Single(UnixListingParser.Parse(
            "drwxr-xr-x    2 1002     1002        65536 Oct 04 23:44 thumbnail", "/timelapse/", Now));

        Assert.True(entry.IsDirectory);
        Assert.Equal("thumbnail", entry.Name);
    }

    [Theory]
    [InlineData("Bracket v2 (final), A&B #3.gcode.3mf")]
    [InlineData("  leading spaces.3mf")]
    [InlineData("挂钩 Größe.gcode.3mf")]
    [InlineData("A very long model name that the printer trunc....gcode.3mf")]
    public void Keeps_awkward_names_intact(string name)
    {
        var entry = Assert.Single(UnixListingParser.Parse(
            $"-rw-r--r--    1 1002     1002     1234 Aug 13 11:33 {name}", "/", Now));

        Assert.Equal(name, entry.Name);
    }

    [Fact]
    public void Strips_symlink_target_and_skips_noise()
    {
        var entries = UnixListingParser.Parse(
            "total 12\nlrwxrwxrwx    1 0 0 4 Oct 04 23:44 latest -> a.mp4\ndrwxr-xr-x    2 0 0 0 Oct 04 23:44 .\n\n", "/", Now);

        Assert.Equal("latest", Assert.Single(entries).Name);
    }
}
