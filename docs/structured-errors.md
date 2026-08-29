# Structured errors: the message is data until someone reads it

**Written:** 2026-08-29
**Status:** accepted; implemented on `feat/structured-errors`
**Contract:** RuntimeContract 8 → 9

## The change in one paragraph

`ValidationError` stops storing a composed message and starts storing the ingredients: the field,
the code, the attempted value, and a reference to a `ValidationMessageInfo` — the constraint's
message template and its arguments, emitted once as a `static readonly` per constraint site.
`Message` renders on read. A result set is culture-free until something reads it, so the same
`ValidationResult` can render English in a log line, French in a 400 body, and verbose-with-values
in a dev tool — and a client that wants to do its own rendering gets the code and the arguments
instead of parsing English.

This is the structured-logging shape (`LoggerMessage`: template + args, rendered at the sink),
applied to validation. It supersedes an eager-formatter design considered first: eager formatting
could only translate at validation time, and it required threading a formatter through the
collector; late binding needs no per-pass plumbing at all — translation is entirely a reader
concern.

## The shape

```csharp
public readonly record struct ValidationError {
    public string Field { get; init; }
    public string Code { get; init; }
    public object? Value { get; init; }              // attempted value; see redaction policy
    public ValidationSeverity Severity { get; init; }
    public ValidationMessageInfo? MessageInfo { get; init; }

    public string Message { get; }                   // _message ?? MessageInfo.RenderFor(this) ?? ""

    public ValidationError(string field, string code, string message);                       // compat
    public ValidationError(string field, string code, object? value, ValidationMessageInfo messageInfo);
}

public sealed class ValidationMessageInfo {
    public ValidationMessageInfo(string template, params object[]? args);
    public string Template { get; }                  // holes: {field}, {0}, {1}…
    public IReadOnlyList<object> Args { get; }       // constraint parameters, boxed once
    public IValidationMessageProvider? Provider { get; init; }  // owns the template per render (resx)
    public bool DataAnnotationsHoles { get; init; }  // {0} is the field, {1}… are args — DA's dialect
}

public interface IValidationMessageProvider {
    string Template(in ValidationError error);       // read per render, so culture fallback works
}

public abstract class ValidationMessageFormatter {
    public abstract string Format(in ValidationError error);
}
```

- **The compat constructor survives.** `new ValidationError(field, code, message)` stores the text
  (`MessageInfo` null) — Hardened's source compatibility, the FluentValidation adapter, and every
  hand-written `Report(field, code, message)` keep working, and those errors simply aren't
  translatable, visibly (`MessageInfo == null`).
- **`ValidationMessageInfo` is a class, not a struct.** The point is that one instance is shared:
  emitted as `static readonly` per constraint site (per-site because the args differ; deduplicated
  within a validator class), or as a runtime singleton for the parameterless kinds
  (`ValidationMessageInfo.Required`, `.Pattern`, `.Email`, …), where the per-site cost is zero.
- **Templates live in the runtime** as `static readonly string` fields
  (`ValidationMessageTemplates`), *not* `const` — a const would be inlined into consumer IL, which
  is exactly the per-site string duplication the Report* helpers were built to avoid. Emitted infos
  reference the fields, so each template exists once, in this assembly.
- **All shape conditionality resolves before render.** Today's `BoundsMessage` picks
  between/at-least/at-most and singular/plural at compose time; under this design the *emitter*
  picks the exact template variant at build time (the bounds are constants), and the runtime
  helpers pick it at failure time for hand-written calls. `Render` itself is a dumb hole-filler:
  `{field}`, `{0}`…`{9}`, args formatted with `CultureInfo.InvariantCulture` — so the default
  render is byte-identical to the current composed strings, which the existing message-pinning
  tests prove.

## Rendering and translation

- `error.Message` — the default render: template (or `Provider.Template`) + args, invariant,
  **never includes `Value`**. This is what the problem-details writer reads today and continues to
  read; behavior is unchanged for anyone who changes nothing.
- `error.ToMessage(formatter)` / a formatter applied at a boundary — the override layer. A
  formatter sees the whole error (field, code, value, args) and decides text and permissiveness.
- `ValidationMessageMap : ValidationMessageFormatter` — the shipped mapper: code → delegate, with
  per-code fallback to the default render. Thirty lines of C# is a language; user-defined codes
  (`Ensure(code: "date_order")`, custom constraints) localize with the same gesture as built-ins.
  Culture is ambient (`CurrentUICulture`) or the formatter's own business — deliberately not
  stored on the error or the collector, which is what keeps pooled collectors and cross-thread
  reads correct.

## Redaction policy (decided, not incidental)

Prior art: FluentValidation and MVC ModelState capture attempted values by default and render them
nowhere by default; DataAnnotations never captures; EF Core (`EnableSensitiveDataLogging`) and
Microsoft.IdentityModel (`ShowPII`) gate sensitive *rendering* behind loud opt-ins. This library
takes the layered version, with one guarantee only a source generator can make:

1. **Captured by default.** `Value` is a reference to data the application already holds.
2. **No library surface renders it.** The default render, `ValidationError.ToString()` (overridden
   — the synthesized record printer would have leaked it), `ValidationException.Message` (already
   field+code only), and the problem-details writer never emit `Value`. Only an installed
   formatter can choose to — an explicit reopening point, same stance as the reporter tier.
3. **`ValidationModules_CaptureValues=false`** makes the emitter pass `null` instead of the member:
   the value is provably absent from the compiled binary, a guarantee no runtime flag can offer.
4. Serializing `ValidationError` directly with your own serializer is yours to police — the
   library's own JSON context does not carry `Value`.

## Equality, ToString, size

- Record-struct field equality is kept. Two failures from the same site are equal (same static
  info; boxed primitive `Value`s compare by value through `EqualityComparer<object>`). The
  attribute-vs-rule-class duplicate pair that used to compare equal now differs by `MessageInfo`
  reference — the test pinning that flips, deliberately: they *are* different rule sites.
- `ToString()` → `field: code - message`. Stable, value-free.
- The struct grows ~32 → ~48 bytes; failure-path only. The failure path stops composing strings
  entirely (allocations: the error node, plus one box when a value type fails while capture is on);
  the ~56 B/error message cost moves to first read. Repeated `Message` reads re-render — a
  readonly struct cannot cache — which is fine for the read-once shapes (serialize, log) and
  documented for the rest.

## What this folds in from the rc1012 evaluation backlog

- **B-03** `{field}` in `Message` overrides: substituted by the emitter at generation time (the
  field is a literal at every attribute site), matching the XML-doc contract;
  `IConstraintFor`'s default `Validate` keeps its runtime `Replace` (shared instance, field varies).
- **B-04** `[DeniedValues]`: new template + `ReportDeniedValues` helper — "{field} must not be one
  of: …". Code stays `enum` (wire compat).
- **B-05** DataAnnotations `ErrorMessage` composite templates: every argument except the display
  name is a build-time constant, so the reader bakes `{1}`+ in and rewrites `{0}` → `{field}`.
- **B-06** `ErrorMessageResourceType/Name` on mapped attributes: compiled to a generated
  `IValidationMessageProvider` reading the resx accessor property per render — culture fallback
  and satellite assemblies work, zero reflection, trim-safe (a direct property reference roots the
  resource class). `DataAnnotationsHoles` renders `{0}` as the field. VM0081 narrows to the
  invoked-custom path, where reflection genuinely remains.
- **B-19** exclusive `[Range]` bounds finally say so: exclusive min → "must be greater than",
  exclusive max → "must be less than" ("{field} must be greater than {0} and at most {1}." for the
  mixed case). Message-only change; `range` code unchanged.

## What bypasses, stated up front

Finished-string producers stay finished strings: the FluentValidation adapter, DataAnnotations'
invoked user code (`IValidatableObject`, custom `ValidationAttribute`s, `[CustomValidation]`), and
hand-written `Report(field, code, message)`. They carry `MessageInfo == null` and do not translate.
`Ensure`'s rendered-condition messages are compile-time source and stay literal; `code:` is the
route into the map, as documented.

## Contract 8 → 9

The emitter now writes `ctx.Report(field, code, value, info)` calls, references
`ValidationMessageTemplates` / `ValidationMessageInfo` singletons, and emits per-site static infos —
none of which a contract-8 runtime declares. Not additive-overall:
`IValidationContextReporter` gains the structured `Report` overload (a real member, not a DIM — a
default interface member invoked through the constrained generic would box the context struct),
and `ValidationError`'s positional record shape becomes explicit constructors. Both are the kind of
break rc windows exist for; the compat constructor and `(field, code, message)` call sites are
unaffected.
