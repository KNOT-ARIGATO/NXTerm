using System.Text.Json;
using System.Text.Json.Serialization;
using NXTerm.Connections;
using NXTerm.QuickCommands;

namespace NXTerm;

public enum ReceiveNewline { Normal, LfAsCrLf, CrAsCrLf }
public enum TransmitNewline { CR, CRLF, LF }
public enum CursorShape { Block, Underline, Bar }

public sealed class AppSettings
{
    public string FontName = "Consolas";
    public float FontSize = 11f;
    public int Cols = 80;
    public int Rows = 24;
    public string TermType = "xterm-256color";
    public ReceiveNewline ReceiveNewline = ReceiveNewline.Normal;
    public TransmitNewline TransmitNewline = TransmitNewline.CR;
    public bool LocalEcho;
    public string EncodingName = "utf-8";
    public bool BackspaceSendsDel = true;
    public int ScrollbackLines = 10000;
    public CursorShape CursorShape = CursorShape.Block;
    public bool CursorBlink = true;
    public bool CopyOnSelect = true;
    public bool RightClickPaste = true;
    public bool BoldAsBright = true;
    public string ForeColor = "#E0E0E0";
    public string BackColor = "#000000";
    public string CursorColor = "#00E000";

    public bool LogPlainText = true;
    public bool LogTimestamp;
    public bool LogAppend;
    public bool LogBinary;
    public string LogFolder = "";

    public QuickMode QuickMode = QuickMode.Docked;
    public QuickMode QuickLastMode = QuickMode.Docked;  // what Alt+K brings back
    public int QuickDockHeight = 104;
    public int[]? QuickWindowBounds;  // x, y, width, height of the floating window
    public bool QuickWindowOnTop;
    public int QuickGroup;

    // Extra tools: ticked in Setup > Extra tools to show their button on the menu bar.
    public bool ShowRtspTool;
    /// <summary>Recent RTSP addresses, each encrypted with DPAPI because they may contain a camera password.</summary>
    public List<string> RtspHistory = [];
    public int[]? RtspWindowBounds;
    public bool RtspOnTop;

    public bool CheckForUpdates = true;
    public DateTime LastUpdateCheck;
    public string SkippedVersion = "";

    public List<SavedSession> Sessions = [];
    public string LastSession = "";
    public ConnectionParams LastConnection = new();
    public List<string> HostHistory = [];
    public Dictionary<string, string> KnownHosts = [];

    static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NXTerm", "NXTerm.json");

    /// <summary>The program used to be called NTerm: copy its settings over the first time NXTerm starts.</summary>
    public static void MigrateFromOldName()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string newDir = Path.Combine(appData, "NXTerm"), oldDir = Path.Combine(appData, "NTerm");
        if (Directory.Exists(newDir) || !Directory.Exists(oldDir)) return;
        try
        {
            Directory.CreateDirectory(newDir);
            foreach (var (from, to) in new[] { ("NTerm.json", "NXTerm.json"), ("QuickCommands.json", "QuickCommands.json") })
            {
                var src = Path.Combine(oldDir, from);
                if (File.Exists(src)) File.Copy(src, Path.Combine(newDir, to));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static AppSettings Load(string path) =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();

    public static AppSettings LoadDefault()
    {
        try
        {
            if (File.Exists(DefaultPath)) return Load(DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    [JsonIgnore]
    public string NewLineString => TransmitNewline switch
    {
        TransmitNewline.CRLF => "\r\n",
        TransmitNewline.LF => "\n",
        _ => "\r",
    };

    public static Color ParseColor(string s, Color fallback)
    {
        try { return ColorTranslator.FromHtml(s); }
        catch (Exception) { return fallback; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
