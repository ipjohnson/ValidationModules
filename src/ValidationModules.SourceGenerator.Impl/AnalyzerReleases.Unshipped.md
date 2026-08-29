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
VM0010 | ValidationModules.Usage | Info | A DataAnnotations constraint is ignored by ValidationModules because the front-end is set to Ignore; another validation system may still enforce it.
VM0016 | ValidationModules.Usage | Warning | RegexOptions.Compiled is ignored; patterns use [GeneratedRegex].
VM0021 | ValidationModules.Usage | Error | [MultipleOf] was applied to a member with no numeric type.
VM0022 | ValidationModules.Usage | Error | A [MultipleOf] divisor is zero or negative.
VM0023 | ValidationModules.Usage | Error | A [MultipleOf] divisor does not parse as the member's type.
VM0024 | ValidationModules.Usage | Error | [UniqueItems] was applied to a non-collection member.
VM0025 | ValidationModules.Usage | Warning | [UniqueItems] will compare elements by reference.
VM0026 | ValidationModules.Usage | Warning | [Range] declares neither bound and can never fail.
VM0017 | ValidationModules.Usage | Warning | An inline pattern roots the regex engine in an AOT-facing project.
VM0018 | ValidationModules.Usage | Error | A referenced regex member is missing, not static, inaccessible or not a Regex.
VM0040 | ValidationModules.Usage | Error | The referenced ValidationModules.Runtime is older than the emitted code requires.
VM0051 | ValidationModules.Usage | Warning | A constraint on a record parameter is missing the property: target.
VM0060 | ValidationModules.Usage | Info | A custom ValidationAttribute subclass is constructed once and invoked with DataAnnotations semantics. Reported as Warning when its arguments cannot be rendered, and as Info with an ignoring tail when ValidationModules_DataAnnotations is Ignore.
VM0061 | ValidationModules.Usage | Warning | A cross-field DataAnnotations attribute cannot be compiled.
VM0063 | ValidationModules.Usage | Info | A format DataAnnotations attribute is compiled with the BCL's exact semantics, stated in the message.
VM0064 | ValidationModules.Usage | Error | A length constraint was applied to a member that is neither string nor collection.
VM0065 | ValidationModules.Usage | Error | Range bounds do not parse as the member's type.
VM0067 | ValidationModules.Usage | Info | IValidatableObject.Validate is called after every other rule on the type passes, as Validator.TryValidateObject sequences it. Reported with an ignoring tail when ValidationModules_DataAnnotations is Ignore.
VM0070 | ValidationModules.Usage | Error | A statement in a Describe body is not transcribable.
VM0071 | ValidationModules.Usage | Error | A rule's value argument is not a member path on the subject parameter.
VM0075 | ValidationModules.Usage | Error | An Ensure has no inferable field and no explicit field name.
VM0027 | ValidationModules.Usage | Error | [EnumDefined] was applied to a member whose type is not an enum.
VM0030 | ValidationModules.Usage | Warning | A derived property hides a base declaration whose constraints are dropped.
VM0028 | ValidationModules.Usage | Error | A When/Unless condition names a member the validated type does not declare.
VM0029 | ValidationModules.Usage | Error | A When/Unless condition names a member that is not a predicate.
VM0033 | ValidationModules.Usage | Error | A constraint sets both When and Unless.
VM0031 | ValidationModules.Usage | Warning | A [ValidateNested] target is not sealed and declares no polymorphism mode.
VM0032 | ValidationModules.Usage | Error | Polymorphism.Runtime was applied to a sealed or value type.
VM0079 | ValidationModules.Usage | Error | A generic type cannot have a generated validator.
VM0080 | ValidationModules.Usage | Error | A [CustomValidation] target does not resolve to a callable public static ValidationResult method.
VM0081 | ValidationModules.Usage | Warning | Resource-based ErrorMessage resolution reflects at run time and may break under trimming.
VM0082 | ValidationModules.Usage | Error | A CustomConstraintAttribute subclass has no usable public static bool IsValid, or its parameters do not line up with the constructor.
VM0083 | ValidationModules.Usage | Error | An IConstraintFor<T> attribute cannot be compiled: no implemented instantiation accepts the member, several do, an argument is not renderable, or the class mixes custom shapes.
VM0084 | ValidationModules.Usage | Info | A [PerValidationInstance] constraint constructs a new attribute instance on every check.
VM0085 | ValidationModules.Usage | Error | A fragment target is compiled IL from a referenced assembly; fragments must be part of this compilation.
VM0086 | ValidationModules.Usage | Error | A fragment call chain returns to where it started.
VM0087 | ValidationModules.Usage | Error | The rules builder flows somewhere the generator cannot follow.
VM0088 | ValidationModules.Usage | Error | Transcribed code references a member that is not accessible from the companion file.
VM0089 | ValidationModules.Usage | Error | A rule declaration sits inside a loop, lambda, or local function.
VM0090 | ValidationModules.Usage | Error | Require on a non-nullable value type can never fail.
VM0091 | ValidationModules.Usage | Error | A facet validated with As declares no rules in this compilation.
VM0100 | ValidationModules.Usage | Error | A language pack file cannot be read.
VM0101 | ValidationModules.Usage | Warning | A language pack names an unknown shape key.
VM0102 | ValidationModules.Usage | Error | A language pack template hole exceeds the shape's arguments.
VM0103 | ValidationModules.Usage | Error | A language pack repeats a key.
VM0104 | ValidationModules.Usage | Warning | A language pack's file name and culture disagree.
VM0105 | ValidationModules.Usage | Info | Language pack coverage.
