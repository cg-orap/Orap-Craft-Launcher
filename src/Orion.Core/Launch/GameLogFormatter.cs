using System.Text;
using System.Text.RegularExpressions;

namespace Orion.Core.Launch;

/// <summary>
/// Converts Minecraft's console output into readable text.
///
/// Since 1.12 the vanilla log4j configuration writes <c>&lt;log4j:Event&gt;</c> XML fragments to
/// stdout so launchers can parse structured log events. Showing that XML to a user is
/// useless, so it is folded back into the familiar
/// <c>[HH:mm:ss] [thread/LEVEL]: message</c> form. Non-XML output (older versions, mod
/// installers, crashes on the JVM level) is passed through unchanged.
/// </summary>
public sealed class GameLogFormatter
{
    private static readonly Regex EventRegex = new(
        @"<log4j:Event\s+logger=""(?<logger>[^""]*)""\s+timestamp=""(?<ts>\d+)""\s+level=""(?<level>[^""]*)""\s+thread=""(?<thread>[^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex MessageRegex = new(
        @"<log4j:Message><!\[CDATA\[(?<message>.*?)\]\]></log4j:Message>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ThrowableRegex = new(
        @"<log4j:Throwable><!\[CDATA\[(?<throwable>.*?)\]\]></log4j:Throwable>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly StringBuilder _pending = new();
    private bool _insideEvent;

    /// <summary>
    /// Feeds one raw line. Returns one or more display lines, or an empty list while an XML
    /// event is still incomplete.
    /// </summary>
    public IReadOnlyList<string> Process(string? line)
    {
        if (string.IsNullOrEmpty(line)) return [];

        var trimmed = line.TrimStart();

        if (!_insideEvent && !trimmed.StartsWith("<log4j:Event", StringComparison.Ordinal))
        {
            return [line];
        }

        if (!_insideEvent)
        {
            _insideEvent = true;
            _pending.Clear();
        }

        _pending.AppendLine(line);

        if (!line.Contains("</log4j:Event>", StringComparison.Ordinal)) return [];

        var xml = _pending.ToString();
        _pending.Clear();
        _insideEvent = false;

        return Format(xml);
    }

    /// <summary>Formats a complete event block; exposed for tests.</summary>
    public static IReadOnlyList<string> Format(string xml)
    {
        var match = EventRegex.Match(xml);
        if (!match.Success) return Collapse(xml);

        var logger = match.Groups["logger"].Value;
        var level = match.Groups["level"].Value;
        var thread = match.Groups["thread"].Value;

        var time = DateTimeOffset.Now;
        if (long.TryParse(match.Groups["ts"].Value, out var millis))
        {
            try
            {
                time = DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        var messageMatch = MessageRegex.Match(xml);
        var message = messageMatch.Success ? messageMatch.Groups["message"].Value : CollapseFirst(xml);

        var result = new List<string>
        {
            $"[{time:HH:mm:ss}] [{thread}/{level}]: {message}",
        };

        var throwableMatch = ThrowableRegex.Match(xml);
        if (throwableMatch.Success)
        {
            var trace = throwableMatch.Groups["throwable"].Value.TrimEnd();
            if (trace.Length > 0) result.AddRange(trace.Split('\n').Select(l => l.TrimEnd('\r')));
        }

        _ = logger;
        return result;
    }

    private static IReadOnlyList<string> Collapse(string xml)
        => xml.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    private static string CollapseFirst(string xml)
    {
        var start = xml.IndexOf('>');
        var end = xml.LastIndexOf("</log4j:Event>", StringComparison.Ordinal);
        if (start < 0 || end <= start) return xml;
        return xml[(start + 1)..end].Trim();
    }
}
