using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NXTerm.Macro;

public interface IMacroHost
{
    string NewLine { get; }
    bool IsConnected { get; }
    void Send(string text);
    bool Connect(string args);
    void Disconnect();
    void LogOpen(string path, bool binary, bool append);
    void LogClose();
    void LogWrite(string text);
    void MessageBox(string text, string title);
    bool YesNoBox(string text, string title);
    string? InputBox(string text, string title, string def, bool password);
    void Status(string text);
    void SendBreak();
    void ClearScreen();
    void Display(string text);
    void SetTitle(string title);
}

public sealed class MacroException(string message, int line = -1) : Exception(message)
{
    public int Line { get; set; } = line;
}

/// <summary>
/// Interpreter for the macro script language (.ttl files):
/// variables, expressions, if/elseif/else/endif, while/endwhile, for/next, goto/call/return,
/// send/sendln/wait/waitln/recvln/waitregex, pause/mpause, connect/disconnect, log*, dialogs and string helpers.
/// </summary>
public sealed class MacroRunner
{
    enum K { Ident, Str, Num, Op }
    readonly record struct Tok(K Kind, string Text, long Num = 0);

    readonly IMacroHost _host;
    readonly List<List<Tok>> _code = [];
    readonly List<int> _srcLine = [];
    readonly Dictionary<string, int> _labels = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, int> _nextBranch = [], _blockEnd = [], _breakTarget = [];
    readonly Dictionary<int, (string Var, long End, int Step)> _forInfo = [];
    readonly Dictionary<string, object> _vars = new(StringComparer.OrdinalIgnoreCase);
    readonly Stack<int> _callStack = new();

    readonly StringBuilder _rx = new();
    readonly AutoResetEvent _rxEvent = new(false);
    const int RxLimit = 256 * 1024;

    public MacroRunner(IEnumerable<string> lines, IMacroHost host)
    {
        _host = host;
        int lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var toks = Tokenize(raw, lineNo, out string? label);
            if (label != null) _labels[label] = _code.Count;
            if (toks.Count == 0) continue;
            _code.Add(toks);
            _srcLine.Add(lineNo);
        }
        Link();
        foreach (var v in new[] { "result", "timeout", "mtimeout" }) _vars[v] = 0L;
        _vars["inputstr"] = "";
        _vars["matchstr"] = "";
    }

    // ------------------------------------------------------------------ received data

    public void OnReceived(string text)
    {
        lock (_rx)
        {
            _rx.Append(text);
            if (_rx.Length > RxLimit) _rx.Remove(0, _rx.Length - RxLimit);
        }
        _rxEvent.Set();
    }

    /// <summary>Waits until <paramref name="match"/> finds something in the receive buffer.
    /// The matcher returns (result, charsToConsume); result 0 means no match yet.</summary>
    int WaitFor(Func<string, (int Result, int Consume)> match, CancellationToken ct)
    {
        long ms = AsLong(_vars["timeout"]) * 1000 + AsLong(_vars["mtimeout"]);
        long deadline = ms > 0 ? Environment.TickCount64 + ms : long.MaxValue;
        while (true)
        {
            lock (_rx)
            {
                var (res, consume) = match(_rx.ToString());
                if (res != 0)
                {
                    _rx.Remove(0, Math.Min(consume, _rx.Length));
                    return res;
                }
            }
            ct.ThrowIfCancellationRequested();
            long rem = deadline - Environment.TickCount64;
            if (rem <= 0) return 0;
            WaitHandle.WaitAny([_rxEvent, ct.WaitHandle], (int)Math.Min(rem, 500));
        }
    }

    // ------------------------------------------------------------------ parsing

    static List<Tok> Tokenize(string s, int lineNo, out string? label)
    {
        label = null;
        var list = new List<Tok>();
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i < s.Length && s[i] == ':')
        {
            int st = ++i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            label = s[st..i];
        }
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (c == ';') break;
            if (c is '\'' or '"' or '#')
            {
                var sb = new StringBuilder();
                while (i < s.Length && s[i] is '\'' or '"' or '#')
                {
                    if (s[i] == '#')
                    {
                        i++;
                        int st = i;
                        bool hex = i < s.Length && s[i] == '$';
                        if (hex) st = ++i;
                        while (i < s.Length && (hex ? Uri.IsHexDigit(s[i]) : char.IsDigit(s[i]))) i++;
                        if (st == i) throw new MacroException("bad character code after '#'", lineNo);
                        sb.Append((char)int.Parse(s[st..i], hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        char q = s[i++];
                        int end = s.IndexOf(q, i);
                        if (end < 0) throw new MacroException("unterminated string", lineNo);
                        sb.Append(s, i, end - i);
                        i = end + 1;
                    }
                }
                list.Add(new Tok(K.Str, sb.ToString()));
                continue;
            }
            if (char.IsDigit(c) || (c == '$' && i + 1 < s.Length && Uri.IsHexDigit(s[i + 1])))
            {
                bool hex = c == '$';
                int st = hex ? ++i : i;
                while (i < s.Length && (hex ? Uri.IsHexDigit(s[i]) : char.IsDigit(s[i]))) i++;
                long v = long.Parse(s[st..i], hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture);
                list.Add(new Tok(K.Num, s[st..i], v));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                list.Add(new Tok(K.Ident, s[st..i]));
                continue;
            }
            string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "<>" or "<=" or ">=" or "==" or "!=" or "&&" or "||")
            {
                list.Add(new Tok(K.Op, two));
                i += 2;
                continue;
            }
            if ("=<>+-*/%()!".Contains(c))
            {
                list.Add(new Tok(K.Op, c.ToString()));
                i++;
                continue;
            }
            throw new MacroException($"unexpected character '{c}'", lineNo);
        }
        return list;
    }

    static string Cmd(List<Tok> t) => t[0].Kind == K.Ident ? t[0].Text.ToLowerInvariant() : "";
    static bool IsAssign(List<Tok> t) => t.Count > 1 && t[1].Kind == K.Op && t[1].Text == "=";
    static bool IsBlockIf(List<Tok> t) => t[^1].Kind == K.Ident && t[^1].Text.Equals("then", StringComparison.OrdinalIgnoreCase);

    void Link()
    {
        var stack = new Stack<(string Kind, int Pc, List<int> Branches)>();
        for (int pc = 0; pc < _code.Count; pc++)
        {
            var t = _code[pc];
            if (IsAssign(t)) continue;
            string k = Cmd(t);
            if (t[^1].Kind == K.Ident && t[^1].Text.Equals("break", StringComparison.OrdinalIgnoreCase))
            {
                var loop = stack.FirstOrDefault(s => s.Kind is "while" or "for");
                if (loop.Kind == null) throw new MacroException("break outside of a loop", _srcLine[pc]);
                _breakTarget[pc] = loop.Pc;
            }
            switch (k)
            {
                case "if":
                    if (IsBlockIf(t)) stack.Push(("if", pc, [pc]));
                    break;
                case "elseif":
                case "else":
                    {
                        if (stack.Count == 0 || stack.Peek().Kind != "if") throw new MacroException($"{k} without if", _srcLine[pc]);
                        var top = stack.Peek();
                        _nextBranch[top.Branches[^1]] = pc;
                        top.Branches.Add(pc);
                        break;
                    }
                case "endif":
                    {
                        if (stack.Count == 0 || stack.Peek().Kind != "if") throw new MacroException("endif without if", _srcLine[pc]);
                        var top = stack.Pop();
                        _nextBranch[top.Branches[^1]] = pc;
                        foreach (var b in top.Branches) _blockEnd[b] = pc;
                        break;
                    }
                case "while":
                case "for":
                    stack.Push((k, pc, []));
                    break;
                case "endwhile":
                case "next":
                    {
                        string open = k == "next" ? "for" : "while";
                        if (stack.Count == 0 || stack.Peek().Kind != open) throw new MacroException($"{k} without {open}", _srcLine[pc]);
                        var top = stack.Pop();
                        _blockEnd[top.Pc] = pc;
                        _blockEnd[pc] = top.Pc;
                        break;
                    }
            }
        }
        if (stack.Count > 0)
        {
            var top = stack.Peek();
            throw new MacroException($"'{top.Kind}' block is not closed", _srcLine[top.Pc]);
        }
    }

    // ------------------------------------------------------------------ expressions

    static bool Truth(object v) => v is string s ? s.Length > 0 : (long)v != 0;
    static long AsLong(object v) => v is long l ? l : long.TryParse((string)v, out long r) ? r : throw new MacroException($"'{v}' is not a number");
    static string AsStr(object v) => v is string s ? s : ((long)v).ToString(CultureInfo.InvariantCulture);

    static bool IsOp(List<Tok> t, int i, params string[] ops) =>
        i < t.Count && ((t[i].Kind == K.Op && ops.Contains(t[i].Text)) ||
                        (t[i].Kind == K.Ident && ops.Contains(t[i].Text.ToLowerInvariant())));

    object Expr(List<Tok> t, ref int i)
    {
        var l = AndExpr(t, ref i);
        while (IsOp(t, i, "||", "or"))
        {
            i++;
            var r = AndExpr(t, ref i);
            l = Truth(l) || Truth(r) ? 1L : 0L;
        }
        return l;
    }

    object AndExpr(List<Tok> t, ref int i)
    {
        var l = CmpExpr(t, ref i);
        while (IsOp(t, i, "&&", "and"))
        {
            i++;
            var r = CmpExpr(t, ref i);
            l = Truth(l) && Truth(r) ? 1L : 0L;
        }
        return l;
    }

    object CmpExpr(List<Tok> t, ref int i)
    {
        var l = AddExpr(t, ref i);
        if (!IsOp(t, i, "=", "==", "<>", "!=", "<", ">", "<=", ">=")) return l;
        string op = t[i++].Text;
        var r = AddExpr(t, ref i);
        int cmp = l is string || r is string
            ? string.CompareOrdinal(AsStr(l), AsStr(r))
            : ((long)l).CompareTo((long)r);
        bool res = op switch
        {
            "=" or "==" => cmp == 0,
            "<>" or "!=" => cmp != 0,
            "<" => cmp < 0,
            ">" => cmp > 0,
            "<=" => cmp <= 0,
            _ => cmp >= 0,
        };
        return res ? 1L : 0L;
    }

    object AddExpr(List<Tok> t, ref int i)
    {
        var l = MulExpr(t, ref i);
        while (IsOp(t, i, "+", "-"))
        {
            string op = t[i++].Text;
            var r = MulExpr(t, ref i);
            if (op == "+" && (l is string || r is string)) l = AsStr(l) + AsStr(r);
            else l = op == "+" ? AsLong(l) + AsLong(r) : AsLong(l) - AsLong(r);
        }
        return l;
    }

    object MulExpr(List<Tok> t, ref int i)
    {
        var l = Unary(t, ref i);
        while (IsOp(t, i, "*", "/", "%"))
        {
            string op = t[i++].Text;
            long a = AsLong(l), b = AsLong(Unary(t, ref i));
            if (op != "*" && b == 0) throw new MacroException("division by zero");
            l = op switch { "*" => a * b, "/" => a / b, _ => a % b };
        }
        return l;
    }

    object Unary(List<Tok> t, ref int i)
    {
        if (IsOp(t, i, "-")) { i++; return -AsLong(Unary(t, ref i)); }
        if (IsOp(t, i, "+")) { i++; return AsLong(Unary(t, ref i)); }
        if (IsOp(t, i, "!", "not")) { i++; return Truth(Unary(t, ref i)) ? 0L : 1L; }
        return Primary(t, ref i);
    }

    object Primary(List<Tok> t, ref int i)
    {
        if (i >= t.Count) throw new MacroException("expression expected");
        var tok = t[i++];
        switch (tok.Kind)
        {
            case K.Num: return tok.Num;
            case K.Str: return tok.Text;
            case K.Ident:
                if (_vars.TryGetValue(tok.Text, out var v)) return v;
                throw new MacroException($"undefined variable '{tok.Text}'");
            default:
                if (tok.Text == "(")
                {
                    var r = Expr(t, ref i);
                    if (!IsOp(t, i, ")")) throw new MacroException("')' expected");
                    i++;
                    return r;
                }
                throw new MacroException($"unexpected '{tok.Text}'");
        }
    }

    List<object> Args(List<Tok> t, int from)
    {
        var list = new List<object>();
        int i = from;
        while (i < t.Count) list.Add(Expr(t, ref i));
        return list;
    }

    static string Arg(List<object> a, int i, string def = "") => i < a.Count ? AsStr(a[i]) : def;
    static long IntArg(List<object> a, int i, long def = 0) => i < a.Count ? AsLong(a[i]) : def;

    static string VarName(List<Tok> t, int i) =>
        i < t.Count && t[i].Kind == K.Ident ? t[i].Text : throw new MacroException("variable name expected");

    static string SendText(List<object> args)
    {
        var sb = new StringBuilder();
        foreach (var a in args)
        {
            if (a is long l) sb.Append((char)l);
            else sb.Append((string)a);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ execution

    public Task RunAsync(CancellationToken ct) => Task.Run(() => Run(ct), ct);

    void Run(CancellationToken ct)
    {
        int pc = 0;
        while (pc < _code.Count)
        {
            ct.ThrowIfCancellationRequested();
            int line = _srcLine[pc];
            try
            {
                pc = Exec(_code[pc], pc, ct);
            }
            catch (MacroException ex) when (ex.Line < 0)
            {
                ex.Line = line;
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not MacroException)
            {
                throw new MacroException(ex.Message, line);
            }
        }
    }

    void SetResult(long v) => _vars["result"] = v;

    static void Sleep(long ms, CancellationToken ct)
    {
        if (ms > 0) ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(ms));
        ct.ThrowIfCancellationRequested();
    }

    int Goto(List<Tok> t, int i)
    {
        if (i >= t.Count) throw new MacroException("label expected");
        return _labels.TryGetValue(t[i].Text, out int target) ? target : throw new MacroException($"label '{t[i].Text}' not found");
    }

    int Exec(List<Tok> t, int pc, CancellationToken ct)
    {
        if (t[0].Kind != K.Ident) throw new MacroException("command expected");
        if (IsAssign(t))
        {
            int i = 2;
            var v = Expr(t, ref i);
            if (i < t.Count) throw new MacroException("unexpected text after expression");
            _vars[t[0].Text] = v;
            return pc + 1;
        }

        string cmd = Cmd(t);
        switch (cmd)
        {
            // ---- flow control
            case "if": return ExecIf(t, pc, ct);
            case "elseif":
            case "else": return _blockEnd[pc] + 1;
            case "endif": return pc + 1;
            case "while":
                {
                    int i = 1;
                    return Truth(Expr(t, ref i)) ? pc + 1 : _blockEnd[pc] + 1;
                }
            case "endwhile": return _blockEnd[pc];
            case "for":
                {
                    string name = VarName(t, 1);
                    int i = 2;
                    long start = AsLong(Expr(t, ref i)), end = AsLong(Expr(t, ref i));
                    _vars[name] = start;
                    _forInfo[pc] = (name, end, start <= end ? 1 : -1);
                    return pc + 1;
                }
            case "next":
                {
                    int f = _blockEnd[pc];
                    var (name, end, step) = _forInfo[f];
                    long v = AsLong(_vars[name]) + step;
                    _vars[name] = v;
                    return (step > 0 ? v <= end : v >= end) ? f + 1 : pc + 1;
                }
            case "break": return _blockEnd[_breakTarget[pc]] + 1;
            case "goto": return Goto(t, 1);
            case "call":
                _callStack.Push(pc + 1);
                return Goto(t, 1);
            case "return":
                return _callStack.Count > 0 ? _callStack.Pop() : throw new MacroException("return without call");
            case "end":
            case "exit":
                return int.MaxValue;
        }

        var a = cmd is "int2str" or "str2int" or "strconcat" or "gettime" or "getdate" or "strcopy"
            ? []
            : Args(t, 1);
        switch (cmd)
        {
            // ---- communication
            case "send": _host.Send(SendText(a)); break;
            case "sendln": _host.Send(SendText(a) + _host.NewLine); break;
            case "sendbreak": _host.SendBreak(); break;
            case "flushrecv": lock (_rx) _rx.Clear(); break;
            case "wait":
                {
                    var targets = a.Select(AsStr).ToList();
                    SetResult(WaitFor(s =>
                    {
                        int best = -1, bestPos = int.MaxValue;
                        for (int k = 0; k < targets.Count; k++)
                        {
                            int p = s.IndexOf(targets[k], StringComparison.Ordinal);
                            if (p >= 0 && p < bestPos) { best = k; bestPos = p; }
                        }
                        return best < 0 ? (0, 0) : (best + 1, bestPos + targets[best].Length);
                    }, ct));
                    break;
                }
            case "waitln":
                {
                    var targets = a.Select(AsStr).ToList();
                    SetResult(WaitFor(s =>
                    {
                        for (int k = 0; k < targets.Count; k++)
                        {
                            int p = s.IndexOf(targets[k], StringComparison.Ordinal);
                            if (p < 0) continue;
                            int eol = s.IndexOf('\n', p + targets[k].Length);
                            if (eol < 0) continue;
                            int bol = s.LastIndexOf('\n', p) + 1;
                            _vars["inputstr"] = s[bol..eol].TrimEnd('\r');
                            return (k + 1, eol + 1);
                        }
                        return (0, 0);
                    }, ct));
                    break;
                }
            case "recvln":
                SetResult(WaitFor(s =>
                {
                    int eol = s.IndexOf('\n');
                    if (eol < 0) return (0, 0);
                    _vars["inputstr"] = s[..eol].TrimEnd('\r');
                    return (1, eol + 1);
                }, ct));
                break;
            case "waitregex":
                {
                    var re = new Regex(Arg(a, 0), RegexOptions.Multiline);
                    SetResult(WaitFor(s =>
                    {
                        var m = re.Match(s);
                        if (!m.Success) return (0, 0);
                        _vars["matchstr"] = m.Value;
                        for (int g = 1; g <= 9; g++) _vars[$"groupmatchstr{g}"] = g < m.Groups.Count ? m.Groups[g].Value : "";
                        return (1, m.Index + m.Length);
                    }, ct));
                    break;
                }
            case "pause": Sleep(IntArg(a, 0) * 1000, ct); break;
            case "mpause": Sleep(IntArg(a, 0), ct); break;
            case "connect":
                if (!_host.IsConnected) _host.Connect(Arg(a, 0));
                break;
            case "disconnect": _host.Disconnect(); break;
            case "testlink": SetResult(_host.IsConnected ? 2 : 1); break;

            // ---- log
            case "logopen": _host.LogOpen(Arg(a, 0), IntArg(a, 1) != 0, IntArg(a, 2) != 0); break;
            case "logclose": _host.LogClose(); break;
            case "logwrite": _host.LogWrite(Arg(a, 0)); break;

            // ---- UI
            case "messagebox": _host.MessageBox(Arg(a, 0), Arg(a, 1, "NXTerm macro")); break;
            case "yesnobox": SetResult(_host.YesNoBox(Arg(a, 0), Arg(a, 1, "NXTerm macro")) ? 1 : 0); break;
            case "inputbox":
            case "passwordbox":
                {
                    var r = _host.InputBox(Arg(a, 0), Arg(a, 1, "NXTerm macro"), Arg(a, 2), cmd == "passwordbox");
                    _vars["inputstr"] = r ?? "";
                    SetResult(r == null ? 0 : 1);
                    break;
                }
            case "statusbox": _host.Status(Arg(a, 0)); break;
            case "dispstr": _host.Display(SendText(a)); break;
            case "clearscreen": _host.ClearScreen(); break;
            case "settitle": _host.SetTitle(Arg(a, 0)); break;
            case "beep": System.Media.SystemSounds.Beep.Play(); break;

            // ---- strings & time
            case "int2str":
                {
                    int i = 2;
                    _vars[VarName(t, 1)] = AsStr(AsLong(Expr(t, ref i)));
                    break;
                }
            case "str2int":
                {
                    int i = 2;
                    bool ok = long.TryParse(AsStr(Expr(t, ref i)).Trim(), out long v);
                    _vars[VarName(t, 1)] = ok ? v : 0L;
                    SetResult(ok ? 1 : 0);
                    break;
                }
            case "strconcat":
                {
                    string name = VarName(t, 1);
                    int i = 2;
                    _vars[name] = AsStr(_vars.GetValueOrDefault(name, "")) + AsStr(Expr(t, ref i));
                    break;
                }
            case "strcopy":
                {
                    int i = 1;
                    string s = AsStr(Expr(t, ref i));
                    int start = (int)AsLong(Expr(t, ref i)) - 1, len = (int)AsLong(Expr(t, ref i));
                    string name = VarName(t, i);
                    start = Math.Clamp(start, 0, s.Length);
                    _vars[name] = s.Substring(start, Math.Clamp(len, 0, s.Length - start));
                    break;
                }
            case "strlen": SetResult(Arg(a, 0).Length); break;
            case "strcompare": SetResult(Math.Sign(string.CompareOrdinal(Arg(a, 0), Arg(a, 1)))); break;
            case "strscan": SetResult(Arg(a, 0).IndexOf(Arg(a, 1), StringComparison.Ordinal) + 1); break;
            case "gettime":
            case "getdate":
                {
                    string name = VarName(t, 1);
                    int i = 2;
                    string fmt = i < t.Count ? AsStr(Expr(t, ref i)) : cmd == "gettime" ? "%H:%M:%S" : "%Y-%m-%d";
                    _vars[name] = StrFTime(fmt, DateTime.Now);
                    break;
                }
            default:
                throw new MacroException($"unknown command '{t[0].Text}'");
        }
        return pc + 1;
    }

    int ExecIf(List<Tok> t, int pc, CancellationToken ct)
    {
        int i = 1;
        bool cond = Truth(Expr(t, ref i));
        if (IsBlockIf(t) && i == t.Count - 1)
        {
            if (cond) return pc + 1;
            int j = _nextBranch[pc];
            while (Cmd(_code[j]) == "elseif")
            {
                int ii = 1;
                if (Truth(Expr(_code[j], ref ii))) return j + 1;
                j = _nextBranch[j];
            }
            return j + 1;  // else or endif
        }
        if (i >= t.Count) throw new MacroException("if: command expected after condition");
        return cond ? Exec(t.GetRange(i, t.Count - i), pc, ct) : pc + 1;
    }

    static string StrFTime(string fmt, DateTime d)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%' || i + 1 >= fmt.Length) { sb.Append(fmt[i]); continue; }
            sb.Append(fmt[++i] switch
            {
                'Y' => d.ToString("yyyy", CultureInfo.InvariantCulture),
                'y' => d.ToString("yy", CultureInfo.InvariantCulture),
                'm' => d.ToString("MM", CultureInfo.InvariantCulture),
                'd' => d.ToString("dd", CultureInfo.InvariantCulture),
                'H' => d.ToString("HH", CultureInfo.InvariantCulture),
                'M' => d.ToString("mm", CultureInfo.InvariantCulture),
                'S' => d.ToString("ss", CultureInfo.InvariantCulture),
                '%' => "%",
                char c => "%" + c,
            });
        }
        return sb.ToString();
    }
}
