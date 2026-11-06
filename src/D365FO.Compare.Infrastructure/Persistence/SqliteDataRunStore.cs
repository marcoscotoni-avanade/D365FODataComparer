using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D365FO.Compare.Application.DataComparison;
using D365FO.Compare.Domain.DataComparison;
using Microsoft.Data.Sqlite;

namespace D365FO.Compare.Infrastructure.Persistence;

public sealed class SqliteDataRunStoreFactory : IDataRunStoreFactory
{
    public IDataRunStore Create(Guid runId, long maxBytes) => new SqliteDataRunStore(runId, maxBytes);
}

public sealed class SqliteDataRunStore : IDataRunStore
{
    private readonly string _directory;
    private readonly SqliteConnection _connection;
    private readonly FileStream _lock;
    private readonly long _maxBytes;
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _dataKey = RandomNumberGenerator.GetBytes(32);
    private readonly AesGcm _cipher;
    private bool _disposed;
    private long _encryptedBytes;
    public string DirectoryPath => _directory;

    public SqliteDataRunStore(Guid runId, long maxBytes, string? root = null)
    {
        if (maxBytes < 65536) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
        _cipher = new AesGcm(_dataKey, 16);
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D365FODataComparisonTool", "DataRuns", "Temp");
        Directory.CreateDirectory(root);
        CleanupAbandoned(root);
        _directory = Path.Combine(root, "run-" + runId.ToString("N"));
        Directory.CreateDirectory(_directory);
        _lock = new FileStream(Path.Combine(_directory, "owner.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "records.db"), Pooling = false }.ToString());
        try
        {
            File.WriteAllBytes(Path.Combine(_directory, "key.dpapi"), ProtectedData.Protect(_dataKey, null, DataProtectionScope.CurrentUser));
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=DELETE;
                PRAGMA cache_size=-4096;
                PRAGMA temp_store=MEMORY;
                CREATE TABLE records(entity TEXT NOT NULL, side TEXT NOT NULL, digest TEXT NOT NULL, payload BLOB NOT NULL);
                CREATE INDEX record_keys ON records(entity,digest,side);
                CREATE TABLE results(id INTEGER PRIMARY KEY, entity TEXT NOT NULL, status INTEGER NOT NULL, payload BLOB NOT NULL);
                CREATE INDEX result_page ON results(entity,status,id);
                """;
            command.ExecuteNonQuery();
        }
        catch
        {
            _connection.Dispose();
            _lock.Dispose();
            _cipher.Dispose();
            CryptographicOperations.ZeroMemory(_dataKey);
            Directory.Delete(_directory, true);
            throw;
        }
    }

    public async Task AddPageAsync(string entity, string side, IReadOnlyList<DataRecord> records, CancellationToken cancellationToken)
    {
        var stagedBytes = _encryptedBytes;
        using var transaction = _connection.BeginTransaction();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = Convert.ToHexString(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(record.Key ?? Guid.NewGuid().ToString("N"))));
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO records(entity,side,digest,payload) VALUES($entity,$side,$digest,$payload)";
            command.Parameters.AddWithValue("$entity", entity);
            command.Parameters.AddWithValue("$side", side);
            command.Parameters.AddWithValue("$digest", digest);
            var payload = Protect(record);
            stagedBytes = checked(stagedBytes + payload.Length + 256);
            if (stagedBytes > _maxBytes) throw new DataLimitException("The configured temporary data disk budget was reached.");
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        CheckBudget();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        _encryptedBytes = stagedBytes;
    }

    public async IAsyncEnumerable<(IReadOnlyList<DataRecord> Source, IReadOnlyList<DataRecord> Target)> MatchAsync(
        string entity, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = "";
        while (true)
        {
            var digests = new List<string>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT digest FROM records WHERE entity=$entity AND digest>$cursor GROUP BY digest ORDER BY digest LIMIT 100";
                command.Parameters.AddWithValue("$entity", entity);
                command.Parameters.AddWithValue("$cursor", cursor);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) digests.Add(reader.GetString(0));
            }
            if (digests.Count == 0) yield break;
            foreach (var digest in digests)
            {
                var source = await ReadGroupAsync(entity, digest, "Source", cancellationToken).ConfigureAwait(false);
                var target = await ReadGroupAsync(entity, digest, "Target", cancellationToken).ConfigureAwait(false);
                yield return (source, target);
            }
            cursor = digests[^1];
        }
    }

    private async Task<IReadOnlyList<DataRecord>> ReadGroupAsync(string entity, string digest, string side, CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM records WHERE entity=$entity AND digest=$digest AND side=$side LIMIT 2";
        command.Parameters.AddWithValue("$entity", entity);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$side", side);
        var records = new List<DataRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(Unprotect<DataRecord>((byte[])reader[0]));
        return records;
    }

    public async Task AddResultsAsync(string entity, IReadOnlyList<DataRecordResult> results, CancellationToken cancellationToken)
    {
        var stagedBytes = _encryptedBytes;
        using var transaction = _connection.BeginTransaction();
        foreach (var result in results)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO results(entity,status,payload) VALUES($entity,$status,$payload)";
            command.Parameters.AddWithValue("$entity", entity);
            command.Parameters.AddWithValue("$status", (int)result.Status);
            var payload = Protect(result);
            stagedBytes = checked(stagedBytes + payload.Length + 256);
            if (stagedBytes > _maxBytes) throw new DataLimitException("The configured temporary data disk budget was reached.");
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        CheckBudget();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        _encryptedBytes = stagedBytes;
    }

    public async Task<IReadOnlyList<DataRecordResult>> ReadResultsAsync(string entity, DataRecordStatus? status, long offset, int count, CancellationToken cancellationToken)
    {
        using var command = ResultsCommand("SELECT payload FROM results", entity, status);
        command.CommandText += " ORDER BY id LIMIT $count OFFSET $offset";
        command.Parameters.AddWithValue("$count", Math.Clamp(count, 1, 1000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var results = new List<DataRecordResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(Unprotect<DataRecordResult>((byte[])reader[0]));
        return results;
    }

    public async Task<long> CountResultsAsync(string entity, DataRecordStatus? status, CancellationToken cancellationToken)
    {
        using var command = ResultsCommand("SELECT COUNT(*) FROM results", entity, status);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
    private SqliteCommand ResultsCommand(string select, string entity, DataRecordStatus? status)
    {
        var command = _connection.CreateCommand();
        command.CommandText = select + " WHERE entity=$entity" + (status is null ? "" : " AND status=$status");
        command.Parameters.AddWithValue("$entity", entity);
        if (status is not null) command.Parameters.AddWithValue("$status", (int)status.Value);
        return command;
    }
    private void CheckBudget()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA page_count";
        var pages = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "PRAGMA page_size";
        if (pages * Convert.ToInt64(command.ExecuteScalar()) > _maxBytes
            || Directory.EnumerateFiles(_directory).Sum(path => new FileInfo(path).Length) > _maxBytes)
            throw new DataLimitException("The configured temporary data disk budget was reached.");
    }
    private byte[] Protect<T>(T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var encrypted = new byte[plain.Length + 28];
            RandomNumberGenerator.Fill(encrypted.AsSpan(0, 12));
            _cipher.Encrypt(encrypted.AsSpan(0, 12), plain, encrypted.AsSpan(28), encrypted.AsSpan(12, 16));
            return encrypted;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private T Unprotect<T>(byte[] value)
    {
        if (value.Length < 28) throw new InvalidDataException("Invalid encrypted data record.");
        var plain = new byte[value.Length - 28];
        try
        {
            _cipher.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), plain);
            return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException("Invalid encrypted data record.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _connection.Dispose();
        _lock.Dispose();
        _cipher.Dispose();
        CryptographicOperations.ZeroMemory(_dataKey);
        CryptographicOperations.ZeroMemory(_secret);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        return ValueTask.CompletedTask;
    }
    private static void CleanupAbandoned(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "run-*"))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null || !File.Exists(Path.Combine(directory, "owner.lock"))) continue;
            try
            {
                using (new FileStream(Path.Combine(directory, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}