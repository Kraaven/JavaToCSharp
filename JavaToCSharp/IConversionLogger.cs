namespace JavaToCSharp;

/// <summary>
/// Receives diagnostic events during the Java-to-C# conversion process.
/// Implement this interface to capture warnings, progress updates, or other
/// conversion-time diagnostics without coupling to the conversion engine.
/// </summary>
public interface IConversionLogger
{
    /// <summary>
    /// Called when the conversion state changes (e.g. Parsing, BuildingCSharpAst, EmittingCode).
    /// </summary>
    void OnStateChanged(ConversionState state);

    /// <summary>
    /// Called when a non-fatal warning is produced during conversion.
    /// </summary>
    void OnWarning(string message, int javaLineNumber);
}
