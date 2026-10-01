using System.Diagnostics.Metrics;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public sealed class MetricsTests
{
    [Fact]
    public void EveryInstrumentIsPublished()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name != BaseportMetrics.MeterName) return;
                lock (seen) seen.Add(instrument.Name);
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();
        BaseportMetrics.JobFailed("test");

        Assert.Superset(new HashSet<string>
        {
            "baseport.sse.subscribers", "baseport.backup.age", "baseport.record.write.duration",
            "baseport.audit.queue", "baseport.job.failures"
        }, seen);
    }

    [Fact]
    public async Task RecordWriteIsMeasured()
    {
        var writes = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "baseport.record.write.duration") l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref writes));
        listener.Start();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var db = TestDb.Open(conn);
        await SchemaBootstrap.ApplyAsync(db);
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Notes" };
        db.Tables.Add(table);
        db.Records.Add(new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = "{}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(writes > 0);
    }
}
