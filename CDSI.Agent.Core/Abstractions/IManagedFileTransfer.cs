using CDSI.Agent.Core.Transfers;

namespace CDSI.Agent.Core.Abstractions;

public interface IManagedFileTransfer
{
    Task<VerifiedManagedFileCopy> CopyAndVerifyAsync(
        LocalAssetTransferSource source,
        string targetPath,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default);

    Task DeleteSourceAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
