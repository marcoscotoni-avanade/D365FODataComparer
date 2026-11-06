using System.Globalization;
using System.Text;
using System.Text.Json;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;

namespace D365FO.Compare.Infrastructure.Export;

public sealed class DataComparisonExporter
{
    public async Task ExportAsync(DataRunManifest manifest, IDataRunStore store, string path, bool json,
        bool includeEqual, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                if (json)
                {
                    using var writer = new Utf8JsonWriter(file);
                    writer.WriteStartObject();
                    writer.WritePropertyName("manifest");
                    JsonSerializer.Serialize(writer, ExportManifest(manifest));
                    writer.WriteStartArray("records");
                    foreach (var entity in manifest.Entities.Where(entity => entity.Status == DataEntityStatus.Completed))
                    {
                        long offset = 0;
                        while (true)
                        {
                            var records = await store.ReadResultsAsync(entity.EntitySetName, null, offset, 500, cancellationToken).ConfigureAwait(false);
                            if (records.Count == 0) break;
                            foreach (var record in records)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (includeEqual || record.Status != DataRecordStatus.Equal)
                                    JsonSerializer.Serialize(writer, new { Entity = entity.EntitySetName, Result = record });
                            }
                            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                            offset += records.Count;
                        }
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await using var writer = new StreamWriter(file, new UTF8Encoding(true), 81920, leaveOpen: true);
                    await writer.WriteLineAsync("Entity,Status,Key,Field,Source,Target,SourceState,TargetState,Complete,Context");
                    var context = JsonSerializer.Serialize(ExportManifest(manifest));
                    foreach (var entity in manifest.Entities)
                    {
                        await writer.WriteLineAsync(Csv([entity.EntitySetName, entity.Status.ToString(), "", "", "", "", "", "", entity.IsComplete.ToString(), context]));
                        if (entity.Status != DataEntityStatus.Completed) continue;
                        long offset = 0;
                        while (true)
                        {
                            var records = await store.ReadResultsAsync(entity.EntitySetName, null, offset, 500, cancellationToken).ConfigureAwait(false);
                            if (records.Count == 0) break;
                            foreach (var record in records)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (!includeEqual && record.Status == DataRecordStatus.Equal) continue;
                                if (record.Differences.Count == 0)
                                    await writer.WriteLineAsync(Csv([entity.EntitySetName, record.Status.ToString(), record.Key, "", JsonSerializer.Serialize(record.Source), JsonSerializer.Serialize(record.Target), "", "", entity.IsComplete.ToString(), ""]));
                                foreach (var difference in record.Differences)
                                    await writer.WriteLineAsync(Csv([entity.EntitySetName, record.Status.ToString(), record.Key, difference.Field,
                                        difference.Source?.Original, difference.Target?.Original, difference.Source?.State.ToString(), difference.Target?.State.ToString(), entity.IsComplete.ToString(), ""],
                                        new HashSet<int>(new[] { (Index: 4, Value: difference.Source), (Index: 5, Value: difference.Target) }.Where(item => IsNumber(item.Value)).Select(item => item.Index))));
                            }
                            offset += records.Count;
                        }
                    }
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static object ExportManifest(DataRunManifest manifest) => new
    {
        manifest.RunId, manifest.StartedUtc, manifest.FinishedUtc, manifest.IsComplete, manifest.AreEqual,
        Source = new { manifest.Request.Source.DisplayName, manifest.Request.Source.FinOpsUrl, manifest.Request.SourceSchema.Fingerprint, manifest.Request.SourceSchema.CapturedAtUtc },
        Target = new { manifest.Request.Target.DisplayName, manifest.Request.Target.FinOpsUrl, manifest.Request.TargetSchema.Fingerprint, manifest.Request.TargetSchema.CapturedAtUtc },
        manifest.Request.Profile, manifest.Entities
    };
    private static bool IsNumber(RecordValue? value) => value is { State: RecordValueState.Present }
        && value.EdmType is "Edm.Byte" or "Edm.SByte" or "Edm.Int16" or "Edm.Int32" or "Edm.Int64" or "Edm.Decimal" or "Edm.Single" or "Edm.Double";
    public static string Csv(IEnumerable<string?> values, IReadOnlySet<int>? typedNumberColumns = null) => string.Join(',', values.Select((value, index) =>
    {
        var text = value ?? "";
        if (typedNumberColumns?.Contains(index) != true && text.Length != 0 && "=+-@\t\r\n".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }));
}