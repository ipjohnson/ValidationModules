; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VM1011 | ValidationModules.Usage | Warning | A constraint on a field or a static property is never evaluated.
VM1013 | ValidationModules.Usage | Error | Two types would get generated classes with the same name in one namespace. A nested Order.Item and a top-level Order_Item both get Order_ItemValidator, and neither validator is generated. A nested rules class Order.ItemRules and a top-level Order_ItemRules both get the companion Order_ItemRules_Rules, and neither rules class is compiled. A nested Order.Shared and a top-level Order_Shared that both declare fragments both get Order_Shared_Fragments, and neither container is generated.
VM1014 | ValidationModules.Usage | Warning | A constraint on an indexer is never evaluated.
VM1203 | ValidationModules.Usage | Warning | [AllowedValues] sets Comparison on a member that is not a string, where it has no effect.
VM1303 | ValidationModules.Usage | Warning | Options or MatchTimeoutMilliseconds is set on the reference form of [Pattern], which reads neither.
VM1304 | ValidationModules.Usage | Warning | A match timeout on an inline [Pattern] or a [RegularExpression] is one the Regex constructor rejects; the attribute's default applies.
VM1505 | ValidationModules.Usage | Warning | A nested target from another assembly has no validator this compilation can call; the descent is dropped.
VM2010 | ValidationModules.Usage | Info | A class-level ValidationAttribute runs after the property rules pass and before IValidatableObject. Reported as Warning when its arguments cannot be rendered, and with an ignoring tail when ValidationModules_DataAnnotations is Ignore.
VM2011 | ValidationModules.Usage | Error | ErrorMessageResourceName on a compiled DataAnnotations attribute is not a static string property the generated validator can read.
VM3008 | ValidationModules.Usage | Error | A Pattern or Apply argument names no static method that generated code can call.
VM3009 | ValidationModules.Usage | Error | A generic fragment is called with a type argument its generated expansion cannot name: an anonymous type, or a private or protected type.
VM3106 | ValidationModules.Usage | Warning | A rules-class descent repeats [ValidateNested] on the same property; the rules-class descent is dropped.
VM3108 | ValidationModules.Usage | Error | A value given to AllowedValues in a rules class is not a compile-time constant.
VM3109 | ValidationModules.Usage | Warning | An allowed-values set lists no values, so it validates nothing.
VM3110 | ValidationModules.Usage | Error | As names the subject's own type, whose validator would call itself without end.
VM3111 | ValidationModules.Usage | Warning | A rules-class descent that passes no Polymorphism reaches a type that is not sealed, and runs only the validators for the declared type.
VM5004 | ValidationModules.Usage | Warning | A ValidationModules_* MSBuild property holds a value it does not accept. The generator uses the default.
