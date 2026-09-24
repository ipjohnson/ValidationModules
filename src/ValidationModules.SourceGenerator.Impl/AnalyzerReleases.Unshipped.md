; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VM1303 | ValidationModules.Usage | Warning | Options or MatchTimeoutMilliseconds is set on the reference form of [Pattern], which reads neither.
VM1505 | ValidationModules.Usage | Warning | A nested target from another assembly has no validator this compilation can call; the descent is dropped.
VM3106 | ValidationModules.Usage | Warning | A rules-class descent repeats [ValidateNested] on the same property; the rules-class descent is dropped.
