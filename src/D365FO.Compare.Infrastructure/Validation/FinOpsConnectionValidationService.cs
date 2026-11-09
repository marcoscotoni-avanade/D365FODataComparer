using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.Validation;
using D365FO.Compare.Domain.Environments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace D365FO.Compare.Infrastructure.Validation;

public sealed class FinOpsConnectionValidationService : IConnectionValidationService
{
    private readonly HttpClient _httpClient;
    private readonly IAuthenticationService _authenticationService;
    private readonly IFinOpsHostPolicy _hostPolicy;
    private readonly TimeSpan _requestTimeout;
    private readonly bool _sanitizedDiagnosticsEnabled;
    private readonly ILogger<FinOpsConnectionValidationService> _logger;

    public FinOpsConnectionValidationService(
        HttpClient httpClient,
        IAuthenticationService authenticationService,
        IFinOpsHostPolicy hostPolicy,
        IConfiguration? configuration = null,
        ILogger<FinOpsConnectionValidationService>? logger = null)
    {
        _httpClient = httpClient;
        _authenticationService = authenticationService;
        _hostPolicy = hostPolicy;
        var timeoutSeconds = int.TryParse(configuration?["Environment:RequestTimeoutSeconds"], out var parsedTimeout)
            ? Math.Clamp(parsedTimeout, 1, 300)
            : 30;
        _requestTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        _sanitizedDiagnosticsEnabled = bool.TryParse(configuration?["Poc:SanitizedDiagnosticsEnabled"], out var enabled)
            && enabled;
        _logger = logger ?? NullLogger<FinOpsConnectionValidationService>.Instance;
    }

    public async Task<ConnectionValidationResult> ValidateAsync(
        DiscoveredEnvironment environment,
        IntPtr parentWindow,
        CancellationToken cancellationToken)
    {
        var validatedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        if (!environment.IsSelectableFinOps
            || environment.FinOpsUrl is not { } finOpsUrl
            || !_hostPolicy.IsAllowed(finOpsUrl))
        {
            if (_sanitizedDiagnosticsEnabled)
            {
                _logger.LogWarning("FinOps connection test rejected by host policy.");
            }

            return new ConnectionValidationResult(
                ConnectionValidationStatus.Failed,
                "The environment URL has not been added through discovery or tool configuration.",
                validatedUtc);
        }

        var session = await _authenticationService.GetCurrentSessionAsync(environment.TenantId, cancellationToken).ConfigureAwait(false);
        if (session is null || !string.Equals(session.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return new ConnectionValidationResult(
                ConnectionValidationStatus.RequiresRevalidation,
                "Sign in to the same tenant as this environment before testing the connection.",
                validatedUtc);
        }

        try
        {
            var finOpsResource = finOpsUrl.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant();
            var scope = $"{finOpsResource}/.default";
            var accessToken = await _authenticationService
                .GetAccessTokenAsync(environment.TenantId, [scope], parentWindow, cancellationToken)
                .ConfigureAwait(false);

            using var response = await SendWithRetryAsync(finOpsUrl, accessToken, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                if (_sanitizedDiagnosticsEnabled)
                {
                    _logger.LogInformation(
                        "FinOps connection test completed. StatusCode={StatusCode}, ElapsedMilliseconds={ElapsedMilliseconds}.",
                        (int)response.StatusCode,
                        stopwatch.ElapsedMilliseconds);
                }

                return new ConnectionValidationResult(
                    ConnectionValidationStatus.Succeeded,
                    "The FinOps OData endpoint is reachable and authorized.",
                    validatedUtc);
            }

            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The FinOps token was rejected. Check the signed-in account and token audience.",
                HttpStatusCode.Forbidden => "The signed-in account is not authorized to access this FinOps environment.",
                HttpStatusCode.TooManyRequests => "FinOps throttled the connection test. Try again later.",
                _ => $"The FinOps OData endpoint returned HTTP {(int)response.StatusCode}."
            };

            if (_sanitizedDiagnosticsEnabled)
            {
                _logger.LogWarning(
                    "FinOps connection test failed. StatusCode={StatusCode}, ElapsedMilliseconds={ElapsedMilliseconds}.",
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }

            return new ConnectionValidationResult(ConnectionValidationStatus.Failed, message, validatedUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ConnectionValidationResult(
                ConnectionValidationStatus.Failed,
                "The FinOps connection test timed out.",
                DateTimeOffset.UtcNow);
        }
        catch (TimeoutException)
        {
            if (_sanitizedDiagnosticsEnabled)
            {
                _logger.LogWarning(
                    "FinOps connection test timed out. ElapsedMilliseconds={ElapsedMilliseconds}.",
                    stopwatch.ElapsedMilliseconds);
            }

            return new ConnectionValidationResult(
                ConnectionValidationStatus.Failed,
                "The FinOps connection test timed out.",
                DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException)
        {
            if (_sanitizedDiagnosticsEnabled)
            {
                _logger.LogWarning(
                    "FinOps connection test failed before an HTTP response. ElapsedMilliseconds={ElapsedMilliseconds}.",
                    stopwatch.ElapsedMilliseconds);
            }

            return new ConnectionValidationResult(
                ConnectionValidationStatus.Failed,
                "The FinOps OData endpoint could not be reached.",
                validatedUtc);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Uri environmentUrl,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var serviceRoot = new UriBuilder(environmentUrl.GetLeftPart(UriPartial.Authority))
        {
            Path = "/data"
        }.Uri;

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, serviceRoot);
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
                throw new TimeoutException("The FinOps connection request timed out.");
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 2)
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
                    "FinOps connection test throttled. Attempt={Attempt}, RetryDelaySeconds={RetryDelaySeconds}.",
                    attempt + 1,
                    retryDelay.TotalSeconds);
            }

            await Task.Delay(retryDelay, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}