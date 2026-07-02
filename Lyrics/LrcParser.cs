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

            string body = NormalizeChars(lineRx.Replace(raw, string.Empty)); // full-width punctuation → ASCII

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
                text = UnCensor(body.Trim());
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

        // Attach a standalone punctuation token ("," ")" "(" …) to a neighbouring word so it
        // doesn't render as a floating " , " with spaces on both sides.
        for (int k = words.Count - 1; k >= 0; k--)
        {
            string t = words[k].Text.Trim();
            if (t.Length == 0 || !IsPunctToken(t)) continue;
            bool opening = t[0] is '(' or '[' or '{';
            if (opening && k + 1 < words.Count)
                words[k + 1] = words[k + 1] with { Text = t + words[k + 1].Text };
            else if (!opening && k > 0)
                words[k - 1] = words[k - 1] with { Text = words[k - 1].Text.TrimEnd() + t + " " };
            else continue;
            words.RemoveAt(k);
        }

        // Un-censor per word (asterisked profanity → the actual word).
        for (int k = 0; k < words.Count; k++)
        {
            string u = UnCensor(words[k].Text);
            if (!ReferenceEquals(u, words[k].Text)) words[k] = words[k] with { Text = u };
        }

        // Rebuild the whole-line text from the cleaned words so it matches what's shown.
        var lineText = new StringBuilder(clean.Length);
        foreach (var w in words) lineText.Append(w.Text.TrimEnd()).Append(' ');
        return (lineText.ToString().Trim(), words);
    }

    private static readonly char[] CensorMarks = { '*', '#', '@', '$', '%' };
    private static readonly (Regex rx, string rep)[] CensorFixes =
    {
        (new Regex(@"\bf[\*#@\$%\-]+k(ing|in['’]?|er|ed|s)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "fuck$1"),
        (new Regex(@"\bf[\*#@\$%\-]{2,}(?=ing\b|in['’])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "fuck"),
        (new Regex(@"\bsh[\*#@\$%\-]+t\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "shit"),
        (new Regex(@"\bb[\*#@\$%\-]+tch\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "bitch"),
        (new Regex(@"\bd[\*#@\$%\-]+ck\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "dick"),
        (new Regex(@"\bp[\*#@\$%\-]+s+y\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "pussy"),
        (new Regex(@"\bn[\*#@\$%\-]+gga?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "nigga"),
        (new Regex(@"\ba[\*#@\$%]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ass"),
    };

    /// <summary>Reconstruct common asterisked profanity ("f**king" → "fucking") for accurate lyrics.</summary>
    private static string UnCensor(string s)
    {
        if (s.IndexOfAny(CensorMarks) < 0) return s;
        foreach (var (rx, rep) in CensorFixes) s = rx.Replace(s, rep);
        return s;
    }

    /// <summary>Public cleaner (full-width normalise + un-censor) for plain-lyric lines.</summary>
    public static string CleanText(string s) => UnCensor(NormalizeChars(s));

    private static bool IsPunctToken(string t)
    {
        foreach (char c in t) if (char.IsLetterOrDigit(c)) return false;
        return t.Length > 0;
    }

    private static string NormalizeChars(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c switch
            {
                '（' => '(', '）' => ')',
                '，' or '、' => ',',
                '。' or '．' => '.',
                '！' => '!', '？' => '?',
                '：' => ':', '；' => ';',
                '‘' or '’' => '\'',
                '“' or '”' => '"',
                '　' => ' ',
                _ => c,
            });
        return sb.ToString();
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
