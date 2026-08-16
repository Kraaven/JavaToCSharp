using Xunit.Abstractions;

namespace JavaToCSharp.Tests;

public class EnumConversionTests(ITestOutputHelper output)
{
    [Fact]
    public void ConvertSimpleEnum_CreatesClassWithConstants()
    {
        const string java = """
public enum Day {
    SUNDAY, MONDAY, TUESDAY
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeUsings = false,
            IncludeComments = false,
        }).ConvertedText;

        Assert.NotNull(parsed);
        output.WriteLine(parsed);

        Assert.Contains("public class Day", parsed);
        Assert.DoesNotContain("public enum Day", parsed);
        Assert.Contains("Java Enum of type : Day converted into sealed class", parsed);
        Assert.Contains("public static bool operator ==(Day? a, Day? b)", parsed);
        Assert.Contains("public static bool operator !=(Day? a, Day? b)", parsed);
        Assert.Contains("ReferenceEquals(a, b)", parsed);
        Assert.Contains("public override bool Equals(object? obj)", parsed);
        Assert.Contains("ReferenceEquals(this, obj)", parsed);
        Assert.Contains("public override int GetHashCode()", parsed);
        Assert.Contains("RuntimeHelpers.GetHashCode(this)", parsed);
        Assert.Contains("public static readonly Day SUNDAY = new SundayDay()", parsed);
        Assert.Contains("public static readonly Day MONDAY = new MondayDay()", parsed);
        Assert.Contains("private readonly string _name", parsed);
        Assert.Contains("private readonly int _ordinal", parsed);
        Assert.Contains("public string name()", parsed);
        Assert.Contains("public int ordinal()", parsed);
        Assert.Contains("public static Day[] values()", parsed);
        Assert.Contains("public static Day valueOf(string name)", parsed);
        Assert.Contains("private sealed class SundayDay : Day", parsed);
        Assert.Contains("base(\"SUNDAY\", 0)", parsed);
    }

    [Fact]
    public void ConvertRichEnum_CreatesAbstractClassWithNestedConstants()
    {
        const string java = """
public enum Operation {
    ADD(1, "Addition") {
        @Override
        public int apply(int a, int b) {
            return a + b;
        }

        @Override
        public String description() {
            return "Adds two numbers";
        }
    },
    MULTIPLY(2, "Multiplication") {
        @Override
        public int apply(int a, int b) {
            return a * b;
        }
    };

    public static final String TYPE = "OPERATION";
    private static int instanceCount = 0;

    private final int id;
    private final String displayName;

    Operation(int id, String displayName) {
        this.id = id;
        this.displayName = displayName;
        instanceCount++;
    }

    public int getId() {
        return id;
    }

    public String getDisplayName() {
        return displayName;
    }

    public abstract int apply(int a, int b);

    public String description() {
        return displayName;
    }

    public static int getInstanceCount() {
        return instanceCount;
    }

    @Override
    public String toString() {
        return displayName;
    }
}
""";

        var parsed = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeUsings = false,
            IncludeComments = false,
        }).ConvertedText;

        Assert.NotNull(parsed);
        output.WriteLine(parsed);

        Assert.Contains("public abstract class Operation", parsed);
        Assert.DoesNotContain("public enum Operation", parsed);
        Assert.Contains("public static readonly Operation ADD = new AddOperation()", parsed);
        Assert.Contains("public static readonly Operation MULTIPLY = new MultiplyOperation()", parsed);
        Assert.Contains("public static readonly string TYPE", parsed);
        Assert.Contains("private static int instanceCount", parsed);
        Assert.Contains("private readonly int id", parsed);
        Assert.Contains("protected Operation(string name, int ordinal, int id, string displayName)", parsed);
        Assert.Contains("_name = name", parsed);
        Assert.Contains("_ordinal = ordinal", parsed);
        Assert.Contains("instanceCount++", parsed);
        Assert.Contains("public virtual int getId()", parsed);
        Assert.Contains("public virtual string getDisplayName()", parsed);
        Assert.Contains("public abstract int apply(int a, int b)", parsed);
        Assert.Contains("public virtual string description()", parsed);
        Assert.Contains("public static int getInstanceCount()", parsed);
        Assert.Contains("override string ToString()", parsed);
        Assert.Contains("private sealed class AddOperation : Operation", parsed);
        Assert.Contains("base(\"ADD\", 0, 1, \"Addition\")", parsed);
        Assert.Contains("public override int apply(int a, int b)", parsed);
        Assert.Contains("public override string description()", parsed);
        Assert.Contains("private sealed class MultiplyOperation : Operation", parsed);
        Assert.Contains("base(\"MULTIPLY\", 1, 2, \"Multiplication\")", parsed);
    }
}
