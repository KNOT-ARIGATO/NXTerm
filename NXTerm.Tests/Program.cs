using System.Net;
using System.Net.Sockets;
using System.Text;
using NXTerm.Connections;
using NXTerm.Emulation;
using NXTerm.Logging;
using NXTerm.Macro;
using NXTerm.QuickCommands;
using NXTerm.Transfer;

// Self-contained test runner: dotnet run --project NXTerm.Tests
var tests = new List<(string Name, Func<Task> Body)>();
void Test(string name, Action body) => tests.Add((name, () => { body(); return Task.CompletedTask; }));
void TestAsync(string name, Func<Task> body) => tests.Add((name, body));

static void Eq<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what} expected <{Show(expected)}> but got <{Show(actual)}>");
}
static string Show(object? o) => o is string s ? s.Replace("\x1b", "\\e").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\0") : o?.ToString() ?? "null";
static string Row(VtTerminal t, int y) => t.GetLine(t.Scrollback.Count + y).GetText(0, t.Cols).TrimEnd();
static Cell At(VtTerminal t, int x, int y) => t.GetLine(t.Scrollback.Count + y).Cells[x];

// ---------------------------------------------------------------- emulator

Test("text and CR LF", () =>
{
    var t = new VtTerminal(80, 24);
    t.Feed("hello\r\nworld");
    Eq("hello", Row(t, 0));
    Eq("world", Row(t, 1));
    Eq((5, 1), (t.CursorX, t.CursorY));
});

Test("SGR 16/256/true color and bold", () =>
{
    var t = new VtTerminal(80, 24);
    t.Feed("\x1b[31;1mR\x1b[0m\x1b[38;5;200mX\x1b[48;2;1;2;3mY\x1b[38:2::10:20:30mZ");
    Eq(1, At(t, 0, 0).Fg);
    Eq(CellAttr.Bold, At(t, 0, 0).Attr);
    Eq(200, At(t, 1, 0).Fg);
    Eq(0x1010203, At(t, 2, 0).Bg);
    Eq(0x10A141E, At(t, 3, 0).Fg);
});

Test("cursor positioning", () =>
{
    var t = new VtTerminal(80, 24);
    t.Feed("\x1b[5;10HA\x1b[2;3HB\x1b[2BC\x1b[5DD");
    Eq('A', At(t, 9, 4).Ch);
    Eq('B', At(t, 2, 1).Ch);
    Eq('C', At(t, 3, 3).Ch);
    Eq('D', At(t, 0, 3).Ch);
});

Test("auto wrap marks wrapped line", () =>
{
    var t = new VtTerminal(10, 5);
    t.Feed("0123456789AB");
    Eq("0123456789", Row(t, 0));
    Eq("AB", Row(t, 1));
    Eq(true, t.GetLine(0).Wrapped);
    Eq("0123456789AB", t.GetText(0, 0, 1, 10));
});

Test("erase in line / display", () =>
{
    var t = new VtTerminal(10, 3);
    t.Feed("abcdefghij\r\nklmno\x1b[1;5H\x1b[K");
    Eq("abcd", Row(t, 0));
    t.Feed("\x1b[2J");
    Eq("", Row(t, 0));
    Eq("", Row(t, 1));
});

Test("scroll region keeps lines outside", () =>
{
    var t = new VtTerminal(10, 5);
    t.Feed("top\r\n1\r\n2\r\n3\r\nbottom\x1b[2;4r\x1b[4;1H\nnew");
    Eq("top", Row(t, 0));
    Eq("2", Row(t, 1));
    Eq("3", Row(t, 2));
    Eq("new", Row(t, 3));
    Eq("bottom", Row(t, 4));
    Eq(0, t.Scrollback.Count);
});

Test("scrollback collects scrolled lines", () =>
{
    var t = new VtTerminal(10, 3);
    t.Feed("l1\r\nl2\r\nl3\r\nl4\r\nl5");
    Eq(2, t.Scrollback.Count);
    Eq("l1", t.Scrollback[0].GetText(0, 10).TrimEnd());
    Eq("l3", Row(t, 0));
    t.MaxScrollback = 1;
    t.Feed("\r\nl6");
    Eq(1, t.Scrollback.Count);
    Eq("l3", t.Scrollback[0].GetText(0, 10).TrimEnd());
});

Test("alternate screen restores main screen", () =>
{
    var t = new VtTerminal(20, 5);
    t.Feed("shell prompt$ ");
    t.Feed("\x1b[?1049h\x1b[Hvim screen");
    Eq(true, t.AltScreen);
    Eq("vim screen", Row(t, 0));
    t.Feed("\x1b[?1049l");
    Eq("shell prompt$", Row(t, 0));
    Eq(14, t.CursorX);
});

Test("Thai combining marks share a cell", () =>
{
    var t = new VtTerminal(20, 3);
    t.Feed("กิน ข้าว");
    Eq('ก', At(t, 0, 0).Ch);
    Eq("ิ", At(t, 0, 0).Comb);
    Eq('น', At(t, 1, 0).Ch);
    Eq("้", At(t, 3, 0).Comb);
    Eq(6, t.CursorX);
    Eq("กิน ข้าว", Row(t, 0));
});

Test("wide CJK characters take two cells", () =>
{
    var t = new VtTerminal(20, 3);
    t.Feed("中a😀b");
    Eq(CellAttr.Wide, At(t, 0, 0).Attr);
    Eq(CellAttr.WideCont, At(t, 1, 0).Attr);
    Eq('a', At(t, 2, 0).Ch);
    Eq("😀", At(t, 3, 0).Ch + At(t, 3, 0).Comb);
    Eq('b', At(t, 5, 0).Ch);
    Eq("中a😀b", Row(t, 0));
});

Test("device status report answers cursor position", () =>
{
    var t = new VtTerminal(80, 24);
    string? resp = null;
    t.Response += r => resp = r;
    t.Feed("\x1b[3;7H\x1b[6n");
    Eq("\x1b[3;7R", resp);
    t.Feed("\x1b[c");
    Eq("\x1b[?62;22c", resp);
});

Test("DEC line drawing charset", () =>
{
    var t = new VtTerminal(10, 3);
    t.Feed("\x1b(0lqk\x1b(Bq");
    Eq("┌─┐q", Row(t, 0));
});

Test("insert / delete characters and lines", () =>
{
    var t = new VtTerminal(10, 4);
    t.Feed("abcdef\x1b[1;3H\x1b[2@XY\x1b[1;1H\x1b[1P");
    Eq("bXYcdef", Row(t, 0));
    t.Feed("\r\nline2\r\nline3\x1b[2;1H\x1b[1L");
    Eq("", Row(t, 1));
    Eq("line2", Row(t, 2));
    t.Feed("\x1b[1M");
    Eq("line2", Row(t, 1));
});

Test("receive LF as CR+LF option", () =>
{
    var t = new VtTerminal(20, 5) { LfImpliesCr = true };
    t.Feed("first\nsecond");
    Eq("second", Row(t, 1));
});

Test("resize moves top lines into scrollback", () =>
{
    var t = new VtTerminal(10, 5);
    t.Feed("a\r\nb\r\nc\r\nd\r\ne");
    t.Resize(8, 3);
    Eq((8, 3), (t.Cols, t.Rows));
    Eq(2, t.Scrollback.Count);
    Eq("c", Row(t, 0));
    Eq("e", Row(t, 2));
    Eq(2, t.CursorY);
});

Test("OSC sets the window title", () =>
{
    var t = new VtTerminal(10, 3);
    string? title = null;
    t.TitleChanged += s => title = s;
    t.Feed("\x1b]0;user@host: ~\x07ok");
    Eq("user@host: ~", title);
    Eq("ok", Row(t, 0));
});

// ---------------------------------------------------------------- connections

Test("command line parsing", () =>
{
    var p = ConnectionParams.Parse(["admin@10.0.0.5:2222", "/ssh"], out _, out _)!;
    Eq(ConnectionType.Ssh, p.Type);
    Eq("admin", p.User);
    Eq("10.0.0.5", p.Host);
    Eq(2222, p.Port);
    p = ConnectionParams.Parse(["/C=3", "/BAUD=9600", "/M=boot.ttl"], out var macro, out _)!;
    Eq(ConnectionType.Serial, p.Type);
    Eq("COM3", p.PortName);
    Eq(9600, p.Baud);
    Eq("boot.ttl", macro);
    p = ConnectionParams.Parse(["router.local"], out _, out _)!;
    Eq((ConnectionType.Telnet, 23), (p.Type, p.Port));
    Eq(null, ConnectionParams.Parse([], out _, out _));
    Eq(3, ConnectionParams.SplitArgs("host:22 /ssh '/passwd=a b'").Count);
});

TestAsync("telnet negotiation (NAWS, TTYPE, ECHO) and CR NUL", async () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var conn = new TcpConnection("127.0.0.1", port, telnet: true, "xterm-256color");
    var received = new StringBuilder();
    var gotData = new TaskCompletionSource();
    conn.DataReceived += (b, n) =>
    {
        lock (received) received.Append(Encoding.ASCII.GetString(b, 0, n));
        if (received.ToString().Contains('x')) gotData.TrySetResult();
    };
    var accept = listener.AcceptTcpClientAsync();
    await conn.ConnectAsync(100, 30);
    using var server = await accept;
    var s = server.GetStream();
    byte[] hello = [255, 253, 31, 255, 253, 24, 255, 250, 24, 1, 255, 240, 255, 251, 1, (byte)'h', (byte)'i', 13, 0, (byte)'x'];
    await s.WriteAsync(hello);
    await gotData.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Eq("hi\rx", received.ToString());

    conn.Send(Encoding.ASCII.GetBytes("a\r"));
    var reply = new List<byte>();
    var buf = new byte[256];
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline && !(reply.Count >= 3 && reply[^3] == 'a' && reply[^2] == 13 && reply[^1] == 0))
    {
        int n = await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        reply.AddRange(buf.Take(n));
    }
    var hex = string.Join(" ", reply);
    void Has(byte[] seq, string what)
    {
        if (!string.Join(" ", reply).Contains(string.Join(" ", seq))) throw new Exception($"missing {what}: {hex}");
    }
    Has([255, 251, 31], "WILL NAWS");
    Has([255, 250, 31, 0, 100, 0, 30, 255, 240], "NAWS 100x30");
    Has([255, 251, 24], "WILL TTYPE");
    Has([255, 250, 24, 0, .. Encoding.ASCII.GetBytes("XTERM-256COLOR"), 255, 240], "TTYPE IS");
    Has([255, 253, 1], "DO ECHO");
    Has([(byte)'a', 13, 0], "CR NUL");
    conn.Dispose();
    listener.Stop();
});

// ---------------------------------------------------------------- macro

TestAsync("macro: send/wait/if/for/while/call/waitln/recvln", async () =>
{
    var host = new FakeHost();
    var script = """
        ; login example
        timeout = 2
        sendln 'user'
        wait 'login:' 'Password:'
        if result = 2 then
          sendln 'secret'
        elseif result = 1 then
          sendln 'wrong'
        else
          sendln 'timeout'
        endif
        count = 0
        for i 1 5
          count = count + i
        next
        int2str s count
        sendln 'sum=' s
        while count > 0
          count = count - 10
          if count < 0 break
        endwhile
        call sub
        waitln 'Linux'
        sendln inputstr
        recvln
        sendln '[' inputstr ']' #33
        strcopy 'abcdef' 2 3 part
        sendln part
        end
        :sub
        send 'sub' #13#10
        return
        """;
    var runner = new MacroRunner(script.Split('\n'), host);
    host.OnSend = text =>
    {
        if (text == "user\r") runner.OnReceived("Password:\r\n");
        if (text == "sub\r\n") runner.OnReceived("Welcome Linux 6.1\r\nsecond\r\n");
    };
    await runner.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    Eq("user\r|secret\r|sum=15\r|sub\r\n|Welcome Linux 6.1\r|[second]!\r|bcd\r", string.Join("|", host.Sent));
});

TestAsync("macro: wait timeout gives result 0", async () =>
{
    var host = new FakeHost();
    var runner = new MacroRunner(["mtimeout = 200", "wait 'never'", "int2str s result", "send s"], host);
    await runner.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    Eq("0", string.Join("|", host.Sent));
});

Test("macro: syntax errors report the line", () =>
{
    try
    {
        _ = new MacroRunner(["send 'a'", "if 1 then", "send 'b'"], new FakeHost());
        throw new Exception("no error");
    }
    catch (MacroException ex) { Eq(2, ex.Line); }
});

// ---------------------------------------------------------------- xmodem & log

foreach (var oneK in new[] { false, true })
{
    TestAsync($"XMODEM loopback ({(oneK ? "1K" : "128")})", async () =>
    {
        var data = new byte[3000];
        new Random(42).NextBytes(data);
        data[^1] = 0x55;
        XModem? sender = null, receiver = null;
        sender = new XModem(b => receiver!.OnData(b, b.Length));
        receiver = new XModem(b => sender.OnData(b, b.Length));
        var output = new MemoryStream();
        var rx = Task.Run(() => receiver.Receive(output, null, CancellationToken.None));
        var tx = Task.Run(() => sender.Send(new MemoryStream(data), oneK, null, CancellationToken.None));
        await Task.WhenAll(rx, tx).WaitAsync(TimeSpan.FromSeconds(20));
        Eq(Convert.ToHexString(data), Convert.ToHexString(output.ToArray()));
    });
}

Test("plain-text log strips escape sequences", () =>
{
    var path = Path.GetTempFileName();
    using (var log = new SessionLogger(path, binary: false, plain: true, timestamp: false, append: false))
        log.WriteText("\x1b[31mred\x1b[0m\r\n\x1b]0;title\x07line2\n");
    Eq("red\r\nline2\r\n", File.ReadAllText(path));
    File.Delete(path);
});

// ---------------------------------------------------------------- quick commands

Test("quick command: lines, Enter and escapes", () =>
{
    var c = new QuickCommand { Text = "cd /tmp\r\nls -la\r\n" };
    Eq("cd /tmp\r|ls -la\r", string.Join("|", c.Expand("\r")));
    c = new QuickCommand { Text = "echo a\nb", AppendEnter = false };
    Eq("echo a\r\n|b", string.Join("|", c.Expand("\r\n")));
    c = new QuickCommand { Text = @"\x03", Escapes = true, AppendEnter = false };
    Eq("\x03", string.Join("|", c.Expand("\r")));
    Eq("a\tb\x1b[0m\\x", QuickCommand.Unescape(@"a\tb\e[0m\\x"));
    Eq(@"printf 'a\n'", new QuickCommand { Text = @"printf 'a\n'", AppendEnter = false }.Expand("\r")[0]);
});

Test("quick command set: save and load keeps groups and Thai text", () =>
{
    var path = Path.GetTempFileName();
    var set = QuickCommandSet.CreateSamples();
    set.Groups[0].Commands.Add(new QuickCommand { Name = "ทดสอบ", Text = "echo สวัสดี", Confirm = true, Color = "#FF0000" });
    set.Save(path);
    Eq(true, File.ReadAllText(path).Contains("สวัสดี"));
    var loaded = QuickCommandSet.Load(path);
    Eq(set.Groups.Count, loaded.Groups.Count);
    var last = loaded.Groups[0].Commands[^1];
    Eq(("ทดสอบ", "echo สวัสดี", true, "#FF0000"), (last.Name, last.Text, last.Confirm, last.Color));
    Eq(QuickCommandKind.Macro, loaded.Groups[^1].Commands[0].Kind);
    File.Delete(path);
});

Test("quick command samples: macros compile", () =>
{
    foreach (var c in QuickCommandSet.CreateSamples().Groups.SelectMany(g => g.Commands).Where(c => c.Kind == QuickCommandKind.Macro))
        _ = new MacroRunner(c.Text.Replace("\r\n", "\n").Split('\n'), new FakeHost());
});

// ---------------------------------------------------------------- saved sessions & updates

Test("saved session: password is encrypted and round-trips", () =>
{
    var s = new SavedSession { Name = "pi", Params = new ConnectionParams { Type = ConnectionType.Ssh, Host = "10.0.0.2", User = "pi" } };
    s.SetPassword("รหัสผ่าน-p@ss");
    Eq(false, s.ProtectedPassword.Contains("p@ss"));
    Eq("รหัสผ่าน-p@ss", s.GetPassword());
    Eq("รหัสผ่าน-p@ss", s.ToParams().Password);
    s.SetPassword("");
    Eq(false, s.HasPassword);
    s.ProtectedPassword = "bm90LXZhbGlk";  // not DPAPI data -> treated as no password
    Eq("", s.GetPassword());
});

Test("saved session: settings comparison ignores password", () =>
{
    var a = new ConnectionParams { Type = ConnectionType.Serial, PortName = "COM3", Baud = 9600, Password = "x" };
    var b = a.Clone();
    b.Password = "y";
    Eq(true, SavedSession.SameSettings(a, b));
    b.Baud = 115200;
    Eq(false, SavedSession.SameSettings(a, b));
    Eq("COM3 9600", SavedSession.SuggestName(a));
});

Test("update: GitHub release JSON is parsed", () =>
{
    var json = """
        {"tag_name":"v1.4.2","name":"NXTerm 1.4.2","body":"- faster\n- เพิ่มฟีเจอร์","html_url":"https://github.com/o/r/releases/tag/v1.4.2",
         "assets":[{"name":"README.txt","browser_download_url":"https://x/readme","size":10},
                   {"name":"NXTerm.exe","browser_download_url":"https://x/NXTerm.exe","size":6500000,"digest":"sha256:ABCDEF"}]}
        """;
    var r = NXTerm.Updates.Updater.ParseRelease(json)!;
    Eq(new Version(1, 4, 2), r.Version);
    Eq("NXTerm.exe", r.AssetName);
    Eq("https://x/NXTerm.exe", r.AssetUrl);
    Eq(6500000L, r.AssetSize);
    Eq("ABCDEF", r.Sha256);
    Eq(true, r.Notes.Contains("เพิ่มฟีเจอร์"));
    Eq(null, NXTerm.Updates.Updater.ParseRelease("""{"tag_name":"latest-build"}"""));
});

Test("update: current version and repo are readable", () =>
{
    Eq(true, NXTerm.Updates.Updater.CurrentVersionText.Split('.').Length == 3);
    _ = NXTerm.Updates.Updater.Repo;
});

Test("update: version tags", () =>
{
    foreach (var (tag, expect) in new[] { ("v2.0.1", "2.0.1"), ("1.3", "1.3.0"), ("V3", "3.0.0"), ("1.2.0-beta.1", "1.2.0") })
    {
        Eq(true, NXTerm.Updates.Updater.TryParseVersion(tag, out var v));
        Eq(expect, v.ToString(3));
    }
    Eq(false, NXTerm.Updates.Updater.TryParseVersion("nightly", out _));
});

// ---------------------------------------------------------------- run

int failed = 0;
foreach (var (name, body) in tests)
{
    try
    {
        await body();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {ex.Message}");
    }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

sealed class FakeHost : IMacroHost
{
    public readonly List<string> Sent = [];
    public Action<string>? OnSend;
    public string NewLine => "\r";
    public bool IsConnected => true;
    public void Send(string text)
    {
        Sent.Add(text);
        OnSend?.Invoke(text);
    }
    public bool Connect(string args) => true;
    public void Disconnect() { }
    public void LogOpen(string path, bool binary, bool append) { }
    public void LogClose() { }
    public void LogWrite(string text) { }
    public void MessageBox(string text, string title) { }
    public bool YesNoBox(string text, string title) => true;
    public string? InputBox(string text, string title, string def, bool password) => def;
    public void Status(string text) { }
    public void SendBreak() { }
    public void ClearScreen() { }
    public void Display(string text) { }
    public void SetTitle(string title) { }
}
