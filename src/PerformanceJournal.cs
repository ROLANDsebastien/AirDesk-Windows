using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AirDesk.Companion;

internal sealed record PerformanceDay(DateOnly Date)
{
    public double CpuSeconds { get; init; }
    public long PeakMemoryBytes { get; init; }
    public int LocalImages { get; init; }
    public long LocalImageBytes { get; init; }
    public int SentMeasures { get; init; }
    public int SentImages { get; init; }
    public long SentImageBytes { get; init; }
}

internal sealed class PerformanceJournal : IDisposable
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly string path;
    private readonly List<PerformanceDay> days;
    private TimeSpan cpuTime;
    private DateTimeOffset nextSave;

    public PerformanceJournal(string root)
    {
        path = Path.Combine(root, "performance.json");
        try
        {
            days = File.Exists(path) ? JsonSerializer.Deserialize<List<PerformanceDay>>(File.ReadAllBytes(path)) ?? [] : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { days = []; }
    }

    public void Sample(DateTimeOffset now)
    {
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var memory = process.WorkingSet64;
        Add(day => day with
        {
            CpuSeconds = day.CpuSeconds + Math.Max(0, (cpu - cpuTime).TotalSeconds),
            PeakMemoryBytes = Math.Max(day.PeakMemoryBytes, memory)
        });
        cpuTime = cpu;
        if (now < nextSave) return;
        Save();
        nextSave = now.AddMinutes(5);
    }

    public void Capture(int bytes) => Add(day => day with { LocalImages = day.LocalImages + 1, LocalImageBytes = day.LocalImageBytes + bytes });
    public void Delivery(int count) => Add(day => day with { SentMeasures = day.SentMeasures + count });
    public void ImageDelivery(int bytes) => Add(day => day with { SentImages = day.SentImages + 1, SentImageBytes = day.SentImageBytes + bytes });

    private void Add(Func<PerformanceDay, PerformanceDay> update)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        days.RemoveAll(day => day.Date < date.AddDays(-29));
        int index = days.FindIndex(day => day.Date == date);
        if (index < 0) days.Add(update(new(date)));
        else days[index] = update(days[index]);
    }

    public string Export()
    {
        Save();
        var report = days.OrderBy(day => day.Date).Select(day => FormattableString.Invariant(
            $"{day.Date:yyyy-MM-dd},{day.CpuSeconds:F2},{day.PeakMemoryBytes},{day.LocalImages},{day.LocalImageBytes},{day.SentMeasures},{day.SentImages},{day.SentImageBytes}"));
        return "Date UTC,CPU secondes,Mémoire pic octets,Captures,Captures octets,Mesures envoyées,Images envoyées,Images envoyées octets\r\n" +
            string.Join("\r\n", report);
    }

    private void Save()
    {
        try { LocalData.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(days)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        Save();
        process.Dispose();
    }
}
