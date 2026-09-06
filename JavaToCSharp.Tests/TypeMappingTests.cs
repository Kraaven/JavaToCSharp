using com.github.javaparser;
using com.github.javaparser.ast.expr;
using JavaToCSharp.Expressions;
using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

public class TypeMappingTests
{
    private const string BasicYaml = """
        namespaces:
          - java_packages: [java.lang]
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
        """;

    private static JavaConversionOptions Options(TypeMappingIndex index, bool preserve = true) => new()
    {
        IncludeNamespace = false,
        IncludeComments = false,
        PreserveJavaMethodNames = preserve,
        MappingIndex = index,
    };

    private static string Convert(string java, TypeMappingIndex index, bool preserve = true)
        => JavaToCSharpConverter.ConvertText(java, Options(index, preserve)).ConvertedText ?? "";

    private static string ConvertWithWarnings(
        string java,
        TypeMappingIndex index,
        out IReadOnlyList<ConversionWarningEventArgs> warnings)
    {
        var captured = new List<ConversionWarningEventArgs>();
        var result = JavaToCSharpConverter.ConvertText(java, Options(index), null, null, warning: captured.Add);
        warnings = captured;
        return result.ConvertedText ?? "";
    }

    private static string ConvertExpression(string javaExpr, TypeMappingIndex index, bool preserve = true)
    {
        ParseResult parseResult = new JavaParser().parseExpression(javaExpr);
        Expression parsedExpr = parseResult.getResult().FromRequiredOptional<Expression>();
        return ExpressionVisitor.VisitExpression(new ConversionContext(Options(index, preserve)), parsedExpr)?.ToFullString() ?? "";
    }

    // ---------- Loading and validation ----------

    [Fact]
    public void Load_ValidYaml_PopulatesIndex()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);

        Assert.True(index.TryGetByQualifiedName("java.lang.Math", out var math));
        Assert.NotNull(math);
        Assert.Equal("Math", math.CSharpClass);
        Assert.Equal(ClassMappingKind.Class, math.Kind);
        Assert.Equal(3, math.Entries.Count);
        Assert.Equal("abs", math.Entries[0].JavaMember);
        Assert.Equal(MemberMappingKind.Rename, math.Entries[0].Kind);
        Assert.Equal("Abs", math.Entries[0].CSharpMember);
        Assert.Equal(1, math.Entries[0].ArgCount);
        Assert.Equal(MappingConfidence.Medium, math.Entries[1].Confidence);
        Assert.Equal(MemberMappingType.Field, math.Entries[2].MemberType);

        Assert.True(index.TryGetCSharpPackages("java.lang", out var packages));
        Assert.Equal(["System"], packages);
    }

    [Fact]
    public void Load_Namespaces_ExactAndPrefixMatching()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java]
                csharp_packages: [Java.Runtime]
              - java_packages: [java.util]
                csharp_packages: [System.Collections]
            """);

        Assert.True(index.TryGetCSharpPackages("java.util", out var exact));
        Assert.Equal(["System.Collections"], exact);
        Assert.True(index.TryGetCSharpPackages("java.util.concurrent", out var prefixed));
        Assert.Equal(["System.Collections"], prefixed);
        Assert.True(index.TryGetCSharpPackages("java.io", out var parent));
        Assert.Equal(["Java.Runtime"], parent);
    }

    [Fact]
    public void Load_ConflictingNamespaceMappings_Throw()
    {
        var ex = Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util]
                csharp_packages: [System.Collections]
              - java_packages: [java.util]
                csharp_packages: [System.Collections.Generic]
            """));
        Assert.Contains("java.util", ex.Message);
    }

    [Fact]
    public void Load_MissingJavaPackages_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - csharp_packages: [System]
            """));
        Assert.Contains("java_packages", ex.Message);
    }

    [Fact]
    public void Load_MethodWithoutArgCount_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
            """));
        Assert.Contains("arg_count", ex.Message);
        Assert.Contains("java.lang.Math", ex.Message);
    }

    [Fact]
    public void Load_FieldWithArgCount_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: PI
                    member_type: field
                    arg_count: 0
            """));
        Assert.Contains("must not declare 'arg_count'", ex.Message);
    }

    [Fact]
    public void Load_InvalidMemberKind_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    kind: bizarre
            """));
        Assert.Contains("kind", ex.Message);
    }

    [Fact]
    public void Load_InvalidConfidence_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    confidence: very
            """));
        Assert.Contains("confidence", ex.Message);
    }

    [Fact]
    public void Load_TemplateWithoutExpression_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: random
                    arg_count: 0
                    kind: template
            """));
        Assert.Contains("expression", ex.Message);
    }

    [Fact]
    public void Load_RenameWithoutCSharpMember_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    kind: rename
            """));
        Assert.Contains("csharp_member", ex.Message);
    }

    [Fact]
    public void Load_InvalidJavaClassName_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.9lives
                csharp_class: Math
            """));
        Assert.Contains("java_class", ex.Message);
    }

    [Fact]
    public void Load_RoutingNodeWithoutCSharpMember_IsValid()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: out
                    member_type: field
                    class_map:
                      - java_class: java.io.PrintStream
                        csharp_class: Console
                        entries:
                          - java_member: println
                            arg_count: 1
                            kind: rename
                            csharp_member: WriteLine
            """);

        Assert.True(index.TryGetByQualifiedName("java.lang.System", out var system));
        Assert.NotNull(system);
        Assert.Single(system.Entries);
        Assert.Null(system.Entries[0].CSharpMember);
        Assert.Single(system.Entries[0].ClassMap);
        Assert.Equal("java.io.PrintStream", system.Entries[0].ClassMap[0].JavaClass);
    }

    [Fact]
    public void Load_CannotLoadInvalidYaml()
    {
        Assert.Throws<MappingValidationException>(() => TypeMappingIndex.LoadFromYaml("not: [valid"));
    }

    [Fact]
    public void Load_SameClassAcrossFiles_MergesEntries()
    {
        const string a = """
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    csharp_member: Abs
            """;
        const string b = """
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: pow
                    arg_count: 2
                    csharp_member: Pow
            """;

        var index = TypeMappingIndex.LoadFromSources([("a.yml", a), ("b.yml", b)]);

        Assert.True(index.TryGetByQualifiedName("java.lang.Math", out var math));
        Assert.NotNull(math);
        Assert.Equal(2, math.Entries.Count);
        Assert.Contains(math.Entries, e => e.JavaMember == "abs");
        Assert.Contains(math.Entries, e => e.JavaMember == "pow");
    }

    [Fact]
    public void Load_ConflictingEntriesAcrossFiles_Throw()
    {
        const string a = """
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    csharp_member: Abs
            """;
        const string b = """
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    csharp_member: Abs
            """;

        var ex = Assert.Throws<MappingValidationException>(() =>
            TypeMappingIndex.LoadFromSources([("a.yml", a), ("b.yml", b)]));
        Assert.Contains("duplicate entry", ex.Message);
        Assert.Contains("a.yml", ex.Message);
        Assert.Contains("b.yml", ex.Message);
    }

    [Fact]
    public void Load_CSharpClassMismatchAcrossFiles_Throws()
    {
        const string a = """
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
            """;
        const string b = """
            class_map:
              - java_class: java.lang.Math
                csharp_class: SystemMath
            """;

        var ex = Assert.Throws<MappingValidationException>(() =>
            TypeMappingIndex.LoadFromSources([("a.yml", a), ("b.yml", b)]));
        Assert.Contains("different csharp_class", ex.Message);
    }

    [Fact]
    public void Load_TypeHintDistinguishesSameArityEntries()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Integer
                csharp_class: Integer
                entries:
                  - java_member: valueOf
                    arg_count: 1
                    type_hint: [int]
                    csharp_member: FromInt32
                  - java_member: valueOf
                    arg_count: 1
                    type_hint: [long]
                    csharp_member: FromInt64
            """);

        Assert.True(index.TryGetByQualifiedName("java.lang.Integer", out var integer));
        Assert.NotNull(integer);
        Assert.Equal(2, integer.Entries.Count);
    }

    [Fact]
    public void Load_SimpleNameAmbiguity_IsDetected()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.io.Reader
                csharp_class: TextReader
              - java_class: java.sql.Reader
                csharp_class: SqlReader
            """);

        Assert.False(index.TryGetBySimpleName("Reader", out _, out var ambiguous));
        Assert.True(ambiguous);
        Assert.True(index.TryGetByQualifiedName("java.io.Reader", out var ioReader));
        Assert.NotNull(ioReader);
        Assert.Equal("TextReader", ioReader.CSharpClass);
    }

    [Fact]
    public void Load_LoadFromFiles_IsDeterministic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jtoc-mapping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "b.yml"), "class_map:\n  - java_class: java.lang.B\n    csharp_class: B\n");
            File.WriteAllText(Path.Combine(dir, "a.yml"), "class_map:\n  - java_class: java.lang.A\n    csharp_class: A\n");

            var index = TypeMappingIndex.LoadFromFiles([Path.Combine(dir, "b.yml"), Path.Combine(dir, "a.yml")]);
            Assert.Equal(["java.lang.A", "java.lang.B"], index.Classes.Select(c => c.JavaClass).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- Resolution ----------

    [Fact]
    public void Resolve_Rename_StaticClassCall()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);
        var parsed = Convert("public class Foo { double d = Math.abs(-1.0); }", index);

        Assert.Contains("Math.Abs(-1.0)", parsed);
        Assert.DoesNotContain("Math.abs", parsed);
    }

    [Fact]
    public void Resolve_FullyQualifiedRoot()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);
        var parsed = Convert("public class Foo { double d = java.lang.Math.abs(-1.0); }", index);

        Assert.Contains("Math.Abs(-1.0)", parsed);
    }

    [Fact]
    public void Resolve_FieldRename()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);
        var parsed = Convert("public class Foo { double d = Math.PI; }", index);

        Assert.Contains("Math.PI", parsed);
    }

    [Fact]
    public void Resolve_ChainedRouting_ElidesRoutingNode()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]
              - java_packages: [java.io]
                csharp_packages: [System.IO]

            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: out
                    member_type: field
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
            """);

        var parsed = Convert("""
            public class Foo {
                void f() { System.out.println("hi"); }
            }
            """, index);

        Assert.Contains("Console.WriteLine(\"hi\")", parsed);
        Assert.DoesNotContain("System.out.println", parsed);
    }

    [Fact]
    public void Resolve_ChainedRouting_OverloadByArity()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: out
                    member_type: field
                    class_map:
                      - java_class: java.io.PrintStream
                        csharp_class: Console
                        entries:
                          - java_member: println
                            arg_count: 0
                            kind: rename
                            csharp_member: WriteLine
                          - java_member: println
                            arg_count: 1
                            kind: rename
                            csharp_member: WriteLine
            """);

        Assert.Equal("Console.WriteLine()", ConvertExpression("System.out.println()", index));
        Assert.Equal("Console.WriteLine(\"hi\")", ConvertExpression("System.out.println(\"hi\")", index));
    }

    [Fact]
    public void Resolve_StandaloneRoutingNode_UsesCSharpMember()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
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
                            csharp_member: WriteLine
            """);

        Assert.Equal("Console.Out", ConvertExpression("System.out", index));
        Assert.Equal("Console.WriteLine(\"hi\")", ConvertExpression("System.out.println(\"hi\")", index));
    }

    [Fact]
    public void Resolve_StandaloneRoutingNodeWithoutMember_EmitsTodo()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: out
                    member_type: field
                    class_map:
                      - java_class: java.io.PrintStream
                        csharp_class: Console
            """);

        var parsed = ConvertExpression("System.out", index);
        Assert.Contains("/* TODO(java-to-csharp): no safe mapping for java.lang.System.out", parsed);
        Assert.Contains("System.out", parsed);
    }

    [Fact]
    public void Resolve_LeafAtNonLast_TailConvertedNormally()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: err
                    member_type: field
                    kind: property
                    csharp_member: Error
            """);

        Assert.Equal("Console.Error.println(\"oops\")", ConvertExpression("System.err.println(\"oops\")", index));
        Assert.Equal("Console.Error", ConvertExpression("System.err", index));
    }

    [Fact]
    public void Resolve_MethodCallToProperty_DropsParens()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: currentTimeMillis
                    arg_count: 0
                    kind: property
                    csharp_member: UtcNow
            """);

        Assert.Equal("Console.UtcNow", ConvertExpression("System.currentTimeMillis()", index));
    }

    [Fact]
    public void Resolve_Template_NoPlaceholders()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);
        var parsed = Convert("public class Foo { double d = Math.random(); }", index);

        Assert.Contains("new Random().NextDouble()", parsed);
    }

    [Fact]
    public void Resolve_Template_ReceiverAndArgs()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: out
                    member_type: field
                    class_map:
                      - java_class: java.io.PrintStream
                        csharp_class: Console
                        entries:
                          - java_member: print
                            arg_count: 1
                            kind: template
                            expression: "$0.Write($1)"
            """);

        Assert.Equal("Console.Write(\"hi\")", ConvertExpression("System.out.print(\"hi\")", index));
    }

    [Fact]
    public void Resolve_Template_VariadicRest()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: out
                    member_type: field
                    class_map:
                      - java_class: java.io.PrintStream
                        csharp_class: Console
                        entries:
                          - java_member: printf
                            arg_count: variadic
                            kind: template
                            expression: "$0.Write($rest)"
            """);

        Assert.Equal("Console.Write(\"{0} {1:D}\", name, 5)", ConvertExpression("System.out.printf(\"%s %d\", name, 5)", index));
    }

    [Fact]
    public void Resolve_Template_NamedPlaceholder_ResolvesToReceiver()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: org.slf4j.LoggerFactory
                csharp_class: LoggerFactory
                entries:
                  - java_member: getLogger
                    arg_count: 1
                    kind: template
                    expression: "$FACTORY.CreateLogger($1)"
            """);

        Assert.Equal("LoggerFactory.CreateLogger(\"cat\")", ConvertExpression("LoggerFactory.getLogger(\"cat\")", index));
    }

    [Fact]
    public void Resolve_Template_NamedPlaceholder_WithUnderscore_ResolvesToReceiver()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: com.example.Msal
                csharp_class: Msal
                entries:
                  - java_member: acquireTokenByAuthorizationCode
                    arg_count: 1
                    kind: template
                    expression: "await $0.AcquireTokenByAuthorizationCode($AUTH_CODE).ExecuteAsync()"
            """);

        Assert.Contains("AcquireTokenByAuthorizationCode(Msal).ExecuteAsync", ConvertExpression("Msal.acquireTokenByAuthorizationCode(code)", index));
        Assert.DoesNotContain("$", ConvertExpression("Msal.acquireTokenByAuthorizationCode(code)", index));
    }

    [Fact]
    public void Resolve_GenericMethodTypeArgs()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.util.Collections
                csharp_class: Collections
                entries:
                  - java_member: emptyList
                    arg_count: 0
                    kind: rename
                    csharp_member: EmptyList
            """);

        Assert.Equal("Collections.EmptyList<string>()", ConvertExpression("Collections.<String>emptyList()", index));
    }

    [Fact]
    public void Resolve_TypeHint_DisambiguatesOverloads()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Integer
                csharp_class: Integer
                entries:
                  - java_member: valueOf
                    arg_count: 1
                    type_hint: [int]
                    csharp_member: FromInt32
                  - java_member: valueOf
                    arg_count: 1
                    type_hint: [long]
                    csharp_member: FromInt64
            """);

        Assert.Equal("Integer.FromInt32(5)", ConvertExpression("Integer.valueOf(5)", index));
        Assert.Equal("Integer.FromInt64(5)", ConvertExpression("Integer.valueOf(5L)", index));
    }

    [Fact]
    public void Resolve_TypeHintFallbackToNoHintEntry()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Integer
                csharp_class: Integer
                entries:
                  - java_member: valueOf
                    arg_count: 1
                    type_hint: [int]
                    csharp_member: FromInt32
                  - java_member: valueOf
                    arg_count: 1
                    csharp_member: Parse
            """);

        // Unknown argument type (a variable) falls back to the type-free default entry.
        Assert.Equal("Integer.Parse(x)", ConvertExpression("Integer.valueOf(x)", index));
    }

    [Fact]
    public void Resolve_UnsupportedEntry_EmitsTodo()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: exit
                    arg_count: 1
                    kind: unsupported
                    notes: "Environment.Exit is an application-level decision."
            """);

        var parsed = Convert("""
            public class Foo {
                void f() { System.exit(1); }
            }
            """, index);

        Assert.Contains("/* TODO(java-to-csharp): no safe mapping for java.lang.System.exit — Environment.Exit is an application-level decision.", parsed);
        Assert.Contains("System.exit(1)", parsed);
    }

    [Fact]
    public void Resolve_UnsupportedWithoutNotes_GenericMessage()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: exit
                    arg_count: 1
                    kind: unsupported
            """);

        var parsed = Convert("public class Foo { void f() { System.exit(1); } }", index);
        Assert.Contains("/* TODO(java-to-csharp): no safe mapping for java.lang.System.exit", parsed);
    }

    [Fact]
    public void Resolve_MediumConfidence_EmitsReview()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);
        var parsed = Convert("public class Foo { double d = Math.random(); }", index);

        Assert.Contains("/* REVIEW(java-to-csharp): medium confidence -", parsed);
        Assert.Contains("new Random().NextDouble()", parsed);
    }

    [Fact]
    public void Resolve_HighConfidence_NoReview()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);
        var parsed = Convert("public class Foo { double d = Math.abs(-1.0); }", index);

        Assert.DoesNotContain("REVIEW(java-to-csharp)", parsed);
    }

    [Fact]
    public void Resolve_AmbiguousSimpleName_EmitsTodo()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.io.Reader
                csharp_class: TextReader
                entries:
                  - java_member: read
                    arg_count: 1
                    csharp_member: Read
              - java_class: java.sql.Reader
                csharp_class: SqlReader
                entries:
                  - java_member: read
                    arg_count: 1
                    csharp_member: Read
            """);

        var parsed = ConvertExpression("Reader.read(x)", index);
        Assert.Contains("/* TODO(java-to-csharp)", parsed);
        Assert.Contains("Reader.read(x)", parsed);
    }

    [Fact]
    public void Resolve_UnresolvedVariableReceiver_FallsThrough()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);
        var parsed = Convert("public class Foo { int n = lst.size(); }", index, preserve: false);

        Assert.Contains("lst.Size()", parsed);
        Assert.DoesNotContain("lst.Count", parsed);
    }

    [Fact]
    public void Resolve_UnresolvedWhenNoMappingSet_Unchanged()
    {
        var parsed = JavaToCSharpConverter.ConvertText(
            "public class Foo { void f() { System.out.println(\"hi\"); } }",
            new JavaConversionOptions { IncludeNamespace = false, IncludeComments = false }).ConvertedText ?? "";

        Assert.Contains("System.@out.println(\"hi\")", parsed);
        Assert.DoesNotContain("WriteLine", parsed);
    }

    [Fact]
    public void Resolve_NonChainScope_FallsThrough()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);
        var parsed = Convert("public class Foo { int n = get(0).size(); }", index, preserve: false);

        Assert.Contains("Get(0).Size()", parsed);
    }

    [Fact]
    public void Usings_MappingDerived_SortedAndDeduplicated()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.io]
                csharp_packages: [System.IO]
              - java_packages: [java.lang]
                csharp_packages: [System]
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: out
                    member_type: field
                    class_map:
                      - java_class: java.io.PrintStream
                        csharp_class: Console
                        entries:
                          - java_member: println
                            arg_count: 1
                            csharp_member: WriteLine
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    csharp_member: Abs
            """);

        var parsed = Convert("""
            public class Foo {
                double d = Math.abs(-1.0);
                void f() { System.out.println("hi"); }
            }
            """, index);

        var usingIndex = parsed.IndexOf("using System;", StringComparison.Ordinal);
        var usingIoIndex = parsed.IndexOf("using System.IO;", StringComparison.Ordinal);
        Assert.True(usingIndex >= 0, $"expected 'using System;' in:\n{parsed}");
        Assert.True(usingIoIndex >= 0, $"expected 'using System.IO;' in:\n{parsed}");
        Assert.True(usingIndex < usingIoIndex, "mapping-derived usings must be sorted alphabetically");
    }

    [Fact]
    public void Usings_MappingDerived_NotDuplicatedWithOptionsUsings()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    csharp_member: Abs
            """);

        var options = Options(index);
        options.AddUsing("System");
        var parsed = JavaToCSharpConverter.ConvertText("public class Foo { double d = Math.abs(-1.0); }", options).ConvertedText ?? "";

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(parsed, @"using System;"));
    }

    [Fact]
    public void MissingNamespaceCoverage_Warns()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    csharp_member: Abs
            """);

        var parsed = ConvertWithWarnings("public class Foo { double d = Math.abs(-1.0); }", index, out var warnings);
        Assert.Contains("Math.Abs(-1.0)", parsed);
        var warning = Assert.Single(warnings);
        Assert.Contains("java.lang", warning.Message);
    }

    [Fact]
    public void NestedClassResolvableBySimpleName()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.io.PrintStream
                csharp_class: Console
                entries:
                  - java_member: println
                    arg_count: 1
                    csharp_member: WriteLine
            """);

        Assert.Equal("Console.WriteLine(\"hi\")", ConvertExpression("PrintStream.println(\"hi\")", index));
    }

    [Fact]
    public void MappingAppliesRegardlessOfPreserveJavaMethodNames()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);

        Assert.Equal("Math.Abs(5)", ConvertExpression("Math.abs(5)", index, preserve: true));
        Assert.Equal("Math.Abs(5)", ConvertExpression("Math.abs(5)", index, preserve: false));
    }

    // ---------- Reverse member index ----------

    [Fact]
    public void FindClassesByMember_SingleMatch_ReturnsCandidate()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    kind: rename
                    csharp_member: Abs
            """);

        var candidates = index.FindClassesByMember("append");
        Assert.Single(candidates);
        Assert.Equal("java.lang.StringBuilder", candidates[0].OwnerClass);
    }

    [Fact]
    public void FindClassesByMember_MultipleMatches_ReturnsAll()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: toString
                    arg_count: 0
                    kind: rename
                    csharp_member: ToString
              - java_class: java.lang.Object
                csharp_class: object
                entries:
                  - java_member: toString
                    arg_count: 0
                    kind: rename
                    csharp_member: ToString
            """);

        var candidates = index.FindClassesByMember("toString");
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void FindClassesByMember_FilterByActivePackages()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]
              - java_packages: [com.example]
                csharp_packages: [Example]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
              - java_class: com.example.CustomBuilder
                csharp_class: CustomBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
            """);

        // Without filter: both match
        var all = index.FindClassesByMember("append");
        Assert.Equal(2, all.Count);

        // With filter: only java.lang match
        var filtered = index.FindClassesByMember("append", new HashSet<string> { "java.lang" });
        Assert.Single(filtered);
        Assert.Equal("java.lang.StringBuilder", filtered[0].OwnerClass);
    }

    [Fact]
    public void FindClassesByMember_NoMatch_ReturnsEmpty()
    {
        var index = TypeMappingIndex.LoadFromYaml(BasicYaml);
        var candidates = index.FindClassesByMember("nonExistentMethod");
        Assert.Empty(candidates);
    }

    // ---------- Heuristic type inference ----------

    [Fact]
    public void Heuristic_InferTypeFromUniqueMethod_MapsCall()
    {
        // "append" only exists in StringBuilder → unique match
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
            """);

        // Chain ["unknown", "append"] — "unknown" is not in the index or variable tracker
        // Heuristic sees "append" → unique match is StringBuilder → resolves
        var parsed = Convert("""
            public class Foo {
                void f() {
                    unknown.append("x");
                }
            }
            """, index, preserve: false);

        Assert.Contains("unknown.Append(\"x\")", parsed);
    }

    [Fact]
    public void Heuristic_AmbiguousMethod_FallsThrough()
    {
        // "toString" exists in multiple classes → ambiguous → falls through to default
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: toString
                    arg_count: 0
                    kind: rename
                    csharp_member: ToString
              - java_class: java.lang.Object
                csharp_class: object
                entries:
                  - java_member: toString
                    arg_count: 0
                    kind: rename
                    csharp_member: ToString
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    unknown.toString();
                }
            }
            """, index, preserve: false);

        // Falls through to default: capitalize → ToString()
        Assert.Contains("unknown.ToString()", parsed);
    }

    [Fact]
    public void Heuristic_FieldAccess_ChainWithUnknownRoot()
    {
        // A.sb.append("x") where A is not in the index
        // "append" is unique to StringBuilder → infer A.sb is StringBuilder
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    A.sb.append("x");
                }
            }
            """, index, preserve: false);

        Assert.Contains("A.sb.Append(\"x\")", parsed);
    }

    [Fact]
    public void Heuristic_UnsupportedEntry_ExcludedFromCandidates()
    {
        // "close" is unsupported in StringBuilder → excluded from reverse index
        // The heuristic can't resolve it → falls through to default conversion
        // which maps "close" → "Dispose" via ReplaceCommonMethodNames
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
                  - java_member: close
                    arg_count: 0
                    kind: unsupported
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    unknown.close();
                }
            }
            """, index, preserve: false);

        // "close" is unsupported → heuristic can't resolve → default conversion → "Dispose" (hardcoded)
        Assert.Contains("unknown.Dispose()", parsed);
    }

    // ---------- Import-based class name resolution ----------

    [Fact]
    public void ImportBased_ResolveImportedClassName()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
            """);

        // StringBuilder is auto-imported via java.lang.* → TryResolveClassName should find it
        var parsed = Convert("""
            import java.lang.StringBuilder;
            public class Foo {
                void f() {
                    StringBuilder sb = new StringBuilder();
                    sb.append("x");
                }
            }
            """, index, preserve: false);

        Assert.Contains("sb.Append(\"x\")", parsed);
    }

    // ---------- Feature 4: Chain resolution with intermediate method calls ----------

    [Fact]
    public void ChainedCall_VariableScope_ReNamesOuterCall()
    {
        // sb.append("x").deleteCharAt(3) — sb is a StringBuilder
        // ChainExtractor returns null (intermediate MethodCallExpr), so the
        // MethodCallExpressionVisitor's chained-casing path kicks in:
        // walks to root "sb", finds StringBuilder, renames deleteCharAt → Remove.
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
                  - java_member: deleteCharAt
                    arg_count: 1
                    kind: rename
                    csharp_member: Remove
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    StringBuilder sb = new StringBuilder();
                    sb.append("x").deleteCharAt(3);
                }
            }
            """, index, preserve: false);

        Assert.Contains("Append", parsed);
        Assert.Contains("Remove", parsed);
    }

    [Fact]
    public void ChainedCall_BareMethodCallRoot_DefaultCapitalizes()
    {
        // getMap().get("key").toString() — root is a bare method call
        // ChainExtractor returns null (intermediate MethodCallExpr).
        // The chained-casing path walks to "getMap" but TryGetVariableType fails.
        // Falls through to default: capitalize all method names.
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util, java.lang]
                csharp_packages: [System.Collections.Generic, System]

            class_map:
              - java_class: java.util.Map
                csharp_class: Dictionary
                entries:
                  - java_member: get
                    arg_count: 1
                    kind: rename
                    csharp_member: get_Item
              - java_class: java.lang.Object
                csharp_class: object
                entries:
                  - java_member: toString
                    arg_count: 0
                    kind: rename
                    csharp_member: ToString
            """);

        var parsed = ConvertExpression("getMap().get(\"key\").toString()", index, preserve: false);

        // Root is a bare method call → chained-casing can't resolve → default capitalization
        Assert.Contains("ToString", parsed);
        Assert.Contains("Get", parsed);
        Assert.Contains("GetMap", parsed);
    }

    [Fact]
    public void ChainedCall_FindRootReceiverName_BareMethodCall()
    {
        // Verify FindRootReceiverName returns method name for bare calls (not null)
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util]
                csharp_packages: [System.Collections.Generic]

            class_map:
              - java_class: java.util.Map
                csharp_class: Dictionary
                entries:
                  - java_member: get
                    arg_count: 1
                    kind: rename
                    csharp_member: get_Item
            """);

        // getMap().get("key") — root is bare "getMap", chained-casing walks to "getMap"
        // TryGetVariableType fails → falls through to default
        var parsed = ConvertExpression("getMap().get(\"key\")", index, preserve: false);
        Assert.Contains("GetMap", parsed);
        Assert.Contains("Get", parsed);
    }

    [Fact]
    public void ChainedCall_TemplateEntry_FillsTemplate()
    {
        // sb.random() where random is a template entry.
        // The chained-casing path resolves random against StringBuilder's mapping.
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
                  - java_member: random
                    arg_count: 0
                    kind: template
                    expression: "new Random().NextDouble()"
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    StringBuilder sb = new StringBuilder();
                    double v = sb.random();
                }
            }
            """, index, preserve: false);

        Assert.Contains("new Random().NextDouble()", parsed);
    }

    [Fact]
    public void ChainedCall_TemplateOnChain_FillsTemplateOnScope()
    {
        // sb.append("x").random() — append resolves as rename, random as template
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.StringBuilder
                csharp_class: StringBuilder
                entries:
                  - java_member: append
                    arg_count: variadic
                    kind: rename
                    csharp_member: Append
                  - java_member: random
                    arg_count: 0
                    kind: template
                    expression: "new Random().NextDouble()"
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    StringBuilder sb = new StringBuilder();
                    double v = sb.append("x").random();
                }
            }
            """, index, preserve: false);

        Assert.Contains("new Random().NextDouble()", parsed);
    }

    // ---------- Feature 5: Constructor mapping ----------

    [Fact]
    public void ConstructorMapping_Rename_SimpleType()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util]
                csharp_packages: [System.Collections.Generic]

            class_map:
              - java_class: java.util.ArrayList
                csharp_class: List
                entries:
                  - java_member: <init>
                    member_type: constructor
                    arg_count: 0
                    kind: rename
                    csharp_member: List
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    var x = new ArrayList();
                }
            }
            """, index, preserve: false);
        Assert.Contains("new List()", parsed);
    }

    [Fact]
    public void ConstructorMapping_Rename_WithArgs()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util]
                csharp_packages: [System.Collections.Generic]

            class_map:
              - java_class: java.util.ArrayList
                csharp_class: List
                entries:
                  - java_member: <init>
                    member_type: constructor
                    arg_count: 1
                    kind: rename
                    csharp_member: List
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    var x = new ArrayList(otherList);
                }
            }
            """, index, preserve: false);
        Assert.Contains("new List(otherList)", parsed);
    }

    [Fact]
    public void ConstructorMapping_Template_RewritesArgs()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util]
                csharp_packages: [System.Collections.Generic]

            class_map:
              - java_class: java.util.HashMap
                csharp_class: Dictionary
                entries:
                  - java_member: <init>
                    member_type: constructor
                    arg_count: 0
                    kind: template
                    expression: "new Dictionary<string, object>()"
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    var x = new HashMap();
                }
            }
            """, index, preserve: false);
        Assert.Contains("new Dictionary<string, object>()", parsed);
    }

    [Fact]
    public void ConstructorMapping_NoArgs_EmptyConstructor()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util]
                csharp_packages: [System.Collections.Generic]

            class_map:
              - java_class: java.util.ArrayList
                csharp_class: List
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    var x = new ArrayList();
                }
            }
            """, index, preserve: false);
        Assert.Contains("new List()", parsed);
    }

    [Fact]
    public void ConstructorMapping_ArityDisambiguation()
    {
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util]
                csharp_packages: [System.Collections.Generic]

            class_map:
              - java_class: java.util.ArrayList
                csharp_class: List
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
            """);

        var parsed0 = Convert("""
            public class Foo {
                void f() {
                    var x = new ArrayList();
                }
            }
            """, index, preserve: false);
        Assert.Contains("new List<string>()", parsed0);

        var parsed1 = Convert("""
            public class Foo {
                void f() {
                    var x = new ArrayList(other);
                }
            }
            """, index, preserve: false);
        Assert.Contains("new List(other)", parsed1);
    }

    // ---------- Feature 5: returns_type chain resolution ----------

    [Fact]
    public void ChainedCall_GenericType_ResolvesFromFollowOnMember()
    {
        // env.lookup("srv").host() — lookup returns GenericType (any class). The converter
        // looks at the follow-on member "host" to infer the returned type is Server (the only
        // mapped class with a host member) and resolves host() against it.
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [com.example]
                csharp_packages: [Example]

            class_map:
              - java_class: com.example.Environment
                csharp_class: Environment
                entries:
                  - java_member: lookup
                    arg_count: 1
                    kind: template
                    expression: "$0.Lookup($1)"
                    returns_type: GenericType
              - java_class: com.example.Server
                csharp_class: Server
                entries:
                  - java_member: host
                    arg_count: 0
                    kind: rename
                    csharp_member: Host
              - java_class: com.example.Port
                csharp_class: Port
                entries:
                  - java_member: portNumber
                    arg_count: 0
                    kind: rename
                    csharp_member: PortNumber
            """);

        var parsed = Convert("""
            public class Foo {
                void f(com.example.Environment env) {
                    String s = env.lookup("srv").host();
                }
            }
            """, index, preserve: false);

        Assert.Contains("env.Lookup(\"srv\").Host", parsed);
    }

    [Fact]
    public void ChainedCall_GenericType_AmbiguousFollowOnMember_FallsThrough()
    {
        // Both Server and Port have a "value" member, so the GenericType look-ahead cannot
        // infer a unique receiver type and falls through to default conversion.
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [com.example]
                csharp_packages: [Example]

            class_map:
              - java_class: com.example.Environment
                csharp_class: Environment
                entries:
                  - java_member: lookup
                    arg_count: 1
                    kind: template
                    expression: "$0.Lookup($1)"
                    returns_type: GenericType
              - java_class: com.example.Server
                csharp_class: Server
                entries:
                  - java_member: value
                    arg_count: 0
                    kind: rename
                    csharp_member: Value
              - java_class: com.example.Port
                csharp_class: Port
                entries:
                  - java_member: value
                    arg_count: 0
                    kind: rename
                    csharp_member: Value
            """);

        var parsed = Convert("""
            public class Foo {
                void f(com.example.Environment env) {
                    String s = env.lookup("srv").value();
                }
            }
            """, index, preserve: false);

        Assert.Contains("env.Lookup(\"srv\").Value()", parsed);
    }

    [Fact]
    public void ChainedCall_ReturnsType_ResolvesFromMethodEntry()
    {
        // Pattern.compile("x").matcher("y").find() — compile's returns_type tells resolver
        // that compile returns java.util.regex.Pattern, so matcher resolves against Pattern,
        // and matcher's returns_type resolves find against Matcher.
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util.regex]
                csharp_packages: [System.Text.RegularExpressions]

            class_map:
              - java_class: java.util.regex.Pattern
                csharp_class: Regex
                entries:
                  - java_member: compile
                    arg_count: 1
                    kind: template
                    expression: "new Regex($1)"
                    returns_type: java.util.regex.Pattern
                  - java_member: matcher
                    arg_count: 1
                    kind: template
                    expression: "$0.Match($1)"
                    returns_type: java.util.regex.Matcher
              - java_class: java.util.regex.Matcher
                csharp_class: Match
                entries:
                  - java_member: find
                    arg_count: 0
                    kind: rename
                    csharp_member: NextMatch
            """);

        var parsed = ConvertExpression("Pattern.compile(\"x\").matcher(\"y\").find()", index, preserve: false);

        // Should resolve find() against Match → NextMatch
        Assert.Contains("NextMatch", parsed);
    }

    [Fact]
    public void ChainedCall_ReturnsType_VariableScopeStillWorks()
    {
        // Variable-scoped chain still works with returns_type present
        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [java.util.regex]
                csharp_packages: [System.Text.RegularExpressions]

            class_map:
              - java_class: java.util.regex.Pattern
                csharp_class: Regex
                entries:
                  - java_member: compile
                    arg_count: 1
                    kind: template
                    expression: "new Regex($1)"
                    returns_type: java.util.regex.Pattern
                  - java_member: matcher
                    arg_count: 1
                    kind: template
                    expression: "$0.Match($1)"
                    returns_type: java.util.regex.Matcher
              - java_class: java.util.regex.Matcher
                csharp_class: Match
                entries:
                  - java_member: find
                    arg_count: 0
                    kind: rename
                    csharp_member: NextMatch
                  - java_member: matches
                    arg_count: 0
                    kind: property
                    csharp_member: Success
            """);

        var parsed = Convert("""
            public class Foo {
                void f() {
                    Pattern p = Pattern.compile("x");
                    Matcher m = p.matcher("y");
                    boolean b = m.find();
                    boolean c = m.matches();
                }
            }
            """, index, preserve: false);

        Assert.Contains("NextMatch", parsed);
        Assert.Contains("Success", parsed);
    }
}
