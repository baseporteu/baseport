using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace Baseport;

// Turns the startup failures an operator actually causes into one actionable line; a wrong port or a locked database file is a configuration mistake, not a defect.
public static class StartupFailure
{
    // Returns an actionable message for a known failure, or null when the cause is unrecognised.
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

                // The schema guard already explains itself and how to fix it.
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

    // Kestrel puts the address in its message ("Failed to bind to address http://127.0.0.1:5000: ..."), which is the only place it survives to.
    private static string Address(Exception ex)
    {
        foreach (var e in Unwrap(ex))
        {
            const string marker = "Failed to bind to address ";
            var start = e.Message.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;

            // "Failed to bind to address http://127.0.0.1:5000: address already in use." The address runs to the first space; Kestrel's trailing colon separates it from the reason and is not part of it.
            var rest = e.Message[(start + marker.Length)..];
            var end = rest.IndexOf(' ');
            return (end > 0 ? rest[..end] : rest).Trim().TrimEnd(':', '.');
        }
        return "The configured address";
    }
}
