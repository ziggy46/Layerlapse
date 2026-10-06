using System.Globalization;
using System.Text.RegularExpressions;

namespace Layerlapse.Core.Printers;

/// <summary>
/// Parses vsftpd's Unix "ls -l" style LIST output. Recent entries show a time ("Oct 04 23:44"),
/// older ones a year ("Jul 07  2025"). Times are the printer's local clock, so they are returned
/// as <see cref="DateTimeKind.Unspecified"/>.
/// </summary>
public static partial class UnixListingParser
{
    [GeneratedRegex(@"^(?<type>[-dl])\S{9}\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<month>[A-Za-z]{3})\s+(?<day>\d{1,2})\s+(?<timeOrYear>\d{1,2}:\d{2}|\d{4})\s(?<name>.+)$")]
    private static partial Regex LineRegex();

    public static IReadOnlyList<RemoteEntry> Parse(string listing, string folder, DateTime now)
    {
        var entries = new List<RemoteEntry>();
        var prefix = folder.EndsWith('/') ? folder : folder + "/";

        foreach (var rawLine in listing.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = LineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            if (match.Groups["type"].Value == "l")
            {
                var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    name = name[..arrow];
                }
            }

            if (name is "." or "..")
            {
                continue;
            }

            entries.Add(new RemoteEntry(
                name,
                prefix + name,
                long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture),
                ParseDate(match.Groups["month"].Value, match.Groups["day"].Value, match.Groups["timeOrYear"].Value, now),
                match.Groups["type"].Value == "d"));
        }

        return entries;
    }

    private static DateTime ParseDate(string month, string day, string timeOrYear, DateTime now)
    {
        var monthNumber = DateTime.ParseExact(month, "MMM", CultureInfo.InvariantCulture).Month;
        var dayNumber = int.Parse(day, CultureInfo.InvariantCulture);

        if (!timeOrYear.Contains(':'))
        {
            return new DateTime(int.Parse(timeOrYear, CultureInfo.InvariantCulture), monthNumber, dayNumber, 0, 0, 0, DateTimeKind.Unspecified);
        }

        var parts = timeOrYear.Split(':');
        var candidate = new DateTime(now.Year, monthNumber, dayNumber,
            int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), 0, DateTimeKind.Unspecified);

        // "ls" shows a time only for entries within the last six months, so a date in the future
        // belongs to the previous year.
        return candidate > now.AddDays(1) ? candidate.AddYears(-1) : candidate;
    }
}
