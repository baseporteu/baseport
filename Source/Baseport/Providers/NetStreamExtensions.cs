namespace Baseport.Providers;

internal static class NetStreamExtensions
{
    public const int MaxMessageBytes = 1 << 20;

    // connections per listener
    public const int MaxConnections = 32;

    internal static TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    public static async Task<byte[]?> ReadExactAsync(this Stream stream, int count, CancellationToken ct)
    {
        if (count <= 0) return Array.Empty<byte>();
        if (count > MaxMessageBytes) return null;
        var buf = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
            if (read == 0) return null;
            offset += read;
        }
        return buf;
    }
}
