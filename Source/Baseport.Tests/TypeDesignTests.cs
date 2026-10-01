using System.Reflection;
using System.Runtime.CompilerServices;
using Baseport.Client;
using Xunit;

namespace Baseport.Tests;

public class TypeDesignTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly[] Assemblies = [typeof(RecordEngine).Assembly, typeof(IBaseportClient).Assembly];

    private static IEnumerable<Type> OwnTypes() =>
        Assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace?.StartsWith("Baseport", StringComparison.Ordinal) == true)
            .Where(t => t.Namespace != "Baseport.Data.Migrations")
            .Where(t => !t.Name.Contains('<') && !t.IsDefined(typeof(CompilerGeneratedAttribute), false));

    private static bool IsRecord(Type t) => t.GetMethod("<Clone>$") is not null;

    private static bool IsList(Type t) =>
        t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>)
            || (t.GetGenericTypeDefinition() == typeof(Task<>) || t.GetGenericTypeDefinition() == typeof(ValueTask<>) || t.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
                && t.GetGenericArguments().Any(IsList));

    private static void Empty(IEnumerable<string> offenders, string rule)
    {
        var list = offenders.Order(StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{rule}:\n{string.Join("\n", list)}");
    }

    [Fact]
    public void EveryClassIsSealedOrStatic() =>
        Empty(OwnTypes().Where(t => t.IsClass && !t.IsAbstract && !t.IsSealed).Select(t => t.FullName!),
            "Seal these classes");

    [Fact]
    public void NoMutableStructs() =>
        Empty(OwnTypes().Where(t => t.IsValueType && !t.IsEnum && !t.IsDefined(typeof(IsReadOnlyAttribute), false)).Select(t => t.FullName!),
            "Make these structs readonly");

    [Fact]
    public void NoPublicListReturns()
    {
        var types = OwnTypes().Where(t => t.IsVisible).ToList();
        var methods = types
            .SelectMany(t => t.GetMethods(Declared).Where(m => m.IsPublic && !m.IsSpecialName && IsList(m.ReturnType)).Select(m => $"{t.FullName}.{m.Name}"));
        var properties = types.Where(IsRecord)
            .SelectMany(t => t.GetProperties(Declared).Where(p => IsList(p.PropertyType)).Select(p => $"{t.FullName}.{p.Name}"));
        Empty(methods.Concat(properties), "Return IReadOnlyList<T> instead");
    }

    [Fact]
    public void StaticLookupsAreFrozen() =>
        Empty(OwnTypes()
            .SelectMany(t => t.GetFields(Declared)
                .Where(f => f.IsStatic && f.IsInitOnly && f.FieldType.IsGenericType && !f.IsDefined(typeof(CompilerGeneratedAttribute), false))
                .Where(f => f.FieldType.GetGenericTypeDefinition() is var g && (g == typeof(Dictionary<,>) || g == typeof(HashSet<>)))
                .Select(f => $"{t.FullName}.{f.Name}")),
            "Use FrozenDictionary or FrozenSet");
}
