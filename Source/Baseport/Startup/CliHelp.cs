using System.Reflection;

namespace Baseport;

public static class CliHelp
{
    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "unknown").Split('+')[0];

    public static readonly string[] WrapperCommands =
    {
        "logs", "update", "service", "start", "stop", "restart", "status", "doctor", "uninstall"
    };

    public static readonly string[] Commands =
    {
        // Help & Information
        "help",
        "version                       Display the application version",
        "status                        Show whether Baseport is running and its location",
        "config [--check]              Display each setting and its source",
        "doctor                        Verify the installation and diagnose issues",
        "check                         Run a quick integrity check on the database",
        "restore <archive> [--yes]     Replace the data with a backup, keeping the old files aside",
        "logs [lines]                  Follow log files and show initial login details",

        // Operations
        "accounts                      List, promote, and repair admin accounts",
        "providers                     Toggle Postgres and TDS endpoints on or off",

        // Service control
        "start | stop | restart        Control the systemd service (Linux, root)",
        "stop --force                  Stop any running service or foreground process",

        // Installation
        "service [--urls URL]          Install the systemd service (Linux, root)",
        "update                        Replace the installation with the latest release",
        "uninstall [--purge]           Remove Baseport, with optional data deletion"
    };

    public static int List(string what, IEnumerable<string> commands, string? error = null)
    {
        var output = error is null ? Console.Out : Console.Error;
        if (error is not null) output.WriteLine($"Error: {error}");
        output.WriteLine($"baseport version: {Version}");
        output.WriteLine();
        output.WriteLine($"Choose one of the available {what}:");
        foreach (var c in commands)
            output.WriteLine($"        {c}");
        return error is null ? 0 : 1;
    }

    public static int Invalid() => List("commands", Commands, "invalid command");
}
