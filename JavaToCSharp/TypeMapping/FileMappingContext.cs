namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Holds the set of Java packages that are actively imported or auto-imported in the current
/// file being converted. Used to gate mapping resolution: only types from active packages
/// are resolved through the <see cref="TypeMappingIndex"/>, preventing false-positive
/// remapping on user-defined types that happen to share names with mapped types.
/// </summary>
public sealed class FileMappingContext
{
    private readonly HashSet<string> _activeJavaPackages;
    private readonly TypeMappingIndex? _index;

    /// <summary>
    /// Creates a new file mapping context by scanning the file's imports against the mapping index.
    /// </summary>
    /// <param name="index">The mapping index to check against. If null, all packages are considered active.</param>
    /// <param name="importNames">Fully qualified import names from the Java file (e.g. "java.util.List").</param>
    /// <param name="autoImportedPackages">Packages that are auto-imported in Java (e.g. "java.lang").</param>
    public FileMappingContext(
        TypeMappingIndex? index,
        IEnumerable<string> importNames,
        IEnumerable<string>? autoImportedPackages = null)
    {
        _index = index;
        _activeJavaPackages = new HashSet<string>(StringComparer.Ordinal);

        if (index is null)
        {
            // No mapping index → all packages are "active" (no gating needed, everything passes through)
            return;
        }

        // Add auto-imported packages (java.lang.* by default)
        if (autoImportedPackages is not null)
        {
            foreach (var pkg in autoImportedPackages)
            {
                _activeJavaPackages.Add(pkg);
            }
        }

        // Scan imports and extract package prefixes that have mappings
        foreach (var importName in importNames)
        {
            var lastDot = importName.LastIndexOf('.');
            if (lastDot <= 0) continue;

            // Check each prefix of the import path (e.g. "java.util" from "java.util.List")
            var parts = importName.Split('.');
            for (int i = parts.Length - 1; i >= 1; i--)
            {
                var prefix = string.Join('.', parts, 0, i);
                if (index.TryGetCSharpPackages(prefix, out _))
                {
                    _activeJavaPackages.Add(prefix);
                }
            }
        }
    }

    /// <summary>
    /// Returns true if the given Java class name belongs to a package that has at least one
    /// mapping in the index. Always returns true when no mapping index is configured.
    /// </summary>
    public bool IsPackageActive(string javaClassName)
    {
        if (_index is null) return true;

        var lastDot = javaClassName.LastIndexOf('.');
        if (lastDot <= 0)
        {
            // Simple name — check if any mapped package is active
            return _activeJavaPackages.Count > 0;
        }

        var pkg = javaClassName[..lastDot];
        return _activeJavaPackages.Contains(pkg);
    }

    /// <summary>
    /// Returns true if any mapped package was found in this file's imports.
    /// When false, the entire mapping resolution can be skipped for this file.
    /// </summary>
    public bool HasAnyMappedPackage => _activeJavaPackages.Count > 0;
}
