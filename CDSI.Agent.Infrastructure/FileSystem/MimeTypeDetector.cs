namespace CDSI.Agent.Infrastructure.FileSystem;

internal static class MimeTypeDetector
{
    private static readonly IReadOnlyDictionary<string, string> MimeTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".7z"] = "application/x-7z-compressed",
            [".avi"] = "video/x-msvideo",
            [".csv"] = "text/csv",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".gif"] = "image/gif",
            [".gz"] = "application/gzip",
            [".htm"] = "text/html",
            [".html"] = "text/html",
            [".jpeg"] = "image/jpeg",
            [".jpg"] = "image/jpeg",
            [".json"] = "application/json",
            [".m4a"] = "audio/mp4",
            [".md"] = "text/markdown",
            [".mkv"] = "video/x-matroska",
            [".mov"] = "video/quicktime",
            [".mp3"] = "audio/mpeg",
            [".mp4"] = "video/mp4",
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".rar"] = "application/vnd.rar",
            [".rtf"] = "application/rtf",
            [".srt"] = "application/x-subrip",
            [".svg"] = "image/svg+xml",
            [".tar"] = "application/x-tar",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".tsv"] = "text/tab-separated-values",
            [".txt"] = "text/plain",
            [".wav"] = "audio/wav",
            [".webm"] = "video/webm",
            [".webp"] = "image/webp",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".xml"] = "application/xml",
            [".zip"] = "application/zip"
        };

    public static string? Detect(string extension)
    {
        return MimeTypes.GetValueOrDefault(extension);
    }
}
