using System.Net;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Infrastructure.Metadata;
using D365FO.Compare.Infrastructure.ODataData;
using D365FO.Compare.Infrastructure.Validation;

namespace D365FO.Compare.UnitTests;

public sealed class ODataEntityDataReaderTests
{
    internal static EnvironmentConnection Environment => new() { TenantId = "tenant", ProviderEnvironmentId = "environment", DisplayName = "Example", FinOpsUrl = new Uri("https://source.example") };
    [Fact]
    public async Task ReaderUsesExplicitCompanyAndPagesWithoutTopOrExpand()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            count++;
            if (count == 1)
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                Assert.Contains("Company eq 'IT'", query);
                Assert.Contains("cross-company=true", query);
                Assert.DoesNotContain("$top", query);
                Assert.Contains("$select=Account,Company,Credit,Status", query);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(count == 1
                ? """{"value":[{"Company":"IT","Account":"A","Credit":"1.0","Status":"Open"}],"@odata.nextLink":"?$skiptoken=opaque"}"""
                : """{"value":[]}""") };
        }));
        var reader = Create(http);
        var rows = 0;
        await foreach (var page in reader.ReadPagesAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Prepared, "IT", new(), null, "Source", default)) rows += page.Count;
        Assert.Equal(1, rows);
        Assert.Equal(2, count);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }
    [Fact]
    public async Task ForbiddenStopsWithoutRetries()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.Forbidden); }));
        await Assert.ThrowsAsync<DataAuthorizationException>(async () =>
        {
            await foreach (var page in Create(http).ReadPagesAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Prepared, "IT", new(), null, "Source", default)) { }
        });
        Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData("https://other.example/data/Customers")]
    [InlineData("https://source.example/data/Other")]
    [InlineData("https://source.example/data/Customers?$expand=Orders")]
    public void NextLinkCannotLeaveConfiguredEntity(string link) => Assert.Throws<InvalidDataException>(() => ODataEntityDataReader.ValidateNextLink(
        new Uri("https://source.example"), new Uri("https://source.example/data/Customers"), "/data/Customers", link));

    [Fact]
    public async Task RowLimitWithMorePagesIsNotACompleteRead()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(
            """{"value":[{"Company":"IT","Account":"A","Credit":1,"Status":"Open"}],"@odata.nextLink":"?$skiptoken=more"}""") }));
        var rows = 0;
        await Assert.ThrowsAsync<DataLimitException>(async () =>
        {
            await foreach (var page in Create(http).ReadPagesAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Prepared, "IT",
                new DataReadLimits(MaxRows: 1), null, "Source", default)) rows += page.Count;
        });
        Assert.Equal(1, rows);
    }
    [Fact]
    public void NextLinkCannotBroadenProjectionOrCompanyFilter()
    {
        var origin = new Uri("https://source.example");
        var initial = new ODataQueryBuilder(new()).Build(origin, DataComparisonTests.Prepared, SchemaComparisonTests.Snapshot(), "IT");
        Assert.Throws<InvalidDataException>(() => ODataEntityDataReader.ValidateNextLink(origin, initial, initial.AbsolutePath, "?$select=Secret", initial));
        Assert.Throws<InvalidDataException>(() => ODataEntityDataReader.ValidateNextLink(origin, initial, initial.AbsolutePath, "?$filter=Company%20eq%20'OTHER'", initial));
        Assert.Equal("?$skiptoken=opaque", ODataEntityDataReader.ValidateNextLink(origin, initial, initial.AbsolutePath, "?$skiptoken=opaque", initial).Query);
    }

    private static ODataEntityDataReader Create(HttpClient http)
    {
        var hosts = new ApprovedFinOpsHostPolicy();
        hosts.Approve(Environment.FinOpsUrl);
        var values = new RecordValueCanonicalizer();
        return new(http, new Auth(), hosts, new MetadataWindowContext(), new ODataQueryBuilder(values), values);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class Auth : IAuthenticationService
    {
        public Task<UserSession?> GetCurrentSessionAsync(CancellationToken cancellationToken) => Task.FromResult<UserSession?>(new("tenant", "user@example.com", "account"));
        public Task<string> GetAccessTokenAsync(IReadOnlyCollection<string> scopes, IntPtr parentWindow, CancellationToken cancellationToken) => Task.FromResult("fake");
        public Task<UserSession> SignInAsync(IntPtr parentWindow, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UserSession> ChangeAccountAsync(IntPtr parentWindow, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}