using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Baseport;

public static class BundledSettings
{
    public const string FileName = "appsettings.json";

    public static JsonConfigurationSource Source(string directory) => new()
    {
        Path = FileName,
        Optional = true,
        ReloadOnChange = true,
        FileProvider = new PhysicalFileProvider(directory)
    };
}
