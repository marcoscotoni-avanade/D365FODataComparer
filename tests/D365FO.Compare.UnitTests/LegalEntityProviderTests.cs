using System.Net;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.Metadata;
using D365FO.Compare.Infrastructure.ODataData;
using D365FO.Compare.Infrastructure.Validation;

namespace D365FO.Compare.UnitTests;

public sealed class LegalEntityProviderTests
{
    internal static SchemaSnapshot Schema(bool hasName = true)
    {
        var snapshot = SchemaComparisonTests.Snapshot();
        var template = snapshot.Entities[0].Fields.Single(property => property.Name == "Account");
        var fields = new List<FieldSchema> { template with { Name = "LegalEntityId" } };
        if (hasName) fields.Add(template with { Name = "Name" });
        return snapshot with { Entities = [snapshot.Entities[0] with { EntitySetName = "LegalEntities", Fields = fields }] };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompanyProviderReadsExactEntityAndCompleteIndependentPages(bool hasName)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Equal("/data/LegalEntities", request.RequestUri!.AbsolutePath);
            if (calls == 1)
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                Assert.Contains(hasName ? "$select=LegalEntityId,Name" : "$select=LegalEntityId&", query);
                Assert.Contains("cross-company=true", query);
                Assert.DoesNotContain("$filter", query);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(calls == 1
                ? """{"value":[{"LegalEntityId":"US","Name":"USA"}],"@odata.nextLink":"?$skiptoken=opaque"}"""
                : """{"value":[{"LegalEntityId":"IT","Name":"Italy"}]}""") };
        }));
        var companies = await Create(http).LoadAsync(ODataEntityDataReaderTests.Environment, Schema(hasName), null, "Source", default);
        Assert.Equal(["IT", "US"], companies.Select(company => company.Code));
        Assert.Equal(hasName ? "IT - Italy" : "IT", companies[0].DisplayText);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InvalidCompanyAndMissingSchemaDoNotProducePartialLists()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("""{"value":[{"LegalEntityId":"IT"},{}]}""") }));
        var provider = Create(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.LoadAsync(ODataEntityDataReaderTests.Environment, Schema(), null, "Source", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.LoadAsync(ODataEntityDataReaderTests.Environment, SchemaComparisonTests.Snapshot(), null, "Source", default));
    }

    [Fact]
    public async Task SetupPreservesSideFailureAndStopsAfterAuthenticationFailure()
    {
        var companies = new FakeCompanies(403);
        var provider = new DataComparisonSetupProvider(companies);
        var context = DataComparisonWorkspaceTests.Context().State;
        var result = await provider.LoadAsync(context, null, default);
        Assert.False(result.Source.IsComplete);
        Assert.Equal(403, result.Source.Diagnostic!.HttpStatus);
        Assert.False(result.Source.RequiresAuthentication);
        Assert.True(result.Target.IsComplete);
        Assert.Equal(2, companies.Calls);
        companies = new FakeCompanies(401);
        result = await new DataComparisonSetupProvider(companies).LoadAsync(context, null, default);
        Assert.True(result.Source.RequiresAuthentication);
        Assert.True(result.Target.RequiresAuthentication);
        Assert.Equal(1, companies.Calls);
    }
    private sealed class FakeCompanies(int status) : ILegalEntityProvider
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<D365FO.Compare.Domain.DataComparison.LegalEntityOption>> LoadAsync(
            D365FO.Compare.Domain.Environments.EnvironmentConnection environment, SchemaSnapshot schema,
            IProgress<D365FO.Compare.Domain.DataComparison.DataReadProgress>? progress, string side, CancellationToken cancellationToken)
        {
            Calls++;
            return side == "Source" ? Task.FromException<IReadOnlyList<D365FO.Compare.Domain.DataComparison.LegalEntityOption>>(
                new D365FO.Compare.Infrastructure.FoHttp.MetadataEndpointException("/data/LegalEntities", (HttpStatusCode)status, null))
                : Task.FromResult<IReadOnlyList<D365FO.Compare.Domain.DataComparison.LegalEntityOption>>([new("US", "USA")]);
        }
    }
    private static ODataLegalEntityProvider Create(HttpClient http)
    {
        var hosts = new ApprovedFinOpsHostPolicy(); hosts.Approve(ODataEntityDataReaderTests.Environment.FinOpsUrl);
        return new(http, new Auth(), hosts, new MetadataWindowContext());
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class Auth : IAuthenticationService
    {
        public Task<UserSession?> GetCurrentSessionAsync(string tenantId, string clientId, CancellationToken cancellationToken, string? preferredHomeAccountId = null) => Task.FromResult<UserSession?>(new(tenantId, "user@example.com", "account"));
        public Task<UserSession?> GetCurrentSessionAsync(string tenantId, CancellationToken cancellationToken, string? preferredHomeAccountId = null) => Task.FromResult<UserSession?>(new(tenantId, "user@example.com", "account"));
        public Task<string> GetAccessTokenAsync(string tenantId, IReadOnlyCollection<string> scopes, IntPtr parentWindow, CancellationToken cancellationToken) => Task.FromResult("fake");
        public Task<UserSession> SignInAsync(string tenantId, string clientId, IntPtr parentWindow, CancellationToken cancellationToken, string? preferredHomeAccountId = null) => throw new NotSupportedException();
        public Task<UserSession> ChangeAccountAsync(string tenantId, string clientId, IntPtr parentWindow, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SignOutAsync(string tenantId, string clientId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}