using System.Diagnostics;
using System.Text.Json;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Reader;

namespace CDSI.Agent.Application.Reader;

public sealed class ReaderApplicationService
{
    private const long MaximumOpmlBytes = 10 * 1024 * 1024;
    private const long MaximumSnapshotBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IReaderRepository _repository;
    private readonly IReaderFeedClient _feedClient;
    private readonly IReaderSubscriptionExchange _subscriptionExchange;

    public ReaderApplicationService(
        IReaderRepository repository,
        IReaderFeedClient feedClient,
        IReaderSubscriptionExchange subscriptionExchange)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _feedClient = feedClient ?? throw new ArgumentNullException(nameof(feedClient));
        _subscriptionExchange = subscriptionExchange ??
            throw new ArgumentNullException(nameof(subscriptionExchange));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return _repository.InitializeAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ReaderFeedSummary>> ListFeedsAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.ListFeedsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ReaderEntryListItem>> ListEntriesAsync(
        ReaderEntryQuery query,
        CancellationToken cancellationToken = default)
    {
        return _repository.ListEntriesAsync(query, cancellationToken);
    }

    public Task<ReaderEntryListItem?> GetEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        return _repository.GetEntryAsync(entryId, cancellationToken);
    }

    public async Task<ReaderFeed> SubscribeAsync(
        ReaderSubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = ReaderUrl.ParseAndNormalize(request.FeedUrl);
        if (await _repository.FindFeedByUrlAsync(normalized.AbsoluteUri, cancellationToken) is not null)
        {
            throw new InvalidOperationException("该 Feed 已经订阅。");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var result = await _feedClient.FetchAsync(
            new ReaderFeedFetchRequest(
                normalized,
                null,
                null,
                request.AllowPrivateNetwork),
            cancellationToken);
        var parsed = result.ParsedFeed ??
            throw new InvalidDataException("Feed 没有返回可解析内容。");
        if (await _repository.FindFeedByUrlAsync(result.FinalUri.AbsoluteUri, cancellationToken) is not null)
        {
            throw new InvalidOperationException("该 Feed 的最终地址已经订阅。");
        }

        var now = DateTimeOffset.UtcNow;
        var feedId = Guid.NewGuid();
        var entries = CreateEntries(feedId, parsed.Entries, now);
        var feed = new ReaderFeed(
            feedId,
            string.IsNullOrWhiteSpace(request.PreferredTitle)
                ? parsed.Title
                : request.PreferredTitle.Trim(),
            result.FinalUri.AbsoluteUri,
            parsed.SiteUrl,
            parsed.Description,
            parsed.Type,
            NormalizeOptional(request.FolderName),
            result.ETag,
            result.LastModified,
            now,
            now,
            LatestEntryAt(entries),
            now,
            true,
            request.AllowPrivateNetwork,
            null);
        await _repository.SaveFetchedFeedAsync(
            feed,
            entries,
            CreateLog(
                feed.Id,
                startedAt,
                now,
                result,
                stopwatch.ElapsedMilliseconds,
                "Success"),
            cancellationToken);
        return feed;
    }

    public async Task<ReaderFeedRefreshResult> RefreshFeedAsync(
        Guid feedId,
        CancellationToken cancellationToken = default)
    {
        var feed = await _repository.GetFeedAsync(feedId, cancellationToken) ??
            throw new InvalidOperationException("订阅源不存在或已被移除。");
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _feedClient.FetchAsync(
                new ReaderFeedFetchRequest(
                    ReaderUrl.ParseAndNormalize(feed.FeedUrl),
                    feed.ETag,
                    feed.LastModified,
                    feed.AllowPrivateNetwork),
                cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (result.NotModified)
            {
                var unchanged = feed with
                {
                    ETag = result.ETag,
                    LastModified = result.LastModified,
                    LastCheckedAt = now,
                    LastSuccessAt = now,
                    LastError = null
                };
                await _repository.SaveFetchedFeedAsync(
                    unchanged,
                    [],
                    CreateLog(
                        feed.Id,
                        startedAt,
                        now,
                        result,
                        stopwatch.ElapsedMilliseconds,
                        "NotModified"),
                    cancellationToken);
                return new ReaderFeedRefreshResult(unchanged, true, 0);
            }

            var parsed = result.ParsedFeed ??
                throw new InvalidDataException("Feed 没有返回可解析内容。");
            var entries = CreateEntries(feed.Id, parsed.Entries, now);
            var latestEntryAt = LatestEntryAt(entries);
            var updated = feed with
            {
                Title = feed.Title,
                FeedUrl = result.FinalUri.AbsoluteUri,
                SiteUrl = parsed.SiteUrl ?? feed.SiteUrl,
                Description = parsed.Description,
                Type = parsed.Type,
                ETag = result.ETag,
                LastModified = result.LastModified,
                LastCheckedAt = now,
                LastSuccessAt = now,
                LastEntryAt = Max(feed.LastEntryAt, latestEntryAt),
                LastError = null
            };
            var newEntries = await _repository.SaveFetchedFeedAsync(
                updated,
                entries,
                CreateLog(
                    feed.Id,
                    startedAt,
                    now,
                    result,
                    stopwatch.ElapsedMilliseconds,
                    "Success"),
                cancellationToken);
            return new ReaderFeedRefreshResult(updated, false, newEntries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failedAt = DateTimeOffset.UtcNow;
            await _repository.SaveFetchFailureAsync(
                feed.Id,
                failedAt,
                exception.Message,
                new ReaderFetchLog(
                    Guid.NewGuid(),
                    feed.Id,
                    startedAt,
                    failedAt,
                    null,
                    "Failed",
                    0,
                    0,
                    stopwatch.ElapsedMilliseconds,
                    exception.Message),
                cancellationToken);
            throw;
        }
    }

    public async Task<ReaderRefreshSummary> RefreshAllAsync(
        IProgress<ReaderRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var feeds = (await _repository.ListFeedsAsync(cancellationToken))
            .Where(item => item.Feed.IsEnabled)
            .Select(item => item.Feed)
            .ToArray();
        var succeeded = 0;
        var notModified = 0;
        var failed = 0;
        var newEntries = 0;
        var errors = new List<string>();
        for (var index = 0; index < feeds.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feed = feeds[index];
            try
            {
                var result = await RefreshFeedAsync(feed.Id, cancellationToken);
                succeeded++;
                notModified += result.NotModified ? 1 : 0;
                newEntries += result.NewEntries;
                progress?.Report(new ReaderRefreshProgress(
                    index + 1,
                    feeds.Length,
                    feed.Title,
                    null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                var error = $"{feed.Title}: {exception.Message}";
                errors.Add(error);
                progress?.Report(new ReaderRefreshProgress(
                    index + 1,
                    feeds.Length,
                    feed.Title,
                    exception.Message));
            }
        }

        return new ReaderRefreshSummary(
            feeds.Length,
            succeeded,
            notModified,
            failed,
            newEntries,
            errors);
    }

    public Task SetEntryReadAsync(
        Guid entryId,
        bool isRead,
        CancellationToken cancellationToken = default)
    {
        return _repository.SetEntryReadAsync(
            entryId,
            isRead,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public Task SetEntryStarredAsync(
        Guid entryId,
        bool isStarred,
        CancellationToken cancellationToken = default)
    {
        return _repository.SetEntryStarredAsync(
            entryId,
            isStarred,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public Task DeleteFeedAsync(
        Guid feedId,
        CancellationToken cancellationToken = default)
    {
        return _repository.DeleteFeedAsync(feedId, cancellationToken);
    }

    public async Task<ReaderOpmlImportResult> ImportOpmlAsync(
        string path,
        IProgress<ReaderRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInputFile(path, MaximumOpmlBytes, "OPML");
        var opml = await File.ReadAllTextAsync(path, cancellationToken);
        var definitions = _subscriptionExchange.Parse(opml);
        var imported = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();
        for (var index = 0; index < definitions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = definitions[index];
            try
            {
                if (await _repository.FindFeedByUrlAsync(definition.FeedUrl, cancellationToken) is not null)
                {
                    skipped++;
                }
                else
                {
                    await SubscribeAsync(
                        new ReaderSubscribeRequest(
                            definition.FeedUrl,
                            definition.Title,
                            definition.FolderName,
                            AllowPrivateNetwork: false),
                        cancellationToken);
                    imported++;
                }

                progress?.Report(new ReaderRefreshProgress(
                    index + 1,
                    definitions.Count,
                    definition.Title ?? definition.FeedUrl,
                    null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                errors.Add($"{definition.Title ?? definition.FeedUrl}: {exception.Message}");
                progress?.Report(new ReaderRefreshProgress(
                    index + 1,
                    definitions.Count,
                    definition.Title ?? definition.FeedUrl,
                    exception.Message));
            }
        }

        return new ReaderOpmlImportResult(imported, skipped, failed, errors);
    }

    public async Task ExportOpmlAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var feeds = (await _repository.ListFeedsAsync(cancellationToken))
            .Select(item => item.Feed)
            .ToArray();
        var opml = _subscriptionExchange.Serialize(feeds);
        await WriteAtomicallyAsync(path, opml, cancellationToken);
    }

    public async Task ExportDataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _repository.CreateSnapshotAsync(cancellationToken);
        var target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    SnapshotJsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<ReaderDataImportResult> ImportDataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ValidateInputFile(path, MaximumSnapshotBytes, "Reader 数据");
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var snapshot = await JsonSerializer.DeserializeAsync<ReaderDataSnapshot>(
            stream,
            SnapshotJsonOptions,
            cancellationToken) ?? throw new InvalidDataException("Reader 数据文件为空或格式无效。");
        if (snapshot.Feeds is null || snapshot.Entries is null)
        {
            throw new InvalidDataException("Reader 数据文件缺少订阅或条目集合。");
        }

        if (snapshot.Feeds.Count > 100_000 || snapshot.Entries.Count > 2_000_000)
        {
            throw new InvalidDataException("Reader 数据文件包含的记录数超过安全限制。");
        }

        return await _repository.ImportSnapshotAsync(snapshot, cancellationToken);
    }

    private static ReaderEntry[] CreateEntries(
        Guid feedId,
        IReadOnlyList<ReaderParsedEntry> parsedEntries,
        DateTimeOffset fetchedAt)
    {
        return parsedEntries
            .Select(entry => new ReaderEntry(
                Guid.NewGuid(),
                feedId,
                ReaderEntryIdentity.CreateKey(entry),
                entry.ExternalId,
                entry.Title,
                NormalizeEntryUrl(entry.Url),
                entry.Author,
                entry.Summary,
                entry.Content,
                entry.PublishedAt,
                entry.UpdatedAt,
                fetchedAt,
                false,
                false,
                null,
                null))
            .GroupBy(entry => entry.DeduplicationKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static ReaderFetchLog CreateLog(
        Guid feedId,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        ReaderFeedFetchResult result,
        long durationMilliseconds,
        string status)
    {
        return new ReaderFetchLog(
            Guid.NewGuid(),
            feedId,
            startedAt,
            finishedAt,
            result.HttpStatus,
            status,
            0,
            result.ResponseBytes,
            durationMilliseconds,
            null);
    }

    private static DateTimeOffset? LatestEntryAt(IEnumerable<ReaderEntry> entries)
    {
        return entries
            .Select(entry => entry.PublishedAt ?? entry.UpdatedAt)
            .Where(value => value is not null)
            .Max();
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left >= right ? left : right;
    }

    private static string? NormalizeEntryUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return ReaderUrl.ParseAndNormalize(value).AbsoluteUri;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void ValidateInputFile(string path, long maximumBytes, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"{label}文件不存在。", path);
        }

        if (file.Length > maximumBytes)
        {
            throw new InvalidDataException($"{label}文件超过大小限制。");
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

public sealed record ReaderSubscribeRequest(
    string FeedUrl,
    string? PreferredTitle = null,
    string? FolderName = null,
    bool AllowPrivateNetwork = false);

public sealed record ReaderFeedRefreshResult(
    ReaderFeed Feed,
    bool NotModified,
    int NewEntries);
