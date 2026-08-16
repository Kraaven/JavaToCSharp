# Java to C# Converter

[![.NET Build](https://github.com/paulirwin/JavaToCSharp/actions/workflows/build.yml/badge.svg)](https://github.com/paulirwin/JavaToCSharp/actions/workflows/build.yml) [![Nuget](https://img.shields.io/nuget/v/JavaToCSharp)](https://www.nuget.org/packages/JavaToCSharp/)


Java to C# converter. 
Uses [JavaParser](https://github.com/javaparser/javaparser) to parse the Java source code text, 
[IKVM.NET](https://github.com/ikvmnet/ikvm/) to convert the javaparser .jar into a .NET .dll, 
and [Roslyn](https://github.com/dotnet/roslyn) for C# AST generation. 

Pull requests and issue submission welcome.

## Getting Started

Clone the repo, build, and launch the Gui WPF app. Click the "..." button on
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

## Syntax Mappings

By default, JavaToCSharp translates some usual Java classes and methods into their C# counterparts 
(e.g. Java maps are converted into dictionaries).
You can specify additional mappings to fine tune the translation of the syntactic elements.

The mappings are specified in a yaml file with root keys that represent the kind of mapping,
each having a set of key-value pairs that specify the java to C# mappings:

- `ImportMappings`: Mappings from Java package names to the C# namespaces.
  If a key-value pair has an empty value, the package will be removed from the resulting C#.
- `VoidMethodMappings`: Mappings from unqualified Java void methods to C#. 
- `NonVoidMethodMappings`: Same as before, but for non-void java methods.
- `AnnotationMappings`: Mappings from Java method annotations to C#.

For example, to convert *JUnit* tests into *xUnit* you can create this mapping file:
```yaml
ImportMappings:
  org.junit.Test : Xunit
  org.junit.Assert.assertEquals : ""
  org.junit.Assert.assertTrue : ""
VoidMethodMappings:
  assertEquals : Assert.Equal
  assertTrue : Assert.True
AnnotationMappings:
  Test : Fact
```

If you specify this file in the `--mappings-file` CLI argument, the conversion of this JUnit test:
```Java
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;
import org.junit.Test;
public class MappingsTest {
  @Test
  public void testAsserts() {
    assertEquals("a", "a");
    assertTrue(true);
  }
}
```

will produce this xUnit test:
```csharp
using Xunit;

public class MappingsTest
{
  [Fact]
  public virtual void TestAsserts()
  {
    Assert.Equal("a", "a");
    Assert.True(true);
  }
}
```

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
