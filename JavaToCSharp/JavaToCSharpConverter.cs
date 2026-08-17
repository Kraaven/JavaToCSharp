using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using com.github.javaparser;
using com.github.javaparser.ast;
using com.github.javaparser.ast.body;
using ikvm.io;
using JavaToCSharp.Declarations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp;

public static partial class JavaToCSharpConverter
{
    /// <summary>
    /// Rewrites invalid primitive method type witnesses (e.g. <c>.&lt;boolean&gt;</c>) to boxed forms
    /// so JavaParser can accept them.
    /// </summary>
    [GeneratedRegex(@"\.<\s*(boolean|byte|short|int|long|char|float|double)\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex PrimitiveTypeWitnessRegex();

    private static readonly Dictionary<string, string> PrimitiveToBoxed = new(StringComparer.Ordinal)
    {
        ["boolean"] = "Boolean",
        ["byte"] = "Byte",
        ["short"] = "Short",
        ["int"] = "Integer",
        ["long"] = "Long",
        ["char"] = "Character",
        ["float"] = "Float",
        ["double"] = "Double",
    };

    /// <summary>
    /// Converts a single Java source snippet to C#.
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="ConversionResult"/> and never throws for parse/conversion failures —
    /// check <see cref="ConversionResult.FailedCount"/> (and read the generated text from
    /// <see cref="ConversionResult.ConvertedText"/>) instead of catching exceptions.
    /// When <see cref="JavaConversionOptions.EnableLogging"/> is set, one log line is emitted:
    /// <c>&lt;path&gt; -&gt; OK</c> on success and <c>&lt;path&gt; -&gt; FAILED: &lt;exception&gt;</c> on failure.
    /// </remarks>
    /// <param name="javaText">The Java source code to convert.</param>
    /// <param name="options">Conversion options. Defaults to <see cref="JavaConversionOptions"/>.</param>
    /// <param name="sourcePath">Optional source path used in logs and <see cref="ConversionResult"/> entries; defaults to "&lt;inline&gt;".</param>
    /// <param name="stateChanged">Optional callback raised as conversion state changes.</param>
    /// <param name="warning">Optional callback raised for conversion warnings.</param>
    /// <returns>A <see cref="ConversionResult"/> describing the single-file run.</returns>
    public static ConversionResult ConvertText(
        string? javaText,
        JavaConversionOptions? options = null,
        string? sourcePath = null,
        Action<ConversionState>? stateChanged = null,
        Action<ConversionWarningEventArgs>? warning = null)
    {
        var result = ConvertTextResult(javaText, options, sourcePath, stateChanged, warning);

        if (result.FailedCount > 0)
        {
            options?.LogMessage($"{result.FailedFiles[0].InputPath} -> FAILED: {result.FailedFiles[0].Exception}");
        }
        else
        {
            options?.LogMessage($"{result.SucceededFiles[0].InputPath} -> OK");
        }

        return result;
    }

    private static ConversionResult ConvertTextResult(
        string? javaText,
        JavaConversionOptions? options,
        string? sourcePath,
        Action<ConversionState>? stateChanged,
        Action<ConversionWarningEventArgs>? warning)
    {
        var sw = Stopwatch.StartNew();
        string source = sourcePath ?? "<inline>";

        try
        {
            string? csharp = ConvertTextCore(javaText, options, stateChanged, warning);

            return new ConversionResult
            {
                TotalFiles = 1,
                SucceededFiles = [new ConvertedFile(source, null)],
                FailedFiles = [],
                Elapsed = sw.Elapsed,
                ConvertedText = csharp,
            };
        }
        catch (Exception ex)
        {
            return new ConversionResult
            {
                TotalFiles = 1,
                SucceededFiles = [],
                FailedFiles = [new FailedFile(source, ex.Message, ex)],
                Elapsed = sw.Elapsed,
                ConvertedText = null,
            };
        }
    }

    private static string? ConvertTextCore(
        string? javaText,
        JavaConversionOptions? options,
        Action<ConversionState>? stateChanged,
        Action<ConversionWarningEventArgs>? warning)
    {
        options ??= new JavaConversionOptions();

        var context = new ConversionContext(options, stateChanged, warning);

        context.NotifyStateChanged(ConversionState.Starting);

        javaText = RewritePrimitiveTypeWitnesses(javaText ?? string.Empty);

        var textBytes = Encoding.UTF8.GetBytes(javaText);

        using var memoryStream = new MemoryStream(textBytes);
        using var wrapper = new InputStreamWrapper(memoryStream);

        context.NotifyStateChanged(ConversionState.ParsingJavaAst);

        var parser = new JavaParser();
        parser.getParserConfiguration().setLanguageLevel(ParserConfiguration.LanguageLevel.JAVA_25);

        var parsed = parser.parse(wrapper);

        if (!parsed.isSuccessful())
        {
            var problems = parsed.getProblems();
            var problemText = new StringBuilder();

            foreach (var problem in problems.OfType<Problem>())
            {
                problemText.AppendLine(problem.getMessage());
            }

            throw new InvalidOperationException($"Parsing failed:{Environment.NewLine}{problemText}");
        }

        var result = parsed.getResult().FromRequiredOptional<CompilationUnit>();

        context.NotifyStateChanged(ConversionState.BuildingCSharpAst);

        var types = result.getTypes().ToList<TypeDeclaration>() ?? [];
        var imports = result.getImports()?.ToList<ImportDeclaration>() ?? [];
        var package = result.getPackageDeclaration().FromOptional<PackageDeclaration>();

        // Pre-scan: build the file-level mapping context for namespace-gated mapping resolution
        if (options.MappingIndex is not null)
        {
            var importNames = imports
                .Where(i => !i.isStatic())
                .Select(i => i.getNameAsString())
                .ToList();

            // Add the file's own package as active (types in the same package are always available)
            if (package is not null)
            {
                var pkgName = package.getNameAsString();
                if (!string.IsNullOrEmpty(pkgName))
                {
                    importNames.Add(pkgName + ".*");
                }
            }

            context.FileMappingContext = new TypeMapping.FileMappingContext(
                options.MappingIndex,
                importNames,
                options.AutoImportedJavaPackages);
        }

        var rootMembers = new List<MemberDeclarationSyntax>();
        NameSyntax? namespaceNameSyntax = null;

        if (options.IncludeNamespace)
        {
            string packageName = package?.getName()?.toString() ?? "MyApp";

            foreach (var packageReplacement in options.PackageReplacements)
            {
                if (string.IsNullOrWhiteSpace(packageName))
                {
                    continue;
                }

                packageName = packageReplacement.Replace(packageName);
            }

            packageName = TypeHelper.Capitalize(packageName);

            namespaceNameSyntax = SyntaxFactory.ParseName(packageName);
        }

        foreach (var type in types)
        {
            if (type is ClassOrInterfaceDeclaration classOrIntType)
            {
                if (classOrIntType.isInterface())
                {
                    var interfaceSyntax = ClassOrInterfaceDeclarationVisitor.VisitInterfaceDeclaration(context, classOrIntType);
                    rootMembers.Add(interfaceSyntax.NormalizeWhitespace().WithTrailingNewLines());
                }
                else
                {
                    var classSyntax = ClassOrInterfaceDeclarationVisitor.VisitClassDeclaration(context, classOrIntType);
                    rootMembers.Add(classSyntax.NormalizeWhitespace().WithTrailingNewLines());
                }
            }
            else if (type is EnumDeclaration enumType)
            {
                var enumSyntax = EnumDeclarationVisitor.VisitEnumDeclaration(context, enumType);
                rootMembers.Add(enumSyntax.NormalizeWhitespace().WithTrailingNewLines());
            }
            else if (type is RecordDeclaration recordType)
            {
                var recordSyntax = RecordDeclarationVisitor.VisitRecordDeclaration(context, recordType);
                rootMembers.Add(recordSyntax.NormalizeWhitespace().WithTrailingNewLines());
            }
        }

        if (rootMembers.Count > 1)
        {
            for (int i = 1; i < rootMembers.Count; i++)
            {
                rootMembers[i] = rootMembers[i].WithLeadingNewLines();
            }
        }

        if (namespaceNameSyntax is not null)
        {
            if (options.UseFileScopedNamespaces && rootMembers.Count > 0)
            {
                rootMembers[0] = rootMembers[0].WithLeadingNewLines();
            }

            MemberDeclarationSyntax namespaceSyntax =
                options.UseFileScopedNamespaces
                ? SyntaxFactory.FileScopedNamespaceDeclaration(namespaceNameSyntax)
                    .NormalizeWhitespace()
                    .WithTrailingNewLines()
                    .WithMembers(SyntaxFactory.List(rootMembers))
                : SyntaxFactory.NamespaceDeclaration(namespaceNameSyntax)
                    .WithMembers(SyntaxFactory.List(rootMembers))
                    .NormalizeWhitespace();

            rootMembers = [namespaceSyntax];
        }

        var root = SyntaxFactory.CompilationUnit(
                externs: [],
                usings: SyntaxFactory.List(UsingsHelper.GetUsings(context, imports, options)),
                attributeLists: [],
                members: SyntaxFactory.List(rootMembers)
            );

        root = root.WithPackageFileComments(context, result, package);

        var postConversionSanitizer = new SanitizingSyntaxRewriter();
        var sanitizedRoot = postConversionSanitizer.VisitCompilationUnit(root);

        if (sanitizedRoot is null)
        {
            return null;
        }

        var tree = SyntaxFactory.SyntaxTree(sanitizedRoot);

        context.NotifyStateChanged(ConversionState.Done);

        return tree.GetText().ToString();
    }

    internal static string RewritePrimitiveTypeWitnesses(string javaText)
    {
        return PrimitiveTypeWitnessRegex().Replace(javaText, static match =>
        {
            string primitive = match.Groups[1].Value;
            return PrimitiveToBoxed.TryGetValue(primitive, out var boxed)
                ? ".<" + boxed + ">"
                : match.Value;
        });
    }
}
