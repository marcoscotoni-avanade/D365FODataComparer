using System.Security.Cryptography;
using System.Text.Json;
using D365FO.Compare.Domain.Metadata;

namespace D365FO.Compare.Application.Metadata;

public static class SchemaFingerprint
{
    public static string Compute(SchemaSnapshot snapshot)
    {
        object Fields(IReadOnlyList<FieldSchema> fields) => fields.OrderBy(field => field.Name, StringComparer.Ordinal).Select(field => new
        {
            field.Name, field.TypeFullName, field.IsCollection, field.Nullable, field.MaxLength, field.Precision, field.Scale, field.EnumTypeFullName
        }).ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.PolicyVersion,
            Entities = snapshot.Entities.OrderBy(entity => entity.EntitySetName, StringComparer.Ordinal).Select(entity => new
            {
                entity.EntitySetName, entity.EntityTypeFullName, entity.Key, Fields = Fields(entity.Fields)
            }).ToArray(),
            Enums = snapshot.Enums.OrderBy(type => type.FullName, StringComparer.Ordinal).Select(type => new
            {
                type.FullName, type.UnderlyingType, type.IsFlags,
                Members = type.Members.OrderBy(member => member.Name, StringComparer.Ordinal).ToArray()
            }).ToArray(),
            ComplexTypes = snapshot.ComplexTypes.OrderBy(type => type.FullName, StringComparer.Ordinal)
                .Select(type => new { type.FullName, Fields = Fields(type.Fields) }).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
    }
}