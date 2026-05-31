using System.Text.RegularExpressions;
using System.Web;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public sealed record ParsedHead(
    string? Title,
    string? Description,
    string? ImageUrl,
    string? SiteName,
    string? Author,
    string? OEmbedHref);

public static partial class HeadParser
{
    public static ParsedHead Parse(string html, Uri? baseUri = null)
    {
        var head = ExtractHead(html);

        var titleTag    = ReadTitleTag(head);
        var meta        = ReadMetaTags(head);
        var oembedHref  = ReadOEmbedHref(head);

        var ogTitle     = Get(meta, "og:title") ?? Get(meta, "twitter:title");
        var description = Get(meta, "og:description") ?? Get(meta, "twitter:description") ?? Get(meta, "description");
        var image       = Get(meta, "og:image") ?? Get(meta, "twitter:image");
        var siteName    = Get(meta, "og:site_name") ?? Get(meta, "application-name");
        var author      = Get(meta, "article:author") ?? Get(meta, "author") ?? Get(meta, "og:author");

        return new ParsedHead(
            Title: NullIfBlank(ogTitle) ?? NullIfBlank(titleTag),
            Description: NullIfBlank(description),
            ImageUrl: Resolve(NullIfBlank(image), baseUri),
            SiteName: NullIfBlank(siteName),
            Author: NullIfBlank(author),
            OEmbedHref: Resolve(NullIfBlank(oembedHref), baseUri));
    }

    private static string ExtractHead(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var startIdx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return html.Length > 32 * 1024 ? html[..(32 * 1024)] : html;
        var afterTag = html.IndexOf('>', startIdx);
        if (afterTag < 0) return html;
        var endIdx = html.IndexOf("</head", afterTag, StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0) endIdx = Math.Min(html.Length, afterTag + 32 * 1024);
        return html.Substring(afterTag + 1, endIdx - afterTag - 1);
    }

    private static string? ReadTitleTag(string head)
    {
        var m = TitleRegex().Match(head);
        if (!m.Success) return null;
        return HttpUtility.HtmlDecode(m.Groups[1].Value).Trim();
    }

    private static string? ReadOEmbedHref(string head)
    {
        foreach (Match m in LinkRegex().Matches(head))
        {
            var attrs = ParseAttrs(m.Groups[1].Value);
            if (!attrs.TryGetValue("rel", out var rel)) continue;
            if (!rel.Contains("alternate", StringComparison.OrdinalIgnoreCase)) continue;
            if (!attrs.TryGetValue("type", out var type)) continue;
            if (!type.Contains("oembed", StringComparison.OrdinalIgnoreCase)) continue;
            if (attrs.TryGetValue("href", out var href))
            {
                return HttpUtility.HtmlDecode(href);
            }
        }
        return null;
    }

    private static Dictionary<string, string> ReadMetaTags(string head)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MetaRegex().Matches(head))
        {
            var attrs = ParseAttrs(m.Groups[1].Value);
            string? key = null;
            if (attrs.TryGetValue("property", out var prop) && !string.IsNullOrWhiteSpace(prop)) key = prop;
            else if (attrs.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name)) key = name;
            else if (attrs.TryGetValue("itemprop", out var ip) && !string.IsNullOrWhiteSpace(ip)) key = ip;
            if (key is null) continue;
            if (!attrs.TryGetValue("content", out var content)) continue;
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (!dict.ContainsKey(key))
            {
                dict[key] = HttpUtility.HtmlDecode(content).Trim();
            }
        }
        return dict;
    }

    private static Dictionary<string, string> ParseAttrs(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRegex().Matches(raw))
        {
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Success ? m.Groups[2].Value
                       : m.Groups[3].Success ? m.Groups[3].Value
                       : m.Groups[4].Success ? m.Groups[4].Value
                       : string.Empty;
            if (!dict.ContainsKey(key))
            {
                dict[key] = value;
            }
        }
        return dict;
    }

    private static string? Get(Dictionary<string, string> meta, string key) =>
        meta.TryGetValue(key, out var v) ? v : null;

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Resolve(string? value, Uri? baseUri)
    {
        if (value is null) return null;
        if (baseUri is null) return value;
        if (Uri.TryCreate(baseUri, value, out var abs)) return abs.ToString();
        return value;
    }

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta\b([^>]*?)/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaRegex();

    [GeneratedRegex(@"<link\b([^>]*?)/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"([a-zA-Z_:][\w:.\-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))")]
    private static partial Regex AttrRegex();
}
