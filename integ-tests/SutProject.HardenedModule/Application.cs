using DependencyModules.Runtime.Helpers;
using DependencyModules.Runtime.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using ValidationModules.Constraints;

namespace SutProject.Hm;

/// <summary>
/// A Hardened application: an entry point carrying <c>[HardenedModule]</c> and no
/// <c>[DependencyModule]</c> of its own.
/// </summary>
/// <remarks>
/// <para>
/// The second shape <c>EntryPointLookup</c> has to recognise, and the one the whole change exists
/// for. Hardened's <c>HardenedSourceGenerator</c> derives from DependencyModules'
/// <c>BaseSourceGenerator</c> and names <c>HardenedModuleAttribute</c> where DependencyModules'
/// own generator names <c>DependencyModuleAttribute</c>, so a Hardened application is a module
/// keyed on <c>DependencyRegistry&lt;Application&gt;</c> that carries neither the attribute
/// DependencyModules looks for nor any type this repository can reference.
/// </para>
/// <para>
/// So the <c>IDependencyModule</c> half is written out below rather than generated. It is a
/// transcription of what <c>DependencyModuleWriter</c> emits, which is what makes
/// <c>AddModule&lt;Application&gt;()</c> in the tests the same call a Hardened application's host
/// makes.
/// </para>
/// </remarks>
[Hardened.Shared.Runtime.Attributes.HardenedModule]
public partial class Application : IDependencyModule
{
    /// <summary>
    /// Empty, and load-bearing. Without an explicit static constructor the type is
    /// <c>beforefieldinit</c>, and the CLR is then only obliged to run the field initializers
    /// before the first access to a static field - not before the module is constructed. The
    /// registration this generator adds is a field initializer, so it could run after the
    /// registry had already been read. <c>DependencyModuleWriter</c> emits this on every module
    /// for the same reason, which is why nothing has to be done about it in a real application.
    /// </summary>
    static Application() { }

    public void PopulateServiceCollection(IServiceCollection services) =>
        DependencyRegistry<Application>.LoadModules(services, this);

    void IDependencyModule.InternalApplyServices(IServiceCollection services) =>
        DependencyRegistry<Application>.ApplyServices(services);
}

public sealed record Shipment
{
    [Required]
    [StringLength(min: 3, max: 20)]
    public string? Tracking { get; init; }

    [Range(1, 500)]
    public int Weight { get; init; }
}
