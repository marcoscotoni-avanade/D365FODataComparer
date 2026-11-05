using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D365FO.Compare.Application.Metadata;
using D365FO.Compare.Domain.Environments;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Infrastructure.Persistence;

public sealed class JsonSchemaSnapshotRepository(string? directory = null) : ISchemaSnapshotRepository
{
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "D365FODataComparisonTool", "Schemas");
    private sealed record Envelope(int Version, SchemaSnapshot Snapshot, string IntegrityHash);

    public async Task<SchemaSnapshot?> LoadAsync(EnvironmentConnection environment, string accountId, CancellationToken cancellationToken)
    {
        var path = GetPath(environment.TenantId, accountId, environment.ProviderEnvironmentId, environment.FinOpsUrl.GetLeftPart(UriPartial.Authority));
        if (!File.Exists(path)) return null;
        try
        {
            await using var file = File.OpenRead(path);
            var envelope = await JsonSerializer.DeserializeAsync<Envelope>(file, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (envelope is null || envelope.Version != 1 || envelope.Snapshot is not { } snapshot || snapshot.FormatVersion != 1
                || snapshot.PolicyVersion != "schema-v1" || snapshot.AccountId != accountId || snapshot.EnvironmentId != environment.ProviderEnvironmentId
                || !string.Equals(snapshot.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(snapshot.EnvironmentOrigin, environment.FinOpsUrl.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                || Hash(snapshot) != envelope.IntegrityHash || SchemaFingerprint.Compute(snapshot) != snapshot.Fingerprint)
                throw new InvalidDataException("The schema cache is incompatible or failed its integrity check. Clear the cache and refresh.");
            return snapshot;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The schema cache contains invalid JSON. Clear the cache and refresh.");
        }
    }

    public async Task SaveAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (SchemaFingerprint.Compute(snapshot) != snapshot.Fingerprint)
            throw new InvalidDataException("Cannot save a schema snapshot with an invalid fingerprint.");
        Directory.CreateDirectory(_directory);
        var path = GetPath(snapshot.TenantId, snapshot.AccountId, snapshot.EnvironmentId, snapshot.EnvironmentOrigin);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, new Envelope(1, snapshot, Hash(snapshot)), cancellationToken: cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task DeleteAsync(EnvironmentConnection environment, string accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Delete(GetPath(environment.TenantId, accountId, environment.ProviderEnvironmentId, environment.FinOpsUrl.GetLeftPart(UriPartial.Authority)));
        }
        catch (DirectoryNotFoundException)
        {
            return Task.CompletedTask;
        }
        return Task.CompletedTask;
    }

    private string GetPath(string tenant, string account, string environment, string origin)
    {
        var identity = JsonSerializer.Serialize(new[] { tenant.ToLowerInvariant(), account, environment, origin.TrimEnd('/').ToLowerInvariant() });
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }
    private static string Hash(SchemaSnapshot snapshot) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
}