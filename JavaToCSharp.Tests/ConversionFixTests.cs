using Xunit.Abstractions;

namespace JavaToCSharp.Tests;

public class ConversionFixTests(ITestOutputHelper output)
{
    private static JavaConversionOptions Options() => new()
    {
        IncludeNamespace = false,
        IncludeUsings = false,
        IncludeComments = false,
        PreserveJavaMethodNames = false,
    };

    private string Convert(string java)
    {
        var result = JavaToCSharpConverter.ConvertText(java, Options());
        Assert.Equal(0, result.FailedCount);
        string parsed = result.ConvertedText ?? "";
        output.WriteLine(parsed);
        return parsed;
    }

    [Fact]
    public void KeywordIdentifiers_AreEscaped()
    {
        // fixed/struct/using are valid Java identifiers but C# keywords
        const string java = """
public class Example {
    public void run(boolean fixed, int struct, String using) {
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains("@fixed", parsed);
        Assert.Contains("@struct", parsed);
        Assert.Contains("@using", parsed);
    }

    [Fact]
    public void EnumConstructor_ThisInvoke_BecomesInitializer()
    {
        const string java = """
public enum Slot {
    MAIN(1),
    OFF(2);

    private final int index;

    Slot(int index) {
        this(index, "x");
    }

    Slot(int index, String label) {
        this.index = index;
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains(": this(name, ordinal, index, \"x\")", parsed);
        Assert.DoesNotContain("this(index, \"x\")", parsed);
    }

    [Fact]
    public void NestedAnnotationInInterface_DoesNotThrow()
    {
        const string java = """
public interface Host {
    @interface Nested {
        String value();
    }

    void run();
}
""";

        var parsed = Convert(java);
        Assert.Contains("interface Host", parsed);
        Assert.Contains("void Run()", parsed);
        Assert.DoesNotContain("interface Nested", parsed);
        Assert.DoesNotContain("class Nested", parsed);
    }

    [Fact]
    public void TypePatternSwitch_ConvertsToIsPattern()
    {
        const string java = """
public class Crash {
    public String describe(Object value) {
        return switch (value) {
            case NullPointerException npe -> npe.getMessage();
            case String s -> s;
            default -> "other";
        };
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains("is NullReferenceException npe", parsed);
        Assert.Contains("is string s", parsed);
    }

    [Fact]
    public void RecordPatternInstanceOf_ConvertsToPositionalPattern()
    {
        const string java = """
public record StringTag(String value) {}

public class Reader {
    public String read(Object tag) {
        if (tag instanceof StringTag(String s)) {
            return s;
        }
        return "";
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains("is StringTag", parsed);
        Assert.Contains("string s", parsed);
        Assert.Contains("void Deconstruct(out string value)", parsed);
    }

    [Fact]
    public void FlexibleConstructor_StatementsBeforeSuper_MovedToBody()
    {
        const string java = """
public class Parent {
    private final int id;
    public Parent(int id) {
        this.id = id;
    }
}

public class Child extends Parent {
    private final String name;

    public Child(String name, int id) {
        this.name = name;
        super(id);
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains(": base(id)", parsed);
        Assert.Contains("this.name = name", parsed);
    }

    [Fact]
    public void PrimitiveTypeWitness_RewrittenAndEmitted()
    {
        const string java = """
import java.util.List;
import java.util.Collections;

public class Holder {
    public List<Boolean> empty() {
        return Collections.<boolean>emptyList();
    }

    public List<Boolean> emptyBoxed() {
        return Collections.<Boolean>emptyList();
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains("EmptyList<bool>", parsed);
    }

    [Fact]
    public void GuardedTypePatternSwitch_AddsAndCondition()
    {
        const string java = """
public class Guarded {
    public int classify(Object value) {
        return switch (value) {
            case String s when s.length() > 0 -> 1;
            default -> 0;
        };
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains("is string s", parsed);
        Assert.Contains("s.Length > 0", parsed);
    }
}
