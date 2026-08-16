namespace JavaToCSharp.Tests;

public class ConvertInterfaceTests
{
    [Fact]
    public void Verify_Method_Param_Types_Use_Interface_Name_Types()
    {
        const string javaCode = """
                                import com.github.javaparser.resolution.Context;
                                import com.github.javaparser.resolution.Test;
                                public interface ResolvedType {
                                    default boolean isArray() {
                                        return false;
                                    }
                                }

                                public class InferenceVariableType implements ResolvedType {
                                    public void registerEquivalentType(ResolvedType type) {
                                    }
                                }

                                public interface ResolvedValueDeclaration extends ResolvedDeclaration {
                                    ResolvedType getType();
                                }

                                """;
        var parsed = JavaToCSharpConverter.ConvertText(javaCode).ConvertedText ?? "";

        const string expectedCSharpCode = """
                                          using Com.Github.Javaparser.Resolution;
                                          
                                          namespace MyApp
                                          {
                                              public interface ResolvedType
                                              {
                                                  bool IsArray()
                                                  {
                                                      return false;
                                                  }
                                              }
                                          
                                              public class InferenceVariableType : ResolvedType
                                              {
                                                  public virtual void RegisterEquivalentType(ResolvedType type)
                                                  {
                                                  }
                                              }
                                          
                                              public interface ResolvedValueDeclaration : ResolvedDeclaration
                                              {
                                                  ResolvedType GetType();
                                              }
                                          }
                                          """;

        Assert.Equal(expectedCSharpCode.ReplaceLineEndings(), parsed.ReplaceLineEndings());
    }

    [Fact]
    public void Verify_Interface_Extends_Are_Converted()
    {
        const string javaCode = """
                                public interface CharTermAttribute extends Attribute, CharSequence, Appendable {
                                }
                                """;
        var parsed = JavaToCSharpConverter.ConvertText(javaCode).ConvertedText ?? "";

        const string expected = """
                                namespace MyApp
                                {
                                    public interface CharTermAttribute : Attribute, CharSequence, Appendable
                                    {
                                    }
                                }
                                """;

        Assert.Equal(expected.ReplaceLineEndings(), parsed.ReplaceLineEndings());
    }
}
