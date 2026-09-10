using CDSI.Agent.Core.OpenWeb;

namespace CDSI.Agent.Core.Abstractions;

public interface IOpenWebArticleContentReader
{
    bool Supports(string path);

    Task<OpenWebArticleContent> ReadAsync(
        string path,
        CancellationToken cancellationToken = default);
}
