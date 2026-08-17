using JavaToCSharp;
using JavaToCSharp.TypeMapping;
using JavaToCSharpGui.Properties;

namespace JavaToCSharpGui;

public static class CurrentOptions
{
    public static List<string> MappingFiles { get; } = new(
        Settings.Default.MappingFiles.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static List<BuiltInMappingSpec> MappingSpecs { get; } = ParseMappingSpecs(Settings.Default.MappingSpecs);

    static CurrentOptions()
    {
        Options.IncludeUsings = Settings.Default.UseUsingsPreference;
        Options.IncludeNamespace = Settings.Default.UseNamespacePreference;
        Options.IncludeComments = Settings.Default.IncludeComments;
        Options.UseDebugAssertForAsserts = Settings.Default.UseDebugAssertPreference;
        Options.UseUnrecognizedCodeToComment = Settings.Default.UseUnrecognizedCodeToComment;
#pragma warning disable CS0618 // Type or member is obsolete
        Options.ConvertSystemOutToConsole = Settings.Default.ConvertSystemOutToConsole;
#pragma warning restore CS0618
        Options.UseFileScopedNamespaces = Settings.Default.UseFileScopedNamespaces;
        Options.PreserveJavaMethodNames = Settings.Default.PreserveJavaMethodNames;
        Options.EnableLogging = Settings.Default.EnableLogging;
        Options.LogToFile = Settings.Default.LogToFile;
        Options.LogFilePath = Settings.Default.LogFilePath;

        Options.SetUsings(Settings.Default.Usings.Split(';'));

        ReloadMappingIndex();
    }

    public static JavaConversionOptions Options { get; } = new JavaConversionOptions();

    /// <summary>
    /// Rebuilds <see cref="JavaConversionOptions.MappingIndex"/> from the selected built-in specs
    /// and user mapping files, merging them through the shared validation pipeline.
    /// </summary>
    public static void ReloadMappingIndex()
    {
        if (MappingSpecs.Count == 0 && MappingFiles.Count == 0)
        {
            Options.MappingIndex = null;
            return;
        }

        var builder = new TypeMappingBuilder();
        foreach (var spec in MappingSpecs)
        {
            builder.AddBuiltIn(spec);
        }

        if (MappingFiles.Count > 0)
        {
            builder.AddYamlFiles(MappingFiles);
        }

        Options.MappingIndex = builder.Build();
    }

    public static void Persist()
    {
        Settings.Default.UseUsingsPreference = Options.IncludeUsings;
        Settings.Default.UseNamespacePreference = Options.IncludeNamespace;
        Settings.Default.IncludeComments = Options.IncludeComments;
        Settings.Default.UseDebugAssertPreference = Options.UseDebugAssertForAsserts;
        Settings.Default.UseUnrecognizedCodeToComment = Options.UseUnrecognizedCodeToComment;
#pragma warning disable CS0618 // Type or member is obsolete
        Settings.Default.ConvertSystemOutToConsole = Options.ConvertSystemOutToConsole;
#pragma warning restore CS0618
        Settings.Default.UseFileScopedNamespaces = Options.UseFileScopedNamespaces;
        Settings.Default.PreserveJavaMethodNames = Options.PreserveJavaMethodNames;
        Settings.Default.EnableLogging = Options.EnableLogging;
        Settings.Default.LogToFile = Options.LogToFile;
        Settings.Default.LogFilePath = Options.LogFilePath;
        Settings.Default.Usings = string.Join(";", Options.Usings);
        Settings.Default.MappingFiles = string.Join(";", MappingFiles);
        Settings.Default.MappingSpecs = string.Join(";", MappingSpecs.Select(s => s.ToString()));

        Settings.Default.Save();
    }

    private static List<BuiltInMappingSpec> ParseMappingSpecs(string raw)
    {
        var specs = new List<BuiltInMappingSpec>();
        foreach (var name in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<BuiltInMappingSpec>(name, ignoreCase: true, out var spec) && !specs.Contains(spec))
            {
                specs.Add(spec);
            }
        }

        return specs;
    }
}
