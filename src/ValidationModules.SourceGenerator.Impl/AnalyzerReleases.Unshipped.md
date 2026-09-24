; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VM1013 | ValidationModules.Usage | Error | Two types would get validators with the same name in one namespace, such as a nested Order.Item and a top-level Order_Item. Neither validator is generated.
VM1303 | ValidationModules.Usage | Warning | Options or MatchTimeoutMilliseconds is set on the reference form of [Pattern], which reads neither.
VM1505 | ValidationModules.Usage | Warning | A nested target from another assembly has no validator this compilation can call; the descent is dropped.
VM2010 | ValidationModules.Usage | Info | A class-level ValidationAttribute runs after the property rules pass and before IValidatableObject. Reported as Warning when its arguments cannot be rendered, and with an ignoring tail when ValidationModules_DataAnnotations is Ignore.
VM3008 | ValidationModules.Usage | Error | A Pattern or Apply argument names no static method that generated code can call.
VM3106 | ValidationModules.Usage | Warning | A rules-class descent repeats [ValidateNested] on the same property; the rules-class descent is dropped.
