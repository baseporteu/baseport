using System.Reflection;

namespace Baseport;

// One shape for every command list the binary and its wrapper print, a caller who mistypes gets the same answer wherever they were.
public static class CliHelp
{
    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "unknown").Split('+')[0];

    // Handled by the wrapper script the installer writes, never by the binary; listed here so both print one menu.
    public static readonly string[] WrapperCommands =
    {
        "logs", "update", "service", "start", "stop", "restart", "status", "doctor", "uninstall"
    };

    public static readonly string[] Commands =
    {
        "accounts                      list, promote and repair admin accounts",
        "providers                     turn the Postgres and TDS endpoints on or off",
        "status                        say whether Baseport is running, and where",
        "doctor                        check this install and name what is wrong",
        "logs [lines]                  follow the log files",
        "service [--urls URL]          install the systemd service (Linux, root)",
        "start | stop | restart        control that service (Linux, root)",
        "update                        replace this install with the latest release",
        "uninstall [--purge]           remove Baseport, --purge deletes the data too",
        "version                       print the version",
        "help                          this list"
    };

    public static int List(string what, IEnumerable<string> commands, string? error = null)
    {
        var output = error is null ? Console.Out : Console.Error;
        if (error is not null) output.WriteLine($"Error: {error}");
        output.WriteLine($"baseport version: {Version}");
        output.WriteLine();
        output.WriteLine($"Choose one of the available {what}:");
        foreach (var c in commands) output.WriteLine($"        {c}");
        return error is null ? 0 : 1;
    }

    public static int Invalid() => List("commands", Commands, "invalid command");
}
