using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Baseport;

public static partial class EnvFile
{
    public const string FileName = ".env";

    public static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["BASEPORT_CONNECTION_STRING"] = "Baseport:ConnectionString",
        ["BASEPORT_TRUST_FORWARDED_HEADERS"] = "Baseport:TrustForwardedHeaders",
        ["BASEPORT_ADMIN_ADDRESS"] = "Baseport:AdminAddress",
        ["BASEPORT_ALLOW_INSECURE_SIGNIN"] = "Baseport:AllowInsecureSignIn",
        ["BASEPORT_ALLOW_REMOTE_PROVIDERS"] = "Baseport:AllowRemoteProviders",
        ["BASEPORT_MAX_CONNECTIONS"] = "Baseport:MaxConnections",
        ["BASEPORT_URLS"] = "urls"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static readonly FrozenSet<string> ComposeOnly = new[] { "BASEPORT_TAG", "BASEPORT_PORT" }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> KnownKeys = Aliases.Values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public sealed record Entry(int Line, string Name, string? Key, string Value);

    public sealed record Parsed(IReadOnlyList<Entry> Entries, IReadOnlyList<string> Problems);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    public static string Path(string directory) => System.IO.Path.Combine(directory, FileName);

    public static string InstallDirectory(string? processPath, string baseDirectory) =>
        processPath is not null && !System.IO.Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetDirectoryName(processPath) ?? baseDirectory
            : baseDirectory;

    public static string InstallDirectory() => InstallDirectory(Environment.ProcessPath, AppContext.BaseDirectory);

    public static string? KeyFor(string name) =>
        Aliases.TryGetValue(name, out var alias) ? alias
        : name.Contains("__", StringComparison.Ordinal) ? name.Replace("__", ":", StringComparison.Ordinal)
        : null;

    public static Parsed Parse(string text)
    {
        var entries = new List<Entry>();
        var problems = new List<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var number = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            var name = eq > 0 ? line[..eq].Trim() : "";
            if (!NamePattern().IsMatch(name))
            {
                problems.Add($"{FileName} line {number}: not NAME=value");
                continue;
            }

            var value = Unquote(line[(eq + 1)..].Trim());
            var key = KeyFor(name);
            if (key is null)
            {
                if (!ComposeOnly.Contains(name)) problems.Add($"{FileName} line {number}: unknown name {name}");
                continue;
            }
            if (key.StartsWith("Baseport:", StringComparison.OrdinalIgnoreCase) && !KnownKeys.Contains(key))
                problems.Add($"{FileName} line {number}: unknown setting {key}");
            entries.Add(new Entry(number, name, key, value));
        }

        foreach (var repeated in entries.GroupBy(e => e.Key!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"{FileName}: {repeated.Key} is set on lines {string.Join(", ", repeated.Select(e => e.Line))}; line {repeated.Last().Line} wins");

        return new Parsed(entries, problems);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) return value[1..^1];
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? value[..comment].TrimEnd() : value;
    }

    public static IConfigurationSource Source(string directory) => new EnvFileSource(Path(directory));

    public static IConfigurationSource EnvironmentAliases() => new AliasSource();

    public static IEnumerable<KeyValuePair<string, string>> AliasesFromEnvironment()
    {
        foreach (var (alias, key) in Aliases)
            if (Environment.GetEnvironmentVariable(alias) is { } value)
                yield return new(key, value);
    }

    public static void InsertBeforeEnvironment(IList<IConfigurationSource> sources, IConfigurationSource source)
    {
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
            {
                sources.Insert(i, source);
                return;
            }
        }
        sources.Add(source);
    }

    private sealed class AliasSource : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new AliasProvider();
    }

    private sealed class AliasProvider : ConfigurationProvider
    {
        public override void Load()
        {
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in AliasesFromEnvironment()) Data[key] = value;
        }
    }

    private sealed class EnvFileSource(string path) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new EnvFileProvider(path);
    }

    private sealed class EnvFileProvider(string path) : ConfigurationProvider
    {
        public override void Load()
        {
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return;
            foreach (var entry in Parse(File.ReadAllText(path)).Entries)
                Data[entry.Key!] = entry.Value;
        }
    }
}
