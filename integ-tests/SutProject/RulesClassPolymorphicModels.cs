using ValidationModules;
using ValidationModules.Constraints;

namespace SutProject.Polymorphic;

/// <summary>
/// A base that is not sealed and has rules of its own. A plain <c>Animal</c> is a value of the
/// declared type, which no subtype's arm matches.
/// </summary>
public class Animal
{
    [Required]
    public string? Name { get; init; }
}

public sealed class Dog : Animal
{
    [Required]
    public string? Breed { get; init; }
}

/// <summary>
/// Stands in for a type from a package nobody here owns: nothing on it asks for a descent, so the
/// rules class below does, and asks for the rules of each value's actual type.
/// </summary>
public sealed record Household
{
    public Animal? Pet { get; init; }
    public List<Animal> Pets { get; init; } = new();
}

public sealed class HouseholdRules : IValidationRulesFor<Household>
{
    public static void Describe(ValidationRules<Household> rules, Household x)
    {
        rules.Nested(x.Pet, Polymorphism.CompileTime);
        rules.Each(x.Pets, Polymorphism.CompileTime);
    }
}

/// <summary>The same descents, resolved through the container rather than a switch.</summary>
public sealed record DynamicHousehold
{
    public Animal? Pet { get; init; }
    public List<Animal> Pets { get; init; } = new();
}

public sealed class DynamicHouseholdRules : IValidationRulesFor<DynamicHousehold>
{
    public static void Describe(ValidationRules<DynamicHousehold> rules, DynamicHousehold x)
    {
        rules.Nested(x.Pet, Polymorphism.Runtime);
        rules.Each(x.Pets, Polymorphism.Runtime);
    }
}
