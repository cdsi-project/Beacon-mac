using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Git;

namespace CDSI.Agent.Infrastructure.Git;

public sealed class GitCliProjectSynchronizer : IGitProjectSynchronizer
{
    private const int CopyBufferSize = 1024 * 1024;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<GitProjectSyncResult> SyncAsync(
        GitProjectSyncRequest request,
        IProgress<GitProjectSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var operationDirectory = CreateOperationDirectory(request.WorkspacePath);
        var repositoryDirectory = Path.Combine(operationDirectory, "repository");
        var environment = await CreateGitEnvironmentAsync(
            request,
            operationDirectory,
            cancellationToken);
        try
        {
            Report(progress, "正在检查 Git", request, 0, 0);
            await RunGitAsync(
                operationDirectory,
                environment,
                ["--version"],
                cancellationToken);

            Report(progress, "正在读取远端仓库", request, 0, 0);
            var repositoryUrl = CreateCommandRepositoryUrl(request);
            await RunGitAsync(
                operationDirectory,
                environment,
                ["clone", "--no-checkout", "--no-tags", repositoryUrl, repositoryDirectory],
                cancellationToken);

            await CheckoutConfiguredBranchAsync(
                repositoryDirectory,
                environment,
                request.Profile.DefaultBranch,
                cancellationToken);

            var existingManifest = await ReadManifestAsync(
                repositoryDirectory,
                cancellationToken);
            ValidateProjectBinding(existingManifest, request);

            var processedFiles = 0;
            long processedBytes = 0;
            var manifestAssets = new List<GitProjectManifestAsset>(request.Assets.Count);
            foreach (var asset in request.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = Path.Combine(
                    repositoryDirectory,
                    asset.OriginalFilename);
                await EnsureDestinationCanBeUpdatedAsync(
                    destinationPath,
                    asset,
                    existingManifest,
                    cancellationToken);
                await CopyAssetAsync(
                    asset,
                    destinationPath,
                    bytesCopied =>
                    {
                        processedBytes += bytesCopied;
                        Report(
                            progress,
                            "正在复制项目文件",
                            request,
                            processedFiles,
                            processedBytes,
                            asset.Path);
                    },
                    cancellationToken);
                processedFiles++;
                manifestAssets.Add(new GitProjectManifestAsset(
                    asset.AssetId,
                    asset.OriginalFilename,
                    asset.Size,
                    asset.Sha256));
                Report(
                    progress,
                    "正在复制项目文件",
                    request,
                    processedFiles,
                    processedBytes,
                    asset.Path);
            }

            var updatedManifest = CreateUpdatedManifest(
                request,
                existingManifest,
                manifestAssets);
            await WriteManifestAsync(
                repositoryDirectory,
                updatedManifest,
                cancellationToken);

            Report(
                progress,
                "正在创建 Git 提交",
                request,
                processedFiles,
                processedBytes);
            var addArguments = new List<string>
            {
                "add",
                "--force",
                "--",
                GitProjectSyncConventions.ManifestFileName
            };
            addArguments.AddRange(request.Assets.Select(asset => asset.OriginalFilename));
            await RunGitAsync(
                repositoryDirectory,
                environment,
                addArguments,
                cancellationToken);

            var diffResult = await RunGitAsync(
                repositoryDirectory,
                environment,
                ["diff", "--cached", "--quiet", "--exit-code"],
                cancellationToken,
                acceptedExitCodes: new HashSet<int> { 0, 1 });
            var createdCommit = diffResult.ExitCode == 1;
            if (createdCommit)
            {
                await RunGitAsync(
                    repositoryDirectory,
                    environment,
                    [
                        "-c", "user.name=CDSI Beacon",
                        "-c", "user.email=beacon@cdsi.local",
                        "commit", "-m", $"chore(beacon): sync {request.Project.Name}"
                    ],
                    cancellationToken);
            }

            Report(
                progress,
                "正在推送到 Git 仓库",
                request,
                processedFiles,
                processedBytes);
            await RunGitAsync(
                repositoryDirectory,
                environment,
                [
                    "push",
                    "origin",
                    $"HEAD:refs/heads/{request.Profile.DefaultBranch}"
                ],
                cancellationToken);
            var commit = await RunGitAsync(
                repositoryDirectory,
                environment,
                ["rev-parse", "HEAD"],
                cancellationToken);

            return new GitProjectSyncResult(
                request.Project.Id,
                request.Profile.Id,
                request.Profile.RepositoryUrl,
                request.Profile.DefaultBranch,
                commit.StandardOutput.Trim(),
                request.Assets.Count,
                request.Assets.Sum(asset => asset.Size),
                createdCommit);
        }
        finally
        {
            TryDeleteOperationDirectory(operationDirectory);
        }
    }

    private static void ValidateRequest(GitProjectSyncRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspacePath);
        if (request.Assets.Count == 0)
        {
            throw new ArgumentException("At least one project asset is required.", nameof(request));
        }

        if (request.Profile.AuthenticationMethod == GitAuthenticationMethod.Password &&
            request.Profile.RepositoryUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(request.Password))
        {
            throw new InvalidOperationException("Git 密码或访问令牌不存在。");
        }

        var duplicate = request.Assets
            .GroupBy(asset => asset.OriginalFilename, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"项目中存在多个同名文件“{duplicate.Key}”，无法同步到 Git。");
        }

        var invalidFilename = request.Assets.FirstOrDefault(asset =>
            !string.Equals(
                Path.GetFileName(asset.OriginalFilename),
                asset.OriginalFilename,
                StringComparison.Ordinal) ||
            string.Equals(asset.OriginalFilename, ".git", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                asset.OriginalFilename,
                GitProjectSyncConventions.ManifestFileName,
                StringComparison.OrdinalIgnoreCase));
        if (invalidFilename is not null)
        {
            throw new InvalidOperationException(
                $"文件名“{invalidFilename.OriginalFilename}”不能同步到 Git 仓库根目录。");
        }
    }

    private static string CreateOperationDirectory(string workspacePath)
    {
        var workspaceRoot = Path.GetFullPath(workspacePath);
        var operationRoot = Path.GetFullPath(Path.Combine(
            workspaceRoot,
            "Temp",
            "GitSync"));
        EnsureDescendantPath(workspaceRoot, operationRoot);
        Directory.CreateDirectory(operationRoot);
        var operationDirectory = Path.Combine(
            operationRoot,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationDirectory);
        return operationDirectory;
    }

    private static async Task<IReadOnlyDictionary<string, string?>> CreateGitEnvironmentAsync(
        GitProjectSyncRequest request,
        string operationDirectory,
        CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "Never"
        };
        if (request.Profile.AuthenticationMethod == GitAuthenticationMethod.Password &&
            request.Profile.RepositoryUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var askPassPath = Path.Combine(
                operationDirectory,
                OperatingSystem.IsWindows()
                    ? "cdsi-git-askpass.cmd"
                    : "cdsi-git-askpass.sh");
            var askPassContent = OperatingSystem.IsWindows()
                ? "@echo off\r\n" +
                  "powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"[Console]::Out.Write($env:CDSI_GIT_PASSWORD)\"\r\n"
                : "#!/bin/sh\nprintf '%s' \"$CDSI_GIT_PASSWORD\"\n";
            await File.WriteAllTextAsync(
                askPassPath,
                askPassContent,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    askPassPath,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }

            environment["GIT_ASKPASS"] = askPassPath;
            environment["GIT_ASKPASS_REQUIRE"] = "force";
            environment["CDSI_GIT_PASSWORD"] = request.Password;
        }
        else if (request.Profile.AuthenticationMethod == GitAuthenticationMethod.Ssh)
        {
            var publicKeyPath = request.Profile.SshPublicKeyPath
                ?? throw new InvalidOperationException("Git SSH 公钥路径不存在。");
            var privateKeyPath = publicKeyPath.EndsWith(
                    ".pub",
                    StringComparison.OrdinalIgnoreCase)
                ? publicKeyPath[..^4]
                : throw new InvalidOperationException("Git SSH 公钥路径无效。");
            if (!File.Exists(privateKeyPath))
            {
                throw new FileNotFoundException("Git SSH 私钥不存在。", privateKeyPath);
            }

            environment["GIT_SSH_COMMAND"] =
                $"ssh -i {QuotePosixShellArgument(privateKeyPath.Replace('\\', '/'))} " +
                "-o IdentitiesOnly=yes -o BatchMode=yes";
        }

        return environment;
    }

    private static string QuotePosixShellArgument(string value)
    {
        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static string CreateCommandRepositoryUrl(GitProjectSyncRequest request)
    {
        if (request.Profile.AuthenticationMethod != GitAuthenticationMethod.Password ||
            !request.Profile.RepositoryUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return request.Profile.RepositoryUrl;
        }

        var builder = new UriBuilder(request.Profile.RepositoryUrl)
        {
            UserName = request.Profile.Username,
            Password = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static async Task CheckoutConfiguredBranchAsync(
        string repositoryDirectory,
        IReadOnlyDictionary<string, string?> environment,
        string branch,
        CancellationToken cancellationToken)
    {
        var remoteRef = $"refs/remotes/origin/{branch}";
        var refResult = await RunGitAsync(
            repositoryDirectory,
            environment,
            ["show-ref", "--verify", "--quiet", remoteRef],
            cancellationToken,
            acceptedExitCodes: new HashSet<int> { 0, 1 });
        if (refResult.ExitCode == 0)
        {
            await RunGitAsync(
                repositoryDirectory,
                environment,
                ["checkout", "-B", branch, remoteRef],
                cancellationToken);
            return;
        }

        await RunGitAsync(
            repositoryDirectory,
            environment,
            ["checkout", "--orphan", branch],
            cancellationToken);
    }

    private static async Task<GitProjectManifest?> ReadManifestAsync(
        string repositoryDirectory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(
            repositoryDirectory,
            GitProjectSyncConventions.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var manifest = await JsonSerializer.DeserializeAsync<GitProjectManifest>(
                stream,
                ManifestJsonOptions,
                cancellationToken);
            if (manifest is null ||
                manifest.SchemaVersion != 1 ||
                manifest.Assets is null)
            {
                throw new InvalidDataException("远端 .cdsi-project.json 版本不受支持。");
            }

            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("远端 .cdsi-project.json 无法解析。", exception);
        }
    }

    private static void ValidateProjectBinding(
        GitProjectManifest? manifest,
        GitProjectSyncRequest request)
    {
        if (manifest is not null && manifest.ProjectId != request.Project.Id)
        {
            throw new InvalidOperationException(
                $"该 Git 仓库已绑定项目“{manifest.ProjectName}”" +
                $"（{manifest.ProjectId:D}），不能与当前项目合并。");
        }
    }

    private static GitProjectManifest CreateUpdatedManifest(
        GitProjectSyncRequest request,
        GitProjectManifest? existingManifest,
        IReadOnlyList<GitProjectManifestAsset> assets)
    {
        var manifestUnchanged = existingManifest is not null &&
            existingManifest.ProjectId == request.Project.Id &&
            string.Equals(
                existingManifest.ProjectName,
                request.Project.Name,
                StringComparison.Ordinal) &&
            string.Equals(
                existingManifest.ProjectType,
                request.Project.Type.ToString(),
                StringComparison.Ordinal) &&
            existingManifest.Assets.SequenceEqual(assets);
        return new GitProjectManifest(
            SchemaVersion: 1,
            request.Project.Id,
            request.Project.Name,
            request.Project.Type.ToString(),
            manifestUnchanged
                ? existingManifest!.UpdatedAt
                : DateTimeOffset.UtcNow,
            assets);
    }

    private static async Task EnsureDestinationCanBeUpdatedAsync(
        string destinationPath,
        AssetListItem asset,
        GitProjectManifest? manifest,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(destinationPath))
        {
            return;
        }

        var isManagedAsset = manifest?.Assets.Any(item =>
            item.AssetId == asset.AssetId &&
            string.Equals(
                item.FileName,
                asset.OriginalFilename,
                StringComparison.OrdinalIgnoreCase)) == true;
        if (isManagedAsset ||
            await FilesHaveSameContentAsync(asset.Path, destinationPath, cancellationToken))
        {
            return;
        }

        throw new IOException(
            $"Git 仓库中已存在不同内容的文件“{asset.OriginalFilename}”，Beacon 不会覆盖它。");
    }

    private static async Task CopyAssetAsync(
        AssetListItem asset,
        string destinationPath,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".cdsi-tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(
                asset.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[CopyBufferSize];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    reportBytes(read);
                }

                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteManifestAsync(
        string repositoryDirectory,
        GitProjectManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            repositoryDirectory,
            GitProjectSyncConventions.ManifestFileName);
        var temporaryPath = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    ManifestJsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<bool> FilesHaveSameContentAsync(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(firstPath).Length != new FileInfo(secondPath).Length)
        {
            return false;
        }

        var firstHash = await ComputeSha256Async(firstPath, cancellationToken);
        var secondHash = await ComputeSha256Async(secondPath, cancellationToken);
        return firstHash.AsSpan().SequenceEqual(secondHash);
    }

    private static async Task<byte[]> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlySet<int>? acceptedExitCodes = null)
    {
        acceptedExitCodes ??= new HashSet<int> { 0 };
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

        foreach (var item in environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Git 进程。");
        }
        catch (Win32Exception exception)
        {
            var installationHint = OperatingSystem.IsMacOS()
                ? "未找到 Git。请先安装 Xcode Command Line Tools（xcode-select --install）。"
                : "未找到 Git。请先安装 Git for Windows，并确保 git.exe 已加入 PATH。";
            throw new InvalidOperationException(installationHint, exception);
        }

        using (process)
        {
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Cancellation still wins if the process has already exited.
                }

                throw;
            }

            var result = new GitCommandResult(
                process.ExitCode,
                await standardOutputTask,
                await standardErrorTask);
            if (!acceptedExitCodes.Contains(result.ExitCode))
            {
                var details = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
                details = details.Trim();
                if (details.Length > 4000)
                {
                    details = details[^4000..];
                }

                throw new InvalidOperationException(
                    $"Git 命令执行失败（退出码 {result.ExitCode}）。" +
                    (details.Length == 0
                        ? string.Empty
                        : $"{Environment.NewLine}{details}"));
            }

            return result;
        }
    }

    private static void Report(
        IProgress<GitProjectSyncProgress>? progress,
        string stage,
        GitProjectSyncRequest request,
        int processedFiles,
        long processedBytes,
        string? currentPath = null)
    {
        progress?.Report(new GitProjectSyncProgress(
            stage,
            processedFiles,
            request.Assets.Count,
            processedBytes,
            request.Assets.Sum(asset => asset.Size),
            currentPath));
    }

    private static void EnsureDescendantPath(string rootPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Git 临时目录必须位于 CDSI 工作目录中。");
        }
    }

    private static void TryDeleteOperationDirectory(string operationDirectory)
    {
        try
        {
            if (Directory.Exists(operationDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(
                             operationDirectory,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(operationDirectory, recursive: true);
            }
        }
        catch
        {
            // A failed cleanup leaves only a uniquely named workspace temp directory.
        }
    }

    private sealed record GitProjectManifest(
        int SchemaVersion,
        Guid ProjectId,
        string ProjectName,
        string ProjectType,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<GitProjectManifestAsset> Assets);

    private sealed record GitProjectManifestAsset(
        Guid AssetId,
        string FileName,
        long Size,
        string? Sha256);

    private sealed record GitCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
