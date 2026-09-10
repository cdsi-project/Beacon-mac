using System.Text;

namespace CDSI.Agent.Infrastructure.OpenWeb;

internal static class LocalArticleTextFileReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    static LocalArticleTextFileReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<LocalArticleText> ReadAsync(
        string path,
        int maximumInputBytes,
        int maximumOutputCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumInputBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
        }

        if (maximumOutputCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters));
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytesToRead = (int)Math.Min(stream.Length, maximumInputBytes);
        var bytes = GC.AllocateUninitializedArray<byte>(bytesToRead);
        var offset = 0;

        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(
                bytes.AsMemory(offset, bytes.Length - offset),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset != bytes.Length)
        {
            Array.Resize(ref bytes, offset);
        }

        var inputTruncated = stream.Length > offset;
        var decoded = Decode(bytes, inputTruncated);
        var normalized = NormalizeLineEndings(decoded.Text);
        var outputTruncated = normalized.Length > maximumOutputCharacters;
        if (outputTruncated)
        {
            normalized = normalized[..maximumOutputCharacters];
        }

        return new LocalArticleText(
            normalized.Trim(),
            inputTruncated || outputTruncated);
    }

    private static DecodedArticleText Decode(byte[] bytes, bool inputTruncated)
    {
        var span = bytes.AsSpan();

        if (span.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return DecodeKnownEncoding(
                span[4..],
                new UTF32Encoding(true, false, true),
                new UTF32Encoding(true, false, false),
                inputTruncated);
        }

        if (span.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return DecodeKnownEncoding(
                span[4..],
                new UTF32Encoding(false, false, true),
                new UTF32Encoding(false, false, false),
                inputTruncated);
        }

        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return DecodeKnownEncoding(
                span[3..],
                StrictUtf8,
                new UTF8Encoding(false, false),
                inputTruncated);
        }

        if (span.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return DecodeKnownEncoding(
                span[2..],
                new UnicodeEncoding(true, false, true),
                new UnicodeEncoding(true, false, false),
                inputTruncated);
        }

        if (span.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return DecodeKnownEncoding(
                span[2..],
                new UnicodeEncoding(false, false, true),
                new UnicodeEncoding(false, false, false),
                inputTruncated);
        }

        if (TryDecode(StrictUtf8, span, inputTruncated, out var utf8))
        {
            return new DecodedArticleText(utf8, inputTruncated);
        }

        var strictGb18030 = Encoding.GetEncoding(
            54936,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        if (TryDecode(strictGb18030, span, inputTruncated, out var gb18030))
        {
            return new DecodedArticleText(gb18030, inputTruncated);
        }

        return new DecodedArticleText(
            Encoding.GetEncoding(1252).GetString(span),
            inputTruncated);
    }

    private static DecodedArticleText DecodeKnownEncoding(
        ReadOnlySpan<byte> bytes,
        Encoding strictEncoding,
        Encoding lenientEncoding,
        bool inputTruncated)
    {
        try
        {
            return new DecodedArticleText(
                strictEncoding.GetString(bytes),
                inputTruncated);
        }
        catch (DecoderFallbackException)
        {
            return new DecodedArticleText(lenientEncoding.GetString(bytes), true);
        }
    }

    private static bool TryDecode(
        Encoding encoding,
        ReadOnlySpan<byte> bytes,
        bool inputTruncated,
        out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException) when (inputTruncated)
        {
            var maximumTrim = Math.Min(4, bytes.Length);
            for (var trim = 1; trim <= maximumTrim; trim++)
            {
                try
                {
                    text = encoding.GetString(bytes[..^trim]);
                    return true;
                }
                catch (DecoderFallbackException)
                {
                }
            }
        }
        catch (DecoderFallbackException)
        {
        }

        text = string.Empty;
        return false;
    }

    private static string NormalizeLineEndings(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private sealed record DecodedArticleText(string Text, bool IsTruncated);
}

internal sealed record LocalArticleText(string Text, bool IsTruncated);
