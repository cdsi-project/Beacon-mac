using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Infrastructure.Metadata;

public sealed class TagLibMetadataExtractor : IAssetMetadataExtractor
{
    private const int MaximumTextLength = 512;

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".aac", ".aiff", ".ape", ".avi", ".bmp", ".dng", ".dsf",
            ".flac", ".gif", ".jpeg", ".jpg", ".m4a", ".m4b", ".m4v",
            ".mkv", ".mp3", ".mp4", ".mpeg", ".mpg", ".oga", ".ogg",
            ".ogv", ".png", ".svg", ".tif", ".tiff", ".wav", ".webm",
            ".wma", ".wmv", ".wv"
        };

    public string Name => "taglib";

    public bool Supports(DiscoveredFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return SupportedExtensions.Contains(file.Extension);
    }

    public Task<MetadataExtractionResult> ExtractAsync(
        DiscoveredFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();

        using var mediaFile = TagLib.File.Create(file.FullPath, TagLib.ReadStyle.Average);
        cancellationToken.ThrowIfCancellationRequested();

        var properties = mediaFile.Properties;
        var kind = GetMediaKind(file, properties.MediaTypes);
        var width = kind switch
        {
            AssetMediaKind.Image => PositiveOrNull(properties.PhotoWidth),
            AssetMediaKind.Video => PositiveOrNull(properties.VideoWidth),
            _ => null
        };
        var height = kind switch
        {
            AssetMediaKind.Image => PositiveOrNull(properties.PhotoHeight),
            AssetMediaKind.Video => PositiveOrNull(properties.VideoHeight),
            _ => null
        };
        long? durationMilliseconds = properties.Duration > TimeSpan.Zero
            ? Convert.ToInt64(properties.Duration.TotalMilliseconds)
            : null;

        var content = new AssetMetadataContent(
            kind,
            width,
            height,
            durationMilliseconds,
            GetCodecDescription(properties.Codecs, TagLib.MediaTypes.Video),
            null,
            GetCodecDescription(properties.Codecs, TagLib.MediaTypes.Audio),
            PositiveOrNull(properties.AudioBitrate),
            PositiveOrNull(properties.AudioSampleRate),
            PositiveOrNull(properties.AudioChannels),
            Normalize(mediaFile.Tag.Title),
            Normalize(JoinValues(mediaFile.Tag.Performers)),
            Normalize(mediaFile.Tag.Album));

        return Task.FromResult(new MetadataExtractionResult(
            MetadataExtractionStatus.Extracted,
            content));
    }

    private static AssetMediaKind GetMediaKind(
        DiscoveredFile file,
        TagLib.MediaTypes mediaTypes)
    {
        if ((mediaTypes & TagLib.MediaTypes.Video) != 0 ||
            file.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return AssetMediaKind.Video;
        }

        if ((mediaTypes & TagLib.MediaTypes.Audio) != 0 ||
            file.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return AssetMediaKind.Audio;
        }

        return AssetMediaKind.Image;
    }

    private static int? PositiveOrNull(int value)
    {
        return value > 0 ? value : null;
    }

    private static string? GetCodecDescription(
        IEnumerable<TagLib.ICodec> codecs,
        TagLib.MediaTypes mediaType)
    {
        var descriptions = codecs
            .Where(codec => (codec.MediaTypes & mediaType) != 0)
            .Select(codec => codec.Description)
            .Where(description => !string.IsNullOrWhiteSpace(description))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);
        return Normalize(string.Join(", ", descriptions));
    }

    private static string? JoinValues(IEnumerable<string> values)
    {
        var selected = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(8);
        return string.Join(", ", selected);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= MaximumTextLength
            ? trimmed
            : trimmed[..MaximumTextLength];
    }
}
