using System.Globalization;
using System.Text.RegularExpressions;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Lyrics;

public interface ILrcParser
{
    LyricDocument Parse(string content, PlatformId source, CancellationToken cancellationToken = default);
}

public sealed partial class LrcParser : ILrcParser
{
    [GeneratedRegex(@"\[(?:(?<hours>\d{1,2}):)?(?<minutes>\d{1,3}):(?<seconds>\d{1,2})(?:[\.:](?<fraction>\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"^\s*\[offset\s*:\s*(?<offset>[+-]?\d+)\]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OffsetRegex();

    public LyricDocument Parse(string content, PlatformId source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var offset = TimeSpan.Zero;
        var entries = new List<(TimeSpan Timestamp, int Order, string Text)>();
        var order = 0;

        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offsetMatch = OffsetRegex().Match(rawLine);
            if (offsetMatch.Success && long.TryParse(offsetMatch.Groups["offset"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                offset = TimeSpan.FromMilliseconds(milliseconds);
                continue;
            }

            var timestamps = TimestampRegex().Matches(rawLine);
            if (timestamps.Count == 0) continue;
            var text = TimestampRegex().Replace(rawLine, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (Match timestamp in timestamps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryParseTimestamp(timestamp, out var value)) entries.Add((value, order++, text));
            }
        }

        var lines = entries
            .GroupBy(entry => entry.Timestamp)
            .OrderBy(group => group.Key)
            .Select(group => Merge(group.Key, group.OrderBy(entry => entry.Order).Select(entry => entry.Text)))
            .ToArray();
        var isInstrumental = lines.Length > 0 && lines.All(line => IsInstrumentalText(line.Text));
        return new LyricDocument(lines, isInstrumental, offset, source);
    }

    private static LyricLine Merge(TimeSpan timestamp, IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        return new LyricLine(timestamp, distinct[0], distinct.Length > 1 ? distinct[1] : null);
    }

    private static bool TryParseTimestamp(Match match, out TimeSpan timestamp)
    {
        timestamp = default;
        if (!int.TryParse(match.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(match.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds >= 60)
            return false;

        var hours = 0;
        if (match.Groups["hours"].Success && !int.TryParse(match.Groups["hours"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out hours))
            return false;
        var fractionText = match.Groups["fraction"].Value;
        var milliseconds = fractionText.Length switch
        {
            1 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 100,
            2 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 10,
            3 => int.Parse(fractionText, CultureInfo.InvariantCulture),
            _ => 0
        };
        timestamp = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private static bool IsInstrumentalText(string value) =>
        value.Contains("纯音乐", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("instrumental", StringComparison.OrdinalIgnoreCase);
}
