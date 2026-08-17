using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

public class BuiltInMappingTests
{
    private static JavaConversionOptions Options(TypeMappingIndex index) => new()
    {
        IncludeNamespace = false,
        IncludeComments = false,
        PreserveJavaMethodNames = true,
        MappingIndex = index,
    };

    private static string Convert(string java, TypeMappingIndex index)
        => JavaToCSharpConverter.ConvertText(java, Options(index)).ConvertedText ?? "";

    // ---------- Loading ----------

    [Fact]
    public void Load_AllSpecs_TogetherAreValid()
    {
        var index = BuiltInMappings.Load(BuiltInMappings.All);

        Assert.True(index.TryGetByQualifiedName("java.lang.System", out var system));
        Assert.NotNull(system);
        Assert.True(index.TryGetByQualifiedName("java.lang.Math", out var math));
        Assert.NotNull(math);
        Assert.Equal("Math", math.CSharpClass);
        Assert.True(index.TryGetByQualifiedName("java.util.List", out var list));
        Assert.NotNull(list);
        Assert.Equal("IList", list.CSharpClass);
        Assert.True(index.TryGetByQualifiedName("java.io.File", out var file));
        Assert.NotNull(file);
        Assert.Equal("File", file.CSharpClass);
        Assert.True(index.TryGetByQualifiedName("java.lang.Thread", out var thread));
        Assert.NotNull(thread);
        Assert.Equal("Thread", thread.CSharpClass);
        Assert.True(index.TryGetByQualifiedName("java.lang.StringBuilder", out var builder));
        Assert.NotNull(builder);
        Assert.Equal("StringBuilder", builder.CSharpClass);
    }

    [Fact]
    public void Load_AllSpecs_NamespaceRowsMerge()
    {
        var index = BuiltInMappings.Load(BuiltInMappings.All);

        Assert.True(index.TryGetCSharpPackages("java.io", out var io));
        Assert.Equal(["System.IO"], io);
        Assert.True(index.TryGetCSharpPackages("java.util.concurrent", out var concurrent));
        Assert.Equal(["System.Threading"], concurrent);
        Assert.True(index.TryGetCSharpPackages("java.lang", out var lang));
        Assert.Equal(["System"], lang);
    }

    [Fact]
    public void YamlFor_EverySpec_IsNonEmptyYaml()
    {
        foreach (var spec in BuiltInMappings.All)
        {
            var yaml = BuiltInMappings.YamlFor(spec);
            Assert.False(string.IsNullOrWhiteSpace(yaml), $"spec {spec} produced empty YAML");
            Assert.Contains("class_map", yaml);
        }
    }

    [Fact]
    public void Load_SingleSpec_OnlyOwnsItsMembers()
    {
        var console = BuiltInMappings.Load(BuiltInMappingSpec.Console);
        Assert.True(console.TryGetByQualifiedName("java.lang.System", out _));
        Assert.False(console.TryGetByQualifiedName("java.lang.Math", out _));
        Assert.False(console.TryGetByQualifiedName("java.util.List", out _));
    }

    [Fact]
    public void Load_IsDeterministicForSameInput()
    {
        var java = """
            public class Foo {
                void f() {
                    System.out.println("hi");
                    double d = Math.abs(-1.0);
                    java.util.Collections.sort(l);
                    long t = Thread.currentThread().getId();
                }
            }
            """;

        var a = BuiltInMappings.Load(BuiltInMappings.All);
        var b = BuiltInMappings.Load(BuiltInMappings.All.Reverse().ToArray());

        Assert.Equal(Convert(java, a), Convert(java, b));
    }

    // ---------- Console spec ----------

    [Fact]
    public void ConsoleSpec_SystemOutPrintln_BecomesWriteLine()
    {
        var index = BuiltInMappings.Console;
        var parsed = Convert("public class Foo { void f() { System.out.println(\"hi\"); } }", index);

        Assert.Contains("Console.WriteLine(\"hi\")", parsed);
        Assert.DoesNotContain("System.out.println", parsed);
    }

    [Fact]
    public void ConsoleSpec_CurrentTimeMillis_BecomesUnixEpoch()
    {
        var index = BuiltInMappings.Console;
        var parsed = Convert("public class Foo { long t = System.currentTimeMillis(); }", index);

        Assert.Contains("DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()", parsed);
    }

    [Fact]
    public void ConsoleSpec_ErrPrintln_RoutesToError()
    {
        var index = BuiltInMappings.Console;
        var parsed = Convert("public class Foo { void f() { System.err.println(\"oops\"); } }", index);

        Assert.Contains("Console.Error.WriteLine(\"oops\")", parsed);
    }

    // ---------- System spec ----------

    [Fact]
    public void SystemSpec_MathAbs_Renames()
    {
        var index = BuiltInMappings.System;
        var parsed = Convert("public class Foo { double d = Math.abs(-1.0); }", index);

        Assert.Contains("Math.Abs(-1.0)", parsed);
    }

    [Fact]
    public void SystemSpec_IntegerParseInt_BecomesIntParse()
    {
        var index = BuiltInMappings.System;
        var parsed = Convert("public class Foo { int n = Integer.parseInt(\"42\"); }", index);

        Assert.Contains("int.Parse(\"42\")", parsed);
    }

    [Fact]
    public void SystemSpec_BoxingTypeConstants_UseTemplates()
    {
        var index = BuiltInMappings.System;
        var parsed = Convert("public class Foo { int m = Integer.MAX_VALUE; }", index);

        Assert.Contains("int.MaxValue", parsed);
    }

    [Fact]
    public void SystemSpec_StringLength_IsProperty()
    {
        var index = BuiltInMappings.System;

        Assert.True(index.TryGetByQualifiedName("java.lang.String", out var str));
        Assert.NotNull(str);
        var length = str.Entries.Single(e => e.JavaMember == "length");
        Assert.Equal(MemberMappingKind.Property, length.Kind);
        Assert.Equal("Length", length.CSharpMember);
    }

    // ---------- System.Collections spec ----------

    [Fact]
    public void CollectionsSpec_ListType_IsRenamed()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("""
            public class Foo {
                void f() {
                    java.util.List<Integer> l = new java.util.ArrayList<>();
                    java.util.Collections.sort(l);
                }
            }
            """, index);

        Assert.Contains("new List<int>()", parsed);
        Assert.DoesNotContain("ArrayList", parsed);
    }

    [Fact]
    public void CollectionsSpec_CollectionsSort_BecomesListSort()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("public class Foo { void f(java.util.List<Integer> l) { java.util.Collections.sort(l); } }", index);

        Assert.Contains("l.Sort()", parsed);
    }

    [Fact]
    public void CollectionsSpec_ArraysFill_BecomesArrayFill()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("public class Foo { void f(int[] a) { java.util.Arrays.fill(a, 0); } }", index);

        Assert.Contains("Array.Fill(a, 0)", parsed);
    }

    // ---------- System.IO spec ----------

    [Fact]
    public void IoSpec_FilesReadString_BecomesReadAllText()
    {
        var index = BuiltInMappings.SystemIO;
        var parsed = Convert("public class Foo { String s = java.nio.file.Files.readString(p); }", index);

        Assert.Contains("File.ReadAllText(p)", parsed);
    }

    [Fact]
    public void IoSpec_FilesExists_BecomesFileExists()
    {
        var index = BuiltInMappings.SystemIO;
        var parsed = Convert("public class Foo { boolean b = java.nio.file.Files.exists(p); }", index);

        Assert.Contains("File.Exists(p)", parsed);
    }

    [Fact]
    public void IoSpec_FileSeparator_BecomesPathSeparator()
    {
        var index = BuiltInMappings.SystemIO;
        var parsed = Convert("public class Foo { char c = java.io.File.separator; }", index);

        Assert.Contains("Path.DirectorySeparatorChar", parsed);
    }

    // ---------- System.Threading spec ----------

    [Fact]
    public void ThreadingSpec_ThreadSleep_IsFullyQualified()
    {
        var index = BuiltInMappings.SystemThreading;
        var parsed = Convert("public class Foo { void f() { Thread.sleep(1000); } }", index);

        Assert.Contains("System.Threading.Thread.Sleep(1000)", parsed);
    }

    [Fact]
    public void ThreadingSpec_CurrentThread_IsProperty()
    {
        var index = BuiltInMappings.SystemThreading;
        var parsed = Convert("public class Foo { Thread t = Thread.currentThread(); }", index);

        Assert.Contains("System.Threading.Thread.CurrentThread", parsed);
    }

    // ---------- System.Text spec ----------

    [Fact]
    public void TextSpec_StringBuilder_AppendIsVariadicRename()
    {
        var index = BuiltInMappings.SystemText;

        Assert.True(index.TryGetByQualifiedName("java.lang.StringBuilder", out var builder));
        Assert.NotNull(builder);
        var append = builder.Entries.Single(e => e.JavaMember == "append");
        Assert.Equal(MemberMappingKind.Rename, append.Kind);
        Assert.Null(append.ArgCount);
        Assert.True(append.IsVariadic);
    }

    [Fact]
    public void TextSpec_StringBuffer_IsPureRename()
    {
        var index = BuiltInMappings.SystemText;

        Assert.True(index.TryGetByQualifiedName("java.lang.StringBuffer", out var buffer));
        Assert.NotNull(buffer);
        Assert.Equal("StringBuilder", buffer.CSharpClass);
        Assert.Empty(buffer.Entries);
    }

    // ---------- Mixing with user YAML ----------

    [Fact]
    public void Builder_MixBuiltInAndYaml_Merges()
    {
        var index = new TypeMappingBuilder()
            .AddBuiltIn(BuiltInMappingSpec.Console)
            .AddYaml("""
                class_map:
                  - java_class: java.lang.Math
                    csharp_class: Math
                    entries:
                      - java_member: abs
                        arg_count: 1
                        kind: rename
                        csharp_member: Abs
                """, "inline-math")
            .Build();

        var parsed = Convert("""
            public class Foo {
                void f() { System.out.println("hi"); }
                double d = Math.abs(-1.0);
            }
            """, index);

        Assert.Contains("Console.WriteLine(\"hi\")", parsed);
        Assert.Contains("Math.Abs(-1.0)", parsed);
    }

    [Fact]
    public void Builder_BuiltInConflictWithUserYaml_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => new TypeMappingBuilder()
            .AddBuiltIn(BuiltInMappingSpec.Console)
            .AddYaml("""
                class_map:
                  - java_class: java.lang.System
                    csharp_class: Console
                    entries:
                      - java_member: gc
                        arg_count: 0
                        kind: rename
                        csharp_member: Collect
                """, "conflicting")
            .Build());

        Assert.Contains("java.lang.System.gc", ex.Message);
    }

    // ---------- Variable-receiver member resolution ----------

    [Fact]
    public void CollectionsSpec_MapVariable_ResolvesPutAndGet()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("""
            public class Foo {
                void f() {
                    java.util.Map<String,Integer> inventory = new java.util.HashMap<>();
                    inventory.put("a", 1);
                    Integer i = inventory.get("a");
                }
            }
            """, index);

        Assert.Contains("inventory[\"a\"] = 1", parsed);
        Assert.Contains("= inventory[\"a\"]", parsed);
    }

    [Fact]
    public void CollectionsSpec_PutWithObjectCreation_KeepsNewKeyword()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("""
            public class Foo {
                static class Item {
                    Item(String n) {}
                }
                void f(java.util.Map<String,Item> m, String name) {
                    m.put(name, new Item(name));
                }
            }
            """, index);

        Assert.Contains("m[name] = new Item(name)", parsed);
        Assert.DoesNotContain("newItem", parsed);
    }

    [Fact]
    public void CollectionsSpec_MapEntry_BecomesKeyValuePair()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("""
            public class Foo {
                void f(java.util.Map.Entry<String,Integer> entry) {
                    Object k = entry.getKey();
                    Object v = entry.getValue();
                }
            }
            """, index);

        Assert.Contains("KeyValuePair<string, int> entry", parsed);
        Assert.Contains("entry.Key", parsed);
        Assert.Contains("entry.Value", parsed);
    }

    [Fact]
    public void CollectionsSpec_ListVariable_ResolvesSizeAndGet()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("""
            public class Foo {
                void f(java.util.List<String> items) {
                    int n = items.size();
                    String first = items.get(0);
                }
            }
            """, index);

        Assert.Contains("items.Count", parsed);
        Assert.Contains("items[0]", parsed);
    }

    [Fact]
    public void Import_MappedPackage_BecomesUsingFromIndex()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("""
            import java.util.List;
            public class Foo {
                void f() { }
            }
            """, index);

        Assert.Contains("using System.Collections.Generic;", parsed);
        Assert.DoesNotContain("using Java.Util;", parsed);
    }

    // ---------- Console / System spec additions ----------

    [Fact]
    public void ConsoleSpec_SystemIn_BecomesConsoleIn()
    {
        var index = BuiltInMappings.Console;
        var parsed = Convert("public class Foo { void f() { System.in.read(); } }", index);

        Assert.Contains("Console.In.read()", parsed);
    }

    [Fact]
    public void SystemSpec_DoubleCompare_BecomesCompareTo()
    {
        var index = BuiltInMappings.System;
        var parsed = Convert("public class Foo { int f(double a, double b) { return Double.compare(a, b); } }", index);

        Assert.Contains("a.CompareTo(b)", parsed);
    }

    [Fact]
    public void SystemSpec_NumberFormatException_MapsToFormatException()
    {
        var index = BuiltInMappings.System;
        var parsed = Convert("""
            public class Foo {
                void f() {
                    try {
                        Integer.parseInt("x");
                    } catch (java.lang.NumberFormatException e) {
                        throw new java.lang.NumberFormatException(e.getMessage());
                    }
                }
            }
            """, index);

        Assert.Contains("catch (FormatException e)", parsed);
        Assert.Contains("new FormatException", parsed);
        Assert.DoesNotContain("NumberFormatException", parsed);
    }

    [Fact]
    public void StringSpec_CompareTo_BecomesStringCompare()
    {
        var index = BuiltInMappings.System;
        var parsed = Convert("public class Foo { int f(String a, String b) { return a.compareTo(b); } }", index);

        Assert.Contains("string.Compare(a, b)", parsed);
        Assert.DoesNotContain("a.CompareTo(b)", parsed);
    }

    [Fact]
    public void StringSpec_CompareToIgnoreCase_BecomesStringCompareOrdinal()
    {
        var index = BuiltInMappings.System;
        var parsed = Convert("public class Foo { int f(String a, String b) { return a.compareToIgnoreCase(b); } }", index);

        Assert.Contains("string.Compare(a, b, StringComparison.OrdinalIgnoreCase)", parsed);
    }

    [Fact]
    public void StringSpec_Split_BecomesRegexSplit()
    {
        var index = BuiltInMappings.System;
        var parsed = Convert("public class Foo { String[] f(String s) { return s.split(\"\\\\s+\"); } }", index);

        Assert.Contains("Regex.Split(s, \"\\\\s+\")", parsed);
        Assert.DoesNotContain("s.Split(", parsed);
    }

    [Fact]
    public void StringBuilderSpec_DeleteCharAt_BecomesRemoveWithCount()
    {
        var index = BuiltInMappings.SystemText;
        var parsed = Convert("public class Foo { void f(StringBuilder sb) { sb.deleteCharAt(3); } }", index);

        Assert.Contains("sb.Remove(3, 1)", parsed);
        Assert.DoesNotContain("sb.Remove(3)", parsed);
    }
}
