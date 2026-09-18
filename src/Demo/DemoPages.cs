namespace TexBowser.Demo;

/// <summary>Built-in offline pages (about:demo*) so the browser shines without network.</summary>
public static class DemoPages
{
    public const string ThreeCol = """
        <!doctype html><html><head><title>Acme Widgets — Home</title></head><body>
        <header class="masthead"><h1>Acme Widgets</h1><p>Quality widgets since 1987. Free shipping over $50.</p></header>
        <nav class="navbar"><a href="https://example.com/">Home</a><a href="https://example.com/shop">Shop</a><a href="https://example.com/docs">Docs</a><a href="https://example.com/about">About</a><a href="https://example.com/contact">Contact</a></nav>
        <div class="container"><div class="row">
          <div class="col-md-4"><h2>Starter Kit</h2><p>Everything a hobbyist needs: ten widgets, a spanner, and our famous illustrated guide to widget care.</p><p><a href="https://example.com/shop/starter">Buy Starter — $29</a></p></div>
          <div class="col-md-4"><h2>Pro Bundle</h2><p>For workshops and small factories. Includes hardened steel widgets rated for 10,000 hours of continuous use.</p><p><a href="https://example.com/shop/pro">Buy Pro — $149</a></p></div>
          <div class="col-md-4"><h2>Enterprise</h2><p>Custom alloys, on-site calibration, and a dedicated engineer. Trusted by three national railways.</p><p><a href="https://example.com/contact">Talk to sales</a></p></div>
        </div></div>
        <aside class="sidebar"><h3>News</h3><p>WidgetCon 2026 tickets are on sale now.</p><p>Read the <a href="https://example.com/blog/spring">spring maintenance guide</a>.</p></aside>
        <footer class="site-footer"><p>© 2026 Acme Widgets · <a href="https://example.com/privacy">Privacy</a> · <a href="https://example.com/terms">Terms</a></p></footer>
        </body></html>
        """;

    public const string Cards = """
        <!doctype html><html><head><title>DevBlog — Latest posts</title></head><body>
        <header id="topbar"><h1>DevBlog</h1><p>Notes on terminals, text UIs and retro computing.</p></header>
        <nav role="navigation"><a href="https://example.com/">Latest</a><a href="https://example.com/tags">Tags</a><a href="https://example.com/rss">RSS</a></nav>
        <main id="content"><div class="post-grid">
          <article class="post-card"><h3>Why text UIs are back</h3><p>Low bandwidth, full keyboard control, and zero distractions. I rebuilt my whole workflow around the terminal.</p><p><a href="https://example.com/p/tui">Read more</a></p></article>
          <article class="post-card"><h3>CSS grid without CSS</h3><p>How to sniff a 12-column layout from markup alone and re-typeset it for an 80-column world. Fractions included.</p><p><a href="https://example.com/p/grid">Read more</a></p></article>
          <article class="post-card"><h3>SQLite is all you need</h3><p>One file, zero servers, and your app remembers everything. A love letter to the world's most deployed database.</p><p><a href="https://example.com/p/sqlite">Read more</a></p></article>
        </div></main>
        <footer><p>© 2026 DevBlog · built with patience</p></footer>
        </body></html>
        """;

    public const string TablesAndSidebar = """
        <!doctype html><html><head><title>RailStats — Timetable</title></head><body>
        <nav class="main-nav"><a href="https://example.com/">Departures</a><a href="https://example.com/arrivals">Arrivals</a><a href="https://example.com/fares">Fares</a></nav>
        <div class="row">
          <div class="content col-md-8"><h2>Evening departures</h2><p>Live from platform screens. Times shown in local time.</p>
          <table><tr><th>Time</th><th>Train</th><th>Destination</th><th>Platform</th><th>Status</th></tr>
          <tr><td>18:04</td><td>IC 221</td><td>Harbour City</td><td>3</td><td>On time</td></tr>
          <tr><td>18:17</td><td>RE 9</td><td>Mill Valley via Old Town</td><td>7</td><td>Delayed +6</td></tr>
          <tr><td>18:29</td><td>IC 225</td><td>Harbour City</td><td>3</td><td>On time</td></tr>
          <tr><td>18:52</td><td>N 44</td><td>Night service to Port West</td><td>1</td><td>Boarding</td></tr></table>
          <p style="width:100%">Full timetable as <a href="https://example.com/pdf">PDF download</a>.</p></div>
          <div class="sidebar col-md-4"><h3>Service alerts</h3><p>Engineering works Sunday: buses replace trains north of Mill Valley.</p><p>Lost property: <a href="https://example.com/lost">claim form</a>.</p></div>
        </div>
        <footer class="colophon"><p>RailStats demo data · not for navigation</p></footer>
        </body></html>
        """;

    public static string? Get(string url) => url.ToLowerInvariant() switch
    {
        "about:home" or "about:blank" => Home,
        "about:demo" => ThreeCol,
        "about:demo-cards" => Cards,
        "about:demo-table" => TablesAndSidebar,
        "about:demo-grail" => HolyGrail,
        "about:demo-tailwind" => Tailwindish,
        "about:demo-search" => Search,
        _ => null,
    };

    /// <summary>
    /// Default landing / new-tab page. Deliberately a dummy with no links,
    /// no forms and no nav hooks: the TUI renders it as a centered
    /// "Ready To Surf!" message (see BrowserApp home rendering) instead of
    /// the normal perceived flow.
    /// </summary>
    public const string Home = """
        <!doctype html><html><head><title>New Tab</title></head><body>
        <main><h1>Ready To Surf!</h1><p>Type an address above and press Enter.</p></main>
        </body></html>
        """;

    public const string HolyGrail = """
        <!doctype html><html><head><title>Gazette — Front page</title>
        <style>.lead{display:flex}.lead-main{flex:2}.lead-side{flex:1}.digest{display:grid;grid-template-columns:repeat(3,minmax(0,1fr))}</style>
        </head><body>
        <header><h1>Gazette</h1><p>All the news that fits in 80 columns.</p></header>
        <nav><a href="https://example.com/">World</a><a href="https://example.com/tech">Tech</a><a href="https://example.com/sport">Sport</a></nav>
        <main>
          <p>Morning edition. Three stories lead today, digest below.</p>
          <div class="lead">
            <div class="lead-main"><h2>Harbour bridge reopens</h2><p>After eighteen months of repairs the old harbour bridge reopened to cheering crowds and a flotilla of small boats.</p><p><a href="https://example.com/bridge">Full story</a></p></div>
            <div class="lead-side"><h2>Markets rally</h2><p>Widget futures hit a record high for the third session running.</p></div>
          </div>
          <div class="digest">
            <div><h3>Weather</h3><p>Sun, then slightly different sun. Highs near the usual.</p></div>
            <div><h3>Transit</h3><p>Night buses replace trains north of Mill Valley all weekend.</p></div>
            <div><h3>Culture</h3><p>The terminal arts festival opens Friday with 40 performances.</p></div>
          </div>
        </main>
        <aside><h3>Most read</h3><p>1. Bridge reopens. 2. Markets rally. 3. New widget day announced.</p></aside>
        <footer><p>© 2026 Gazette · <a href="https://example.com/about">About</a></p></footer>
        </body></html>
        """;

    public const string Tailwindish = """
        <!doctype html><html><head><title>Shop — Tailwind demo</title></head><body>
        <nav><a href="https://example.com/">Store</a><a href="https://example.com/cart">Cart</a></nav>
        <main>
          <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div class="rounded shadow"><h3>Hammer</h3><p>Forged steel, hickory handle. Swings true for decades of honest work.</p><p><a href="https://example.com/hammer">$24</a></p></div>
            <div class="rounded shadow"><h3>Saw</h3><p>Japanese pull saw with replaceable blades for dovetails and despair.</p><p><a href="https://example.com/saw">$39</a></p></div>
            <div class="rounded shadow"><h3>Square</h3><p>Machinist square accurate to a hair split four ways. Trust it.</p><p><a href="https://example.com/square">$31</a></p></div>
          </div>
          <div class="flex flex-col md:flex-row">
            <div class="md:w-2/3"><h2>Workshop notes</h2><p>Keep cutting tools dry, edges honed, and coffee within reach at all times.</p></div>
            <div class="md:w-1/3"><h2>Shipping</h2><p>Free over $50. Owls available in rural zones.</p></div>
          </div>
        </main>
        </body></html>
        """;

    public const string Search = """
        <!doctype html><html><head><title>Archive search</title></head><body>
        <header><h1>The Archive</h1><p>Every manual ever printed, searchable.</p></header>
        <nav><a href="https://example.com/">Home</a><a href="https://example.com/browse">Browse</a></nav>
        <main>
          <form action="https://example.com/search" method="get">
            <label for="q">Search the archive</label>
            <input type="search" id="q" name="q" placeholder="try: widget maintenance">
            <label>Format
              <select name="fmt">
                <option value="any">Anything</option>
                <option value="pdf" selected>PDF manual</option>
                <option value="txt">Plain text</option>
              </select>
            </label>
            <label><input type="checkbox" name="ocr" value="1" checked> Include scanned pages</label>
            <input type="hidden" name="lang" value="en">
            <input type="submit" value="Search">
          </form>
          <form action="https://example.com/feedback" method="post">
            <legend>Request a manual</legend>
            <label for="who">Your name</label>
            <input type="text" id="who" name="who">
            <label for="what">Which manual?</label>
            <textarea id="what" name="what" placeholder="Title, year, anything you remember"></textarea>
            <label>Urgency</label>
            <input type="radio" name="urg" value="low"> Low
            <input type="radio" name="urg" value="high" checked> High
            <input type="submit" name="go" value="Send request">
            <input type="reset" value="Clear">
          </form>
        </main>
        <footer><p>© 2026 The Archive demo</p></footer>
        </body></html>
        """;
}
