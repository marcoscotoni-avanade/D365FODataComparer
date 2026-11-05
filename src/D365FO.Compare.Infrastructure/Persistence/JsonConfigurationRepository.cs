using System.Text.Json;
using D365FO.Compare.Application.Configuration;
using D365FO.Compare.Domain.Configuration;

namespace D365FO.Compare.Infrastructure.Persistence;

public sealed class JsonConfigurationRepository : IConfigurationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _configurationPath;

    public JsonConfigurationRepository(string? configurationPath = null)
    {
        _configurationPath = configurationPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "D365FODataComparisonTool",
            "configuration.json");
    }

    public async Task<AppConfiguration?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configurationPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            _configurationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var configuration = await JsonSerializer.DeserializeAsync<AppConfiguration>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (configuration is not null && configuration.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported configuration schema version: {configuration.SchemaVersion}.");
        }

        return configuration;
    }

    public async Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported configuration schema version: {configuration.SchemaVersion}.");
        }

        var directory = Path.GetDirectoryName(_configurationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_configurationPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _configurationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_configurationPath))
        {
            File.Delete(_configurationPath);
        }

        return Task.CompletedTask;
    }
}