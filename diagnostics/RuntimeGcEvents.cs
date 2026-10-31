using System.Diagnostics.Tracing;

namespace DiscoAutosave;

internal sealed class RuntimeGcEvents : EventListener
{
    internal readonly record struct Sample(int Id, DateTime Utc, int Generation, int Reason);
    private readonly object gate = new();
    private readonly Queue<Sample> samples = new();
    internal bool ProviderEnabled { get; private set; }
    internal long EventsSeen;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name != "Microsoft-Windows-DotNETRuntime") return;
        EnableEvents(source, EventLevel.Informational, (EventKeywords)1);
        ProviderEnabled = true;
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventId is not (1 or 2 or 3 or 7 or 8 or 9)) return;
        int generation = -1, reason = -1;
        if (data.EventId == 1 && data.PayloadNames != null && data.Payload != null)
            for (int i = 0; i < data.PayloadNames.Count; i++)
            {
                if (data.PayloadNames[i] == "Depth") generation = Convert.ToInt32(data.Payload[i]);
                if (data.PayloadNames[i] == "Reason") reason = Convert.ToInt32(data.Payload[i]);
            }
        lock (gate)
        {
            samples.Enqueue(new(data.EventId, data.TimeStamp.ToUniversalTime(), generation, reason));
            if (samples.Count > 2048) samples.Dequeue();
        }
        Interlocked.Increment(ref EventsSeen);
    }

    internal Sample[] Snapshot(DateTime from, DateTime to)
    {
        lock (gate) return samples.Where(s => s.Utc >= from && s.Utc <= to).OrderBy(s => s.Utc).ToArray();
    }

    internal static (DateTime Start, DateTime End, int Generation, int Reason)[] Pauses(IEnumerable<Sample> events)
    {
        var result = new List<(DateTime, DateTime, int, int)>();
        DateTime? suspended = null;
        int generation = -1, reason = -1;
        foreach (var sample in events.OrderBy(s => s.Utc))
        {
            if (sample.Id == 9) { suspended = sample.Utc; generation = reason = -1; }
            if (sample.Id == 1) { generation = sample.Generation; reason = sample.Reason; }
            if (sample.Id == 3 && suspended.HasValue)
            {
                result.Add((suspended.Value, sample.Utc, generation, reason));
                suspended = null;
            }
        }
        return result.ToArray();
    }
}
