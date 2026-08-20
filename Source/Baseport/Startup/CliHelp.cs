using System.Reflection;

namespace Baseport;

// One shape for every command list the binary and its wrapper print, so a caller who mistypes gets the same answer wherever they were.
public static class CliHelp
{
    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "unknown").Split('+')[0];

    // Handled by the wrapper script the installer writes, never by the binary; listed here so both print one menu.
    public static readonly string[] WrapperCommands = { "logs", "update", "-i", "-d" };

    public static readonly string[] Commands =
    {
        "accounts",
        "providers",
        "logs",
        "update",
        "-i",
        "-d",
        "version",
        "help"
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
