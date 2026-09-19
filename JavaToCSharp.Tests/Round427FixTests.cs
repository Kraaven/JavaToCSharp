using JavaToCSharp;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the 4.0.28 fixes, all reproducing parse errors observed in real converted
/// output:
/// - record component types (backing fields, accessors, canonical constructor, Deconstruct)
///   convert through the structural path, so nested mapped generics inside record components
///   no longer emit separate argument groups
///   (<c>ChunkSectionsToRender.drawGroupsPerLayer</c>);
/// - Java type-use annotations on array types are stripped even without a trailing space
///   (<c>String @Nullable[]</code> renders without one), including qualified annotation names
///   (<c>@org.jspecify.annotations.Nullable</c>) - <c>RealmsText</c>,
///   <c>ServerboundCookieResponsePacket</c>, <c>SimpleBitStorage</c>, <c>ProtoChunk</c>;
/// - Java intersection casts (<c>(A &amp; B) expr</c>) cut at the first ampersand regardless of
///   surrounding whitespace (<c>TimeCommand.baseCommand</c>).
/// </summary>
public class Round427FixTests
{
    private const string MappingYaml = """
        namespaces:
          - java_packages: [it.unimi.dsi.fastutil.objects, it.unimi.dsi.fastutil.ints]
            csharp_packages: [System.Collections.Generic]
          - java_packages: [java.util]
            csharp_packages: [System.Collections.Generic]
        class_map:
          - java_class: it.unimi.dsi.fastutil.objects.Object2ObjectMap
            csharp_class: 'Dictionary<$1, $2>'
            kind: interface
            generic: true
          - java_class: it.unimi.dsi.fastutil.ints.Int2ObjectOpenHashMap
            csharp_class: 'Dictionary<int, $1>'
            kind: class
            generic: true
          - java_class: java.util.List
            csharp_class: IList
            kind: interface
            generic: true
        """;

    private static string Convert(string java, Action<JavaConversionOptions>? configure = null)
    {
        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeComments = false,
            MappingIndex = JavaToCSharp.TypeMapping.TypeMappingIndex.LoadFromSources([("test", MappingYaml)]),
        };
        configure?.Invoke(options);
        return JavaToCSharpConverter.ConvertText(java, options).ConvertedText ?? "";
    }

    private static void AssertParseable(string csharp)
    {
        var errors = CSharpSyntaxTree.ParseText(csharp).GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0, "Converted output has parse errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void RecordComponent_NestedMappedGenerics_ConvertStructurally()
    {
        const string java = """
            import it.unimi.dsi.fastutil.objects.Object2ObjectMap;
            import it.unimi.dsi.fastutil.ints.Int2ObjectOpenHashMap;
            import java.util.List;

            public record ChunkSectionsToRender(Object2ObjectMap<Layer, Int2ObjectOpenHashMap<List<Draw>>> drawGroupsPerLayer) {
                public record Layer() { }
                public record Draw() { }
            }
            """;

        var result = Convert(java);

        Assert.Contains("Dictionary<Layer, Dictionary<int, IList<Draw>>>", result);
        Assert.DoesNotContain("< IList", result);
        AssertParseable(result);
    }

    [Fact]
    public void RecordComponent_TypeUseAnnotationOnArray_IsStripped()
    {
        const string java = """
            import org.jspecify.annotations.Nullable;

            public record RealmsText(String translationKey, String @Nullable [] args) {
            }
            """;

        var result = Convert(java);

        Assert.Contains("string[] args", result);
        Assert.DoesNotContain("@", result);
        AssertParseable(result);
    }

    [Fact]
    public void ConstructorParameter_TypeUseAnnotationOnArray_IsStripped()
    {
        const string java = """
            import org.jspecify.annotations.Nullable;

            public class ProtoChunk {
                public ProtoChunk(byte @Nullable [] payload) {
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("byte[] payload", result);
        Assert.DoesNotContain("@", result);
        AssertParseable(result);
    }

    [Fact]
    public void IntersectionCast_CutsAtFirstAmpersand_RegardlessOfSpacing()
    {
        const string java = """
            import com.mojang.brigadier.builder.LiteralArgumentBuilder;

            public class TimeCommand {
                public void build() {
                    var baseCommand = (LiteralArgumentBuilder<CommandSourceStack> & ArgumentBuilder) Commands.literal("time");
                }

                public class CommandSourceStack { }
                public class ArgumentBuilder { }
            }
            """;

        var result = Convert(java);

        Assert.DoesNotContain(" & ", result);
        Assert.DoesNotContain("& ArgumentBuilder", result);
        AssertParseable(result);
    }

    [Fact]
    public void QualifiedAnnotationName_IsStripped()
    {
        const string java = """
            public class Holder {
                private @org.jspecify.annotations.Nullable String value;
            }
            """;

        var result = Convert(java);

        Assert.DoesNotContain("jspecify", result);
        AssertParseable(result);
    }
}
