using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Application.Validation;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;
using D365FO.Compare.Infrastructure.Metadata;

namespace D365FO.Compare.Infrastructure.ODataData;

public sealed class ODataEntityDataReader : IEntityDataReader
{
    private readonly HttpClient _http;
    private readonly IAuthenticationService _authentication;
    private readonly IFinOpsHostPolicy _hosts;
    private readonly MetadataWindowContext _window;
    private readonly ODataQueryBuilder _queries;
    private readonly RecordValueCanonicalizer _values;
    public ODataEntityDataReader(HttpClient http, IAuthenticationService authentication, IFinOpsHostPolicy hosts,
        MetadataWindowContext window, ODataQueryBuilder queries, RecordValueCanonicalizer values)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _authentication = authentication;
        _hosts = hosts;
        _window = window;
        _queries = queries;
        _values = values;
    }

    public async IAsyncEnumerable<IReadOnlyList<DataRecord>> ReadPagesAsync(EnvironmentConnection environment, SchemaSnapshot schema,
        PreparedEntityPlan plan, string company, DataReadLimits limits, IProgress<DataReadProgress>? progress,
        string side, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var context = new ReadDiagnosticContext();
        await using var pages = ReadPagesCoreAsync(environment, schema, plan, company, limits, progress, side, context, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasPage;
            try { hasPage = await pages.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException
                or InvalidOperationException or FormatException or HttpRequestException or OperationCanceledException
                or System.Security.Cryptography.CryptographicException)
            {
                var json = exception as JsonException;
                var identifiers = plan.Fields.Select(property => property.Name).Append(plan.Plan.EntitySetName).ToHashSet(StringComparer.Ordinal);
                var reason = context.ServerMessage is not null ? context.ServerMessage
                    : DataErrorDetails.Message(exception.Message, identifiers, context.Query);
                var diagnostic = new DataFailureDiagnostic(side, context.Stage, context.Page, exception.GetType().Name, reason,
                    context.HttpStatus, context.RequestId, context.ServerErrorCode, json?.Path, json?.LineNumber,
                    json?.BytePositionInLine, exception.InnerException?.GetType().Name);
                throw new DataReadDiagnosticException(diagnostic, exception);
            }
            if (!hasPage) yield break;
            yield return pages.Current;
        }
    }

    private async IAsyncEnumerable<IReadOnlyList<DataRecord>> ReadPagesCoreAsync(EnvironmentConnection environment, SchemaSnapshot schema,
        PreparedEntityPlan plan, string company, DataReadLimits limits, IProgress<DataReadProgress>? progress,
        string side, ReadDiagnosticContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var origin = new Uri(environment.FinOpsUrl.GetLeftPart(UriPartial.Authority));
        if (schema.EnvironmentId != environment.ProviderEnvironmentId
            || !string.Equals(schema.EnvironmentOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            throw new DataAuthorizationException("The schema does not belong to the configured data environment.");
        Uri? next = _queries.Build(origin, plan, schema, company);
        var initial = next;
        var completeProjection = schema.Entities.Single(entity => entity.EntitySetName == plan.Plan.EntitySetName).Fields.Select(property => property.Name).ToArray();
        context.Query = initial.Query;
        var path = next.AbsolutePath;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        long rows = 0;
        long totalBytes = 0;
        while (next is not null)
        {
            context.Page = visited.Count + 1;
            context.Stage = "Validating paging limits";
            context.HttpStatus = null;
            context.RequestId = null;
            context.ServerMessage = null;
            context.ServerErrorCode = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (visited.Count >= limits.MaxPages || !visited.Add(next.AbsoluteUri))
                throw new DataLimitException("Data paging exceeded its limit or contains a loop.");
            context.Stage = "Validating session";
            await ValidateIdentityAsync(environment, schema, cancellationToken).ConfigureAwait(false);
            context.Stage = "Acquiring FO token";
            var token = await _authentication.GetAccessTokenAsync([origin.AbsoluteUri.TrimEnd('/').ToLowerInvariant() + "/.default"],
                _window.ParentWindowProvider(), cancellationToken).ConfigureAwait(false);
            context.Stage = "Validating session after token";
            await ValidateIdentityAsync(environment, schema, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(plan.Plan.EntitySetName, side, "Waiting for data", rows, visited.Count - 1, totalBytes));
            context.Stage = "Sending HTTP request";
            using var body = await DownloadPageAsync(next, token, limits.MaxPageBytes, context, plan, cancellationToken).ConfigureAwait(false);
            totalBytes += body.Length;
            if (totalBytes > limits.MaxSpoolBytes) throw new DataLimitException("The cumulative data transfer budget was reached.");
            context.Stage = "Parsing JSON response";
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            context.Stage = "Validating OData envelope";
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("value", out var array)
                || array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The data endpoint must return an OData value array.");
            if (array.GetArrayLength() > limits.MaxRows - rows) throw new DataLimitException("The configured row limit was reached before this entity was fully read.");
            var records = new List<DataRecord>(array.GetArrayLength());
            context.Stage = "Canonicalizing records and validating company";
            foreach (var row in array.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                records.Add(_values.ReadRecord(row, plan, schema, company));
            }
            context.Stage = "Validating nextLink";
            var link = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink) ? nextLink.GetString() : null;
            next = link is null ? null : ValidateNextLink(origin, next, path, link, initial, completeProjection);
            rows += records.Count;
            context.Stage = "Validating session after response";
            await ValidateIdentityAsync(environment, schema, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(plan.Plan.EntitySetName, side, "Page received", rows, visited.Count, totalBytes));
            context.Stage = "Page ready";
            yield return records;
            if (rows >= limits.MaxRows && next is not null) throw new DataLimitException("The row limit was reached while more server pages remain.");
        }
    }

    private async Task ValidateIdentityAsync(EnvironmentConnection environment, SchemaSnapshot schema, CancellationToken cancellationToken)
    {
        if (!_hosts.IsAllowed(environment.FinOpsUrl)) throw new DataAuthorizationException("The data environment is not approved by the host policy.");
        var session = await _authentication.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is null || session.HomeAccountId != schema.AccountId
            || !string.Equals(session.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(session.TenantId, schema.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new DataAuthorizationException("The signed-in account or tenant changed. The data run was stopped.");
    }

    private async Task<MemoryStream> DownloadPageAsync(Uri uri, string token, long maxBytes, ReadDiagnosticContext context,
        PreparedEntityPlan plan, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            context.Stage = "Sending HTTP request";
            context.HttpStatus = null;
            context.RequestId = null;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json;IEEE754Compatible=true");
            request.Headers.TryAddWithoutValidation("Prefer", "odata.maxpagesize=1000");
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
                continue;
            }
            using (response)
            {
                context.HttpStatus = (int)response.StatusCode;
                context.RequestId = new[] { "x-ms-request-id", "request-id", "x-ms-correlation-request-id", "activityid" }
                    .Select(name => response.Headers.TryGetValues(name, out var values) ? DataErrorDetails.Identifier(values.FirstOrDefault()) : null)
                    .FirstOrDefault(value => value is not null);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    await CaptureServerErrorAsync(response, context, plan, cancellationToken).ConfigureAwait(false);
                    throw new DataAuthorizationException($"Data read refused (HTTP {(int)response.StatusCode}). Verify the account and entity permissions before starting another run.");
                }
                if (attempt < 2 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt + 1));
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 0, 120)), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    await CaptureServerErrorAsync(response, context, plan, cancellationToken).ConfigureAwait(false);
                    throw new IOException($"Data endpoint returned HTTP {(int)response.StatusCode}.");
                }
                if (response.Content.Headers.ContentLength > maxBytes) throw new DataLimitException("The configured page byte limit was reached.");
                var contents = new MemoryStream();
                context.Stage = "Downloading HTTP response body";
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = new byte[81920];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        if (contents.Length + count > maxBytes) throw new DataLimitException("The configured page byte limit was reached.");
                        await contents.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }
                    contents.Position = 0;
                    return contents;
                }
                catch { contents.Dispose(); throw; }
            }
        }
    }

    private static async Task CaptureServerErrorAsync(HttpResponseMessage response, ReadDiagnosticContext context,
        PreparedEntityPlan plan, CancellationToken cancellationToken)
    {
        context.Stage = "Reading HTTP error response";
        context.ServerMessage = $"FO returned HTTP {(int)response.StatusCode}.";
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[16384];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
            }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return;
            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                context.ServerErrorCode = DataErrorDetails.Identifier(code.GetString());
            if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                context.ServerMessage = DataErrorDetails.Message(message.GetString()!, plan.Fields.Select(property => property.Name)
                    .Append(plan.Plan.EntitySetName).ToHashSet(StringComparer.Ordinal), context.Query);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or HttpRequestException) { }
    }

    private sealed class ReadDiagnosticContext
    {
        public string Stage { get; set; } = "Building data query";
        public int Page { get; set; } = 1;
        public int? HttpStatus { get; set; }
        public string? RequestId { get; set; }
        public string? ServerErrorCode { get; set; }
        public string? ServerMessage { get; set; }
        public string? Query { get; set; }
    }

    public static Uri ValidateNextLink(Uri origin, Uri current, string expectedPath, string link, Uri? initial = null,
        IReadOnlyCollection<string>? completeProjection = null)
    {
        if (!Uri.TryCreate(current, link, out var next) || next.Scheme != Uri.UriSchemeHttps
            || !string.Equals(next.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase)
            || next.AbsolutePath != expectedPath || next.UserInfo.Length != 0 || next.Fragment.Length != 0)
            throw new InvalidDataException("The data nextLink must remain on the same HTTPS EntitySet endpoint.");
        var query = System.Web.HttpUtility.ParseQueryString(next.Query);
        if (query.AllKeys.Any(name => name is not null && (name.Equals("$expand", StringComparison.OrdinalIgnoreCase)
            || name.Equals("$apply", StringComparison.OrdinalIgnoreCase) || name.Equals("$compute", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Navigation expansion or computed projections are not allowed in data paging.");
        if (initial is not null)
        {
            var original = System.Web.HttpUtility.ParseQueryString(initial.Query);
            var expectedSelect = original["$select"];
            var selectionMatches = query["$select"] is not { } select
                || expectedSelect is not null && select.Split(',').ToHashSet(StringComparer.Ordinal).SetEquals(expectedSelect.Split(','))
                || expectedSelect is null && (select == "*" || completeProjection is not null && select.Split(',').ToHashSet(StringComparer.Ordinal).SetEquals(completeProjection));
            if (!selectionMatches
                || query["$filter"] is { } filter && filter != original["$filter"]
                || query["cross-company"] is { } cross && !string.Equals(cross, original["cross-company"], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Data paging changed the approved field projection or explicit query scope.");
        }
        return next;
    }
}