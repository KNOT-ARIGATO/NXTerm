using System.IO.Ports;
using System.Text;
using System.Text.Json.Serialization;

namespace NXTerm.Connections;

public enum ConnectionType { Telnet, Ssh, Raw, Serial }

public sealed class ConnectionParams
{
    public ConnectionType Type = ConnectionType.Ssh;
    public string Host = "";
    public int Port = 22;
    public string User = "";
    [JsonIgnore] public string Password = "";
    public string KeyFile = "";

    public string PortName = "COM1";
    public int Baud = 115200;
    public int DataBits = 8;
    public Parity Parity = Parity.None;
    public StopBits StopBits = StopBits.One;
    public Handshake Handshake = Handshake.None;
    public int CharDelayMs;
    public int LineDelayMs;

    public ConnectionParams Clone() => (ConnectionParams)MemberwiseClone();

    public static int DefaultPort(ConnectionType t) => t switch
    {
        ConnectionType.Ssh => 22,
        ConnectionType.Telnet => 23,
        _ => 0,
    };

    public override string ToString() => Type == ConnectionType.Serial
        ? $"{PortName} {Baud} {DataBits}{"NOEMS"[(int)Parity]}{StopText}"
        : $"{(User.Length > 0 && Type == ConnectionType.Ssh ? User + "@" : "")}{Host}:{Port} ({Type.ToString().ToLowerInvariant()})";

    string StopText => StopBits switch
    {
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "1",
    };

    /// <summary>Command line that recreates this connection (without the password).</summary>
    public string ToCommandLine() => Type switch
    {
        ConnectionType.Serial => $"/C={PortName} /BAUD={Baud}",
        ConnectionType.Ssh => $"{Host}:{Port} /ssh" + (User.Length > 0 ? $" /user={User}" : "") +
                              (KeyFile.Length > 0 ? $" \"/keyfile={KeyFile}\"" : ""),
        ConnectionType.Raw => $"{Host}:{Port} /raw",
        _ => $"{Host}:{Port} /telnet",
    };

    /// <summary>
    /// Parses Tera Term style arguments:
    /// host[:port] /ssh /telnet /raw /P=port /user=name /passwd=pw /keyfile=path /C=n /BAUD=n /M=macro /L=log
    /// </summary>
    public static ConnectionParams? Parse(IEnumerable<string> args, out string? macro, out string? log)
    {
        macro = log = null;
        var p = new ConnectionParams { Type = ConnectionType.Telnet };
        bool any = false, portSet = false;
        foreach (var raw in args)
        {
            var a = raw.Trim();
            if (a.Length == 0) continue;
            if (a[0] == '/')
            {
                var body = a[1..];
                int eq = body.IndexOf('=');
                string key = (eq < 0 ? body : body[..eq]).ToLowerInvariant();
                string val = eq < 0 ? "" : body[(eq + 1)..].Trim('"');
                switch (key)
                {
                    case "ssh": case "ssh2": p.Type = ConnectionType.Ssh; any = true; break;
                    case "telnet": case "t": p.Type = ConnectionType.Telnet; any = true; break;
                    case "raw": case "nossh": p.Type = ConnectionType.Raw; any = true; break;
                    case "c":
                        p.Type = ConnectionType.Serial;
                        p.PortName = int.TryParse(val, out int n) ? $"COM{n}" : val.ToUpperInvariant();
                        any = true;
                        break;
                    case "baud": case "speed": if (int.TryParse(val, out int b)) p.Baud = b; break;
                    case "p": if (int.TryParse(val, out int port)) { p.Port = port; portSet = true; } break;
                    case "user": p.User = val; break;
                    case "passwd": case "password": p.Password = val; break;
                    case "keyfile": p.KeyFile = val; break;
                    case "m": macro = val; break;
                    case "l": log = val; break;
                }
            }
            else
            {
                var host = a;
                int at = host.LastIndexOf('@');
                if (at > 0)
                {
                    p.User = host[..at];
                    host = host[(at + 1)..];
                }
                int colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out int port))
                {
                    p.Port = port;
                    portSet = true;
                    host = host[..colon];
                }
                p.Host = host;
                any = true;
            }
        }
        if (!any) return null;
        if (p.Type != ConnectionType.Serial)
        {
            if (!portSet) p.Port = DefaultPort(p.Type);
            if (p.Host.Length == 0 || p.Port <= 0) return null;
        }
        return p;
    }

    /// <summary>Splits a command line into arguments, honoring double and single quotes.</summary>
    public static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        bool has = false;
        foreach (char c in s)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else sb.Append(c);
            }
            else if (c is '"' or '\'') { quote = c; has = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (has || sb.Length > 0) list.Add(sb.ToString());
                sb.Clear();
                has = false;
            }
            else sb.Append(c);
        }
        if (has || sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}
