using Avalonia.Controls;
using Avalonia.Interactivity;
using JavaToCSharpGui.Infrastructure;
using JavaToCSharpGui.ViewModels;

namespace JavaToCSharpGui.Views;

public partial class SettingsWindow : Window
{
    private readonly IHostStorageProvider? _storageProvider;

    public SettingsWindow() : this(null)
    {
    }

    public SettingsWindow(IHostStorageProvider? storageProvider)
    {
        _storageProvider = storageProvider;
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var vm = new SettingsWindowViewModel(_storageProvider);
        DataContext = vm;

        vm.CloseRequested += OnCloseRequested;

        Usings.DoubleTapped += (_, _) => vm.RemoveSelectedUsingCommand.Execute(null);
    }

    private void OnCloseRequested(object? o, EventArgs eventArgs) => Close();

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
        {
            vm.CloseRequested -= OnCloseRequested;
        }

        base.OnUnloaded(e);
    }
}
