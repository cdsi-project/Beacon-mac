using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Infrastructure.Metadata;

public sealed class GenericMetadataExtractor : IAssetMetadataExtractor
{
    public string Name => "generic";

    public bool Supports(DiscoveredFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return true;
    }

    public Task<MetadataExtractionResult> ExtractAsync(
        DiscoveredFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MetadataExtractionResult(
            MetadataExtractionStatus.Unsupported));
    }
}
