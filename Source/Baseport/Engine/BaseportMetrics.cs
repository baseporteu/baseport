using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Baseport;

// ponytail: no exporter, read with dotnet-counters; add OpenTelemetry or Prometheus when an operator asks
public static class BaseportMetrics
{
    public const string MeterName = "Baseport";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> WriteDuration =
        Meter.CreateHistogram<double>("baseport.record.write.duration", "ms", "Time to save a change to records");

    private static readonly Counter<long> JobFailures =
        Meter.CreateCounter<long>("baseport.job.failures", description: "Scheduled job runs that failed");

    private static string _backups = "";

    private static Func<int> _auditQueue = () => 0;

    static BaseportMetrics()
    {
        Meter.CreateObservableGauge("baseport.sse.subscribers", () => RecordEvents.SubscriberCount, description: "Open realtime subscriptions");
        Meter.CreateObservableGauge("baseport.backup.age", BackupAge, "s", "Seconds since the newest backup archive");
        Meter.CreateObservableGauge("baseport.audit.queue", () => _auditQueue(), description: "Audit entries waiting to be written");
    }

    public static void Initialize(string backupsDirectory, Func<int> auditQueue)
    {
        _backups = backupsDirectory;
        _auditQueue = auditQueue;
    }

    public static void RecordWrite(long startedTimestamp) =>
        WriteDuration.Record(Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);

    public static void JobFailed(string key) => JobFailures.Add(1, new KeyValuePair<string, object?>("job", key));

    private static IEnumerable<Measurement<double>> BackupAge()
    {
        if (_backups.Length == 0) yield break;
        var newest = BackupStore.List(_backups).FirstOrDefault(b => !b.DatabaseOnly);
        if (newest is not null) yield return new((DateTime.UtcNow - newest.CreatedAt).TotalSeconds);
    }
}
