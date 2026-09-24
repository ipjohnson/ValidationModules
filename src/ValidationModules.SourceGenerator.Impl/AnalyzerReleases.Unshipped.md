; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VM1303 | ValidationModules.Usage | Warning | Options or MatchTimeoutMilliseconds is set on the reference form of [Pattern], which reads neither.
VM3008 | ValidationModules.Usage | Error | A Pattern or Apply argument names no static method that generated code can call.
