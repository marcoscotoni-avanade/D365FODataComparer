using System.Text.Json;
using D365FO.Compare.Application.Configuration;
using D365FO.Compare.Domain.Configuration;

namespace D365FO.Compare.Infrastructure.Persistence;

public sealed class JsonGeneralPreferencesRepository : IGeneralPreferencesRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _preferencesPath;

    public JsonGeneralPreferencesRepository(string? preferencesPath = null)
    {
        _preferencesPath = preferencesPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "D365FODataComparisonTool",
            "general-preferences.json");
    }

    public async Task<GeneralPreferences> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_preferencesPath)) return GeneralPreferences.Default;

        await using var stream = new FileStream(_preferencesPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var preferences = await JsonSerializer.DeserializeAsync<GeneralPreferences>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The general preferences file is empty.");
        if (!preferences.IsValid) throw new InvalidDataException("The saved count concurrency must be between 1 and 8.");
        return preferences;
    }

    public async Task SaveAsync(GeneralPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!preferences.IsValid) throw new InvalidDataException("The count concurrency must be between 1 and 8.");

        var directory = Path.GetDirectoryName(_preferencesPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_preferencesPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _preferencesPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}