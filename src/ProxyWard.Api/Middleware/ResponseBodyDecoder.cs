using System.IO.Compression;

namespace ProxyWard.Api.Middleware;

internal static class ResponseBodyDecoder
{
    public static bool TryDecode(
        byte[] body,
        string? contentEncoding,
        int maxDecodedBytes,
        out byte[] decodedBody)
    {
        decodedBody = body;
        if (string.IsNullOrWhiteSpace(contentEncoding))
        {
            return true;
        }

        var encodings = contentEncoding
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = encodings.Length - 1; index >= 0; index--)
        {
            if (!TryDecodeSingleEncoding(decodedBody, encodings[index], maxDecodedBytes, out decodedBody))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecodeSingleEncoding(
        byte[] body,
        string encoding,
        int maxDecodedBytes,
        out byte[] decodedBody)
    {
        decodedBody = body;
        return encoding.ToLowerInvariant() switch
        {
            "identity" => true,
            "gzip" or "x-gzip" => TryDecompress(body, stream => new GZipStream(stream, CompressionMode.Decompress), maxDecodedBytes, out decodedBody),
            "br" => TryDecompress(body, stream => new BrotliStream(stream, CompressionMode.Decompress), maxDecodedBytes, out decodedBody),
            "deflate" => TryDecompress(body, stream => new DeflateStream(stream, CompressionMode.Decompress), maxDecodedBytes, out decodedBody),
            _ => false
        };
    }

    private static bool TryDecompress(
        byte[] body,
        Func<Stream, Stream> createDecoder,
        int maxDecodedBytes,
        out byte[] decodedBody)
    {
        decodedBody = [];
        try
        {
            using var source = new MemoryStream(body);
            using var decoder = createDecoder(source);
            return TryReadToEnd(decoder, maxDecodedBytes, out decodedBody);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryReadToEnd(Stream source, int maxBytes, out byte[] body)
    {
        body = [];
        using var destination = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        Span<byte> buffer = stackalloc byte[16 * 1024];

        while (true)
        {
            var bytesRead = source.Read(buffer);
            if (bytesRead == 0)
            {
                body = destination.ToArray();
                return true;
            }

            if (destination.Length + bytesRead > maxBytes)
            {
                return false;
            }

            destination.Write(buffer[..bytesRead]);
        }
    }
}
