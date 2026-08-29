# Language packs: translations as build-time data

**Written:** 2026-08-29
**Status:** accepted; first cut on `feat/language-packs`
**Builds on:** docs/structured-errors.md (the message is data until read)
**Contract:** RuntimeContract 9 → 10

## The design in one paragraph

A language pack is a JSON file of `key → template`, compiled by the source generator into a
static entry array and registered through the same per-assembly extension every
validator uses. Keys are contracts (the wire code, plus a shape key beneath the four codes that
render more than one sentence); values are templates rendered by the hole-filler that already
exists. Because packs are data compiled at the consumer's build, the generator validates them
there — unknown shape keys, holes that exceed a shape's arity, coverage gaps — which no resx or
runtime-loaded catalog can do. Translations become the fourth thing this library checks at build
time.

## Keys, not wording

Two levels, both stable, neither of them text:

- **Code** (`required`, `multiple_of`, user codes like `date_order`) — the wire contract, one
  sentence shape each for twelve of the sixteen built-ins.
- **Shape key** (`string_length.at_most`, `range.greater_and_less`, `enum.denied`) — beneath the
  four codes whose sentence varies with the arguments. Derived at render time from the template
  *reference* the info already holds (`ValidationMessageTemplates.KeyOf`), never from its text —
  rewording an English default breaks nothing.

The full inventory is 34 keys. A shape's argument list is part of its contract: changing what
`{0}` means for a key is a new key, never a mutation — the same additive-only rule the emitted
surface lives by. New shapes render the English default until a pack adds them: visible,
per-shape, never wrong-language.

| key family | keys | args |
|---|---|---|
| `required`, `unique_items`, `pattern`, `email`, `phone`, `url`, `credit_card`, `base64`, `custom` | 9 | — |
| `multiple_of`, `enum`, `enum.denied`, `enum.flags`, `file_extension` | 5 | `{0}` |
| `string_length.*`, `array_bounds.*` (between / at_most / at_least, each ×singular) | 12 | `{0}` (+`{1}` for between) |
| `range.*` (between, greater_and_at_most, at_least_and_less, greater_and_less, at_least, greater_than, at_most, less_than) | 8 | `{0}` (+`{1}` two-bounded) |

## The file

```json
{
    "culture": "fr",
    "templates": {
        "required": "{field} est obligatoire.",
        "string_length.at_most": "{field} doit contenir au plus {0} caractères.",
        "date_order": "la date de fin doit suivre la date de début."
    }
}
```

- File name: `*.validation-messages.json`, globbed into `AdditionalFiles` by the package targets.
  The culture lives in the body — explicit over filename magic; a filename/body mismatch is worth
  a warning, not a convention.
- JSON, not CSV: translated templates are full of commas, and comma-bearing text round-tripping
  Excel's quoting is how packs get corrupted. The parser is ~a hundred dependency-free lines in
  Impl, because a generator that references System.Text.Json inherits the compiler host's version
  conflicts.
- Unknown bare keys are user codes and compile silently — `date_order` is the point, not a typo.
  The typo heuristic fires only on dotted keys whose prefix is a known code but whose whole is
  not (`string_length.atmost`), where a user code is implausible and a misspelled shape key is
  near-certain.

## What the generator checks (the reason this design exists)

| ID | Severity | |
|---|---|---|
| VM0100 | Error | the file does not parse, or has no `culture` |
| VM0101 | Warning | dotted key under a known code that names no known shape — with the nearest match |
| VM0102 | Error | a hole exceeds the shape's argument contract — the classic runtime `FormatException`, caught at build |
| VM0103 | Error | the same key twice in one file |
| VM0104 | Warning | the file name's culture disagrees with the body's |
| VM0105 | Info | coverage: "fr covers 31/34 shapes; missing: …" — drift made visible at the build it affects |

## Storage: a static entry array, merged once - decided by benchmark

Each file becomes a sealed class carrying one `static readonly KeyValuePair<string, string>[]` -
pure data, literals the string heap deduplicates across every pack in the assembly, trimmed to
nothing when unregistered. Packs answer no lookups; the formatter folds every registered pack
into **one ordinary `Dictionary` per requested culture**, built lazily on that culture's first
render.

`benchmarks/…/Design/LanguagePackStorageBenchmarks.cs` is the evidence (JIT quick job, per-probe,
8 hits + 2 misses):

| entries | if-chain | switch | Dictionary | FrozenDictionary |
|---:|---:|---:|---:|---:|
| 35 | 10.3 ns | 3.8 ns | 4.5 ns | 3.6 ns |
| 150 | 50.8 ns | 8.4 ns | 4.5 ns | 5.8 ns |
| 400 | 145.8 ns | 8.5 ns | 5.0 ns | 5.2 ns |

The first cut's per-pack lookup chain degrades linearly and was retired. `FrozenDictionary` loses
to `Dictionary` here: lookups within noise at pack sizes, construction 3-16× dearer (773 ns vs
257 ns at 35 entries; ~58 µs vs 3.6 µs at 400). And the serialized-bytes alternative - a blob
parsed into a dictionary on demand - is the same dictionary build plus UTF-8 decoding: 2.1 µs and
8.8 KB at 35 entries against 257 ns and 1.1 KB from the static array, at every size, for a binary
delta that is a wash (UTF-8 beats UTF-16 for Latin scripts, loses for CJK, and duplicates key
strings per pack where literals share them). The literals are the serialized form; `ldstr` is the
deserializer, and the runtime maintains it.

Compression was considered earlier and declined on the same arithmetic (deflate saves ~1 KB per
pack; rooting a decompressor under Native AOT costs tens). Because authoring is a data file and
storage is generated, the encoding can still change later without touching pack authors or the
API - that decoupling is the point of generating.

## Selection and layering at run time

`LanguagePackFormatter` takes `IEnumerable<IValidationLanguagePack>` - the container-wide set, so
composition is per process, never per assembly - and builds one merged table per requested
culture on first use (`ConcurrentDictionary.GetOrAdd`; the first-use race is benign, same posture
as the nested-validator arrays). The fold:

1. the culture's parent chain is folded in, parent-most first, so an `fr-CA` table layers `fr-CA`
   packs over `fr` packs and a render costs at most two probes - shape key, then code - ever;
2. within the fold, later registration is a higher layer, and registration order across
   assemblies is the order the composition root called the `Add*` methods - the same precedence
   knob validators already have;
3. per render, the shape key beats the code only within a layer: a later pack that rewords a
   whole code takes all of its shapes, which is what lets a one-entry override reword one message
   and a wholesale pack reword a family. Measured: the merged read is 1.4 ns against 7.4 ns for
   walking packs per render - and it stops scaling with pack count entirely.

Tables cost ~257 ns and ~1.1 KB per 35 entries, once per culture actually rendered; request
cultures are bounded by localization middleware's supported list. Errors without a `MessageInfo`
match at the code level and render `{field}`-only templates.

MSBuild evaluation order makes the override rule fall out naturally: package `.props` items are
added before project items, so **app-local files land later and win** — the person closest to the
user has the last word, with no configuration.

## Distribution: one feature, three provenances

The generator compiles whatever `*.validation-messages.json` reaches `AdditionalFiles`; where the
file came from is invisible to it.

- **In-app** — drop `fr.validation-messages.json` in the project; `Add<Assembly>Validators()`
  registers the pack alongside the validators. The end-user story is one file and the call the
  app already makes.
- **Soft publish (the recommended sharing shape)** — a package ships the JSON plus a three-line
  `build/*.props` adding it to the consumer's `AdditionalFiles`. The consumer's own generator
  compiles it, so validation and the VM0105 coverage check run against the *consumer's* template
  inventory — drift is visible to the person it affects, not the person who caused it. No extra
  assemblies in the deployment; trimming is trivially exact. A library localizing *its own* codes
  ships the same thing under `buildTransitive/` so its messages reach every downstream app.
- **Compiled pack assembly** — mode one running in the pack author's repo, shipped as a dll. Kept
  for frozen, reviewed artifacts and for Runtime-only consumers who don't run the generator;
  documented, not recommended.

## All-in-one, opt-out in the csproj

The bundled offering is one `ValidationModules.Messages` package carrying every language as data
files, filtered by an MSBuild property before the generator ever sees them:

```xml
<ValidationModulesLanguages>fr;de</ValidationModulesLanguages>   <!-- unset = all; 'none' = off -->
```

The props include all files when the property is unset, or transforms the semicolon list into
per-culture includes otherwise. Opt-out therefore means *not compiled* — the unwanted languages
are absent from the binary, not carried and ignored — which is the same tier of guarantee as
`ValidationModules_CaptureValues`. One package to reference, one property to trim it, nothing to
configure for the common case.

## Wiring

Registration folds into the existing story: packs register as `IValidationLanguagePack`
singletons inside `Add<Assembly>Validators()` (inert until something resolves a formatter), and
the same method `TryAdd`s a `ValidationMessageFormatter` factory over the registered packs — TryAdd
so an app's hand-built formatter wins. The ASP.NET Core filter falls back to the container's
formatter when `ValidationProblemOptions.MessageFormatter` is unset, so the end-to-end story is:
drop in a file, call the method you already call, and the `errors` object localizes per request
culture while `validationCodes` stays exactly what it was.

A pack-only assembly — zero validated types, five languages — still emits its registration
method; language packs are a legitimate reason for the extension to exist.

## Contract 9 → 10

Generated registrations reference `IValidationLanguagePack`, `LanguagePackFormatter` and the
generated pack classes implement the interface — none of which a contract-9 runtime declares.
Additive.

## Reference packs

`fr`, `es`, `de`, `zh`, `ja` ship as the worked examples (CJK collapses the singular/plural
variants to one string, which the key inventory makes a non-event). They are reference-grade
translations pending native-speaker review — the community-pack model from the Humanizer
playbook is the intended steady state, and the coverage diagnostic is what keeps those honest.
