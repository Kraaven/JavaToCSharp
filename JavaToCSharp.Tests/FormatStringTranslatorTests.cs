using JavaToCSharp.Utilities;

namespace JavaToCSharp.Tests;

public class FormatStringTranslatorTests
{
    [Theory]
    [InlineData("Hello %s", "Hello {0}")]
    [InlineData("Count: %d", "Count: {0:D}")]
    [InlineData("%s %d %s", "{0} {1:D} {2}")]
    [InlineData("%%", "%")]
    [InlineData("%%s", "%s")]
    [InlineData("%-25s", "{0,-25}")]
    [InlineData("%.2f", "{0:F2}")]
    [InlineData("%10d", "{0,10:D}")]
    [InlineData("%-10.2f", "{0,-10:F2}")]
    [InlineData("%n", "\n")]
    [InlineData("hello %n world", "hello \n world")]
    [InlineData("", "")]
    [InlineData("no specifiers here", "no specifiers here")]
    public void ConvertJavaToCSharp_ConvertsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, FormatStringTranslator.ConvertJavaToCSharp(input));
    }

    [Fact]
    public void ConvertJavaToCSharp_MultipleSpecifiers_Sequential()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("The Score is %d, while %s got %d");
        Assert.Equal("The Score is {0:D}, while {1} got {2:D}", result);
    }

    [Fact]
    public void ConvertJavaToCSharp_PercentOnly()
    {
        Assert.Equal("100%", FormatStringTranslator.ConvertJavaToCSharp("100%%"));
    }

    [Fact]
    public void ConvertJavaToCSharp_BooleanFormat()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("flag=%b");
        Assert.Equal("flag={0:S}", result);
    }

    [Fact]
    public void ConvertJavaToCSharp_HexFormat()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("hex=%x");
        Assert.Equal("hex={0:x}", result);
    }

    [Fact]
    public void ConvertJavaToCSharp_ExplicitPosition()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("%2$s %1$d");
        Assert.Equal("{1} {0:D}", result);
    }

    [Fact]
    public void ConvertWithArgCount_ReturnsCorrectCount()
    {
        var (converted, count) = FormatStringTranslator.ConvertWithArgCount("%s %d %s");
        Assert.Equal("{0} {1:D} {2}", converted);
        Assert.Equal(3, count);
    }

    [Fact]
    public void ConvertWithArgCount_NoSpecifiers_ReturnsZero()
    {
        var (converted, count) = FormatStringTranslator.ConvertWithArgCount("hello");
        Assert.Equal("hello", converted);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ConvertWithArgCount_StartOffset()
    {
        var (converted, count) = FormatStringTranslator.ConvertWithArgCount("%s", startArgIndex: 2);
        Assert.Equal("{2}", converted);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ConvertJavaToCSharp_EmptyOrNull()
    {
        Assert.Equal("", FormatStringTranslator.ConvertJavaToCSharp(""));
        Assert.Null(FormatStringTranslator.ConvertJavaToCSharp(null!));
    }

    [Fact]
    public void ConvertJavaToCSharp_LongFormat()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("%d");
        Assert.Equal("{0:D}", result);
    }

    [Fact]
    public void ConvertJavaToCSharp_FloatFormat()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("%f");
        Assert.Equal("{0:F}", result);
    }

    [Fact]
    public void ConvertJavaToCSharp_ScientificFormat()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("%e");
        Assert.Equal("{0:e}", result);
    }

    [Fact]
    public void ConvertJavaToCSharp_OctalFormat()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("%o");
        Assert.Equal("{0:O}", result);
    }

    [Fact]
    public void ConvertJavaToCSharp_PaddedString()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("%20s");
        Assert.Equal("{0,20}", result);
    }

    [Fact]
    public void ConvertJavaToCSharp_LeftAlignedPaddedString()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("%-20s");
        Assert.Equal("{0,-20}", result);
    }

    [Fact]
    public void ConvertJavaToCSharp_ZeroPaddedInt()
    {
        var result = FormatStringTranslator.ConvertJavaToCSharp("%010d");
        Assert.Equal("{0:D10}", result);
    }
}
