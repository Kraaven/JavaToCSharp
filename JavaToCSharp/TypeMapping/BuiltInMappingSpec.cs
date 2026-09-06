namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Identifies one of the bundled, curated mapping specs shipped with the library.
/// Each spec maps a family of Java packages to its C# equivalent (e.g. the Console
/// spec maps <c>System.out.println</c> chains to <c>Console.WriteLine</c>). Specs are
/// strictly opt-in and compose with each other and with user-supplied YAML mapping
/// files through the normal merge/validation pipeline.
/// </summary>
public enum BuiltInMappingSpec
{
    /// <summary><c>java.lang.System</c> console I/O → <c>System.Console</c>
    /// (<c>System.out.println</c> → <c>Console.WriteLine</c>, <c>currentTimeMillis</c>,
    /// <c>gc</c>, <c>exit</c>, …).</summary>
    Console,

    /// <summary><c>java.lang</c> core types → <c>System</c> (<c>Math</c>, boxing types,
    /// <c>String</c>, <c>Object</c>).</summary>
    System,

    /// <summary><c>java.util</c> collections → <c>System.Collections</c> (<c>List</c>,
    /// <c>Dictionary</c>, <c>Collections</c>, <c>Objects</c>, …).</summary>
    SystemCollections,

    /// <summary><c>java.io</c> / <c>java.nio.file</c> → <c>System.IO</c> (<c>File</c>,
    /// <c>Files</c>, stream types, …).</summary>
    SystemIO,

    /// <summary><c>java.lang.Thread</c> and <c>java.util.concurrent</c> →
    /// <c>System.Threading</c>.</summary>
    SystemThreading,

    /// <summary>Text builders and formatting → <c>System.Text</c> (<c>StringBuilder</c>,
    /// <c>MessageFormat</c>).</summary>
    SystemText,

    /// <summary><c>java.time</c> date/time types → <c>System</c> (<c>LocalDate</c> →
    /// <c>DateTime</c>, <c>Duration</c> → <c>TimeSpan</c>, <c>ZoneId</c> →
    /// <c>TimeZoneInfo</c>).</summary>
    SystemTime,

    /// <summary><c>java.util.regex</c> → <c>System.Text.RegularExpressions</c>
    /// (<c>Pattern</c> → <c>Regex</c>, <c>Matcher</c> → <c>Match</c>).</summary>
    SystemTextRegularExpressions,

    /// <summary>Arbitrary-precision numerics → <c>System.Numerics</c>
    /// (<c>BigInteger</c>, <c>BigDecimal</c> → <c>decimal</c>).</summary>
    SystemNumerics,
}
