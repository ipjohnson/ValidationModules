using ValidationModules.SourceGenerator.Impl.Models;

namespace ValidationModules.SourceGenerator.Impl.Emitters;

/// <summary>
/// Which validators can reach which, following <c>[ValidateNested]</c> edges.
/// </summary>
/// <remarks>
/// <para>
/// Built once per compilation, for one question with two consequences: does this nested descent
/// come back round to the validator that declares it?
/// </para>
/// <para>
/// If it does, the nested validator cannot be a constructor dependency. MS.DI answers
/// <c>IEnumerable&lt;IValidatorFor&lt;T&gt;&gt;</c> by constructing the registered
/// <c>IValidatorFor&lt;T&gt;</c>, so a cycle in the model graph is a cycle in the service graph -
/// reported as a circular dependency, and fatal at startup rather than on first use because
/// ASP.NET Core validates on build in Development. It also means the straight-line <c>IsValid</c>
/// cannot be emitted: it calls the nested validator's <c>IsValid</c> directly and nothing on that
/// path counts depth, so cyclic data recursed until the process aborted.
/// </para>
/// <para>
/// A direct self-reference is the shape that matters most - category trees, comment threads, BOMs,
/// org charts - but it is not the only one, and a two-type cycle is not visibly different to the
/// container. So this walks the graph rather than testing for identity. The model set is already
/// collected at the emission site, so knowing this costs no incrementality.
/// </para>
/// </remarks>
public sealed class NestingGraph {

    private readonly Dictionary<string, HashSet<string>> _reachable;

    private NestingGraph(Dictionary<string, HashSet<string>> reachable) => _reachable = reachable;

    /// <summary>The empty graph, for a caller with no model set to hand - golden tests, mostly.</summary>
    public static NestingGraph Empty { get; } = new(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));

    /// <summary>
    /// Builds the transitive closure of the nesting edges between <paramref name="models"/>.
    /// </summary>
    public static NestingGraph Build(IEnumerable<ValidatedTypeModel> models) {
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var model in models) {
            var from = NameOf(model);

            if (!edges.TryGetValue(from, out var targets)) {
                targets = new HashSet<string>(StringComparer.Ordinal);
                edges[from] = targets;
            }

            foreach (var property in model.Properties) {
                if (property.ElementValidatorName is { } to) {
                    targets.Add(to);
                }
            }
        }

        var reachable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var node in edges.Keys) {
            reachable[node] = Close(node, edges);
        }

        return new NestingGraph(reachable);
    }

    /// <summary>
    /// Everything reachable from <paramref name="start"/>, by an explicit stack rather than
    /// recursion - the graph being walked is a cyclic one by assumption, and a generator that
    /// overflows its stack takes the compiler with it.
    /// </summary>
    private static HashSet<string> Close(string start, Dictionary<string, HashSet<string>> edges) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();

        if (edges.TryGetValue(start, out var first)) {
            foreach (var target in first) {
                pending.Push(target);
            }
        }

        while (pending.Count > 0) {
            var node = pending.Pop();

            if (!seen.Add(node) || !edges.TryGetValue(node, out var next)) {
                continue;
            }

            foreach (var target in next) {
                pending.Push(target);
            }
        }

        return seen;
    }

    /// <summary>
    /// Whether descending from <paramref name="model"/> into <paramref name="property"/> can arrive
    /// back at <paramref name="model"/>.
    /// </summary>
    public bool DescentReturnsToDeclarer(ValidatedTypeModel model, ValidatedPropertyModel property) {
        if (property.ElementValidatorName is not { } target) {
            return false;
        }

        var declarer = NameOf(model);

        return target == declarer ||
            (_reachable.TryGetValue(target, out var reachable) && reachable.Contains(declarer));
    }

    /// <summary>
    /// Whether <paramref name="model"/> lies on a cycle at all - the question the straight-line
    /// <c>IsValid</c> turns on, since any unbounded descent is enough to lose the process.
    /// </summary>
    public bool ParticipatesInACycle(ValidatedTypeModel model) =>
        model.Properties.Any(property => DescentReturnsToDeclarer(model, property));

    private static string NameOf(ValidatedTypeModel model) =>
        model.Namespace.Length == 0
            ? $"global::{model.ValidatorName}"
            : $"global::{model.Namespace}.{model.ValidatorName}";
}
