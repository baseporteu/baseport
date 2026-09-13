namespace Baseport;

public static class OpenApiCache
{
    private static long _version;
    private static long _bakedVersion = -1;
    private static string? _json;

    public static void Invalidate() => Interlocked.Increment(ref _version);

    public static long CurrentVersion => Volatile.Read(ref _version);

    public static string? Get(long version) => version == Volatile.Read(ref _bakedVersion) ? _json : null;

    public static void Set(long version, string json)
    {
        _json = json;
        Volatile.Write(ref _bakedVersion, version);
    }
}
