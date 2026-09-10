using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Core.Abstractions;

public interface IAssetMetadataExtractor
{
    string Name { get; }

    bool Supports(DiscoveredFile file);

    Task<MetadataExtractionResult> ExtractAsync(
        DiscoveredFile file,
        CancellationToken cancellationToken = default);
}
