using Xunit.Abstractions;

namespace JavaToCSharp.Tests;

public class SwitchConversionTests(ITestOutputHelper output)
{
    private static JavaConversionOptions Options() => new()
    {
        IncludeNamespace = false,
        IncludeUsings = false,
        IncludeComments = false,
    };

    private string Convert(string java)
    {
        var parsed = JavaToCSharpConverter.ConvertText(java, Options());
        Assert.NotNull(parsed);
        output.WriteLine(parsed);
        return parsed!;
    }

    [Fact]
    public void BasicSwitch_BecomesIfElseChain()
    {
        const string java = """
public class T {
    void m(int x) {
        switch (x) {
            case 1:
                foo();
                break;
            case 2:
                bar();
                break;
            default:
                baz();
                break;
        }
    }
    void foo() {}
    void bar() {}
    void baz() {}
}
""";

        var parsed = Convert(java);

        Assert.Contains("Java Switch has been converted into if-else chain", parsed);
        Assert.Contains("if (x == 1)", parsed);
        Assert.Contains("else if (x == 2)", parsed);
        Assert.DoesNotContain("switch (", parsed);
        Assert.DoesNotContain("case ", parsed);
    }

    [Fact]
    public void MultiCaseLabels_BecomeOrConditions()
    {
        const string java = """
public class T {
    void m(int x) {
        switch (x) {
            case 1:
            case 2:
            case 3:
                foo();
                break;
        }
    }
    void foo() {}
}
""";

        var parsed = Convert(java);

        Assert.Contains("if (x == 1 || x == 2 || x == 3)", parsed);
        Assert.Contains("Foo()", parsed);
    }

    [Fact]
    public void CommaCaseLabels_BecomeOrConditions()
    {
        const string java = """
public class T {
    void m(int x) {
        switch (x) {
            case 1, 2, 3:
                foo();
                break;
        }
    }
    void foo() {}
}
""";

        var parsed = Convert(java);
        Assert.Contains("if (x == 1 || x == 2 || x == 3)", parsed);
    }

    [Fact]
    public void FallThrough_PreservesMergedBodies()
    {
        const string java = """
public class T {
    void m(int x) {
        switch (x) {
            case 1:
                foo();
            case 2:
                bar();
                break;
        }
    }
    void foo() {}
    void bar() {}
}
""";

        var parsed = Convert(java);

        Assert.Contains("if (x == 1)", parsed);
        Assert.Contains("else if (x == 2)", parsed);
        // case 1 body should include both foo and bar
        var case1Start = parsed.IndexOf("if (x == 1)", StringComparison.Ordinal);
        var case2Start = parsed.IndexOf("else if (x == 2)", StringComparison.Ordinal);
        Assert.True(case1Start >= 0 && case2Start > case1Start);
        var case1Body = parsed[case1Start..case2Start];
        Assert.Contains("Foo()", case1Body);
        Assert.Contains("Bar()", case1Body);
    }

    [Fact]
    public void ArrowSwitch_BecomesIfElse()
    {
        const string java = """
public class T {
    void m(int x) {
        switch (x) {
            case 1 -> foo();
            case 2 -> bar();
            default -> baz();
        }
    }
    void foo() {}
    void bar() {}
    void baz() {}
}
""";

        var parsed = Convert(java);
        Assert.Contains("if (x == 1)", parsed);
        Assert.Contains("else if (x == 2)", parsed);
        Assert.DoesNotContain("switch (", parsed);
    }

    [Fact]
    public void SwitchExpression_AssignmentUsesTempOrDirectAssign()
    {
        const string java = """
public class T {
    int m(int x) {
        int result = switch (x) {
            case 1 -> 10;
            case 2 -> 20;
            default -> 0;
        };
        return result;
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains("int result;", parsed);
        Assert.Contains("if (x == 1)", parsed);
        Assert.Contains("result = 10", parsed);
        Assert.Contains("result = 20", parsed);
        Assert.Contains("result = 0", parsed);
        Assert.DoesNotContain("switch (", parsed);
        Assert.DoesNotContain("=>", parsed);
    }

    [Fact]
    public void ReturnSwitchExpression_ReturnsFromBranches()
    {
        const string java = """
public class T {
    int m(int x) {
        return switch (x) {
            case 1 -> 10;
            default -> 0;
        };
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains("if (x == 1)", parsed);
        Assert.Contains("return 10", parsed);
        Assert.Contains("return 0", parsed);
        Assert.DoesNotContain("switch (", parsed);
    }

    [Fact]
    public void YieldInSwitchExpression_BecomesAssignment()
    {
        const string java = """
public class T {
    int m(int x) {
        int result = switch (x) {
            case 1 -> {
                int value = 2;
                yield value * 2;
            }
            default -> {
                yield 0;
            }
        };
        return result;
    }
}
""";

        var parsed = Convert(java);
        Assert.Contains("int result;", parsed);
        Assert.Contains("int value = 2", parsed);
        Assert.Contains("result = value * 2", parsed);
        Assert.Contains("result = 0", parsed);
    }

    [Fact]
    public void NullCase_BecomesNullCheck()
    {
        const string java = """
public class T {
    void m(String x) {
        switch (x) {
            case null:
                foo();
                break;
            default:
                bar();
                break;
        }
    }
    void foo() {}
    void bar() {}
}
""";

        var parsed = Convert(java);
        Assert.Contains("if (x == null)", parsed);
        Assert.Contains("Foo()", parsed);
        Assert.Contains("Bar()", parsed);
    }

    [Fact]
    public void EnumSwitch_UsesQualifiedConstants()
    {
        const string java = """
public enum Operation { ADD, MULTIPLY }
public class T {
    void m(Operation operation) {
        switch (operation) {
            case ADD:
                foo();
                break;
            case MULTIPLY:
                bar();
                break;
            default:
                baz();
                break;
        }
    }
    void foo() {}
    void bar() {}
    void baz() {}
}
""";

        var parsed = Convert(java);
        Assert.Contains("if (operation == Operation.ADD)", parsed);
        Assert.Contains("else if (operation == Operation.MULTIPLY)", parsed);
        Assert.Contains("operator ==", parsed);
        Assert.Contains("ReferenceEquals", parsed);
    }
}
