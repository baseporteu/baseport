using System.Collections;

namespace Baseport;

public static class ConfigCli
{
    public sealed record Layer(string Name, IReadOnlyDictionary<string, string> Values);

    public sealed record Report(IReadOnlyList<(string Key, string Value, string Source)> Settings, IReadOnlyList<string> Problems);

    public static IConfigurationRoot Build(string bundledSettings, string localSettings) =>
        new ConfigurationBuilder()
            .AddJsonFile(bundledSettings, optional: true)
            .AddJsonFile(localSettings, optional: true)
            .AddJsonFile(EnvironmentJson(), optional: true)
            .Add(EnvFile.Source(EnvFile.InstallDirectory()))
            .Add(EnvFile.EnvironmentAliases())
            .AddEnvironmentVariables()
            .Build();

    private static string EnvironmentJson() =>
        Path.Combine(Directory.GetCurrentDirectory(), $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json");

    public static int Run(string[] args, string bundledSettings, string localSettings)
    {
        var check = args is [_, "--check"];
        if (args.Length > 1 && !check)
        {
            Console.Error.WriteLine("Usage: baseport config [--check]");
            return 1;
        }

        var report = Inspect(Layers(bundledSettings, localSettings), EnvFileProblems(EnvFile.InstallDirectory()));
        if (!check)
        {
            foreach (var (key, value, source) in report.Settings)
                Console.WriteLine($"{key,-30} {value,-40} {source}");
            if (report.Problems.Count > 0) Console.WriteLine();
        }
        foreach (var problem in report.Problems) Console.WriteLine(problem);
        return check && report.Problems.Count > 0 ? 1 : 0;
    }

    public static IReadOnlyList<Layer> Layers(string bundledSettings, string localSettings)
    {
        var layers = new List<Layer>();
        foreach (var file in new[] { bundledSettings, localSettings, EnvironmentJson() }.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(file)) continue;
            var values = new ConfigurationBuilder().AddJsonFile(file, optional: true).Build().AsEnumerable()
                .Where(p => p.Value is not null && Relevant(p.Key))
                .ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);
            layers.Add(new Layer(Path.GetFileName(file), values));
        }

        var envFile = EnvFile.Path(EnvFile.InstallDirectory());
        var fromFile = File.Exists(envFile)
            ? EnvFile.Parse(File.ReadAllText(envFile)).Entries.GroupBy(e => e.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        layers.Add(new Layer(EnvFile.FileName, fromFile));

        var environment = EnvFile.AliasesFromEnvironment().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = (string)variable.Key;
            if (name.StartsWith("Baseport__", StringComparison.OrdinalIgnoreCase))
                environment[name.Replace("__", ":", StringComparison.Ordinal)] = variable.Value as string ?? "";
        }
        layers.Add(new Layer("environment", environment));
        return layers;
    }

    private static bool Relevant(string key) =>
        key.StartsWith("Baseport:", StringComparison.OrdinalIgnoreCase) || key.Equals("urls", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> EnvFileProblems(string directory)
    {
        var path = EnvFile.Path(directory);
        return File.Exists(path) ? EnvFile.Parse(File.ReadAllText(path)).Problems : [];
    }

    public static Report Inspect(IReadOnlyList<Layer> layers, IReadOnlyList<string> envFileProblems)
    {
        var problems = new List<string>(envFileProblems);
        var envFile = layers.FirstOrDefault(l => l.Name == EnvFile.FileName);
        var environment = layers.FirstOrDefault(l => l.Name == "environment");

        if (envFile is not null)
            foreach (var key in envFile.Values.Keys.Order(StringComparer.OrdinalIgnoreCase))
            {
                var files = layers.TakeWhile(l => l != envFile).Where(l => l.Values.ContainsKey(key)).Select(l => l.Name).ToList();
                if (files.Count > 0) problems.Add($"{key} is set in {EnvFile.FileName} and {string.Join(", ", files)}; {EnvFile.FileName} wins");
                if (environment?.Values.ContainsKey(key) == true) problems.Add($"{key} is set in {EnvFile.FileName} and the environment; the environment wins");
            }

        var keys = EnvFile.KnownKeys.Concat(layers.SelectMany(l => l.Values.Keys)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var settings = new List<(string, string, string)>();
        foreach (var key in keys)
        {
            var winner = layers.LastOrDefault(l => l.Values.ContainsKey(key));
            var value = winner is null ? "" : winner.Values[key];
            settings.Add((key, value, winner?.Name ?? "default"));
        }
        return new Report(settings, problems);
    }
}
