namespace TexBowser.Sys;

/// <summary>
/// Last-resort crash recorder. A hard crash with no message is undebuggable,
/// so every fatal path appends timestamp + version + command line + full
/// stack to %AppData%/TexBowser/crash.log. Best-effort: never throws.
/// </summary>
public static class CrashLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TexBowser", "crash.log");

    public static void Write(string kind, Exception? ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{kind}] "
                         + $"TexBowser {ThisVersion()} :: {Environment.CommandLine}{Environment.NewLine}"
                         + (ex?.ToString() ?? "(no exception object)") + Environment.NewLine
                         + new string('-', 60) + Environment.NewLine;
            System.IO.File.AppendAllText(Path, entry);
        }
        catch { /* logging must never crash the crash handler */ }
    }

    private static string ThisVersion() =>
        typeof(CrashLog).Assembly.GetName().Version?.ToString() ?? "?";
}
