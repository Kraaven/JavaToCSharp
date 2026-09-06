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

    // ---------- Chained-call resolution via returns_type ----------

    [Fact]
    public void StringBuilderSpec_ChainedAppendDelete_ResolvesAcrossEntries()
    {
        var index = BuiltInMappings.SystemText;
        var parsed = Convert("public class Foo { void f(StringBuilder sb) { sb.append(\"a\").append(\"b\").deleteCharAt(2).toString(); } }", index);

        Assert.Contains("sb.Append(\"a\").Append(\"b\").Remove(2, 1)", parsed);
        Assert.Contains(".ToString()", parsed);
    }

    [Fact]
    public void TimeSpec_LocalDateChain_DayOffsetThenYear()
    {
        var index = BuiltInMappings.Load(BuiltInMappingSpec.SystemTime);
        var parsed = Convert("import java.time.LocalDate; public class Foo { int f() { return LocalDate.now().plusDays(1).getYear(); } }", index);

        Assert.Contains("DateTime.Today.AddDays(1).Year", parsed);
    }

    [Fact]
    public void TimeSpec_LocalDateAtStartOfDay_ReturnsDateTime()
    {
        var index = BuiltInMappings.Load(BuiltInMappingSpec.SystemTime);
        var parsed = Convert("import java.time.LocalDate; public class Foo { void f() { DateTime dt = LocalDate.now().atStartOfDay(); } }", index);

        Assert.Contains("DateTime.Today.Date", parsed);
    }

    [Fact]
    public void RegexSpec_PatternCompileMatcher_ResolvesBothHops()
    {
        var index = BuiltInMappings.Load(BuiltInMappingSpec.SystemTextRegularExpressions);
        var parsed = Convert("import java.util.regex.Pattern; public class Foo { boolean f() { return Pattern.compile(\"x\").matcher(\"y\").matches(); } }", index);

        Assert.Contains("new Regex(\"x\").Match(\"y\").Success", parsed);
    }

    [Fact]
    public void RegexSpec_PatternCompileMatcherFind_ResolvesToNextMatch()
    {
        var index = BuiltInMappings.Load(BuiltInMappingSpec.SystemTextRegularExpressions);
        var parsed = Convert("import java.util.regex.Pattern; public class Foo { boolean f() { return Pattern.compile(\"x\").matcher(\"y\").find(); } }", index);

        Assert.Contains("new Regex(\"x\").Match(\"y\").NextMatch", parsed);
    }

    [Fact]
    public void IOSpec_PathsGetChained_ResolvesToFileAndFullPath()
    {
        var index = BuiltInMappings.SystemIO;
        var parsed = Convert("import java.nio.file.Paths; public class Foo { void f() { Path p = Paths.get(\"a\", \"b\").toAbsolutePath(); File f2 = Paths.get(\"a\", \"b\").toFile(); } }", index);

        Assert.Contains("Path.GetFullPath(Path.Combine(\"a\", \"b\"))", parsed);
        Assert.Contains("new File(Path.Combine(\"a\", \"b\"))", parsed);
        Assert.DoesNotContain("ToFile()", parsed);
        Assert.DoesNotContain("ToAbsolutePath()", parsed);
    }

    // ---------- Concrete collection classes mirror interface members ----------

    [Fact]
    public void CollectionsSpec_HashMapGetPut_UseIndexer()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("public class Foo { void f() { HashMap m = new HashMap(); m.put(\"k\", \"v\"); Object v = m.get(\"k\"); } }", index);

        Assert.Contains("m[\"k\"] = \"v\"", parsed);
        Assert.Contains("v = m[\"k\"]", parsed);
        Assert.DoesNotContain("m.Get(", parsed);
    }

    [Fact]
    public void CollectionsSpec_ArrayListGetSize_UseIndexerAndCount()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("public class Foo { void f() { ArrayList list = new ArrayList(); list.add(\"x\"); Object o = list.get(0); int n = list.size(); } }", index);

        Assert.Contains("list.Add(\"x\")", parsed);
        Assert.Contains("o = list[0]", parsed);
        Assert.Contains("n = list.Count", parsed);
    }

    [Fact]
    public void CollectionsSpec_HashSetAddContains_Resolve()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("public class Foo { void f() { HashSet set = new HashSet(); set.add(\"x\"); boolean c = set.contains(\"x\"); } }", index);

        Assert.Contains("set.Add(\"x\")", parsed);
        Assert.Contains("set.Contains(\"x\")", parsed);
    }

    [Fact]
    public void CollectionsSpec_LinkedListGet_UsesElementAt()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("public class Foo { Object f(LinkedList list) { return list.get(0); } }", index);

        Assert.Contains("list.ElementAt(0)", parsed);
    }

    [Fact]
    public void CollectionsSpec_ListIteratorNext_ResolvesCurrent()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("public class Foo { Object f(ArrayList list) { return list.iterator().next(); } }", index);

        Assert.Contains("list.GetEnumerator().Current", parsed);
    }

    [Fact]
    public void CollectionsSpec_ArraysAsListSize_ResolvesCount()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("import java.util.Arrays; public class Foo { int f() { return Arrays.asList(1, 2, 3).size(); } }", index);

        Assert.Contains("new List<object>", parsed);
        Assert.Contains(".Count", parsed);
        Assert.DoesNotContain(".Count()", parsed);
    }

    [Fact]
    public void CollectionsSpec_MapGetFollowOn_GenericTypeResolves()
    {
        var index = BuiltInMappings.Load(BuiltInMappingSpec.SystemCollections, BuiltInMappingSpec.SystemTime);
        var parsed = Convert("import java.util.HashMap; import java.time.LocalDate; public class Foo { int f() { HashMap m = new HashMap(); return m.get(\"d\").getDayOfYear(); } }", index);

        Assert.Contains("m[\"d\"].DayOfYear", parsed);
    }

    // ---------- Sibling classes ----------

    [Fact]
    public void CollectionsSpec_Random_MapsToSystemRandom()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("public class Foo { void f() { Random r = new Random(); int i = r.nextInt(10); long l = r.nextLong(); boolean b = r.nextBoolean(); } }", index);

        Assert.Contains("r.Next(10)", parsed);
        Assert.Contains("r.NextInt64()", parsed);
        Assert.Contains("r.Next(2) == 0", parsed);
    }

    [Fact]
    public void CollectionsSpec_UUID_MapsToGuid()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("import java.util.UUID; public class Foo { void f() { UUID u = UUID.randomUUID(); UUID p = UUID.fromString(\"00000000-0000-0000-0000-000000000000\"); String s = u.toString(); } }", index);

        Assert.Contains("Guid.NewGuid()", parsed);
        Assert.Contains("Guid.Parse(\"00000000-0000-0000-0000-000000000000\")", parsed);
        Assert.Contains("s = u.ToString()", parsed);
    }

    [Fact]
    public void CollectionsSpec_StackAndPriorityQueue_MappedEntries()
    {
        var index = BuiltInMappings.SystemCollections;
        var parsed = Convert("public class Foo { Object f() { Stack s = new Stack(); s.push(\"x\"); PriorityQueue q = new PriorityQueue(); q.offer(\"y\"); return s.pop(); } }", index);

        Assert.Contains("s.Push(\"x\")", parsed);
        Assert.Contains("q.Enqueue(\"y\")", parsed);
        Assert.Contains("s.Pop()", parsed);
    }

    // ---------- Numerics returns_type ----------

    [Fact]
    public void NumericsSpec_BigIntegerValueOfAdd_Resolves()
    {
        var index = BuiltInMappings.Load(BuiltInMappingSpec.SystemNumerics);
        var parsed = Convert("import java.math.BigInteger; public class Foo { void f() { BigInteger a = BigInteger.valueOf(5).add(BigInteger.valueOf(3)); String s = a.toString(); } }", index);

        Assert.Contains("new BigInteger(5) + new BigInteger(3)", parsed);
        Assert.DoesNotContain(".Add(", parsed);
        Assert.Contains("a.ToString()", parsed);
    }

    [Fact]
    public void NumericsSpec_BigDecimalValueOfAdd_Resolves()
    {
        var index = BuiltInMappings.Load(BuiltInMappingSpec.SystemNumerics);
        var parsed = Convert("import java.math.BigDecimal; public class Foo { void f() { BigDecimal a = BigDecimal.valueOf(10).add(BigDecimal.valueOf(5)); } }", index);

        Assert.Contains("new decimal (10) + new decimal (5)", parsed);
        Assert.DoesNotContain(".Add(", parsed);
    }

    // ---------- Threading returns_type ----------

    [Fact]
    public void ThreadingSpec_CurrentThreadGetName_ResolvesToProperty()
    {
        var index = BuiltInMappings.SystemThreading;
        var parsed = Convert("public class Foo { void f() { String n = Thread.currentThread().getName(); } }", index);

        Assert.Contains("System.Threading.Thread.CurrentThread.Name", parsed);
    }

    [Fact]
    public void ThreadingSpec_CompletableFutureGet_ResolvesResult()
    {
        var index = BuiltInMappings.SystemThreading;
        var parsed = Convert("import java.util.concurrent.CompletableFuture; public class Foo { void f() { CompletableFuture<String> cf = CompletableFuture.supplyAsync(() -> \"x\"); String s = cf.get(); } }", index);

        Assert.Contains("Task.Run(() => \"x\")", parsed);
        Assert.Contains("cf.Result", parsed);
    }

    [Fact]
    public void IOSpec_StringWriterToString_HasReturnsType()
    {
        var index = BuiltInMappings.SystemIO;
        var parsed = Convert("public class Foo { String f() { StringWriter w = new StringWriter(); w.write(\"hi\"); return w.toString(); } }", index);

        Assert.Contains("w.Write(\"hi\")", parsed);
        Assert.Contains("w.ToString()", parsed);
    }
}
