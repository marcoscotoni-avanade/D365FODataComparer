using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using D365FO.Compare.Application.Comparison;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Desktop.ViewModels;
using D365FO.Compare.Desktop.Views;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Configuration;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.UnitTests;

public sealed class MetadataWorkspaceTests
{
    internal static readonly UserSession Session = new("tenant", "user@example.com", "account");
    internal static AppConfiguration Configuration => new()
    {
        AuthenticationContext = new AuthenticationContext("tenant", "user@example.com", "account"),
        SourceEnvironment = Environment("source"),
        TargetEnvironment = Environment("target")
    };
    private static EnvironmentConnection Environment(string name) => new()
    {
        TenantId = "tenant", ProviderEnvironmentId = name, DisplayName = name,
        FinOpsUrl = new Uri($"https://{name}.example"), ValidationStatus = ConnectionValidationStatus.Succeeded
    };

    [Fact]
    public async Task CachedSchemasLoadWithoutNetworkAndAllowSelectiveComparison()
    {
        var provider = new Provider();
        var workspace = new MetadataWorkspaceViewModel(provider, new Repository(), new SchemaComparisonService());
        await workspace.ConfigureAsync(Configuration, Session, canRefresh: false, default);
        Assert.Equal(0, provider.Calls);
        Assert.True(workspace.CanCompare);
        Assert.False(workspace.CanRefresh);
        workspace.SelectedOnly = true;
        workspace.FilteredSelection[0].IsSelected = true;
        await workspace.CompareCommand.ExecuteAsync(null);
        Assert.Single(workspace.Result!.Entities);
        Assert.Equal("Selected entities", workspace.Result.Scope);
    }

    [Fact]
    public async Task RestoredConfigurationExposesValidationActionWithoutStartingMetadataDownload()
    {
        var provider = new Provider();
        var workspace = new MetadataWorkspaceViewModel(provider, new Repository(), new SchemaComparisonService());
        var requested = 0;
        workspace.ConnectionValidationRequested += (_, _) => requested++;

        await workspace.ConfigureAsync(Configuration, Session, canRefresh: false, default);
        Assert.False(workspace.CanRefresh);
        Assert.Contains("require revalidation", workspace.ConnectionState);
        Assert.True(workspace.ValidateConnectionsCommand.CanExecute(null));
        workspace.ValidateConnectionsCommand.Execute(null);
        Assert.Equal(1, requested);
        Assert.Equal(0, provider.Calls);

        await workspace.ClearCacheCommand.ExecuteAsync(null);
        Assert.True(workspace.ValidateConnectionsCommand.CanExecute(null));
        Assert.Contains("require revalidation", workspace.ConnectionState);

        await workspace.ConfigureAsync(Configuration, Session, canRefresh: true, default);
        Assert.True(workspace.CanRefresh);
        Assert.False(workspace.ValidateConnectionsCommand.CanExecute(null));
        Assert.Equal("Connections validated", workspace.ConnectionState);
    }

    [Fact]
    public async Task FailedRefreshKeepsOldSnapshotAndInvalidatesComparison()
    {
        var workspace = new MetadataWorkspaceViewModel(new Provider { Fail = true }, new Repository(), new SchemaComparisonService());
        await workspace.ConfigureAsync(Configuration, Session, canRefresh: true, default);
        var previous = workspace.Source.Snapshot;
        await workspace.CompareCommand.ExecuteAsync(null);
        Assert.NotNull(workspace.Result);
        await workspace.RefreshSourceCommand.ExecuteAsync(null);
        Assert.Same(previous, workspace.Source.Snapshot);
        Assert.Null(workspace.Result);
        Assert.Contains("Previous snapshot", workspace.Source.Status);
        Assert.Contains("required endpoint", workspace.Status);
        Assert.False(workspace.IsBusy);
    }

    [Fact]
    public async Task ResetClearsSchemasAndRejectsOtherAccountCache()
    {
        var workspace = new MetadataWorkspaceViewModel(new Provider(), new Repository(), new SchemaComparisonService());
        await workspace.ConfigureAsync(Configuration, Session, true, default);
        workspace.Reset();
        Assert.Null(workspace.Source.Snapshot);
        Assert.False(workspace.CanCompare);
        await workspace.ConfigureAsync(Configuration, Session with { HomeAccountId = "other" }, true, default);
        Assert.Null(workspace.Target.Snapshot);
        Assert.False(workspace.CanRefresh);
    }

    [Fact]
    public async Task SearchDoesNotChangeSelectionAndEmptySelectionShowsError()
    {
        var workspace = new MetadataWorkspaceViewModel(new Provider(), new Repository(), new SchemaComparisonService());
        await workspace.ConfigureAsync(Configuration, Session, true, default);
        workspace.SelectedOnly = true;
        await workspace.CompareCommand.ExecuteAsync(null);
        Assert.Null(workspace.Result);
        Assert.Contains("at least one", workspace.Status);
        workspace.SelectFilteredCommand.Execute(null);
        workspace.SelectionSearch = "not present";
        Assert.Empty(workspace.FilteredSelection);
        await workspace.CompareCommand.ExecuteAsync(null);
        Assert.Equal(2, workspace.Result!.Entities.Count);
    }

    [Fact]
    public async Task ClearCacheRemovesBothSnapshots()
    {
        var repository = new Repository();
        var workspace = new MetadataWorkspaceViewModel(new Provider(), repository, new SchemaComparisonService());
        await workspace.ConfigureAsync(Configuration, Session, true, default);
        await workspace.ClearCacheCommand.ExecuteAsync(null);
        Assert.Null(workspace.Source.Snapshot);
        Assert.Null(workspace.Target.Snapshot);
        Assert.Equal(2, repository.Deleted);
    }

    [Fact]
    public void WpfWorkspaceRendersLargeCatalogAtNormalAndMinimumSizesWithoutBindingErrors()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new D365FO.Compare.Desktop.App();
                application.InitializeComponent();
                using var output = new StringWriter();
                using var listener = new TextWriterTraceListener(output);
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
                var workspace = new MetadataWorkspaceViewModel(new Provider(), new Repository { EntityCount = 2000 }, new SchemaComparisonService());
                workspace.ConfigureAsync(Configuration, Session, true, default).GetAwaiter().GetResult();
                workspace.Source.SelectedEntity = workspace.Source.Snapshot!.Entities[0];
                var source = workspace.Source.Snapshot;
                var target = workspace.Target.Snapshot! with
                {
                    Entities = workspace.Target.Snapshot!.Entities.Select(entity => entity with
                    {
                        Fields = entity.Fields.Select(property => property.Name == "Account"
                            ? property with { MaxLength = ObservedValue.Declared("10") } : property).ToArray()
                    }).ToArray()
                };
                workspace.Result = new SchemaComparisonService().Compare(source, target);
                workspace.SelectedResult = workspace.Result.Entities[0];
                var view = new MetadataWorkspaceView { DataContext = workspace };
                var window = new Window { Content = view, Width = 1120, Height = 650, Left = -10000, Top = -10000,
                    ShowInTaskbar = false, ShowActivated = false, Background = Brushes.White };
                try
                {
                    window.Show();
                    var tabs = (TabControl)view.FindName("WorkspaceTabs");
                    var artifacts = Path.Combine(AppContext.BaseDirectory, "TestResults");
                    Directory.CreateDirectory(artifacts);
                    foreach (var width in new[] { 1120, 900 })
                    {
                        window.Width = width;
                        foreach (var tab in new[] { 0, 1, 2 })
                        {
                            tabs.SelectedIndex = tab;
                            window.UpdateLayout();
                            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                            window.UpdateLayout();
                            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window);
                            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                            Assert.True(pixels.Where((_, index) => index % 4 == 0).Distinct().Count() > 5);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var image = File.Create(Path.Combine(artifacts, $"metadata-{width}-tab{tab}.png"));
                            encoder.Save(image);
                            Assert.True(VisualChildren<DataGridRow>(view).Count() < 150);
                        }
                    }
                    listener.Flush();
                    Assert.True(string.IsNullOrWhiteSpace(output.ToString()), output.ToString());
                }
                finally
                {
                    window.Close();
                    PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
                    application.Shutdown();
                }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "WPF rendering did not complete.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }

    internal sealed class Provider : IEntityMetadataProvider
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public Task<SchemaSnapshot> CreateSnapshotAsync(EnvironmentConnection environment, IProgress<MetadataProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Fail ? Task.FromException<SchemaSnapshot>(new InvalidDataException("A required endpoint failed."))
                : Task.FromResult(SchemaComparisonTests.Snapshot());
        }
    }
    internal sealed class Repository : ISchemaSnapshotRepository
    {
        public int Deleted { get; private set; }
        public int EntityCount { get; init; } = 2;
        public Task<SchemaSnapshot?> LoadAsync(EnvironmentConnection environment, string accountId, CancellationToken cancellationToken)
        {
            var snapshot = SchemaComparisonTests.Snapshot();
            if (EntityCount != 2) snapshot = snapshot with { Entities = Enumerable.Range(0, EntityCount)
                .Select(index => snapshot.Entities[0] with { EntitySetName = $"Customers{index:D4}" }).ToArray() };
            return Task.FromResult<SchemaSnapshot?>(snapshot);
        }
        public Task SaveAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(EnvironmentConnection environment, string accountId, CancellationToken cancellationToken) { Deleted++; return Task.CompletedTask; }
    }
}