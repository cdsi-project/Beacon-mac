using System.Text;

namespace CDSI.Agent.Core.Storage;

public static class ObjectStorageObjectKey
{
    // Aliyun OSS limits the complete UTF-8 object key to 1,023 bytes.
    private const int MaximumUtf8ByteCount = 1_023;

    public static bool TryCreateForAsset(
        Guid assetId,
        string? filename,
        out string objectKey,
        out string? errorMessage)
    {
        objectKey = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(filename))
        {
            errorMessage = "OSS 文件名不能为空。";
            return false;
        }

        if (filename is "." or "..")
        {
            errorMessage = "OSS 文件名不能是 . 或 ..。";
            return false;
        }

        if (filename.Any(character =>
                character is '/' or '\\' || char.IsControl(character)))
        {
            errorMessage = "OSS 文件名不能包含路径分隔符或控制字符。";
            return false;
        }

        var candidate = $"assets/{assetId:N}/{filename}";
        if (Encoding.UTF8.GetByteCount(candidate) > MaximumUtf8ByteCount)
        {
            errorMessage = "OSS 文件名过长。";
            return false;
        }

        objectKey = candidate;
        return true;
    }

    public static bool TryCreateForDirectory(
        string? directoryName,
        string? filename,
        out string objectKey,
        out string? errorMessage)
    {
        objectKey = string.Empty;
        errorMessage = null;

        if (!TryValidatePathSegment(directoryName, "OSS 目录名", out errorMessage) ||
            !TryValidatePathSegment(filename, "OSS 文件名", out errorMessage))
        {
            return false;
        }

        var candidate = $"{directoryName}/{filename}";
        if (Encoding.UTF8.GetByteCount(candidate) > MaximumUtf8ByteCount)
        {
            errorMessage = "OSS 目录名和文件名组合过长。";
            return false;
        }

        objectKey = candidate;
        return true;
    }

    public static bool TryRenameFile(
        string? currentObjectKey,
        string? newFilename,
        out string objectKey,
        out string? errorMessage)
    {
        objectKey = string.Empty;
        if (string.IsNullOrWhiteSpace(currentObjectKey))
        {
            errorMessage = "云端对象键不能为空。";
            return false;
        }

        if (!TryValidatePathSegment(newFilename, "云端文件名", out errorMessage))
        {
            return false;
        }

        var separatorIndex = currentObjectKey.LastIndexOf('/');
        var prefix = separatorIndex < 0
            ? string.Empty
            : currentObjectKey[..(separatorIndex + 1)];
        var candidate = prefix + newFilename!.Trim();
        if (Encoding.UTF8.GetByteCount(candidate) > MaximumUtf8ByteCount)
        {
            errorMessage = "云端文件名过长。";
            return false;
        }

        objectKey = candidate;
        errorMessage = null;
        return true;
    }

    private static bool TryValidatePathSegment(
        string? value,
        string displayName,
        out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errorMessage = $"{displayName}不能为空。";
            return false;
        }

        if (value is "." or "..")
        {
            errorMessage = $"{displayName}不能是 . 或 ..。";
            return false;
        }

        if (value.Any(character =>
                character is '/' or '\\' || char.IsControl(character)))
        {
            errorMessage = $"{displayName}不能包含路径分隔符或控制字符。";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
