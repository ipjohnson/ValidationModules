; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VM1303 | ValidationModules.Usage | Warning | Options or MatchTimeoutMilliseconds is set on the reference form of [Pattern], which reads neither.
VM1505 | ValidationModules.Usage | Warning | A nested target from another assembly has no validator this compilation can call; the descent is dropped.
VM2010 | ValidationModules.Usage | Info | A class-level ValidationAttribute runs after the property rules pass and before IValidatableObject. Reported as Warning when its arguments cannot be rendered, and with an ignoring tail when ValidationModules_DataAnnotations is Ignore.
VM3106 | ValidationModules.Usage | Warning | A rules-class descent repeats [ValidateNested] on the same property; the rules-class descent is dropped.
VM5004 | ValidationModules.Usage | Warning | A ValidationModules_* MSBuild property holds a value it does not accept. The generator uses the default.
