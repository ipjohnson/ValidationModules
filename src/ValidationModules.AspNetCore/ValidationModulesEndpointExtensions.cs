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
    /// <para>
    /// <b>Naming a type the handler does not take throws at startup</b> rather than answering every
    /// request as valid - see <see cref="ValidationEndpointFilterFactory"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The argument type to validate.</typeparam>
    /// <param name="builder">The endpoint being built.</param>
    /// <param name="statusCode">
    /// The status a failure on this endpoint answers with, overriding
    /// <see cref="ValidationProblemOptions.StatusCode"/> for this endpoint alone. The body's
    /// <c>type</c> member follows it, so a 422 links the definition of 422. Null - the default -
    /// keeps the application-wide options.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The handler has no parameter that could hold a <typeparamref name="T"/>.
    /// </exception>
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder, int? statusCode = null) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilterFactory(ValidationEndpointFilterFactory.For<T>(strict: true, statusCode));

        return builder;
    }

    /// <summary>
    /// The same, for a whole group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate rather than one generic method over <c>IEndpointConventionBuilder</c>, because C#
    /// infers all type arguments or none: a single two-parameter overload would force every caller
    /// to write <c>Validate&lt;RouteHandlerBuilder, CreateOrder&gt;()</c>, naming a type they
    /// mention nowhere else.
    /// </para>
    /// <para>
    /// <b>A group does not throw for a handler that takes no <typeparamref name="T"/>, it skips
    /// it.</b> A group is a mixed bag by construction - one that validates <c>CreateOrder</c> on its
    /// writes may reasonably carry a <c>GET /orders/{id}</c> too - so a non-matching endpoint is
    /// left without the filter rather than rejected. The single-endpoint overload, where naming the
    /// wrong type can only be a mistake, throws.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The argument type to validate.</typeparam>
    /// <param name="builder">The group being built.</param>
    /// <param name="statusCode">
    /// The status a failure in this group answers with. See the single-endpoint overload.
    /// </param>
    public static RouteGroupBuilder Validate<T>(this RouteGroupBuilder builder, int? statusCode = null) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilterFactory(ValidationEndpointFilterFactory.For<T>(strict: false, statusCode));

        return builder;
    }
}
