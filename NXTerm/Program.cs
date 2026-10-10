using System.Text;
using NXTerm.Updates;

namespace NXTerm;

static class Program
{
    /// <summary>The multi-size application icon (embedded copy of Resources\NXTerm.ico).</summary>
    public static Icon AppIcon { get; } = LoadIcon();

    static Icon LoadIcon()
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream("NXTerm.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    [STAThread]
    static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        AppSettings.MigrateFromOldName();
        // After an update the previous exe may still be closing; retry for a few seconds.
        Task.Run(async () =>
        {
            for (int i = 0; i < 10 && File.Exists(Updater.ExePath + ".old"); i++)
            {
                Updater.CleanupOldVersion();
                await Task.Delay(1000);
            }
        });
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}
