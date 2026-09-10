using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Application.Metadata;

public sealed class MetadataExtractionApplicationService
{
    private const int PageSize = 128;
    private const int MaximumErrorLength = 1_024;

    private readonly IReadOnlyList<IAssetMetadataExtractor> _extractors;
    private readonly IAssetRepository _repository;

    public MetadataExtractionApplicationService(
        IEnumerable<IAssetMetadataExtractor> extractors,
        IAssetRepository repository)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors.ToArray();
        if (_extractors.Count == 0)
        {
            throw new ArgumentException(
                "At least one metadata extractor is required.",
                nameof(extractors));
        }

        _repository = repository;
    }

    public async Task<MetadataSummary> ProcessPendingAsync(
        IProgress<MetadataProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var work = await _repository.GetMetadataWorkSummaryAsync(
            MetadataPipeline.CurrentVersion,
            cancellationToken);
        var completedFiles = 0;
        var extractedFiles = 0;
        var unsupportedFiles = 0;
        var errors = 0;
        Guid? afterAssetId = null;

        void Report(string? currentPath, string? message = null)
        {
            progress?.Report(new MetadataProgress(
                work.Files,
                completedFiles,
                extractedFiles,
                unsupportedFiles,
                errors,
                currentPath,
                message));
        }

        Report(null);

        try
        {
            while (true)
            {
                var candidates = await _repository.ListMetadataCandidatesAsync(
                    MetadataPipeline.CurrentVersion,
                    afterAssetId,
                    PageSize,
                    cancellationToken);
                if (candidates.Count == 0)
                {
                    break;
                }

                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Report(candidate.File.FullPath);
                    var extractorName = "registry";

                    try
                    {
                        var extractor = SelectExtractor(candidate.File);
                        extractorName = extractor.Name;
                        EnsureFileUnchanged(candidate.File);
                        var result = await extractor.ExtractAsync(
                            candidate.File,
                            cancellationToken);
                        EnsureFileUnchanged(candidate.File);

                        var metadata = new AssetMetadata(
                            candidate.AssetId,
                            extractor.Name,
                            MetadataPipeline.CurrentVersion,
                            result.Status,
                            candidate.File.Size,
                            candidate.File.ModifiedAt,
                            result.Content,
                            DateTimeOffset.UtcNow,
                            null);
                        var saved = await _repository.SaveMetadataAsync(
                            metadata,
                            cancellationToken);

                        if (!saved)
                        {
                            errors++;
                            Report(
                                candidate.File.FullPath,
                                "File metadata changed before extracted metadata could be saved.");
                        }
                        else if (result.Status == MetadataExtractionStatus.Extracted)
                        {
                            extractedFiles++;
                        }
                        else if (result.Status == MetadataExtractionStatus.Unsupported)
                        {
                            unsupportedFiles++;
                        }
                        else
                        {
                            errors++;
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        errors++;
                        var message = NormalizeError(exception.Message);
                        var failedMetadata = new AssetMetadata(
                            candidate.AssetId,
                            extractorName,
                            MetadataPipeline.CurrentVersion,
                            MetadataExtractionStatus.Error,
                            candidate.File.Size,
                            candidate.File.ModifiedAt,
                            null,
                            DateTimeOffset.UtcNow,
                            message);

                        try
                        {
                            await _repository.SaveMetadataAsync(
                                failedMetadata,
                                cancellationToken);
                        }
                        catch (Exception saveException)
                            when (saveException is not OperationCanceledException)
                        {
                            message = NormalizeError(
                                $"{message} Metadata state could not be saved: {saveException.Message}");
                        }

                        Report(candidate.File.FullPath, message);
                    }

                    completedFiles++;
                    afterAssetId = candidate.AssetId;
                    Report(candidate.File.FullPath);
                }
            }

            Report(null);
            return new MetadataSummary(
                work.Files,
                extractedFiles,
                unsupportedFiles,
                errors,
                Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            Report(null, "Metadata extraction cancelled.");
            return new MetadataSummary(
                work.Files,
                extractedFiles,
                unsupportedFiles,
                errors,
                Cancelled: true);
        }
    }

    private IAssetMetadataExtractor SelectExtractor(DiscoveredFile file)
    {
        return _extractors.FirstOrDefault(extractor => extractor.Supports(file))
            ?? throw new InvalidOperationException(
                $"No metadata extractor supports: {file.FullPath}");
    }

    private static void EnsureFileUnchanged(DiscoveredFile expected)
    {
        var actual = new FileInfo(expected.FullPath);
        actual.Refresh();
        if (!actual.Exists)
        {
            throw new FileNotFoundException(
                "File disappeared before metadata extraction.",
                expected.FullPath);
        }

        if (actual.Length != expected.Size ||
            actual.LastWriteTimeUtc != expected.ModifiedAt.UtcDateTime)
        {
            throw new FileChangedDuringMetadataExtractionException(expected);
        }
    }

    private static string NormalizeError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Metadata extraction failed.";
        }

        var normalized = message.Trim();
        return normalized.Length <= MaximumErrorLength
            ? normalized
            : normalized[..MaximumErrorLength];
    }
}
