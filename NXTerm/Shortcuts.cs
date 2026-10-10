namespace NXTerm;

public static class Shortcuts
{
    /// <summary>Creates "NXTerm - name.lnk" on the desktop that opens NXTerm connected to the saved session.</summary>
    public static string CreateForSession(string sessionName)
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var safe = string.Concat(sessionName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"NXTerm - {safe}.lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = exe;
            link.Arguments = $"\"/S={sessionName}\"";
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = $"NXTerm: {sessionName}";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        return path;
    }
}
