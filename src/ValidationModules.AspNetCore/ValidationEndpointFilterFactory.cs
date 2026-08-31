using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ValidationModules.AspNetCore;

/// <summary>
/// Builds the endpoint filter, having first checked that the endpoint can actually receive a
/// <c>T</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a factory rather than <c>AddEndpointFilter&lt;TBuilder, TFilter&gt;</c>.</b> The filter
/// finds its argument by scanning <c>context.Arguments</c> for the first <c>T</c>, and finds nothing
/// in two very different situations: the argument is legally absent on this request, or the handler
/// has no <c>T</c> parameter at all and never will. The first is a per-request condition the filter
/// is right to stand aside for. The second is a wiring mistake that makes the endpoint report
/// success for every body forever, which is the exact failure this library exists to remove -
/// <c>[Required]</c> on a record parameter is a build error here for the same reason.
/// </para>
/// <para>
/// The two are indistinguishable at request time and trivially distinguishable at endpoint-build
/// time, where <see cref="EndpointFilterFactoryContext.MethodInfo"/> is the handler's signature. So
/// the check happens once, at startup, and the per-request path is unchanged.
/// </para>
/// <para>
/// <b>Constructing the filter here also removes a reflective activation.</b>
/// <c>AddEndpointFilter&lt;TBuilder, TFilter&gt;</c> goes through
/// <c>ActivatorUtilities.CreateFactory</c>, which is what forced a public constructor onto an
/// internal type. Resolving the options and calling <c>new</c> is the same work without the
/// indirection.
/// </para>
/// </remarks>
internal static class ValidationEndpointFilterFactory {

    /// <summary>
    /// The factory delegate to hand to <c>AddEndpointFilterFactory</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="strict"/> is what separates the two call sites. On a single endpoint, naming
    /// a type the handler does not take is unambiguously a mistake and throws. On a group it is
    /// not: a group that validates <c>CreateOrder</c> may reasonably also carry a
    /// <c>GET /orders/{id}</c>, so a non-matching endpoint is left alone instead - and left alone
    /// properly, without the filter in its chain at all.
    /// </remarks>
    /// <typeparam name="T">The argument type to validate.</typeparam>
    /// <param name="strict">Whether a handler without a <typeparamref name="T"/> is an error.</param>
    /// <param name="statusCode">The per-endpoint status override, or null for the options'.</param>
    internal static Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> For<T>(
        bool strict, int? statusCode = null) {

        return (context, next) => {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            if (!CanReceive<T>(context.MethodInfo)) {
                if (strict) {
                    throw new InvalidOperationException(
                        $"Validate<{typeof(T)}>() is attached to a handler that takes no " +
                        $"{typeof(T)} parameter ({Describe(context.MethodInfo)}). The filter would " +
                        "find nothing to validate and answer every request as valid. Name the type " +
                        "the handler actually takes, or drop the call.");
                }

                return next;
            }

            EnsureValidatorRegistered<T>(context.ApplicationServices, context.MethodInfo);

            var options = context.ApplicationServices.GetRequiredService<IOptions<ValidationProblemOptions>>();
            var filter = new ValidationEndpointFilter<T>(options, statusCode);

            return invocation => filter.InvokeAsync(invocation, next);
        };
    }

    /// <summary>
    /// Fails endpoint building when nothing is registered to validate a <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same wiring mistake the filter's own throw reports, discovered at <c>app.Run()</c>
    /// instead of on the first request: <c>.Validate&lt;T&gt;()</c> naming an unregistered type
    /// compiled clean and then answered every request - valid bodies included - with a 500.
    /// </para>
    /// <para>
    /// A presence check rather than a resolve, because the runner is scoped and this holds the
    /// root provider. Fires for the group overload too: its leniency is for handlers that take no
    /// <typeparamref name="T"/>, not for a type nothing can validate - every handler this filter
    /// does attach to would fail. A container that does not implement
    /// <see cref="IServiceProviderIsService"/> cannot be asked, so the request-time throw stays as
    /// the backstop.
    /// </para>
    /// </remarks>
    private static void EnsureValidatorRegistered<T>(IServiceProvider services, MethodInfo handler) {
        if (services.GetService<IServiceProviderIsService>() is not { } registered) {
            return;
        }

        if (registered.IsService(typeof(ValidationRunner<T>)) ||
            registered.IsService(typeof(IValidatorFor<T>))) {
            return;
        }

        throw new InvalidOperationException(
            $"Validate<{typeof(T)}>() is attached to a handler ({Describe(handler)}) " +
            $"but no validator is registered for {typeof(T)}. Call the generated " +
            "Add<Assembly>Validators() at startup, or register an IValidatorFor<> by hand. A " +
            "collection body validates element-wise when declared as List<T> or T[] of a " +
            "validated type; other collection shapes need a hand-written validator. Failing here " +
            "beats answering every request with a 500.");
    }

    /// <summary>
    /// Whether any parameter of <paramref name="handler"/> could hold a <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// Assignable in <b>either</b> direction, deliberately. A parameter declared as <c>T</c> or a
    /// subtype obviously qualifies. A parameter declared as a base type or interface qualifies too,
    /// because the value arriving at run time may still be a <c>T</c> and the filter's <c>is T</c>
    /// would match it - rejecting that case would turn a working endpoint into a startup failure,
    /// which is a worse trade than missing a rare mistake. Nullable wrappers are unwrapped on both
    /// sides: a handler taking <c>Coupon?</c> receives a boxed <c>Coupon</c> at run time, which
    /// <c>IsAssignableFrom</c> alone cannot see.
    /// </remarks>
    private static bool CanReceive<T>(MethodInfo handler) {
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        var parameters = handler.GetParameters();

        for (var i = 0; i < parameters.Length; i++) {
            var declared = Nullable.GetUnderlyingType(parameters[i].ParameterType)
                ?? parameters[i].ParameterType;

            if (target.IsAssignableFrom(declared) || declared.IsAssignableFrom(target)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The handler's parameter list, for the exception message.</summary>
    private static string Describe(MethodInfo handler) {
        var parameters = handler.GetParameters();

        if (parameters.Length == 0) {
            return "it takes no parameters";
        }

        var names = new string[parameters.Length];

        for (var i = 0; i < parameters.Length; i++) {
            names[i] = parameters[i].ParameterType.Name;
        }

        return "it takes " + string.Join(", ", names);
    }
}
