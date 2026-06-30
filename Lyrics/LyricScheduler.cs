using SpotifyLyricsTaskbar.Config;

namespace SpotifyLyricsTaskbar.Lyrics;

/// <summary>What the overlay should show right now. <see cref="Snap"/> means render instantly (e.g. after a seek).</summary>
public readonly record struct LyricView(int LineId, IReadOnlyList<string> Words, int Reveal, bool Snap = false)
{
    public static readonly LyricView Empty = new(-1, Array.Empty<string>(), 0);
}

/// <summary>
/// Maps an interpolated playback position to the active line + word-reveal count.
/// Shows an instrumental tag during long no-lyric gaps, and supports instant
/// "snap" jumps after a manual seek. Reads tuning live from <see cref="AppConfig"/>.
/// </summary>
public sealed class LyricScheduler
{
    public const int InstrumentalLineId = -3;
    public const int TitleLineId = -2;
    private const int DefaultLastLineMs = 4000;

    private readonly Func<long> _getPositionMs;
    private readonly AppConfig _config;

    private LyricSet _set = LyricSet.None;
    private string[][] _words = Array.Empty<string[]>();
    private long[][] _wordStart = Array.Empty<long[]>(); // per-line absolute word start times (empty = none)
    private double[][] _wordFrac = Array.Empty<double[]>(); // per-line synthetic reveal fractions (used when no real word timing)
    private LyricView _last = new(-99, Array.Empty<string>(), -1);
    private bool _forceSnap;
    private bool _forceEmit;
    private bool _pending; // lyrics are still being fetched for the current track

    public event Action<LyricView>? ViewChanged;

    public LyricScheduler(Func<long> getPositionMs, AppConfig config)
    {
        _getPositionMs = getPositionMs;
        _config = config;
    }

    /// <summary>Force the next tick to render instantly (used after a manual seek).</summary>
    public void Snap() => _forceSnap = true;

    /// <summary>Force the next tick to re-emit even if unchanged (used when the intro ends).</summary>
    public void ForceNext() => _forceEmit = true;

    /// <summary>While true (and no lyrics loaded yet), show the instrumental tag as a "tuning in" indicator.</summary>
    public void SetPending(bool pending)
    {
        if (_pending == pending) return;
        _pending = pending;
        Tick();
    }

    public void SetLyrics(LyricSet set)
    {
        _set = set ?? LyricSet.None;
        _pending = false; // lyrics resolved (even "none")
        if (_set.Kind is LyricKind.Synced or LyricKind.PlainBestEffort)
        {
            int n = _set.Lines.Count;
            _words = new string[n][];
            _wordStart = new long[n][];
            _wordFrac = new double[n][];
            for (int i = 0; i < n; i++)
            {
                var line = _set.Lines[i];
                if (line.Words is { Count: > 0 } wt)
                {
                    var texts = new List<string>(wt.Count);
                    var starts = new List<long>(wt.Count);
                    foreach (var w in wt)
                    {
                        var t = w.Text.Trim();
                        if (t.Length == 0) continue;
                        texts.Add(t);
                        starts.Add(w.StartMs);
                    }
                    _words[i] = texts.ToArray();
                    _wordStart[i] = starts.ToArray();
                }
                else
                {
                    _words[i] = SplitWords(line.Text);
                    _wordStart[i] = Array.Empty<long>();
                }
                _wordFrac[i] = ComputeWordFractions(_words[i]);
            }
        }
        else { _words = Array.Empty<string[]>(); _wordStart = Array.Empty<long[]>(); _wordFrac = Array.Empty<double[]>(); }

        _last = new(-99, Array.Empty<string>(), -1);
        Tick();
    }

    /// <summary>
    /// Synthetic per-word reveal fractions (0..1 of the line's window) for lines that lack
    /// real word timing: weight each word by length and add a brief hold after pause
    /// punctuation, so reveal speeds and slows within a line instead of marching uniformly.
    /// </summary>
    private static double[] ComputeWordFractions(string[] words)
    {
        int n = words.Length;
        if (n == 0) return Array.Empty<double>();
        var weight = new double[n];
        double total = 0;
        for (int k = 0; k < n; k++)
        {
            int len = 0;
            foreach (char ch in words[k]) if (char.IsLetterOrDigit(ch)) len++;
            double wt = Math.Max(2, len);
            char last = words[k].Length > 0 ? words[k][^1] : ' ';
            if (last is ',' or '.' or '!' or '?' or ';' or ':' or '—' or '-' or ')' or '…') wt += 3;
            weight[k] = wt;
            total += wt;
        }
        var frac = new double[n];
        double cum = 0;
        for (int k = 0; k < n; k++)
        {
            frac[k] = total > 0 ? cum / total : (double)k / n; // fraction at which word k starts appearing
            cum += weight[k];
        }
        return frac;
    }

    public void Clear() => SetLyrics(LyricSet.None);

    /// <summary>Time (ms) of the first real (non-empty) lyric line, or long.MaxValue.</summary>
    public long FirstLineMs()
    {
        if (_set.Kind is not (LyricKind.Synced or LyricKind.PlainBestEffort)) return long.MaxValue;
        for (int i = 0; i < _set.Lines.Count; i++)
            if (_words[i].Length > 0) return (long)_set.Lines[i].Time.TotalMilliseconds;
        return long.MaxValue;
    }

    /// <summary>Full text of the line due right now, or "" (never the instrumental tag) — the intro scramble target.</summary>
    public string CurrentLineText()
    {
        var v = Resolve();
        return v.LineId < 0 || v.Words.Count == 0 ? string.Empty : string.Join(' ', v.Words);
    }

    public void Tick()
    {
        var view = Resolve();
        bool snap = _forceSnap, force = _forceEmit;
        _forceSnap = false; _forceEmit = false;
        if (snap) view = view with { Snap = true };

        if (snap || force || view.LineId != _last.LineId || view.Reveal != _last.Reveal)
        {
            _last = view;
            ViewChanged?.Invoke(view);
        }
    }

    private LyricView Instrumental()
    {
        var tag = SplitWords(string.IsNullOrWhiteSpace(_config.InstrumentalTag) ? "♪" : _config.InstrumentalTag);
        return tag.Length == 0 ? LyricView.Empty : new LyricView(InstrumentalLineId, tag, tag.Length);
    }

    private LyricView Resolve()
    {
        switch (_set.Kind)
        {
            case LyricKind.None:
                // Still fetching → show the instrumental tag as a "tuning in" indicator so the
                // bar isn't blank between the intro card and the first line.
                return _config.ShowInstrumental && _pending ? Instrumental() : LyricView.Empty;

            case LyricKind.TitleOnly:
                var tw = SplitWords(_set.StaticText ?? string.Empty);
                return tw.Length == 0 ? LyricView.Empty : new LyricView(TitleLineId, tw, tw.Length);

            default:
                var lines = _set.Lines;
                if (lines.Count == 0) return LyricView.Empty;
                long pos = _getPositionMs() + _config.SyncOffsetMs + _config.CurrentSongOffset;
                int i = IndexAt(lines, pos);

                // In a gap (before the first line, or on a blank line)?
                if (i < 0 || _words[i].Length == 0)
                {
                    long next = NextRealLineMs(i, lines);
                    long gapStart = i < 0 ? 0 : (long)lines[i].Time.TotalMilliseconds;
                    bool longGap = next == long.MaxValue || next - gapStart >= _config.InstrumentalGapMs;
                    bool clearOfNext = next == long.MaxValue || next - pos > 1200;
                    return _config.ShowInstrumental && longGap && clearOfNext ? Instrumental() : LyricView.Empty;
                }

                var words = _words[i];
                double startMs = lines[i].Time.TotalMilliseconds;
                // Window = time this line is on screen (until the next line). Follow the real
                // duration so reveal tracks the singer's tempo, but cap very long/trailing gaps.
                double window = i + 1 < lines.Count ? lines[i + 1].Time.TotalMilliseconds - startMs : DefaultLastLineMs;
                double cap = words.Length * Math.Max(120, _config.WordPaceMs) * 1.8;
                double revealMs = Math.Clamp(window, 200, cap);

                // Outro: well past the last line → instrumental tag.
                if (i == lines.Count - 1 && _config.ShowInstrumental && pos - startMs > revealMs + 5000)
                    return Instrumental();

                if (!_config.WordByWord) return new LyricView(i, words, words.Length);

                int reveal;
                var starts = _wordStart[i];
                if (starts.Length == words.Length && starts.Length > 0)
                {
                    // true per-word timing (enhanced LRC / NetEase yrc)
                    reveal = 0;
                    while (reveal < starts.Length && starts[reveal] <= pos) reveal++;
                    reveal = Math.Clamp(reveal, 0, words.Length);
                }
                else
                {
                    // estimate: spread words across the window, weighted so pacing varies within the line
                    double frac = Math.Clamp((pos - startMs) / Math.Max(1, revealMs), 0, 1);
                    var fr = i < _wordFrac.Length ? _wordFrac[i] : Array.Empty<double>();
                    if (fr.Length == words.Length && fr.Length > 0)
                    {
                        reveal = 0;
                        while (reveal < fr.Length && fr[reveal] <= frac) reveal++;
                        reveal = Math.Clamp(reveal, 1, words.Length);
                    }
                    else reveal = Math.Clamp((int)Math.Floor(frac * words.Length) + 1, 1, words.Length);
                }
                return new LyricView(i, words, reveal);
        }
    }

    private long NextRealLineMs(int afterIndex, IReadOnlyList<LyricLine> lines)
    {
        for (int k = Math.Max(0, afterIndex + 1); k < lines.Count; k++)
            if (_words[k].Length > 0) return (long)lines[k].Time.TotalMilliseconds;
        return long.MaxValue;
    }

    private static string[] SplitWords(string text)
        => string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int IndexAt(IReadOnlyList<LyricLine> lines, long posMs)
    {
        int lo = 0, hi = lines.Count - 1, res = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].Time.TotalMilliseconds <= posMs) { res = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return res;
    }
}
