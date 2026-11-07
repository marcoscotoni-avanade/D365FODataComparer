using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Infrastructure.Persistence;

public sealed class JsonDataComparisonProfileRepository(string? directory = null) : IDataComparisonProfileRepository
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "D365FODataComparisonTool", "DataProfiles");
    private readonly string _directory = directory ?? DefaultDirectory;
    public async Task SaveAsync(DataComparisonRequest request, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("The profile file path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var file = new ProfileFile(1, GetIdentity(request.SourceSchema), GetIdentity(request.TargetSchema), request.Profile);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(file), null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<DataComparisonProfile?> LoadAsync(SchemaSnapshot source, SchemaSnapshot target, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected data comparison profile does not exist.", fullPath);
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var plaintext = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        using var document = JsonDocument.Parse(plaintext);
        DataComparisonProfile profile;
        if (document.RootElement.TryGetProperty("Profile", out _))
        {
            var file = JsonSerializer.Deserialize<ProfileFile>(plaintext)
                ?? throw new InvalidDataException("The data comparison profile is incompatible.");
            if (file.FormatVersion != 1 || file.SourceIdentity != GetIdentity(source) || file.TargetIdentity != GetIdentity(target))
                throw new InvalidDataException("The profile belongs to a different Source/Target account, environment or schema pair.");
            profile = file.Profile;
        }
        else
        {
            var legacyProfile = JsonSerializer.Deserialize<DataComparisonProfile>(plaintext)
                ?? throw new InvalidDataException("The data comparison profile is incompatible.");
            var expectedPath = GetLegacyPath(source, target);
            if (!string.Equals(Path.GetFileName(fullPath), Path.GetFileName(expectedPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This legacy profile has no embedded environment identity and must be opened from its original identity-named file.");
            profile = legacyProfile;
        }
        return FieldSelectionPolicy.Migrate(profile, source);
    }
    private string GetLegacyPath(SchemaSnapshot source, SchemaSnapshot target)
    {
        var identity = JsonSerializer.Serialize(new[] { source.TenantId, source.AccountId, source.EnvironmentId, source.EnvironmentOrigin,
            source.Fingerprint, target.AccountId, target.EnvironmentId, target.EnvironmentOrigin, target.Fingerprint });
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".profile");
    }
    private static string GetIdentity(SchemaSnapshot schema) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
    {
        schema.TenantId, schema.AccountId, schema.EnvironmentId, schema.EnvironmentOrigin, schema.Fingerprint
    }))));
    private sealed record ProfileFile(int FormatVersion, string SourceIdentity, string TargetIdentity, DataComparisonProfile Profile);
}