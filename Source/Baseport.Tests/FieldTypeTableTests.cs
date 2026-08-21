using Xunit;
using Baseport;

namespace Baseport.Tests;

// The type table is data, and a typo in data fails at first use instead of at compile time.
public class FieldTypeTableTests
{
    [Fact]
    public void Every_name_and_alias_resolves_to_exactly_one_type()
    {
        foreach (var type in FieldTypes.All)
        {
            Assert.Same(type, FieldTypes.Find(type.Name));
            foreach (var alias in type.Aliases) Assert.Same(type, FieldTypes.Find(alias));
        }
    }

    [Fact]
    public void Every_type_sits_in_a_group_the_picker_draws()
    {
        foreach (var type in FieldTypes.All)
            Assert.Contains(type.Group, FieldGroups.Order);
    }

    // A computed type is filled in server-side over a whole record, it can never be a member of one.
    [Fact]
    public void No_computed_type_is_nestable()
    {
        foreach (var type in FieldTypes.All.Where(t => t.Computed || t.Secret))
            Assert.False(type.Nestable, $"{type.Name} is computed or secret but marked nestable.");
    }

    // The console greys Required and Identifier for a computed type off the payload's Computed flag, so that flag has to name exactly the types the validator refuses them on.
    [Fact]
    public void Every_computed_type_is_one_the_validator_refuses_required_and_identifier_on()
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
