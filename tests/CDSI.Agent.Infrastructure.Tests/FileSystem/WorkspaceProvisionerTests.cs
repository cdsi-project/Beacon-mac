using CDSI.Agent.Infrastructure.FileSystem;

namespace CDSI.Agent.Infrastructure.Tests.FileSystem;

public sealed class WorkspaceProvisionerTests
{
    [Fact]
    public async Task ProvisionAsync_CreatesTheControlledWorkspaceLayout()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "cdsi_workspace");
        var provisioner = new WorkspaceProvisioner();

        var layout = await provisioner.ProvisionAsync(path);

        Assert.Equal(Path.GetFullPath(path), layout.RootPath);
        Assert.All(
            new[]
            {
                layout.InboxPath,
                layout.AssetsPath,
                layout.ExportsPath,
                layout.CachePath,
                layout.TempPath,
                layout.SystemPath
            },
            item => Assert.True(Directory.Exists(item)));
    }

    [Fact]
    public void NormalizeAndValidatePath_RejectsAFileSystemRoot()
    {
        using var directory = new TestDirectory();
        var root = Path.GetPathRoot(directory.Path)
            ?? throw new InvalidOperationException("Test directory has no root.");
        var provisioner = new WorkspaceProvisioner();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provisioner.NormalizeAndValidatePath(root));

        Assert.Contains("根目录", exception.Message);
    }
}
