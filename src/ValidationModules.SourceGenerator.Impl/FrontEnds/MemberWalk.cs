using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// The properties a validator should check, in the order it should check them, together with every
/// declaration whose constraints apply to each.
/// </summary>
/// <remarks>
/// <para>
/// This replaces <c>type.GetMembers()</c>, which returns <b>declared</b> members only. A derived
/// type therefore never saw its base's constrained properties, so <c>CreateOrder : BaseRequest</c>
/// silently validated none of the correlation and tenant ids <c>BaseRequest</c> declares - a clean
/// build and a validator answering "valid" for invalid input. The base's own validator was correct
/// all along; only the derived type lost the rules.
/// </para>
/// <para>
/// Both front-ends walk through here, native and DataAnnotations alike. One rule to explain, rather
/// than <c>[Required]</c> behaving differently depending on which namespace it came from.
/// </para>
/// </remarks>
public static class MemberWalk {

    /// <summary>
    /// One property to check, and where its constraints come from.
    /// </summary>
    /// <param name="Property">
    /// The declaration the generated validator reads. Always the most-derived one, which is the
    /// member C# itself binds <c>value.Name</c> to.
    /// </param>
    /// <param name="Sources">
    /// The declarations whose attributes apply, in the order their constraints should be read:
    /// the property's own declaration first, then any interface declarations it implements.
    /// </param>
    /// <param name="Inherited">
    /// True when <paramref name="Property"/> was declared by a base type rather than by the type
    /// being validated. Its diagnostics belong to the assembly that declared it, not to every
    /// assembly that derives from it.
    /// </param>
    /// <param name="Hidden">
    /// A base declaration this one displaced, when that declaration carried constraints that are
    /// now dropped. Drives VM0030; null in every other case.
    /// </param>
    public readonly record struct Member(
        IPropertySymbol Property,
        ImmutableArray<IPropertySymbol> Sources,
        bool Inherited,
        IPropertySymbol? Hidden);

    /// <summary>
    /// Walks <paramref name="type"/>'s base chain and interfaces.
    /// </summary>
    /// <param name="type">The type being validated.</param>
    /// <param name="compilation">
    /// Used to decide readability. The generated validator is a separate class in
    /// <paramref name="type"/>'s own assembly, so a <c>protected</c> member, or an
    /// <c>internal</c> one belonging to a referenced assembly, cannot be read from it - and emitting
    /// the reference anyway would put the error inside generated code.
    /// </param>
    /// <param name="carriesConstraints">
    /// Whether a declaration carries anything this front-end cares about. Interfaces are only
    /// consulted for members that do, so implementing a plain interface costs nothing.
    /// </param>
    public static ImmutableArray<Member> PropertiesOf(
        INamedTypeSymbol type,
        Compilation compilation,
        Func<IPropertySymbol, bool> carriesConstraints) {

        // Root-most base first, down the chain, then the type's own members. This satisfies the
        // declaration-order guarantee of IMPLEMENTATION-PLAN.md §4.2 naturally: a shared base's
        // fields report before the fields of the type that extends it, which is the order someone
        // reading the two declarations top to bottom would expect.
        var chain = new List<INamedTypeSymbol>();

        for (var current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType) {
            chain.Add(current);
        }

        chain.Reverse();

        var order = new List<string>();
        var declarations = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        var hidden = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        var inheritedAttributes = new Dictionary<string, List<IPropertySymbol>>(StringComparer.Ordinal);

        foreach (var declaring in chain) {
            foreach (var member in declaring.GetMembers()) {
                if (member is not IPropertySymbol property || property.IsStatic || property.IsIndexer) {
                    continue;
                }

                // A property the validator could not read is skipped here rather than reported.
                // Its own declaring type reports it if it is constrained; from a derived type it is
                // simply not part of the surface, and emitting an unbindable reference would land
                // the error in generated code.
                //
                // The type's own members are exempt: an inaccessible one there is a mistake worth
                // VM0009, and dropping it silently is what VM0009 exists to prevent.
                var own = SymbolEqualityComparer.Default.Equals(declaring, type);

                if (!own && !IsReadableFrom(property, type, compilation)) {
                    continue;
                }

                if (declarations.TryGetValue(property.Name, out var displaced)) {
                    if (Overrides(property, displaced)) {
                        // An override is one property with two declarations, not two properties.
                        // ValidationConstraintAttribute is declared Inherited = true, so an
                        // override that says nothing inherits what the base said - and one that
                        // adds a constraint adds to it. Accumulated most-derived first, so the
                        // chain reads the way the declarations do.
                        if (!inheritedAttributes.TryGetValue(property.Name, out var overrides)) {
                            inheritedAttributes[property.Name] = overrides = new List<IPropertySymbol>();
                        }

                        overrides.Insert(0, displaced);
                    } else if (carriesConstraints(displaced)) {
                        // Two separate properties that share a name. Most-derived wins entirely,
                        // never merged: two [StringLength] bounds on one field is ambiguous and
                        // would report twice. Remembered rather than discarded so VM0030 can say
                        // what was dropped.
                        hidden[property.Name] = displaced;
                    }
                } else {
                    order.Add(property.Name);
                }

                declarations[property.Name] = property;
            }
        }

        // Interface declarations merge onto whatever implements them, rather than replacing. An
        // interface is a contract the type opted into, so [Required] on IAudited.ModifiedBy and
        // [StringLength] on the implementing property are both meant, where a base and a derived
        // declaration of one property are two answers to the same question.
        //
        // Sorted by name: AllInterfaces order is not contractual, and an incremental generator that
        // emits members in a different order between runs invalidates downstream caches for nothing.
        var extra = new Dictionary<string, List<IPropertySymbol>>(StringComparer.Ordinal);

        foreach (var contract in type.AllInterfaces.OrderBy(i => i.ToDisplayString(), StringComparer.Ordinal)) {
            foreach (var member in contract.GetMembers()) {
                if (member is not IPropertySymbol declared || !carriesConstraints(declared)) {
                    continue;
                }

                if (type.FindImplementationForInterfaceMember(declared) is not IPropertySymbol implementation) {
                    continue;
                }

                // An explicit implementation is private, so it never made the walk above; there is
                // nothing to hang the constraint on and nothing the validator could read.
                if (!declarations.TryGetValue(implementation.Name, out var target)
                    || !SymbolEqualityComparer.Default.Equals(target, implementation)) {
                    continue;
                }

                if (!extra.TryGetValue(implementation.Name, out var list)) {
                    extra[implementation.Name] = list = new List<IPropertySymbol>();
                }

                list.Add(declared);
            }
        }

        var members = ImmutableArray.CreateBuilder<Member>(order.Count);

        foreach (var name in order) {
            var property = declarations[name];

            var sources = ImmutableArray.CreateBuilder<IPropertySymbol>();
            sources.Add(property);

            if (inheritedAttributes.TryGetValue(name, out var overridden)) {
                sources.AddRange(overridden);
            }

            if (extra.TryGetValue(name, out var interfaces)) {
                sources.AddRange(interfaces);
            }

            members.Add(new Member(
                property,
                sources.ToImmutable(),
                Inherited: !SymbolEqualityComparer.Default.Equals(property.ContainingType, type),
                Hidden: hidden.TryGetValue(name, out var displaced) ? displaced : null));
        }

        return members.ToImmutable();
    }

    /// <summary>
    /// Whether <paramref name="property"/> overrides <paramref name="candidate"/>, at any depth.
    /// </summary>
    /// <remarks>
    /// Walked rather than compared one level deep, so that a three-level chain where the middle
    /// level also overrides still resolves to one property rather than reading as a hide.
    /// </remarks>
    private static bool Overrides(IPropertySymbol property, IPropertySymbol candidate) {
        for (var current = property.OverriddenProperty;
             current is not null;
             current = current.OverriddenProperty) {
            if (SymbolEqualityComparer.Default.Equals(current, candidate)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the generated validator, a separate class in <paramref name="type"/>'s assembly, can
    /// read <paramref name="property"/>.
    /// </summary>
    private static bool IsReadableFrom(
        IPropertySymbol property, INamedTypeSymbol type, Compilation compilation) {

        if (property.GetMethod is not { } getter) {
            return false;
        }

        // Asked of the assembly rather than of the type: the validator is a sibling class, so
        // protected buys it nothing, and internal only works when the declaration is local.
        return compilation.IsSymbolAccessibleWithin(property, type.ContainingAssembly)
            && compilation.IsSymbolAccessibleWithin(getter, type.ContainingAssembly);
    }
}
