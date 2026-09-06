using com.github.javaparser;
using com.github.javaparser.ast.expr;
using JavaToCSharp.Expressions;
using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the 4.0.11 fixes:
/// J — match_qualified_only keeps placeholder classes from hijacking unrelated simple names;
/// E — unsupported-member fragments get Java-only syntax (explicit type witnesses, keyword
///      member names) rewritten before reparse;
/// I — member names that are C# keywords are escaped with '@'.
/// </summary>
public class Round411FixTests
{
    private const string JoggPacketYaml = """
        namespaces:
          - java_packages: [com.jcraft.jogg]
            csharp_packages: [NVorbis, System.IO]

        class_map:
          - java_class: com.jcraft.jogg.Packet
            csharp_class: byte
            kind: class
            match_qualified_only: true
            entries:
              - java_member: packet_base
                member_type: field
                kind: unsupported
        """;

    private const string UnsupportedMemberYaml = """
        namespaces:
          - java_packages: [com.example]
            csharp_packages: [Example]

        class_map:
          - java_class: com.example.Thing
            csharp_class: Thing
            entries:
              - java_member: broken
                arg_count: 1
                kind: unsupported
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

    private static string ConvertExpression(string javaExpr, TypeMappingIndex index, bool preserve = true)
    {
        ParseResult parseResult = new JavaParser().parseExpression(javaExpr);
        Expression parsedExpr = parseResult.getResult().FromRequiredOptional<Expression>();
        return ExpressionVisitor.VisitExpression(new ConversionContext(Options(index, preserve)), parsedExpr)?.ToFullString() ?? "";
    }

    // ---------- J: match_qualified_only ----------

    [Fact]
    public void MatchQualifiedOnly_ClassIsFlagged()
    {
        var index = TypeMappingIndex.LoadFromYaml(JoggPacketYaml);

        Assert.True(index.TryGetByQualifiedName("com.jcraft.jogg.Packet", out var cls));
        Assert.NotNull(cls);
        Assert.True(cls.MatchQualifiedOnly);
        Assert.Equal("byte", cls.CSharpClass);
    }

    [Fact]
    public void MatchQualifiedOnly_ClassIsNotResolvedBySimpleName()
    {
        var index = TypeMappingIndex.LoadFromYaml(JoggPacketYaml);

        Assert.False(index.TryGetBySimpleName("Packet", out _, out _));
        Assert.False(index.TryGetByDottedName("Packet", out _, out _));
        Assert.True(index.TryGetByDottedName("com.jcraft.jogg.Packet", out _, out _));
    }

    [Fact]
    public void MatchQualifiedOnly_SharedSimpleName_IsUnambiguous()
    {
        const string yaml = """
            namespaces:
              - java_packages: [com.example, com.jcraft.jogg]
                csharp_packages: [Example, NVorbis]

            class_map:
              - java_class: com.example.Packet
                csharp_class: Packet
                kind: class
              - java_class: com.jcraft.jogg.Packet
                csharp_class: byte
                kind: class
                match_qualified_only: true
            """;

        var index = TypeMappingIndex.LoadFromYaml(yaml);

        Assert.True(index.TryGetBySimpleName("Packet", out var cls, out var ambiguous));
        Assert.False(ambiguous);
        Assert.Equal("com.example.Packet", cls!.JavaClass);
    }

    [Fact]
    public void MatchQualifiedOnly_DoesNotHijackImplementsClause()
    {
        const string java = """
            import com.jcraft.jogg.Packet;

            public class ClientboundAddEntityPacket implements Packet<ClientGamePacketListener> {
            }
            """;

        var index = TypeMappingIndex.LoadFromYaml(JoggPacketYaml);
        var output = Convert(java, index);

        Assert.Contains("Packet<ClientGamePacketListener>", output);
        Assert.DoesNotContain("byte<", output);
        Assert.DoesNotContain("implements byte", output);
    }

    [Fact]
    public void MatchQualifiedOnly_SurvivesCopyToBuilder()
    {
        var index = TypeMappingIndex.LoadFromYaml(JoggPacketYaml);
        var copied = index.CopyToBuilder().Build();

        Assert.True(copied.TryGetByQualifiedName("com.jcraft.jogg.Packet", out var cls));
        Assert.True(cls!.MatchQualifiedOnly);
        Assert.False(copied.TryGetBySimpleName("Packet", out _, out _));
    }

    // ---------- E: unsupported-member fragment reparse ----------

    [Fact]
    public void UnsupportedFragment_ExplicitTypeWitness_RewrittenBeforeReparse()
    {
        const string java = """
            import com.example.Thing;

            public class X {
                public void run() {
                    Thing.broken(List.<Component>of(References.ITEM_STACK.in(schema)));
                }
            }
            """;

        var index = TypeMappingIndex.LoadFromYaml(UnsupportedMemberYaml);
        var output = Convert(java, index);

        Assert.Contains("Thing.broken", output);
        Assert.Contains(".of<Component>(", output);
        Assert.DoesNotContain(". < Component >", output);
    }

    [Fact]
    public void UnsupportedFragment_KeywordMember_IsEscaped()
    {
        const string java = """
            import com.example.Thing;

            public class X {
                public void run() {
                    Thing.broken(References.ITEM_STACK.in(schema));
                }
            }
            """;

        var index = TypeMappingIndex.LoadFromYaml(UnsupportedMemberYaml);
        var output = Convert(java, index);

        Assert.Contains("ITEM_STACK.@in(", output);
        Assert.DoesNotContain("ITEM_STACK.in(", output);
    }

    [Fact]
    public void UnsupportedFragment_NestedGenericWitness_Rewritten()
    {
        const string java = """
            import com.example.Thing;

            public class X {
                public void run() {
                    Thing.broken(Objects.<List<String>>requireNonNullElse(value, fallback));
                }
            }
            """;

        var index = TypeMappingIndex.LoadFromYaml(UnsupportedMemberYaml);
        var output = Convert(java, index);

        Assert.Contains("requireNonNullElse<List<String>>(", output);
        Assert.DoesNotContain(". < List", output);
    }

    // ---------- I: keyword member names ----------

    [Fact]
    public void KeywordMember_ContextualIn_IsEscaped()
    {
        var index = TypeMappingIndex.LoadFromYaml("namespaces: []");
        var output = ConvertExpression("a.in(b)", index, preserve: true);

        Assert.Contains("a.@in(b)", output);
    }

    [Fact]
    public void KeywordMember_ReservedEvent_IsEscaped()
    {
        var index = TypeMappingIndex.LoadFromYaml("namespaces: []");
        var output = ConvertExpression("a.event(b)", index, preserve: true);

        Assert.Contains("a.@event(b)", output);
    }

    [Fact]
    public void KeywordMember_GenericName_IsEscaped()
    {
        var index = TypeMappingIndex.LoadFromYaml("namespaces: []");
        var output = ConvertExpression("X.<T>in(a)", index, preserve: true);

        Assert.Contains("X.@in<T>(", output);
    }

    [Fact]
    public void KeywordMember_ValidContextual_NotEscaped()
    {
        var index = TypeMappingIndex.LoadFromYaml("namespaces: []");
        var output = ConvertExpression("a.value(b)", index, preserve: true);

        Assert.Contains("a.value(b)", output);
        Assert.DoesNotContain("a.@value(b)", output);
    }
}