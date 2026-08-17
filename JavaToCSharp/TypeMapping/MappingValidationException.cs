namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Thrown when a mapping file fails schema validation or when two loaded mapping
/// files define conflicting entries. Per MAPPING_FILES.md, validation happens
/// at load time and any failure fails the whole load.
/// </summary>
public sealed class MappingValidationException : Exception
{
    public MappingValidationException(string message)
        : base(message)
    {
    }
}
