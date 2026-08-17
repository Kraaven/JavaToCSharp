using com.github.javaparser;
using com.github.javaparser.ast.expr;
using JavaToCSharp;
using JavaToCSharp.Expressions;

namespace JavaToCSharp.Tests;

public class DoubleLiteralExpressionTests
{
    [Theory]
    [InlineData("-1.0", "-1.0")]
    [InlineData("1.0", "1.0")]
    [InlineData("1.5", "1.5")]
    [InlineData("1e3", "1e3")]
    [InlineData("1.0f", "1.0f")]
    [InlineData("2.5d", "2.5d")]
    [InlineData("1_000.5", "1_000.5")]
    [InlineData("0x1.8p3", "12")]
    [InlineData("1.", "1.")]
    public void VisitDoubleLiteral_PreservesLiteralText(string javaLiteral, string expected)
    {
        var parsed = new JavaParser().parseExpression(javaLiteral).getResult().FromRequiredOptional<Expression>();
        var converted = ExpressionVisitor.VisitExpression(
            new ConversionContext(new JavaConversionOptions()), parsed)?.ToString();

        Assert.Equal(expected, converted);
    }
}
