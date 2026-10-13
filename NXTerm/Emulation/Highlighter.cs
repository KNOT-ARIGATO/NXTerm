using System.Text.RegularExpressions;

namespace NXTerm.Emulation;

/// <summary>One keyword highlight: text (or a regular expression) and the colour to show it in.</summary>
public sealed class HighlightRule
{
    public string Pattern = "";
    public string Color = "#FF5555";
    /// <summary>Colour the whole line that contains the match instead of just the match.</summary>
    public bool WholeLine;
    /// <summary>False: Pattern is plain text matched as a whole word. True: Pattern is a regular expression.</summary>
    public bool IsRegex;

    public HighlightRule Clone() => (HighlightRule)MemberwiseClone();

    /// <summary>Builds the matcher; throws ArgumentException when a regular expression is invalid.</summary>
    public Regex ToRegex()
    {
        string pattern = IsRegex ? Pattern : $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(Pattern)}(?![\p{{L}}\p{{N}}_])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    public static List<HighlightRule> Defaults() =>
    [
        // Order matters: the first rule that matches a piece of text wins.
        // 1. "Nothing went wrong" phrases contain a bad word but are good news: "0 errors", "no failures".
        new() { Pattern = @"\b(0|no|zero|without)\s+(errors?|failures?|failed|fails?|warnings?|problems?|issues?|faults?)\b", Color = Good, IsRegex = true },
        // 2. Negated good words are bad news: "not ok", "not found", "cannot connect", "no such file".
        new()
        {
            Pattern = @"\b((not|never)\s+(ok|okay|found|connected|ready|running|responding|available|supported|permitted|allowed|valid|recognized|reachable|installed|enabled|exist|exists)" +
                      @"|no such \w+|cannot|can't|couldn't|could not|unable to|failed to|no response|no route|no carrier|out of (memory|space|range))\b",
            Color = Bad,
            IsRegex = true,
        },
        // 3. Errors and failures.
        new()
        {
            Pattern = @"\b(errors?|err|fail|fails|failed|failing|failures?|fatal|panic|exception|critical|crit|emerg|alert|abort|aborted|aborting" +
                      @"|denied|refused|rejected|forbidden|unauthorized|invalid|illegal|corrupt|corrupted|crash|crashed|segfault|segmentation fault|core dumped" +
                      @"|traceback|undefined|unreachable|offline|disconnected|timeout|timed out|overflow|underflow|brownout|killed|dead|nak|nok|ng)\b" +
                      @"|\[\s*(FAILED|ERROR|FAIL)\s*\]",
            Color = Bad,
            IsRegex = true,
        },
        // 4. Things worth a second look.
        new()
        {
            Pattern = @"\b(warn|warning|warnings|caution|deprecated|retry|retrying|retries|skipped|skipping|pending|degraded|unstable|throttled|low battery|busy)\b|\[\s*WARN(ING)?\s*\]",
            Color = Warning,
            IsRegex = true,
        },
        // 5. Success.
        new()
        {
            Pattern = @"\b(pass|passed|passes|passing|ok|okay|success|succeeded|successful|successfully|done|complete|completed|finished|ready" +
                      @"|connected|online|active|running|started|verified|valid|ack)\b|\[\s*(OK|PASS|PASSED|DONE)\s*\]",
            Color = Good,
            IsRegex = true,
        },
    ];

    const string Bad = "#FF5555", Warning = "#F1C40F", Good = "#2ECC71";
}

/// <summary>
/// Colours keywords such as FAIL or PASS while the screen is drawn. It only changes how text is shown:
/// the terminal contents, copied text and log files stay exactly as received.
/// </summary>
public sealed class Highlighter
{
    readonly List<(Regex Regex, Color Color, bool WholeLine)> _rules = [];
    char[] _text = [];

    public Highlighter(IEnumerable<HighlightRule> rules)
    {
        foreach (var r in rules)
        {
            if (string.IsNullOrWhiteSpace(r.Pattern)) continue;
            try { _rules.Add((r.ToRegex(), AppSettings.ParseColor(r.Color, System.Drawing.Color.Red), r.WholeLine)); }
            catch (ArgumentException) { }  // an invalid pattern in a hand-edited settings file is skipped
        }
    }

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>
    /// Fills <paramref name="colors"/> with the highlight colour of each column (null = not highlighted).
    /// Earlier rules win where two rules match the same text. Returns false when nothing on the line matched.
    /// </summary>
    public bool Apply(TermLine line, int cols, Color?[] colors)
    {
        var cells = line.Cells;
        int n = Math.Min(Math.Min(cols, cells.Length), colors.Length);
        if (_text.Length < n) _text = new char[n];
        int last = -1;
        for (int i = 0; i < n; i++)
        {
            char ch = (cells[i].Attr & CellAttr.WideCont) != 0 || cells[i].Ch < ' ' ? ' ' : cells[i].Ch;
            _text[i] = ch;
            if (ch != ' ') last = i;
        }
        if (last < 0) return false;

        var text = new string(_text, 0, last + 1);  // one character per column, so match positions are columns
        bool any = false;
        foreach (var (regex, color, wholeLine) in _rules)
        {
            try
            {
                for (var m = regex.Match(text); m.Success; m = m.NextMatch())
                {
                    if (m.Length == 0) continue;
                    if (!any) Array.Clear(colors, 0, n);
                    any = true;
                    int from = wholeLine ? 0 : m.Index, to = wholeLine ? last + 1 : m.Index + m.Length;
                    for (int i = from; i < to; i++) colors[i] ??= color;
                    if (wholeLine) break;
                }
            }
            catch (RegexMatchTimeoutException) { }
        }
        return any;
    }
}
