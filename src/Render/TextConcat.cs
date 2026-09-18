namespace TexBowser.Render;

/// <summary>
/// Zero-extra-allocation text building for the render path.
///
/// Every method allocates exactly once (the returned string, which is
/// unavoidable) and no intermediate strings. All filling uses
/// <see cref="string.Create{TState}(int, TState, SpanAction{char, TState})"/>
/// whose destination span is heap-backed, so big pages never touch the stack.
///
/// Deliberately NO stackalloc anywhere in this file: a
/// `stackalloc char[n]` buffer combined with the string.Create API would
/// overflow the thread stack (StackOverflow / OOM) on big text such as full
/// article bodies or large data tables. Integer formatting uses
/// <see cref="int.TryFormat(Span{char}, out int)"/> directly into the
/// destination span, so no temporary number strings are created.
/// </summary>
public static class TextConcat
{
    private const char Ellipsis = '…';

    private static int DigitCount(int value)
    {
        if (value <= 0) return 1;
        int n = 0;
        for (int t = value; t > 0; t /= 10) n++;
        return n;
    }

    /// <summary>Fit exactly to width: pad with spaces or truncate. Single alloc.</summary>
    public static string Fit(string? s, int width)
    {
        s ??= "";
        if (s.Length == width) return s;
        if (s.Length > width)
        {
            string src = s;
            return string.Create(width, src, static (span, state) =>
                state.AsSpan(0, span.Length).CopyTo(span));
        }
        string src2 = s;
        return string.Create(width, src2, static (span, state) =>
        {
            state.AsSpan().CopyTo(span);
            span.Slice(state.Length).Fill(' ');
        });
    }

    /// <summary>Truncate with … marker. Returns same ref when no cut (zero alloc).</summary>
    public static string Trunc(string s, int n)
    {
        if (s.Length <= n) return s;
        string src = s;
        return string.Create(n + 1, src, static (span, state) =>
        {
            state.AsSpan(0, span.Length - 1).CopyTo(span);
            span[span.Length - 1] = Ellipsis;
        });
    }

    /// <summary>Clamp a line to width. Returns same ref when it fits.</summary>
    public static string FitLine(string s, int w)
    {
        if (s.Length <= w) return s;
        string src = s;
        return string.Create(w, src, static (span, state) =>
            state.AsSpan(0, span.Length).CopyTo(span));
    }

    /// <summary>Rule line, optionally titled ("── title ──"). Single alloc.</summary>
    public static string Rule(int width, string? title = "")
    {
        if (width < 4) width = 4;
        if (string.IsNullOrEmpty(title))
            return string.Create(width, 0, static (span, _) => span.Fill('─'));
        string t = title;
        // head = "── " + title + " "  (3 + title.Length), then ─ padding.
        int headLen = 3 + t.Length;
        if (headLen >= width)
        {
            return string.Create(width, t, static (span, state) =>
            {
                "── ".AsSpan().CopyTo(span);
                int copy = Math.Min(state.Length, span.Length - 3);
                if (copy > 0) state.AsSpan(0, copy).CopyTo(span.Slice(3));
            });
        }
        return string.Create(width, t, static (span, state) =>
        {
            "── ".AsSpan().CopyTo(span);
            state.AsSpan().CopyTo(span.Slice(3));
            span[3 + state.Length] = ' ';
            span.Slice(3 + state.Length + 1).Fill('─');
        });
    }

    /// <summary>"text [index]" link label. Single alloc, int via TryFormat.</summary>
    public static string LinkLabel(string text, int index)
    {
        int digits = DigitCount(index);
        int total = text.Length + 1 + 1 + digits + 1;
        return string.Create(total, (text, index), static (span, state) =>
        {
            state.text.AsSpan().CopyTo(span);
            int at = state.text.Length;
            span[at++] = ' ';
            span[at++] = '[';
            state.index.TryFormat(span.Slice(at), out int written);
            at += written;
            span[at] = ']';
        });
    }

    /// <summary>"[index] text" variant used by history lines. Single alloc.</summary>
    public static string BracketedIndex(int index, string text)
    {
        int digits = DigitCount(index);
        int total = 1 + digits + 1 + 1 + text.Length;
        return string.Create(total, (text, index), static (span, state) =>
        {
            span[0] = '[';
            state.index.TryFormat(span.Slice(1), out int written);
            int at = 1 + written;
            span[at++] = ']';
            span[at++] = ' ';
            state.text.AsSpan().CopyTo(span.Slice(at));
        });
    }

    /// <summary>"col i/n" stacked fallback header. Single alloc.</summary>
    public static string ColLabel(int i, int count)
    {
        int d1 = DigitCount(i + 1), d2 = DigitCount(count);
        int total = 4 + d1 + 1 + d2;
        return string.Create(total, (i, count), static (span, state) =>
        {
            "col ".AsSpan().CopyTo(span);
            int at = 4;
            (state.i + 1).TryFormat(span.Slice(at), out int w1);
            at += w1;
            span[at++] = '/';
            state.count.TryFormat(span.Slice(at), out _);
        });
    }

    /// <summary>"table · RxC" header. Single alloc.</summary>
    public static string TableTitle(int rows, int cols)
    {
        int d1 = DigitCount(rows), d2 = DigitCount(cols);
        int total = 8 + d1 + 1 + d2;
        return string.Create(total, (rows, cols), static (span, state) =>
        {
            "table · ".AsSpan().CopyTo(span);
            int at = 8;
            state.rows.TryFormat(span.Slice(at), out int w1);
            at += w1;
            span[at++] = '×';
            state.cols.TryFormat(span.Slice(at), out _);
        });
    }

    /// <summary>"[ label ]" submit rendering. Single alloc.</summary>
    public static string BracketSpaced(string label)
    {
        int total = 2 + label.Length + 2;
        return string.Create(total, label, static (span, state) =>
        {
            span[0] = '[';
            span[1] = ' ';
            state.AsSpan().CopyTo(span.Slice(2));
            span[2 + state.Length] = ' ';
            span[2 + state.Length + 1] = ']';
        });
    }

    /// <summary>"[label]" heading fallback. Single alloc.</summary>
    public static string Bracketed(string label)
    {
        int total = label.Length + 2;
        return string.Create(total, label, static (span, state) =>
        {
            span[0] = '[';
            state.AsSpan().CopyTo(span.Slice(1));
            span[1 + state.Length] = ']';
        });
    }

    /// <summary>"base:" prompt (label + colon). Single alloc.</summary>
    public static string WithColon(string label)
    {
        int total = label.Length + 1;
        return string.Create(total, label, static (span, state) =>
        {
            state.AsSpan().CopyTo(span);
            span[state.Length] = ':';
        });
    }

    /// <summary>"(•) text" / "( ) text" radio lines. Single alloc.</summary>
    public static string RadioLine(bool check, string text)
    {
        int total = 4 + text.Length;
        return string.Create(total, (check, text), static (span, state) =>
        {
            span[0] = '(';
            span[1] = state.check ? '•' : ' ';
            span[2] = ')';
            span[3] = ' ';
            state.text.AsSpan().CopyTo(span.Slice(4));
        });
    }

    /// <summary>"[x] text" / "[ ] text" checkbox lines. Single alloc.</summary>
    public static string CheckLine(bool check, string text)
    {
        int total = 4 + text.Length;
        return string.Create(total, (check, text), static (span, state) =>
        {
            span[0] = '[';
            span[1] = state.check ? 'x' : ' ';
            span[2] = ']';
            span[3] = ' ';
            state.text.AsSpan().CopyTo(span.Slice(4));
        });
    }

    /// <summary>"▸ text" / "  text" select-option lines. Single alloc.</summary>
    public static string OptionLine(bool selected, string text)
    {
        int total = 2 + text.Length;
        return string.Create(total, (selected, text), static (span, state) =>
        {
            if (state.selected) { span[0] = '▸'; span[1] = ' '; }
            else { span[0] = ' '; span[1] = ' '; }
            state.text.AsSpan().CopyTo(span.Slice(2));
        });
    }

    /// <summary>Bordered-table border: l + ─segments joined by m + r. Single alloc.</summary>
    public static string Border(string l, string m, string r, int[] widths, int pad = 2)
    {
        long total = (long)l.Length + r.Length;
        for (int i = 0; i < widths.Length; i++) total += widths[i] + pad;
        total += (long)m.Length * Math.Max(0, widths.Length - 1);
        int n = (int)total;
        return string.Create(n, (l, m, r, widths, pad), static (span, state) =>
        {
            int at = 0;
            state.l.AsSpan().CopyTo(span.Slice(at));
            at += state.l.Length;
            for (int i = 0; i < state.widths.Length; i++)
            {
                if (i > 0)
                {
                    state.m.AsSpan().CopyTo(span.Slice(at));
                    at += state.m.Length;
                }
                span.Slice(at, state.widths[i] + state.pad).Fill('─');
                at += state.widths[i] + state.pad;
            }
            state.r.AsSpan().CopyTo(span.Slice(at));
        });
    }

    /// <summary>
    /// One side-by-side grid line: padded cells joined by sep, with trailing
    /// whitespace trimmed exactly like string.TrimEnd() (single alloc, no
    /// per-cell Fit strings, no StringBuilder, no TrimEnd second allocation).
    /// </summary>
    public static string ColumnLine(
        IReadOnlyList<string> cells, IReadOnlyList<int> widths, string sep)
    {
        int cols = cells.Count;
        if (cols == 0) return "";
        // Start offsets of each cell in the full (untrimmed) line.
        int full = 0;
        for (int i = 0; i < cols; i++)
        {
            if (i > 0) full += sep.Length;
            full += widths[i];
        }
        // Walk back over trailing whitespace (cells + seps), mirroring TrimEnd.
        int t = full;
        for (int i = cols - 1; i >= 0; i--)
        {
            string cell = cells[i] ?? "";
            int w = widths[i];
            int take = Math.Min(cell.Length, w);
            // Trailing whitespace inside the cell's taken part (pad + content).
            int j = take - 1;
            while (j >= 0 && char.IsWhiteSpace(cell[j])) j--;
            int contentEnd = j + 1; // trimmed content length within taken part
            int segPad = w - take; // padding spaces after content
            if (contentEnd > 0)
            {
                // Cell contributes content: trim its trailing pad/content spaces.
                t -= segPad + (take - contentEnd);
                break;
            }
            // Whole taken part blank: drop it plus its padding.
            t -= w;
            if (i == 0) break;
            // Drop trailing whitespace of the preceding separator only.
            int k = sep.Length - 1;
            while (k >= 0 && char.IsWhiteSpace(sep[k])) { t--; k--; }
            // If separator had non-space chars (e.g. │), stop; else the
            // all-space separator merges into the blank run — continue loop,
            // which re-examines the previous cell (already handled next iter).
            if (k >= 0) break;
        }
        if (t <= 0) return "";
        int n = t;
        return string.Create(n, (cells, widths, sep), static (span, state) =>
        {
            int at = 0;
            for (int i = 0; i < state.cells.Count; i++)
            {
                if (i > 0)
                {
                    if (at >= span.Length) break;
                    int sl = Math.Min(state.sep.Length, span.Length - at);
                    state.sep.AsSpan(0, sl).CopyTo(span.Slice(at));
                    at += sl;
                }
                if (at >= span.Length) break;
                string cell = state.cells[i] ?? "";
                int w = state.widths[i];
                int room = Math.Min(w, span.Length - at);
                int take = Math.Min(cell.Length, room);
                if (take > 0) cell.AsSpan(0, take).CopyTo(span.Slice(at));
                for (int k = at + take; k < at + room; k++) span[k] = ' ';
                at += room;
            }
        });
    }

    /// <summary>
    /// One bordered-table row line: "│ cell │ cell │" with cell padding kept
    /// (lines always end with │, so no trimming). Single alloc.
    /// Cells are pre-wrapped single lines (no newlines).
    /// </summary>
    public static string TableRowLine(IReadOnlyList<string> cells, int[] widths)
    {
        int cols = cells.Count;
        if (cols == 0) return "";
        long total = 1;
        for (int i = 0; i < cols; i++) total += widths[i] + 3;
        int n = (int)total;
        return string.Create(n, (cells, widths), static (span, state) =>
        {
            int at = 0;
            span[at++] = '│';
            for (int i = 0; i < state.cells.Count; i++)
            {
                span[at++] = ' ';
                string cell = state.cells[i] ?? "";
                int w = state.widths[i];
                int take = Math.Min(cell.Length, w);
                if (take > 0) cell.AsSpan(0, take).CopyTo(span.Slice(at));
                for (int k = at + take; k < at + w; k++) span[k] = ' ';
                at += w;
                span[at++] = ' ';
                span[at++] = '│';
            }
        });
    }

    /// <summary>Join lines with '\n'. Single alloc, no Join intermediates.</summary>
    public static string JoinLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return "";
        if (lines.Count == 1) return lines[0] ?? "";
        long total = lines.Count - 1;
        for (int i = 0; i < lines.Count; i++) total += (lines[i] ?? "").Length;
        int n = (int)total;
        return string.Create(n, lines, static (span, state) =>
        {
            int at = 0;
            for (int i = 0; i < state.Count; i++)
            {
                if (i > 0) span[at++] = '\n';
                string s = state[i] ?? "";
                if (s.Length == 0) continue;
                s.AsSpan().CopyTo(span.Slice(at));
                at += s.Length;
            }
        });
    }

    /// <summary>
    /// Collapse to one line: '\n'/'\r' → space, runs of spaces → one, trim.
    /// Two span passes (measure, fill), single result allocation, no regex,
    /// no Replace loop, no stackalloc (safe for big cells).
    /// </summary>
    public static string OneLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        ReadOnlySpan<char> src = s.AsSpan();
        // Pass 1: measure trimmed/collapsed length.
        int start = 0, end = src.Length;
        while (start < end && IsSpace(src[start])) start++;
        while (end > start && IsSpace(src[end - 1])) end--;
        int outLen = 0;
        bool inSpace = false;
        for (int i = start; i < end; i++)
        {
            char c = src[i];
            if (c == '\n' || c == '\r' || c == ' ')
            {
                if (!inSpace) { outLen++; inSpace = true; }
            }
            else { outLen++; inSpace = false; }
        }
        string source = s;
        int sStart = start, sEnd = end;
        return string.Create(outLen, (source, sStart, sEnd), static (span, state) =>
        {
            ReadOnlySpan<char> r = state.source.AsSpan();
            int at = 0;
            bool sp = false;
            for (int i = state.sStart; i < state.sEnd; i++)
            {
                char c = r[i];
                if (c == '\n' || c == '\r' || c == ' ')
                {
                    if (!sp) { span[at++] = ' '; sp = true; }
                }
                else { span[at++] = c; sp = false; }
            }
        });
    }

    /// <summary>"◈ host · sig · summary · W cols" banner line. Single alloc.</summary>
    public static string HeaderLine(string host, string sig, string summary, int w)
    {
        int dw = DigitCount(w);
        int total = 2 + host.Length + 3 + sig.Length + 3 + summary.Length + 3 + dw + 5;
        return string.Create(total, (host, sig, summary, w), static (span, state) =>
        {
            int at = 0;
            "◈ ".AsSpan().CopyTo(span.Slice(at));
            at += 2;
            state.host.AsSpan().CopyTo(span.Slice(at));
            at += state.host.Length;
            " · ".AsSpan().CopyTo(span.Slice(at));
            at += 3;
            state.sig.AsSpan().CopyTo(span.Slice(at));
            at += state.sig.Length;
            " · ".AsSpan().CopyTo(span.Slice(at));
            at += 3;
            state.summary.AsSpan().CopyTo(span.Slice(at));
            at += state.summary.Length;
            " · ".AsSpan().CopyTo(span.Slice(at));
            at += 3;
            state.w.TryFormat(span.Slice(at), out int written);
            at += written;
            " cols".AsSpan().CopyTo(span.Slice(at));
        });
    }

    /// <summary>
    /// "[index] truncated-text → url" clamped to width. Text truncated to 48
    /// (with …) exactly like before, whole line clamped — one allocation, no
    /// Trunc/interpolation/FitLine intermediates.
    /// </summary>
    public static string LinkIndexLine(int index, string text, string url, int w)
    {
        int digits = DigitCount(index);
        int prefix = 1 + digits + 1 + 1;
        int tLen = Math.Min(text.Length, 48);
        bool cut = text.Length > 48;
        int tPart = tLen + (cut ? 1 : 0);
        int sep = 3; // " → "
        int full = prefix + tPart + sep + url.Length;
        int n = Math.Min(full, w);
        if (n <= 0) return "";
        return string.Create(n, (index, text, url, tLen, cut), static (span, state) =>
        {
            int at = 0;
            span[at++] = '[';
            state.index.TryFormat(span.Slice(at), out int wr);
            at += wr;
            if (at < span.Length) span[at++] = ']';
            if (at < span.Length) span[at++] = ' ';
            if (at < span.Length)
            {
                int take = Math.Min(state.tLen, span.Length - at);
                if (take > 0) state.text.AsSpan(0, take).CopyTo(span.Slice(at));
                at += take;
                if (state.cut && at < span.Length) span[at++] = '…';
            }
            ReadOnlySpan<char> arrow = " → ";
            if (at < span.Length)
            {
                int take = Math.Min(arrow.Length, span.Length - at);
                arrow.Slice(0, take).CopyTo(span.Slice(at));
                at += take;
            }
            if (at < span.Length && state.url.Length > 0)
            {
                int take = Math.Min(state.url.Length, span.Length - at);
                if (take > 0) state.url.AsSpan(0, take).CopyTo(span.Slice(at));
            }
        });
    }

    /// <summary>"form" / "form · title" plus " · METHOD action". Single alloc.</summary>
    public static string FormHead(string title, string method, string action)
    {
        bool hasTitle = title.Length > 0;
        bool hasSuffix = method == "post" || action.Length > 0;
        bool isPost = method == "post";
        int total = 4 + (hasTitle ? 3 + title.Length : 0)
            + (hasSuffix ? 3 + 4 + 1 + action.Length : 0);
        return string.Create(total, (title, action, hasTitle, hasSuffix, isPost), static (span, state) =>
        {
            int at = 0;
            "form".AsSpan().CopyTo(span.Slice(at));
            at += 4;
            if (state.hasTitle)
            {
                " · ".AsSpan().CopyTo(span.Slice(at));
                at += 3;
                state.title.AsSpan().CopyTo(span.Slice(at));
                at += state.title.Length;
            }
            if (state.hasSuffix)
            {
                " · ".AsSpan().CopyTo(span.Slice(at));
                at += 3;
                (state.isPost ? "POST" : "GET").AsSpan().CopyTo(span.Slice(at));
                at += 4;
                span[at++] = ' ';
                state.action.AsSpan().CopyTo(span.Slice(at));
            }
        });
    }

    /// <summary>"label: [file: value]" descriptor. Single alloc.</summary>
    public static string FileLine(string label, string value)
    {
        int total = label.Length + 2 + 7 + value.Length + 1;
        return string.Create(total, (label, value), static (span, state) =>
        {
            int at = 0;
            state.label.AsSpan().CopyTo(span.Slice(at));
            at += state.label.Length;
            ": [file: ".AsSpan().CopyTo(span.Slice(at));
            at += 9;
            state.value.AsSpan().CopyTo(span.Slice(at));
            at += state.value.Length;
            span[at] = ']';
        });
    }

    /// <summary>"label: [kind show]" descriptor. Single alloc.</summary>
    public static string FieldLine(string label, string kind, string show)
    {
        int total = label.Length + 2 + 1 + kind.Length + 1 + show.Length + 1;
        return string.Create(total, (label, kind, show), static (span, state) =>
        {
            int at = 0;
            state.label.AsSpan().CopyTo(span.Slice(at));
            at += state.label.Length;
            span[at++] = ':';
            span[at++] = ' ';
            span[at++] = '[';
            state.kind.AsSpan().CopyTo(span.Slice(at));
            at += state.kind.Length;
            span[at++] = ' ';
            state.show.AsSpan().CopyTo(span.Slice(at));
            at += state.show.Length;
            span[at] = ']';
        });
    }

    /// <summary>Vertical " │ " separator column of height h. Single alloc.</summary>
    public static string VSep(int h)
    {
        if (h <= 0) return "";
        int total = h * 3 + (h - 1);
        return string.Create(total, h, static (span, state) =>
        {
            int at = 0;
            for (int i = 0; i < state; i++)
            {
                if (i > 0) span[at++] = '\n';
                " │ ".AsSpan().CopyTo(span.Slice(at));
                at += 3;
            }
        });
    }

    /// <summary>
    /// HtmlText.Clean equivalent without regex: collapse horizontal runs,
    /// strip single spaces around newlines, cap newline runs at two, drop
    /// spaces before punctuation, trim ends. Two span passes (measure, fill),
    /// single result allocation, no Regex, no Replace loop, no stackalloc.
    /// </summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string src = s;
        int start = 0, end = src.Length;
        while (start < end && char.IsWhiteSpace(src[start])) start++;
        while (end > start && char.IsWhiteSpace(src[end - 1])) end--;
        if (start >= end) return "";
        int len = RunClean(src, start, end, default);
        return string.Create(len, (src, start, end), static (span, st) =>
            RunClean(st.src, st.start, st.end, span));
    }

    private static bool IsPunct(char c) =>
        c == '.' || c == ',' || c == ';' || c == ':' || c == '!' || c == '?';

    private static bool IsHSpace(char c) =>
        c == ' ' || c == '\t' || c == '\f' || c == '\v';

    // Returns characters written (or that would be written for empty dst).
    private static int RunClean(string src, int start, int end, Span<char> dst)
    {
        int at = 0;
        bool pendingSpace = false;
        int nlRun = 0;
        for (int i = start; i < end; i++)
        {
            char c = src[i];
            if (c == '\n')
            {
                pendingSpace = false;
                nlRun++;
                continue;
            }
            if (IsHSpace(c))
            {
                pendingSpace = true;
                continue;
            }
            if (IsPunct(c))
            {
                pendingSpace = false;
                if (nlRun > 0)
                {
                    int k = nlRun <= 2 ? nlRun : 2;
                    for (int q = 0; q < k; q++) { if (at < dst.Length) dst[at] = '\n'; at++; }
                    nlRun = 0;
                }
                if (at < dst.Length) dst[at] = c;
                at++;
                continue;
            }
            if (nlRun > 0)
            {
                int k = nlRun <= 2 ? nlRun : 2;
                for (int q = 0; q < k; q++) { if (at < dst.Length) dst[at] = '\n'; at++; }
                nlRun = 0;
            }
            else if (pendingSpace)
            {
                if (at < dst.Length) dst[at] = ' ';
                at++;
            }
            pendingSpace = false;
            if (at < dst.Length) dst[at] = c;
            at++;
        }
        return at;
    }

    private static bool IsSpace(char c) =>
        c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\f' || c == '\v';
}
