using System.Text;
using System.Text.Json;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Identity;

namespace CDSI.Agent.Infrastructure.Identity;

public sealed class FileClientIdentityProvider : IClientIdentityProvider
{
    public const string IdentityFileName = "client-identity.json";

    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _identityPath;

    public FileClientIdentityProvider(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _identityPath = Path.Combine(
            Path.GetFullPath(dataDirectory),
            IdentityFileName);
    }

    public ClientIdentity GetOrCreate()
    {
        if (File.Exists(_identityPath))
        {
            return ReadIdentity();
        }

        var directory = Path.GetDirectoryName(_identityPath)
            ?? throw new InvalidOperationException(
                "Client identity path has no parent directory.");
        Directory.CreateDirectory(directory);

        var identity = new ClientIdentity(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var document = new ClientIdentityDocument(
            CurrentSchemaVersion,
            identity.Id,
            identity.CreatedAtUtc);
        var json = JsonSerializer.Serialize(document, SerializerOptions);
        var temporaryPath = Path.Combine(
            directory,
            $".{IdentityFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, _identityPath, overwrite: false);
                return identity;
            }
            catch (IOException) when (File.Exists(_identityPath))
            {
                return ReadIdentity();
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private ClientIdentity ReadIdentity()
    {
        try
        {
            var document = JsonSerializer.Deserialize<ClientIdentityDocument>(
                File.ReadAllText(_identityPath),
                SerializerOptions);
            if (document is null ||
                document.SchemaVersion != CurrentSchemaVersion ||
                document.ClientId == Guid.Empty ||
                document.CreatedAtUtc == default)
            {
                throw new InvalidDataException("Client identity document is invalid.");
            }

            return new ClientIdentity(
                document.ClientId,
                document.CreatedAtUtc.ToUniversalTime());
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException)
        {
            throw new InvalidDataException(
                "Client identity document is invalid.",
                exception);
        }
    }

    private sealed record ClientIdentityDocument(
        int SchemaVersion,
        Guid ClientId,
        DateTimeOffset CreatedAtUtc);
}
