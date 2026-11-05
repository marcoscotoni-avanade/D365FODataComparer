using System.Net;
using System.Net.Http.Headers;
using D365FO.Compare.Application.Metadata;
using Microsoft.Extensions.Configuration;

namespace D365FO.Compare.Infrastructure.FoHttp;

public sealed class MetadataEndpointException(string endpoint, HttpStatusCode statusCode, string? requestId)
    : IOException($"{endpoint}: HTTP {(int)statusCode}. " + (statusCode switch
    {
        HttpStatusCode.Unauthorized => "The FO token was rejected.",
        HttpStatusCode.Forbidden => "The signed-in user is not authorized.",
        HttpStatusCode.NotFound => "The required metadata endpoint is unavailable.",
        _ => "The metadata request failed."
    }))
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? RequestId { get; } = requestId;
}

public sealed class FoMetadataHttpClient
{
    private readonly HttpClient _http;
    private readonly long _maxBytes;
    private readonly TimeSpan? _totalTimeout;

    public FoMetadataHttpClient(HttpClient http, IConfiguration? configuration = null)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _maxBytes = long.TryParse(configuration?["Metadata:MaxPayloadBytes"], out var bytes) && bytes > 0 ? bytes : 512L * 1024 * 1024;
        _totalTimeout = double.TryParse(configuration?["Metadata:TotalTimeoutMinutes"], out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes) : null;
    }

    public async Task<FileStream> DownloadAsync(Uri uri, string token, string endpoint, string accept,
        IProgress<MetadataProgress>? progress, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_totalTimeout is { } timeout) operation.CancelAfter(timeout);
        var ct = operation.Token;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new MetadataProgress($"Waiting for {endpoint}"));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd(accept);
                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < 2)
                {
                    progress?.Report(new MetadataProgress($"Retrying {endpoint} after network error"));
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }
                using (response)
                {
                    if (attempt < 2 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                    {
                        var wait = response.Headers.RetryAfter?.Delta
                            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt + 1));
                        progress?.Report(new MetadataProgress($"Retrying {endpoint} (HTTP {(int)response.StatusCode})"));
                        response.Dispose();
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 0, 120)), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new MetadataEndpointException(endpoint, response.StatusCode,
                            response.Headers.TryGetValues("x-ms-request-id", out var ids) ? ids.FirstOrDefault() : null);
                    if (response.Content.Headers.ContentLength > _maxBytes)
                        throw new InvalidDataException($"{endpoint} exceeds the configured metadata payload limit.");
                    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "D365FODataComparisonTool", "Temp");
                    Directory.CreateDirectory(directory);
                    var file = new FileStream(Path.Combine(directory, $"metadata-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
                        FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                    try
                    {
                        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        var buffer = new byte[81920];
                        long total = 0;
                        int count;
                        while ((count = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                        {
                            total += count;
                            if (total > _maxBytes) throw new InvalidDataException($"{endpoint} exceeds the configured metadata payload limit.");
                            await file.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                            progress?.Report(new MetadataProgress($"Downloading {endpoint}", total, response.Content.Headers.ContentLength));
                        }
                        file.Position = 0;
                        return file;
                    }
                    catch
                    {
                        await file.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(operation.IsCancellationRequested
                ? $"{endpoint} exceeded the optional metadata timeout."
                : $"{endpoint} was interrupted by a transport or connection timeout; no short metadata HTTP timeout is configured.");
        }
    }
}