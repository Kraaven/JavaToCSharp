using System.Text.RegularExpressions;

namespace JavaToCSharp;

/// <summary>
/// Read-only configuration for a conversion. Instances are safe to share across
/// concurrent <see cref="JavaToCSharpConverter.ConvertText"/> calls; conversion never mutates
/// this object. Configure it before converting and treat it as immutable afterwards.
/// </summary>
public class JavaConversionOptions
{
    private readonly List<Replacement> _packageReplacements = [];
    private readonly List<string> _usings = [];

    public IReadOnlyList<Replacement> PackageReplacements => _packageReplacements;

    public IReadOnlyList<string> Usings => _usings;

    public bool IncludeSubdirectories { get; set; } = true;

    public bool IncludeUsings { get; set; } = true;

    public bool IncludeNamespace { get; set; } = true;

    public bool UseDebugAssertForAsserts { get; set; }

    /// <summary>
    /// Unrecognized code is translated into comments
    /// </summary>
    public bool UseUnrecognizedCodeToComment { get; set; } = true;

    public bool ConvertSystemOutToConsole { get; set; }

    public bool IncludeComments { get; set; } = true;

    public bool UseFileScopedNamespaces { get; set; }

    public SyntaxMapping SyntaxMappings { get; set; } = new SyntaxMapping();

    public JavaConversionOptions AddPackageReplacement(string pattern, string replacement, RegexOptions options = RegexOptions.None)
    {
        _packageReplacements.Add(new Replacement(pattern, replacement, options));

        return this;
    }

    public JavaConversionOptions ClearUsings()
    {
        _usings.Clear();

        return this;
    }

    public JavaConversionOptions AddUsing(string ns)
    {
        _usings.Add(ns);

        return this;
    }

    public JavaConversionOptions SetUsings(IEnumerable<string> usings)
    {
        _usings.Clear();

        _usings.AddRange(usings);

        return this;
    }
}
