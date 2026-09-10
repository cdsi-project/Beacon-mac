using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Infrastructure.Git;

namespace CDSI.Agent.Infrastructure.Tests.Git;

public sealed class GitCliProjectSynchronizerTests
{
    [Fact]
    public async Task SyncAsync_PushesProjectManifestAndAssetsWithoutDuplicateCommits()
    {
        using var directory = new TestDirectory();
        var remotePath = Path.Combine(directory.Path, "remote.git");
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var sourcePath = Path.Combine(directory.Path, "article.md");
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(sourcePath, "# Beacon\n");
        try
        {
            await RunGitAsync(directory.Path, "init", "--bare", "--initial-branch=main", remotePath);

            var request = CreateRequest(remotePath, workspacePath, sourcePath, "article.md");
            var synchronizer = new GitCliProjectSynchronizer();

            var first = await synchronizer.SyncAsync(request);
            var second = await synchronizer.SyncAsync(request);

            Assert.True(first.CreatedCommit);
            Assert.False(second.CreatedCommit);
            Assert.Equal(first.CommitId, second.CommitId);
            Assert.Equal("# Beacon\n", await ShowFileAsync(remotePath, "main:article.md"));

            var manifestJson = await ShowFileAsync(
                remotePath,
                $"main:{GitProjectSyncConventions.ManifestFileName}");
            using var manifest = JsonDocument.Parse(manifestJson);
            Assert.Equal(
                request.Project.Id,
                manifest.RootElement.GetProperty("projectId").GetGuid());
            Assert.Equal(
                request.Assets[0].AssetId,
                manifest.RootElement
                    .GetProperty("assets")[0]
                    .GetProperty("assetId")
                    .GetGuid());
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(
                workspacePath,
                "Temp",
                "GitSync")));
        }
        finally
        {
            ClearReadOnlyAttributes(remotePath);
        }
    }

    [Fact]
    public async Task SyncAsync_RejectsAssetFilenamesThatEscapeTheRepositoryRoot()
    {
        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "article.md");
        await File.WriteAllTextAsync(sourcePath, "content");
        var request = CreateRequest(
            Path.Combine(directory.Path, "remote.git"),
            Path.Combine(directory.Path, "workspace"),
            sourcePath,
            $"..{Path.DirectorySeparatorChar}article.md");
        var synchronizer = new GitCliProjectSynchronizer();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => synchronizer.SyncAsync(request));

        Assert.Contains("不能同步到 Git 仓库根目录", exception.Message);
    }

    [Fact]
    public async Task PasswordAskPassHelper_IsExecutableAndDoesNotContainSecret_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var sourcePath = Path.Combine(directory.Path, "article.md");
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(sourcePath, "content");
        var original = CreateRequest(
            "https://example.invalid/owner/repository.git",
            workspacePath,
            sourcePath,
            "article.md");
        var request = original with
        {
            Profile = original.Profile with
            {
                RepositoryUrl = "https://example.invalid/owner/repository.git",
                Username = "creator"
            },
            Password = "test-secret"
        };
        var operationDirectory = Path.Combine(directory.Path, "operation");
        Directory.CreateDirectory(operationDirectory);
        var method = typeof(GitCliProjectSynchronizer).GetMethod(
            "CreateGitEnvironmentAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = (Task<IReadOnlyDictionary<string, string?>>)method.Invoke(
            null,
            [request, operationDirectory, CancellationToken.None])!;
        var environment = await task;
        var helperPath = environment["GIT_ASKPASS"]!;
        var helperContent = await File.ReadAllTextAsync(helperPath);

        Assert.Equal("force", environment["GIT_ASKPASS_REQUIRE"]);
        Assert.Equal("test-secret", environment["CDSI_GIT_PASSWORD"]);
        Assert.DoesNotContain("test-secret", helperContent);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(helperPath));
    }

    [Fact]
    public async Task SshPrivateKeyPath_IsQuotedForGitShellCommand()
    {
        using var directory = new TestDirectory();
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var sourcePath = Path.Combine(directory.Path, "article.md");
        var privateKeyPath = Path.Combine(
            directory.Path,
            "creator's key $(touch should-not-run)");
        var publicKeyPath = privateKeyPath + ".pub";
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(sourcePath, "content");
        await File.WriteAllTextAsync(privateKeyPath, "private");
        await File.WriteAllTextAsync(publicKeyPath, "public");
        var original = CreateRequest(
            "git@example.invalid:owner/repository.git",
            workspacePath,
            sourcePath,
            "article.md");
        var request = original with
        {
            Profile = original.Profile with
            {
                AuthenticationMethod = GitAuthenticationMethod.Ssh,
                SshPublicKeyPath = publicKeyPath
            }
        };
        var operationDirectory = Path.Combine(directory.Path, "operation");
        Directory.CreateDirectory(operationDirectory);
        var method = typeof(GitCliProjectSynchronizer).GetMethod(
            "CreateGitEnvironmentAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = (Task<IReadOnlyDictionary<string, string?>>)method.Invoke(
            null,
            [request, operationDirectory, CancellationToken.None])!;
        var environment = await task;

        var normalizedPath = privateKeyPath.Replace('\\', '/');
        var quotedPath = $"'{normalizedPath.Replace("'", "'\\''", StringComparison.Ordinal)}'";
        Assert.Equal(
            $"ssh -i {quotedPath} -o IdentitiesOnly=yes -o BatchMode=yes",
            environment["GIT_SSH_COMMAND"]);
    }

    private static GitProjectSyncRequest CreateRequest(
        string repositoryPath,
        string workspacePath,
        string sourcePath,
        string filename)
    {
        var now = DateTimeOffset.UtcNow;
        var project = new AssetCollection(
            Guid.NewGuid(),
            "文章项目",
            AssetCollectionType.Text,
            now,
            now);
        var profile = new GitProfile(
            Guid.NewGuid(),
            "仓库1",
            GitHostingProvider.Gitee,
            repositoryPath,
            "main",
            GitAuthenticationMethod.Password,
            string.Empty,
            null,
            IsDefault: true,
            now,
            now);
        var info = new FileInfo(sourcePath);
        var asset = new AssetListItem(
            Guid.NewGuid(),
            filename,
            info.Extension,
            "text/markdown",
            info.Length,
            null,
            new DateTimeOffset(info.LastWriteTimeUtc),
            now,
            info.FullName,
            AssetLocationOwnership.External,
            AssetLocationStatus.Available,
            AssetStatus.Indexed,
            HasHealthyObjectStorageBackup: false);
        return new GitProjectSyncRequest(
            profile,
            Password: null,
            workspacePath,
            project,
            [asset]);
    }

    private static async Task<string> ShowFileAsync(
        string repositoryPath,
        string objectName)
    {
        return await RunGitAsync(
            Path.GetDirectoryName(repositoryPath)!,
            $"--git-dir={repositoryPath}",
            "show",
            objectName);
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start Git for the test.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    private static void ClearReadOnlyAttributes(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     rootPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
