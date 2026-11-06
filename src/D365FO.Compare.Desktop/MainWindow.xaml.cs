using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using D365FO.Compare.Desktop.ViewModels;
using D365FO.Compare.Infrastructure.Metadata;

namespace D365FO.Compare.Desktop;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closingRequested;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel, MetadataWindowContext metadataWindowContext)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.SetParentWindowProvider(() => new WindowInteropHelper(this).Handle);
        metadataWindowContext.ParentWindowProvider = () => Dispatcher.Invoke(() => new WindowInteropHelper(this).Handle);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closing += OnWindowClosing;
    }

    public Task InitializeAsync() => _viewModel.InitializeAsync();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentStepIndex))
        {
            WizardTabs.SelectedIndex = _viewModel.CurrentStepIndex;
        }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closingRequested) return;
        _closingRequested = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.CancelOperationCommand.Execute(null);
        try { await _viewModel.DataWorkspace.CloseAsync(); }
        catch (Exception)
        {
            MessageBox.Show("Temporary encrypted data could not be fully cleaned up. Cleanup will be retried on the next data run.", "Data comparison", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _viewModel.Workspace.Reset();
            _allowClose = true;
            Close();
        }
    }
}