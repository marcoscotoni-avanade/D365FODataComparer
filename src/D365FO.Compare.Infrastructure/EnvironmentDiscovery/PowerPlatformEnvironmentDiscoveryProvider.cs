using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.EnvironmentDiscovery;
using D365FO.Compare.Domain.Authentication;
using D365FO.Compare.Domain.Environments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace D365FO.Compare.Infrastructure.EnvironmentDiscovery;

public sealed class PowerPlatformEnvironmentDiscoveryProvider : IEnvironmentDiscoveryProvider
{
    private static readonly Uri ApiRoot = new("https://api.powerplatform.com/");
    private readonly HttpClient _httpClient;
    private readonly IAuthenticationService _authenticationService;
    private readonly ILogger<PowerPlatformEnvironmentDiscoveryProvider> _logger;
    private readonly string _apiVersion;
    private readonly string _scope;
    private readonly int _maxPages;
    private readonly TimeSpan _requestTimeout;
    private readonly bool _sanitizedDiagnosticsEnabled;

    public PowerPlatformEnvironmentDiscoveryProvider(
        HttpClient httpClient,
        IAuthenticationService authenticationService,
        IConfiguration configuration,
        ILogger<PowerPlatformEnvironmentDiscoveryProvider> logger)
    {
        _httpClient = httpClient;
        _authenticationService = authenticationService;
        _logger = logger;
        _apiVersion = configuration["PowerPlatform:ApiVersion"] ?? "2024-10-01";
        _scope = configuration["PowerPlatform:Scope"] ?? "https://api.powerplatform.com/.default";
        _maxPages = int.TryParse(configuration["PowerPlatform:MaxDiscoveryPages"], out var maxPages)
            ? Math.Clamp(maxPages, 1, 1000)
            : 100;
        var timeoutSeconds = int.TryParse(configuration["PowerPlatform:RequestTimeoutSeconds"], out var parsedTimeout)
            ? Math.Clamp(parsedTimeout, 1, 300)
            : 30;
        _requestTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        _sanitizedDiagnosticsEnabled = bool.TryParse(configuration["Poc:SanitizedDiagnosticsEnabled"], out var enabled)
            && enabled;
    }

    public async Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverAsync(
        UserSession session,
        IntPtr parentWindow,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _authenticationService
            .GetAccessTokenAsync(session.TenantId, [_scope], parentWindow, cancellationToken)
            .ConfigureAwait(false);

        var nextPage = new Uri(
            ApiRoot,
            $"environmentmanagement/environments?api-version={Uri.EscapeDataString(_apiVersion)}");
        var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var environments = new List<DiscoveredEnvironment>();
        var pageCount = 0;

        while (nextPage is not null)
        {
            if (pageCount >= _maxPages)
            {
                throw new InvalidDataException($"Environment discovery exceeded the configured limit of {_maxPages} pages.");
            }

            if (!IsTrustedPage(nextPage) || !visitedPages.Add(nextPage.AbsoluteUri))
            {
                throw new InvalidDataException("The environment discovery API returned an invalid or repeated nextLink.");
            }

            pageCount++;
            using var response = await SendPageWithRetryAsync(nextPage, accessToken, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (_sanitizedDiagnosticsEnabled)
                {
                    _logger.LogWarning(
                        "Power Platform discovery request failed. StatusCode={StatusCode}, Page={PageNumber}.",
                        (int)response.StatusCode,
                        pageCount);
                }
                throw new HttpRequestException(
                    $"Environment discovery failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                    null,
                    response.StatusCode);
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                contentStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = document.RootElement;
            var itemCount = 0;
            if (TryGetProperty(root, "value", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in values.EnumerateArray())
                {
                    itemCount++;
                    var environment = MapEnvironment(item, session.TenantId);
                    if (environment is not null)
                    {
                        environments.Add(environment);
                    }
                }
            }

            if (_sanitizedDiagnosticsEnabled)
            {
                _logger.LogInformation(
                    "Power Platform discovery page completed. Page={PageNumber}, Items={ItemCount}, StatusCode={StatusCode}.",
                    pageCount,
                    itemCount,
                    (int)response.StatusCode);
            }

            nextPage = TryGetString(root, "@odata.nextLink") is { Length: > 0 } nextLink
                ? new Uri(nextPage, nextLink)
                : null;
        }

        if (_sanitizedDiagnosticsEnabled)
        {
            _logger.LogInformation(
                "Power Platform discovery completed. Pages={PageCount}, Environments={EnvironmentCount}, FinOpsEnvironments={FinOpsEnvironmentCount}, ElapsedMilliseconds={ElapsedMilliseconds}.",
                pageCount,
                environments.Count,
                environments.Count(environment => environment.IsSelectableFinOps),
                stopwatch.ElapsedMilliseconds);
        }

        return environments;
    }

    private async Task<HttpResponseMessage> SendPageWithRetryAsync(
        Uri page,
        string accessToken,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, page);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(_requestTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The Power Platform discovery request timed out.");
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 4)
            {
                return response;
            }

            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } retryDate
                    ? retryDate - DateTimeOffset.UtcNow
                    : TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
            response.Dispose();
                var retryDelay = TimeSpan.FromSeconds(Math.Clamp(retryAfter.TotalSeconds, 1, 120));
                if (_sanitizedDiagnosticsEnabled)
                {
                    _logger.LogWarning(
                        "Power Platform discovery throttled. Attempt={Attempt}, RetryDelaySeconds={RetryDelaySeconds}.",
                        attempt + 1,
                        retryDelay.TotalSeconds);
                }
                await Task.Delay(retryDelay, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsTrustedPage(Uri page) =>
        page.IsAbsoluteUri
        && string.Equals(page.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && page.IsDefaultPort
        && string.IsNullOrEmpty(page.UserInfo)
        && string.Equals(page.Host, ApiRoot.Host, StringComparison.OrdinalIgnoreCase);

    private static DiscoveredEnvironment? MapEnvironment(JsonElement item, string sessionTenantId)
    {
        var id = TryGetString(item, "id");
        var displayName = TryGetString(item, "displayName");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var finOps = TryGetProperty(item, "finOpsMetadata", out var finOpsElement)
            ? finOpsElement
            : default;

        return new DiscoveredEnvironment
        {
            ProviderEnvironmentId = id,
            TenantId = TryGetString(item, "tenantId") ?? sessionTenantId,
            DisplayName = displayName,
            PowerPlatformType = TryGetString(item, "type"),
            State = TryGetString(item, "state"),
            Region = TryGetString(item, "geo") ?? TryGetString(item, "azureRegion"),
            DataverseUrl = TryGetHttpsUri(TryGetString(item, "url")),
            FinOpsEnvironmentId = TryGetString(finOps, "id"),
            FinOpsEnvironmentType = TryGetString(finOps, "type"),
            FinOpsUrl = TryGetHttpsUri(TryGetString(finOps, "url"))
        };
    }

    private static Uri? TryGetHttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;

    private static string? TryGetString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }
}