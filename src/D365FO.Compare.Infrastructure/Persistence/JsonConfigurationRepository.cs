using System.Text.Json;
using D365FO.Compare.Application.Configuration;
using D365FO.Compare.Domain.Configuration;
using D365FO.Compare.Domain.Environments;

namespace D365FO.Compare.Infrastructure.Persistence;

public sealed class JsonConfigurationRepository : IConfigurationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _configurationPath;
    private readonly string? _defaultClientId;

    public JsonConfigurationRepository(string? configurationPath = null, string? defaultClientId = null)
    {
        _configurationPath = configurationPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "D365FODataComparisonTool",
            "configuration.json");
        _defaultClientId = Guid.TryParse(defaultClientId, out var clientId) ? clientId.ToString("D") : null;
    }

    public async Task<AppConfiguration?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configurationPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(_configurationPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("schemaVersion", out var versionElement)
            || !versionElement.TryGetInt32(out var schemaVersion))
        {
            throw new InvalidDataException("The saved configuration has no valid schema version.");
        }

        if (schemaVersion is 1 or 2)
        {
            var migrated = schemaVersion == 1
                ? Migrate(document.RootElement.Deserialize<LegacyConfiguration>(JsonOptions)
                    ?? throw new InvalidDataException("The saved configuration is empty."))
                : Migrate(document.RootElement.Deserialize<AppConfiguration>(JsonOptions)
                    ?? throw new InvalidDataException("The saved configuration is empty."));
            await SaveAsync(migrated, cancellationToken).ConfigureAwait(false);
            return migrated;
        }

        if (schemaVersion != 3)
        {
            throw new InvalidDataException($"Unsupported configuration schema version: {schemaVersion}.");
        }

        var configuration = document.RootElement.Deserialize<AppConfiguration>(JsonOptions)
            ?? throw new InvalidDataException("The saved configuration is empty.");
        Validate(configuration);
        return configuration;
    }

    public async Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion != 3)
        {
            throw new InvalidDataException($"Unsupported configuration schema version: {configuration.SchemaVersion}.");
        }

        Validate(configuration);
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

    private AppConfiguration Migrate(LegacyConfiguration legacy)
    {
        var tenantId = legacy.AuthenticationContext?.TenantId
            ?? legacy.SourceEnvironment?.TenantId
            ?? legacy.TargetEnvironment?.TenantId;
        if (tenantId is null)
        {
            return new AppConfiguration();
        }

        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"Tenant {tenantId}",
            TenantId = tenantId,
            ClientId = _defaultClientId,
            AuthenticationContext = legacy.AuthenticationContext,
            SourceEnvironment = legacy.SourceEnvironment,
            TargetEnvironment = legacy.TargetEnvironment
        };
        var migrated = new AppConfiguration
        {
            ActiveProfileId = profile.Id,
            Profiles = [profile]
        };
        Validate(migrated);
        return migrated;
    }

    private AppConfiguration Migrate(AppConfiguration previous)
    {
        var profiles = previous.Profiles.Select(profile => profile with
        {
            ClientId = NormalizeClientId(profile.ClientId) ?? _defaultClientId
        }).ToArray();
        var migrated = new AppConfiguration
        {
            ActiveProfileId = previous.ActiveProfileId,
            Profiles = profiles
        };
        Validate(migrated);
        return migrated;
    }

    private static string? NormalizeClientId(string? clientId) =>
        Guid.TryParse(clientId, out var parsedClientId) ? parsedClientId.ToString("D") : null;

    private static void Validate(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Profiles is null)
        {
            throw new InvalidDataException("The saved configuration has no profile collection.");
        }

        if (configuration.Profiles.Any(profile =>
                profile is null
                || string.IsNullOrWhiteSpace(profile.Id)
                || string.IsNullOrWhiteSpace(profile.Name)
                || !Guid.TryParse(profile.TenantId, out _)
                || profile.ClientId is not null && !Guid.TryParse(profile.ClientId, out _)))
        {
            throw new InvalidDataException("The saved configuration contains an invalid profile.");
        }

        if (configuration.Profiles.Select(profile => profile.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != configuration.Profiles.Count
            || configuration.Profiles.Select(profile => profile.TenantId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != configuration.Profiles.Count)
        {
            throw new InvalidDataException("Profile IDs and tenant IDs must be unique.");
        }

        if (configuration.ActiveProfileId is not null
            && !configuration.Profiles.Any(profile =>
                string.Equals(profile.Id, configuration.ActiveProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The active profile does not exist.");
        }

        foreach (var profile in configuration.Profiles)
        {
            if ((profile.SourceEnvironment is null) != (profile.TargetEnvironment is null))
            {
                throw new InvalidDataException("A profile must contain both environments or neither environment.");
            }

            if (profile.AuthenticationContext is { } authentication
                && !string.Equals(profile.TenantId, authentication.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A profile's authentication context belongs to a different tenant.");
            }

            if (profile.SourceEnvironment is not null && profile.AuthenticationContext is null)
            {
                throw new InvalidDataException("A profile with saved environments must include its authenticated account identity.");
            }

            if ((profile.SourceEnvironment is { } source
                    && !string.Equals(profile.TenantId, source.TenantId, StringComparison.OrdinalIgnoreCase))
                || (profile.TargetEnvironment is { } target
                    && !string.Equals(profile.TenantId, target.TenantId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("A profile environment belongs to a different tenant.");
            }
        }
    }

    private sealed record LegacyConfiguration
    {
        public int SchemaVersion { get; init; }
        public AuthenticationContext? AuthenticationContext { get; init; }
        public EnvironmentConnection? SourceEnvironment { get; init; }
        public EnvironmentConnection? TargetEnvironment { get; init; }
    }
}