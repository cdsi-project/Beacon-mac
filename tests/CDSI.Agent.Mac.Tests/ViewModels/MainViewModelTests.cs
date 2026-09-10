using CDSI.Agent.Mac.ViewModels;
using Xunit;

namespace CDSI.Agent.Mac.Tests.ViewModels;

public sealed class MainViewModelTests
{
    [Fact]
    public void ResolveSuccessfulOperationStatus_UsesReadyWhenStatusWasNotUpdated()
    {
        var status = MainViewModel.ResolveSuccessfulOperationStatus(
            "正在创建启动数据库快照",
            "正在创建启动数据库快照");

        Assert.Equal("就绪", status);
    }

    [Fact]
    public void ResolveSuccessfulOperationStatus_PreservesOperationResult()
    {
        var status = MainViewModel.ResolveSuccessfulOperationStatus(
            "正在扫描",
            "扫描完成 · 已索引 3");

        Assert.Equal("扫描完成 · 已索引 3", status);
    }

    [Fact]
    public void IsCurrentProjectAssetRefresh_RejectsOlderRequest()
    {
        var projectId = Guid.NewGuid();

        var current = MainViewModel.IsCurrentProjectAssetRefresh(
            refreshVersion: 4,
            currentRefreshVersion: 5,
            requestedProjectId: projectId,
            selectedProjectId: projectId);

        Assert.False(current);
    }

    [Fact]
    public void IsCurrentProjectAssetRefresh_RejectsRequestForPreviousSelection()
    {
        var current = MainViewModel.IsCurrentProjectAssetRefresh(
            refreshVersion: 5,
            currentRefreshVersion: 5,
            requestedProjectId: Guid.NewGuid(),
            selectedProjectId: Guid.NewGuid());

        Assert.False(current);
    }

    [Fact]
    public void IsCurrentProjectAssetRefresh_AcceptsLatestMatchingRequest()
    {
        var projectId = Guid.NewGuid();

        var current = MainViewModel.IsCurrentProjectAssetRefresh(
            refreshVersion: 5,
            currentRefreshVersion: 5,
            requestedProjectId: projectId,
            selectedProjectId: projectId);

        Assert.True(current);
    }
}
