using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace Baseport;

public static class StartupFailure
{

    public static string? Describe(Exception ex)
    {
        foreach (var e in Unwrap(ex))
        {
            switch (e)
            {
                case SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }:
                case IOException when e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase):
                    return $"{Address(ex)} is already in use. Another Baseport instance is probably running: stop it, or start this one on a different port with --urls http://localhost:PORT";

                case SocketException { SocketErrorCode: SocketError.AccessDenied }:
                    return $"Permission denied binding {Address(ex)}. Ports below 1024 need elevated privileges; pick a higher port with --urls.";

                case SocketException { SocketErrorCode: SocketError.AddressNotAvailable }:
                    return $"{Address(ex)} is not an address on this machine. Check the host in --urls.";

                case InvalidOperationException when e.Message.Contains("delete the database file", StringComparison.OrdinalIgnoreCase):
                    return e.Message;

                case SqliteException { SqliteErrorCode: 14 }:
                    return "The database file could not be opened. Check that the path in Baseport:ConnectionString exists and is writable.";

                case SqliteException { SqliteErrorCode: 5 } or SqliteException { SqliteErrorCode: 8 }:
                    return "The database file is locked or read-only. Another process may be holding it.";

                case UnauthorizedAccessException:
                    return $"Permission denied: {e.Message} Baseport needs write access to its working directory for the database and the log folder.";

                case FileNotFoundException { FileName: not null } fnf when fnf.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase):
                    return $"Configuration file not found: {fnf.FileName}. Run Baseport from the directory containing appsettings.json.";
            }
        }
        return null;
    }

    private static IEnumerable<Exception> Unwrap(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            yield return e;
    }

    private static string Address(Exception ex)
    {
        foreach (var e in Unwrap(ex))
        {
            const string marker = "Failed to bind to address ";
            var start = e.Message.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;

            var rest = e.Message[(start + marker.Length)..];
            var end = rest.IndexOf(' ');
            return (end > 0 ? rest[..end] : rest).Trim().TrimEnd(':', '.');
        }
        return "The configured address";
    }
}
