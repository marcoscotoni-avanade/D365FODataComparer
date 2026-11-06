using System.Text;
using D365FO.Compare.Domain.DataComparison;
using D365FO.Compare.Infrastructure.Persistence;

namespace D365FO.Compare.UnitTests;

[CollectionDefinition("Memory-sensitive data store", DisableParallelization = true)]
public sealed class DataStoreMemoryCollection;

[Collection("Memory-sensitive data store")]
public sealed class DataRunStoreTests
{
    internal static DataRecord Record(string key, string value) => new(new Dictionary<string, RecordValue>
    {
        ["Name"] = new(RecordValueState.Present, "Edm.String", value, value)
    }, key);

    [Fact]
    public async Task StoreKeepsDuplicatesEncryptsValuesAndDeletesOnDispose()
    {
        var store = new SqliteDataRunStore(Guid.NewGuid(), 16 * 1024 * 1024);
        var directory = store.DirectoryPath;
        try
        {
            await store.AddPageAsync("Customers", "Source", [Record("private-key-123", "confidential-123"), Record("private-key-123", "second")], default);
            await store.AddPageAsync("Customers", "Target", [Record("private-key-123", "target")], default);
            var groups = 0;
            await foreach (var group in store.MatchAsync("Customers", default))
            {
                groups++;
                Assert.Equal(2, group.Source.Count);
                Assert.Single(group.Target);
            }
            Assert.Equal(1, groups);
            await using var file = new FileStream(Path.Combine(directory, "records.db"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var contents = new MemoryStream();
            await file.CopyToAsync(contents);
            var bytes = contents.ToArray();
            Assert.DoesNotContain("confidential-123", Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("private-key-123", Encoding.UTF8.GetString(bytes));
        }
        finally { await store.DisposeAsync(); }
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task CanceledPageRollsBackAndOtherActiveRunIsNotCleaned()
    {
        await using var store = new SqliteDataRunStore(Guid.NewGuid(), 16 * 1024 * 1024);
        await using var other = new SqliteDataRunStore(Guid.NewGuid(), 16 * 1024 * 1024);
        Assert.True(Directory.Exists(store.DirectoryPath));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddPageAsync("Customers", "Source", [Record("key", "value")], new CancellationToken(true)));
        await foreach (var group in store.MatchAsync("Customers", default)) Assert.Fail("Canceled page must not be committed.");
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task StoreMatchesOneHundredThousandRowsPerSideWithBoundedBatches()
    {
        var before = GC.GetTotalMemory(true);
        await using var store = new SqliteDataRunStore(Guid.NewGuid(), 512L * 1024 * 1024);
        for (var start = 0; start < 100000; start += 1000)
        {
            var page = Enumerable.Range(start, 1000).Select(index => Record("key-" + index, "value-" + index)).ToArray();
            await store.AddPageAsync("Customers", "Source", page, default);
            await store.AddPageAsync("Customers", "Target", page, default);
        }
        var matched = 0;
        await foreach (var group in store.MatchAsync("Customers", default))
        {
            Assert.Single(group.Source);
            Assert.Single(group.Target);
            Assert.Equal(group.Source[0].Key, group.Target[0].Key);
            matched++;
        }
        Assert.Equal(100000, matched);
        Assert.True(GC.GetTotalMemory(true) - before < 64L * 1024 * 1024, "The store must not retain an in-memory copy of the complete dataset.");
    }
}