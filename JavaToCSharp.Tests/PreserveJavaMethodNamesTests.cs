namespace JavaToCSharp.Tests;

public class PreserveJavaMethodNamesTests
{
    [Fact]
    public void ClassMethods_KeepJavaCasing_ByDefault()
    {
        const string java = """
package net.minecraft.core;

public class Vec3i {
    private int x;
    private int y;
    private int z;

    public int getX() {
        return this.x;
    }

    public int getY() {
        return this.y;
    }

    public int getZ() {
        return this.z;
    }

    public Vec3i offset(int x, int y, int z) {
        return new Vec3i(this.getX() + x, this.getY() + y, this.getZ() + z);
    }

    public static Vec3i min(Vec3i a, Vec3i b) {
        return new Vec3i(a.getX(), b.getY(), a.getZ());
    }
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeComments = false,
        }).ConvertedText ?? "";

        Assert.Contains("public virtual int getX()", parsed);
        Assert.Contains("public virtual int getY()", parsed);
        Assert.Contains("public virtual int getZ()", parsed);
        Assert.Contains("public virtual Vec3i offset(int x, int y, int z)", parsed);
        Assert.Contains("public static Vec3i min(Vec3i a, Vec3i b)", parsed);
        Assert.Contains("this.getX() + x", parsed);
        Assert.Contains("a.getX()", parsed);
        Assert.DoesNotContain("public virtual int GetX()", parsed);
        Assert.DoesNotContain("public static Vec3i Min", parsed);
    }

    [Fact]
    public void PreserveJavaMethodNames_False_RestoresPascalCase()
    {
        const string java = """
public class Vec3i {
    private int x;

    public int getX() {
        return this.x;
    }
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeComments = false,
            IncludeUsings = false,
            IncludeNamespace = false,
            PreserveJavaMethodNames = false,
        }).ConvertedText ?? "";

        Assert.Contains("public virtual int GetX()", parsed);
        Assert.DoesNotContain("public virtual int getX()", parsed);
    }

    [Fact]
    public void PropertyTransforms_AreDisabled_ByDefault()
    {
        const string java = """
import java.util.List;

public class Holder {
    public int size(List<String> lst, String str, String[] arr) {
        return lst.size() + str.length() + arr.length;
    }
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeComments = false,
        }).ConvertedText ?? "";

        Assert.Contains("lst.size()", parsed);
        Assert.Contains("str.length()", parsed);
        Assert.Contains("arr.length", parsed);
        Assert.DoesNotContain("lst.Count", parsed);
        Assert.DoesNotContain("str.Length", parsed);
        Assert.DoesNotContain("arr.Length", parsed);
    }

    [Fact]
    public void MethodReference_KeepsJavaCasing_ByDefault()
    {
        const string java = """
public class BlockPos {
    public static BlockPos of(int x, int y, int z) {
        return new BlockPos(x, y, z);
    }
}

public class Consumer {
    public java.util.function.Function<Integer, BlockPos> factory() {
        return BlockPos::of;
    }
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeComments = false,
        }).ConvertedText ?? "";

        // Unbound method references now convert to a receiver-parameter lambda; the assertion
        // checks the Java casing of the referenced method is preserved.
        Assert.Contains(".of()", parsed);
        Assert.DoesNotContain(".Of()", parsed);
    }

    [Fact]
    public void ConstructorMethodReference_CreatesConstructorLambda()
    {
        const string java = """
public class Foo {
    public Foo(int value) {
    }
}

public class Bar {
    public static java.util.function.Function<Integer, Foo> factory() {
        return Foo::new;
    }
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeComments = false,
        }).ConvertedText ?? "";

        Assert.Contains("() => new Foo()", parsed);
        Assert.DoesNotContain(".new()", parsed);
    }

    [Fact]
    public void ArrayConstructorMethodReference_CreatesEmptyArrayLambda()
    {
        const string java = """
public class Foo {
    public static java.util.function.IntFunction<Foo[]> factory() {
        return Foo[]::new;
    }
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeComments = false,
        }).ConvertedText ?? "";

        Assert.Contains("() => new Foo[0]", parsed);
        Assert.DoesNotContain(".new()", parsed);
    }

    [Fact]
    public void GenericArrayConstructorMethodReference_CreatesEmptyArrayLambda()
    {
        const string java = """
public class Foo<T> {
    public static java.util.function.IntFunction<T[]> factory() {
        return T[]::new;
    }
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeComments = false,
        }).ConvertedText ?? "";

        Assert.Contains("() => new T[0]", parsed);
        Assert.DoesNotContain(".new()", parsed);
    }

    [Fact]
    public void ObjectMethods_StillMapToCSharpEquivalents()
    {
        const string java = """
public class Vec3i {
    @Override
    public boolean equals(Object o) {
        return true;
    }

    @Override
    public int hashCode() {
        return 0;
    }

    @Override
    public String toString() {
        return getClass().getName();
    }
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeComments = false,
        }).ConvertedText ?? "";

        Assert.Contains("public override bool Equals(object o)", parsed);
        Assert.Contains("public override int GetHashCode()", parsed);
        Assert.Contains("public override string ToString()", parsed);
        Assert.Contains("GetType().getName()", parsed);
    }
}
