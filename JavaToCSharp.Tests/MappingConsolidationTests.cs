using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

/// <summary>
/// Tests that replace the old SyntaxMapping tests, verifying the consolidated TypeMapping system
/// handles import replacement, method renaming, and annotation mapping.
/// </summary>
public class MappingConsolidationTests
{
    [Fact]
    public void TypeMappingBuilder_CanBuildEmptyIndex()
    {
        var builder = new TypeMappingBuilder();
        var index = builder.Build();
        Assert.NotNull(index);
        Assert.Empty(index.Classes);
    }

    [Fact]
    public void TypeMappingBuilder_CanAddNamespaceMapping()
    {
        var builder = new TypeMappingBuilder();
        builder.AddNamespace("org.junit", "Xunit");
        var index = builder.Build();
        Assert.NotNull(index);
    }

    [Fact]
    public void TypeMappingBuilder_CanAddClassWithRename()
    {
        var builder = new TypeMappingBuilder();
        builder.AddNamespace("org.junit", "Xunit");
        builder.AddClass("org.junit.Assert", "Assert", cls =>
        {
            cls.RenameVariadic("assertEquals", "Equal");
            cls.RenameVariadic("assertTrue", "True");
        });
        var index = builder.Build();
        Assert.Single(index.Classes);
    }

    [Fact]
    public void Conversion_With_TypeMapping_Index_Rewrites_Members()
    {
        const string javaCode = """
            import org.junit.Assert;
            public class Test {
                public void testAsserts() {
                    Assert.assertEquals("a", "a");
                    Assert.assertTrue(true);
                }
            }

            """;

        var builder = new TypeMappingBuilder();
        builder.AddNamespace("org.junit", "Xunit");
        builder.AddClass("org.junit.Assert", "Assert", cls =>
        {
            cls.RenameVariadic("assertEquals", "Equal");
            cls.RenameVariadic("assertTrue", "True");
        });

        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            MappingIndex = builder.Build(),
            PreserveJavaMethodNames = false,
        };

        var result = JavaToCSharpConverter.ConvertText(javaCode, options);
        var csText = result.ConvertedText ?? "";

        Assert.Contains("Assert", csText);
        Assert.Contains("Equal", csText);
        Assert.Contains("True", csText);
    }

    [Fact]
    public void Conversion_With_BuiltIn_Console_Mapping_Rewrites_SystemOut()
    {
        const string javaCode = """
            public class Test {
                public static void main(String[] args) {
                    System.out.println("Hello");
                    System.out.print("World");
                }
            }

            """;

        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            MappingIndex = BuiltInMappings.Load(BuiltInMappingSpec.Console),
            PreserveJavaMethodNames = false,
        };

        var result = JavaToCSharpConverter.ConvertText(javaCode, options);
        var csText = result.ConvertedText ?? "";

        Assert.Contains("Console.WriteLine", csText);
        Assert.Contains("Console.Write", csText);
    }

    [Fact]
    public void Conversion_Without_MappingIndex_Preserves_Java_Names()
    {
        const string javaCode = """
            public class Test {
                public void doSomething() {
                    System.out.println("Hello");
                }
            }

            """;

        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeUsings = false,
            PreserveJavaMethodNames = true,
        };

        var result = JavaToCSharpConverter.ConvertText(javaCode, options);
        var csText = result.ConvertedText ?? "";

        // Without mapping index, method names stay as Java names (println not remapped to WriteLine)
        // "out" is a C# keyword so it gets escaped to "@out"
        Assert.Contains("println", csText);
        Assert.DoesNotContain("WriteLine", csText);
    }

    [Theory]
    [InlineData(BuiltInMappingSpec.Console)]
    [InlineData(BuiltInMappingSpec.System)]
    [InlineData(BuiltInMappingSpec.SystemCollections)]
    [InlineData(BuiltInMappingSpec.SystemIO)]
    [InlineData(BuiltInMappingSpec.SystemThreading)]
    [InlineData(BuiltInMappingSpec.SystemText)]
    public void BuiltIn_Mappings_CanBeLoaded_Individually(BuiltInMappingSpec spec)
    {
        var index = BuiltInMappings.Load(spec);
        Assert.NotNull(index);
        Assert.NotEmpty(index.Classes);
    }

    [Fact]
    public void FileMappingContext_NoMappedPackages_HasAnyMappedPackageIsFalse()
    {
        var builder = new TypeMappingBuilder();
        builder.AddNamespace("org.junit", "Xunit");
        builder.AddClass("org.junit.Assert", "Assert");
        var index = builder.Build();

        // File only imports java.util.List — not org.junit, so no mapped packages active
        var imports = new[] { "java.util.List" };
        var ctx = new FileMappingContext(index, imports);

        Assert.False(ctx.HasAnyMappedPackage);
    }

    [Fact]
    public void FileMappingContext_WithMappedImport_HasAnyMappedPackageIsTrue()
    {
        var builder = new TypeMappingBuilder();
        builder.AddNamespace("org.junit", "Xunit");
        builder.AddClass("org.junit.Assert", "Assert");
        var index = builder.Build();

        var imports = new[] { "org.junit.Assert" };
        var ctx = new FileMappingContext(index, imports);

        Assert.True(ctx.HasAnyMappedPackage);
    }

    [Fact]
    public void FileMappingContext_NullIndex_AllPackagesActive()
    {
        var ctx = new FileMappingContext(null, Array.Empty<string>());

        // HasAnyMappedPackage is false (no index means no gating needed at resolver level)
        Assert.False(ctx.HasAnyMappedPackage);
        // But IsPackageActive always returns true — no filtering
        Assert.True(ctx.IsPackageActive("com.example.Foo"));
    }

    [Fact]
    public void FileMappingContext_AutoImportedPackages_AreActive()
    {
        var builder = new TypeMappingBuilder();
        builder.AddNamespace("java.lang", "System");
        builder.AddClass("java.lang.String", "String");
        var index = builder.Build();

        // No explicit imports, but java.lang is auto-imported
        var ctx = new FileMappingContext(index, Array.Empty<string>(), ["java.lang"]);

        Assert.True(ctx.HasAnyMappedPackage);
        Assert.True(ctx.IsPackageActive("java.lang.String"));
    }

    [Fact]
    public void FileMappingContext_PackagePrefix_BecomesActive()
    {
        var builder = new TypeMappingBuilder();
        builder.AddNamespace("java.util", "System.Collections.Generic");
        builder.AddClass("java.util.List", "IList");
        var index = builder.Build();

        // Importing java.util.ArrayList activates java.util as a package
        var imports = new[] { "java.util.ArrayList" };
        var ctx = new FileMappingContext(index, imports);

        Assert.True(ctx.HasAnyMappedPackage);
        Assert.True(ctx.IsPackageActive("java.util.List"));
    }

    [Fact]
    public void NamespaceGating_MappedTypeInActivePackage_IsResolved()
    {
        const string javaCode = """
            import org.junit.Assert;
            public class Test {
                public void testAsserts() {
                    Assert.assertEquals("a", "a");
                }
            }

            """;

        var builder = new TypeMappingBuilder();
        builder.AddNamespace("org.junit", "Xunit");
        builder.AddClass("org.junit.Assert", "Assert", cls =>
        {
            cls.RenameVariadic("assertEquals", "Equal");
        });

        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            MappingIndex = builder.Build(),
        };

        var result = JavaToCSharpConverter.ConvertText(javaCode, options);
        var csText = result.ConvertedText ?? "";

        Assert.Contains("Equal", csText);
    }

    [Fact]
    public void NamespaceGating_UnrelatedFile_NoMappingApplied()
    {
        const string javaCode = """
            import java.util.List;
            import java.util.ArrayList;
            public class Test {
                public void doStuff() {
                    List<String> items = new ArrayList<>();
                    items.add("hello");
                }
            }

            """;

        // Only load Console mapping (java.lang.System.out → Console)
        // This file imports java.util, not java.lang — so System.out mapping should not fire
        var builder = new TypeMappingBuilder();
        builder.AddNamespace("java.lang", "System");
        builder.AddClass("java.lang.System", "System", cls =>
        {
            cls.RenameVariadic("println", "WriteLine");
        });

        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            MappingIndex = builder.Build(),
            PreserveJavaMethodNames = true,
        };

        var result = JavaToCSharpConverter.ConvertText(javaCode, options);
        var csText = result.ConvertedText ?? "";

        // Should NOT contain Console.WriteLine since java.util has no Console mapping
        Assert.DoesNotContain("Console", csText);
    }

    [Fact]
    public void CopyToBuilder_RoundTrip_PreservesEntries()
    {
        var builder = new TypeMappingBuilder();
        builder.AddNamespace("java.util", "System.Collections.Generic");
        builder.AddClass("java.util.List", "IList");
        builder.AddClass("java.util.HashMap", "Dictionary");
        var original = builder.Build();

        Assert.Equal(2, original.Classes.Count);

        var copied = original.CopyToBuilder();

        // Add a new entry
        copied.AddClass("java.util.ArrayList", "List");
        var modified = copied.Build();

        Assert.Equal(3, modified.Classes.Count);
        Assert.Contains(modified.Classes, c => c.JavaClass == "java.util.List");
        Assert.Contains(modified.Classes, c => c.JavaClass == "java.util.HashMap");
        Assert.Contains(modified.Classes, c => c.JavaClass == "java.util.ArrayList");
    }
}
