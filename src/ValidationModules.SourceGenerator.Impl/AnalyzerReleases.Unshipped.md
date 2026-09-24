; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VM2010 | ValidationModules.Usage | Info | A class-level ValidationAttribute runs after the property rules pass and before IValidatableObject. Reported as Warning when its arguments cannot be rendered, and with an ignoring tail when ValidationModules_DataAnnotations is Ignore.
