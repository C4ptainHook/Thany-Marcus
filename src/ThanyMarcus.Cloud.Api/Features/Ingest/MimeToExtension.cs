namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static class MimeToExtension
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"]       = "png",
        ["image/jpeg"]      = "jpg",
        ["image/jpg"]       = "jpg",
        ["image/gif"]       = "gif",
        ["image/webp"]      = "webp",
        ["image/heic"]      = "heic",
        ["image/heif"]      = "heif",
        ["image/bmp"]       = "bmp",
        ["image/tiff"]      = "tiff",
        ["image/svg+xml"]   = "svg",

        ["audio/wav"]       = "wav",
        ["audio/x-wav"]     = "wav",
        ["audio/wave"]      = "wav",
        ["audio/mpeg"]      = "mp3",
        ["audio/mp3"]       = "mp3",
        ["audio/m4a"]       = "m4a",
        ["audio/mp4"]       = "m4a",
        ["audio/x-m4a"]     = "m4a",
        ["audio/aac"]       = "aac",
        ["audio/ogg"]       = "ogg",
        ["audio/opus"]      = "opus",
        ["audio/flac"]      = "flac",
        ["audio/webm"]      = "webm",

        ["application/pdf"]               = "pdf",
        ["text/plain"]                    = "txt",
        ["text/markdown"]                 = "md",
        ["application/json"]              = "json",
        ["text/csv"]                      = "csv",
        ["application/zip"]               = "zip",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/msword"]            = "doc",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"]       = "xlsx",
        ["application/octet-stream"]      = "bin",
    };

    public static string Resolve(string? mimeType) =>
        mimeType is not null && Map.TryGetValue(mimeType, out var ext) ? ext : "bin";
}
