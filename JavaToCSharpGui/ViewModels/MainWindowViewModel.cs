using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JavaToCSharp;
using JavaToCSharpGui.Infrastructure;
using JavaToCSharpGui.Views;

namespace JavaToCSharpGui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IHostStorageProvider? _storageProvider;
    private readonly IUIDispatcher _dispatcher;
    private readonly ITextClipboard? _clipboard;

    private bool _usingFolderConvert;

    /// <summary>
    /// Constructor for the Avalonia Designer view inside the IDE.
    /// </summary>
    public MainWindowViewModel()
    {
        _dispatcher = new UIDispatcher(Dispatcher.UIThread);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: not null
            } desktop)
        {
            _storageProvider = new HostStorageProvider(desktop.MainWindow.StorageProvider);
            _clipboard = new TextClipboard(desktop.MainWindow.Clipboard);
        }
    }

    /// <summary>
    /// Real constructor
    /// </summary>
    /// <param name="storageProvider">The storage provider.</param>
    /// <param name="dispatcher">The UI Thread Dispatcher.</param>
    /// <param name="clipboard">The clipboard.</param>
    public MainWindowViewModel(IHostStorageProvider storageProvider, IUIDispatcher dispatcher, ITextClipboard clipboard)
    {
        _storageProvider = storageProvider;
        _dispatcher = dispatcher;
        _clipboard = clipboard;
        DisplayName = "Java to C# Converter";

        CurrentOptions.Options.Log = message =>
            _ = DispatcherInvoke(() => FolderOutputFiles.Add(message));
    }

    [ObservableProperty] private AvaloniaList<FileInfo> _folderInputFiles = [];

    [ObservableProperty] private AvaloniaList<string> _folderOutputFiles = [];

    [ObservableProperty] private TextDocument _javaText = new();

    [ObservableProperty] private TextDocument _cSharpText = new();

    [ObservableProperty] private string _openPath = "";

    [ObservableProperty] private string _openFolderPath = "";

    [ObservableProperty] private string _conversionStateLabel = "";

    public FontFamily MonospaceFontFamily { get; } =
        FontFamily.Parse("Cascadia Code,SF Mono,DejaVu Sans Mono,Menlo,Consolas");

    [ObservableProperty] private bool _isConvertEnabled = true;

    [ObservableProperty] private string _message = "";

    [ObservableProperty] private string _messageTitle = "";

    [RelayCommand]
    private async Task Convert()
    {
        IsConvertEnabled = false;
        _usingFolderConvert = false;

        var text = JavaText.Text;

        await Task.Run(async () =>
        {
            try
            {
                var result = JavaToCSharpConverter.ConvertText(
                    text,
                    CurrentOptions.Options,
                    stateChanged: SetConversionState,
                    warning: e => HandleWarning(e, file: null));

                await DispatcherInvoke(() =>
                {
                    if (result.FailedCount > 0)
                    {
                        ShowMessage($"Conversion completed with {result.FailedCount} error(s):\n{string.Join(Environment.NewLine, result.Exceptions.Select(e => $"- {e.InputPath}: {e.Exception.Message}"))}",
                            "Conversion Error");
                    }
                    else
                    {
                        CSharpText.Text = result.ConvertedText ?? "";
                    }
                });
            }
            catch (Exception ex)
            {
                await DispatcherInvoke(() =>
                    ShowMessage($"There was an error converting the text to C#: {ex.GetBaseException().Message}",
                        "Conversion Error"));

                await DispatcherInvoke(() => ConversionStateLabel = "");
            }
            finally
            {
                await DispatcherInvoke(() => IsConvertEnabled = true);
            }
        });
    }

    [RelayCommand]
    private async Task OpenFolderDialog()
    {
        if (_storageProvider?.CanPickFolder is true)
        {
            FolderPickerOpenOptions options = new()
            {
                Title = "Folder Browser",
                AllowMultiple = false,
                SuggestedStartLocation = await _storageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents)
            };

            var result = await _storageProvider.OpenFolderPickerAsync(options);

            if (!result.Any())
            {
                return;
            }

            string path = result[0].Path.LocalPath;
            var dir = new DirectoryInfo(path);

            if (!dir.Exists)
            {
                OpenFolderPath = string.Empty;
                FolderInputFiles.Clear();

                return;
            }

            OpenFolderPath = path;

            await Task.Run(() =>
            {
                var files = dir.GetFiles("*.java", SearchOption.AllDirectories);
                FolderInputFiles.Clear();
                FolderInputFiles.AddRange(files);
                return Task.CompletedTask;
            });
        }
    }

    [RelayCommand]
    private async Task FolderConvert()
    {
        if (FolderInputFiles.Count == 0)
        {
            ShowMessage("No Java files found in the specified folder!");
            return;
        }

        IsConvertEnabled = false;
        _usingFolderConvert = true;
        FolderOutputFiles.Clear();

        var progressQueue = new ConcurrentQueue<FileConversionProgress>();

        var flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        flushTimer.Tick += (_, _) => FlushFolderProgress(progressQueue);
        flushTimer.Start();

        try
        {
            var dir = new DirectoryInfo(OpenFolderPath);
            string outDirName = $"{dir.Name}_csharp_output";
            var outDir = new DirectoryInfo(Path.Combine(OpenFolderPath, outDirName));

            if (!outDir.Exists)
                outDir.Create();

            var result = await JavaToCSharpConverter.ConvertDirectoryAsync(
                dir,
                outDir,
                CurrentOptions.Options,
                new ConvertDirectoryOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    ContinueOnError = true,
                    Overwrite = true,
                },
                progress: new Progress<FileConversionProgress>(progressQueue.Enqueue));

            flushTimer.Stop();
            FlushFolderProgress(progressQueue);

            if (result.FailedCount > 0)
            {
                await DispatcherInvoke(() => ShowMessage(
                    $"Completed {result.SucceededCount}/{result.TotalFiles} files, {result.FailedCount} failed.",
                    "Folder Conversion"));
            }
        }
        catch (Exception ex)
        {
            await DispatcherInvoke(() =>
                ShowMessage($"There was an error converting the directory: {ex.GetBaseException().Message}",
                    "Directory Conversion Error"));

            await DispatcherInvoke(() => ConversionStateLabel = "");
        }
        finally
        {
            flushTimer.Stop();
            await DispatcherInvoke(() => IsConvertEnabled = true);
        }
    }

    private void FlushFolderProgress(ConcurrentQueue<FileConversionProgress> queue)
    {
        while (queue.TryDequeue(out var report))
        {
            if (CurrentOptions.Options.EnableLogging)
            {
                // The library already logs each file (with [index/total]) via the Log callback;
                // avoid duplicate lines in the output list.
                continue;
            }

            if (report.Skipped)
            {
                FolderOutputFiles.Add($"[SKIPPED] {report.OutputPath}");
            }
            else if (!string.IsNullOrEmpty(report.ErrorMessage))
            {
                FolderOutputFiles.Add($"[FAILED] {report.OutputPath}: {report.ErrorMessage}");
            }
            else
            {
                FolderOutputFiles.Add($"[SUCCESS] {report.OutputPath}");
            }
        }
    }

    [RelayCommand]
    private void ClearMessage()
    {
        MessageTitle = "";
        Message = "";
        IsMessageShown = false;
    }

    private void ShowMessage(string message, string title = "")
    {
        MessageTitle = title;
        Message = message;
        IsMessageShown = true;
    }

    [ObservableProperty] private bool _isMessageShown;

    private void SetConversionState(ConversionState state)
    {
        _ = DispatcherInvoke(() =>
        {
            ConversionStateLabel = state switch
            {
                ConversionState.Starting => "Starting...",
                ConversionState.ParsingJavaAst => "Parsing Java code...",
                ConversionState.BuildingCSharpAst => "Building C# AST...",
                ConversionState.Done => "Done!",
                _ => ConversionStateLabel
            };
        });
    }

    private void HandleWarning(ConversionWarningEventArgs e, string? file)
    {
        if (_usingFolderConvert)
        {
            _ = DispatcherInvoke(() =>
            {
                CSharpText.Text = $"{CSharpText.Text}{Environment.NewLine}" +
                                  $"=================={Environment.NewLine}" +
                                  $"[WARN] {file}{e.JavaLineNumber}{Environment.NewLine}" +
                                  $"\t\tMessage: {e.Message}{Environment.NewLine}";
            });
        }
        else
        {
            _ = DispatcherInvoke(() => ShowMessage($"Java Line {e.JavaLineNumber}: {e.Message}", "Warning Encountered"));
        }
    }

    [RelayCommand]
    private async Task OpenFileDialog()
    {
        if (_storageProvider?.CanOpen is true)
        {
            var filePickerOpenOptions = new FilePickerOpenOptions
            {
                FileTypeFilter = new FilePickerFileType[]
                {
                    new("Java files")
                    {
                        Patterns = new[] { "*.java" },
                    }
                },
            };

            var result = await _storageProvider.OpenFilePickerAsync(filePickerOpenOptions);

            if (result.Any())
            {
                OpenPath = result[0].Path.LocalPath;
                JavaText.Text = await File.ReadAllTextAsync(result[0].Path.LocalPath);
            }
        }
    }

    [RelayCommand]
    private async Task PasteJavaCode()
    {
        if (_clipboard is null)
        {
            return;
        }

        string? clipboardText = await _clipboard.GetTextAsync();
        if (clipboardText is null)
        {
            ShowMessage("Clipboard is empty or unavailable.", "Paste Error");
            return;
        }

        JavaText.Text = clipboardText;
        ConversionStateLabel = "Pasted Java code from clipboard!";

        await Task.Delay(2000);

        await _dispatcher.InvokeAsync(() => { ConversionStateLabel = ""; }, DispatcherPriority.Background);
    }

    [RelayCommand]
    private async Task ClipboardConvert()
    {
        if (_clipboard is null)
        {
            return;
        }

        // Get clipboard text
        string? clipboardText = await _clipboard.GetTextAsync();
        if (clipboardText is null)
        {
            ShowMessage("Clipboard is empty or unavailable.", "Clipboard Convert Error");
            return;
        }

        // Set Java text
        JavaText.Text = clipboardText;

        // Perform conversion
        IsConvertEnabled = false;
        _usingFolderConvert = false;

        await Task.Run(async () =>
        {
            try
            {
                var result = JavaToCSharpConverter.ConvertText(
                    clipboardText,
                    CurrentOptions.Options,
                    stateChanged: SetConversionState,
                    warning: e => HandleWarning(e, file: null));

                if (result.FailedCount > 0)
                {
                    await DispatcherInvoke(() => ShowMessage(
                        $"Conversion completed with {result.FailedCount} error(s):\n{string.Join(Environment.NewLine, result.Exceptions.Select(e => $"- {e.InputPath}: {e.Exception.Message}"))}",
                        "Conversion Error"));

                    await DispatcherInvoke(() => ConversionStateLabel = "Conversion completed with errors.");
                    return;
                }

                string? csharp = result.ConvertedText;

                await DispatcherInvoke(() => CSharpText.Text = csharp ?? "");
                await _clipboard.SetTextAsync(csharp ?? "");
                ConversionStateLabel = "Conversion complete! C# code copied to clipboard.";

                await Task.Delay(2000);
                await _dispatcher.InvokeAsync(() => { ConversionStateLabel = ""; }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                await DispatcherInvoke(() =>
                    ShowMessage($"There was an error converting the text to C#: {ex.GetBaseException().Message}",
                        "Conversion Error"));

                await DispatcherInvoke(() => ConversionStateLabel = "");
            }
            finally
            {
                await DispatcherInvoke(() => IsConvertEnabled = true);
            }
        });
    }

    [RelayCommand]
    private async Task CopyOutput()
    {
        if (_clipboard is null)
        {
            return;
        }

        await _clipboard.SetTextAsync(CSharpText.Text);
        ConversionStateLabel = "Copied C# code to clipboard!";

        await Task.Delay(2000);

        await _dispatcher.InvokeAsync(() => { ConversionStateLabel = ""; }, DispatcherPriority.Background);
    }

    [RelayCommand]
    private async Task SaveOutput()
    {
        if (_storageProvider?.CanSave is true)
        {
            IStorageFolder? startLocation = null;

            if (Path.GetDirectoryName(OpenPath) is { } dir)
            {
                startLocation = await _storageProvider.TryGetFolderFromPathAsync(dir);
            }

            startLocation ??= await _storageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);

            var filePickerSaveOptions = new FilePickerSaveOptions
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(OpenPath) + ".cs",
                SuggestedStartLocation = startLocation,
                Title = "Save C# File"
            };

            var result = await _storageProvider.OpenSaveFileDialogAsync(filePickerSaveOptions);

            if (result is not null)
            {
                await File.WriteAllTextAsync(result.Path.LocalPath, CSharpText.Text);

                ConversionStateLabel = "Saved C# code to file!";

                await Task.Delay(2000);

                await _dispatcher.InvokeAsync(() => { ConversionStateLabel = ""; }, DispatcherPriority.Background);
            }
        }
    }

    [RelayCommand]
    private static void ForkMeOnGitHub() => Process.Start(new ProcessStartInfo
    {
        FileName = "https://github.com/paulirwin/javatocsharp",
        UseShellExecute = true
    });

    [RelayCommand]
    private void OpenSettings()
    {
        var parent = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        var settings = new SettingsWindow(_storageProvider);

        if (parent is not null)
        {
            settings.ShowDialog(parent);
        }
        else
        {
            settings.Show();
        }
    }

    [RelayCommand]
    private static void OpenAbout()
    {
        var parent = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        var about = new AboutWindow();

        if (parent is not null)
        {
            about.ShowDialog(parent);
        }
        else
        {
            about.Show();
        }
    }

    private async Task DispatcherInvoke(Action callback) =>
        await _dispatcher.InvokeAsync(callback, DispatcherPriority.Normal);
}
