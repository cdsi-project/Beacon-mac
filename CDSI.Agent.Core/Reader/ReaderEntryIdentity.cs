using System.Security.Cryptography;
using System.Text;

namespace CDSI.Agent.Core.Reader;

public static class ReaderEntryIdentity
{
    public static string CreateKey(ReaderParsedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrWhiteSpace(entry.ExternalId))
        {
            var externalId = entry.ExternalId.Trim();
            return externalId.Length <= 2_000
                ? $"id:{externalId}"
                : $"id-hash:{Hash(externalId)}";
        }

        if (!string.IsNullOrWhiteSpace(entry.Url))
        {
            try
            {
                return $"url:{ReaderUrl.CreateKey(entry.Url)}";
            }
            catch (ArgumentException)
            {
                // Fall back to deterministic content identity for malformed entry URLs.
            }
        }

        var timestamp = entry.PublishedAt ?? entry.UpdatedAt;
        var material = string.Join(
            "\n",
            entry.Title.Trim(),
            timestamp?.ToUniversalTime().ToString("O") ?? string.Empty,
            entry.Author?.Trim() ?? string.Empty);
        return $"hash:{Hash(material)}";
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
