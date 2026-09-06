# Mapping File Guide

This guide is the single source of truth for authoring **member-level API mapping
files** — YAML files that tell the JavaToCSharp converter how to translate Java
API calls (classes, methods, fields) into their C# equivalents.

It covers the schema, the resolution pipeline, template authoring, overload
disambiguation, constructor mapping, chained call resolution, built-in specs,
testing methodology, and practical advice distilled from the nine shipped mapping
specs.

---

## Part 1: Foundations

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
  entirely through the `namespaces` table (see §3), not by qualifying the
  identifier inline.

A mapping file has exactly two top-level lists:

1. **`namespaces`** — package-to-package conversions, which drive `using`
   injection.
2. **`class_map`** — everything about a class: its own rename, plus its
   methods/fields, which can themselves route to further nested `class_map`s.

---

## 2. Using a mapping file

### CLI

`--mapping-file` is repeatable; all files are loaded into one index:

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
// or, built programmatically (see §19):
var index = new TypeMappingBuilder().AddClass(...).Build();
```

---

## 3. Schema quick reference

### 3.1 `namespaces[]`

Maps Java packages to C# namespaces/`using`s. Both sides are always lists
(many-to-many). There is **no `requires_using` field** — wherever a `using` is
needed, it comes from `csharp_packages` here.

| field | required | type | meaning |
|---|---|---|---|
| `java_packages` | yes | list | one or more Java package names that map to the same C# result |
| `csharp_packages` | yes | list | one or more C# namespaces/`using`s this produces |

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

| field | required | type | meaning |
|---|---|---|---|
| `java_class` | yes | string | fully-qualified Java class/interface name — the match key |
| `csharp_class` | yes | string | bare C# identifier, e.g. `List`, `Math`, `Console` |
| `generic` | no | bool | `true` if the type takes type parameters (e.g. `List<T>`) |
| `kind` | no | string | `class` (default) \| `interface` \| `exception` |
| `match_qualified_only` | no | bool | `true` excludes the class from simple-name lookup; it is reachable only via its fully qualified name. Use for placeholder classes whose simple name collides with unrelated target types. |
| `entries` | no | list | method/field/constructor mappings — omit for a pure class rename |

### 3.3 `entries[]`

| field | required | type | meaning |
|---|---|---|---|
| `java_member` | yes | string | Java method/field name, no parens. `<init>` for constructors |
| `member_type` | no | string | `method` (default) \| `field` \| `constructor` |
| `arg_count` | methods | int or `variadic` | number of declared Java arguments |
| `kind` | yes | string | `rename` \| `property` \| `template` \| `unsupported` |
| `csharp_member` | for rename/property | string | bare C# member name |
| `expression` | for template | string | placeholder expression (see §6) |
| `type_hint` | no | list | Java param types, e.g. `[int, int]` — tiebreaker only (see §8) |
| `confidence` | no | string | `high` (default) \| `medium` \| `low` |
| `notes` | no | string | free text — emitted as a code comment on `unsupported`/`REVIEW` |
| `class_map` | no | list | nested `class_map` for chained access (see §9) |
| `returns_type` | no | string | Java type the member returns; enables chain resolution through preceding calls (see §9.2) |

### 3.4 Template placeholders

| placeholder | meaning |
|---|---|
| `$0` | the receiver (instance method) or C# class name (static method / constructor) |
| `$1`, `$2`, `$3` … | positional Java arguments, 1-indexed, in declared order |
| `$rest` | (variadic only) all remaining arguments from the first non-fixed position onward |

### 3.5 The `kind` vocabulary

| kind | when to use | example |
|---|---|---|
| `rename` | Same call shape, different name | `Math.pow(a, b)` → `Math.Pow(a, b)` |
| `property` | Method call becomes property access (parens dropped) | `str.length()` → `str.Length` |
| `template` | Arg reshaping, reordering, or full expression swap | `str.substring(a, b)` → `str.Substring(a, b - a)` |
| `unsupported` | No safe automatic mapping — emits `TODO` comment | |

---

## 4. The resolution pipeline

Understanding how the converter resolves member access chains is critical for
writing effective mappings. Every call flows through this pipeline, each pass
tried in order until one succeeds.

### 4.1 Overview

```
MethodCallExpr / FieldAccessExpr
  │
  ▼
┌─────────────────────────────────┐
│ 1. Chain extraction             │  Flatten NameExpr/FieldAccessExpr chain
│    (ChainExtractor)             │  Intermediate MethodCallExpr → null
└──────────────┬──────────────────┘
               │
               ▼
┌─────────────────────────────────┐
│ 2. Namespace gating             │  Skip if file has no mapped packages
│    (FileMappingContext)         │  Skip if root isn't from an active package
└──────────────┬──────────────────┘
               │
               ▼
┌─────────────────────────────────┐
│ 3. Type-denoting resolution     │  Math.abs → java.lang.Math → rename to Abs
│    (ResolveChain)               │  System.out.println → nested chain resolution
└──────────────┬──────────────────┘
               │ unresolved
               ▼
┌─────────────────────────────────┐
│ 4. Variable-receiver resolution │  sb.append → declared StringBuilder type
│    (TryResolveVariableReceiver) │  Tracked by VariableScopeTracker
└──────────────┬──────────────────┘
               │ unresolved
               ▼
┌─────────────────────────────────┐
│ 5. Import-based resolution      │  A.sb.append → A resolves via import
│    (TryResolveByImportedClass)  │  FileMappingContext.TryResolveClassName
└──────────────┬──────────────────┘
               │ unresolved
               ▼
┌─────────────────────────────────┐
│ 6. Heuristic inference          │  unknown.append → only StringBuilder has append
│    (TryResolveByHeuristicInfer) │  Reverse member index + package filter
└──────────────┬──────────────────┘
               │ unresolved
               ▼
          Default conversion
     (Capitalize + ReplaceCommonMethodNames)
```

### 4.2 Chain extraction (`ChainExtractor`)

`ChainExtractor.Extract` flattens a member access chain into an ordered list of
name segments. **Intermediate `MethodCallExpr` nodes cause extraction to return
`null`** because their arguments cannot be represented in a flat name list.

Chains like `A.field.method()` produce `["A", "field", "method"]`.
Chains like `getMap().get("key")` produce `null` because `getMap()` is a
`MethodCallExpr` scope — this case falls through to the chained-casing path in
`MethodCallExpressionVisitor` instead.

### 4.3 Namespace gating

Before any resolution is attempted, the converter checks:

1. Does this file have any mapped packages at all? (via `FileMappingContext.HasAnyMappedPackage`)
2. Does the root of the chain belong to an active (imported) package?

If either check fails, the entire mapping pipeline is skipped for this call and
the default conversion runs. This prevents false-positive remapping on
user-defined types.

### 4.4 Type-denoting resolution

The converter tries to resolve the chain's root as a Java type:

1. **Longest exact qualified-name prefix.** For `java.lang.Math.abs`, it tries
   `java.lang.Math` → found in index → consumes 2 segments.
2. **Direct first-segment match.** `Math.abs` → `Math` is registered as a
   root-level `java_class`.
3. **Unambiguous simple-name fallback.** If only one `java_class` in the index
   has the simple name `Math`, use it. If two different classes share the same
   simple name (e.g. `java.io.Reader` and `java.sql.Reader`), the reference is
   treated as **ambiguous** → emits `TODO`.

### 4.5 Variable-receiver resolution

If the chain root is a variable name (e.g. `sb.append("x")` where `sb` is a
local), the converter looks up its declared Java type via
`VariableScopeTracker`. If the type maps to a `java_class` in the index, the
member is resolved against that class.

**What gets tracked:**
- Local variable declarations (`StringBuilder sb = new StringBuilder()`)
- Parameters (`void f(StringBuilder sb)`)
- Foreach variables (`for (String s : list)`)
- Fields (limited)

**What does NOT get tracked:**
- `this.field.method()` — the `this.` prefix prevents type tracking
- Method return types (e.g. `getMap().get("key")` — the return type of
  `getMap()` is unknown)

### 4.6 Import-based resolution

When the first segment of a chain is a simple class name (not a known type and
not a tracked variable), the converter tries to resolve it through the file's
import declarations.

For example, `A.sb.append("x")` where `A` is imported as `com.example.A`:
1. `TryResolveClassName("A")` → `"com.example.A"` (via `FileMappingContext`)
2. Look up `"com.example.A"` in the mapping index
3. If found, resolve the chain from there

### 4.7 Heuristic inference

When all else fails, the converter looks up the **member name** in a reverse
index of all mapped classes, filters by the file's active (imported) packages,
and if **exactly one class** matches, infers the receiver's type.

Example: `unknown.append("x")` — `append` only exists in `StringBuilder` in
the reverse index → unique match → resolves to `unknown.Append("x")`.

**Important:** if multiple classes have the same member name (e.g. `toString`
exists in `StringBuilder`, `Object`, etc.), the heuristic cannot resolve and
falls through to default conversion.

`unsupported` entries are **excluded** from the reverse index — they never
participate in heuristic inference.

### 4.8 Chained-casing path

When `ChainExtractor` returns `null` (intermediate method call), the
`MethodCallExpressionVisitor` runs a secondary path on the **outermost** call.
It tries these receivers in order:

1. **Root variable type.** Walk to the root receiver (via
   `FindRootReceiverName`), look up its declared type, and resolve the
   outermost call against that type.
2. **`returns_type` of the preceding call.** If step 1 fails or the member
   isn't on the root type, resolve the declareable `returns_type` of the
   immediate inner call and resolve the outermost call against it. If that
   type is the `GenericType` sentinel, infer it from the member name (see
   §9.2).

For `sb.append("x").deleteCharAt(3)`:
- `ChainExtractor` returns null (intermediate `MethodCallExpr`)
- `FindRootReceiverName` walks to `sb`
- `TryGetVariableType("sb")` → `"StringBuilder"`
- `TryResolveChainedCall` resolves `deleteCharAt` against `StringBuilder` → `Remove`

For `getMap().get("key").getDayOfYear()`:
- Step 1: the root `getMap` is a bare method call, so the variable lookup
  fails.
- Step 2: `get` has `returns_type: GenericType`, so the search looks for
  another mapped class that provides `getDayOfYear`. `java.time.LocalDate` is
  the only one → the receiver is inferred as `LocalDate` and the chain
  resolves to `.DayOfYear` (this previously fell through to default casing).
  If the follow-on member is ambiguous (e.g. `toString` exists on many mapped
  classes) the chain still falls through.

Classes rooted at a **type name** rather than a variable use the same
mechanism: `Pattern.compile(p)` has `returns_type: java.util.regex.Pattern`,
so `Pattern.compile(p).matcher(s)` resolves even though `Pattern` is a class,
not a variable.

---

## 5. Step-by-step workflow for a new library

When mapping a new Java library to C#, follow this process:

### Step 1: Identify the package(s)

List all Java packages the library uses. You need a `namespaces` row for each
distinct package that contains mapped classes.

```yaml
namespaces:
  - java_packages: [org.apache.commons.io]
    csharp_packages: [System.IO]
```

### Step 2: Map the classes

For each Java class that has a C# equivalent, create a `class_map` entry.
Start with **type renames only** — no member entries yet.

```yaml
class_map:
  - java_class: org.apache.commons.io.FileUtils
    csharp_class: FileUtils
```

### Step 3: Map the members

Go through each class's API and decide how each member translates. Group by
kind:

1. **`rename`** — same call shape, different name. Most common.
2. **`property`** — Java `getX()`/`size()` → C# `X`/`Count`.
3. **`template`** — arguments change, expression rewrite needed.
4. **`unsupported`** — no safe C# equivalent.

### Step 4: Add `arg_count` to every method

Every method entry **must** have `arg_count` (unless `kind: unsupported`).
Count the Java method's declared parameters.

### Step 5: Test iteratively

Run the CLI on a small Java file that exercises the mapped APIs. Search the
output for:
- `TODO(java-to-csharp)` — unmapped or unsupported members
- `REVIEW(java-to-csharp)` — low/medium confidence mappings

### Step 6: Refine

Based on the test output:
- Add missing entries
- Promote `confidence: medium` → `high` if you've verified correctness
- Add `notes` to `unsupported` entries to guide manual fixup

---

## Part 2: Writing mappings

---

## 6. Template authoring masterclass

Templates are the most powerful and most nuanced kind of mapping entry. This
section covers every pattern observed in the built-in specs and the test suite.

### 6.1 Instance method — receiver and args

The most common pattern. `$0` is the receiver object; `$1`, `$2`, … are the
arguments:

```yaml
- java_member: substring
  arg_count: 2
  kind: template
  expression: "$0.Substring($1, $2 - $1)"
  notes: "Java's endIndex becomes a C# length."
```

Input: `str.substring(1, 5)` → Output: `str.Substring(1, 5 - 1)`

### 6.2 Static method — no receiver

For static calls like `Integer.parseInt(x)`, `$0` is the C# class name
(equivalent to the `csharp_class`), and arguments start at `$1`:

```yaml
class_map:
  - java_class: java.lang.Integer
    csharp_class: int
    entries:
      - java_member: parseInt
        arg_count: 1
        kind: template
        expression: "int.Parse($1)"
```

Input: `Integer.parseInt("42")` → Output: `int.Parse("42")`

### 6.3 No-placeholder expressions

Some templates have no placeholders at all — the output is a fixed expression:

```yaml
- java_member: currentTimeMillis
  arg_count: 0
  kind: template
  expression: "DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()"
```

Input: `System.currentTimeMillis()` → Output: `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`

### 6.4 Receiver-as-argument templates

When the Java static method's first argument is the "real" receiver in C#:

```yaml
class_map:
  - java_class: java.util.Collections
    csharp_class: Collections
    entries:
      - java_member: sort
        arg_count: 1
        kind: template
        expression: "$1.Sort()"
```

Input: `Collections.sort(list)` → Output: `list.Sort()`

Here `$0` is `Collections` (unused), `$1` is `list` (the actual target).

### 6.5 Field-as-expression templates

Java static fields that map to C# keyword expressions:

```yaml
- java_member: MAX_VALUE
  member_type: field
  kind: template
  expression: "int.MaxValue"
```

Input: `Integer.MAX_VALUE` → Output: `int.MaxValue`

### 6.6 Indexer templates

Map.get/put → C# indexer:

```yaml
class_map:
  - java_class: java.util.Map
    csharp_class: IDictionary
    entries:
      - java_member: get
        arg_count: 1
        kind: template
        expression: "$0[$1]"
      - java_member: put
        arg_count: 2
        kind: template
        expression: "$0[$1] = $2"
```

Input: `map.get("key")` → Output: `map["key"]`
Input: `map.put("key", value)` → Output: `map["key"] = value`

### 6.7 Variadic templates with `$rest`

For methods with variable argument counts:

```yaml
- java_member: printf
  arg_count: variadic
  kind: template
  expression: "$0.Write($1, $rest)"
```

`$rest` expands to all arguments starting from the one after the last explicitly
referenced placeholder. In this case, `$1` is the format string and `$rest` is
everything else.

Input: `System.out.printf("%s %d", name, 5)` → Output: `Console.Write("%s %d", name, 5)`

### 6.8 Boolean-returning templates

Templates can emit boolean expressions:

```yaml
- java_member: isEmpty
  arg_count: 0
  kind: template
  expression: "$0.Count == 0"

- java_member: isNull
  arg_count: 1
  kind: template
  expression: "$1 is null"
```

### 6.9 Templates that drop the receiver

When the Java call uses a receiver but C# doesn't need one:

```yaml
- java_member: valueOf
  arg_count: 1
  type_hint: [int]
  kind: template
  expression: "$1"
```

Input: `Integer.valueOf(x)` where x is int → Output: `x` (identity — no-op boxing)

### 6.10 Templates with `string.Format` / regex

Templates can call arbitrary C# APIs. Note the single-quote escaping for
YAML:

```yaml
- java_member: replaceAll
  arg_count: 2
  kind: template
  expression: "System.Text.RegularExpressions.Regex.Replace($0, $1, $2)"
  confidence: medium
  notes: "Java and .NET regex syntaxes differ slightly; review the pattern."
```

### 6.11 Multi-segment static method templates

When a C# static method requires full qualification because the `using` is
not available:

```yaml
- java_member: sleep
  arg_count: 1
  kind: template
  expression: "System.Threading.Thread.Sleep($1)"
```

### 6.12 Constructor templates

For `new` expressions, `$0` is the C# class name (different from methods
where `$0` is the receiver):

```yaml
- java_member: <init>
  member_type: constructor
  arg_count: 0
  kind: template
  expression: "new Dictionary<string, object>()"
```

Input: `new HashMap()` → Output: `new Dictionary<string, object>()`

### 6.13 What happens when `$0` is empty

For static method calls where `$0` is referenced in the template, `$0`
resolves to the `csharp_class` name. For instance method calls, `$0`
resolves to the receiver expression as it appears in source.

### 6.14 Empty `$rest` expansion

When a variadic call supplies no arguments beyond the last explicitly
referenced placeholder (`string.Format($1, $rest)` called with only a format
string), `$rest` expands to nothing and the template's own literal separator
immediately before it (e.g. `", "`) is collapsed so the output does not end
with a dangling comma.

### 6.15 Precedence safety in templates

A captured parameter (`$0`, `$1`, …) may be an arbitrary expression, including
one whose operators bind more loosely than the operators around it in the
template. **Whenever a template splices a capture into an operator expression,
wrap the capture in parentheses** — unless the capture is already atomic in
context (sole content of the template, receiver of a method call or indexer,
or method argument).

Incorrect:

```yaml
- java_member: toRadians
  arg_count: 1
  kind: template
  expression: "($0 * Math.PI / 180.0)"
```

`Math.toRadians(a + b)` would produce `(a + b * Math.PI / 180.0)` — wrong
grouping. Correct:

```yaml
- java_member: toRadians
  arg_count: 1
  kind: template
  expression: "(($0) * Math.PI / 180.0)"
```

### 6.16 Receiver-ambiguous shared patterns

The mapping system cannot always know a receiver's compile-time type. When the
same Java member name (e.g. `get`) exists on multiple mapped classes with
**different semantics** (`Map.get` returns null for a missing key; C#'s
`Dictionary` indexer throws), a shared template such as `$0[$1]` silently
changes behavior for one of them. Where the behavioral difference matters,
prefer an `unsupported` entry with an explanatory `notes` field, or a template
that routes to a compatibility/helper method implementing Java's semantics,
over a shared indexer or property mapping.

---

## 7. Constructor mapping

Constructor mappings apply `rename` or `template` transformations to `new`
expressions.

### Syntax

```yaml
class_map:
  - java_class: java.util.ArrayList
    csharp_class: List
    entries:
      # Rename: new ArrayList() → new List()
      - java_member: <init>
        member_type: constructor
        arg_count: 0
        kind: rename
        csharp_member: List

      # Rename with args: new ArrayList(x) → new List(x)
      - java_member: <init>
        member_type: constructor
        arg_count: 1
        kind: rename
        csharp_member: List
```

### Constructor templates

```yaml
- java_member: <init>
  member_type: constructor
  arg_count: 0
  kind: template
  expression: "new Dictionary<string, object>()"
```

**Important:** In constructor templates, `$0` is the **C# class name**, not
the receiver. `$1`, `$2`, … are the constructor arguments.

### Arity-based disambiguation

Multiple `<init>` entries with different `arg_count` values let you handle
different constructor overloads:

```yaml
entries:
  - java_member: <init>
    member_type: constructor
    arg_count: 0
    kind: template
    expression: "new List<string>()"
  - java_member: <init>
    member_type: constructor
    arg_count: 1
    kind: rename
    csharp_member: List
```

### When no constructor mapping exists

If no `<init>` entry matches, the converter falls through to the default
`new` expression with the mapped type name. For type renames (where
`csharp_class` differs from `java_class`), this usually produces the correct
output automatically:

Input: `new ArrayList()` → Output: `new List()` (automatic, from the class-level rename)

---

## 8. Overload disambiguation

### The lookup key

The lookup key for a member entry is
`(java_class, java_member, member_type, arg_count[, type_hint])`.

### `arg_count` matching

For methods, `arg_count` must match the number of arguments in the call.
`arg_count: variadic` matches any number of arguments.

### `type_hint` tiebreaker

When two entries share the same `(java_class, java_member, arg_count)`, the
converter uses `type_hint` to break ties:

1. If the parser knows the argument types **and** a matching `type_hint` entry
   exists, use it.
2. Otherwise, use the entry with **no `type_hint`** (the default).
3. If multiple entries exist at the same key and none is type-free → ambiguous
   → falls through to `unsupported`.

```yaml
entries:
  - java_member: valueOf
    arg_count: 1
    type_hint: [String]
    kind: template
    expression: "int.Parse($1)"
  - java_member: valueOf
    arg_count: 1
    type_hint: [int]
    kind: template
    expression: "$1"
  - java_member: valueOf
    arg_count: 1
    kind: template
    expression: "$1"
    # ↑ default fallback when arg type is unknown
```

### Argument type inference

The `ArgumentTypeInferrer` infers Java types from AST expressions:

| Expression type | Inferred type |
|---|---|
| `IntegerLiteralExpr` | `int` |
| `LongLiteralExpr` | `long` |
| `DoubleLiteralExpr` | `double` |
| `BooleanLiteralExpr` | `boolean` |
| `StringLiteralExpr` | `String` |
| `NameExpr` | variable name (not the type) |
| `ObjectCreationExpr` | the created type |
| `CastExpr` | the cast type |
| Everything else | `null` (matches any hint) |

---

## 9. Chained calls and routing nodes

### Routing nodes (nested `class_map`)

Some Java chains collapse segments: `System.out.println(x)` →
`Console.WriteLine(x)`. The routing node for `out` carries a nested
`class_map` that describes the segments that follow:

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
```

### Resolution rules

1. Match root `System` → `csharp_class: Console`.
2. Match `out` → routing node, has nested `class_map` → **elide** `out` from
   output.
3. Match `println` in nested class → leaf, emit `Console.WriteLine(x)`.

### Standalone routing node

If the chain ends at a routing node (e.g. `System.out` used alone), the
converter emits `csharp_class.csharp_member` if available, or a `TODO`
comment if not.

```yaml
- java_member: out
  member_type: field
  kind: property
  csharp_member: Out    # ← used when "System.out" appears alone
  class_map: [...]
```

### Registration gotcha

A nested `class_map` under an `entries[]` item is **only** reachable by
chained access starting from its enclosing class. It is **not** registered as
a standalone class. If the source also uses the nested type directly (e.g.
`java.util.Map.Entry<String,Integer>`), you must add it as its own
**top-level** `class_map` entry.

### 9.2 `returns_type` and the `GenericType` sentinel

The `returns_type` field records what a member returns so the converter can
resolve a call chained *onto* it, even when the receiver is not a tracked
variable. It also powers the `$0` receiver substitution in chained templates.

```yaml
- java_member: plusDays
  arg_count: 1
  kind: rename
  csharp_member: AddDays
  returns_type: java.time.LocalDate
```

Now `LocalDate.now().plusDays(1).getYear()` resolves hop-by-hop:
`now` (`returns_type: java.time.LocalDate`) → `plusDays` against `LocalDate`
→ `getYear` against `LocalDate` (`kind: property` → `.Year`), producing
`DateTime.Today.AddDays(1).Year`.

#### The receiver chain

During resolution the innermost receiver (`$0`) is itself converted, so a
chained template can nest another template. Example with `Paths.get` →
`toAbsolutePath` where `get` is `Path.Combine($rest)` and `toAbsolutePath` is
`Path.GetFullPath($0)`:

```java
Paths.get("a", "b").toAbsolutePath()
```
```csharp
Path.GetFullPath(Path.Combine("a", "b"))
```

#### `GenericType`

When a member returns an element of a generic collection or a type that could
be anything, declare `returns_type: GenericType`. The converter infers the
concrete type from the member called *next* in the chain: it looks up that
member's name in the reverse member index, filters to the file's active
packages, and — when exactly one class provides it — treats that class as the
receiver.

```yaml
- java_member: get
  arg_count: 1
  kind: template
  expression: "$0[$1]"
  returns_type: GenericType
```

```java
HashMap m = new HashMap();
return m.get("d").getDayOfYear();   // get → GenericType → inferred LocalDate
```
```csharp
return m["d"].DayOfYear;
```

**Rules:**
- Use `GenericType` only when the true return type is unknown (collection
  element access, `Optional`, iterators, `Future`, etc.). Use a concrete
  `returns_type` whenever the class is known — it is cheaper and never
  ambiguous.
- The follow-on member must come from an **active** (imported) Java package,
  matching normal namespace gating.
- If two or more active classes provide the follow-on member, resolution
  fails and the default conversion runs.

Sentinels are already placed on the common generic surface: `List.get/set`,
`Map.get/put/getOrDefault/putIfAbsent/computeIfAbsent/merge`, `Iterator.next`
(property `Current`), `Optional`, `Future.get`, `CompletableFuture.get/join`,
concurrent collection operations, `Stack.pop/peek`, `PriorityQueue.poll/peek`
and `ArrayDeque` poll/peek.

> **Note:** `returns_type` alone does not select the entry — member lookup is
> still keyed by (`java_member`, `arg_count`, `type_hint`). It is metadata that
> enables downstream hops.

---

## 10. Confidence and review comments

### `confidence: medium` / `low`

Entries with medium or low confidence are **still applied** (unlike
`unsupported`) but get a `REVIEW` block comment at every call site:

```
/* REVIEW(java-to-csharp): medium confidence — Java's Math.random()... */
new Random().NextDouble();
```

The comment is always a `/* ... */` block (never a `//` line comment) so it
can never swallow the rest of the source line when the reviewed expression
sits mid-statement (after `=`, `??`, `(`, …).

### When to use `confidence: medium`

Use medium confidence when:
- The mapping is semantically imperfect (e.g. Java's `Math.round` uses
  half-up, C#'s `Math.Round` uses half-to-even)
- The C# equivalent has different behavior edge cases (e.g. `double.CompareTo`
  vs Java's `Double.compare` for NaN)
- The mapping requires a `using` that might not be present
- The return type differs slightly (e.g. `Math.Sign` returns `int` in C# but
  `double` in Java)

### `confidence: high` (default)

No comment emitted. Use for direct 1:1 renames and well-understood templates.

---

## Part 3: Advanced patterns

---

## 11. Validation and conflicts

Schema validation happens at **load time**. A broken file never silently
skips.

### What fails at load time

- Missing `java_packages`/`csharp_packages` in a namespace row.
- Missing `java_class`/`csharp_class` in a `class_map` entry.
- Method entries missing `arg_count` (unless `kind: unsupported`).
- `kind: rename`/`property` without `csharp_member`.
- `kind: template` without `expression`.
- `csharp_class`/`csharp_member` containing invalid C# identifiers.

### Duplicates and conflicts

- **Duplicate `using`s are never an error** — they collapse at emission.
- **Duplicate member keys across files are a load-time error.** Same
  `(java_class, java_member, member_type, arg_count[, type_hint])` in two
  files → load fails.
- Same key with **different `type_hint`** → not a conflict (intended
  disambiguation).
- **Duplicate `class_map` roots are merged** (entries concatenated) when
  `csharp_class` matches. Different `csharp_class` → load-time error.

---

## 12. Unsupported and ambiguous cases

### `kind: unsupported`

The converter leaves the original Java fragment in place and emits an inline
block comment:

```java
/* TODO(java-to-csharp): no safe mapping for String.intern() — No safe C# equivalent for string interning via this API. */
someString.intern();
```

The comment text is the entry's `notes` field if present; if absent, a generic
message. Like `REVIEW` markers, the comment is always a `/* ... */` block so
it can never swallow the remainder of the source line when the placeholder is
emitted mid-statement.

### Ambiguous simple-name matches

If two `java_class` entries share a simple name (e.g. `java.io.Reader` and
`java.sql.Reader`), the reference is treated as **unresolved** rather than
guessing. It falls through to default conversion.

### Ambiguous heuristic inference

If the reverse member index returns multiple candidate classes for a member
name, the heuristic cannot resolve and falls through to default conversion.

---

## 13. Literal type preservation

When a mapping rewrites a call, argument literals keep their original form:

| Java source | C# output |
|---|---|
| `Math.abs(-1.0)` | `Math.Abs(-1.0)` |
| `Math.abs(1.0f)` | `Math.Abs(1.0f)` |
| `Math.pow(1_000.5, 2.0)` | `Math.Pow(1_000.5, 2.0)` |
| `0x1.8p3` (hex float) | `12` (decimal equivalent) |

Suffixes (`f`/`F`, `d`/`D`), exponents, and `_` digit separators are
preserved; only Java hex-float literals are converted to their decimal value.

---

## 14. Built-in mapping specs

The converter ships nine curated mapping specs covering common Java → C#
API families. They are normal YAML files loaded through the same pipeline.

### Specs overview

| spec | Java packages | C# targets | highlights |
|---|---|---|---|
| **Console** | `java.lang` (System.out/err) | `System` | `System.out.println` → `Console.WriteLine`, `System.exit` → `Environment.Exit`, `currentTimeMillis` → `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`, `arraycopy`, `identityHashCode` |
| **System** | `java.lang` | `System` | `Math` renames/templates, boxing types (`Integer` → `int.Parse`, `int.MaxValue`), `String`, `Object`, `Throwable`, exceptions |
| **System.Collections** | `java.util` | `System.Collections.Generic` | `List`/`ArrayList`, `Dictionary`/`HashMap`, `Set`/`SortedSet`, `IEnumerator`, `Collections`, `Arrays`, `Objects`, `Optional`, `stream` placeholders |
| **System.IO** | `java.io`, `java.nio.file` | `System.IO` | `File`, `java.nio.file.Files` → `File`/`Directory`, stream renames, exception renames |
| **System.Threading** | `java.lang` (Thread), `java.util.concurrent[.atomic]` | `System.Threading` | `Thread.Sleep`, `Runnable` → `Action`, `ConcurrentHashMap` → `ConcurrentDictionary`, atomic types |
| **System.Text** | `java.lang` (StringBuilder), `java.text` | `System.Text` | `StringBuilder` renames/templates, `StringBuffer` → `StringBuilder`, `MessageFormat` → `string.Format` |
| **System.Time** | `java.time[.temporal][.format]` | `System` | `LocalDate`/`LocalDateTime` → `DateTime`, `Instant`, `Duration` → `TimeSpan`, `Period`, `ZoneId` → `TimeZoneInfo`, `DateTimeFormatter` |
| **System.Text.RegularExpressions** | `java.util.regex` | `System.Text.RegularExpressions` | `Pattern` → `Regex`, `Matcher` → `Match` |
| **System.Numerics** | `java.math` | `System.Numerics` | `BigInteger` operators, `BigDecimal` → `decimal` (with review notes) |

### Multi-spec composition

- **Same `java_class` may appear in several specs** — they merge fine because
  `java.lang → [System]` namespace row and `csharp_class` values agree.
- **Each `java_member` key is owned by exactly one spec** — enabling all nine at
  once is always valid.
- **Synthetic `java_class` names** — The Console spec uses
  `java.lang.System.Out` and `java.lang.System.Err` as synthetic class names
  for the routing nodes, keeping them separate from `java.io.PrintStream`
  (owned by the IO spec).

### CLI

```
JavaToCSharpCli --console-mapping --system-mapping --collections-mapping --io-mapping --threading-mapping --text-mapping --time-mapping --regex-mapping --numerics-mapping file Foo.java
```

Or enable every built-in spec at once with `--all-mapping`.

### Library

```csharp
options.MappingIndex = BuiltInMappings.Load(
    BuiltInMappingSpec.Console,
    BuiltInMappingSpec.SystemCollections);
```

---

## 15. Import-based resolution details

### How `FileMappingContext` works

When the converter processes a Java file, it builds a `FileMappingContext` from
the file's imports:

1. **Auto-imported packages** — `java.lang.*` is always active.
2. **Explicit imports** — `import java.util.List;` activates `java.util`.
3. **Wild-card imports** — `import java.util.*;` activates `java.util`.
4. **Simple-name lookup** — For each non-wildcard import, the simple name
   (e.g. `List`) is mapped to its FQN (e.g. `java.util.List`) in a dictionary.

### Package activation

When a package is "active," the converter is willing to resolve types from that
package through the mapping index. Inactive packages are skipped to prevent
false-positive remapping of user-defined types.

### Auto-imported class names

For classes in auto-imported packages (e.g. `java.lang.StringBuilder`), the
converter populates the simple-name → FQN dictionary automatically. This means
`StringBuilder sb = new StringBuilder(); sb.append("x");` resolves correctly
even without an explicit import.

---

## 16. Heuristic inference details

### The reverse member index

`TypeMappingIndex` builds `_byMemberName` — a dictionary from member names to
the classes that define them. Only **resolvable** entries (not `unsupported`)
participate.

### Resolution

1. Look up the member name in the reverse index.
2. Filter by the file's active packages (only classes whose package is imported).
3. If **exactly one** class matches → infer the type → resolve the chain.
4. If zero or multiple → unresolved.

### Package filtering

The heuristic only considers classes from packages that are active in the
current file. This prevents false positives where a member name exists in both
a mapped library and a user-defined type.

---

## 17. The `TypeHelper` hardcodes

Before mapping resolution runs, `TypeHelper` applies a built-in dictionary of
common Java-to-C# type name conversions. These are **not** mapping operations —
they are core translation behavior.

### Type name conversions

| Java type | C# type |
|---|---|
| `boolean` / `Boolean` | `bool` |
| `Integer` | `int` |
| `Long` | `long` |
| `Float` | `float` |
| `String` | `string` |
| `Object` | `object` |
| `ICloseable` | `IDisposable` |
| `AutoCloseable` | `IDisposable` |
| `ArrayList` | `List` |
| `List` | `IList` |
| `Map` | `Dictionary` |
| `Set` | `HashSet` |
| `Iterator` | `IEnumerator` |
| `Comparable` | `IComparable` |
| `IllegalArgumentException` | `ArgumentException` |
| `IllegalStateException` | `InvalidOperationException` |
| `UnsupportedOperationException` | `NotSupportedException` |
| `RuntimeException` | `Exception` |
| `NullPointerException` | `NullReferenceException` |
| `EOFException` | `EndOfStreamException` |
| `NoSuchFileException` | `FileNotFoundException` |

### Method name replacements

`ReplaceCommonMethodNames` handles well-known Java method names that differ in
C#:

| Java | C# |
|---|---|
| `hashCode` | `GetHashCode` |
| `getClass` | `GetType` |
| `close` | `Dispose` |
| `toUpperCase` | `ToUpper` |
| `toLowerCase` | `ToLower` |
| `equals` | `Equals` |
| `toString` | `ToString` |
| `trim` | `Trim` |
| `readLine` | `ReadLine` |
| `read` | `Read` |
| `ready` | `EndOfStream` |
| `getMessage` | `Message` |
| `abs` | `Abs` |
| `compareTo` | `CompareTo` |

### Interaction with mapping index

When a mapping index is configured, `TypeMappingResolver.TryResolveTypeName`
runs **first** and overrides the dictionary. For example, `Map` → `IDictionary`
in the mapping index takes priority over `Map` → `Dictionary` in the
dictionary.

### Field name replacements

`ReplaceCommonFieldNames` handles:
- `length` → `Length`

---

## Part 4: Reference

---

## 18. Full worked example

```yaml
namespaces:
  - java_packages: [java.lang]
    csharp_packages: [System]
  - java_packages: [java.util]
    csharp_packages: [System.Collections.Generic]
  - java_packages: [java.io]
    csharp_packages: [System.IO]

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
        expression: "Random.Shared.NextDouble()"
        notes: "Uses the shared Random instance; avoids allocating a Random per call site."
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
    entries:
      - java_member: <init>
        member_type: constructor
        arg_count: 0
        kind: rename
        csharp_member: List

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

## 19. Programmatic builder API

`TypeMappingBuilder` constructs a `TypeMappingIndex` in C# code, with a fluent
API that mirrors the YAML schema. Entries flow through the same validation and
conflict-detection pipeline as YAML.

### TypeMappingBuilder

| method | YAML equivalent |
|---|---|
| `AddNamespace(javaPackage, csharpPackage)` | one `namespaces` row, single package |
| `AddNamespace(javaPackages, csharpPackages)` | one `namespaces` row, many-to-many |
| `AddClass(javaClass, csharpClass, configure, generic, kind)` | one `class_map` row |
| `AddBuiltIn(spec)` | one built-in spec |
| `AddYaml(yaml, sourceName)` / `AddYamlFile(path)` | mix YAML sources |
| `Build()` | produce the validated index |

### ClassMappingBuilder

| method | `entries[]` fields |
|---|---|
| `Rename(javaMember, argCount, csharpMember, ...)` | `kind: rename` |
| `RenameVariadic(javaMember, csharpMember, ...)` | `arg_count: variadic`, `kind: rename` |
| `Property(javaMember, argCount, csharpMember, ...)` | `kind: property` |
| `Template(javaMember, argCount, expression, ...)` | `kind: template` |
| `TemplateVariadic(javaMember, expression, ...)` | `arg_count: variadic`, `kind: template` |
| `Unsupported(javaMember, argCount?, notes, ...)` | `kind: unsupported` |
| `AddField(javaMember, csharpMember, ...)` | `member_type: field`, `kind: rename` |
| `Route(javaMember, memberType, configure)` | routing node with nested `class_map` |

### Example

```csharp
var index = new TypeMappingBuilder()
    .AddNamespace("java.lang", "System")
    .AddNamespace(["java.util"], ["System.Collections.Generic"])

    .AddClass("java.lang.Math", "Math", c => c
        .Rename("pow", 2, "Pow")
        .Rename("abs", 1, "Abs")
        .Template("random", 0, "Random.Shared.NextDouble()")
        .AddField("PI", "PI"))

    .AddClass("java.lang.System", "Console", c => c
        .Route("out", MemberMappingType.Field, n => n
            .AddClass("java.io.PrintStream", "Console", p => p
                .Rename("println", 1, "WriteLine")
                .Rename("print", 1, "Write"))))

    .Build();
```

---

## Part 5: Practical advice

---

## 20. Library pattern catalog

Patterns observed across the nine built-in YAML files:

### Static utility classes (Math, Arrays, Collections, Objects)

Almost entirely `rename` entries. Methods have the same shape in Java and C#.
The only templates are for methods that reorder arguments (e.g.
`Collections.sort(l)` → `l.Sort()`) or have different semantics (e.g.
`Math.round` returns `long` in Java but `Math.Round` returns `double`).

### Wrapper/boxing types (Integer, Long, Double, Float, Short, Byte, Boolean, Character)

Heavy `template` usage because `csharp_class` is a C# keyword (`int`,
`long`, `double`, etc.). Static methods like `parseInt` → `int.Parse` and
constants like `MAX_VALUE` → `int.MaxValue` are templates because the output
must be a bare keyword expression.

Pattern:
```yaml
- java_class: java.lang.Integer
  csharp_class: int
  entries:
    - java_member: parseInt
      arg_count: 1
      kind: template
      expression: "int.Parse($1)"
    - java_member: MAX_VALUE
      member_type: field
      kind: template
      expression: "int.MaxValue"
    - java_member: toString
      arg_count: 1
      kind: template
      expression: "$1.ToString()"
```

### Instance collections (List, ArrayList, Map, HashMap, Set, HashSet)

Mix of:
- **`property`** — `size()` → `Count`, `isEmpty()` → template `$0.Count == 0`
- **`rename`** — `add(x)` → `Add(x)`, `contains(x)` → `Contains(x)`
- **`template`** — `get(i)` → `$0[i]`, `put(k,v)` → `$0[$1] = $2`,
  `iterator()` → `$0.GetEnumerator()`
- **`unsupported`** — `toArray()` (use `.ToArray()`), `stream()` (use LINQ),
  `sort(Comparator)` (rewrite needed)

### Interfaces vs implementations

Map both the interface and the concrete class. The interface carries the
shared members; the concrete class adds its own. Example: `java.util.List`
(interface, `kind: interface`) and `java.util.ArrayList` (class) both have
`add`, `get`, `size`, etc.

### I/O pipeline (File, Files, streams)

Heavy `template` usage because Java I/O APIs map to very different C# APIs:

```yaml
- java_member: exists
  arg_count: 0
  kind: template
  expression: "File.Exists($0)"

- java_member: mkdir
  arg_count: 0
  kind: template
  expression: "Directory.CreateDirectory($0)"
  confidence: medium
  notes: "CreateDirectory returns a DirectoryInfo, not a bool."
```

Stream classes are mostly pure renames: `FileReader` → `StreamReader`,
`FileInputStream` → `FileStream`, `ByteArrayInputStream` → `MemoryStream`.

### Threading (Thread, ConcurrentHashMap, atomic types)

Mixed:
- **`template`** — `Thread.sleep(ms)` → `System.Threading.Thread.Sleep(ms)`,
  `currentThread` → `System.Threading.Thread.CurrentThread`
- **`unsupported`** — atomic types (`AtomicInteger.get()`, `.set()`,
  `.incrementAndGet()`) all map to `Interlocked` or `Volatile` patterns but
  cannot be auto-mapped safely

### Text builders (StringBuilder, StringBuffer)

Mostly `rename` with some `template` for arg reshaping:

```yaml
- java_member: delete
  arg_count: 2
  kind: template
  expression: "$0.Remove($1, $2 - $1)"
  notes: "Java's delete(start, end) becomes Remove(start, length)."

- java_member: deleteCharAt
  arg_count: 1
  kind: template
  expression: "$0.Remove($1, 1)"
  notes: "C# StringBuilder.Remove requires (startIndex, count)."
```

`StringBuffer` is a pure rename to `StringBuilder` with no entries needed.

### Exceptions

Map with `kind: exception`. Members use `rename` for `getMessage` → `Message`,
and the class itself renames (e.g. `NumberFormatException` → `FormatException`).

---

## 21. Tips and tricks

### Collection patterns

| Pattern | Java | C# | Entry |
|---|---|---|---|
| Size check | `list.size()` | `list.Count` | `property` |
| Empty check | `list.isEmpty()` | `list.Count == 0` | `template: "$0.Count == 0"` |
| Index access | `list.get(i)` | `list[i]` | `template: "$0[$1]"` |
| Index set | `list.set(i, v)` | `list[i] = v` | `template: "$0[$1] = $2"` |
| Map get | `map.get(k)` | `map[k]` | `template: "$0[$1]"` |
| Map put | `map.put(k, v)` | `map[k] = v` | `template: "$0[$1] = $2"` |
| Iterate | `list.iterator()` | `list.GetEnumerator()` | `template: "$0.GetEnumerator()"` |
| Sort | `Collections.sort(l)` | `l.Sort()` | `template: "$1.Sort()"` |
| Array fill | `Arrays.fill(a, v)` | `Array.Fill(a, v)` | `template: "Array.Fill($1, $2)"` |

### Static method patterns

| Pattern | Java | C# | Entry |
|---|---|---|---|
| Parse | `Integer.parseInt(s)` | `int.Parse(s)` | `template: "int.Parse($1)"` |
| ValueOf (same type) | `Integer.valueOf(i)` | `i` | `template: "$1"` |
| ValueOf (string) | `Integer.valueOf(s)` | `int.Parse(s)` | `template: "int.Parse($1)"` |
| ToString | `Integer.toString(i)` | `i.ToString()` | `template: "$1.ToString()"` |
| Max value | `Integer.MAX_VALUE` | `int.MaxValue` | `template: "int.MaxValue"` |
| Static method → instance | `Collections.sort(l)` | `l.Sort()` | `template: "$1.Sort()"` |

### Use `notes` liberally

The `notes` field appears as a comment above `TODO` and `REVIEW` markers.
Use it to explain:
- Why the mapping is semantically imperfect
- What the developer should check
- What the C# equivalent requires (extra `using`, different pattern, etc.)

### Use `confidence: medium` proactively

Don't wait for semantic analysis — if you know the mapping has edge cases,
mark it medium upfront. This saves time during review.

---

## 22. Common mistakes

### Forgetting `arg_count`

Every method entry **must** have `arg_count` unless `kind: unsupported`. The
load will fail with a clear error, but it's the #1 cause of YAML validation
errors.

### Using namespace-qualified paths in `csharp_class`

```yaml
# WRONG:
csharp_class: System.Console

# RIGHT:
csharp_class: Console
```

Namespace resolution comes from the `namespaces` table, not from
`csharp_class`.

### Missing `namespaces` rows

Every `java_class` package must have a `namespaces` row, or the converter
warns and the output won't compile without a manual `using`.

### Using `property` when the call has arguments

`property` drops parentheses entirely. It's only for zero-argument method
calls that become C# properties. If the method has arguments, use `rename` or
`template`.

### Confusing `$0` between methods and constructors

- **Methods:** `$0` = the receiver expression (e.g. `str` in `str.length()`)
- **Static methods:** `$0` = the C# class name (e.g. `int` in `int.Parse(...)`)
- **Constructors:** `$0` = the C# class name (e.g. `List` in `new List(...)`)

### Duplicate member keys across files

If two loaded files define the same `(java_class, java_member, arg_count)`
with the same (or absent) `type_hint`, the load fails. Split conflicting
entries into one file, or differentiate with `type_hint`.

### Writing a template that emits `new` without the keyword

Templates are filled as raw C# expressions. If your template needs a `new`
expression, include `new` literally:

```yaml
expression: "new Dictionary<string, object>()"
```

---

## 23. Testing methodology

### Unit test pattern

```csharp
[Fact]
public void MyMapping_SomeCall_ConvertsCorrectly()
{
    var index = TypeMappingIndex.LoadFromYaml(yaml);
    var parsed = Convert("public class Foo { void f() { ... } }", index);
    Assert.Contains("expected output", parsed);
}
```

### Expression-only test

For isolated checks on a single expression:

```csharp
private static string ConvertExpression(string javaExpr, TypeMappingIndex index)
{
    var parseResult = new JavaParser().parseExpression(javaExpr);
    var parsedExpr = parseResult.getResult().FromRequiredOptional<Expression>();
    return ExpressionVisitor.VisitExpression(
        new ConversionContext(Options(index)), parsedExpr)?.ToFullString() ?? "";
}
```

### Integration test

Convert a full Java file and search the output:
- `TODO(java-to-csharp)` — unmapped or unsupported members
- `REVIEW(java-to-csharp)` — low/medium confidence mappings

### Determinism

Given the same mapping set and the same source file, output must be
byte-identical across runs.

### Running the test suite

```bash
dotnet test
```

The full test suite must pass before shipping a mapping change.

---

## 24. Checklist for writing a mapping file

1. **Every `java_class` package must have a `namespaces` row.**
2. **Use bare identifiers** for `csharp_class`/`csharp_member` — never
   namespace-qualified paths.
3. **Always set `arg_count`** on method entries (unless `unsupported`).
4. **Prefer `rename`** when the call shape matches.
5. **Use `property`** when a Java method call becomes a C# property access
   (zero args, no return value mutation).
6. **Use `template`** when you need arg reshaping — put the receiver as `$0`,
   arguments as `$1`…
7. **Constructor mappings** use `member_type: constructor` and `java_member:
   <init>`. For constructor templates, `$0` is the C# class name.
8. **Set `confidence: medium`** and `notes` on any entry whose C# translation
   is semantically imperfect.
9. **Use `type_hint` only for genuine same-arity overloads** with different C#
   targets.
10. **Split files along team/project boundaries** and rely on merging: same
    `java_class` roots merge, member keys must not collide.
11. **Test iteratively:** run the CLI on a small file, then search the output
    for `TODO` and `REVIEW` to find gaps.
12. **Map both interfaces and implementations** when Java code uses both.
13. **Mark semantically imperfect mappings as `unsupported`** with helpful
    `notes` rather than writing a bad template.

---

## 25. Open items for future versions

- Structural/statement-level transforms (try-with-resources, streams, etc.)
- Multi-file/cross-file type resolution beyond the simple-name fallback,
  import-based resolution, and heuristic inference
- Method-call chain resolution through bare method call roots is partially
  handled via `returns_type`/`GenericType` (§9.2); chains whose root return
  type is neither declared in the mapping set nor inferable still fall through
  to default casing
- Exception hierarchy mapping beyond simple renames
- Template expressions needing a `using` outside the source class's own
  package (`using`s are inferred from the `namespaces` table and from the
  mapping index for mapped packages; arbitrary C# namespaces inside a template
  are not yet added to the output's `using`s)
