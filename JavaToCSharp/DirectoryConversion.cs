using System.Diagnostics;

namespace JavaToCSharp
{
    public static partial class JavaToCSharpConverter
    {
        /// <summary>
        /// Converts every <c>*.java</c> file under <paramref name="inputDirectory"/> to C# in parallel.
        /// Each file's read → convert → write (or delete, in replace mode) pipeline runs as one
        /// async unit of work, so I/O for one file overlaps with CPU conversion of another.
        /// </summary>
        /// <param name="inputDirectory">The directory to scan for <c>*.java</c> files.</param>
        /// <param name="outputDirectory">
        /// <para>
        /// When non-null, the input directory tree is mirrored under this directory and the original
        /// <c>.java</c> files are left untouched.
        /// </para>
        /// <para>
        /// <b>WARNING:</b> when null, replace mode is active: each <c>Foo.java</c> is converted in
        /// place to <c>Foo.cs</c> in the <i>same directory</i> and the original <c>Foo.java</c> is
        /// <b>deleted</b> after a successful write. A failed file's <c>.java</c> is preserved (the
        /// delete only happens after a successful conversion and write).
        /// </para>
        /// </param>
        /// <param name="options">Conversion options. Defaults to <see cref="JavaConversionOptions"/>.</param>
        /// <param name="runOptions">Run options controlling parallelism, overwrite and error handling.</param>
        /// <param name="progress">Optional per-file progress reporter.</param>
        /// <param name="cancellationToken">Cancellation token that aborts the run.</param>
        /// <returns>A <see cref="ConversionResult"/> with per-file success/failure details and timing.</returns>
        public static async Task<ConversionResult> ConvertDirectoryAsync(
            DirectoryInfo inputDirectory,
            DirectoryInfo? outputDirectory,
            JavaConversionOptions? options = null,
            ConvertDirectoryOptions? runOptions = null,
            IProgress<FileConversionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            runOptions ??= new ConvertDirectoryOptions();

            if (!inputDirectory.Exists)
            {
                throw new DirectoryNotFoundException($"Input directory does not exist: {inputDirectory.FullName}");
            }

            bool replaceMode = outputDirectory is null;

            var searchOption = (options?.IncludeSubdirectories ?? true)
                ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            var files = Directory.EnumerateFiles(inputDirectory.FullName, "*.java", searchOption)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.Length)
                .ToArray();

            var indexedFiles = files.Select((file, index) => (File: file, Index: index)).ToArray();

            int total = files.Length;
            var succeeded = new System.Collections.Concurrent.ConcurrentBag<ConvertedFile>();
            var failed = new System.Collections.Concurrent.ConcurrentBag<FailedFile>();
            int completed = 0;

            var parallelOptions = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = runOptions.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            };

            await System.Threading.Tasks.Parallel.ForEachAsync(indexedFiles, parallelOptions, async (item, ct) =>
            {
                FileInfo javaFile = item.File;
                int fileNumber = item.Index + 1;

                string outPath;
                if (replaceMode)
                {
                    outPath = Path.ChangeExtension(javaFile.FullName, ".cs");
                }
                else
                {
                    outPath = ResolveMirroredOutputPath(inputDirectory,
                        outputDirectory ?? throw new InvalidOperationException("Output directory was not provided."),
                        javaFile);
                }

                if (!replaceMode)
                {
                    string? outDirName = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(outDirName))
                    {
                        Directory.CreateDirectory(outDirName);
                    }
                }

                if (!runOptions.Overwrite && File.Exists(outPath))
                {
                    options?.LogMessage($"[{fileNumber}/{total}] {javaFile.FullName} -> SKIPPED (output already exists)");

                    int done = System.Threading.Interlocked.Increment(ref completed);
                    progress?.Report(new FileConversionProgress(javaFile.FullName, outPath, true, null, done, total));
                    return;
                }

                try
                {
                    string javaText = await File.ReadAllTextAsync(javaFile.FullName, ct);

                    var result = ConvertTextResult(javaText, options, null, null, null);

                    if (result.FailedCount > 0)
                    {
                        throw result.FailedFiles[0].Exception;
                    }

                    await File.WriteAllTextAsync(outPath, result.ConvertedText ?? "", ct);

                    if (replaceMode)
                    {
                        File.Delete(javaFile.FullName);
                    }

                    succeeded.Add(new ConvertedFile(javaFile.FullName, outPath));

                    options?.LogMessage($"[{fileNumber}/{total}] {javaFile.FullName} -> OK");

                    int done = System.Threading.Interlocked.Increment(ref completed);
                    progress?.Report(new FileConversionProgress(javaFile.FullName, outPath, false, null, done, total));
                }
                catch (Exception ex)
                {
                    failed.Add(new FailedFile(javaFile.FullName, ex.Message, ex));

                    options?.LogMessage($"[{fileNumber}/{total}] {javaFile.FullName} -> FAILED: {ex}");

                    int done = System.Threading.Interlocked.Increment(ref completed);
                    progress?.Report(new FileConversionProgress(javaFile.FullName, outPath, false, ex.Message, done, total));

                    if (!runOptions.ContinueOnError)
                    {
                        throw;
                    }
                }
            });

            return new ConversionResult
            {
                TotalFiles = total,
                SucceededFiles = succeeded.ToList(),
                FailedFiles = failed.ToList(),
                Elapsed = sw.Elapsed,
            };
        }

        private static string ResolveMirroredOutputPath(DirectoryInfo input, DirectoryInfo output, FileInfo javaFile)
        {
            string dirName = javaFile.DirectoryName ?? input.FullName;
            string relativeDir = Path.GetRelativePath(input.FullName, dirName);
            string outDir = Path.Combine(output.FullName, relativeDir);
            return Path.Combine(outDir, Path.ChangeExtension(javaFile.Name, ".cs"));
        }
    }

    /// <summary>
    /// Options controlling a <see cref="JavaToCSharpConverter.ConvertDirectoryAsync"/> run.
    /// </summary>
    public sealed class ConvertDirectoryOptions
    {
        /// <summary>Maximum number of files converted concurrently. Defaults to <see cref="Environment.ProcessorCount"/>.</summary>
        public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

        /// <summary>
        /// If true (default), an existing output <c>.cs</c> file is overwritten. If false, the input
        /// file is skipped (reported via <see cref="FileConversionProgress.Skipped"/> and not counted
        /// as succeeded or failed).
        /// </summary>
        public bool Overwrite { get; init; } = true;

        /// <summary>
        /// If true (default), one file's failure is recorded in <see cref="ConversionResult.FailedFiles"/>
        /// and the run continues. If false, the first failure cancels the whole run (remaining in-flight
        /// files finish) and the failure is rethrown to the caller.
        /// </summary>
        public bool ContinueOnError { get; init; } = true;
    }

    public sealed record FileConversionProgress(
        string JavaFilePath,
        string OutputPath,
        bool Skipped,
        string? ErrorMessage,
        int FilesCompleted,
        int FilesTotal);
}