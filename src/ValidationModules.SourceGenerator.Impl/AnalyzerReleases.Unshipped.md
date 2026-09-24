; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VM1011 | ValidationModules.Usage | Warning | A constraint on a field or a static property is never evaluated.
VM1303 | ValidationModules.Usage | Warning | Options or MatchTimeoutMilliseconds is set on the reference form of [Pattern], which reads neither.
