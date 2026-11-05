using System.Net;
using System.Net.Http.Headers;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.EnvironmentDiscovery;
using D365FO.Compare.Application.Validation;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Infrastructure.EnvironmentDiscovery;
using D365FO.Compare.Infrastructure.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace D365FO.Compare.UnitTests;

public sealed class EnvironmentIntegrationTests
{
    private static readonly UserSession Session = new(
        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        "user@contoso.com",
        "opaque-account-id");

    [Fact]
    public async Task DiscoveryFollowsPagesAndMapsFinOpsAndNonFinOpsEnvironments()
    {
        var handler = new QueueHttpMessageHandler(requestNumber => requestNumber == 1
            ? JsonResponse("""
                {
                  "value": [
                    {
                      "id": "pp-source",
                      "displayName": "Source",
                      "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                      "type": "Sandbox",
                      "state": "Ready",
                      "geo": "unitedstates",
                      "url": "https://source.crm.dynamics.com",
                      "finOpsMetadata": {
                        "id": "fo-source",
                        "type": "Sandbox",
                        "url": "https://source.operations.dynamics.com"
                      }
                    }
                  ],
                  "@odata.nextLink": "https://api.powerplatform.com/environmentmanagement/environments?api-version=2024-10-01&$skip=1"
                }
                """ )
            : JsonResponse("""
                {
                  "value": [
                    {
                      "id": "pp-dataverse",
                      "displayName": "Dataverse only",
                      "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                      "type": "Sandbox",
                      "state": "Ready",
                      "geo": "europe"
                    }
                  ]
                }
                """));
        var provider = CreateDiscoveryProvider(handler, new FakeAuthenticationService(Session));

        var environments = await provider.DiscoverAsync(Session, IntPtr.Zero, CancellationToken.None);

        Assert.Equal(2, environments.Count);
        Assert.Equal("fo-source", environments[0].FinOpsEnvironmentId);
        Assert.Equal("https://source.operations.dynamics.com/", environments[0].FinOpsUrl?.AbsoluteUri);
        Assert.True(environments[0].IsSelectableFinOps);
        Assert.False(environments[1].IsSelectableFinOps);
        Assert.Equal("europe", environments[1].Region);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DiscoveryRejectsExternalNextLink()
    {
        var handler = new QueueHttpMessageHandler(_ => JsonResponse("""
            {
              "value": [],
              "@odata.nextLink": "https://attacker.example/collect"
            }
            """));
        var provider = CreateDiscoveryProvider(handler, new FakeAuthenticationService(Session));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.DiscoverAsync(Session, IntPtr.Zero, CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DiscoveryRetriesRateLimitAndUsesRetryAfter()
    {
        var handler = new QueueHttpMessageHandler(requestNumber =>
        {
            if (requestNumber == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            }

            return JsonResponse("{ \"value\": [] }");
        });
        var provider = CreateDiscoveryProvider(handler, new FakeAuthenticationService(Session));

        var environments = await provider.DiscoverAsync(Session, IntPtr.Zero, CancellationToken.None);

        Assert.Empty(environments);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DiscoveryStopsAtConfiguredMaximumPages()
    {
        var handler = new QueueHttpMessageHandler(_ => JsonResponse("""
            {
              "value": [],
              "@odata.nextLink": "https://api.powerplatform.com/environmentmanagement/environments?api-version=2024-10-01&$skip=1"
            }
            """));
        var provider = CreateDiscoveryProvider(handler, new FakeAuthenticationService(Session), maxPages: 1);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.DiscoverAsync(Session, IntPtr.Zero, CancellationToken.None));

        Assert.Contains("limit of 1 pages", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DiscoveryHonorsCallerCancellation()
    {
        var handler = new QueueHttpMessageHandler(_ => JsonResponse("{ \"value\": [] }"));
        var provider = CreateDiscoveryProvider(handler, new FakeAuthenticationService(Session));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.DiscoverAsync(Session, IntPtr.Zero, cancellation.Token));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DiscoveryTimesOutAtConfiguredRequestTimeout()
    {
        var handler = new BlockingHttpMessageHandler();
        var provider = CreateDiscoveryProvider(
            handler,
            new FakeAuthenticationService(Session),
            requestTimeoutSeconds: 1);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            provider.DiscoverAsync(Session, IntPtr.Zero, CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ConnectionTestCallsFinOpsServiceRootWithBearerToken()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new QueueHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, request => capturedRequest = request);
        var authentication = new FakeAuthenticationService(Session);
        var validator = new FinOpsConnectionValidationService(
            new HttpClient(handler),
            authentication,
            CreateFinOpsHostPolicy("source.operations.dynamics.com"));
        var environment = new DiscoveredEnvironment
        {
            ProviderEnvironmentId = "pp-source",
            TenantId = Session.TenantId,
            DisplayName = "Source",
            FinOpsUrl = new Uri("https://source.operations.dynamics.com")
        };

        var result = await validator.ValidateAsync(environment, IntPtr.Zero, CancellationToken.None);

        Assert.Equal(ConnectionValidationStatus.Succeeded, result.Status);
        Assert.Equal(HttpMethod.Get, capturedRequest?.Method);
        Assert.Equal("https://source.operations.dynamics.com/data", capturedRequest?.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("https://source.operations.dynamics.com/.default", Assert.Single(authentication.LastRequestedScopes));
    }

    [Fact]
    public async Task ConnectionTestRejectsDifferentTenantBeforeRequestingToken()
    {
        var handler = new QueueHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var authentication = new FakeAuthenticationService(Session);
        var validator = new FinOpsConnectionValidationService(
            new HttpClient(handler),
            authentication,
            CreateFinOpsHostPolicy("other.operations.dynamics.com"));
        var environment = new DiscoveredEnvironment
        {
            ProviderEnvironmentId = "pp-other-tenant",
            TenantId = "ffffffff-eeee-dddd-cccc-bbbbbbbbbbbb",
            DisplayName = "Other tenant",
            FinOpsUrl = new Uri("https://other.operations.dynamics.com")
        };

        var result = await validator.ValidateAsync(environment, IntPtr.Zero, CancellationToken.None);

        Assert.Equal(ConnectionValidationStatus.RequiresRevalidation, result.Status);
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(authentication.LastRequestedScopes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "token was rejected")]
    [InlineData(HttpStatusCode.Forbidden, "not authorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "throttled")]
    public async Task ConnectionTestExplainsAuthenticationAuthorizationAndThrottling(
        HttpStatusCode statusCode,
        string expectedMessage)
    {
        var handler = new QueueHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
        var validator = new FinOpsConnectionValidationService(
            new HttpClient(handler),
            new FakeAuthenticationService(Session),
            CreateFinOpsHostPolicy("source.operations.dynamics.com"));
        var environment = new DiscoveredEnvironment
        {
            ProviderEnvironmentId = "pp-source",
            TenantId = Session.TenantId,
            DisplayName = "Source",
            FinOpsUrl = new Uri("https://source.operations.dynamics.com")
        };

        var result = await validator.ValidateAsync(environment, IntPtr.Zero, CancellationToken.None);

        Assert.Equal(ConnectionValidationStatus.Failed, result.Status);
        Assert.Contains(expectedMessage, result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionTestReportsTimeoutWithoutTreatingItAsUserCancellation()
    {
        var handler = new QueueHttpMessageHandler(_ => throw new TaskCanceledException("Request timed out."));
        var validator = new FinOpsConnectionValidationService(
            new HttpClient(handler),
            new FakeAuthenticationService(Session),
            CreateFinOpsHostPolicy("source.operations.dynamics.com"));
        var environment = new DiscoveredEnvironment
        {
            ProviderEnvironmentId = "pp-source",
            TenantId = Session.TenantId,
            DisplayName = "Source",
            FinOpsUrl = new Uri("https://source.operations.dynamics.com")
        };

        var result = await validator.ValidateAsync(environment, IntPtr.Zero, CancellationToken.None);

        Assert.Equal(ConnectionValidationStatus.Failed, result.Status);
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionTestRejectsUnapprovedHostBeforeRequestingToken()
    {
        var handler = new QueueHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var authentication = new FakeAuthenticationService(Session);
        var validator = new FinOpsConnectionValidationService(
            new HttpClient(handler),
            authentication,
            CreateFinOpsHostPolicy("source.operations.dynamics.com"));
        var environment = new DiscoveredEnvironment
        {
            ProviderEnvironmentId = "manual:attacker",
            TenantId = Session.TenantId,
            DisplayName = "Unapproved host",
            FinOpsUrl = new Uri("https://attacker.example")
        };

        var result = await validator.ValidateAsync(environment, IntPtr.Zero, CancellationToken.None);

        Assert.Equal(ConnectionValidationStatus.Failed, result.Status);
        Assert.Contains("not been added", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(authentication.LastRequestedScopes);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("https://source.operations.dynamics.com", true)]
    [InlineData("https://sub.source.operations.dynamics.com", false)]
    [InlineData("https://source.operations.dynamics.com:444", false)]
    [InlineData("https://source.operations.dynamics.com/data", false)]
    [InlineData("https://source.operations.dynamics.com/?x=1", false)]
    [InlineData("https://user@source.operations.dynamics.com", false)]
    public void HostPolicyRequiresAnExactApprovedBaseHost(string value, bool expected)
    {
        var policy = CreateFinOpsHostPolicy("source.operations.dynamics.com");

        Assert.Equal(expected, policy.IsAllowed(new Uri(value)));
    }

    private static PowerPlatformEnvironmentDiscoveryProvider CreateDiscoveryProvider(
        HttpMessageHandler handler,
        FakeAuthenticationService authentication,
        int maxPages = 100,
        int requestTimeoutSeconds = 30)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PowerPlatform:ApiVersion"] = "2024-10-01",
                ["PowerPlatform:Scope"] = "https://api.powerplatform.com/.default",
                ["PowerPlatform:MaxDiscoveryPages"] = maxPages.ToString(),
                ["PowerPlatform:RequestTimeoutSeconds"] = requestTimeoutSeconds.ToString()
            })
            .Build();

        return new PowerPlatformEnvironmentDiscoveryProvider(
            new HttpClient(handler),
            authentication,
            configuration,
            NullLogger<PowerPlatformEnvironmentDiscoveryProvider>.Instance);
    }

    private static IFinOpsHostPolicy CreateFinOpsHostPolicy(params string[] approvedHosts)
    {
        var policy = new ApprovedFinOpsHostPolicy();
        foreach (var host in approvedHosts)
        {
            policy.Approve(new Uri($"https://{host}"));
        }

        return policy;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class FakeAuthenticationService(UserSession? session) : IAuthenticationService
    {
        public IReadOnlyCollection<string> LastRequestedScopes { get; private set; } = [];

        public Task<UserSession> SignInAsync(IntPtr parentWindow, CancellationToken cancellationToken) =>
            Task.FromResult(session ?? throw new InvalidOperationException("No test session configured."));

        public Task<UserSession> ChangeAccountAsync(IntPtr parentWindow, CancellationToken cancellationToken) =>
            SignInAsync(parentWindow, cancellationToken);

        public Task<string> GetAccessTokenAsync(
            IReadOnlyCollection<string> scopes,
            IntPtr parentWindow,
            CancellationToken cancellationToken)
        {
            LastRequestedScopes = scopes;
            return Task.FromResult("test-access-token");
        }

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<UserSession?> GetCurrentSessionAsync(CancellationToken cancellationToken) => Task.FromResult(session);
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responseFactory;
        private readonly Action<HttpRequestMessage>? _requestObserver;

        public QueueHttpMessageHandler(
            Func<int, HttpResponseMessage> responseFactory,
            Action<HttpRequestMessage>? requestObserver = null)
        {
            _responseFactory = responseFactory;
            _requestObserver = requestObserver;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            _requestObserver?.Invoke(request);
            return Task.FromResult(_responseFactory(RequestCount));
        }
    }

    private sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}