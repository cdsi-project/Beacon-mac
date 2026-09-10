using System.Diagnostics;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Fingerprints;

namespace CDSI.Agent.Application.Fingerprints;

public sealed class FingerprintApplicationService
{
    private const int PageSize = 128;

    private readonly IFileFingerprintService _fingerprintService;
    private readonly IAssetRepository _repository;

    public FingerprintApplicationService(
        IFileFingerprintService fingerprintService,
        IAssetRepository repository)
    {
        _fingerprintService = fingerprintService;
        _repository = repository;
    }

    public async Task<FingerprintSummary> ProcessPendingAsync(
        FingerprintMode mode,
        IProgress<FingerprintProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var work = await _repository.GetFingerprintWorkSummaryAsync(
            mode,
            cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var completedFiles = 0;
        var fingerprintedFiles = 0;
        var errors = 0;
        var settledBytes = 0L;
        Guid? afterAssetId = null;

        void Report(
            string? currentPath,
            long currentFileBytes = 0,
            string? message = null)
        {
            var processedBytes = Math.Min(work.Bytes, settledBytes + currentFileBytes);
            var elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            progress?.Report(new FingerprintProgress(
                mode,
                work.Files,
                completedFiles,
                fingerprintedFiles,
                errors,
                work.Bytes,
                processedBytes,
                processedBytes / elapsedSeconds,
                currentPath,
                message));
        }

        Report(null);

        try
        {
            while (true)
            {
                var candidates = await _repository.ListFingerprintCandidatesAsync(
                    mode,
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
                    var currentFileBytes = 0L;
                    Report(candidate.File.FullPath);

                    try
                    {
                        var fingerprint = await _fingerprintService.CalculateAsync(
                            candidate.File,
                            fileProgress =>
                            {
                                currentFileBytes = fileProgress.BytesProcessed;
                                Report(candidate.File.FullPath, currentFileBytes);
                            },
                            cancellationToken);
                        var saved = await _repository.SaveSha256Async(
                            candidate.AssetId,
                            fingerprint.Size,
                            fingerprint.ModifiedAt,
                            fingerprint.Sha256,
                            cancellationToken);

                        if (saved)
                        {
                            fingerprintedFiles++;
                        }
                        else
                        {
                            errors++;
                            Report(
                                candidate.File.FullPath,
                                currentFileBytes,
                                "File metadata changed before its fingerprint could be saved.");
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        errors++;
                        Report(
                            candidate.File.FullPath,
                            currentFileBytes,
                            exception.Message);
                    }

                    completedFiles++;
                    settledBytes += candidate.File.Size;
                    afterAssetId = candidate.AssetId;
                    Report(candidate.File.FullPath);
                }
            }

            Report(null);
            return new FingerprintSummary(
                mode,
                work.Files,
                fingerprintedFiles,
                errors,
                settledBytes,
                Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            Report(null, message: "Fingerprinting cancelled.");
            return new FingerprintSummary(
                mode,
                work.Files,
                fingerprintedFiles,
                errors,
                settledBytes,
                Cancelled: true);
        }
    }
}
