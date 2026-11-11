using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AirDesk.Companion;

internal sealed record Settings
{
    public bool Paused { get; init; }
    public bool TrackApps { get; init; } = true;
    public bool TrackSites { get; init; }
    public bool FullLinks { get; init; }
    public string[] ExcludedDomains { get; init; } = [];
    public bool PeriodicCapture { get; init; }
    public bool CaptureOnAppChange { get; init; }
    public bool CaptureOnSiteChange { get; init; }
    public bool SyncCaptures { get; init; }
    public int CaptureLimit { get; init; } = 5_000;
    public int CaptureMinutes { get; init; } = 15;
    public int IdleMinutes { get; init; } = 5;
}

internal sealed record LocalCapture(Guid Id, DateTimeOffset CapturedAt, bool CloudPending);

internal sealed record LocalState
{
    public Guid InstallationId { get; init; } = Guid.NewGuid();
    public Settings Settings { get; init; } = new();
    public Observation? Latest { get; init; }
    public Observation[] Pending { get; init; } = [];
    public LocalCapture[] Captures { get; init; } = [];
    public DateTimeOffset? LastSent { get; init; }
    public ScheduleReceipt? Schedule { get; init; }
    public Dictionary<string, string>? ApplicationIcons { get; init; }
}

internal sealed class LocalData
{
    private readonly string journal;
    private readonly string credential;
    public string Root { get; }
    public string Captures { get; }

    public LocalData(string root)
    {
        Root = root;
        Captures = Path.Combine(root, "Captures");
        Directory.CreateDirectory(Captures);
        journal = Path.Combine(root, "journal.json");
        credential = Path.Combine(root, "association.dat");
    }

    public LocalState Read()
    {
        if (!File.Exists(journal))
        {
            if (File.Exists(credential)) throw new InvalidDataException("Identité locale introuvable.");
            var initial = new LocalState();
            Save(initial);
            return initial;
        }

        var state = JsonSerializer.Deserialize<LocalState>(File.ReadAllBytes(journal), AirDeskClient.Local);
        if (state is null || state.InstallationId == Guid.Empty)
            throw new InvalidDataException("Journal local illisible.");
        return state;
    }

    public void Save(LocalState state) => WriteAtomic(journal, JsonSerializer.SerializeToUtf8Bytes(state, AirDeskClient.Local));

    public Registration? ReadRegistration()
    {
        if (!File.Exists(credential)) return null;
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(credential), null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Registration>(clear, AirDeskClient.Local)
                ?? throw new InvalidDataException("Association locale illisible.");
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void SaveRegistration(Registration registration)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(registration, AirDeskClient.Local);
        try { WriteAtomic(credential, ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void RemoveRegistration() => File.Delete(credential);
    public string CapturePath(Guid id) => Path.Combine(Captures, id.ToString("N") + ".jpg");
    public void SaveCapture(Guid id, byte[] data) => WriteAtomic(CapturePath(id), data);

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
