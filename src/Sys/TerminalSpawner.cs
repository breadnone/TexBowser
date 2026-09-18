using System.Diagnostics;
using System.Reflection;

namespace TexBowser.Sys;

/// <summary>
/// A text UI needs a real interactive terminal. When TexBowser is started from
/// a pipe, an IDE runner or any other non-interactive host, it relaunchs itself
/// inside a dedicated terminal window instead of rendering into the void.
/// Headless modes (--dump/--demo/--selftest/--help) always run inline.
/// </summary>
public static class TerminalSpawner
{
    /// <summary>Pure decision logic (unit-tested).</summary>
    public static bool ShouldSpawn(bool forceNewWindow, bool forceInline, bool alreadySpawned,
        bool inputRedirected, bool outputRedirected, bool userInteractive)
    {
        if (forceInline || alreadySpawned) return false;
        if (forceNewWindow) return true;
        if (!userInteractive) return false; // service-like host: stay put, don't pop windows
        return inputRedirected || outputRedirected;
    }

    public static bool WantsSpawn(string[] args, bool tuiMode)
    {
        if (!tuiMode) return false;
        bool forceNew = Has(args, "--new-window");
        bool forceInline = Has(args, "--inline");
        bool spawned = Has(args, "--spawned");
        return ShouldSpawn(forceNew, forceInline, spawned,
            Console.IsInputRedirected, Console.IsOutputRedirected, Environment.UserInteractive);
    }

    /// <summary>
    /// Launch a dedicated terminal running this app (with --spawned loop guard).
    /// Returns true when a terminal was launched and the caller should exit.
    /// </summary>
    public static bool TrySpawn(string[] args)
    {
        try
        {
            var fwd = args
                .Where(a => !a.Equals("--new-window", StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { "--spawned" })
                .ToList();

            var (target, prefix) = RelaunchTarget();
            var full = new List<string>();
            if (prefix.Length > 0) full.Add(prefix);
            full.AddRange(fwd);

            if (OperatingSystem.IsWindows())
                return SpawnWindows(target, full);
            if (OperatingSystem.IsMacOS())
                return SpawnMac(target, full);
            return SpawnLinux(target, full);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>How to re-invoke the current app: (executable, pre-args, raw).</summary>
    public static (string File, string PrefixArgs) RelaunchTarget()
    {
        string asm = Assembly.GetExecutingAssembly().Location;
        string? proc = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(proc) &&
            Path.GetFileName(proc).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Running via `dotnet TexBowser.dll` / `dotnet run`: re-invoke through dotnet.
            return (proc, asm);
        }
        if (!string.IsNullOrEmpty(proc))
            return (proc, "");
        return ("dotnet", asm);
    }

    private static bool SpawnWindows(string target, List<string> args)
    {
        // `start` always opens a fresh console window (honoring Windows Terminal
        // when it is the default console host). Title first: start treats the
        // first quoted token as the window title.
        string cmd = "/c start \"TexBowser\" /D " + Q(Directory.GetCurrentDirectory())
                   + " " + Q(target)
                   + (args.Count > 0 ? " " + string.Join(" ", args.Select(Q)) : "");
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmd,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return p != null;
    }

    private static bool SpawnLinux(string target, List<string> args)
    {
        string full = "cd " + ShQ(Directory.GetCurrentDirectory()) + "; "
                    + string.Join(" ", new[] { target }.Concat(args).Select(ShQ)) + "; "
                    + "echo; read -p '[TexBowser exited — press Enter to close] ' _";
        string[][] candidates =
        {
            new[] { "x-terminal-emulator", "-e", "sh", "-c", full },
            new[] { "gnome-terminal", "--", "sh", "-c", full },
            new[] { "konsole", "-e", "sh", "-c", full },
            new[] { "xfce4-terminal", "-e", "sh", "-c", full },
            new[] { "xterm", "-e", "sh", "-c", full },
        };
        foreach (var c in candidates)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = c[0],
                    Arguments = string.Join(" ", c.Skip(1).Select(Q)),
                    UseShellExecute = false,
                });
                if (p != null) return true;
            }
            catch { /* try next emulator */ }
        }
        return false;
    }

    private static bool SpawnMac(string target, List<string> args)
    {
        string full = "cd " + ShQ(Directory.GetCurrentDirectory()) + "; "
                    + string.Join(" ", new[] { target }.Concat(args).Select(ShQ));
        string script = "tell application \"Terminal\" to do script " + ShQ(full);
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = "-e " + Q(script),
                UseShellExecute = false,
            });
            return p != null;
        }
        catch { return false; }
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private static string ShQ(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
