using JavaToCSharp;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace JavaToCSharpCli;

/// <summary>
/// The main JavaToCSharpCli program class.
/// </summary>
public class Program
{
    private static readonly ILoggerFactory _loggerFactory;
    private static readonly ILogger _logger;

    private static readonly Option<bool> _includeSubdirectoriesOption = new("--include-subdirectories")
    {
        Description = "When the command is dir, converts files in all subdirectories",
        DefaultValueFactory = _ => true,
    };

    private static readonly Option<bool> _includeUsingsOption = new("--include-usings")
    {
        Description = "Include using directives in output",
        DefaultValueFactory = _ => true,
    };

    private static readonly Option<bool> _includeNamespaceOption = new("--include-namespace")
    {
        Description = "Include namespace in output",
        DefaultValueFactory = _ => true,
    };

    private static readonly Option<bool> _includeCommentsOption = new("--include-comments")
    {
        Description = "Include comments in output",
        DefaultValueFactory = _ => true,
    };

    private static readonly Option<bool> _useDebugAssertOption = new("--use-debug-assert")
    {
        Description = "Use Debug.Assert for asserts",
        DefaultValueFactory = _ => false,
    };

    private static readonly Option<bool> _commentUnrecognizedCodeOption = new("--comment-unrecognized-code")
    {
        Description = "Include unrecognized code in output as commented-out code",
        DefaultValueFactory = _ => true,
    };

    private static readonly Option<bool> _systemOutToConsoleOption = new("--system-out-to-console")
    {
        Description = "Convert System.out calls to Console",
        DefaultValueFactory = _ => false,
    };

    private static readonly Option<bool> _fileScopedNamespacesOption = new("--file-scoped-namespaces")
    {
        Description = "Use file-scoped namespaces in C# output",
        DefaultValueFactory = _ => false,
    };

    private static readonly Option<bool> _preserveJavaMethodNamesOption = new("--preserve-java-method-names")
    {
        Description = "Keep Java member names and method calls as-is instead of PascalCasing them / rewriting to C# properties (default: true)",
        DefaultValueFactory = _ => true,
    };

    private static readonly Option<bool> _clearDefaultUsingsOption = new("--clear-usings")
    {
        Description = "Remove all default usings provided by this app",
        DefaultValueFactory = _ => false,
    };

    private static readonly Option<List<string>> _addUsingsOption = new("--add-using")
    {
        Description = "Adds a using directive to the collection of usings",
        HelpName = "namespace",
    };

    private static readonly Option<string?> _mappingsFileNameOption = new("--mappings-file")
    {
        Description = "A yaml file with syntax mappings from imports, methods and annotations",
    };

    private static readonly Option<int> _maxParallelismOption = new("--max-parallelism")
    {
        Description = "Maximum degree of parallelism for directory conversion (default: number of CPU cores)",
        DefaultValueFactory = _ => Environment.ProcessorCount,
    };

    private static readonly Option<bool> _continueOnErrorOption = new("--continue-on-error")
    {
        Description = "Continue converting files even if some fail (default: true)",
        DefaultValueFactory = _ => true,
    };

    private static readonly Option<bool> _overwriteOption = new("--overwrite")
    {
        Description = "Overwrite existing .cs files (default: true)",
        DefaultValueFactory = _ => true,
    };

    private static readonly Option<bool> _statsOption = new("--stats")
    {
        Description = "Print conversion statistics (total, succeeded, failed, elapsed, files/sec)",
        DefaultValueFactory = _ => false,
    };

    private static readonly Option<bool> _yesOption = new("--yes", "-y")
    {
        Description = "Skip the confirmation prompt when --output is omitted (WARNING: this deletes source .java files)",
        DefaultValueFactory = _ => false,
    };

    private static readonly Option<bool> _enableLoggingOption = new("--enable-logging")
    {
        Description = "Log every file converted (path and result) to the console",
        DefaultValueFactory = _ => false,
        Recursive = true,
    };

    private static readonly Option<string?> _logFileOption = new("--log-file")
    {
        Description = "Append every file converted (path and result) to this file (default: conversion.log). Can be passed without a value",
        Arity = ArgumentArity.ZeroOrOne,
        Recursive = true,
    };

    static Program()
    {
        _loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        _logger = _loggerFactory.CreateLogger<Program>();
    }

    public static async Task Main(string[] args)
    {
        var rootCommand = new RootCommand("Java to C# Converter")
        {
            Description = "A syntactic transformer of source code from Java to C#.",
        };

        rootCommand.Subcommands.Add(CreateFileCommand());
        rootCommand.Subcommands.Add(CreateDirectoryCommand());

        rootCommand.Options.Add(_includeSubdirectoriesOption);
        rootCommand.Options.Add(_includeUsingsOption);
        rootCommand.Options.Add(_includeNamespaceOption);
        rootCommand.Options.Add(_includeCommentsOption);
        rootCommand.Options.Add(_useDebugAssertOption);
        rootCommand.Options.Add(_commentUnrecognizedCodeOption);
        rootCommand.Options.Add(_systemOutToConsoleOption);
        rootCommand.Options.Add(_fileScopedNamespacesOption);
        rootCommand.Options.Add(_preserveJavaMethodNamesOption);
        rootCommand.Options.Add(_clearDefaultUsingsOption);
        rootCommand.Options.Add(_addUsingsOption);
        rootCommand.Options.Add(_mappingsFileNameOption);
        rootCommand.Options.Add(_enableLoggingOption);
        rootCommand.Options.Add(_logFileOption);

        await rootCommand.Parse(args).InvokeAsync();

        // flush logs
        _loggerFactory.Dispose();
    }

    private static Command CreateFileCommand()
    {
        var inputArgument = new Argument<FileInfo>("input")
        {
            Description = "A Java source code file to convert",
        };

        var outputArgument = new Argument<FileInfo?>("output")
        {
            Description = "Path to place the C# output file, or stdout if omitted",
            DefaultValueFactory = _ => null,
        };

        var fileCommand = new Command("file", "Convert a Java file to C#")
        {
            inputArgument,
            outputArgument,
        };

        fileCommand.SetAction(context =>
        {
            var input = context.GetRequiredValue(inputArgument);
            var output = context.GetValue(outputArgument);

            var options = GetJavaConversionOptions(context);
            ConfigureLogging(context, options);

            ConvertToCSharpFile(input, output, options);
        });

        return fileCommand;
    }

    private static JavaConversionOptions GetJavaConversionOptions(ParseResult context)
    {
        var options = new JavaConversionOptions
        {
            IncludeSubdirectories = context.GetValue(_includeSubdirectoriesOption),
            IncludeUsings = context.GetValue(_includeUsingsOption),
            IncludeComments = context.GetValue(_includeCommentsOption),
            IncludeNamespace = context.GetValue(_includeNamespaceOption),
            ConvertSystemOutToConsole = context.GetValue(_systemOutToConsoleOption),
            UseDebugAssertForAsserts = context.GetValue(_useDebugAssertOption),
            UseUnrecognizedCodeToComment = context.GetValue(_commentUnrecognizedCodeOption),
            UseFileScopedNamespaces = context.GetValue(_fileScopedNamespacesOption),
            PreserveJavaMethodNames = context.GetValue(_preserveJavaMethodNamesOption),
        };

        if (context.GetValue(_clearDefaultUsingsOption))
        {
            options.ClearUsings();
        }
        else
        {
            options.AddUsing("System");
            options.AddUsing("System.Collections.Generic");
            options.AddUsing("System.Collections.ObjectModel");
            options.AddUsing("System.Linq");
            options.AddUsing("System.Text");
        }

        foreach (string ns in context.GetValue(_addUsingsOption) ?? [])
        {
            options.AddUsing(ns);
        }

        var mappingsFile = context.GetValue(_mappingsFileNameOption);
        if (!string.IsNullOrEmpty(mappingsFile))
        {
            options.SyntaxMappings = ReadMappingsFile(mappingsFile);
        }

        return options;
    }

    private static void ConfigureLogging(ParseResult context, JavaConversionOptions options)
    {
        if (context.GetValue(_enableLoggingOption))
        {
            options.EnableLogging = true;
            options.LogToConsole = true;
        }

        if (context.GetResult(_logFileOption) is not null)
        {
            options.EnableLogging = true;
            options.LogToFile = true;
            options.LogFilePath = context.GetValue(_logFileOption) ?? "conversion.log";
        }
    }

    private static SyntaxMapping ReadMappingsFile(string mappingsFile)
    {
        // Let fail if cannot be read or deserialized to display the exception message in the CLI
        var mappingsStr = File.ReadAllText(mappingsFile);
        return SyntaxMapping.Deserialize(mappingsStr);
    }

    private static Command CreateDirectoryCommand()
    {
        var inputArgument = new Argument<DirectoryInfo>("input")
        {
            Description = "A directory containing Java source code files to convert",
        };

        var outputArgument = new Argument<DirectoryInfo?>("output")
        {
            Description = "Path to place the C# output files. If omitted, original .java files are converted in place and DELETED (WARNING: destructive mode)",
            DefaultValueFactory = _ => null,
        };

        var dirCommand = new Command("dir", "Convert a directory containing Java files to C#")
        {
            inputArgument,
            outputArgument,
            _maxParallelismOption,
            _continueOnErrorOption,
            _overwriteOption,
            _statsOption,
            _yesOption,
        };

        dirCommand.SetAction(async context =>
        {
            var input = context.GetRequiredValue(inputArgument);
            var output = context.GetValue(outputArgument);

            var options = GetJavaConversionOptions(context);
            ConfigureLogging(context, options);

            var runOptions = new ConvertDirectoryOptions
            {
                MaxDegreeOfParallelism = context.GetValue(_maxParallelismOption),
                ContinueOnError = context.GetValue(_continueOnErrorOption),
                Overwrite = context.GetValue(_overwriteOption),
            };

            await ConvertToCSharpDir(input, output, options, runOptions, context);
        });

        return dirCommand;
    }

    private static async Task ConvertToCSharpDir(
        DirectoryInfo inputDirectory,
        DirectoryInfo? outputDirectory,
        JavaConversionOptions options,
        ConvertDirectoryOptions runOptions,
        ParseResult context)
    {
        if (!inputDirectory.Exists)
        {
            _logger.LogError("Java input folder {path} doesn't exist!", inputDirectory);
            return;
        }

        bool replaceMode = outputDirectory is null;

        if (replaceMode && !context.GetValue(_yesOption))
        {
            _logger.LogWarning(
                "WARNING: no output directory specified. Original .java files will be DELETED after successful conversion.");
            Console.Out.Write("Proceed? [y/N]: ");
            string? answer = Console.ReadLine();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Aborted.");
                return;
            }
        }

        if (!replaceMode && outputDirectory is not null && !outputDirectory.Exists)
        {
            outputDirectory.Create();
        }

        _logger.LogInformation("Converting directory {path}...", inputDirectory.FullName);

        var result = await JavaToCSharpConverter.ConvertDirectoryAsync(
            inputDirectory,
            outputDirectory,
            options,
            runOptions,
            progress: new Progress<FileConversionProgress>(report =>
            {
                if (options.EnableLogging)
                {
                    // The library already logs each file (with [index/total]); avoid duplicate output.
                    return;
                }

                if (report.Skipped)
                {
                    _logger.LogInformation("[SKIPPED] {output}: Output already exists (overwrite=false)", report.OutputPath);
                }
                else if (!string.IsNullOrEmpty(report.ErrorMessage))
                {
                    _logger.LogWarning("[FAILED] {output}: {message}", report.OutputPath, report.ErrorMessage);
                }
                else
                {
                    _logger.LogInformation("[SUCCESS] {output}", report.OutputPath);
                }
            }));

        if (context.GetValue(_statsOption))
        {
            _logger.LogInformation("Total files: {total}", result.TotalFiles);
            _logger.LogInformation("Succeeded: {succeeded}", result.SucceededCount);
            _logger.LogInformation("Failed: {failed}", result.FailedCount);
            _logger.LogInformation("Elapsed: {elapsed}", result.Elapsed);
            _logger.LogInformation("Files/sec: {rate:F2}", result.FilesPerSecond);

            foreach (var failedFile in result.FailedFiles)
            {
                _logger.LogError("Failed file {path}: {message}", failedFile.InputPath, failedFile.ErrorMessage);
            }
        }
        else if (result.FailedCount > 0)
        {
            foreach (var failedFile in result.FailedFiles)
            {
                _logger.LogError("Failed file {path}: {message}", failedFile.InputPath, failedFile.ErrorMessage);
            }
        }
    }

    private static void ConvertToCSharpFile(FileSystemInfo inputFile, FileSystemInfo? outputFile, JavaConversionOptions options, bool overwrite = true)
    {
        if (!overwrite && outputFile is { Exists: true })
        {
            _logger.LogInformation("{outputFilePath} exists, skip to next.", outputFile);
        }
        else if (inputFile.Exists)
        {
            var result = JavaToCSharpConverter.ConvertText(
                File.ReadAllText(inputFile.FullName),
                options,
                sourcePath: inputFile.FullName,
                warning: eventArgs =>
                {
                    if (outputFile != null)
                    {
                        _logger.LogWarning("Line {JavaLineNumber}: {Message}", eventArgs.JavaLineNumber,
                            eventArgs.Message);
                    }
                    else
                    {
                        Console.Out.WriteLine(eventArgs.Message);
                    }
                });

            if (result.FailedCount > 0)
            {
                _logger.LogError("{filePath} failed! {type}: {message}", inputFile.Name,
                    result.Exceptions[0].Exception.GetType().Name,
                    result.Exceptions[0].Exception.Message);

                if (outputFile != null)
                {
                    File.WriteAllLines(Path.ChangeExtension(outputFile.FullName, ".error"),
                        result.Exceptions.Select(e => $"{e.Exception.GetType().Name}: {e.Exception.Message}"));
                }

                return;
            }

            OutputFileOrPrint(outputFile?.FullName, result.ConvertedText ?? "");

            if (outputFile != null)
            {
                _logger.LogInformation("{filePath} converted!", inputFile.Name);
            }
        }
        else
        {
            _logger.LogError("Java input file {filePath} doesn't exist!", inputFile.FullName);
        }
    }

    private static void OutputFileOrPrint(string? fileName, string contents)
    {
        if (fileName == null)
        {
            Console.Out.WriteLine(contents);
        }
        else
        {
            File.WriteAllText(fileName, contents);
        }
    }
}
