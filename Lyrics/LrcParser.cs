using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SpotifyLyricsTaskbar.Lyrics;

/// <summary>A single word with its absolute start time (for true word-by-word sync).</summary>
public readonly record struct WordTime(long StartMs, string Text);

/// <summary>A lyric line. <see cref="Words"/> is non-null when per-word timing is available.</summary>
public readonly record struct LyricLine(TimeSpan Time, string Text, IReadOnlyList<WordTime>? Words = null);

/// <summary>
/// Parses LRC into time-sorted lines. Supports enhanced LRC (inline &lt;mm:ss.xx&gt;
/// word tags → per-word timing) and drops credit/metadata lines.
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex LineTagRegex();

    [GeneratedRegex(@"<(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled)]
    private static partial Regex WordTagRegex();

    [GeneratedRegex(@"^\s*(作词|作曲|编曲|制作|出品|混音|母带|和声|配唱|监制|录音|演唱|歌手|制作人|文案|策划|统筹|发行|吉他|贝斯|鼓|键盘|词|曲|编|produced by|producer|written by|writer|composed by|composer|lyrics?|music|arranged by|arranger|mixing|mastered by|mastering|vocals?)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CreditRegex();

    public static List<LyricLine> Parse(string? lrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrEmpty(lrc)) return result;

        var lineRx = LineTagRegex();
        var wordRx = WordTagRegex();
        var credit = CreditRegex();

        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var lineTags = lineRx.Matches(raw);
            if (lineTags.Count == 0) continue;

            string body = lineRx.Replace(raw, string.Empty);

            // Per-word timing?
            IReadOnlyList<WordTime>? words = null;
            string text;
            if (wordRx.IsMatch(body))
            {
                var (clean, wt) = ParseWords(body, wordRx);
                text = clean;
                words = wt.Count > 0 ? wt : null;
            }
            else
            {
                text = body.Trim();
            }

            if (text.Length > 0 && credit.IsMatch(text)) continue; // drop credit lines

            foreach (Match m in lineTags)
            {
                long ms = TagMs(m);
                result.Add(new LyricLine(TimeSpan.FromMilliseconds(ms), text, words));
            }
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    private static (string clean, List<WordTime> words) ParseWords(string body, Regex wordRx)
    {
        var words = new List<WordTime>();
        var clean = new StringBuilder();
        var matches = wordRx.Matches(body);
        bool prevEndedWithSpace = true;
        for (int k = 0; k < matches.Count; k++)
        {
            var m = matches[k];
            int textStart = m.Index + m.Length;
            int textEnd = k + 1 < matches.Count ? matches[k + 1].Index : body.Length;
            string seg = body.Substring(textStart, textEnd - textStart);
            if (seg.Length == 0) continue;
            clean.Append(seg);

            string trimmed = seg.Trim();
            if (trimmed.Length == 0) { prevEndedWithSpace = true; continue; } // whitespace-only gap between tags

            // A segment with no space between it and the previous one is the *same* word that the
            // source split for sub-word timing (e.g. "you" + "'ve", or a hyphenated word). Merge it
            // into the previous word and keep the earlier start time, so contractions render without
            // a stray space and reveal/sync as one unit.
            bool continuation = words.Count > 0 && !prevEndedWithSpace && !char.IsWhiteSpace(seg[0]);
            if (continuation)
            {
                var prev = words[^1];
                words[^1] = prev with { Text = prev.Text.TrimEnd() + trimmed + " " };
            }
            else words.Add(new WordTime(TagMs(m), trimmed + " "));

            prevEndedWithSpace = char.IsWhiteSpace(seg[^1]);
        }
        return (clean.ToString().Trim(), words);
    }

    private static long TagMs(Match m)
    {
        int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        long ms = (min * 60L + sec) * 1000L;
        if (m.Groups[3].Success)
        {
            var f = m.Groups[3].Value;
            int frac = int.Parse(f, CultureInfo.InvariantCulture);
            ms += f.Length switch { 1 => frac * 100, 2 => frac * 10, _ => frac };
        }
        return ms;
    }

    /// <summary>True if the LRC has at least two non-empty, non-credit timed lines.</summary>
    public static bool HasRealTimestamps(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return false;
        int withText = 0;
        foreach (var l in Parse(lrc))
            if (!string.IsNullOrWhiteSpace(l.Text) && ++withText >= 2)
                return true;
        return false;
    }
}
