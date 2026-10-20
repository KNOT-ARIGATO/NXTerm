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

/// <summary>One tab of buttons.</summary>
public sealed class QuickCommandTab
{
    public string Name = "";
    public List<QuickCommand> Commands = [];
}

/// <summary>A group of tabs, for example one per product or device.</summary>
public sealed class QuickCommandGroup
{
    public string Name = "";
    public List<QuickCommandTab> Tabs = [];

    /// <summary>Only in files from before groups had tabs: there each "group" was a single tab of commands.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QuickCommand>? Commands;
}

public sealed class QuickCommandSet
{
    public List<QuickCommandGroup> Groups = [];

    /// <summary>Name of the group that tabs from an older file are put in.</summary>
    public const string DefaultGroupName = "General";

    /// <summary>The group with this name (ignoring case), or null.</summary>
    public QuickCommandGroup? FindGroup(string name) =>
        Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Index of the group with this name, created at the end when there is none.</summary>
    public int GroupIndex(string name)
    {
        int g = Groups.FindIndex(x => x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (g >= 0) return g;
        Groups.Add(new QuickCommandGroup { Name = name });
        return Groups.Count - 1;
    }

    /// <summary>Indexes of the named tab in the named group; both are created when they do not exist yet.</summary>
    public (int Group, int Tab) TabIndex(string group, string tab)
    {
        int g = GroupIndex(group);
        var tabs = Groups[g].Tabs;
        int t = tabs.FindIndex(x => x.Name.Equals(tab, StringComparison.CurrentCultureIgnoreCase));
        if (t < 0)
        {
            tabs.Add(new QuickCommandTab { Name = tab });
            t = tabs.Count - 1;
        }
        return (g, t);
    }

    static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // keep Thai readable in the file
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NXTerm", "QuickCommands.json");

    public static QuickCommandSet Load(string path) => Parse(File.ReadAllText(path));

    public static QuickCommandSet Parse(string json)
    {
        var set = JsonSerializer.Deserialize<QuickCommandSet>(json, Json) ?? new QuickCommandSet();
        set.Upgrade();
        return set;
    }

    /// <summary>
    /// Files from before 1.5 had a single level: each group was one tab of commands. Those tabs are moved,
    /// in order, into one group so nothing is lost and the buttons look the same as before.
    /// </summary>
    void Upgrade()
    {
        var old = Groups.Where(g => g.Commands != null && g.Tabs.Count == 0).ToList();
        if (old.Count == 0) return;
        var general = new QuickCommandGroup
        {
            Name = DefaultGroupName,
            Tabs = old.Select(g => new QuickCommandTab { Name = g.Name, Commands = g.Commands! }).ToList(),
        };
        int at = Groups.IndexOf(old[0]);
        Groups.RemoveAll(old.Contains);
        Groups.Insert(Math.Min(at, Groups.Count), general);
        foreach (var g in Groups) g.Commands = null;
    }

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
                Name = DefaultGroupName,
                Tabs =
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
            },
        ],
    };
}
