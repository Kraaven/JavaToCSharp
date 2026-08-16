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

    /// <summary>
    /// When true, one log line is emitted per converted file to each enabled sink
    /// (<see cref="LogToConsole"/>, <see cref="LogToCallback"/>, <see cref="LogToFile"/>).
    /// False by default.
    /// </summary>
    public bool EnableLogging { get; set; }

    /// <summary>When <see cref="EnableLogging"/> is true, write each log line to <see cref="Console.WriteLine"/>.</summary>
    public bool LogToConsole { get; set; }

    /// <summary>When <see cref="EnableLogging"/> is true, invoke <see cref="Log"/> for each log line.</summary>
    public bool LogToCallback { get; set; }

    /// <summary>
    /// Callback sink used when <see cref="EnableLogging"/> and <see cref="LogToCallback"/> are true.
    /// May be invoked from multiple threads concurrently during a directory conversion, so the
    /// handler must be thread-safe.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>When <see cref="EnableLogging"/> is true, append each log line to <see cref="LogFilePath"/>.</summary>
    public bool LogToFile { get; set; }

    /// <summary>
    /// Path of the log file when <see cref="LogToFile"/> is true. Defaults to "conversion.log",
    /// resolved relative to the process working directory unless an absolute path is given.
    /// </summary>
    public string LogFilePath { get; set; } = "conversion.log";

    private readonly object _logLock = new();

    /// <summary>
    /// Emits a log line to every enabled sink. No-op unless <see cref="EnableLogging"/> is true.
    /// Thread-safe: file appends are serialized with an internal lock.
    /// </summary>
    /// <param name="message">The log line to write (without a trailing newline).</param>
    public void LogMessage(string message)
    {
        if (!EnableLogging)
        {
            return;
        }

        if (LogToConsole)
        {
            Console.WriteLine(message);
        }

        if (LogToCallback)
        {
            Log?.Invoke(message);
        }

        if (LogToFile)
        {
            lock (_logLock)
            {
                File.AppendAllText(LogFilePath, message + Environment.NewLine);
            }
        }
    }

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
