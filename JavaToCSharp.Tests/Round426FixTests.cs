using JavaToCSharp;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the 4.0.27 fixes:
/// - type strings arriving from JavaParser's <c>toString()</c> carry whitespace around every
///   token (<c>Object2IntMap &lt; GossipType &gt;</c>); they are now compacted before the
///   mapping and template machinery runs (a wildcard bound keeps its separating space:
///   <c>? extends Foo</c>);
/// - <see cref="JavaToCSharp.TypeMapping.TypeMappingResolver"/> consumes the Java argument
///   group after a substituted <c>$N</c> template even when whitespace precedes the group,
///   so <c>Object2IntMap&lt;T&gt;</c> no longer collapses to
///   <c>Dictionary&lt;object, int&gt; &lt; T &gt;</c>;
/// - nested mapped generics inside other mapped generics
///   (<c>ImmutableMap&lt;String, Pair&lt;String, ImmutableMap&lt;String, String&gt;&gt;&gt;</c>)
///   never emit literal <c>$N</c> placeholders or stray argument groups.
/// These reproduce parse errors observed in real converted output (GossipContainer,
/// ChunkSectionsToRender, ChunkStructuresTemplateRenameFix).
/// </summary>
public class Round426FixTests
{
    private const string MappingYaml = """
        namespaces:
          - java_packages: [java.util]
            csharp_packages: [System.Collections.Generic]
          - java_packages: [it.unimi.dsi.fastutil.objects, it.unimi.dsi.fastutil.ints]
            csharp_packages: [System.Collections.Generic]
          - java_packages: [com.google.common.collect]
            csharp_packages: [System.Collections.Immutable]
        class_map:
          - java_class: java.util.Map
            csharp_class: IDictionary
            kind: interface
            generic: true
          - java_class: java.util.UUID
            csharp_class: Guid
            kind: class
          - java_class: it.unimi.dsi.fastutil.objects.Object2IntMap
            csharp_class: 'Dictionary<$1, int>'
            kind: class
            generic: true
          - java_class: it.unimi.dsi.fastutil.objects.Object2ObjectMap
            csharp_class: 'Dictionary<$1, $2>'
            kind: class
            generic: true
          - java_class: it.unimi.dsi.fastutil.ints.Int2ObjectOpenHashMap
            csharp_class: 'Dictionary<int, $1>'
            kind: class
            generic: true
          - java_class: com.google.common.collect.ImmutableMap
            csharp_class: ImmutableDictionary
            kind: class
            generic: true
          - java_class: com.google.common.collect.ImmutableMap.Builder
            csharp_class: 'ImmutableDictionary<$1, $2>.Builder'
            kind: class
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
    public void CompactTypeString_StripsTokenWhitespace_PreservesWildcardBounds()
    {
        Assert.Equal("Object2IntMap<GossipType>", TypeHelper.CompactTypeString("Object2IntMap < GossipType >"));
        Assert.Equal("FrameRenderPass.Draw<GpuBufferSlice[]>", TypeHelper.CompactTypeString("FrameRenderPass . Draw < GpuBufferSlice [ ] >"));
        Assert.Equal("Map<? extends Foo,?>", TypeHelper.CompactTypeString("Map < ? extends Foo , ? >"));
    }

    [Fact]
    public void SpacedNestedFastutilType_FillsTemplateArguments()
    {
        const string java = """
            import java.util.Map;
            import java.util.UUID;
            import it.unimi.dsi.fastutil.objects.Object2IntMap;

            public class GossipContainer {
                private Map<UUID, Object2IntMap<GossipType>> gossip;

                public Map<UUID, Object2IntMap<GossipType>> getGossipEntries() { return this.gossip; }
            }
            """;

        var result = Convert(java);

        // The mapped arguments must be consumed by the template, not re-emitted as a
        // separate bracket group (which would be a parse error).
        Assert.Contains("IDictionary<Guid, Dictionary<GossipType, int>>", result);
        Assert.DoesNotContain("< GossipType >", result);
        AssertParseable(result);
    }

    [Fact]
    public void NestedFastutilMaps_NeverEmitSeparateArgumentGroups()
    {
        const string java = """
            import it.unimi.dsi.fastutil.objects.Object2ObjectMap;
            import it.unimi.dsi.fastutil.ints.Int2ObjectOpenHashMap;
            import java.util.List;

            public class ChunkSectionsToRender {
                private Object2ObjectMap<Layer, Int2ObjectOpenHashMap<List<Draw>>> drawGroupsPerLayer;

                public class Layer { }
                public class Draw { }
            }
            """;

        var result = Convert(java);

        Assert.Contains("Dictionary<Layer, Dictionary<int, IList<Draw>>>", result);
        Assert.DoesNotContain("< IList", result);
        AssertParseable(result);
    }

    [Fact]
    public void NestedImmutableMaps_NeverEmitLiteralPlaceholders()
    {
        const string java = """
            import com.google.common.collect.ImmutableMap;
            import com.mojang.datafixers.util.Pair;

            public class ChunkStructuresTemplateRenameFix {
                private static final ImmutableMap<String, Pair<String, ImmutableMap<String, String>>> RENAMES = ImmutableMap.builder().build();
            }
            """;

        var result = Convert(java);

        Assert.DoesNotContain("$", result);
        AssertParseable(result);
    }
}
