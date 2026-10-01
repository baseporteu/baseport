using Xunit;
using Baseport;

namespace Baseport.Tests;

public class FieldTypeTableTests
{
    [Fact]
    public void AliasesResolveOnce()
    {
        foreach (var type in FieldTypes.All)
        {
            Assert.Same(type, FieldTypes.Find(type.Name));
            foreach (var alias in type.Aliases) Assert.Same(type, FieldTypes.Find(alias));
        }
    }

    [Fact]
    public void TypesHavePickerGroup()
    {
        foreach (var type in FieldTypes.All)
            Assert.Contains(type.Group, FieldGroups.Order);
    }

    [Fact]
    public void ComputedNotNestable()
    {
        foreach (var type in FieldTypes.All.Where(t => t.Computed || t.Secret))
            Assert.False(type.Nestable, $"{type.Name} is computed or secret but marked nestable.");
    }

    [Fact]
    public void ComputedRefusesRequired()
    {
        foreach (var t in FieldTypes.All.Where(t => t.Computed))
        {
            var required = FieldValidation.ValidateFieldDefinition(
                new FieldDefinition { Name = "f", DataType = t.Name, Expression = "1", IsRequired = true },
                new List<string>(), new List<string>(), _ => true);
            Assert.Contains(required, e => e.Contains("cannot be required"));

            var identifier = FieldValidation.ValidateFieldDefinition(
                new FieldDefinition { Name = "f", DataType = t.Name, Expression = "1", IsIdentifier = true },
                new List<string>(), new List<string>(), _ => true);
            Assert.Contains(identifier, e => e.Contains("lookup identifier"));
        }
    }
}
