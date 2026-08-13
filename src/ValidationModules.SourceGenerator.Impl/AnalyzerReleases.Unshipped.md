; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VM0001 | ValidationModules.Usage | Error | A string constraint was applied to a non-string member.
VM0002 | ValidationModules.Usage | Error | [ItemCount] was applied to a non-collection member.
VM0003 | ValidationModules.Usage | Error | [Range] was applied to a member with no ordering.
VM0004 | ValidationModules.Usage | Warning | [Required] on a non-nullable value type can never fail.
VM0006 | ValidationModules.Usage | Error | A pattern is not a valid regular expression.
VM0007 | ValidationModules.Usage | Warning | A [ValidateNested] target declares no rules.
VM0008 | ValidationModules.Usage | Error | A constraint's lower bound exceeds its upper bound.
VM0009 | ValidationModules.Usage | Error | A constrained property has no accessible getter.
VM0010 | ValidationModules.Usage | Warning | A DataAnnotations constraint was skipped because the front-end is off.
VM0016 | ValidationModules.Usage | Warning | RegexOptions.Compiled is ignored; patterns use [GeneratedRegex].
VM0017 | ValidationModules.Usage | Warning | An inline pattern roots the regex engine in an AOT-facing project.
VM0018 | ValidationModules.Usage | Error | A referenced regex member is missing, not static, inaccessible or not a Regex.
VM0040 | ValidationModules.Usage | Error | The referenced ValidationModules.Runtime is older than the emitted code requires.
VM0051 | ValidationModules.Usage | Warning | A constraint on a record parameter is missing the property: target.
VM0060 | ValidationModules.Usage | Warning | A custom ValidationAttribute subclass cannot be compiled.
VM0061 | ValidationModules.Usage | Warning | A cross-field DataAnnotations attribute cannot be compiled.
VM0063 | ValidationModules.Usage | Warning | A format DataAnnotations attribute is not compiled.
VM0064 | ValidationModules.Usage | Error | A length constraint was applied to a member that is neither string nor collection.
VM0065 | ValidationModules.Usage | Error | Range bounds do not parse as the member's type.
VM0067 | ValidationModules.Usage | Warning | IValidatableObject is not called by the generated validator.
VM0070 | ValidationModules.Usage | Error | A statement in Describe is not a rule declaration.
VM0071 | ValidationModules.Usage | Error | A rule selector is not a simple property path.
VM0072 | ValidationModules.Usage | Error | A predicate references state outside its own parameter.
VM0075 | ValidationModules.Usage | Error | An Ensure has no inferable field and no explicit field name.
