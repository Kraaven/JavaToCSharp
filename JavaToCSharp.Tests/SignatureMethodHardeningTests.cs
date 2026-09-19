using JavaToCSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the signature-synthesis hardening (parse-regression fixes): captured
/// signature parts can be statement text (method-body invocations with eaten string
/// literals) or carry Java parameter prefixes; the converter must reject garbage and
/// normalize the prefixes instead of emitting unparseable members.
/// </summary>
public class SignatureMethodHardeningTests
{
    private static ConversionContext Context() => new(new JavaConversionOptions
    {
        IncludeNamespace = false,
        IncludeComments = false,
    });

    [Fact]
    public void FinalParameterModifier_IsStripped()
    {
        Assert.True(TypeHelper.TryBuildSignatureMethod(
            Context(), "|void|visitRequiredDependencies|final Action<K> output", out var method));
        Assert.Equal("void visitRequiredDependencies(Action<K> output)",
            method.NormalizeWhitespace().ToFullString().Replace("\r", "").Replace("\n", "").Replace("  ", " ").Trim());
    }

    [Fact]
    public void StatementText_ReturnType_IsRejected()
    {
        // A method-body invocation with eaten string literals captured as a signature.
        Assert.False(TypeHelper.TryBuildSignatureMethod(
            Context(), "|throw new registry.IllegalStateException|IllegalStateException| + @this . key ( ) + +key", out _));
    }

    [Fact]
    public void GarbageParameterText_IsRejected()
    {
        Assert.False(TypeHelper.TryBuildSignatureMethod(
            Context(), "|void|foo| + @this . key ( ) + +key", out _));
    }

    [Fact]
    public void GarbageParameterName_RejectsWholeMember()
    {
        // Non-empty parameter text that yields no valid parameter: reject the member entirely
        // (an accidental wrong-arity member could collide with a real overload).
        Assert.False(TypeHelper.TryBuildSignatureMethod(
            Context(), "|void|foo|+key", out _));
    }

    [Fact]
    public void VarArgs_BecomeParamsArray()
    {
        Assert.True(TypeHelper.TryBuildSignatureMethod(
            Context(), "|void|write|byte... data", out var method));
        var parameter = Assert.Single(method.ParameterList.Parameters);
        Assert.True(parameter.Modifiers.Any(SyntaxKind.ParamsKeyword));
        Assert.Equal("byte[]", parameter.Type!.ToString());
    }

    [Fact]
    public void AnnotationParameterPrefix_IsStripped()
    {
        Assert.True(TypeHelper.TryBuildSignatureMethod(
            Context(), "|int|hash|@Nullable String input", out var method));
        var parameter = Assert.Single(method.ParameterList.Parameters);
        Assert.Equal("string", parameter.Type!.ToString());
        Assert.Equal("input", parameter.Identifier.Text);
    }

    [Fact]
    public void TrailingDot_ReturnType_IsRejected()
    {
        // `builder.add(x);` captured with "builder." as the return type.
        Assert.False(TypeHelper.TryBuildSignatureMethod(
            Context(), "|builder.|add|Foo x", out _));
    }
}
