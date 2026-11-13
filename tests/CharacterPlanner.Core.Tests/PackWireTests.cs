using System.Reflection;
using System.Runtime.CompilerServices;
using CharacterPlanner.Core.Packs.Wire;

namespace CharacterPlanner.Core.Tests;

public sealed class PackWireTests
{
    [Fact]
    public void WireTypesCarryNoDefaultsOfTheirOwn()
    {
        // Source-generated reading resets a member a file leaves out to its type's default, where reflection kept an
        // initializer's value. That's safe only while no wire member has an initializer: defaults belong in the mapper.
        var wireTypes = typeof(PackWireContext).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(PackWireContext).Namespace && type.IsClass && !type.IsAbstract && type.Name.EndsWith("Wire", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(typeof(RulesWire), wireTypes);
        foreach (var type in wireTypes)
        {
            var instance = RuntimeHelpers.GetUninitializedObject(type);
            var constructed = Activator.CreateInstance(type)!;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.GetIndexParameters().Length == 0))
            { Assert.True(Equals(property.GetValue(instance), property.GetValue(constructed)), $"{type.Name}.{property.Name} has a default of its own"); }
        }
    }
}
