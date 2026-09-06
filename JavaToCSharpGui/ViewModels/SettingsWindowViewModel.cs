using Avalonia.Collections;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JavaToCSharp.TypeMapping;
using JavaToCSharpGui.Infrastructure;

namespace JavaToCSharpGui.ViewModels;

public partial class SettingsWindowViewModel : ViewModelBase
{
    private readonly IHostStorageProvider? _storageProvider;

    [ObservableProperty] private string _addUsingInput = "";

    [ObservableProperty] private string _addMappingFileInput = "";

    [ObservableProperty] private AvaloniaList<string> _usings = new(CurrentOptions.Options.Usings);

    [ObservableProperty] private AvaloniaList<string> _mappingFiles = new(CurrentOptions.MappingFiles);

    [ObservableProperty] private bool _consoleMapping = CurrentOptions.MappingSpecs.Contains(BuiltInMappingSpec.Console);

    [ObservableProperty] private bool _systemMapping = CurrentOptions.MappingSpecs.Contains(BuiltInMappingSpec.System);

    [ObservableProperty] private bool _collectionsMapping = CurrentOptions.MappingSpecs.Contains(BuiltInMappingSpec.SystemCollections);

    [ObservableProperty] private bool _ioMapping = CurrentOptions.MappingSpecs.Contains(BuiltInMappingSpec.SystemIO);

    [ObservableProperty] private bool _threadingMapping = CurrentOptions.MappingSpecs.Contains(BuiltInMappingSpec.SystemThreading);

    [ObservableProperty] private bool _textMapping = CurrentOptions.MappingSpecs.Contains(BuiltInMappingSpec.SystemText);

    [ObservableProperty] private bool _timeMapping = CurrentOptions.MappingSpecs.Contains(BuiltInMappingSpec.SystemTime);

    [ObservableProperty] private bool _regexMapping = CurrentOptions.MappingSpecs.Contains(BuiltInMappingSpec.SystemTextRegularExpressions);

    [ObservableProperty] private bool _numericsMapping = CurrentOptions.MappingSpecs.Contains(BuiltInMappingSpec.SystemNumerics);

    [ObservableProperty] private string? _selectedUsing;

    [ObservableProperty] private string? _selectedMappingFile;

    [ObservableProperty] private bool _includeUsings = CurrentOptions.Options.IncludeUsings;

    [ObservableProperty] private bool _includeNamespace = CurrentOptions.Options.IncludeNamespace;

    [ObservableProperty] private bool _includeComments = CurrentOptions.Options.IncludeComments;

    [ObservableProperty] private bool _useDebugAssertForAsserts = CurrentOptions.Options.UseDebugAssertForAsserts;

    [ObservableProperty] private bool _unrecognizedCodeToComment = CurrentOptions.Options.UseUnrecognizedCodeToComment;

#pragma warning disable CS0618 // Type or member is obsolete
    [ObservableProperty] private bool _convertSystemOutToConsole = CurrentOptions.Options.ConvertSystemOutToConsole;
#pragma warning restore CS0618

    [ObservableProperty] private bool _useFileScopedNamespaces = CurrentOptions.Options.UseFileScopedNamespaces;

    [ObservableProperty] private bool _preserveJavaMethodNames = CurrentOptions.Options.PreserveJavaMethodNames;

    [ObservableProperty] private bool _enableLogging = CurrentOptions.Options.EnableLogging;

    [ObservableProperty] private bool _logToFile = CurrentOptions.Options.LogToFile;

    [ObservableProperty] private string _logFilePath = CurrentOptions.Options.LogFilePath;

    public SettingsWindowViewModel(IHostStorageProvider? storageProvider = null)
    {
        _storageProvider = storageProvider;
    }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void RemoveSelectedUsing()
    {
        if (SelectedUsing is not null && Usings.Contains(SelectedUsing))
        {
            Usings.Remove(SelectedUsing);
        }
    }

    [RelayCommand]
    private void AddUsing()
    {
        Usings.Add(AddUsingInput);
        AddUsingInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveSelectedMappingFile()
    {
        if (SelectedMappingFile is not null && MappingFiles.Contains(SelectedMappingFile))
        {
            MappingFiles.Remove(SelectedMappingFile);
        }
    }

    [RelayCommand]
    private void AddMappingFile()
    {
        var path = AddMappingFileInput.Trim().Trim('"');
        if (path.Length > 0 && !MappingFiles.Contains(path))
        {
            MappingFiles.Add(path);
        }

        AddMappingFileInput = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanBrowseFolder))]
    private async Task BrowseMappingFolder()
    {
        if (_storageProvider is null || !_storageProvider.CanPickFolder)
        {
            return;
        }

        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder containing YAML mapping files",
            AllowMultiple = false,
        });

        if (folders.Count == 0)
        {
            return;
        }

        var folder = folders[0];
        var folderPath = folder.Path.LocalPath;
        if (string.IsNullOrEmpty(folderPath))
        {
            return;
        }

        var yamlFiles = Directory.GetFiles(folderPath, "*.yaml", SearchOption.AllDirectories);
        foreach (var yamlFile in yamlFiles)
        {
            if (!MappingFiles.Contains(yamlFile))
            {
                MappingFiles.Add(yamlFile);
            }
        }
    }

    private bool CanBrowseFolder() => _storageProvider?.CanPickFolder == true;

    [RelayCommand]
    private void Save()
    {
        CurrentOptions.Options.IncludeUsings = IncludeUsings;
        CurrentOptions.Options.IncludeNamespace = IncludeNamespace;
        CurrentOptions.Options.IncludeComments = IncludeComments;
        CurrentOptions.Options.UseDebugAssertForAsserts = UseDebugAssertForAsserts;
        CurrentOptions.Options.UseUnrecognizedCodeToComment = UnrecognizedCodeToComment;
#pragma warning disable CS0618 // Type or member is obsolete
        CurrentOptions.Options.ConvertSystemOutToConsole = ConvertSystemOutToConsole;
#pragma warning restore CS0618
        CurrentOptions.Options.UseFileScopedNamespaces = UseFileScopedNamespaces;
        CurrentOptions.Options.PreserveJavaMethodNames = PreserveJavaMethodNames;
        CurrentOptions.Options.EnableLogging = EnableLogging;
        CurrentOptions.Options.LogToFile = LogToFile;
        CurrentOptions.Options.LogFilePath = LogFilePath;

        CurrentOptions.Options.SetUsings(Usings);

        CurrentOptions.MappingFiles.Clear();
        CurrentOptions.MappingFiles.AddRange(MappingFiles);

        CurrentOptions.MappingSpecs.Clear();
        if (ConsoleMapping)
        {
            CurrentOptions.MappingSpecs.Add(BuiltInMappingSpec.Console);
        }

        if (SystemMapping)
        {
            CurrentOptions.MappingSpecs.Add(BuiltInMappingSpec.System);
        }

        if (CollectionsMapping)
        {
            CurrentOptions.MappingSpecs.Add(BuiltInMappingSpec.SystemCollections);
        }

        if (IoMapping)
        {
            CurrentOptions.MappingSpecs.Add(BuiltInMappingSpec.SystemIO);
        }

        if (ThreadingMapping)
        {
            CurrentOptions.MappingSpecs.Add(BuiltInMappingSpec.SystemThreading);
        }

        if (TextMapping)
        {
            CurrentOptions.MappingSpecs.Add(BuiltInMappingSpec.SystemText);
        }

        if (TimeMapping)
        {
            CurrentOptions.MappingSpecs.Add(BuiltInMappingSpec.SystemTime);
        }

        if (RegexMapping)
        {
            CurrentOptions.MappingSpecs.Add(BuiltInMappingSpec.SystemTextRegularExpressions);
        }

        if (NumericsMapping)
        {
            CurrentOptions.MappingSpecs.Add(BuiltInMappingSpec.SystemNumerics);
        }

        CurrentOptions.ReloadMappingIndex();

        CurrentOptions.Persist();

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
