namespace Baseport.Providers;

// shared by both wire listeners: read exactly n bytes, or learn the peer hung up, instead of raw ReadAsync
internal static class NetStreamExtensions
{
    public static async Task<byte[]?> ReadExactAsync(this Stream stream, int count, CancellationToken ct)
    {
        if (count <= 0) return Array.Empty<byte>();
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
