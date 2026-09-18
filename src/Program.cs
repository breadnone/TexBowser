using TexBowser.Engine;
using TexBowser.Sys;
using TexBowser.Ui;

namespace TexBowser;

class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("task", e.Exception);
            e.SetObserved();
        };

        string? dataDir = null;
        string? dumpUrl = null;
        string? demo = null;
        int width = DefaultWidth();
        bool selftest = false;
        bool helped = false;
        string? startUrl = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                case "--db" when i + 1 < args.Length:
                    dataDir = args[++i]; // legacy alias of --data-dir
                    break;
                case "--dump" when i + 1 < args.Length:
                    dumpUrl = args[++i];
                    break;
                case "--demo":
                    demo = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "demo";
                    break;
                case "--width" when i + 1 < args.Length && int.TryParse(args[i + 1], out int w):
                    width = Math.Clamp(w, 40, 300);
                    i++;
                    break;
                case "--selftest":
                    selftest = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    helped = true;
                    PrintHelp();
                    return 0;
                case "--new-window":
                case "--inline":
                case "--spawned":
                    break; // handled by the terminal-spawn check below
                default:
                    if (!args[i].StartsWith("--")) startUrl = args[i];
                    break;
            }
        }

        if (selftest) return SelfTest.Run();

        dataDir ??= BrowserEngine.DefaultDataDir();

        // Headless render to stdout.
        if (dumpUrl != null)
        {
            using var engine = new BrowserEngine(dataDir);
            try
            {
                if (dumpUrl.Equals("about:history", StringComparison.OrdinalIgnoreCase)
                    || dumpUrl.Equals(":history", StringComparison.OrdinalIgnoreCase))
                {
                    var (lines, _) = engine.HistoryLines(width);
                    foreach (var line in lines) Console.WriteLine(line);
                    return 0;
                }
                var page = engine.LoadAsync(dumpUrl, width).GetAwaiter().GetResult();
                Console.WriteLine(page.Text);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"dump failed: {ex.Message}");
                return 2;
            }
        }

        if (demo != null)
        {
            using var engine = new BrowserEngine(dataDir);
            string about = demo switch
            {
                "cards" => "about:demo-cards",
                "table" => "about:demo-table",
                "grail" => "about:demo-grail",
                "tailwind" => "about:demo-tailwind",
                "search" => "about:demo-search",
                _ => "about:demo",
            };
            var page = engine.LoadAsync(about, width).GetAwaiter().GetResult();
            Console.WriteLine(page.Text);
            return 0;
        }

        // The TUI needs a real interactive terminal. From a pipe, IDE runner or
        // any redirected host, relaunch in a dedicated window. Headless modes
        // (--dump/--demo/--selftest/--help) always run inline.
        bool tuiMode = dumpUrl == null && demo == null && !selftest && !helped;
        if (tuiMode && TerminalSpawner.WantsSpawn(args, tuiMode: true))
        {
            if (TerminalSpawner.TrySpawn(args))
            {
                Console.Error.WriteLine("TexBowser opened in a new window (this console stays put).");
                return 0;
            }
            Console.Error.WriteLine("note: could not open a dedicated terminal; running inline.");
        }

        using (var engine = new BrowserEngine(dataDir))
            new BrowserApp(engine).Run(startUrl);
        return 0;
    }

    private static int DefaultWidth()
    {
        try { return Math.Clamp(Console.WindowWidth, 40, 300); }
        catch { return 100; }
    }

    private static void PrintHelp()
    {
        string version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "?";
        Console.WriteLine($"""
            TexBowser v{version} — text web browser (C# + Terminal.Gui v2)

            Usage:
              TexBowser [url]                 open the TUI browser (default page: about:home)
              TexBowser --dump <url>          render a page to stdout, no TUI
              TexBowser --demo [demo|cards|table|grail|tailwind|search]   render a built-in page
              TexBowser --selftest            run offline verification suite
              TexBowser --width <n>           viewport width for --dump/--demo (40-300)
              TexBowser --data-dir <dir>      history storage dir (default: %AppData%/TexBowser)
              TexBowser --new-window          force a dedicated terminal window
              TexBowser --inline              never spawn a window (run in this console)

            Inside the browser (text-only pages, full browser chrome + mouse):
              url box + Enter/Go ............ open URL (bare number follows that link)
              < > buttons ................... per-tab back / forward session history
              Reload / Stop ................. re-fetch the page / cancel a slow load
              Hist button (or :history) ..... persistent per-URL log, number to open
              Tab strip + ................... new tab, next to the tabs (10 max)
              Tab strip x (top-right) ....... close active tab
              Click a tab header ............ switch tabs (or Ctrl+Tab, per-tab history kept)
              Right-click tab strip ......... close tab / close tabs to the right
              Click any [n] marker .......... follow that link (mouse fully supported)
              Hover a link .................. highlight cue + target URL preview
              Scrollbar / mouse wheel ....... scroll the page
              Ctrl+T / Ctrl+W ............... new tab (lands in address box, text selected) / close tab
              Ctrl+Tab / Ctrl+L / Ctrl+R .... next tab / focus url / reload
              Ctrl+Shift+Left/Right ......... move active tab left / right
              :clear-history ................ wipe the browsing log
              Forms work for real ........... type in fields, toggle checks, pick options,
                                              Submit posts (GET navigates, POST incl. upload),
                                              Reset restores — all native TUI controls
              about:home .................... landing page (Ready To Surf!)
              about:demo[-cards|-table|-grail|-tailwind|-search] .. offline sample pages
              Esc ........................... quit

            How layout works: every page is analyzed fresh — regions
            (header/nav/main/aside/footer) are scored from tags, ARIA roles and
            id/class hooks; column geometry is detected in order (CSS grid,
            flexbox, Bootstrap/Tailwind/Bulma/Foundation classes, width:%/px,
            floats, card repetition, layout tables, incl. rules from <style>
            blocks) and reconstructed as text grids: side-by-side columns with
            exact widths, sidebars at the detected ratio (stacked when narrow),
            real <table>s as bordered tables.
            """);
    }
}
