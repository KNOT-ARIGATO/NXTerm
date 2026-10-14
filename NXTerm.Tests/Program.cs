using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NXTerm.Connections;
using NXTerm.Emulation;
using NXTerm.Logging;
using NXTerm.Macro;
using NXTerm.QuickCommands;
using NXTerm.Rtsp;
using NXTerm.Transfer;
using NXTerm.Updates;

// Self-contained test runner: dotnet run --project NXTerm.Tests
// Pretend camera for trying the RTSP viewer by hand: dotnet run --project NXTerm.Tests -- --rtsp-server [port] [user password]
if (args is ["--rtsp-server", ..])
{
    int port = args.Length > 1 ? int.Parse(args[1]) : 8554;
    using var camera = new TestRtspServer(port, args.Length > 3 ? args[2] : null, args.Length > 3 ? args[3] : null, widthMb: 40, heightMb: 30, fps: 15);
    Console.WriteLine($"Test camera running at rtsp://127.0.0.1:{camera.Port}/live  (640x480, 15 fps). Press Ctrl+C to stop.");
    Thread.Sleep(Timeout.Infinite);
}

// Online check of the update servers' certificates: dotnet run --project NXTerm.Tests -- --tls-check
// "bundled roots" is the verdict a PC without any suitable Windows root certificate would reach.
if (args is ["--tls-check", ..])
{
    bool allGood = true;
    foreach (var host in new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com" })
    {
        using var tcp = new TcpClient(host, 443);
        using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, chain, errors) =>
        {
            X509Certificate2Collection sent = [.. chain!.ChainPolicy.ExtraStore];
            bool bundled = UpdateTrust.ChainsTo((X509Certificate2)certificate!, sent, UpdateTrust.BundledRoots);

            // Rebuild the chain the way a PC without the right root sees it, and pass that through the real decision.
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var unrelated = new CertificateRequest("CN=Unrelated root", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var bare = new X509Chain();
            bare.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            bare.ChainPolicy.CustomTrustStore.Add(unrelated);
            bare.ChainPolicy.ExtraStore.AddRange(sent);
            bare.ChainPolicy.DisableCertificateDownloads = true;
            bare.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            bool windowsWouldAccept = bare.Build((X509Certificate2)certificate!);
            var status = string.Join("+", bare.ChainStatus.Select(s => s.Status).Distinct());
            bool rescued = !windowsWouldAccept && UpdateTrust.Accept(certificate, bare, SslPolicyErrors.RemoteCertificateChainErrors);

            allGood &= bundled && rescued;
            Console.WriteLine($"{host,-38} Windows: {(errors == SslPolicyErrors.None ? "ok" : errors.ToString())}   bundled roots: {(bundled ? "ok" : "REFUSED")}" +
                              $"   PC without the root ({status}): {(rescued ? "accepted by NXTerm" : "REFUSED")}");
            return true;
        });
        ssl.AuthenticateAsClient(host);
    }
    return allGood ? 0 : 1;
}

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

Test("highlight: keywords get their colour, display only", () =>
{
    var t = new VtTerminal(40, 3);
    t.Feed("test 1: PASS\r\ntest 2: failed (failover ok)\r\nplain line");
    var h = new Highlighter(HighlightRule.Defaults());
    var colors = new Color?[40];
    Color red = ColorTranslator.FromHtml("#FF5555"), green = ColorTranslator.FromHtml("#2ECC71");

    Eq(true, h.Apply(t.GetLine(0), 40, colors));
    Eq((Color?)green, colors[8]);            // P of PASS
    Eq((Color?)green, colors[11]);           // S of PASS
    Eq((Color?)null, colors[0]);             // "test" is not coloured
    Eq(true, h.Apply(t.GetLine(1), 40, colors));
    Eq((Color?)red, colors[8]);              // "failed"
    Eq((Color?)null, colors[16]);            // "failover" is a different word
    Eq((Color?)green, colors[25]);           // "ok"
    Eq(false, h.Apply(t.GetLine(2), 40, colors));
    Eq("test 1: PASS", t.GetLine(0).GetText(0, 40).TrimEnd());   // the stored text is untouched

    // plain word, whole line, and an earlier rule winning over a later one
    var custom = new Highlighter([
        new HighlightRule { Pattern = "fail", Color = "#0000FF", WholeLine = true },
        new HighlightRule { Pattern = "test", Color = "#00FF00" }]);
    Eq(true, custom.Apply(t.GetLine(1), 40, colors));
    Eq((Color?)Color.FromArgb(0, 255, 0), colors[0]);            // "test" matched
    Eq((Color?)null, colors[8]);                                 // "fail" as a whole word does not match "failed"
    t.Feed("\r\ntest: FAIL here");
    Eq(true, custom.Apply(t.GetLine(t.Scrollback.Count + 2), 40, colors));
    Eq((Color?)Color.FromArgb(0, 0, 255), colors[0]);            // whole line is blue, including "test"
    Eq((Color?)Color.FromArgb(0, 0, 255), colors[14]);
    try { new HighlightRule { Pattern = "(", IsRegex = true }.ToRegex(); throw new Exception("bad regex accepted"); }
    catch (ArgumentException) { }
});

Test("highlight: default rules read real log lines sensibly", () =>
{
    var h = new Highlighter(HighlightRule.Defaults());
    // What colour ("R", "Y", "G" or "-") does the given word get on this line?
    string ColorOf(string line, string word)
    {
        var t = new VtTerminal(100, 2);
        t.Feed(line);
        var colors = new Color?[100];
        if (!h.Apply(t.GetLine(0), 100, colors)) return "-";
        var c = colors[line.IndexOf(word, StringComparison.Ordinal)];
        return c == null ? "-" : c.Value.R > 200 && c.Value.G < 120 ? "R" : c.Value.R > 200 ? "Y" : "G";
    }
    (string Line, string Word, string Expect)[] cases =
    [
        ("Tests: 12 passed, 0 failed", "passed", "G"),
        ("Tests: 12 passed, 0 failed", "failed", "G"),          // "0 failed" is good news
        ("Build finished with no errors", "errors", "G"),
        ("Tests: 10 passed, 2 failed", "failed", "R"),
        ("ping: host not found", "found", "R"),                 // negated good word
        ("status: not ok", "ok", "R"),
        ("sh: cannot open /dev/ttyUSB0", "cannot", "R"),
        ("ls: no such file or directory", "such", "R"),
        ("[FAILED] Failed to start nginx.service", "FAILED", "R"),
        ("[  OK  ] Started OpenSSH server", "OK", "G"),
        ("Active: active (running) since Mon", "running", "G"),
        ("E: invalid checksum", "invalid", "R"),
        ("checksum valid", "valid", "G"),
        ("WARNING: low voltage, CPU throttled", "throttled", "Y"),
        ("Segmentation fault (core dumped)", "Segmentation", "R"),
        ("wlan0: disconnected", "disconnected", "R"),
        ("wlan0: connected to HomeWiFi", "connected", "G"),
        ("Download complete", "complete", "G"),
        ("starting failover node", "failover", "-"),           // not the word "fail"
        ("password:", "password", "-"),                         // not the word "pass"
        ("booking confirmed", "booking", "-"),                  // contains "ok" but is another word
    ];
    foreach (var (line, word, expect) in cases)
        Eq(expect, ColorOf(line, word), $"\"{word}\" in \"{line}\":");
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

Test("update: each kind of download only updates to its own kind", () =>
{
    var json = """
        {"tag_name":"v2.0.0","assets":[
          {"name":"NXTerm-standalone.exe","browser_download_url":"https://x/big","size":55000000},
          {"name":"NXTerm.exe","browser_download_url":"https://x/small","size":7000000}]}
        """;
    Eq("https://x/small", NXTerm.Updates.Updater.ParseRelease(json, standalone: false)!.AssetUrl);
    Eq("https://x/big", NXTerm.Updates.Updater.ParseRelease(json, standalone: true)!.AssetUrl);
    // a release without the standalone file must not offer the small one to a standalone install
    var smallOnly = """{"tag_name":"v2.0.0","assets":[{"name":"NXTerm.exe","browser_download_url":"https://x/small","size":1}]}""";
    Eq(null, NXTerm.Updates.Updater.ParseRelease(smallOnly, standalone: true)!.AssetUrl);
    Eq(false, NXTerm.Updates.Updater.IsStandalone);
});

Test("update: failures explain the cause and what to try", () =>
{
    var ssl = new HttpRequestException("The SSL connection could not be established, see inner exception.",
        new System.Security.Authentication.AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: NotTimeValid"));
    var text = NXTerm.Updates.Updater.DescribeFailure(ssl, "RemoteCertificateChainErrors (NotTimeValid), issued by \"Sectigo\"");
    Eq(true, text.Contains("was not accepted") && text.Contains("NotTimeValid") && !text.Contains("see inner exception"));
    Eq(true, text.Contains("date and time of this PC"));
    text = NXTerm.Updates.Updater.DescribeFailure(ssl, "RemoteCertificateChainErrors (UntrustedRoot), issued by \"Some Antivirus CA\"");
    Eq(true, text.Contains("Some Antivirus CA") && text.Contains("Windows Update"));
    text = NXTerm.Updates.Updater.DescribeFailure(new HttpRequestException("The SSL connection could not be established, see inner exception.",
        new IOException("Unable to read data from the transport connection.")), null);
    Eq(true, text.Contains("Unable to read data") && text.Contains("cut before it could be secured"));
    Eq(true, NXTerm.Updates.Updater.DescribeFailure(new TaskCanceledException(), null).Contains("did not answer in time"));
});

Test("update: bundled root certificates load and are current", () =>
{
    var roots = NXTerm.Updates.UpdateTrust.BundledRoots;
    Eq(8, roots.Count);
    foreach (var r in roots)
    {
        Eq(r.Subject, r.Issuer, "self-signed root:");
        Eq(true, r.NotAfter > DateTime.Now.AddYears(1), r.Subject + " still valid for a year:");
        Eq(false, r.HasPrivateKey);
    }
    // the root GitHub's certificate chain ends in, and the one behind the release downloads
    Eq(true, roots.Any(r => r.Thumbprint == "D1CBCA5DB2D52A7F693B674DE5F05A1D0C957DF0"), "USERTrust ECC:");
    Eq(true, roots.Any(r => r.Thumbprint == "CABD2A79A1076A31F21D253635CB039D4329A5E8"), "ISRG Root X1:");
});

Test("update: only a chain to a known root is accepted", () =>
{
    // A private certificate authority, like the one an HTTPS-inspecting proxy or antivirus installs.
    using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var now = DateTimeOffset.UtcNow;
    CertificateRequest Authority(string name, ECDsa key)
    {
        var req = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req;
    }
    using var root = Authority("NXTerm Test Root", rootKey).CreateSelfSigned(now.AddDays(-30), now.AddDays(300));
    using var caWithKey = Authority("NXTerm Test CA", caKey).Create(root, now.AddDays(-20), now.AddDays(200), [1, 2, 3]).CopyWithPrivateKey(caKey);
    X509Certificate2 Leaf(DateTimeOffset from, DateTimeOffset to)
    {
        var req = new CertificateRequest("CN=api.github.com", leafKey, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("api.github.com");
        req.CertificateExtensions.Add(san.Build());
        return req.Create(caWithKey, from, to, [4, 5, 6]);
    }
    using var leaf = Leaf(now.AddDays(-10), now.AddDays(100));
    using var expired = Leaf(now.AddDays(-10), now.AddDays(-1));
    using var ca = X509CertificateLoader.LoadCertificate(caWithKey.RawData);
    X509Certificate2Collection sent = [ca], testRoots = [root];

    Eq(true, UpdateTrust.ChainsTo(leaf, sent, testRoots), "chain to a root in the list:");
    Eq(false, UpdateTrust.ChainsTo(leaf, sent, UpdateTrust.BundledRoots), "private root against the bundled list:");
    Eq(false, UpdateTrust.ChainsTo(expired, sent, testRoots), "expired certificate:");
    Eq(false, UpdateTrust.ChainsTo(leaf, [], testRoots), "missing intermediate:");
    Eq(false, UpdateTrust.ChainsTo(leaf, sent, []), "no roots:");

    // What Windows hands to the callback for this certificate: an untrusted root, as on the proxy's PC.
    using var chain = new X509Chain();
    chain.ChainPolicy.ExtraStore.Add(ca);
    chain.ChainPolicy.ExtraStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    Eq(false, chain.Build(leaf));
    Eq(true, chain.ChainStatus.Any(s => s.Status == X509ChainStatusFlags.UntrustedRoot));
    Eq(false, UpdateTrust.Accept(leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors), "private root is refused:");
    Eq(true, UpdateTrust.Accept(leaf, chain, SslPolicyErrors.None), "what Windows accepted stays accepted:");
    Eq(false, UpdateTrust.Accept(leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch), "wrong host name:");
    Eq(false, UpdateTrust.Accept(null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
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

// ---------------------------------------------------------------- RTSP viewer

Test("rtsp: RTP packets are rebuilt into pictures", () =>
{
    static byte[] Rtp(uint ts, bool marker, params byte[] payload)
    {
        var p = new byte[12 + payload.Length];
        p[0] = 0x80;
        p[1] = (byte)(96 | (marker ? 0x80 : 0));
        for (int i = 0; i < 4; i++) p[4 + i] = (byte)(ts >> (24 - 8 * i));
        payload.CopyTo(p, 12);
        return p;
    }
    var frames = new List<(string Hex, uint Ts, bool Key)>();
    var d = new H264Depacketizer();
    d.FrameReady += (f, ts, key) => frames.Add((Convert.ToHexString(f), ts, key));

    d.OnRtp(Rtp(100, true, 0x41, 1, 2));                                   // a picture before the first key frame is dropped
    Eq(0, frames.Count);
    d.OnRtp(Rtp(200, false, 24, 0, 2, 0x67, 0xAA, 0, 2, 0x68, 0xBB));      // STAP-A: SPS + PPS
    d.OnRtp(Rtp(200, false, 0x7C, 0x85, 1, 2));                            // FU-A start of the key frame
    d.OnRtp(Rtp(200, true, 0x7C, 0x45, 3, 4));                             // FU-A end
    Eq(1, frames.Count);
    Eq(("0000000167AA0000000168BB000000016501020304", 200u, true), frames[0]);
    d.OnRtp(Rtp(300, true, 0x41, 9));                                      // single NAL, not a key frame
    Eq(("000000014109", 300u, false), frames[1]);

    // parameter sets from the SDP are added when the camera does not repeat them
    var d2 = new H264Depacketizer();
    d2.FrameReady += (f, ts, key) => frames.Add((Convert.ToHexString(f), ts, key));
    d2.SetParameterSets([0x67, 1], [0x68, 2]);
    d2.OnRtp(Rtp(1, true, 0x65, 7));
    Eq("0000000167010000000168020000000165" + "07", frames[2].Hex);
});

TestAsync("rtsp: client logs in (Digest) and receives the camera's pictures", async () =>
{
    using var camera = new TestRtspServer(user: "admin", password: "s3cret");
    using var client = new RtspClient($"rtsp://admin:s3cret@127.0.0.1:{camera.Port}/live");
    var frames = new List<byte[]>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    bool playing = false;
    client.FrameReceived += (f, _, _) =>
    {
        lock (frames)
        {
            frames.Add(f);
            if (frames.Count >= 3) cts.Cancel();
        }
    };
    await Task.Run(() =>
    {
        try { client.Run(cts.Token, () => playing = true); }
        catch (OperationCanceledException) { }
    });
    Eq(true, playing);
    Eq(true, frames.Count >= 3);
    Eq(Convert.ToHexString(TestH264.AnnexBFrame(20, 15, 0)), Convert.ToHexString(frames[0]));
    Eq(Convert.ToHexString(TestH264.AnnexBFrame(20, 15, 2)), Convert.ToHexString(frames[2]));
    Eq(false, client.DisplayUrl.Contains("s3cret"));
});

Test("rtsp: wrong or missing password is reported, not retried", () =>
{
    using var camera = new TestRtspServer(user: "admin", password: "s3cret");
    foreach (var (url, expect) in new[] { ($"rtsp://admin:wrong@127.0.0.1:{camera.Port}/live", "rejected"), ($"rtsp://127.0.0.1:{camera.Port}/live", "needs a user name") })
    {
        using var client = new RtspClient(url);
        try
        {
            client.Run(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            throw new Exception("no error for " + expect);
        }
        catch (RtspException ex)
        {
            Eq(true, ex.Fatal);
            Eq(true, ex.Message.Contains(expect));
        }
    }
    try { _ = new RtspClient("http://example.com/x"); throw new Exception("accepted a non-rtsp address"); }
    catch (RtspException ex) { Eq(true, ex.Fatal); }
});

Test("rtsp: Windows H.264 decoder shows the test picture", () =>
{
    H264Decoder decoder;
    try { decoder = new H264Decoder(); }
    catch (RtspException ex) when (ex.Fatal)
    {
        Console.WriteLine("      (skipped: " + ex.Message + ")");
        return;
    }
    using (decoder)
    {
        byte[]? picture = null;
        int w = 0, h = 0, fed = 0;
        for (; fed < 60 && picture == null; fed++)
            decoder.Decode(TestH264.AnnexBFrame(20, 15, fed), fed * 1_000_000L, (pw, ph) => new byte[pw * ph * 4],
                (buf, pw, ph) => { picture ??= buf; w = pw; h = ph; });
        if (picture == null) throw new Exception("the decoder produced no picture after 60 frames");
        Eq((320, 240), (w, h));
        (int R, int G, int B)[] bars = [(255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0), (255, 0, 255), (255, 0, 0), (0, 0, 255), (0, 0, 0)];
        for (int i = 0; i < 8; i++)
        {
            int o = (40 * w + 20 + 40 * i) * 4;
            var got = (R: (int)picture[o + 2], G: (int)picture[o + 1], B: (int)picture[o]);
            if (Math.Abs(got.R - bars[i].R) > 20 || Math.Abs(got.G - bars[i].G) > 20 || Math.Abs(got.B - bars[i].B) > 20)
                throw new Exception($"bar {i}: expected about {bars[i]} but got {got}");
        }
        Console.WriteLine($"      (first picture after {fed} frame(s))");
    }
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
