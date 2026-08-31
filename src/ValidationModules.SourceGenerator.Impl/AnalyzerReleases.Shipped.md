; Shipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VM1001 | ValidationModules.Usage | Error | A string constraint was applied to a non-string member.
VM1002 | ValidationModules.Usage | Error | [ItemCount] was applied to a non-collection member.
VM1003 | ValidationModules.Usage | Error | [Range] was applied to a member with no ordering.
VM1004 | ValidationModules.Usage | Error | [MultipleOf] was applied to a member with no numeric type.
VM1005 | ValidationModules.Usage | Error | [UniqueItems] was applied to a non-collection member.
VM1006 | ValidationModules.Usage | Error | [EnumDefined] was applied to a member whose type is not an enum.
VM1007 | ValidationModules.Usage | Error | A constrained property has no accessible getter.
VM1008 | ValidationModules.Usage | Warning | A constraint on a record parameter is missing the property: target.
VM1009 | ValidationModules.Usage | Warning | A derived property hides a base declaration whose constraints are dropped.
VM1010 | ValidationModules.Usage | Error | A generic type cannot have a generated validator.
VM1101 | ValidationModules.Usage | Error | A constraint's lower bound exceeds its upper bound.
VM1102 | ValidationModules.Usage | Warning | [Range] declares neither bound and can never fail.
VM1103 | ValidationModules.Usage | Error | Range bounds do not parse as the member's type.
VM1104 | ValidationModules.Usage | Error | A [MultipleOf] divisor is zero or negative.
VM1105 | ValidationModules.Usage | Error | A [MultipleOf] divisor does not parse as the member's type.
VM1106 | ValidationModules.Usage | Error | A pattern is not a valid regular expression.
VM1107 | ValidationModules.Usage | Error | A referenced regex member is missing, not static, inaccessible or not a Regex.
VM1201 | ValidationModules.Usage | Warning | [Required] on a non-nullable value type can never fail.
VM1202 | ValidationModules.Usage | Warning | [UniqueItems] will compare elements by reference.
VM1301 | ValidationModules.Usage | Warning | An inline pattern roots the regex engine in an AOT-facing project.
VM1302 | ValidationModules.Usage | Warning | RegexOptions.Compiled is ignored; patterns use [GeneratedRegex].
VM1401 | ValidationModules.Usage | Error | A When/Unless condition names a member the validated type does not declare.
VM1402 | ValidationModules.Usage | Error | A When/Unless condition names a member that is not a predicate.
VM1403 | ValidationModules.Usage | Error | A constraint sets both When and Unless.
VM1501 | ValidationModules.Usage | Warning | A [ValidateNested] target declares no rules.
VM1502 | ValidationModules.Usage | Warning | A [ValidateNested] target can never have a generated validator; the descent is dropped.
VM1503 | ValidationModules.Usage | Warning | A [ValidateNested] target is not sealed and declares no polymorphism mode.
VM1504 | ValidationModules.Usage | Error | Polymorphism.Runtime was applied to a sealed or value type.
VM1601 | ValidationModules.Usage | Error | A CustomConstraintAttribute subclass has no usable public static bool IsValid, or its parameters do not line up with the constructor.
VM1602 | ValidationModules.Usage | Error | An IConstraintFor<T> attribute cannot be compiled: no implemented instantiation accepts the member, several do, an argument is not renderable, or the class mixes custom shapes.
VM1603 | ValidationModules.Usage | Info | A [PerValidationInstance] constraint constructs a new attribute instance on every check.
VM2001 | ValidationModules.Usage | Info | A DataAnnotations constraint is ignored by ValidationModules because the front-end is set to Ignore; another validation system may still enforce it.
VM2002 | ValidationModules.Usage | Info | A custom ValidationAttribute subclass is constructed once and invoked with DataAnnotations semantics. Reported as Warning when its arguments cannot be rendered, and as Info with an ignoring tail when ValidationModules_DataAnnotations is Ignore.
VM2003 | ValidationModules.Usage | Warning | A cross-field DataAnnotations attribute cannot be compiled.
VM2004 | ValidationModules.Usage | Info | A format DataAnnotations attribute is compiled with the BCL's exact semantics, stated in the message.
VM2005 | ValidationModules.Usage | Error | A length constraint was applied to a member that is neither string nor collection.
VM2006 | ValidationModules.Usage | Info | IValidatableObject.Validate is called after every other rule on the type passes, as Validator.TryValidateObject sequences it. Reported with an ignoring tail when ValidationModules_DataAnnotations is Ignore.
VM2007 | ValidationModules.Usage | Warning | [EnumDataType] checks a runtime string conversion and is not compiled.
VM2008 | ValidationModules.Usage | Error | A [CustomValidation] target does not resolve to a callable public static ValidationResult method.
VM2009 | ValidationModules.Usage | Warning | Resource-based ErrorMessage resolution reflects at run time and may break under trimming.
VM3001 | ValidationModules.Usage | Error | A statement in a Describe body is not transcribable.
VM3002 | ValidationModules.Usage | Error | The rules builder flows somewhere the generator cannot follow.
VM3003 | ValidationModules.Usage | Error | A rule declaration sits inside a loop, lambda, or local function.
VM3004 | ValidationModules.Usage | Error | Transcribed code references a member that is not accessible from the companion file.
VM3005 | ValidationModules.Usage | Error | A fragment target is compiled IL from a referenced assembly; fragments must be part of this compilation.
VM3006 | ValidationModules.Usage | Error | A fragment call chain returns to where it started.
VM3007 | ValidationModules.Usage | Error | A rule's value argument is not a member path on the subject parameter.
VM3101 | ValidationModules.Usage | Error | Require on a non-nullable value type can never fail.
VM3102 | ValidationModules.Usage | Error | An Ensure has no inferable field and no explicit field name.
VM3103 | ValidationModules.Usage | Info | An Ensure derived its code from its condition; the message states the code.
VM3104 | ValidationModules.Usage | Warning | A rule value unwraps a nullable member with .Value; the rule takes the nullable directly.
VM3105 | ValidationModules.Usage | Error | A facet validated with As declares no rules in this compilation.
VM4001 | ValidationModules.Usage | Error | A language pack file cannot be read.
VM4002 | ValidationModules.Usage | Warning | A language pack names an unknown shape key.
VM4003 | ValidationModules.Usage | Error | A language pack template hole exceeds the shape's arguments.
VM4004 | ValidationModules.Usage | Error | A language pack repeats a key.
VM4005 | ValidationModules.Usage | Warning | A language pack's file name and culture disagree.
VM4006 | ValidationModules.Usage | Info | Language pack coverage.
VM5001 | ValidationModules.Usage | Error | The referenced ValidationModules.Runtime is older than the emitted code requires.
VM5002 | ValidationModules.Usage | Error | An emit stage threw an unhandled exception; the build fails rather than succeeding with generated source missing.
VM5003 | ValidationModules.Usage | Warning | Validate&lt;T&gt;() names a type this compilation declares and generates no validator for.
