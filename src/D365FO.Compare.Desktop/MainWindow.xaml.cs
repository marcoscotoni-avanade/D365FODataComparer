using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using D365FO.Compare.Desktop.ViewModels;

namespace D365FO.Compare.Desktop;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.SetParentWindowProvider(() => new WindowInteropHelper(this).Handle);
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

    private void OnWindowClosing(object? sender, CancelEventArgs e) =>
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
}