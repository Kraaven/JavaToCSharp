using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

/// <summary>
/// Template-filling correctness:
/// - positional arguments stay aligned with the Java argument list even when an earlier
///   argument fails conversion (the failed slot substitutes a placeholder, it does not shift
///   the positions of the remaining arguments);
/// - method references map object-method equivalents exactly like method calls do.
/// </summary>
public class TemplateFillTests
{
    private static JavaConversionOptions Options(TypeMappingIndex? index) => new()
    {
        IncludeNamespace = false,
        IncludeComments = false,
        PreserveJavaMethodNames = true,
        MappingIndex = index,
    };

    private static string Convert(string java, TypeMappingIndex? index = null)
        => JavaToCSharpConverter.ConvertText(java, Options(index)).ConvertedText ?? "";

    [Fact]
    public void Template_FailedArgument_DoesNotShiftPositions()
    {
        // The text block first argument has no expression visitor, so its slot is empty;
        // "$2" must still refer to the second argument (5), not to the first convertible one.
        const string java = """"
            import example.Foo;

            public class Bar {
                int go(Foo f) {
                    return f.combine("""
                    text
                    """, 5);
                }
            }
            """";

        var index = TypeMappingIndex.LoadFromYaml("""
            namespaces:
              - java_packages: [example]
                csharp_packages: [ExampleNs]
            class_map:
              - java_class: example.Foo
                csharp_class: Foo
                entries:
                  - java_member: combine
                    arg_count: 2
                    kind: template
                    expression: "$1 + $2"
            """);

        var output = Convert(java, index);

        Assert.Contains("object () + 5", output);
        Assert.DoesNotContain("5 + object", output);
    }

    [Fact]
    public void MethodReference_ObjectMethod_MapsLikeMethodCall()
    {
        const string java = """
            import java.util.function.Supplier;

            public class Foo {
                Supplier<Integer> go() {
                    return this::hashCode;
                }
            }
            """;

        var output = Convert(java);

        Assert.Contains("GetHashCode", output);
        Assert.DoesNotContain("hashCode", output);
    }
}
