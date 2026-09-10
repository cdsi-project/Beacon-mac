using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Mac.Services;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Services;

public sealed class BackgroundServiceCoordinatorTests
{
    [Fact]
    public void GetDueIdleScanRoots_UsesTheLaterScanOrConfigurationTime()
    {
        var now = DateTimeOffset.Parse("2026-09-10T12:00:00+08:00");
        var exactlyDue = CreateRoot(
            now,
            updatedAt: now.AddMinutes(-30),
            lastScannedAt: now.AddMinutes(-10));
        var neverScannedAndDue = CreateRoot(
            now,
            updatedAt: now.AddMinutes(-11)) with
        {
            LastScannedAt = null
        };
        var recentlyScanned = CreateRoot(
            now,
            updatedAt: now.AddMinutes(-30),
            lastScannedAt: now.AddMinutes(-5));
        var recentlyConfigured = CreateRoot(
            now,
            updatedAt: now.AddMinutes(-5),
            lastScannedAt: now.AddMinutes(-30));

        var result = BackgroundServiceCoordinator.GetDueIdleScanRoots(
            [exactlyDue, neverScannedAndDue, recentlyScanned, recentlyConfigured],
            now);

        Assert.Equal(
            [exactlyDue.Id, neverScannedAndDue.Id],
            result.Select(root => root.Id));
    }

    [Fact]
    public void GetDueIdleScanRoots_RequiresAnEnabledReadonlyScheduledRootThatIsNotOffline()
    {
        var now = DateTimeOffset.Parse("2026-09-10T12:00:00+08:00");
        var due = CreateRoot(now);
        var managed = CreateRoot(now) with { Mode = ScanRootMode.Managed };
        var disabled = CreateRoot(now) with { Enabled = false };
        var offline = CreateRoot(now) with { Status = ScanRootStatus.Offline };
        var unscheduled = CreateRoot(now) with
        {
            IdleSchedule = IdleScanSchedule.Disabled
        };

        var result = BackgroundServiceCoordinator.GetDueIdleScanRoots(
            [managed, disabled, offline, unscheduled, due],
            now);

        Assert.Equal(due.Id, Assert.Single(result).Id);
    }

    [Fact]
    public void GetDueIdleScanRoots_RejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BackgroundServiceCoordinator.GetDueIdleScanRoots(null!, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task RunIdleScanCheckAsync_LogsScanRootListingFailures()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var runtimeLog = new RuntimeLogService(testRoot);
            var exception = await Record.ExceptionAsync(() =>
                BackgroundServiceCoordinator.RunIdleScanCheckAsync(
                    () => Task.FromException<IReadOnlyList<ScanRoot>>(
                        new InvalidOperationException("scan roots unavailable")),
                    _ => Task.CompletedTask,
                    () => false,
                    runtimeLog,
                    DateTimeOffset.UtcNow));

            Assert.Null(exception);
            Assert.Contains("检查空闲扫描计划失败", runtimeLog.ReadRecent());
            Assert.Contains("scan roots unavailable", runtimeLog.ReadRecent());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunIdleScanCheckAsync_AllowsAnotherCheckAfterScanFailure()
    {
        var now = DateTimeOffset.Parse("2026-09-10T12:00:00+08:00");
        var due = CreateRoot(now);
        var testRoot = CreateTestDirectory();
        try
        {
            var runtimeLog = new RuntimeLogService(testRoot);
            var scanAttempts = 0;
            IReadOnlyCollection<Guid>? lastScanRootIds = null;
            Task RunScanAsync(IReadOnlyCollection<Guid> scanRootIds)
            {
                lastScanRootIds = scanRootIds;
                scanAttempts++;
                return scanAttempts == 1
                    ? Task.FromException(new InvalidOperationException("idle scan failed"))
                    : Task.CompletedTask;
            }

            await BackgroundServiceCoordinator.RunIdleScanCheckAsync(
                () => Task.FromResult<IReadOnlyList<ScanRoot>>([due]),
                RunScanAsync,
                () => false,
                runtimeLog,
                now);
            await BackgroundServiceCoordinator.RunIdleScanCheckAsync(
                () => Task.FromResult<IReadOnlyList<ScanRoot>>([due]),
                RunScanAsync,
                () => false,
                runtimeLog,
                now);

            Assert.Equal(2, scanAttempts);
            Assert.Equal(due.Id, Assert.Single(lastScanRootIds!));
            Assert.Contains("检查空闲扫描计划失败", runtimeLog.ReadRecent());
            Assert.Contains("idle scan failed", runtimeLog.ReadRecent());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static ScanRoot CreateRoot(
        DateTimeOffset now,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? lastScannedAt = null)
    {
        var configuredAt = updatedAt ?? now.AddMinutes(-20);
        return new ScanRoot(
            Guid.NewGuid(),
            Path.Combine("/Volumes/Test", Guid.NewGuid().ToString("N")),
            ScanRootMode.Readonly,
            true,
            ScanRootStatus.Active,
            configuredAt.AddDays(-1),
            configuredAt,
            lastScannedAt ?? now.AddMinutes(-20),
            null,
            IdleSchedule: new IdleScanSchedule(
                true,
                10,
                IdleScanIntervalUnit.Minutes));
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "cdsi-background-service-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
