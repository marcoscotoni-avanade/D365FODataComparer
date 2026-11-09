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
                Assert.DoesNotContain("$select", query);
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
        var error = await Assert.ThrowsAsync<DataReadDiagnosticException>(async () =>
        {
            await foreach (var page in Create(http).ReadPagesAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Prepared, "IT", new(), null, "Source", default)) { }
        });
        Assert.True(error.IsAuthorizationFailure);
        Assert.IsType<DataAuthorizationException>(error.InnerException);
        Assert.Equal(403, error.Diagnostic.HttpStatus);
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
        var error = await Assert.ThrowsAsync<DataReadDiagnosticException>(async () =>
        {
            await foreach (var page in Create(http).ReadPagesAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Prepared, "IT",
                new DataReadLimits(MaxRows: 1), null, "Source", default)) rows += page.Count;
        });
        Assert.True(error.IsLimitFailure);
        Assert.IsType<DataLimitException>(error.InnerException);
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

    [Fact]
    public void CountQueryUsesCompanyAndTypedFiltersWithoutComparisonEligibility()
    {
        var schema = SchemaComparisonTests.Snapshot();
        var plan = DataComparisonTests.Plan with
        {
            Scope = DataScopeKind.Company,
            CompanyField = "Company",
            Filters = [new("Account", "eq", "\"A\"")]
        };

        var uri = new ODataQueryBuilder(new()).BuildCount(new Uri("https://source.example"), plan, schema, "IT");
        var query = Uri.UnescapeDataString(uri.Query);

        Assert.Equal("/data/Customers", uri.AbsolutePath);
        Assert.Contains("$count=true", query);
        Assert.Contains("$top=0", query);
        Assert.Contains("cross-company=true", query);
        Assert.Contains("Company eq 'IT'", query);
        Assert.Contains("Account eq 'A'", query);
        Assert.DoesNotContain("$select", query);
    }

    [Fact]
    public async Task CountReaderSendsZeroTopQueryAndParsesODataCountAnnotation()
    {
        var plan = DataComparisonTests.Plan with
        {
            Scope = DataScopeKind.Company,
            CompanyField = "Company",
            Filters = [new("Account", "eq", "\"A\"")]
        };
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/data/Customers", request.RequestUri!.AbsolutePath);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            Assert.Contains("$count=true", query);
            Assert.Contains("$top=0", query);
            Assert.Contains("Company eq 'IT'", query);
            Assert.Contains("Account eq 'A'", query);
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"@odata.count":42,"value":[]}""", System.Text.Encoding.UTF8, "application/json") };
        }));

        var result = await CreateCount(http).CountAsync(Environment, SchemaComparisonTests.Snapshot(), plan, "IT", "Source", default);

        Assert.Equal(42, result.Count);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task CountReaderKeepsHttpFailureDistinctFromZero()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.NotFound); }));
        var result = await CreateCount(http).CountAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Plan, "IT", "Target", default);

        Assert.Null(result.Count);
        Assert.Equal(404, result.Diagnostic!.HttpStatus);
        Assert.Equal("Target", result.Diagnostic.Side);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"@odata.count\":-1,\"value\":[]}")]
    [InlineData("{\"@odata.count\":\"42\",\"value\":[]}")]
    [InlineData("{\"@odata.count\":42,\"value\":[{}]}")]
    public async Task CountReaderRejectsInvalidCountPayload(string body)
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        }));
        var result = await CreateCount(http).CountAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Plan, "", "Source", default);
        Assert.Null(result.Count);
        Assert.NotNull(result.Diagnostic);
    }

    [Fact]
    public async Task HttpErrorDiagnosticsPreserveStatusRequestIdAndSafeServerMessage()
    {
        const string requestId = "ec59282d-7160-4e51-a854-e7d837c778f8";
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":"BadRequest","message":"Property 'Account' failed for 'IT' and 'private-customer' at https://source.example/data/Customers?secret=1 Bearer TOKEN-SECRET"},"value":[{"Secret":"do-not-retain"}]}""")
            };
            response.Headers.Add("x-ms-request-id", requestId);
            return response;
        }));
        var error = await Assert.ThrowsAsync<DataReadDiagnosticException>(async () =>
        {
            await foreach (var page in Create(http).ReadPagesAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Prepared,
                "IT", new(), null, "Source", default)) { }
        });
        Assert.Equal(400, error.Diagnostic.HttpStatus);
        Assert.Equal(requestId, error.Diagnostic.RequestId);
        Assert.Equal("BadRequest", error.Diagnostic.ServerErrorCode);
        Assert.Equal("Source", error.Diagnostic.Side);
        Assert.Equal(1, error.Diagnostic.Page);
        Assert.Equal("Reading HTTP error response", error.Diagnostic.Stage);
        Assert.Contains("Property 'Account'", error.Diagnostic.Reason);
        var serialized = System.Text.Json.JsonSerializer.Serialize(error.Diagnostic);
        Assert.DoesNotContain("TOKEN-SECRET", serialized);
        Assert.DoesNotContain("private-customer", serialized);
        Assert.DoesNotContain("do-not-retain", serialized);
        Assert.DoesNotContain("source.example", serialized);
        Assert.DoesNotContain("'IT'", error.Diagnostic.Reason);
    }

    [Fact]
    public async Task InvalidJsonAndNextLinkFailuresHaveDistinctStages()
    {
        using var invalidJson = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"value\":[") }));
        var parseError = await Assert.ThrowsAsync<DataReadDiagnosticException>(async () =>
        {
            await foreach (var page in Create(invalidJson).ReadPagesAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Prepared,
                "IT", new(), null, "Source", default)) { }
        });
        Assert.Equal("Parsing JSON response", parseError.Diagnostic.Stage);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(parseError.InnerException);
        Assert.Equal(parseError.InnerException!.GetType().Name, parseError.Diagnostic.ExceptionType);
        Assert.NotNull(parseError.Diagnostic.BytePosition);

        using var badNextLink = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(
            """{"value":[{"Company":"IT","Account":"A","Credit":1,"Status":"Open"}],"@odata.nextLink":"https://other.example/data/Customers"}""") }));
        var received = 0;
        var pagingError = await Assert.ThrowsAsync<DataReadDiagnosticException>(async () =>
        {
            await foreach (var page in Create(badNextLink).ReadPagesAsync(Environment, SchemaComparisonTests.Snapshot(), DataComparisonTests.Prepared,
                "IT", new(), null, "Source", default)) received += page.Count;
        });
        Assert.Equal(0, received);
        Assert.Equal("Validating nextLink", pagingError.Diagnostic.Stage);
        Assert.Equal(200, pagingError.Diagnostic.HttpStatus);
        Assert.Equal("InvalidDataException", pagingError.Diagnostic.ExceptionType);
    }

    [Fact]
    public void FullPagingAcceptsCompleteProjectionButExplicitPagingCannotBroadenIt()
    {
        var snapshot = SchemaComparisonTests.Snapshot();
        var fields = snapshot.Entities[0].Fields;
        var builder = new ODataQueryBuilder(new());
        var origin = new Uri("https://source.example");
        var full = builder.Build(origin, DataComparisonTests.Prepared, snapshot, "IT");
        var names = fields.Select(property => property.Name).ToArray();
        Assert.NotNull(ODataEntityDataReader.ValidateNextLink(origin, full, full.AbsolutePath, "?$select=" + string.Join(',', names), full, names));
        Assert.NotNull(ODataEntityDataReader.ValidateNextLink(origin, full, full.AbsolutePath, "?$select=*", full, names));
        Assert.Throws<InvalidDataException>(() => ODataEntityDataReader.ValidateNextLink(origin, full, full.AbsolutePath, "?$select=Credit", full, names));
        var plan = DataComparisonTests.Plan with { AvailableFields = fields.Select(property => new AvailableFieldSelection(property, property.Name == "Credit")).ToArray() };
        var prepared = new EntityDataEligibilityService().Prepare(snapshot, snapshot, plan);
        var selected = builder.Build(origin, prepared, snapshot, "IT");
        Assert.Throws<InvalidDataException>(() => ODataEntityDataReader.ValidateNextLink(origin, selected, selected.AbsolutePath, "?$select=*", selected, names));
    }

    private static ODataEntityDataReader Create(HttpClient http)
    {
        var hosts = new ApprovedFinOpsHostPolicy();
        hosts.Approve(Environment.FinOpsUrl);
        var values = new RecordValueCanonicalizer();
        return new(http, new Auth(), hosts, new MetadataWindowContext(), new ODataQueryBuilder(values), values);
    }
    private static ODataEntityCountReader CreateCount(HttpClient http)
    {
        var hosts = new ApprovedFinOpsHostPolicy();
        hosts.Approve(Environment.FinOpsUrl);
        return new(http, new Auth(), hosts, new MetadataWindowContext(), new ODataQueryBuilder(new RecordValueCanonicalizer()));
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