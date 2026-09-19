using JavaToCSharp;
using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the nested-import misfire in <see cref="UsingsHelper.GetUsings"/>: the
/// nested-type branch (import a.b.C.Nested) must only fire when the segment before the
/// leaf starts a class name by Java's casing convention (java.util.Map.Entry → outer
/// "Map"). A flat package import (java.util.List → outer segment "util", lowercase) must
/// fall through to the package-row mapping path and emit the mapped namespace usings —
/// never a bogus alias to a tree type that merely shares the package segment's name
/// (tree type Util → Net.Minecraft.Utils).
/// </summary>
public class UsingsHelperNestedImportTests
{
    private const string FlatImportJava = """
        package com.mojang.authlib;

        import java.util.List;

        public class EnvironmentParser {
            private List<String> properties;

            public List<String> getProperties() {
                return properties;
            }
        }
        """;

    private const string NestedJdkImportJava = """
        package com.example;

        import java.util.Map.Entry;

        public class Registry {
            private Entry<String, Integer> binding;

            public Entry<String, Integer> getBinding() {
                return binding;
            }
        }
        """;

    private const string TreeTypedOuterImportJava = """
        package com.example;

        import com.mojang.datafixers.DSL.TypeReference;

        public class Holder {
            private TypeReference reference;

            public TypeReference getReference() {
                return reference;
            }
        }
        """;

    private const string TwoSegmentImportJava = """
        package com.example;

        import foo.Bar;

        public class Widget {
            private Bar bar;

            public Bar getBar() {
                return bar;
            }
        }
        """;

    private const string MappingYaml = """
        namespaces:
          - java_packages: [java.util]
            csharp_packages: [System.Collections.Generic, System.Collections]
          - java_packages: [foo]
            csharp_packages: [Foo.Library]

        class_map:
          - java_class: java.util.List
            csharp_class: IList
            kind: interface
            generic: true
            entries:
              - java_member: size
                arg_count: 0
                kind: property
                csharp_member: Count

          - java_class: java.util.Map
            csharp_class: IDictionary
            kind: interface
            generic: true
            entries:
              - java_member: size
                arg_count: 0
                kind: property
                csharp_member: Count

          - java_class: java.util.Map.Entry
            csharp_class: KeyValuePair
            kind: interface
            generic: true
            entries:
              - java_member: getKey
                arg_count: 0
                kind: property
                csharp_member: Key
        """;

    private static JavaConversionOptions Options() => new()
    {
        IncludeNamespace = false,
        IncludeComments = false,
        MappingIndex = TypeMappingIndex.LoadFromYaml(MappingYaml),
        KnownTypeNamespaces = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // A tree type "Util" declared in net.minecraft.util shares its simple name with
            // the java.util package segment — the misfire trigger from the driver project.
            ["util"] = "Net.Minecraft.Utils",
            // A tree-typed outer "DSL" (com.mojang.datafixers.DSL) for the genuinely-nested case.
            ["dsl"] = "Net.Minecraft.Datafixers.Dsl",
        },
    };

    private static string Convert(string java, JavaConversionOptions? options = null)
        => JavaToCSharpConverter.ConvertText(java, options ?? Options()).ConvertedText ?? "";

    [Fact]
    public void FlatPackageImport_EmitsNoAlias_EmitsPackageRowUsings()
    {
        var csharp = Convert(FlatImportJava);

        Assert.Contains("class EnvironmentParser", csharp);
        Assert.DoesNotContain("using List =", csharp);
        Assert.DoesNotContain("Net.Minecraft.Utils.List", csharp);
        Assert.Contains("using System.Collections.Generic;", csharp);
        Assert.Contains("using System.Collections;", csharp);
    }

    [Fact]
    public void JdkNestedTypeImport_StillAliasesToMappedTarget()
    {
        var csharp = Convert(NestedJdkImportJava);

        Assert.Contains("using Entry = KeyValuePair;", csharp);
        Assert.DoesNotContain("using Entry = Net.Minecraft", csharp);
    }

    [Fact]
    public void TreeTypedOuterNestedImport_StillAliasesToKnownNamespace()
    {
        var csharp = Convert(TreeTypedOuterImportJava);

        Assert.Contains("using TypeReference = Net.Minecraft.Datafixers.Dsl.TypeReference;", csharp);
    }

    [Fact]
    public void TwoSegmentImport_EmitsMappedPackageUsing_NoAlias()
    {
        var csharp = Convert(TwoSegmentImportJava);

        Assert.Contains("class Widget", csharp);
        Assert.Contains("using Foo.Library;", csharp);
        Assert.DoesNotContain("using Bar =", csharp);
    }

    [Fact]
    public void TwoSegmentImport_UnmappedPackage_FallsBackToCapitalizedUsing_NoCrash()
    {
        var java = TwoSegmentImportJava.Replace("import foo.Bar;", "import unmapped.Widget;");
        var csharp = Convert(java);

        Assert.Contains("using Unmapped;", csharp);
        Assert.DoesNotContain("using Widget =", csharp);
    }
}
