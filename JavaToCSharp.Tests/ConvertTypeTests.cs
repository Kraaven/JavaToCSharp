using com.github.javaparser.ast.type;

namespace JavaToCSharp.Tests;

public class ConvertTypeTests
{
    [Fact]
    public void ConvertType_Int()
    {
        Assert.Equal("int", TypeHelper.ConvertType("int"));
    }

    [Fact]
    public void ConvertType_String()
    {
        Assert.Equal("string", TypeHelper.ConvertType("String"));
    }

    [Fact]
    public void ConvertType_Object()
    {
        Assert.Equal("object", TypeHelper.ConvertType("Object"));
    }

    [Fact]
    public void ConvertType_IntArray_BracketsAfterType()
    {
        Assert.Equal("int[]", TypeHelper.ConvertType("int[]"));
    }

    [Fact]
    public void ConvertType_GenericSingleParameter()
    {
        Assert.Equal("MyType<string>", TypeHelper.ConvertType("MyType<String>"));
    }

    [Fact]
    public void ConvertType_GenericMultipleParameters()
    {
        Assert.Equal("MyType<string, object>", TypeHelper.ConvertType("MyType<String, Object>"));
    }

    [Fact]
    public void ConvertType_WithGenericWildcard_ShouldReplaceToken()
    {
        Assert.Equal("MyType<object>", TypeHelper.ConvertType("MyType<?>"));
    }

    [Fact]
    public void ConvertType_WithDottedNestedWildcard_ShouldErase()
    {
        Assert.Equal(
            "FrameGraphBuilder.InternalVirtualResource<object>",
            TypeHelper.ConvertType("FrameGraphBuilder.InternalVirtualResource<?>"));
    }

    [Fact]
    public void ConvertType_WithNestedBoundedWildcards_ShouldEraseInnermost()
    {
        Assert.Equal(
            "Optional<PointFree<Func<object, object>>>",
            TypeHelper.ConvertType("Optional<? extends PointFree<? extends Func<?, ?>>>"));
    }

    [Fact]
    public void ConvertType_WithDottedBoundedWildcard_ShouldEraseBound()
    {
        Assert.Equal(
            "Optic<Proof2, S, T, A, B>",
            TypeHelper.ConvertType("Optic<? super Proof2, S, T, A, B>"));
    }

    [Fact]
    public void ConvertTypeSyntax_GivenNonArrayType_ShouldReturnCorrectSyntax()
    {
        var type = new PrimitiveType(PrimitiveType.Primitive.INT);

        var syntax = TypeHelper.ConvertTypeSyntax(type, 0);

        Assert.Equal("int", syntax.ToString());
    }

    [Fact]
    public void ConvertTypeSyntax_GivenNonArrayTypeWithRank_ShouldReturnCorrectSyntax()
    {
        var type = new PrimitiveType(PrimitiveType.Primitive.INT);

        var syntax = TypeHelper.ConvertTypeSyntax(type, 1);

        Assert.Equal("int[]", syntax.ToString());
    }

    [Fact]
    public void ConvertTypeSyntax_GivenNonArrayTypeWithMultidimensionalRank_ShouldReturnCorrectSyntax()
    {
        var type = new PrimitiveType(PrimitiveType.Primitive.INT);

        var syntax = TypeHelper.ConvertTypeSyntax(type, 2);

        Assert.Equal("int[,]", syntax.ToString());
    }

    [Fact]
    public void ConvertTypeSyntax_GivenArrayTypeWithoutRank_ShouldReturnCorrectSyntax()
    {
        var type = new ArrayType(new PrimitiveType(PrimitiveType.Primitive.INT));

        var syntax = TypeHelper.ConvertTypeSyntax(type, 0);

        Assert.Equal("int[]", syntax.ToString());
    }

    [Fact]
    public void ConvertTypeSyntax_GivenArrayTypeWithRank_ShouldReturnCorrectSyntax()
    {
        var type = new ArrayType(new PrimitiveType(PrimitiveType.Primitive.INT));

        var syntax = TypeHelper.ConvertTypeSyntax(type, 1);

        Assert.Equal("int[]", syntax.ToString());
    }

    [Fact]
    public void ConvertTypeSyntax_GivenArrayTypeWithMultidimensionalRank_ShouldReturnCorrectSyntax()
    {
        var type = new ArrayType(new ArrayType(new PrimitiveType(PrimitiveType.Primitive.INT)));

        var syntax = TypeHelper.ConvertTypeSyntax(type, 2);

        Assert.Equal("int[,]", syntax.ToString());
    }

    [Fact]
    public void ConvertTypeSyntax_GivenMismatchedRank_ShouldThrowException()
    {
        var type = new ArrayType(new ArrayType(new PrimitiveType(PrimitiveType.Primitive.INT)));

        Assert.Throws<ArgumentException>(() => TypeHelper.ConvertTypeSyntax(type, 1));
    }

    [Theory]
    [InlineData("GenericClass<T>", "GenericClass<T>")]
    [InlineData("GenericClass<T extends BoundType<T>>", "GenericClass<T>\n    where T : BoundType<T>")]
    [InlineData("GenericClass<T extends BoundType>", "GenericClass<T>\n    where T : BoundType")]

    [InlineData("GenericClass<T, U>", "GenericClass<T, U>")]
    [InlineData("GenericClass<T, U extends BoundType<U>>", "GenericClass<T, U>\n    where U : BoundType<U>")]
    [InlineData("GenericClass<T extends BoundType1<T>, U extends BoundType2<U>>", "GenericClass<T, U>\n    where T : BoundType1<T> where U : BoundType2<U>")]

    [InlineData("GenericClass<T extends BoundType1<T> & BoundType2<T>>", "GenericClass<T>\n    where T : BoundType1<T>, BoundType2<T>")]
    public void ConvertClassTypeBoundedParameters(string javaClass, string csharpClass)
    {
        string javaCode = $$"""
                                public class {{javaClass}} { }
                                """;
        var options = new JavaConversionOptions
        {
            IncludeUsings = false,
            IncludeNamespace = false,
        };
        var parsed = JavaToCSharpConverter.ConvertText(javaCode, options).ConvertedText ?? "";
        string expected = $$"""
                                public class {{csharpClass}}
                                {
                                }

                                """;

        Assert.Equal(expected.ReplaceLineEndings(), parsed.ReplaceLineEndings());
    }

    [Fact]
    public void ConvertMethodWithBoundedTypeParameter_ShouldEmitGenericMethodWithConstraint()
    {
        const string javaCode = """
                                public class Foo {
                                    public <E extends Entity> int getPackedLightCoords(E entity, float partialTickTime) { return 0; }
                                }
                                """;
        var options = new JavaConversionOptions
        {
            IncludeUsings = false,
            IncludeNamespace = false,
        };
        var parsed = JavaToCSharpConverter.ConvertText(javaCode, options).ConvertedText ?? "";

        Assert.Contains("getPackedLightCoords<E>(E entity, float partialTickTime)", parsed);
        Assert.Contains("where E : Entity", parsed);
        Assert.DoesNotContain("E extends Entity", parsed);
    }

    [Fact]
    public void ConvertMethodWithWildcardBound_ShouldEraseWildcardInConstraint()
    {
        const string javaCode = """
                                public class Foo {
                                    public <S extends StateHolder<?, S>> void foo(S s) { }
                                }
                                """;
        var options = new JavaConversionOptions
        {
            IncludeUsings = false,
            IncludeNamespace = false,
        };
        var parsed = JavaToCSharpConverter.ConvertText(javaCode, options).ConvertedText ?? "";

        Assert.Contains("void foo<S>(S s)", parsed);
        Assert.Contains("where S : StateHolder<object, S>", parsed);
        Assert.DoesNotContain("StateHolder<?, S>", parsed);
    }

    [Fact]
    public void ConvertInterfaceMethodWithBoundedTypeParameter_ShouldEmitGenericMethodWithConstraint()
    {
        const string javaCode = """
                                public interface Foo {
                                    <T extends Bar> void baz(T t);
                                }
                                """;
        var options = new JavaConversionOptions
        {
            IncludeUsings = false,
            IncludeNamespace = false,
        };
        var parsed = JavaToCSharpConverter.ConvertText(javaCode, options).ConvertedText ?? "";

        Assert.Contains("void baz<T>(T t)", parsed);
        Assert.Contains("where T : Bar", parsed);
        Assert.DoesNotContain("T extends Bar", parsed);
    }
}
