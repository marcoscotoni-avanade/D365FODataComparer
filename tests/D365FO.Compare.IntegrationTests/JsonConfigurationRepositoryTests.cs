using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Configuration;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Infrastructure.Persistence;

namespace D365FO.Compare.IntegrationTests;

public sealed class JsonConfigurationRepositoryTests
{
    [Fact]
    public async Task RepositoryPersistsAndDeletesVersionedConfiguration()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"d365fo-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(temporaryDirectory, "configuration.json");
        var repository = new JsonConfigurationRepository(path);
        var sourceProfile = new ConnectionProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Contoso",
            TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            ClientId = "11111111-2222-3333-4444-555555555555",
            AuthenticationContext = new AuthenticationContext(
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "user@contoso.com",
                "opaque-account-id"),
            SourceEnvironment = CreateConnection("source", "Source", "https://source.operations.dynamics.com"),
            TargetEnvironment = CreateConnection("target", "Target", "https://target.operations.dynamics.com")
        };
        var otherTenantProfile = new ConnectionProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Fabrikam",
            TenantId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
            ClientId = "22222222-3333-4444-5555-666666666666"
        };
        var configuration = new AppConfiguration
        {
            ActiveProfileId = sourceProfile.Id,
            Profiles = [sourceProfile, otherTenantProfile]
        };

        try
        {
            await repository.SaveAsync(configuration, CancellationToken.None);
            var loaded = await repository.LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Profiles.Count);
            Assert.Equal(sourceProfile, loaded.Profiles[0]);
            Assert.Equal(otherTenantProfile, loaded.Profiles[1]);
            Assert.Equal(sourceProfile.Id, loaded.ActiveProfileId);
            Assert.DoesNotContain("accessToken", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"schemaVersion\": 3", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

            await repository.DeleteAsync(CancellationToken.None);
            Assert.Null(await repository.LoadAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RepositoryMigratesSingleTenantConfigurationToOneProfile()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"d365fo-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(temporaryDirectory, "configuration.json");
        var legacyJson = """
            {
              "schemaVersion": 1,
              "authenticationContext": {
                "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "accountUsername": "user@contoso.com",
                "homeAccountId": "opaque-account-id"
              },
              "sourceEnvironment": {
                "providerEnvironmentId": "source",
                "displayName": "Source",
                "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "finOpsUrl": "https://source.operations.dynamics.com/",
                "validationStatus": 1
              },
              "targetEnvironment": {
                "providerEnvironmentId": "target",
                "displayName": "Target",
                "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "finOpsUrl": "https://target.operations.dynamics.com/",
                "validationStatus": 1
              }
            }
            """;

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            await File.WriteAllTextAsync(path, legacyJson);
            var repository = new JsonConfigurationRepository(path, "11111111-2222-3333-4444-555555555555");

            var migrated = await repository.LoadAsync(CancellationToken.None);
            Assert.NotNull(migrated);
            var profile = Assert.Single(migrated!.Profiles);

            Assert.Equal(3, migrated.SchemaVersion);
            Assert.Equal(profile.Id, migrated.ActiveProfileId);
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", profile.TenantId);
            Assert.Equal("11111111-2222-3333-4444-555555555555", profile.ClientId);
            Assert.Equal("user@contoso.com", profile.AuthenticationContext?.AccountUsername);
            Assert.Equal("source", profile.SourceEnvironment?.ProviderEnvironmentId);
            Assert.Equal("target", profile.TargetEnvironment?.ProviderEnvironmentId);
            Assert.Contains("\"schemaVersion\": 3", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RepositoryMigratesVersionTwoProfilesWithDefaultClientId()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"d365fo-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(temporaryDirectory, "configuration.json");
        const string tenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string defaultClientId = "11111111-2222-3333-4444-555555555555";
        var versionTwoJson = $$"""
            {
              "schemaVersion": 2,
              "activeProfileId": "profile-id",
              "profiles": [
                {
                  "id": "profile-id",
                  "name": "Contoso",
                  "tenantId": "{{tenantId}}",
                  "authenticationContext": null,
                  "sourceEnvironment": null,
                  "targetEnvironment": null
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            await File.WriteAllTextAsync(path, versionTwoJson);
            var repository = new JsonConfigurationRepository(path, defaultClientId);

            var migrated = await repository.LoadAsync(CancellationToken.None);

            Assert.NotNull(migrated);
            Assert.Equal(3, migrated.SchemaVersion);
            Assert.Equal(defaultClientId, Assert.Single(migrated.Profiles).ClientId);
            Assert.Contains("\"schemaVersion\": 3", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RepositoryRejectsMoreThanOneProfileForTheSameTenant()
    {
        var tenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var configuration = new AppConfiguration
        {
            Profiles =
            [
                new ConnectionProfile { Id = "one", Name = "First", TenantId = tenantId },
                new ConnectionProfile { Id = "two", Name = "Second", TenantId = tenantId }
            ]
        };
        var repository = new JsonConfigurationRepository(Path.Combine(Path.GetTempPath(), $"d365fo-{Guid.NewGuid():N}.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(configuration, CancellationToken.None));
    }

    private static EnvironmentConnection CreateConnection(string id, string name, string url) => new()
    {
        ProviderEnvironmentId = id,
        DisplayName = name,
        TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        FinOpsUrl = new Uri(url),
        ValidationStatus = ConnectionValidationStatus.Succeeded
    };
}