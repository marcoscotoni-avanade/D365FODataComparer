using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Infrastructure.Persistence;

public sealed class JsonDataComparisonProfileRepository(string? directory = null) : IDataComparisonProfileRepository
{
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D365FODataComparisonTool", "DataProfiles");
    public async Task SaveAsync(DataComparisonRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var path = GetPath(request.SourceSchema, request.TargetSchema);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(request.Profile), null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<DataComparisonProfile?> LoadAsync(SchemaSnapshot source, SchemaSnapshot target, CancellationToken cancellationToken)
    {
        var path = GetPath(source, target);
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var profile = JsonSerializer.Deserialize<DataComparisonProfile>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        if (profile is null) throw new InvalidDataException("The data comparison profile is incompatible.");
        return FieldSelectionPolicy.Migrate(profile, source);
    }
    private string GetPath(SchemaSnapshot source, SchemaSnapshot target)
    {
        var identity = JsonSerializer.Serialize(new[] { source.TenantId, source.AccountId, source.EnvironmentId, source.EnvironmentOrigin,
            source.Fingerprint, target.AccountId, target.EnvironmentId, target.EnvironmentOrigin, target.Fingerprint });
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".profile");
    }
}