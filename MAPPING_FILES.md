# Writing Java → C# Mapping Files

This guide is the single source of truth for authoring **member-level API mapping
files** — YAML files that tell the JavaToCSharp converter how to translate Java
API calls (classes, methods, fields) into their C# equivalents.

- **What it covers:** the YAML schema, every field, resolution/chain rules,
  validation and conflict behavior, and step-by-step instructions for writing
  and testing your own mapping files.
- **What it replaces:** the former `java-to-csharp-mapping-spec.md` and
  `java-to-csharp-implementation-spec.md`.

---

## 1. What mapping files do

After basic syntax translation has happened, a mapping file swaps Java
**identifiers** for C# ones — member and type names become their C#
equivalents. Scope is deliberately narrow:

- **Word/identifier replacement only.** The converter does not reason about
  staticness, access modifiers, or any other code-shape change.
- **No structural/statement-level transforms** (try-with-resources, streams →
  LINQ, etc.) are performed.
- **No fully-qualified names in output.** `csharp_class` / `csharp_member`
  values are always the bare identifier as it appears in code (`List`, `Pow`,
  `Console`) — never a namespace-qualified path. Namespace resolution happens
  entirely through the `namespaces` table (see §4), not by qualifying the
  identifier inline.

A mapping file has exactly two top-level lists:

1. **`namespaces`** — package-to-package conversions, which drive `using`
   injection.
2. **`class_map`** — everything about a class: its own rename, plus its
   methods/fields, which can themselves route to further nested `class_map`s.

There is no file-level metadata (version, source/target language) — just these
two lists.

---

## 2. Using a mapping file

### CLI

`--mapping-file` is repeatable and can be passed multiple times; all files are
loaded into one index. Like the other value-taking root options it must come
**before** the subcommand:

```
JavaToCSharpCli --mapping-file java-mappings.yaml --mapping-file extras.yaml file Foo.java
```

### GUI

Add mapping files under the **Mappings** tab in Settings. They apply to every
conversion and are persisted.

### Library

```csharp
options.MappingIndex = TypeMappingIndex.LoadFromFiles(["java-mappings.yaml", "extras.yaml"]);
// or, from a raw string:
var index = TypeMappingIndex.LoadFromYaml(yaml);
// or, built programmatically (see §15):
var index = new TypeMappingBuilder().AddClass(...).Build();
```

---

## 3. The YAML schema

### 3.1 `namespaces[]`

Maps Java packages to the C# namespaces/`using`s they produce.

| field | required | type | meaning |
|---|---|---|---|
| `java_packages` | yes | list | one or more Java package names that map to the same C# result, e.g. `[java.util.concurrent, java.util.concurrent.atomic]` |
| `csharp_packages` | yes | list | one or more C# namespaces/`using`s this produces, e.g. `[System.Threading]` |

Both sides are always lists because the relationship is many-to-many: several
Java packages can collapse into one C# namespace, and a single Java package can
require multiple `using`s. There is **no `requires_using` field anywhere** —
wherever a `using` is needed, it comes from `csharp_packages` here.

```yaml
namespaces:
  - java_packages: [java.lang]
    csharp_packages: [System]
  - java_packages: [java.util]
    csharp_packages: [System.Collections.Generic]
  - java_packages: [java.util.concurrent, java.util.concurrent.atomic]
    csharp_packages: [System.Threading]
```

### 3.2 `class_map[]`

One entry describes one Java class/interface and everything about it: its own
C# equivalent, plus optionally its methods/fields.

| field | required | type | meaning |
|---|---|---|---|
| `java_class` | yes | string | fully-qualified Java class/interface name — the match key |
| `csharp_class` | yes | string | bare C# identifier, e.g. `List`, `Math`, `Console` |
| `generic` | no | bool | `true` if the type takes type parameters (e.g. `List<T>`) |
| `kind` | no | string | `class` (default) \| `interface` \| `exception` |
| `entries` | no | list | method/field mappings belonging to this class — omit for a pure class rename |

A pure type rename (no members touched):

```yaml
class_map:
  - java_class: java.util.ArrayList
    csharp_class: List
    generic: true
```

### 3.3 `class_map[].entries[]`

One flat object per Java member.

| field | required | type | meaning |
|---|---|---|---|
| `java_member` | yes | string | Java method/field name, no parens |
| `member_type` | no | string | `method` (default) \| `field` |
| `arg_count` | methods | int or `variadic` | number of declared Java arguments |
| `kind` | yes | string | `rename` \| `property` \| `template` \| `unsupported` |
| `csharp_member` | for `rename`/`property` | string | bare C# member name. Optional fallback value even when `class_map` is present (see §7) |
| `expression` | for `template` | string | placeholder expression, see §6 |
| `type_hint` | no | list | Java param types, e.g. `[int, int]` — tiebreaker only, see §8 |
| `confidence` | no | string | `high` (default) \| `medium` \| `low` |
| `notes` | no | string | free text — semantic caveats, emitted as a code comment |
| `class_map` | no | list | a nested `class_map` (same shape as the top-level one) describing further members reachable off this one — this is what makes `X.Y.Z()` chains resolvable |

---

## 4. How `using`s are derived

Every `class_map` entry's `java_class` is fully qualified (`java.lang.Math`).
The converter takes everything before the last `.` (`java.lang`) and looks it
up against `namespaces[].java_packages` to get the `using`(s).

- Nested `class_map` entries are resolved the same way, independently, off
  their own `java_class` — there is no inheritance of the parent's `using`s.
- **Missing coverage is a warning, not silence.** If no `namespaces` entry
  covers a `java_class`'s package, the converter warns because the mapping
  file is incomplete and the emitted code will not compile without a manual
  `using`.
- Duplicate `using`s collapse automatically (emitted block is sorted
  alphabetically), so multiple `class_map` entries may legitimately share the
  same `csharp_packages` — that's expected, not an error.

---

## 5. The `kind` vocabulary

| kind | when to use | example |
|---|---|---|
| `rename` | Pure word swap — same call shape, just a different name | `Math.pow(a, b)` → `Math.Pow(a, b)` |
| `property` | Java method call becomes a C# property access (drop the parens) | `str.length()` → `str.Length` |
| `template` | Anything needing arg reshaping, reordering, or a fuller expression swap | `str.substring(a, b)` → `str.Substring(a, b - a)` |
| `unsupported` | No safe automatic mapping — converter flags it for manual review and emits a `// TODO` comment | |

```yaml
- java_member: length
  arg_count: 0
  kind: property
  csharp_member: Length

- java_member: intern
  arg_count: 0
  kind: unsupported
  notes: "No safe C# equivalent for string interning via this API."
```

---

## 6. Template placeholders

Used in `expression` when `kind: template`:

| placeholder | meaning |
|---|---|
| `$0` | the receiver / target object the method is called on. Empty for static calls. |
| `$1`, `$2`, `$3`, … | positional Java arguments, 1-indexed, in declared order |
| `$rest` | (variadic only) all remaining arguments from the first non-fixed position onward, passed through as-is |

```yaml
- java_member: substring
  arg_count: 2
  kind: template
  expression: "$0.Substring($1, $2 - $1)"
  notes: "Java's endIndex becomes a length in C#."
```

---

## 7. Nested `class_map`: chained access

Some Java chains don't resolve one segment at a time to an equally-shaped C#
chain — `System.out.println(x)` → `Console.WriteLine(x)` collapses three
segments into two, because `out` has no standalone C# equivalent worth naming;
it only exists to route to `println`. An `entries[]` item can carry its own
nested `class_map` to describe exactly that:

```yaml
class_map:
  - java_class: java.lang.System
    csharp_class: Console
    entries:
      - java_member: out
        member_type: field
        kind: rename
        class_map:
          - java_class: java.io.PrintStream
            csharp_class: Console
            entries:
              - java_member: println
                arg_count: 1
                kind: rename
                csharp_member: WriteLine
              - java_member: print
                arg_count: 1
                kind: rename
                csharp_member: Write
```

How a chain is resolved, segment by segment:

1. Match the first segment against a root `class_map` entry by `java_class`
   (exact fully-qualified match if the parser resolved the receiver's type;
   unambiguous simple-name match otherwise — see §9).
2. Consume the next segment against that class's `entries[]` by
   `java_member` (and `arg_count` if it's immediately called).
3. If the matched entry has a nested `class_map`:
   - **Do not emit anything for this segment.** It is a routing node, not a
     leaf. Descend into the nested `class_map` and repeat step 2 against the
     next segment.
   - Exception: if the chain *ends* at this segment (the routing node is used
     standalone) and the entry has a `csharp_member` set, emit
     `csharp_class.csharp_member` using the **current** level's
     `csharp_class` as a fallback. If no `csharp_member` is set on a routing
     node used standalone, treat as `unsupported`.
4. If the matched entry has no nested `class_map` (a leaf), resolution stops
   there. The emitted substitution is `<csharp_class of the class_map level
   containing this entry>.<output of this entry per its kind>` — a plain
   member name for `rename`, a property access with no parens for `property`,
   or the filled-in `expression` for `template`.
5. Any source segments *after* a resolved leaf are left untouched by this
   mapping step — a leaf's own further chaining is handled by whatever mapping
   (if any) applies to that intermediate type.

**Worked trace** for `System.out.println(x)`:
`System` → root match (`csharp_class: Console`) → `out` → entry found, has
nested `class_map`, so **elide** `out` → nested class `PrintStream`
(`csharp_class: Console`) → `println` → leaf, `kind: rename`,
`csharp_member: WriteLine` → emit `Console.WriteLine(x)`.

**Registration gotcha:** a nested `class_map` under an `entries[]` item is
only reachable by *chained access* starting from its enclosing class — it is
**not** registered as a standalone class. If the source also uses the nested
type as a type name (e.g. `java.util.Map.Entry<String,Integer>`), you must
add it as its own **top-level** `class_map` entry with the fully-qualified
`java_class` (e.g. `java.util.Map.Entry`). Only entry-level `class_map`
routing is exposed to the type-name index; a `class_map` nested under
`entries[]` is invisible to it.

---

## 8. Overloads: `arg_count` and `type_hint`

The lookup key is `(java_class, java_member, arg_count)`. If two entries share
the same key (a real same-arity, different-type overload with genuinely
different C# targets), disambiguate with `type_hint`:

1. If the parser knows the argument types **and** a `type_hint`-matching
   entry exists, use it.
2. Otherwise, use the entry with no `type_hint` at that key (the default).
3. If multiple entries exist at the same key and none is type-free, treat
   this as **ambiguous** — fall back to `unsupported` behavior rather than
   guess.

If you never expect a real collision for a given method, just omit
`type_hint` and everything resolves on arg count alone.

```yaml
- java_member: valueOf
  arg_count: 1
  type_hint: [long]
  kind: rename
  csharp_member: FromInt64
```

Variadic methods use `arg_count: variadic`; in a template, `$rest` expands to
all trailing arguments.

---

## 9. Resolution: exact vs. simple-name matching

- The converter prefers an exact fully-qualified match if the parser resolved
  the receiver's type.
- If the parser only has a simple/unqualified name (common — this is a
  file-by-file parser with partial type info), it falls back to matching by
  **simple class name only if unambiguous** across the loaded mapping set. If
  two different `java_class` entries share a simple name (e.g. two packages
  both have a `Reader`), the reference is treated as unresolved and falls
  through to `unsupported` handling rather than guessing.
- The resolver runs in two passes on a method call:
  1. **Type-denoting resolution** — receivers whose Java name resolves to a
     type (static calls like `Math.abs(...)`, `System.out.println(...)`, or
     `java.util.Map.Entry` used as a type) are mapped through the index first.
  2. **Variable-receiver resolution** — if the type-denoting pass cannot
     resolve the receiver, the converter falls back to looking up the
     variable's *declared type* (tracked across method bodies as `ConversionContext`
     scopes, including locals, parameters, fields, and foreach variables).
     If the declared type is a mapped `java_class`, its members (renames,
     properties, templates) are applied to the call. For example, a variable
     declared as `java.util.Map<String,Integer>` resolves `put(...)` to the
     indexer-assignment template and `get(...)` to the indexer, even though
     the source file wrote `inventory.put(...)` / `inventory.get(...)` with
     no qualifying type.
- Variable-receiver lookup is by **dotted name** (the declared type as
  written). A chain like `this.field.method()` is not resolved because the
  leading `this.` prefix prevents type tracking; only the innermost known
  variable name is matched.
- Type names themselves are also resolved through the index when a declared
  or used type appears in source (e.g. `java.util.Map.Entry<String,Integer>`
  becomes `KeyValuePair<string,int>`), with the flat dictionary used as a
  fallback for anything the index does not know.

---

## 10. Unsupported and ambiguous cases

- `kind: unsupported` entries, and anything the resolver falls back to
  treating as unsupported (ambiguous overload, ambiguous simple-name match,
  missing `namespaces` coverage), are **never silently dropped or guessed**.
- The converter leaves the original Java-syntax fragment in place
  (untranslated) and emits an inline comment immediately above it:

  ```
  // TODO(java-to-csharp): no safe mapping for String.intern() —
  // No safe C# equivalent for string interning via this API.
  someString.intern();
  ```

  The comment text is the entry's `notes` field if present; if absent, a
  generic "no mapping found for `<java_class>.<java_member>`" message. These
  markers are emitted even when comment generation is otherwise disabled.
- `confidence: medium` / `low` entries **are still applied** (unlike
  `unsupported`, which is never applied) but get a `// REVIEW(java-to-csharp)`
  comment appended flagging them for human review:

  ```
  // REVIEW(java-to-csharp): medium confidence — Java's Math.random()...
  new Random().NextDouble();
  ```

---

## 11. Validation: what fails at load time

Schema validation happens at load time, not lazily during conversion. A file
that fails validation fails the whole load — a broken file is never silently
skipped.

- Every `namespaces[]` entry must have non-empty `java_packages` and
  `csharp_packages` lists.
- Every `class_map[]` entry must have `java_class` and `csharp_class`.
- Every `entries[]` item must have `java_member` and `kind`. Methods
  (`member_type: method`, the default) must have `arg_count` unless
  `kind: unsupported`. `kind: rename`/`property` must have `csharp_member`.
  `kind: template` must have `expression`.
- `csharp_class`/`csharp_member` values must be bare, valid C# identifiers.
- Errors report the file, the `java_class`/`java_member` path, and the
  missing/invalid field.

### Duplicates and conflicts

- **Duplicate `using`s are never an error** — they collapse at emission. Two
  `class_map` entries both pulling in `System` is expected.
- **Duplicate member keys across files are a load-time error.** If two loaded
  files define the same `(java_class, java_member, arg_count[, type_hint])`
  with the same (or absent) `type_hint`, the load fails and both file
  locations are reported. There is no implicit last-write-wins; a project that
  genuinely needs override behavior must say so explicitly (not yet supported
  in v1).
- Two entries with the **same key but different `type_hint`** are not a
  conflict — that's the intended disambiguation mechanism.
- **Duplicate `class_map` roots are merged.** Two files may define the same
  `java_class`; their `entries[]` lists are concatenated, **provided** their
  `csharp_class` values match and the member entries don't collide per the
  rule above. If `csharp_class` disagrees, that's a load-time error.

### Determinism

Given the same mapping set and the same source file, output must be
byte-identical across runs. Files are loaded in the order given; `using`s are
always emitted sorted alphabetically.

---

## 12. Literal type preservation

When a mapping rewrites a call such as `Math.abs(-1.0)`, the argument literals
keep their original form, so the numeric type carries over:

| Java source | C# output |
|---|---|
| `Math.abs(-1.0)` | `Math.Abs(-1.0)` |
| `Math.abs(1.0f)` | `Math.Abs(1.0f)` |
| `Math.abs(1e3)` | `Math.Abs(1e3)` |
| `Math.pow(1_000.5, 2.0)` | `Math.Pow(1_000.5, 2.0)` |
| `0x1.8p3` (hex float) | `12` (decimal equivalent — hex floats aren't valid C#) |

Suffixes (`f`/`F`, `d`/`D`), exponents, and `_` digit separators are preserved;
only Java hex-float literals are converted to their decimal value, since C# has
no hex-float syntax.

---

## 13. Full worked example

```yaml
namespaces:
  - java_packages: [java.lang]
    csharp_packages: [System]
  - java_packages: [java.util]
    csharp_packages: [System.Collections.Generic]
  - java_packages: [java.io]
    csharp_packages: [System]

class_map:
  - java_class: java.lang.Math
    csharp_class: Math
    entries:
      - java_member: pow
        arg_count: 2
        kind: rename
        csharp_member: Pow
      - java_member: abs
        arg_count: 1
        kind: rename
        csharp_member: Abs
      - java_member: random
        arg_count: 0
        kind: template
        expression: "new Random().NextDouble()"
        confidence: medium
        notes: >
          Java's Math.random() is a static PRNG with no direct C# static
          equivalent. This creates a new Random() per call site, which is
          wasteful/statistically risky if called in a loop. Flagging for
          review - consider hoisting a shared Random instance.
      - java_member: PI
        member_type: field
        kind: rename
        csharp_member: PI

  - java_class: java.lang.String
    csharp_class: String
    entries:
      - java_member: substring
        arg_count: 1
        kind: template
        expression: "$0.Substring($1)"
      - java_member: substring
        arg_count: 2
        kind: template
        expression: "$0.Substring($1, $2 - $1)"
        notes: "Java endIndex becomes a C# length."
      - java_member: length
        arg_count: 0
        kind: property
        csharp_member: Length
      - java_member: intern
        arg_count: 0
        kind: unsupported
        notes: "No safe C# equivalent for string interning via this API."

  - java_class: java.util.ArrayList
    csharp_class: List
    generic: true

  - java_class: java.lang.System
    csharp_class: Console
    entries:
      - java_member: out
        member_type: field
        kind: rename
        class_map:
          - java_class: java.io.PrintStream
            csharp_class: Console
            entries:
              - java_member: println
                arg_count: 1
                kind: rename
                csharp_member: WriteLine
              - java_member: print
                arg_count: 1
                kind: rename
                csharp_member: Write
```

---

## 14. Checklist for writing a mapping file

1. **Every `java_class` package must have a `namespaces` row**, or the
   converter will warn and the output won't compile without a manual `using`.
2. **Use bare identifiers** for `csharp_class`/`csharp_member` — never
   namespace-qualified paths.
3. **Always set `arg_count`** on method entries (unless `unsupported`). If you
   forget it, the load fails.
4. **Prefer `rename`** when the call shape matches. Use `property` when Java
   method call becomes a C# property. Use `template` only when you need arg
   reshaping — and put the receiver as `$0`, arguments as `$1`…
5. **Set `confidence: medium`/`low`** and `notes` on any entry whose C#
   translation is semantically imperfect — it surfaces as a `REVIEW` comment
   at every call site.
6. **Use `type_hint` only for genuine same-arity overloads** with different C#
   targets. Otherwise omit it.
7. **Split files along team/project boundaries** and rely on merging: same
   `java_class` roots merge, member keys must not collide across files.
8. **Test iteratively:** run the CLI on a small file, then search the output
   for `TODO(java-to-csharp)` and `REVIEW(java-to-csharp)` to find the gaps in
   your mapping set.

---

## 15. Building mappings in C# (programmatic)

You don't have to use YAML at all. `TypeMappingBuilder` constructs a
`TypeMappingIndex` in C# code, with a fluent API that mirrors the YAML schema
one-to-one. Programmatically built entries flow through the **same**
registration, validation and conflict-detection pipeline as YAML sources, so
the two approaches produce identical indexes and can be mixed freely.

```csharp
using JavaToCSharp;
using JavaToCSharp.TypeMapping;

var index = new TypeMappingBuilder()
    .AddNamespace("java.lang", "System")
    .AddNamespace(["java.util"], ["System.Collections.Generic"])
    .AddNamespace("java.io", "System")

    .AddClass("java.lang.Math", "Math", c => c
        .Rename("pow", 2, "Pow")
        .Rename("abs", 1, "Abs")
        .Template("random", 0, "new Random().NextDouble()",
            confidence: MappingConfidence.Medium,
            notes: "Creates a new Random() per call site; hoist a shared instance.")
        .AddField("PI", "PI"))

    .AddClass("java.lang.String", "String", c => c
        .Property("length", 0, "Length")
        .Template("substring", 2, "$0.Substring($1, $2 - $1)",
            notes: "Java endIndex becomes a C# length.")
        .Unsupported("intern", notes: "No safe C# equivalent."))

    .AddClass("java.lang.System", "Console", c => c
        .Route("out", MemberMappingType.Field, n => n
            .AddClass("java.io.PrintStream", "Console", p => p
                .Rename("println", 1, "WriteLine")
                .Rename("print", 1, "Write"))))

    .Build();

options.MappingIndex = index;
```

### Method reference

`TypeMappingBuilder` (each method returns the builder for chaining):

| method | YAML equivalent |
|---|---|
| `AddNamespace(javaPackage, csharpPackage)` | one `namespaces` row, single package |
| `AddNamespace(javaPackages, csharpPackages)` | one `namespaces` row, many-to-many |
| `AddClass(javaClass, csharpClass, configure, generic, kind)` | one `class_map` row; configure members in the lambda |
| `AddYaml(yaml, sourceName)` / `AddYamlFile(path)` / `AddYamlFiles(paths)` | mix YAML sources into the same builder |
| `Build()` | produce the validated `TypeMappingIndex` |

`ClassMappingBuilder` (the `c` in the lambda; each method returns it for chaining):

| method | `entries[]` fields |
|---|---|
| `Rename(javaMember, argCount, csharpMember, typeHint, confidence, notes)` | `kind: rename` |
| `RenameVariadic(javaMember, csharpMember, …)` | `arg_count: variadic`, `kind: rename` |
| `Property(javaMember, argCount, csharpMember, …)` | `kind: property` |
| `PropertyVariadic(javaMember, csharpMember, …)` | `arg_count: variadic`, `kind: property` |
| `Template(javaMember, argCount, expression, …)` | `kind: template` + `expression` |
| `TemplateVariadic(javaMember, expression, …)` | `arg_count: variadic`, `kind: template` |
| `Unsupported(javaMember, argCount?, notes, …)` | `kind: unsupported` (arg_count optional) |
| `AddField(javaMember, csharpMember, …)` | `member_type: field`, `kind: rename` |
| `Route(javaMember, memberType, configure)` | routing node with a nested `class_map` built in the lambda |

### Mixing YAML and programmatic entries

The builder can absorb YAML alongside C# additions; everything is merged under
the usual rules from §11:

```csharp
var index = new TypeMappingBuilder()
    .AddYamlFile("shared-mappings.yaml")   // shared base mapping set
    .AddClass("java.lang.Math", "Math", c => c.Rename("pow", 2, "Pow"))
    .Build();
```

Same `java_class` roots merge (provided `csharp_class` matches), duplicate
member keys across the two fail `Build()`, and conflicting namespace rows throw
— exactly as if the programmatic entries had been an extra YAML file. The
builder is reusable: `Build()` can be called repeatedly, and declarations added
after a build are included in the next one.

---

## 16. Built-in mapping specs

The converter ships six **bundled, curated mapping specs** covering common Java
→ C# API families. They are normal YAML mapping files (same schema as everything
in this guide) embedded in the library and loaded through the exact same
validation/merge pipeline, so they compose with user files and with each other.

Built-in specs are **strictly opt-in** — nothing is applied unless you ask for it.

| spec | Java packages | C# targets | highlights |
|---|---|---|---|
| **Console** | `java.lang` (`System.out/err`) | `System` | `System.out.println(x)` → `Console.WriteLine(x)`, `System.exit` → `Environment.Exit`, `currentTimeMillis` → `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`, `arraycopy`, `identityHashCode` |
| **System** | `java.lang` | `System` | `Math` renames/templates, `Integer`/`Long`/`Double`/`Float`/`Short`/`Byte`/`Boolean`/`Character` boxing types (`int.Parse`, `int.MaxValue`, …), `String`, `Object` |
| **System.Collections** | `java.util` | `System.Collections` | `List`/`ArrayList`, `Dictionary`/`HashMap`, `Set`/`SortedSet`, `IEnumerator`, `Collections`, `Arrays`, `Objects` |
| **System.IO** | `java.io`, `java.nio.file` | `System.IO` | `File`, `java.nio.file.Files` → `File`/`Directory`, stream renames (`StreamReader`, `FileStream`, `MemoryStream`), `IOException` exception rename |
| **System.Threading** | `java.lang` (`Thread`), `java.util.concurrent[.atomic]` | `System.Threading` | `Thread.Sleep/Yield/CurrentThread`, `Runnable` → `Action`, `ConcurrentHashMap` → `ConcurrentDictionary`, atomic types (unsupported → `Interlocked` notes) |
| **System.Text** | `java.lang` (`StringBuilder`), `java.text` | `System.Text` | `StringBuilder` renames/templates, `StringBuffer` → `StringBuilder`, `MessageFormat` → `string.Format` |

Two rules of thumb from earlier sections, applied to specs:

- **Same `java_class` may appear in several specs** — the Console, System,
  Threading and Text specs all touch `java.lang`, and they merge fine because the
  `java.lang → [System]` namespace row and `csharp_class` values agree.
- **Each `java_member` key is owned by exactly one spec**, so enabling all six
  at once is always valid.

### CLI

```text
JavaToCSharpCli --console-mapping --collections-mapping --io-mapping file Foo.java
```

Each spec has its own boolean flag (`--console-mapping`, `--system-mapping`,
`--collections-mapping`, `--io-mapping`, `--threading-mapping`, `--text-mapping`).
Flags combine with `--mapping-file`; specs are applied first, then user files
merge over them.

### GUI

Check the specs under the **Mappings** tab in Settings; they apply to every
conversion and are persisted alongside mapping files.

### Library

```csharp
using JavaToCSharp;
using JavaToCSharp.TypeMapping;

// a single spec, or a combination:
options.MappingIndex = BuiltInMappings.Load(
    BuiltInMappingSpec.Console,
    BuiltInMappingSpec.SystemCollections);

// or, mixed with user YAML through the builder:
var index = new TypeMappingBuilder()
    .AddBuiltIn(BuiltInMappingSpec.Console)
    .AddYamlFiles(["team-mappings.yaml"])
    .Build();
options.MappingIndex = index;
```

`BuiltInMappings` also exposes each spec as a convenience property
(`BuiltInMappings.Console`, `BuiltInMappings.System`, …), and
`BuiltInMappings.All` lists every spec in a fixed order. The YAML source of any
spec is available via `BuiltInMappings.YamlFor(spec)` — useful as a reference
when authoring your own files. As with user files, applying a spec is opt-in and
adding a spec never breaks conversion of code outside its coverage: uncovered
calls simply fall through to the normal `TODO(java-to-csharp)` handling.

---

## 17. Open items for future versions

- Structural/statement-level transforms (try-with-resources, streams, etc.)
- Multi-file/cross-file type resolution beyond the simple-name fallback and
  in-file variable-type tracking
- Exception hierarchy mapping beyond simple renames
- Template expressions needing a `using` outside the source class's own
  package (`using`s are inferred from the `namespaces` table and from the
  mapping index for mapped packages; arbitrary C# namespaces inside a template
  are not yet added to the output's `using`s)
