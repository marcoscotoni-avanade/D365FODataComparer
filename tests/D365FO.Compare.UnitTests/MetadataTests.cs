using System.Text;
using System.Net;
using D365FO.Compare.Infrastructure.FoHttp;
using D365FO.Compare.Infrastructure.Metadata;
using D365FO.Compare.Infrastructure.RestMetadata;
using D365FO.Compare.Infrastructure.Persistence;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Infrastructure.Validation;
using Microsoft.Extensions.Configuration;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.ODataEdm;

namespace D365FO.Compare.UnitTests;

public sealed class MetadataTests
{
    internal const string Csdl = """
        <edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">
          <edmx:DataServices><Schema Namespace="Example" Alias="Ex" xmlns="http://docs.oasis-open.org/odata/ns/edm">
            <EnumType Name="Status"><Member Name="Open" Value="0"/><Member Name="Closed" Value="1"/></EnumType>
            <EntityType Name="Customer">
              <Key><PropertyRef Name="Company"/><PropertyRef Name="Account"/></Key>
              <Property Name="Company" Type="Edm.String" Nullable="false" MaxLength="4"/>
              <Property Name="Account" Type="Edm.String" Nullable="false" MaxLength="20"/>
              <Property Name="Credit" Type="Edm.Decimal" Precision="18" Scale="2"/>
              <Property Name="Status" Type="Ex.Status"/>
            </EntityType>
            <EntityContainer Name="Resources">
              <EntitySet Name="Customers" EntityType="Ex.Customer"/>
              <EntitySet Name="OtherCustomers" EntityType="Ex.Customer"/>
            </EntityContainer>
          </Schema></edmx:DataServices>
        </edmx:Edmx>
        """;

    internal static ParsedSchema Parse(string csdl = Csdl)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csdl));
        return new CsdlSchemaParser().Parse(stream);
    }

    [Fact]
    public void ParserPreservesSetsKeyOrderEnumAndMissingFacets()
    {
        var parsed = Parse();
        Assert.Equal(2, parsed.Entities.Count);
        var entity = parsed.Entities[0];
        Assert.Equal("Example.Customer", entity.EntityTypeFullName);
        Assert.Equal(["Company", "Account"], entity.Key.Select(field => field.Name));
        var credit = Assert.Single(entity.Fields, field => field.Name == "Credit");
        Assert.Equal(ObservationState.Missing, credit.Nullable.State);
        Assert.Equal("true", credit.Nullable.EffectiveValue);
        Assert.Null(credit.Nullable.Value);
        Assert.Equal("18", credit.Precision.Value);
        Assert.Equal("2", credit.Scale.Value);
        Assert.Equal(ObservationState.Missing, credit.MaxLength.State);
        Assert.Equal("Example.Status", Assert.Single(entity.Fields, field => field.Name == "Status").EnumTypeFullName);
        Assert.Equal(2, Assert.Single(parsed.Enums).Members.Count);
    }

    [Fact]
    public void ParserRejectsDtd()
    {
        Assert.Throws<System.Xml.XmlException>(() => Parse("<!DOCTYPE foo [<!ENTITY xxe SYSTEM 'file:///secret'>]><foo>&xxe;</foo>"));
    }

    [Fact]
    public void ParserHonorsCancellation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Csdl));
        Assert.Throws<OperationCanceledException>(() => new CsdlSchemaParser().Parse(stream, new CancellationToken(true)));
    }

      [Fact]
      public async Task MetadataClientRemovesShortTimeoutAndWaitsForHeaders()
      {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, ct) => response.Task.WaitAsync(ct))) { Timeout = TimeSpan.FromMilliseconds(1) };
        var client = new FoMetadataHttpClient(http);
        var download = client.DownloadAsync(new Uri("https://source.example/data/$metadata"), "not-a-real-token", "/data/$metadata", "application/xml", null, default);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
        Assert.False(download.IsCompleted);
        response.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Csdl) });
        await using var file = await download;
        Assert.True(file.Length > 0);
        Assert.Equal(2, new CsdlSchemaParser().Parse(file).Entities.Count);
      }

      [Fact]
      public async Task MetadataClientCancelsWhileWaitingForHeaders()
      {
        using var cancellation = new CancellationTokenSource();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, ct) => response.Task.WaitAsync(ct)));
        var download = new FoMetadataHttpClient(http).DownloadAsync(new Uri("https://source.example/data/$metadata"), "fake", "metadata", "application/xml", null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
      }

      [Fact]
      public async Task MetadataClientCancelsDuringBodyAndDeletesTemporaryFile()
      {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StreamContent(new WaitingStream(started))
        })));
        var download = new FoMetadataHttpClient(http).DownloadAsync(new Uri("https://source.example/data/$metadata"), "fake", "metadata", "application/xml", null, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
      }

      [Theory]
      [InlineData(401)]
      [InlineData(403)]
      [InlineData(404)]
      public async Task MetadataClientDoesNotRetryPermanentErrors(int status)
      {
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
          requests++;
          return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        }));
        var error = await Assert.ThrowsAsync<MetadataEndpointException>(() => new FoMetadataHttpClient(http)
          .DownloadAsync(new Uri("https://source.example/Metadata/DataEntities"), "fake", "DataEntities", "application/json", null, default));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, requests);
      }

      [Fact]
      public async Task CatalogFollowsRelativePagesAndPreservesCanonicalIdentity()
      {
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent(++requests == 1
            ? """{"value":[{"Name":"CustomerEntity","PublicEntityName":"Customer","PublicCollectionName":"Customers","FutureProperty":42}],"@odata.nextLink":"?$skip=1"}"""
            : """{"value":[]}""")
        })));
        var catalog = await new DataEntitiesClient(new FoMetadataHttpClient(http)).LoadAsync(new Uri("https://source.example"), "fake", null, default);
        Assert.Equal("CustomerEntity", Assert.Single(catalog).Name);
        Assert.Equal(2, requests);
      }

      [Theory]
      [InlineData("https://other.example/Metadata/DataEntities")]
      [InlineData("http://source.example/Metadata/DataEntities")]
      [InlineData("https://source.example/data/Customers")]
      [InlineData("https://user@source.example/Metadata/DataEntities")]
      public void CatalogRejectsUntrustedNextLink(string link)
      {
        var origin = new Uri("https://source.example");
        Assert.Throws<InvalidDataException>(() => DataEntitiesClient.ValidateNextLink(origin, new Uri(origin, "/Metadata/DataEntities"), link));
      }

      [Fact]
      public void CorrelatorOnlyAssignsExactUniqueIdentity()
      {
        var entities = Parse().Entities;
        var correlator = new EntityCatalogCorrelator();
        var exact = correlator.Correlate(entities, [new("CustomerEntity", "Customer", "Customers", null)]);
        Assert.Equal(CorrelationStatus.Exact, exact.Entities[0].Correlation);
        Assert.Equal("CustomerEntity", exact.Entities[0].FoEntityName);
        Assert.Null(exact.Entities[1].FoEntityName);
        var ambiguous = correlator.Correlate(entities, [new("First", "Customer", "Customers", null), new("Second", "Customer", "Customers", null)]);
        Assert.Equal(CorrelationStatus.Ambiguous, ambiguous.Entities[0].Correlation);
        Assert.Null(ambiguous.Entities[0].FoEntityName);
        Assert.Equal(entities.Count, ambiguous.Entities.Count);
      }

      [Fact]
      public async Task SnapshotCacheRoundTripsAndIsolatesAccountAndOrigin()
      {
        var directory = Path.Combine(Path.GetTempPath(), "schema-tests-" + Guid.NewGuid().ToString("N"));
        var snapshot = SchemaComparisonTests.Snapshot();
        snapshot = snapshot with { Fingerprint = SchemaFingerprint.Compute(snapshot) };
        var environment = new EnvironmentConnection { TenantId = snapshot.TenantId, ProviderEnvironmentId = snapshot.EnvironmentId,
          DisplayName = snapshot.EnvironmentName, FinOpsUrl = new Uri(snapshot.EnvironmentOrigin) };
        var repository = new JsonSchemaSnapshotRepository(directory);
        try
        {
          await repository.SaveAsync(snapshot, default);
          var loaded = await repository.LoadAsync(environment, snapshot.AccountId, default);
          Assert.Equal(snapshot.Fingerprint, loaded?.Fingerprint);
          Assert.Equal(2, loaded?.Entities.Count);
          Assert.Null(await repository.LoadAsync(environment, "another-account", default));
          Assert.Null(await repository.LoadAsync(environment with { FinOpsUrl = new Uri("https://other.example") }, snapshot.AccountId, default));
          using var canceled = new CancellationTokenSource();
          canceled.Cancel();
          await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SaveAsync(snapshot, canceled.Token));
          Assert.NotNull(await repository.LoadAsync(environment, snapshot.AccountId, default));
          await repository.DeleteAsync(environment, snapshot.AccountId, default);
          Assert.Null(await repository.LoadAsync(environment, snapshot.AccountId, default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
      }

      [Fact]
      public async Task SnapshotCacheDeleteIsIdempotentWhenDirectoryDoesNotExist()
      {
        var directory = Path.Combine(Path.GetTempPath(), "missing-schema-cache-" + Guid.NewGuid().ToString("N"));
        var repository = new JsonSchemaSnapshotRepository(directory);
        var environment = new EnvironmentConnection { TenantId = "tenant", ProviderEnvironmentId = "source",
          DisplayName = "Source", FinOpsUrl = new Uri("https://source.example") };

        await repository.DeleteAsync(environment, "account", default);
        await repository.DeleteAsync(environment, "account", default);

        Assert.False(Directory.Exists(directory));
        Assert.Null(await repository.LoadAsync(environment, "account", default));
      }

      [Fact]
      public async Task ProviderRequiresBothEndpointsBeforePublishingSnapshot()
      {
        var catalogFails = true;
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
          requests.Add(request.RequestUri!.AbsolutePath);
          return Task.FromResult(request.RequestUri.AbsolutePath == "/data/$metadata"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Csdl) }
            : catalogFails ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[{"Name":"CustomerEntity","PublicEntityName":"Customer","PublicCollectionName":"Customers"}]}""") });
        }));
        var transport = new FoMetadataHttpClient(http);
        var hosts = new ApprovedFinOpsHostPolicy();
        var environment = new EnvironmentConnection { TenantId = "tenant", ProviderEnvironmentId = "source",
          DisplayName = "Source", FinOpsUrl = new Uri("https://source.example") };
        hosts.Approve(environment.FinOpsUrl);
        var auth = new Authentication();
        var provider = new EntityMetadataProvider(transport, new DataEntitiesClient(transport), new CsdlSchemaParser(),
          new EntityCatalogCorrelator(), auth, hosts, new MetadataWindowContext());
        await Assert.ThrowsAsync<MetadataEndpointException>(() => provider.CreateSnapshotAsync(environment, null, default));
        Assert.Equal(["/data/$metadata", "/Metadata/DataEntities"], requests);
        catalogFails = false;
        var snapshot = await provider.CreateSnapshotAsync(environment, null, default);
        Assert.Equal("CustomerEntity", snapshot.Entities[0].FoEntityName);
        Assert.NotEmpty(snapshot.Fingerprint);
        var tokenCalls = auth.TokenCalls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateSnapshotAsync(environment with { TenantId = "other" }, null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateSnapshotAsync(environment with { FinOpsUrl = new Uri("https://unapproved.example") }, null, default));
        Assert.Equal(tokenCalls, auth.TokenCalls);
      }

      [Theory]
      [InlineData(429)]
      [InlineData(503)]
      public async Task MetadataRetryIsBounded(int status)
      {
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
          requests++;
          var response = new HttpResponseMessage((HttpStatusCode)status);
          response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
          return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<MetadataEndpointException>(() => new FoMetadataHttpClient(http)
          .DownloadAsync(new Uri("https://source.example/data/$metadata"), "fake", "metadata", "application/xml", null, default));
        Assert.Equal(3, requests);
      }

      [Fact]
      public async Task MetadataRejectsPayloadAndPageLimits()
      {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent("""{"value":[],"@odata.nextLink":"?$skip=1"}""")
        })));
        var small = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Metadata:MaxPayloadBytes"] = "4" }).Build();
        await Assert.ThrowsAsync<InvalidDataException>(() => new FoMetadataHttpClient(http, small)
          .DownloadAsync(new Uri("https://source.example/data/$metadata"), "fake", "metadata", "application/xml", null, default));
        using var otherHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent("""{"value":[],"@odata.nextLink":"?$skip=1"}""")
        })));
        var onePage = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Metadata:MaxCatalogPages"] = "1" }).Build();
        await Assert.ThrowsAsync<InvalidDataException>(() => new DataEntitiesClient(new FoMetadataHttpClient(otherHttp), onePage)
          .LoadAsync(new Uri("https://source.example"), "fake", null, default));
      }

      [Fact]
      public void ParserIncludesInheritedFieldsComplexTypesNavigationAndAnnotations()
      {
        var parsed = Parse("""
          <edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx"><edmx:DataServices>
          <Schema Namespace="Example" Alias="Ex" xmlns="http://docs.oasis-open.org/odata/ns/edm">
            <ComplexType Name="Address"><Property Name="City" Type="Edm.String" MaxLength="max" /></ComplexType>
            <EntityType Name="Base"><Key><PropertyRef Name="Id" /></Key><Property Name="Id" Type="Edm.String" Nullable="false" /></EntityType>
            <EntityType Name="Derived" BaseType="Ex.Base">
            <Property Name="Address" Type="Ex.Address" />
            <Property Name="Amounts" Type="Collection(Edm.Decimal)" Scale="variable" />
            <NavigationProperty Name="Related" Type="Collection(Ex.Base)" />
            <Annotation Term="Example.Note" String="inline" />
            </EntityType>
            <Annotations Target="Ex.Derived/Address"><Annotation Term="Example.Note" String="external" /></Annotations>
            <EntityContainer Name="Resources"><EntitySet Name="DerivedEntities" EntityType="Ex.Derived" /></EntityContainer>
          </Schema></edmx:DataServices></edmx:Edmx>
          """);
        var entity = Assert.Single(parsed.Entities);
        Assert.Equal("Id", Assert.Single(entity.Key).Name);
        Assert.Contains(entity.Fields, property => property.Name == "Id");
        Assert.Equal("Example.Address", Assert.Single(entity.Fields, property => property.Name == "Address").TypeFullName);
        Assert.Single(Assert.Single(entity.Fields, property => property.Name == "Address").Annotations);
        var amounts = Assert.Single(entity.Fields, property => property.Name == "Amounts");
        Assert.True(amounts.IsCollection);
        Assert.Equal("variable", amounts.Scale.Value);
        Assert.Single(entity.Navigations);
        Assert.Single(entity.Annotations);
        Assert.Equal("max", Assert.Single(Assert.Single(parsed.ComplexTypes).Fields).MaxLength.Value);
      }

      [Fact]
      public async Task ProviderRejectsAccountSwitchDuringTokenAcquisitionBeforeHttp()
      {
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
          requests++;
          return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var transport = new FoMetadataHttpClient(http);
        var hosts = new ApprovedFinOpsHostPolicy();
        var environment = new EnvironmentConnection { TenantId = "tenant", ProviderEnvironmentId = "source",
          DisplayName = "Source", FinOpsUrl = new Uri("https://source.example") };
        hosts.Approve(environment.FinOpsUrl);
        var provider = new EntityMetadataProvider(transport, new DataEntitiesClient(transport), new CsdlSchemaParser(),
          new EntityCatalogCorrelator(), new Authentication { SwitchAccountOnToken = true }, hosts, new MetadataWindowContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateSnapshotAsync(environment, null, default));
        Assert.Equal(0, requests);
      }

      private sealed class Authentication : IAuthenticationService
      {
        public int TokenCalls { get; private set; }
        public bool SwitchAccountOnToken { get; init; }
        public Task<UserSession?> GetCurrentSessionAsync(string tenantId, string clientId, CancellationToken cancellationToken, string? preferredHomeAccountId = null) => Task.FromResult<UserSession?>(
          new("tenant", "user@example.com", SwitchAccountOnToken && TokenCalls > 0 ? "another-account" : "account"));
        public Task<UserSession?> GetCurrentSessionAsync(string tenantId, CancellationToken cancellationToken, string? preferredHomeAccountId = null) =>
          Task.FromResult<UserSession?>(new("tenant", "user@example.com", SwitchAccountOnToken && TokenCalls > 0 ? "another-account" : "account"));
        public Task<string> GetAccessTokenAsync(string tenantId, IReadOnlyCollection<string> scopes, IntPtr parentWindow, CancellationToken cancellationToken)
        {
          TokenCalls++;
          Assert.Equal("https://source.example/.default", Assert.Single(scopes));
          return Task.FromResult("fake");
        }
        public Task<UserSession> SignInAsync(string tenantId, string clientId, IntPtr parentWindow, CancellationToken cancellationToken, string? preferredHomeAccountId = null) => throw new NotSupportedException();
        public Task<UserSession> ChangeAccountAsync(string tenantId, string clientId, IntPtr parentWindow, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SignOutAsync(string tenantId, string clientId, CancellationToken cancellationToken) => throw new NotSupportedException();
      }

      private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
      {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
      }

      private sealed class WaitingStream(TaskCompletionSource started) : Stream
      {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
          started.TrySetResult();
          await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
          return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
      }
}