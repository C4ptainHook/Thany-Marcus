# CLOUD-URL-NEVER-FAILS — Drop SmartReader, tiered "URL always succeeds" extraction

**Goal:** make URL ingestion a contract that **never fails**. Drop the SmartReader/Readability dependency. Replace with a universal extractor that returns *something useful* for every reachable URL — title + creator + description for social/video URLs via oEmbed, OG metadata for everything else, URL-only as the irreducible fallback. No more "SmartReader could not extract readable content" failure badges. Estimated **1–1.5 person-days** with AI-agent assistance.

## Why this exists

The current `UrlExtractor` uses [SmartReader](https://github.com/Strumenta/SmartReader) (a .NET Readability port). It works on the ~30-40% of URLs that have article-shaped HTML (news, blogs, Wikipedia, docs, GitHub READMEs) and **throws** on the rest:

```
SmartReader could not extract readable content from https://obsidian.md/
```

That exception bubbles up to `Attachment.ExtractionStatus = Failed`, which surfaces in the plugin queue as a scary "**1 failure**" badge on a note that otherwise synthesised correctly. Users get the bad-UX of "the note worked but something looks broken."

The deeper problem is that **Readability is the wrong tool for ~60% of URLs people actually paste in 2026**:

- YouTube, TikTok, Instagram (no `<article>`, JS-heavy)
- Twitter/X (login-walled)
- App marketing pages (SPAs, no semantic content)
- Substack subscriber-only, NYT paywall
- Notion, Google Docs, Figma (auth-walled SPAs)

Crucially, the user's insight (2026-05-31): **"users usually attach URLs they have already seen."** They don't need the system to read the article on their behalf — they read it. The extracted body is for the *system's* benefit (routing, embedding, entity suggestion). That makes content extraction a *nice-to-have*, not a *must-have*. Title + creator + 1-sentence description is already enough for routing and basic embedding signal.

So:
- Today: Readability passes for ~40% of URLs, throws for ~60% → user sees failures.
- This ticket: every reachable URL produces a useful outcome. SmartReader removed; replaced with cheaper, broader-coverage extractors.

## Scope

**In scope:**
- Remove `SmartReader` NuGet package from `ThanyMarcus.Cloud.Api.csproj`.
- Rewrite `UrlExtractor.cs` to a tiered pipeline:
  1. **Reachability probe** — HEAD or short GET to confirm 2xx and grab `Content-Type`.
  2. **oEmbed discovery** — for HTML responses, look for `<link rel="alternate" type="application/json+oembed">` in the head. If present, fetch oEmbed JSON. Universal handler for YouTube, TikTok, Vimeo, SoundCloud, Spotify, Reddit, etc.
  3. **OG / meta-head fallback** — parse `<title>`, `og:title`, `og:description`, `og:image`, `og:site_name` from HTML head. Always works for any HTML response with a `<head>`.
  4. **URL-only outcome** — if even step 1 fails (DNS error, 4xx, 5xx, timeout), still produce a valid result containing just the URL + the reason. **No exception thrown.**
- Drop `Attachment.ExtractionStatus = Failed` as a terminal state for URL kind. URLs only get `Extracted` (with metadata/oembed payload) or `ExtractedMinimal` (URL only, reason recorded but not surfaced as failure). The enum value stays for other kinds (image, voice) where genuine extraction failure is meaningful.
- `SourcesRenderer.RenderUrl` reflects the new outcome shape: friendly platform-aware heading + (when present) caption/description body. Never renders an error message.
- Plugin queue UI: URL extraction outcomes never count toward the "N failures" badge. Other kinds (image, voice) still do.
- Backend tests for: oEmbed-bearing URL, OG-only URL, unreachable URL, malformed URL — all return success outcomes.

**Out of scope:**
- YouTube transcript extraction (via `yt-dlp` or `youtube-transcript-api`). oEmbed already gives title + creator + thumbnail; transcripts are a separate, opt-in enrichment. Land in a follow-up if synth quality on YouTube-heavy notes suggests value.
- TikTok/Instagram dedicated scrapers. oEmbed coverage is enough for v1; if it fails (private accounts, geo-locked), we soft-fail to URL-only.
- Headless browser (Playwright) for SPA rendering. Expensive (CPU + memory), and the metadata-only baseline already gives the system enough signal.
- PDF URL handling. Already covered by docling for explicit file attachments; URL-side PDF detection is a separate ticket.
- Per-domain custom extractors (Wikipedia API, GitHub API, Reddit JSON). Useful enrichments but optional; oEmbed covers the high-traffic platforms cheaply.
- Auto-promotion changes in the plugin (whether URLs in body get auto-attached vs require explicit "+ URL"). Separate UX question, address only if today's auto-promote produces too much noise after this ticket lands.
- Rich preview cards in the synth body. The Sources callout is the only place URL metadata renders for now.

## The contract

| Input | Outcome |
|---|---|
| `https://en.wikipedia.org/wiki/Zettelkasten` (oEmbed-enabled article) | oEmbed title + author + description, status `Extracted` |
| `https://www.youtube.com/watch?v=abc` | oEmbed title + channel + thumbnail, status `Extracted` |
| `https://www.tiktok.com/@user/video/123` (public) | oEmbed title (= user caption) + creator, status `Extracted` |
| `https://obsidian.md/` (no oEmbed, OG tags present) | Page title + OG description, status `Extracted` |
| `https://some.app.example/` (no oEmbed, no OG tags) | Page title only, status `Extracted` |
| `https://login-walled.example/private` (302 to login) | URL + reason "redirect-to-login", status `ExtractedMinimal` |
| `https://dead-domain.example/` (DNS fail) | URL + reason "unreachable: dns", status `ExtractedMinimal` |
| `https://example.com/404` (404 response) | URL + reason "http: 404", status `ExtractedMinimal` |
| Malformed URL (`not-a-url`) | Rejected at attachment-create time (existing validation), never reaches extractor |

**No outcome ever surfaces as a "failure" to the user.** The worst case is "URL captured (no preview available)" — a quieter, factual status.

## Concrete files

### Backend

#### `src/ThanyMarcus.Cloud.Api/ThanyMarcus.Cloud.Api.csproj` — EDIT

Remove:
```xml
<PackageReference Include="SmartReader" />
```

If `ReverseMarkdown` was only used to convert SmartReader's HTML output to Markdown, remove that too. Verify no other code path imports it.

#### `src/ThanyMarcus.Cloud.Api/Infrastructure/Extraction/UrlExtractor.cs` — REWRITE

New signature stays the same so `RealUrlFetcherClient` callers don't change:

```csharp
public async Task<UrlExtractionResult> ExtractAsync(string url, CancellationToken ct)
```

But `ExtractAsync` no longer throws on extraction failure — only on programmer error (e.g., malformed URL passed in). Returned `UrlExtractionResult` gains a `MinimalReason` property that's non-null when only URL-level metadata was captured.

Implementation outline:

```csharp
public async Task<UrlExtractionResult> ExtractAsync(string url, CancellationToken ct)
{
    var uri = ParseOrThrow(url);
    using var client = ConfigureClient();

    HttpResponseMessage? resp;
    try
    {
        resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return UrlExtractionResult.Minimal(uri, reason: $"unreachable: {ShortReason(ex)}");
    }

    if (!resp.IsSuccessStatusCode)
    {
        return UrlExtractionResult.Minimal(uri, reason: $"http: {(int)resp.StatusCode}");
    }

    var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "";
    if (!ct2.Contains("html", StringComparison.OrdinalIgnoreCase))
    {
        // PDF, image, JSON, etc. — out of scope for URL-extract; preserve URL + content-type only.
        return UrlExtractionResult.Minimal(uri, reason: $"content-type: {ct2}");
    }

    var html = await ReadCappedAsync(resp, MaxResponseBytes, ct);
    var head = HeadParser.Parse(html);   // <title>, og:*, link rel=alternate

    // Tier: oEmbed
    if (head.OEmbedHref is { } oembedHref)
    {
        try
        {
            var oembed = await FetchOEmbedAsync(client, oembedHref, ct);
            return UrlExtractionResult.FromOEmbed(uri, head, oembed);
        }
        catch
        {
            // fall through to OG-only — oEmbed is a nice-to-have, not load-bearing
        }
    }

    // Tier: OG + <title>
    return UrlExtractionResult.FromMetaHead(uri, head);
}
```

Drop the SmartReader call entirely. Drop the Markdown-conversion code path; the new path produces no large body, only structured metadata fields.

#### `src/ThanyMarcus.Cloud.Api/Infrastructure/Extraction/HeadParser.cs` — NEW

Lightweight HTML head parser. Avoid pulling HtmlAgilityPack just for this — a regex-based + occasional `AngleSharp` if already present would be sufficient. Returns:

```csharp
public record ParsedHead(
    string? Title,           // <title> or og:title
    string? Description,     // og:description
    string? ImageUrl,        // og:image
    string? SiteName,        // og:site_name
    string? Author,          // article:author or og:author
    string? OEmbedHref);     // <link rel="alternate" type="application/json+oembed" href="...">
```

200-300 LoC max. The head parsing only needs to look at the first ~16 KB of the response — stop reading once `</head>` is seen.

#### `src/ThanyMarcus.Cloud.Api/Infrastructure/Extraction/OEmbedClient.cs` — NEW

Fetches and validates an oEmbed JSON document. Returns:

```csharp
public record OEmbedResponse(
    string? Title,
    string? AuthorName,
    string? AuthorUrl,
    string? ProviderName,    // "YouTube", "TikTok", "Vimeo", ...
    string? ThumbnailUrl,
    string? Type,            // "video", "photo", "rich", "link"
    string? Html);           // ignored for our use; we don't embed iframes in vault notes
```

Cap response to 64 KB. 5-second timeout. Soft-fail (caller handles).

#### `src/ThanyMarcus.Cloud.Api/Infrastructure/Extraction/UrlExtractionResult.cs` — EDIT

```csharp
public record UrlExtractionResult(
    string CanonicalUrl,
    string? Title,
    string? Description,       // OG description or oEmbed title-as-caption fallback
    string? AuthorName,        // oEmbed author_name OR og:author OR null
    string? ProviderName,      // "YouTube", "TikTok", "Wikipedia", or null
    string? ThumbnailUrl,
    int HttpStatus,
    string? MinimalReason)     // non-null = "URL captured, no preview" outcome
{
    public static UrlExtractionResult Minimal(Uri uri, string reason) => new(
        uri.ToString(), Title: null, Description: null, AuthorName: null,
        ProviderName: null, ThumbnailUrl: null, HttpStatus: 0, MinimalReason: reason);

    public static UrlExtractionResult FromMetaHead(Uri uri, ParsedHead head) => /* … */;
    public static UrlExtractionResult FromOEmbed(Uri uri, ParsedHead head, OEmbedResponse oe) => /* … */;
}
```

Drop the previous `Markdown`, `Byline`, `Excerpt`, `PublishedAt`, `Lang`, `Truncated` fields. They were SmartReader-specific and the new pipeline doesn't produce a body.

#### `src/ThanyMarcus.Cloud.Api/Infrastructure/Sidecars/RealUrlFetcherClient.cs` — EDIT

Currently sets `Attachment.ExtractionStatus = Failed` on exceptions. Adjust:

- URL extractor no longer throws — every call returns a `UrlExtractionResult`
- When `MinimalReason` is non-null: `ExtractionStatus = ExtractedMinimal`, `ExtractedText = "(URL captured, no preview available)"`, `Extra = { reason, canonical_url }`
- When `MinimalReason` is null: `ExtractionStatus = Extracted`, `ExtractedText = $"{provider}: {title}{description?}"`, `Extra = { canonical_url, title, author_name, provider_name, thumbnail_url, description }`

#### `src/ThanyMarcus.Cloud.Api/Features/Ingest/AttachmentExtractionStatus.cs` — EDIT

Add the new enum value:

```csharp
public enum AttachmentExtractionStatus
{
    Pending,
    Extracted,
    ExtractedMinimal,   // NEW — captured the URL/file but no meaningful content
    Failed              // STAYS for image/voice/file genuine failures
}
```

Migration: add to the EF enum mapping. Existing rows are unaffected.

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/SourcesRenderer.cs` — EDIT

`RenderUrl` becomes:

```csharp
private static string RenderUrl(Attachment att)
{
    var (title, canonicalUrl, providerName, authorName, description, minimalReason)
        = ReadUrlExtra(att);
    var url = canonicalUrl ?? att.Url ?? string.Empty;

    // Heading: "[Provider] Title by Author" or fall back to the URL itself
    string heading;
    if (!string.IsNullOrWhiteSpace(title))
    {
        var bracket = !string.IsNullOrWhiteSpace(providerName) ? $"[{providerName}] " : "";
        var by      = !string.IsNullOrWhiteSpace(authorName)   ? $" — {authorName}"   : "";
        heading = $"{bracket}{title}{by}";
    }
    else
    {
        heading = url;
    }

    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"> [!source]- URL — [{Escape(heading)}]({url})\n");

    if (!string.IsNullOrWhiteSpace(description))
    {
        AppendCalloutBody(sb, description!);
    }
    else if (!string.IsNullOrWhiteSpace(minimalReason))
    {
        sb.AppendLine($"> *(URL captured, no preview — {minimalReason})*");
    }
    return sb.ToString();
}
```

Drop the previous error-style rendering (`"URL fetch failed: ..."`). It no longer fires.

### Plugin

#### `plugin/thany-marcus/src/queue/QueueSidebar.ts` (or wherever the failure badge is computed) — EDIT

Filter URL extraction outcomes out of the "N failures" count:

```ts
const failureCount = note.attachments.filter(a =>
    a.extractionStatus === "failed"      // only true terminal failures
    // ExtractedMinimal does NOT count
).length;
```

If the count is 0, no badge. If non-zero, the badge text stays.

### Configuration

`appsettings.json` — optionally tighten `Sidecars.Url`:

```json
"Url": {
  "HeadTimeoutSeconds": 5,
  "GetTimeoutSeconds": 10,
  "OEmbedTimeoutSeconds": 5,
  "MaxResponseBytes": 5242880,
  "MaxHeadBytes": 16384
}
```

No new env vars; oEmbed providers are discovered, not configured.

## Tests

### Backend
- `HeadParserTests`: extracts `<title>` from minimal HTML; parses `og:*` block; finds oEmbed `<link rel="alternate">`.
- `OEmbedClientTests`: parses canonical YouTube/TikTok/Vimeo oEmbed responses; rejects oversized payloads; handles non-JSON gracefully.
- `UrlExtractorTests`:
  - oEmbed-discoverable URL → returns `FromOEmbed` shape
  - OG-only URL → returns `FromMetaHead` shape, no provider
  - DNS failure → `Minimal(reason: "unreachable: dns")`, no exception
  - HTTP 404 → `Minimal(reason: "http: 404")`, no exception
  - PDF URL → `Minimal(reason: "content-type: application/pdf")`, no exception
  - Malformed URL → still throws (programmer error)
- `RealUrlFetcherClientTests`: minimal-result path sets `ExtractedMinimal`, not `Failed`; full path sets `Extracted`.
- `SourcesRendererTests`: renders friendly heading with provider + author; renders `(no preview — http: 404)` minimal line when `MinimalReason` set; never emits "URL fetch failed".

### Plugin
- `QueueSidebar.test.ts`: note with one URL attachment in `ExtractedMinimal` shows no failure badge; note with one Image attachment in `Failed` still shows badge.

## Migration / deployment notes

- The `ExtractedMinimal` enum value is additive — no DB migration; the existing `extraction_status` column is `text`, just accepts the new value.
- Plugin needs a rebuild + copy to demo vault for the badge change to take effect.
- No model/sidecar changes. Cloud-api Docker image stays the same shape; just smaller binary footprint after removing SmartReader.
- Reprocessing an existing URL note will re-extract and re-render — old `Failed` status becomes `Extracted` or `ExtractedMinimal` per the new contract.

## Risks / open questions

- **oEmbed coverage gaps.** Some platforms have deprecated their oEmbed endpoints (Twitter/X removed theirs in 2023). Those URLs will fall through to OG-only, which is fine — we still capture title + description. Not a regression vs today (today they'd be `Failed`).
- **Bot detection / 403s.** Sites that block non-browser User-Agents will return 403 instead of HTML. The minimal-reason path covers this (`http: 403`). If too many high-value URLs hit this, add a rotating UA list — defer until observed.
- **oEmbed-as-credential-leak risk.** Some oEmbed responses include thumbnail URLs that themselves require auth (signed CDN URLs). We don't render embedded iframes; thumbnails are just URLs in metadata, not auto-fetched. Safe.
- **Plugin auto-promote of URLs in body.** Today the plugin auto-detects URLs in the draft and offers to attach them. After this ticket lands, even auto-promoted URLs that fail extraction will look fine. But the noise of *many* auto-promoted URL attachments per note is a separate UX concern, addressed in a follow-up if observed.
- **What if the user *actually* wants the article body?** For research-flow notes ("synthesise these 5 articles"), title + description is too thin. This ticket explicitly drops that capability. Add a follow-up "extract article body" tier (Readability layered back in, OR a different lib like trafilatura via a sidecar) if/when a clear use case emerges.

## Done = ?

1. Submit a note with a URL to `https://obsidian.md/` (the failing case from the bug report). The note synthesises successfully, the queue row shows `Ready` with **no failure badge**, the Sources callout reads `> [!source]- URL — [Obsidian](https://obsidian.md/)` with the OG description below it.
2. Submit a note with a YouTube URL. Sources callout reads `> [!source]- URL — [YouTube] "Video Title" — Channel Name` with the description below.
3. Submit a note with a TikTok URL. Sources callout shows `[TikTok] caption-as-title — @creator`.
4. Submit a note with a URL to a domain that returns 404. Queue shows `Ready`, no badge, Sources callout shows `> *(URL captured, no preview — http: 404)*`.
5. Submit a note with a URL to a non-existent domain. Same shape, reason `unreachable: dns`.
6. Submit a note with one URL (any of above) **and** one image with intentionally bad VLM data. Queue row shows "1 failure" badge (from the image, not the URL).
7. `dotnet list package` no longer shows SmartReader. CI green.
