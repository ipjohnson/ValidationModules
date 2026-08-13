#!/usr/bin/env bash
#
# Runs the benchmark suites.
#
# The default suite measures ValidationModules on its own — components, whole validation passes
# through generated code, and the machinery around them. That is the one to run after a change, and
# the one whose numbers are comparable between runs.
#
# The comparative suite measures it against FluentValidation and DataAnnotations. It is a separate
# project and opt-in, because "is this library still fast" and "is this library faster than that
# one" are different questions asked at different times, and only the first is worth running often.
#
# Usage:
#   ./scripts/benchmark.sh                          default suite, both runtimes
#   ./scripts/benchmark.sh --quick                  default suite, JIT only, short job
#   ./scripts/benchmark.sh --comparative            comparative suite, JIT
#   ./scripts/benchmark.sh --comparative --aot      comparative suite, Native AOT as well
#   ./scripts/benchmark.sh --all                    both suites
#   ./scripts/benchmark.sh -- --filter '*Nested*'   anything after -- goes to BenchmarkDotNet
#
# Useful BenchmarkDotNet arguments to forward:
#   --list flat                 what is available without running any of it
#   --anyCategories=endtoend    component | endtoend | design (default suite)
#   --anyCategories=flat        flat | nested | collection | startup | di (comparative suite)
#   --filter '*Collection*'     one class or one benchmark

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEFAULT_PROJECT="${REPO_ROOT}/benchmarks/ValidationModules.Benchmarks"
COMPARATIVE_PROJECT="${REPO_ROOT}/benchmarks/ValidationModules.Benchmarks.Comparative"

RUN_DEFAULT=true
RUN_COMPARATIVE=false
FORWARDED=()

# --runtime is this repository's own switch, consumed by the benchmark app rather than by
# BenchmarkDotNet — see BenchmarkArguments.cs for why BDN's --runtimes could not be used.
DEFAULT_RUNTIME=""
COMPARATIVE_RUNTIME=""

while [ $# -gt 0 ]; do
    case "$1" in
        --comparative)
            RUN_DEFAULT=false
            RUN_COMPARATIVE=true
            ;;
        --all)
            RUN_DEFAULT=true
            RUN_COMPARATIVE=true
            ;;
        --quick)
            # Fewer iterations, JIT only. For "did I break something", not for a number worth
            # quoting. --quick is ours rather than BenchmarkDotNet's --job short, which declares no
            # runtime and so picks up Native AOT from the project — see BenchmarkArguments.cs.
            DEFAULT_RUNTIME="jit"
            COMPARATIVE_RUNTIME="jit"
            FORWARDED+=("--quick")
            ;;
        --aot)
            # The comparative suite is JIT by default; this asks for the ILC publish as well.
            DEFAULT_RUNTIME="both"
            COMPARATIVE_RUNTIME="both"
            ;;
        --)
            shift
            FORWARDED+=("$@")
            break
            ;;
        -h|--help)
            sed -n '2,26p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            FORWARDED+=("$1")
            ;;
    esac
    shift
done

# BenchmarkDotNet drops into an interactive menu when nothing selects a benchmark, which makes the
# no-argument invocation — the documented way to run the whole suite — print a prompt and exit,
# and hang outright under redirection or CI. Supply the selector it wants unless the caller already
# named one.
has_selector() {
    for argument in "$@"; do
        case "${argument}" in
            --filter|--filter=*|--anyCategories|--anyCategories=*|--allCategories|--allCategories=*|--attribute|--attribute=*|--list|--list=*|-h|--help)
                return 0
                ;;
        esac
    done

    return 1
}

run_suite() {
    local project="$1"
    local runtime="$2"
    shift 2

    local args=()
    if [ -n "${runtime}" ]; then
        args+=("--runtime" "${runtime}")
    fi
    args+=("$@")

    if ! has_selector "$@"; then
        args+=("--filter" "*")
    fi

    echo
    echo "==> $(basename "${project}")"
    echo

    # ${args[@]+"${args[@]}"} rather than "${args[@]}": an empty array under `set -u` is an
    # unbound variable on the bash 3.2 that ships with macOS.
    dotnet run --configuration Release --project "${project}" -- ${args[@]+"${args[@]}"}
}

if [ "${RUN_DEFAULT}" = true ]; then
    run_suite "${DEFAULT_PROJECT}" "${DEFAULT_RUNTIME}" ${FORWARDED[@]+"${FORWARDED[@]}"}
fi

if [ "${RUN_COMPARATIVE}" = true ]; then
    run_suite "${COMPARATIVE_PROJECT}" "${COMPARATIVE_RUNTIME}" ${FORWARDED[@]+"${FORWARDED[@]}"}
fi

echo
echo "Results are written to BenchmarkDotNet.Artifacts/results next to the project that produced them."
