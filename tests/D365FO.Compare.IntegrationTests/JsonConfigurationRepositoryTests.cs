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
        var configuration = new AppConfiguration
        {
            AuthenticationContext = new AuthenticationContext(
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "user@contoso.com",
                "opaque-account-id"),
            SourceEnvironment = CreateConnection("source", "Source", "https://source.operations.dynamics.com"),
            TargetEnvironment = CreateConnection("target", "Target", "https://target.operations.dynamics.com")
        };

        try
        {
            await repository.SaveAsync(configuration, CancellationToken.None);
            var loaded = await repository.LoadAsync(CancellationToken.None);

            Assert.Equal(configuration, loaded);
            Assert.DoesNotContain("accessToken", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);

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

    private static EnvironmentConnection CreateConnection(string id, string name, string url) => new()
    {
        ProviderEnvironmentId = id,
        DisplayName = name,
        TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        FinOpsUrl = new Uri(url),
        ValidationStatus = ConnectionValidationStatus.Succeeded
    };
}