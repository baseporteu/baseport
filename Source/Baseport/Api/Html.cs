using System.Text;

namespace Baseport;

// Builds the HTML fragments the console swaps into the page.
public static class Html
{
    // Escapes a value for use as element text or an attribute value.
    public static string Text(object? value)
    {
        var raw = value switch
        {
            null => "",
            string s => s,
            // No trailing Z: callers convert to local time first, and "u" would label that UTC.
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
            _ => value.ToString() ?? ""
        };

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // A table row. Cells are raw HTML, so each must already be escaped.
    public static string Row(params string[] cells) => $"<tr>{string.Concat(cells)}</tr>";

    // A cell whose content is escaped for you.
    public static string Cell(object? value, string? className = null) =>
        $"<td{Class(className)}>{Text(value)}</td>";

    // A cell whose content is already-built HTML.
    public static string RawCell(string html, string? className = null) =>
        $"<td{Class(className)}>{html}</td>";

    public static string Muted(object? value) => $"<span class=\"muted\">{Text(value)}</span>";

    // invariant text is the fallback if js never runs; ui.fragment() reformats data-n with the browser's locale
    public static string Num(double value, int decimals = 0)
    {
        var s = value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
        return $"<span class=\"num\" data-n=\"{s}\">{s}</span>";
    }

    public static string BytesHtml(double bytes)
    {
        var (v, unit) = bytes switch
        {
            < 1024 => (bytes, "B"),
            < 1024 * 1024 => (bytes / 1024, "KB"),
            _ => (bytes / 1024 / 1024, "MB"),
        };
        return $"{Num(v, unit == "B" ? 0 : 1)} {unit}";
    }

    public static string Tag(object? value) => $"<span class=\"tag\">{Text(value)}</span>";

    public static string Badge(object? value, string? extra = null) =>
        $"<span class=\"badge{(extra is null ? "" : " " + extra)}\">{Text(value)}</span>";

    // A button that calls a console function with string arguments.
    public static string Button(string label, string function, params string[] args)
    {
        var call = $"{function}({string.Join(", ", args.Select(a => $"'{JsString(a)}'"))})";
        return $"<button class=\"btn btn-ghost btn-sm\" onclick=\"{Text(call)}\">{Text(label)}</button>";
    }

    public static string IconButton(string icon, string title, string function, params string[] args)
    {
        var call = $"{function}({string.Join(", ", args.Select(a => $"'{JsString(a)}'"))})";
        return $"<button class=\"icon-btn\" title=\"{Text(title)}\" onclick=\"{Text(call)}\">{icon}</button>";
    }

    // Escapes a value for a single-quoted JavaScript string literal.
    public static string JsString(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "\\n");

    // Relaxed here only: every caller passes the result to Text(), which does the HTML escaping.
    private static readonly System.Text.Json.JsonSerializerOptions DisplayJson =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Renders a value the way the grid should show it.
    public static string DisplayValue(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return "";
        if (node is System.Text.Json.Nodes.JsonArray arr)
            return string.Join(", ", arr.Select(DisplayValue));
        if (node is System.Text.Json.Nodes.JsonObject) return node.ToJsonString(DisplayJson);
        return node.ToString();
    }

    // A confirmation dialog names the row it is about, and a longtext value would push the buttons off the sheet.
    public static string Shorten(string value, int max = 60) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "\u2026";

    // The default encoder escapes <, > and & as \u003C and friends, which is what stops a value in the payload from closing the script element it sits in.
    private static readonly System.Text.Json.JsonSerializerOptions BootstrapJson =
        new(System.Text.Json.JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default };

    // A JSON script block, not a JS literal: the browser parses it as data, so nothing in it executes even if it reaches the DOM. Every server-rendered page that seeds its first paint uses this one.
    public static string BootstrapScript(object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload, BootstrapJson);
        // Belt and braces.
        if (json.Contains('<')) json = json.Replace("<", "\\u003C", StringComparison.Ordinal);
        return $"\n<script type=\"application/json\" id=\"bootstrap\">{json}</script>\n";
    }

    private static string Class(string? className) =>
        className is null ? "" : $" class=\"{Text(className)}\"";
}
