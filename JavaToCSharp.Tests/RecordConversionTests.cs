using Xunit.Abstractions;

namespace JavaToCSharp.Tests;

public class RecordConversionTests(ITestOutputHelper output)
{
    [Fact]
    public void ConvertSimpleRecord_CreatesClassWithComponents()
    {
        const string java = """
public record Point(int x, int y) {
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

        Assert.Contains("public sealed class Point", parsed);
        Assert.Contains("private readonly int x_attribute", parsed);
        Assert.Contains("private readonly int y_attribute", parsed);
        Assert.Contains("public Point(int x, int y)", parsed);
        Assert.Contains("this.x_attribute = x", parsed);
        Assert.Contains("this.y_attribute = y", parsed);
        Assert.Contains("public int x()", parsed);
        Assert.Contains("public int y()", parsed);
        Assert.Contains("=> this.x_attribute", parsed);
        Assert.Contains("=> this.y_attribute", parsed);
        Assert.DoesNotContain("public readonly int x", parsed);
        Assert.DoesNotContain("public int X()", parsed);
        Assert.DoesNotContain("public int Y()", parsed);
        Assert.Contains("override bool Equals", parsed);
        Assert.Contains("override int GetHashCode", parsed);
        Assert.Contains("override string ToString", parsed);
    }

    [Fact]
    public void ConvertPersonRecord_MatchesExpectedShape()
    {
        const string java = """
public record Person<T extends Number>(
    String name,
    int age,
    T score
) implements Comparable<Person<T>> {

    public static final String TYPE = "PERSON";

    private static int count = 0;

    public Person {
        if (name == null) {
            throw new IllegalArgumentException("name cannot be null");
        }

        if (age < 0) {
            throw new IllegalArgumentException("age cannot be negative");
        }

        count++;
    }

    public Person(String name) {
        this(name, 0, null);
    }

    public String greeting() {
        return "Hello " + name;
    }

    public static int getCount() {
        return count;
    }

    public <U> U something(U value) {
        return value;
    }

    @Override
    public String name() {
        return name.toUpperCase();
    }

    @Override
    public boolean equals(Object obj) {
        if (!(obj instanceof Person<?> other)) {
            return false;
        }

        return name.equals(other.name)
            && age == other.age;
    }

    @Override
    public int compareTo(Person<T> other) {
        return Integer.compare(age, other.age);
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

        Assert.Contains("public sealed class Person<T>", parsed);
        Assert.Contains("where T : Number", parsed);
        Assert.Contains("IComparable<Person<T>>", parsed);
        Assert.Contains("public static readonly string TYPE", parsed);
        Assert.Contains("private static int count", parsed);

        Assert.Contains("private readonly string name_attribute", parsed);
        Assert.Contains("private readonly int age_attribute", parsed);
        Assert.Contains("private readonly T score_attribute", parsed);

        // Overridden accessor kept; default accessors for the rest
        Assert.Contains("public string name()", parsed);
        Assert.Contains("name_attribute.ToUpper()", parsed);
        Assert.Contains("public int age()", parsed);
        Assert.Contains("public T score()", parsed);
        Assert.DoesNotContain("public int Age()", parsed);
        Assert.DoesNotContain("public T Score()", parsed);
        Assert.DoesNotContain("public string Name()", parsed);

        Assert.Contains("public Person(string name, int age, T score)", parsed);
        Assert.Contains("ArgumentException", parsed);
        Assert.Contains("this.name_attribute = name", parsed);
        Assert.Contains("this.age_attribute = age", parsed);
        Assert.Contains("this.score_attribute = score", parsed);
        Assert.Contains("count++", parsed);
        Assert.Contains(": this(name, 0, default)", parsed);

        // Record methods keep Java casing for cross-file use
        Assert.Contains("public string greeting()", parsed);
        Assert.Contains("public static int getCount()", parsed);
        Assert.Contains("public U something<U>(U value)", parsed);
        Assert.Contains("public int compareTo(Person<T> other)", parsed);

        Assert.Contains("is not Person<T> other", parsed);
        Assert.Contains("override bool Equals", parsed);
        Assert.Contains("override int GetHashCode", parsed);
        Assert.Contains("override string ToString", parsed);
    }

    [Fact]
    public void ConvertLocalRecord_CreatesNestedClassWithAccessors()
    {
        const string java = """
public class Holder {
    public int sum(int a, int b) {
        record Pair(int left, int right) {
        }

        Pair p = new Pair(a, b);
        return p.left() + p.right();
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

        // ParseStatement may insert extra whitespace; strip it for token checks.
        var compact = System.Text.RegularExpressions.Regex.Replace(parsed, @"\s+", "");
        Assert.Contains("sealedclassPair", compact);
        Assert.Contains("privatereadonlyintleft_attribute", compact);
        Assert.Contains("privatereadonlyintright_attribute", compact);
        Assert.Contains("publicintleft()", compact);
        Assert.Contains("publicintright()", compact);
        Assert.Contains("newPair(a,b)", compact);
    }
}
