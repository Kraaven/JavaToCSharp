# Java to C# Converter

[![.NET Build](https://github.com/paulirwin/JavaToCSharp/actions/workflows/build.yml/badge.svg)](https://github.com/paulirwin/JavaToCSharp/actions/workflows/build.yml) [![Nuget](https://img.shields.io/nuget/v/JavaToCSharp)](https://www.nuget.org/packages/JavaToCSharp/)


Java to C# converter. 
Uses [JavaParser](https://github.com/javaparser/javaparser) to parse the Java source code text, 
[IKVM.NET](https://github.com/ikvmnet/ikvm/) to convert the javaparser .jar into a .NET .dll, 
and [Roslyn](https://github.com/dotnet/roslyn) for C# AST generation. 

Pull requests and issue submission welcome.

## Getting Started

Clone the repo, build, and launch the Gui Avalonia app. Click the "..." button on
the left side to load a Java file, and then click Convert to convert to
C# on the right side. Please note that conversion may take up to a few
minutes on large files.

Alternatively, launch the command line (Cli) version to process files
from the command line.

The core library is installable via NuGet at https://www.nuget.org/packages/JavaToCSharp/

## Library API

### ConvertText (single file / string)

`JavaToCSharpConverter.ConvertText(string javaText, ...)` returns a `ConversionResult` and **never throws** for
parse or conversion failures. Check `result.FailedCount` and read the generated C# from `result.ConvertedText`:

```csharp
var result = JavaToCSharpConverter.ConvertText(javaSource, options);

if (result.FailedCount > 0)
{
    Console.WriteLine($"Conversion failed: {result.FailedFiles[0].ErrorMessage}");
    return;
}

Console.WriteLine(result.ConvertedText);
```

`ConversionResult` also exposes `TotalFiles`, `SucceededFiles`, `FailedFiles`, `Exceptions`, `Elapsed` and
`FilesPerSecond` for reporting.

### ConvertDirectoryAsync (parallel, multi-file)

Convert every `*.java` file under a directory in parallel:

```csharp
var result = await JavaToCSharpConverter.ConvertDirectoryAsync(
    new DirectoryInfo("C:\\src\\java"),
    new DirectoryInfo("C:\\src\\csharp"));   // mirrored output tree; originals untouched

Console.WriteLine($"Converted {result.SucceededCount}/{result.TotalFiles} in {result.Elapsed}");
```

> **WARNING:** passing `outputDirectory: null` enables **destructive replace mode** — each `Foo.java` is
> converted in place to `Foo.cs` and the original `.java` file is **deleted** after a successful write.
> A failed file's `.java` is preserved. Prefer the non-destructive form unless you are sure.

Conversion is tunable via `ConvertDirectoryOptions` (`MaxDegreeOfParallelism`, `Overwrite`,
`ContinueOnError`), and per-file progress can be observed with `IProgress<FileConversionProgress>`
plus a `CancellationToken`.

### Per-file logging (optional)

Set `JavaConversionOptions.EnableLogging` to `true` to log every file as it is converted
(`path -> OK`, `path -> SKIPPED (output already exists)`, or `path -> FAILED: <exception>`).
Directory conversions prefix each line with `[index/total]`. The master switch is off by default;
each sink has its own switch: `LogToConsole`, `LogToCallback` (with `Log`),
and `LogToFile` (with `LogFilePath`, default `conversion.log`).

```csharp
var options = new JavaConversionOptions
{
    EnableLogging = true,
    LogToConsole = true,
    LogToFile = true,
    LogFilePath = "conversion.log",
};
```

### Preserving Java member names (cross-file conversions)

By default (`JavaConversionOptions.PreserveJavaMethodNames = true`), method, field and member names keep
their original Java casing and common Java calls such as `size()`, `get(i)`, `set(i, v)`, `length()` and
`compare` are **not** rewritten to C# properties/indexers. This keeps individually converted files
call-compatible with each other (e.g. a converted `Vec3i` still exposes `getX()`/`offset()`, so a converted
`BlockPos` that calls them still links). Only `equals`/`hashCode`/`toString`/`getClass` are mapped to the
C# `Equals`/`GetHashCode`/`ToString`/`GetType` equivalents, since C# requires them.

Set `PreserveJavaMethodNames = false` to get C#-style PascalCase method names (`size()` -> `Size()`,
`get(i)` -> `Get(i)`, `length()` -> `Length()`). Member accesses are never rewritten to C# properties
or indexers by default; property/indexer rewrites are only applied explicitly via a member mapping set
(see below). The CLI flag is `--preserve-java-method-names` (default `true`) and the GUI exposes the same
toggle in Settings.

## Member Mapping Sets (member-level rewrites)

By default, JavaToCSharp translates some usual Java classes and methods into their C# counterparts
(e.g. Java maps are converted into dictionaries). For finer control — renaming a static call
(`Math.abs(x)` -> `Math.Abs(x)`), turning a call into a C# property (`String.length()` -> `str.Length`),
rewriting whole chains (`System.out.println(x)` -> `Console.WriteLine(x)`), or flagging code with no safe
equivalent — the converter accepts YAML **mapping sets** with a `namespaces` and a `class_map` section:

```yaml
namespaces:
  - java_packages: [java.lang]
    csharp_packages: [System]
  - java_packages: [java.io]
    csharp_packages: [System]

class_map:
  - java_class: java.lang.Math
    csharp_class: Math
    kind: class
    entries:
      - java_member: abs
        arg_count: 1
        kind: rename
        csharp_member: Abs
      - java_member: random
        arg_count: 0
        kind: template
        expression: "new Random().NextDouble()"
        confidence: medium
        notes: "No direct static C# equivalent; flag for review."
      - java_member: PI
        member_type: field
        kind: rename
        csharp_member: PI
  - java_class: java.lang.System
    csharp_class: System
    entries:
      - java_member: out
        member_type: field
        kind: property
        csharp_member: Out
        class_map:
          - java_class: java.io.PrintStream
            csharp_class: Console
            entries:
              - java_member: println
                arg_count: 1
                kind: rename
                csharp_member: WriteLine
```

- `namespaces` maps Java packages to the C# namespaces the converter adds as `using` directives
  (longest prefix wins).
- `class_map` entries declare `java_class`/`csharp_class` and their `entries` (methods/fields).
  Entries support `rename` (method stays a call), `property` (call becomes a property access, parens
  dropped), `template` (free-form `expression` with `$0` = receiver and `$1..$n`/`$rest` = arguments),
  and `unsupported` (emits a `/* TODO(java-to-csharp): ... */` block marker). `arg_count` may be an
  integer or `variadic`; `type_hint` disambiguates overloads. `confidence: medium|low` emits a
  `/* REVIEW(java-to-csharp): ... */` block marker.
- Nested `class_map` under an entry routes chains: `System.out.println(x)` above becomes
  `Console.WriteLine(x)`, eliding the `out` routing node.

Given the mapping file above, `double d = Math.abs(-1.0);` converts to `double d = Math.Abs(-1.0);`.

CLI: pass `--mapping-file <path>` (repeatable; multiple files are merged, later files win on conflicts):

```
JavaToCSharpCli --mapping-file java-mappings.yaml file Foo.java
```

GUI: add mapping files under the **Mappings** tab in Settings; they apply to every conversion and are
persisted. Library users set `options.MappingIndex = TypeMappingIndex.LoadFromFiles([...])` or build
the index in C# with `new TypeMappingBuilder()...Build()`.

### Built-in mapping specs

For common Java API families, the converter bundles nine curated mapping specs that work exactly like
YAML mapping files but are built in — no file to find or author. They are strictly opt-in:

| CLI flag | spec |
|---|---|
| `--console-mapping` | Console: `System.out.println(x)` → `Console.WriteLine(x)`, `System.exit`, `currentTimeMillis` |
| `--system-mapping` | System: `Math`, `Integer`/`Long`/… boxing types, `String`, `Object` |
| `--collections-mapping` | System.Collections: `java.util` `List`/`Map`/`Set`, `Collections`, `Arrays`, `Objects` |
| `--io-mapping` | System.IO: `java.io` / `java.nio.file` `File`, `Files`, stream types |
| `--threading-mapping` | System.Threading: `Thread`, `java.util.concurrent` |
| `--text-mapping` | System.Text: `StringBuilder`, `MessageFormat` |
| `--time-mapping` | System.Time: `java.time` `LocalDate`, `Instant`, `Duration`, `ZoneId` |
| `--regex-mapping` | System.Text.RegularExpressions: `Pattern` → `Regex`, `Matcher` → `Match` |
| `--numerics-mapping` | System.Numerics: `BigInteger`, `BigDecimal` |

Use `--all-mapping` to enable every built-in spec at once.

```
JavaToCSharpCli --console-mapping --collections-mapping file Foo.java
```

GUI: check the specs under the **Mappings** tab in Settings. Library users call
`BuiltInMappings.Load(BuiltInMappingSpec.Console, ...)` or add specs to a builder:
`new TypeMappingBuilder().AddBuiltIn(BuiltInMappingSpec.Console).AddYamlFiles([...]).Build()`.

See [MAPPING_FILES.md](MAPPING_FILES.md) for the full YAML schema, chain-resolution rules, the built-in
specs table, and step-by-step instructions for writing and testing mapping files.

## .NET Support

Trunk will generally always target the latest LTS version of .NET for the core library and the CLI/GUI apps.
If you require running the app on a prior version of .NET, please see the historical releases.
Where STS BCL or preview (as of latest LTS SDK) C# language features are used in the converter, they will be options that are disabled by default.
These options may become enabled by default when the next LTS ships, as a major version bump.

.NET Framework is no longer supported and will not be supported going forward.
This includes not just for the core library (i.e. .NET Standard will not be supported) and running the CLI/GUI apps, 
but also for the C# code generation where applicable.

Bug fixes and features may be backported to the latest major version that targets an actively-supported, non-latest .NET LTS,
but only based on community interest.
Please submit an issue if you want a bugfix or feature backported; better yet, please submit a PR.
Only clean cherry-picks or minor merges will be supported for backports; significant/messy merges or PRs will likely not be supported or merged.

## License for JavaParser

Licensed under the Apache License available at https://github.com/javaparser/javaparser/blob/master/LICENSE.APACHE
