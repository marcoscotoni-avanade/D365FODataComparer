using System.IO;
using System.Net.Http;
using System.Windows;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.Comparison;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Application.Configuration;
using D365FO.Compare.Application.EnvironmentDiscovery;
using D365FO.Compare.Application.Validation;
using D365FO.Compare.Desktop.ViewModels;
using D365FO.Compare.Infrastructure.Authentication;
using D365FO.Compare.Infrastructure.FoHttp;
using D365FO.Compare.Infrastructure.Metadata;
using D365FO.Compare.Infrastructure.ODataEdm;
using D365FO.Compare.Infrastructure.ODataData;
using D365FO.Compare.Infrastructure.Export;
using D365FO.Compare.Infrastructure.RestMetadata;
using D365FO.Compare.Infrastructure.EnvironmentDiscovery;
using D365FO.Compare.Infrastructure.Persistence;
using D365FO.Compare.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace D365FO.Compare.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
	private IHost? _host;

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		var logDirectory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"D365FODataComparisonTool",
			"Logs");
		Directory.CreateDirectory(logDirectory);

		var logger = new LoggerConfiguration()
			.MinimumLevel.Information()
			.WriteTo.File(Path.Combine(logDirectory, "app-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
			.CreateLogger();

		try
		{
			_host = Host.CreateDefaultBuilder(e.Args)
				.UseContentRoot(AppContext.BaseDirectory)
				.ConfigureAppConfiguration((_, configuration) =>
					configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false))
				.ConfigureLogging(logging =>
				{
					logging.ClearProviders();
					logging.AddSerilog(logger, dispose: true);
				})
				.ConfigureServices((_, services) =>
				{
					services.AddSingleton<IAuthenticationService, MsalAuthenticationService>();
					services.AddSingleton<IConfigurationRepository, JsonConfigurationRepository>();
					services.AddSingleton<IFinOpsHostPolicy, ApprovedFinOpsHostPolicy>();
					services.AddHttpClient("power-platform")
						.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
					services.AddHttpClient("finops")
						.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
					services.AddHttpClient("finops-metadata", client => client.Timeout = Timeout.InfiniteTimeSpan)
						.RemoveAllLoggers()
						.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
						{
							AllowAutoRedirect = false,
							ConnectTimeout = TimeSpan.FromSeconds(60)
						});
					services.AddSingleton(provider => new FoMetadataHttpClient(
						provider.GetRequiredService<IHttpClientFactory>().CreateClient("finops-metadata"),
						provider.GetRequiredService<IConfiguration>()));
					services.AddSingleton<MetadataWindowContext>();
					services.AddSingleton<CsdlSchemaParser>();
					services.AddSingleton<DataEntitiesClient>();
					services.AddSingleton<EntityCatalogCorrelator>();
					services.AddSingleton<IEntityMetadataProvider, EntityMetadataProvider>();
					services.AddSingleton<ISchemaSnapshotRepository, JsonSchemaSnapshotRepository>();
					services.AddSingleton<SchemaComparisonService>();
					services.AddSingleton<MetadataWorkspaceViewModel>();
					services.AddSingleton<ActiveComparisonContext>();
					services.AddSingleton<EntityDataEligibilityService>();
					services.AddSingleton<DataComparisonService>();
					services.AddSingleton<RecordValueCanonicalizer>();
					services.AddSingleton<ODataQueryBuilder>();
					services.AddHttpClient("finops-data", client => client.Timeout = Timeout.InfiniteTimeSpan)
						.RemoveAllLoggers()
						.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(60) });
					services.AddSingleton<IEntityDataReader>(provider => new ODataEntityDataReader(
						provider.GetRequiredService<IHttpClientFactory>().CreateClient("finops-data"),
						provider.GetRequiredService<IAuthenticationService>(), provider.GetRequiredService<IFinOpsHostPolicy>(),
						provider.GetRequiredService<MetadataWindowContext>(), provider.GetRequiredService<ODataQueryBuilder>(),
						provider.GetRequiredService<RecordValueCanonicalizer>()));
					services.AddSingleton<IDataRunStoreFactory, SqliteDataRunStoreFactory>();
					services.AddSingleton<IDataComparisonRunner, DataComparisonRunner>();
					services.AddSingleton<ILegalEntityProvider>(provider => new ODataLegalEntityProvider(
						provider.GetRequiredService<IHttpClientFactory>().CreateClient("finops-data"),
						provider.GetRequiredService<IAuthenticationService>(), provider.GetRequiredService<IFinOpsHostPolicy>(),
						provider.GetRequiredService<MetadataWindowContext>()));
					services.AddSingleton<IDataComparisonSetupProvider, DataComparisonSetupProvider>();
					services.AddSingleton<IDataComparisonProfileRepository, JsonDataComparisonProfileRepository>();
					services.AddSingleton<IDataComparisonInteraction, DataComparisonInteraction>();
					services.AddSingleton<DataComparisonExporter>();
					services.AddSingleton<DataComparisonWorkspaceViewModel>();
					services.AddSingleton<IEnvironmentDiscoveryProvider>(provider => new PowerPlatformEnvironmentDiscoveryProvider(
						provider.GetRequiredService<IHttpClientFactory>().CreateClient("power-platform"),
						provider.GetRequiredService<IAuthenticationService>(),
						provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
						provider.GetRequiredService<ILogger<PowerPlatformEnvironmentDiscoveryProvider>>()));
					services.AddSingleton<IConnectionValidationService>(provider => new FinOpsConnectionValidationService(
						provider.GetRequiredService<IHttpClientFactory>().CreateClient("finops"),
						provider.GetRequiredService<IAuthenticationService>(),
						provider.GetRequiredService<IFinOpsHostPolicy>(),
						provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
					services.AddSingleton<MainViewModel>();
					services.AddTransient<MainWindow>();
				})
				.Build();

			await _host.StartAsync();
			var window = _host.Services.GetRequiredService<MainWindow>();
			MainWindow = window;
			window.Show();
			await window.InitializeAsync();
		}
		catch (Exception exception)
		{
			logger.Error(exception, "The application could not start.");
			MessageBox.Show(
				"The application could not start. Check the local log for details.",
				"D365FO Data Comparison Tool",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			Shutdown(-1);
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_host?.StopAsync().GetAwaiter().GetResult();
		_host?.Dispose();
		base.OnExit(e);
	}
}

