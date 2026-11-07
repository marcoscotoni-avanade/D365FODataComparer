using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Application.Validation;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.Metadata;

namespace D365FO.Compare.Infrastructure.ODataData;

public sealed class ODataEntityCountReader : IEntityCountReader
{
    private const int MaxResponseBytes = 16 * 1024;
    private readonly HttpClient _http;
    private readonly IAuthenticationService _authentication;
    private readonly IFinOpsHostPolicy _hosts;
    private readonly MetadataWindowContext _window;
    private readonly ODataQueryBuilder _queries;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    public ODataEntityCountReader(HttpClient http, IAuthenticationService authentication, IFinOpsHostPolicy hosts,
        MetadataWindowContext window, ODataQueryBuilder queries)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _authentication = authentication;
        _hosts = hosts;
        _window = window;
        _queries = queries;
    }

    public async Task<EntityCountReadResult> CountAsync(EnvironmentConnection environment, SchemaSnapshot schema, EntityDataPlan plan,
        string company, string side, CancellationToken cancellationToken)
    {
        var stage = "Building count query";
        var query = "";
        int? httpStatus = null;
        string? requestId = null;
        try
        {
            var origin = new Uri(environment.FinOpsUrl.GetLeftPart(UriPartial.Authority));
            if (schema.EnvironmentId != environment.ProviderEnvironmentId
                || !string.Equals(schema.EnvironmentOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                throw new DataAuthorizationException("The schema does not belong to the configured data environment.");
            if (!_hosts.IsAllowed(environment.FinOpsUrl)) throw new DataAuthorizationException("The data environment is not approved by the host policy.");

            var uri = _queries.BuildCount(origin, plan, schema, company);
            query = uri.Query;
            if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath != "/data/" + Uri.EscapeDataString(plan.EntitySetName))
                throw new DataAuthorizationException("The count request did not remain on the configured entity endpoint.");

            stage = "Validating session";
            await ValidateIdentityAsync(environment, schema, cancellationToken).ConfigureAwait(false);
            stage = "Acquiring FO token";
            await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string token;
            try
            {
                token = await _authentication.GetAccessTokenAsync([origin.AbsoluteUri.TrimEnd('/').ToLowerInvariant() + "/.default"],
                    _window.ParentWindowProvider(), cancellationToken).ConfigureAwait(false);
            }
            finally { _tokenGate.Release(); }
            stage = "Validating session after token";
            await ValidateIdentityAsync(environment, schema, cancellationToken).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stage = "Sending count request";
                httpStatus = null;
                requestId = null;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd("application/json");
                HttpResponseMessage response;
                try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
                catch (HttpRequestException) when (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using (response)
                {
                    httpStatus = (int)response.StatusCode;
                    requestId = GetRequestId(response);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new DataAuthorizationException($"Count request refused (HTTP {(int)response.StatusCode}). Verify the account and entity permissions.");
                    if (attempt < 2 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                    {
                        var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt + 1));
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 0, 120)), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new IOException($"Count endpoint returned HTTP {(int)response.StatusCode}.");

                    if (response.Content.Headers.ContentLength > MaxResponseBytes)
                        throw new InvalidDataException("The count response exceeded its size limit.");
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                        && mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) != true)
                        throw new InvalidDataException("The count query did not return an OData JSON response.");

                    stage = "Reading count response";
                    var body = await ReadBoundedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                    stage = "Parsing count response";
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("@odata.count", out var countElement)
                        || countElement.ValueKind != JsonValueKind.Number
                        || !countElement.TryGetInt64(out var count) || count < 0)
                        throw new InvalidDataException("The OData response did not contain a valid non-negative @odata.count.");
                    if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("The count query response did not contain the expected OData value array.");
                    if (values.GetArrayLength() != 0)
                        throw new InvalidDataException("The count query returned records despite the zero-row page limit; record data was discarded.");
                    stage = "Validating session after response";
                    await ValidateIdentityAsync(environment, schema, cancellationToken).ConfigureAwait(false);
                    return new(count);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or InvalidOperationException
            or FormatException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            var identifiers = plan.Filters.Select(filter => filter.Field).Append(plan.EntitySetName).ToHashSet(StringComparer.Ordinal);
            var reason = DataErrorDetails.Message(exception.Message, identifiers, query);
            return new(null, new DataFailureDiagnostic(side, stage, null, exception.GetType().Name, reason, httpStatus, requestId));
        }
    }

    private async Task ValidateIdentityAsync(EnvironmentConnection environment, SchemaSnapshot schema, CancellationToken cancellationToken)
    {
        if (!_hosts.IsAllowed(environment.FinOpsUrl)) throw new DataAuthorizationException("The data environment is not approved by the host policy.");
        var session = await _authentication.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is null || session.HomeAccountId != schema.AccountId
            || !string.Equals(session.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(session.TenantId, schema.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new DataAuthorizationException("The signed-in account or tenant changed. The count operation was stopped.");
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[MaxResponseBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > MaxResponseBytes) throw new InvalidDataException("The count response exceeded its size limit.");
        return bytes.AsSpan(0, length).ToArray();
    }

    private static string? GetRequestId(HttpResponseMessage response) => new[]
        { "x-ms-request-id", "request-id", "x-ms-correlation-request-id", "activityid" }
        .Select(name => response.Headers.TryGetValues(name, out var values) ? DataErrorDetails.Identifier(values.FirstOrDefault()) : null)
        .FirstOrDefault(value => value is not null);
}