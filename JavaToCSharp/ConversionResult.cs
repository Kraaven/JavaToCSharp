namespace JavaToCSharp
{
    /// <summary>One file that converted successfully.</summary>
    /// <param name="InputPath">Source .java path. "&lt;inline&gt;" when converted via the in-memory ConvertText(string) overload.</param>
    /// <param name="OutputPath">Where the .cs output was written. Null when converted via the in-memory ConvertText(string) overload (caller reads ConversionResult.ConvertedText instead).</param>
    public sealed record ConvertedFile(string InputPath, string? OutputPath);

    /// <summary>One file that failed to convert.</summary>
    /// <param name="InputPath">Source .java path. "&lt;inline&gt;" when converted via the in-memory ConvertText(string) overload.</param>
    /// <param name="ErrorMessage">The failure message.</param>
    /// <param name="Exception">The underlying exception.</param>
    public sealed record FailedFile(string InputPath, string ErrorMessage, Exception Exception);

    /// <summary>
    /// An exception raised while converting a specific file, along with the path of that file.
    /// </summary>
    /// <param name="InputPath">Source .java path. "&lt;inline&gt;" when converted via the in-memory ConvertText(string) overload.</param>
    /// <param name="Exception">The exception that was raised.</param>
    public sealed record ConversionException(string InputPath, Exception Exception);

    /// <summary>
    /// Result of a single-file (ConvertText) or multi-file (ConvertDirectory) conversion run.
    /// </summary>
    public sealed class ConversionResult
    {
        /// <summary>The total number of files that were part of the run.</summary>
        public required int TotalFiles { get; init; }

        /// <summary>The files that converted successfully.</summary>
        public IReadOnlyList<ConvertedFile> SucceededFiles { get; init; } = [];

        /// <summary>The files that failed to convert.</summary>
        public IReadOnlyList<FailedFile> FailedFiles { get; init; } = [];

        /// <summary>Number of successfully converted files.</summary>
        public int SucceededCount => SucceededFiles.Count;

        /// <summary>Number of failed files. A value of 0 means the run succeeded.</summary>
        public int FailedCount => FailedFiles.Count;

        /// <summary>
        /// All exceptions hit during the run, each paired with the path of the file that raised it,
        /// for later logging. Convenience view over <see cref="FailedFiles"/>.
        /// </summary>
        public IReadOnlyList<ConversionException> Exceptions => FailedFiles
            .Select(f => new ConversionException(f.InputPath, f.Exception))
            .ToList();

        /// <summary>Wall-clock time spent in the run.</summary>
        public TimeSpan Elapsed { get; init; }

        /// <summary>Populated only when produced by the in-memory ConvertText(string) overload.</summary>
        public string? ConvertedText { get; init; }

        /// <summary>Convenience for CLI/GUI reporting: files converted per second.</summary>
        public double FilesPerSecond => Elapsed.TotalSeconds > 0 ? TotalFiles / Elapsed.TotalSeconds : 0;
    }
}
