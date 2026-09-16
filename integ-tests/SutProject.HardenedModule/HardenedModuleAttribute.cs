namespace Hardened.Shared.Runtime.Attributes;

/// <summary>
/// <c>Hardened.Shared.Runtime</c>'s marker, declared here rather than referenced.
/// </summary>
/// <remarks>
/// Hardened depends on ValidationModules, so ValidationModules cannot depend on Hardened - and
/// this is five lines of the real one, which is a plain <c>Attribute</c> subclass with no members.
/// <c>EntryPointLookup</c> matches on the full metadata name, so a declaration is what a package
/// reference would be. A change to the name in <c>Hardened.Shared.Runtime</c> would go unnoticed
/// here; the integration test in Hardened's own repository is what holds that end.
/// </remarks>
public class HardenedModuleAttribute : Attribute;
