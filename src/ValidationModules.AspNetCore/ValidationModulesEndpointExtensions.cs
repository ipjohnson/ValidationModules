using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ValidationModules.AspNetCore;

// ReSharper disable once CheckNamespace - MS convention: endpoint extensions live in the namespace
// a minimal API file has already imported.
namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Attaches validation to an endpoint.
/// </summary>
public static class ValidationModulesEndpointExtensions {

    /// <summary>
    /// Validates the <typeparamref name="T"/> argument before the handler runs, answering with a
    /// problem response instead if it fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <typeparamref name="T"/> is named here rather than inferred from the handler, and that is
    /// what keeps the whole path free of reflection - see
    /// <see cref="ValidationEndpointFilter{T}"/>. It reads as a little ceremony, and it is the
    /// reason a published Native AOT binary works.
    /// </para>
    /// <para>
    /// Chain it for a handler with more than one thing worth validating:
    /// <c>.Validate&lt;CreateOrder&gt;().Validate&lt;Coupon&gt;()</c>. Filters run in the order they
    /// were added, so the first failure answers and the second never runs.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The argument type to validate.</typeparam>
    /// <param name="builder">The endpoint being built.</param>
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder) where T : class {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter<RouteHandlerBuilder, ValidationEndpointFilter<T>>();

        return builder;
    }

    /// <summary>
    /// The same, for a whole group.
    /// </summary>
    /// <remarks>
    /// Separate rather than one generic method over <c>IEndpointConventionBuilder</c>, because C#
    /// infers all type arguments or none: a single two-parameter overload would force every caller
    /// to write <c>Validate&lt;RouteHandlerBuilder, CreateOrder&gt;()</c>, naming a type they
    /// mention nowhere else.
    /// </remarks>
    /// <typeparam name="T">The argument type to validate.</typeparam>
    /// <param name="builder">The group being built.</param>
    public static RouteGroupBuilder Validate<T>(this RouteGroupBuilder builder) where T : class {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter<RouteGroupBuilder, ValidationEndpointFilter<T>>();

        return builder;
    }
}
