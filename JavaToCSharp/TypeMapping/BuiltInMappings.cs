using System.Reflection;

namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Access to the bundled, curated mapping specs shipped with the library. Each spec is a
/// YAML mapping file (same schema as user-authored files) embedded in the assembly and
/// loaded through the normal validation/merge pipeline, so a built-in spec behaves exactly
/// like a YAML file of the same content.
///
/// <para>Specs are strictly opt-in. Load a single spec, combine several, or mix them with
/// user YAML files:</para>
/// <code>
/// options.MappingIndex = BuiltInMappings.Load(BuiltInMappingSpec.Console, BuiltInMappingSpec.SystemCollections);
///
/// // or, mixed with user YAML via the builder:
/// var index = new TypeMappingBuilder()
///     .AddBuiltIn(BuiltInMappingSpec.Console)
///     .AddYamlFiles(["team-mappings.yaml"])
///     .Build();
/// </code>
/// </summary>
public static class BuiltInMappings
{
    private const string ResourcePrefix = "JavaToCSharp.TypeMapping.BuiltInMappings.";

    /// <summary>All bundled specs, in a fixed deterministic order.</summary>
    public static IReadOnlyList<BuiltInMappingSpec> All { get; } = Enum.GetValues<BuiltInMappingSpec>();

    /// <summary>Returns the raw YAML source of the given spec.</summary>
    public static string YamlFor(BuiltInMappingSpec spec)
    {
        var resourceName = ResourcePrefix + FileNameFor(spec);
        var assembly = typeof(BuiltInMappings).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new MappingValidationException(
                $"Built-in mapping spec '{spec}' is missing from the assembly (expected embedded resource '{resourceName}').");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Loads and merges the given specs into one validated index.</summary>
    public static TypeMappingIndex Load(params BuiltInMappingSpec[] specs)
        => Load((IEnumerable<BuiltInMappingSpec>)specs);

    /// <summary>Loads and merges the given specs into one validated index.
    /// Specs are applied in enum order regardless of input order, so identical inputs always
    /// produce the same index.</summary>
    public static TypeMappingIndex Load(IEnumerable<BuiltInMappingSpec> specs)
    {
        var builder = new TypeMappingBuilder();
        foreach (var spec in specs.Distinct().OrderBy(s => s))
        {
            builder.AddBuiltIn(spec);
        }

        return builder.Build();
    }

    /// <summary>Convenience access to the Console spec as a standalone index.</summary>
    public static TypeMappingIndex Console => Load(BuiltInMappingSpec.Console);

    /// <summary>Convenience access to the System (java.lang) spec as a standalone index.</summary>
    public static TypeMappingIndex System => Load(BuiltInMappingSpec.System);

    /// <summary>Convenience access to the System.Collections spec as a standalone index.</summary>
    public static TypeMappingIndex SystemCollections => Load(BuiltInMappingSpec.SystemCollections);

    /// <summary>Convenience access to the System.IO spec as a standalone index.</summary>
    public static TypeMappingIndex SystemIO => Load(BuiltInMappingSpec.SystemIO);

    /// <summary>Convenience access to the System.Threading spec as a standalone index.</summary>
    public static TypeMappingIndex SystemThreading => Load(BuiltInMappingSpec.SystemThreading);

    /// <summary>Convenience access to the System.Text spec as a standalone index.</summary>
    public static TypeMappingIndex SystemText => Load(BuiltInMappingSpec.SystemText);

    internal static string FileNameFor(BuiltInMappingSpec spec) => spec switch
    {
        BuiltInMappingSpec.Console => "Console.yaml",
        BuiltInMappingSpec.System => "System.yaml",
        BuiltInMappingSpec.SystemCollections => "System.Collections.yaml",
        BuiltInMappingSpec.SystemIO => "System.IO.yaml",
        BuiltInMappingSpec.SystemThreading => "System.Threading.yaml",
        BuiltInMappingSpec.SystemText => "System.Text.yaml",
        BuiltInMappingSpec.SystemTime => "System.Time.yaml",
        BuiltInMappingSpec.SystemTextRegularExpressions => "System.Text.RegularExpressions.yaml",
        BuiltInMappingSpec.SystemNumerics => "System.Numerics.yaml",
        _ => throw new ArgumentOutOfRangeException(nameof(spec), spec, "Unknown built-in mapping spec."),
    };
}
