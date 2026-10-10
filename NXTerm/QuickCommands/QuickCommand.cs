using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NXTerm.QuickCommands;

public enum QuickCommandKind { Text, Macro }

public sealed class QuickCommand
{
    public string Name = "";
    public string Text = "";
    public QuickCommandKind Kind = QuickCommandKind.Text;
    public bool AppendEnter = true;
    public bool Escapes;
    public int LineDelayMs;
    public bool Confirm;
    public string Color = "";  // "" = default button color

    public QuickCommand Clone() => (QuickCommand)MemberwiseClone();

    /// <summary>The pieces to send, one per line, with new-lines added as configured.</summary>
    public List<string> Expand(string newLine)
    {
        var lines = Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
        var list = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var s = Escapes ? Unescape(lines[i]) : lines[i];
            if (AppendEnter || i < lines.Length - 1) s += newLine;
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }

    /// <summary>Expands \r \n \t \e \0 \\ and \xHH.</summary>
    public static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }
            char n = s[i + 1];
            switch (n)
            {
                case 'r': sb.Append('\r'); i++; break;
                case 'n': sb.Append('\n'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case 'e': sb.Append('\x1b'); i++; break;
                case '0': sb.Append('\0'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                case 'x' when i + 3 < s.Length &&
                              int.TryParse(s.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v):
                    sb.Append((char)v);
                    i += 3;
                    break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

public sealed class QuickCommandGroup
{
    public string Name = "";
    public List<QuickCommand> Commands = [];
}

public sealed class QuickCommandSet
{
    public List<QuickCommandGroup> Groups = [];

    static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // keep Thai readable in the file
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NXTerm", "QuickCommands.json");

    public static QuickCommandSet Load(string path) =>
        JsonSerializer.Deserialize<QuickCommandSet>(File.ReadAllText(path), Json) ?? new QuickCommandSet();

    public static QuickCommandSet LoadDefault()
    {
        try
        {
            if (File.Exists(DefaultPath)) return Load(DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return CreateSamples();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    static QuickCommand Cmd(string name, string text, bool confirm = false, string color = "") =>
        new() { Name = name, Text = text, Confirm = confirm, Color = color };

    public static QuickCommandSet CreateSamples() => new()
    {
        Groups =
        [
            new()
            {
                Name = "Linux",
                Commands =
                [
                    Cmd("ls -la", "ls -la"),
                    Cmd("Disk", "df -h"),
                    Cmd("Memory", "free -h"),
                    Cmd("Uptime", "uptime"),
                    Cmd("IP", "ip -br addr"),
                    Cmd("Clear", "clear"),
                    new() { Name = "Ctrl+C", Text = "\\x03", Escapes = true, AppendEnter = false },
                ],
            },
            new()
            {
                Name = "Raspberry Pi",
                Commands =
                [
                    Cmd("CPU temp", "vcgencmd measure_temp"),
                    Cmd("Throttled", "vcgencmd get_throttled"),
                    Cmd("Services", "systemctl --failed"),
                    Cmd("apt update", "sudo apt update", confirm: true),
                    Cmd("Reboot", "sudo reboot", confirm: true, color: "#C0392B"),
                ],
            },
            new()
            {
                Name = "AT / Serial",
                Commands =
                [
                    Cmd("AT", "AT"),
                    Cmd("ATI", "ATI"),
                    Cmd("Firmware", "AT+GMR"),
                    Cmd("Reset", "AT+RST", confirm: true),
                ],
            },
            new()
            {
                Name = "Macro",
                Commands =
                [
                    new()
                    {
                        Name = "System info",
                        Kind = QuickCommandKind.Macro,
                        Text = "; waits for the prompt between commands\r\ntimeout = 5\r\nsendln ''\r\nwait '$ ' '# '\r\nsendln 'uname -a'\r\nwait '$ ' '# '\r\nsendln 'uptime'",
                    },
                ],
            },
        ],
    };
}
