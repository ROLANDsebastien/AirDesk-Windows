using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirDesk.Companion;

internal sealed class Companion : IDisposable
{
    private readonly LocalData data;
    private readonly PerformanceJournal performance;
    private readonly AirDeskClient api = new();
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? captureUpload;
    private readonly Retry activityRetry = new();
    private readonly Retry captureRetry = new();
    private readonly Retry scheduleRetry = new();
    private readonly Retry reportRetry = new();
    private Registration? registration;
    private bool credentialUnavailable;
    private bool credentialRejected;
    private bool networkBusy;
    private bool sleeping;
    private bool locked;
    private bool displayOff;
    private long privacyGeneration;
    private DateTimeOffset? bootAt;
    private DateTimeOffset nextSample;
    private DateTimeOffset nextCapture;
    private DateTimeOffset lastBrowserContact;
    private DateTimeOffset lastWebsite;
    private (string Value, IntPtr Window, long Generation, DateTimeOffset Expires, bool MatchTitle)? browserGrant;
    private DateTimeOffset nextCleanup;
    private DateTimeOffset nextSchedule;
    private DateTimeOffset nextInventory;
    private DateTimeOffset lastEventCapture;
    private Foreground? previousApp;
    private Foreground? websiteApp;
    private Website? website;
    private Website? lastSettledSite;

    public LocalState State { get; private set; }
    public bool Busy => networkBusy;
    public bool HasLocalAssociation => registration is not null || credentialUnavailable;
    public bool Linked => registration is not null && !credentialRejected;
    public string Root => data.Root;
    public Account[] Accounts { get; private set; } = [];
    public bool AccountsLoaded { get; private set; }
    public string? Error { get; private set; }
    public string? SyncStatus { get; private set; }
    public string? CaptureStatus { get; private set; }
    public string? AccountStatus { get; private set; }
    public string SiteStatus => DateTimeOffset.UtcNow - lastBrowserContact > TimeSpan.FromSeconds(6)
        ? "Extension Edge en attente de connexion."
        : website is not null ? "Extension connectée · site actif reconnu."
        : "Extension connectée · en attente d’un onglet web actif.";
    public bool CaptureAllowed => Error is null && !State.Settings.Paused && SessionAvailable() &&
        PolicyAllows(DateTimeOffset.UtcNow);
    public event Action? Changed;

    public Companion(LocalData data)
    {
        this.data = data;
        performance = new(data.Root);
        State = data.Read();
        ResetCapture();
        try { registration = data.ReadRegistration(); }
        catch (Exception error) when (error is CryptographicException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            credentialUnavailable = true;
            AccountStatus = "Identifiant Windows inaccessible. Saisissez un nouveau code d’un compte déjà associé.";
        }
        if (registration is not null && State.LastSent is not null) SyncStatus = "Synchronisation prête.";
    }

    public void SettingsChanged(Settings settings)
    {
        var previous = State.Settings;
        if (previous == settings || !Save(State with { Settings = settings })) return;
        if (!settings.SyncCaptures) captureUpload?.Cancel();
        if (settings.CaptureLimit != previous.CaptureLimit) PruneCaptures(DateTimeOffset.UtcNow);
        ResetCapture();
        if (settings.Paused != previous.Paused || settings.IdleMinutes != previous.IdleMinutes) nextSample = default;
        Changed?.Invoke();
    }

    public void SetSession(bool? isLocked = null, bool? isSleeping = null, bool? screenOff = null)
    {
        if (isLocked is { } session) locked = session;
        if (isSleeping is { } sleep) sleeping = sleep;
        if (screenOff is { } screen) displayOff = screen;
        ResetCapture();
        nextSample = default;
    }

    private bool SessionAvailable() => !locked && !sleeping && !displayOff && WindowsTelemetry.IsInputDesktop();

    private bool PolicyAllows(DateTimeOffset now) =>
        State.Schedule?.AllowsCapture(now) ?? (registration is null && !credentialUnavailable && !credentialRejected);

    private void ResetCapture()
    {
        privacyGeneration++;
        previousApp = null;
        websiteApp = null;
        website = lastSettledSite = null;
        browserGrant = null;
        nextCapture = DateTimeOffset.UtcNow.AddMinutes(State.Settings.CaptureMinutes);
    }

    public void Tick()
    {
        if (Error is not null || shutdown.IsCancellationRequested) return;
        var now = DateTimeOffset.UtcNow;
        var settings = State.Settings;
        var available = SessionAvailable();
        var idle = available && !settings.Paused ? WindowsTelemetry.IdleSeconds() : null;
        bool active = idle < settings.IdleMinutes * 60;
        bool sensitive = active && PolicyAllows(now);
        bool needForeground = active && (settings.TrackApps || sensitive &&
            (settings.TrackSites || settings.CaptureOnAppChange || settings.CaptureOnSiteChange));
        var app = needForeground ? WindowsTelemetry.ReadForeground() : null;

        if (!sensitive || app?.Window != websiteApp?.Window)
        {
            websiteApp = app;
            website = lastSettledSite = null;
        }
        if (now - lastWebsite > TimeSpan.FromSeconds(5)) website = null;
        if (now - lastWebsite > TimeSpan.FromSeconds(12)) lastSettledSite = null;

        if (now >= nextCleanup)
        {
            nextCleanup = now.AddMinutes(5);
            PruneCaptures(now);
        }

        if (now >= nextSample)
        {
            nextSample = now.AddSeconds(15);
            performance.Sample(now);
            var state = settings.Paused ? "paused" : !available ? "asleep" : idle is null ? "unknown" : active ? "active" : "idle";
            Record(now, state, idle, settings.TrackApps && active ? app : null, settings.TrackSites && sensitive ? website : null);
        }

        if (now >= nextCapture)
        {
            nextCapture = now.AddMinutes(settings.CaptureMinutes);
            if (settings.PeriodicCapture && sensitive) TakeCapture(automatic: true);
        }

        if (settings.CaptureOnAppChange && sensitive && previousApp is not null && app is not null &&
            previousApp.Id != app.Id && now >= lastEventCapture.AddSeconds(30))
        {
            lastEventCapture = now;
            TakeCapture(automatic: true);
        }
        previousApp = sensitive ? app : null;

        if (Linked && !networkBusy && !sleeping && Error is null && NetworkDue(now))
            _ = Synchronize();
        Changed?.Invoke();
    }

    public BrowserReply ReceiveBrowser(BrowserRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        lastBrowserContact = now;
        var settings = State.Settings;
        var status = new BrowserReply("status", true);
        if (!settings.TrackSites && !settings.CaptureOnSiteChange) return status with { Reason = "tracking_disabled" };
        if (!CaptureAllowed || !(WindowsTelemetry.IdleSeconds() < settings.IdleMinutes * 60))
            return status with { Reason = "session_unavailable" };

        var app = WindowsTelemetry.ReadForeground();
        if (app is null || !WindowsTelemetry.IsSupportedBrowser(app)) return status with { Reason = "browser_not_in_foreground" };
        if (request.Type == "check")
        {
            if (!request.NormalWindow) return status with { Reason = "window_unavailable" };
            if (!string.Equals(request.Browser, app.Process, StringComparison.OrdinalIgnoreCase))
                return status with { Reason = "browser_mismatch" };
            var caption = request.Focused ? null : WindowsTelemetry.ForegroundBrowserCaption(app);
            if (!request.Focused && caption is null) return status with { Reason = "window_title_unavailable" };
            var grant = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            browserGrant = (grant, app.Window, privacyGeneration, now.AddSeconds(2), caption is not null);
            return new("permission", true, grant, settings.FullLinks, caption);
        }

        if (request.Type is not ("site" or "pending") || browserGrant is not { } issued || request.Grant != issued.Value)
            return status with { Reason = "grant_missing" };
        browserGrant = null;
        if (issued.Window != app.Window || issued.Generation != privacyGeneration || now >= issued.Expires)
            return status with { Reason = "grant_expired" };

        var previous = websiteApp?.Window == app.Window && now - lastWebsite <= TimeSpan.FromSeconds(12) ? lastSettledSite : null;
        websiteApp = app;
        if (request.Type == "pending")
        {
            website = null;
            return status with { Reason = "navigation_pending" };
        }

        if (issued.MatchTitle && request.Url is not null && !WindowsTelemetry.MatchesBrowserTitle(app, request.Title))
        {
            website = lastSettledSite = null;
            return status with { Reason = "window_title_mismatch" };
        }
        lastWebsite = now;
        website = lastSettledSite = WindowsTelemetry.ParseWebsite(request.Url, settings.FullLinks, settings.ExcludedDomains);
        if (settings.CaptureOnSiteChange && previous is not null && website is not null && previous != website &&
            now >= lastEventCapture.AddSeconds(30))
        {
            lastEventCapture = now;
            TakeCapture(automatic: true);
        }
        return status with { Reason = website is null ? "site_ignored" : "site_recognized" };
    }

    private void Record(DateTimeOffset now, string state, double? idle, Foreground? app, Website? site)
    {
        var uptime = WindowsTelemetry.AwakeSeconds();
        if (uptime is null) return;
        var battery = state == "paused" ? null : WindowsTelemetry.ReadBattery();
        bootAt ??= ResolveBootAt(State.Latest, WindowsTelemetry.BootAt, uptime.Value);
        var observation = new Observation(Guid.NewGuid(), now, uptime.Value, bootAt <= now ? bootAt : null, state, idle,
            app?.Id, app?.Name, site?.Domain, site?.Url, battery?.Percent, battery?.Charging, battery?.ExternalPower);
        var pending = State.Pending.Where(item => item.CapturedAt >= now.AddDays(-29)).Append(observation).TakeLast(5_760).ToArray();
        var linkBytes = pending.Sum(LinkBytes);
        int excess = 0;
        while (linkBytes > 2_000_000) linkBytes -= LinkBytes(pending[excess++]);
        Save(State with { Latest = observation, Pending = pending[excess..] });
    }

    internal static DateTimeOffset ResolveBootAt(Observation? previous, DateTimeOffset estimate, double awakeSeconds)
    {
        // Keep the same boot timestamp across a companion restart. The supervisor compares it exactly.
        if (previous?.BootAt is { } saved && awakeSeconds >= previous.AwakeSeconds &&
            Math.Abs((estimate - saved).TotalSeconds) < 10) return saved;
        return estimate;
    }

    private static int LinkBytes(Observation item) => Encoding.UTF8.GetByteCount(item.BrowserUrl ?? "");

    public void TakeCapture(bool automatic = false)
    {
        if (!CaptureAllowed)
        {
            if (!automatic) CaptureStatus = "Capture indisponible pendant la pause, le verrouillage ou une plage de confidentialité.";
            Changed?.Invoke();
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var item = new LocalCapture(Guid.NewGuid(), now, State.Settings.SyncCaptures);
        try
        {
            var image = WindowsTelemetry.Screenshot();
            if (!CaptureAllowed) return;
            data.SaveCapture(item.Id, image);
            if (!Save(State with { Captures = State.Captures.Append(item).ToArray() })) File.Delete(data.CapturePath(item.Id));
            else
            {
                performance.Capture(image.Length);
                CaptureStatus = $"Dernière capture locale à {now.LocalDateTime:t}.";
            }
            PruneCaptures(now);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            CaptureStatus = "Impossible d’enregistrer la capture. Vérifiez la session et l’espace disque.";
        }
        Changed?.Invoke();
    }

    public void DeleteLocalCaptures()
    {
        captureUpload?.Cancel();
        PruneCaptures(DateTimeOffset.UtcNow, all: true);
        Changed?.Invoke();
    }

    public void DeleteLocalCaptures(HashSet<Guid> selected)
    {
        captureUpload?.Cancel();
        try
        {
            foreach (var item in State.Captures.Where(item => selected.Contains(item.Id)))
                File.Delete(data.CapturePath(item.Id));
            if (Save(State with { Captures = State.Captures.Where(item => !selected.Contains(item.Id)).ToArray() }))
                CaptureStatus = "Sélection supprimée de ce PC.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CaptureStatus = "Suppression incomplète. Réessayez.";
        }
        Changed?.Invoke();
    }

    private void PruneCaptures(DateTimeOffset now, bool all = false)
    {
        try
        {
            var retained = all ? [] : State.Captures.Where(item => item.CapturedAt > now.AddDays(-7)).TakeLast(Math.Clamp(State.Settings.CaptureLimit, 500, 5000)).ToArray();
            var keep = retained.Select(item => item.Id.ToString("N") + ".jpg").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(data.Captures))
                if ((Path.GetExtension(path) is ".jpg" or ".tmp") && !keep.Contains(Path.GetFileName(path))) File.Delete(path);
            if (retained.Length != State.Captures.Length) Save(State with { Captures = retained });
            if (all && Error is null) CaptureStatus = "Captures locales supprimées.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CaptureStatus = "Nettoyage incomplet. Les fichiers seront revérifiés.";
        }
    }

    public async Task PairOrAdd(string input)
    {
        var code = input.Trim().Replace("-", "").ToLowerInvariant();
        if (!Regex.IsMatch(code, "^[0-9a-f]{32}$"))
        {
            AccountStatus = "Saisissez le code d’association à 32 caractères généré dans AirDesk.";
            Changed?.Invoke();
            return;
        }
        if (Linked) { await ManageAccounts(code, null); return; }
        if (networkBusy || Error is not null) return;
        networkBusy = true;
        AccountStatus = "Association en cours…";
        Changed?.Invoke();
        try
        {
            var name = Environment.MachineName + " · Windows";
            var result = await api.Pair(State.InstallationId, code, name, shutdown.Token);
            if (result.DeviceId == Guid.Empty || !Regex.IsMatch(result.CollectorToken ?? "", "^[0-9a-f]{64}$"))
                throw new InvalidDataException("Association AirDesk invalide.");
            data.SaveRegistration(result);
            registration = result;
            credentialUnavailable = credentialRejected = false;
            AccountsLoaded = false;
            Accounts = [];
            Save(State with { LastSent = null, Schedule = null });
            nextSchedule = nextInventory = nextSample = default;
            foreach (var retry in new[] { activityRetry, captureRetry, scheduleRetry, reportRetry }) retry.Reset();
            SyncStatus = "En attente du premier envoi confirmé.";
            AccountStatus = "Code accepté.";
            ResetCapture();
        }
        catch (Exception error) when (!shutdown.IsCancellationRequested)
        {
            AccountStatus = AccountMessage(error);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally { networkBusy = false; Changed?.Invoke(); }
        Tick();
    }

    public async Task ManageAccounts(string? code = null, Guid? remove = null)
    {
        if (!Linked || networkBusy || registration is null) return;
        networkBusy = true;
        AccountStatus = "Lecture des comptes…";
        Changed?.Invoke();
        try
        {
            var result = await api.Accounts(State.InstallationId, registration.CollectorToken, code, remove, shutdown.Token);
            Accounts = result.Accounts;
            AccountsLoaded = true;
            AccountStatus = Accounts.Length == 0 ? "Aucun compte associé." : null;
        }
        catch (Exception error) when (!shutdown.IsCancellationRequested)
        {
            if (error is ServerException { Status: 401 }) RejectCredential();
            AccountStatus = AccountMessage(error);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally { networkBusy = false; Changed?.Invoke(); }
    }

    public void Disconnect()
    {
        if (networkBusy) return;
        try
        {
            data.RemoveRegistration();
            registration = null;
            credentialUnavailable = credentialRejected = false;
            AccountsLoaded = false;
            Accounts = [];
            AccountStatus = SyncStatus = null;
            Save(State with { LastSent = null, Schedule = null });
            ResetCapture();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AccountStatus = "Impossible d’effacer l’association Windows.";
        }
        Changed?.Invoke();
    }

    public void RetryNow()
    {
        activityRetry.RequestNow();
        captureRetry.RequestNow();
        scheduleRetry.RequestNow();
        reportRetry.RequestNow();
        Tick();
    }

    private bool NetworkDue(DateTimeOffset now) =>
        (now >= nextSchedule && scheduleRetry.Due(now)) ||
        (State.Pending.Length > 0 && activityRetry.Due(now)) ||
        (State.Settings.SyncCaptures && State.Captures.Any(item => item.CloudPending) && captureRetry.Due(now)) ||
        (!State.Settings.Paused && now >= nextInventory && reportRetry.Due(now));

    private async Task Synchronize()
    {
        if (registration is null) return;
        networkBusy = true;
        var token = registration.CollectorToken;
        var id = State.InstallationId;
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (now >= nextSchedule && scheduleRetry.Due(now))
                await Attempt(scheduleRetry, async () =>
                {
                    var receipt = await api.Schedule(id, token, State.Schedule?.Revision, shutdown.Token);
                    if (receipt.Revision < 0 || !double.IsFinite(receipt.ValidUntil) ||
                        receipt.ValidUntil <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                        throw new InvalidDataException("Planning AirDesk invalide.");
                    if (Save(State with { Schedule = receipt }))
                    {
                        ResetCaptureIfPolicyChanged(receipt);
                        nextSchedule = DateTimeOffset.UtcNow.AddMinutes(1);
                    }
                }, "Planning indisponible.");

            if (Linked && Error is null && State.Pending.Length > 0 && activityRetry.Due(now))
                await Attempt(activityRetry, async () =>
                {
                    var batch = Batch(State.Pending);
                    var accepted = await api.Upload(id, token, batch, shutdown.Token);
                    if (accepted.Count == 0) throw new InvalidDataException("Aucune mesure confirmée.");
                    if (Save(State with { LastSent = DateTimeOffset.UtcNow, Pending = State.Pending.Where(item => !accepted.Contains(item.Id)).ToArray() }))
                    {
                        performance.Delivery(accepted.Count);
                        SyncStatus = "Dernier envoi confirmé.";
                    }
                }, "Mesures en attente. AirDesk réessaiera automatiquement.", activity: true);

            if (Linked && Error is null && State.Settings.SyncCaptures && captureRetry.Due(now) &&
                State.Captures.FirstOrDefault(item => item.CloudPending) is { } capture)
                await Attempt(captureRetry, async () =>
                {
                    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    captureUpload = cancel;
                    try
                    {
                        var path = data.CapturePath(capture.Id);
                        if (File.Exists(path))
                        {
                            try
                            {
                                var image = File.ReadAllBytes(path);
                                await api.UploadCapture(id, token, capture, image, cancel.Token);
                                performance.ImageDelivery(image.Length);
                            }
                            catch (ServerException error) when (error.Code == "capture_deleted") { }
                        }
                        Save(State with { Captures = State.Captures.Select(item =>
                            item.Id == capture.Id ? item with { CloudPending = false } : item).ToArray() });
                    }
                    finally { captureUpload = null; }
                }, "Image en attente d’envoi.");

            if (Linked && Error is null && !State.Settings.Paused && now >= nextInventory && reportRetry.Due(now))
                await Attempt(reportRetry, async () =>
                {
                    var inventory = await Task.Run(WindowsTelemetry.Inventory, shutdown.Token);
                    if (!State.Settings.Paused)
                    {
                        await api.Report(id, token, inventory, shutdown.Token);
                        nextInventory = DateTimeOffset.UtcNow.AddHours(24);
                    }
                }, "Inventaire indisponible.");
        }
        finally { networkBusy = false; Changed?.Invoke(); }
    }

    private bool lastPolicyAllowed = true;
    private void ResetCaptureIfPolicyChanged(ScheduleReceipt receipt)
    {
        var allowed = receipt.AllowsCapture(DateTimeOffset.UtcNow);
        if (allowed != lastPolicyAllowed) ResetCapture();
        lastPolicyAllowed = allowed;
    }

    internal static Observation[] Batch(Observation[] observations)
    {
        var batch = new List<Observation>(100);
        var bytes = 0;
        foreach (var item in observations.Take(100))
        {
            bytes += JsonSerializer.SerializeToUtf8Bytes(item, AirDeskClient.Wire).Length + 1;
            if (bytes > 70_000) break;
            batch.Add(item);
        }
        return batch.ToArray();
    }

    private async Task Attempt(Retry retry, Func<Task> work, string message, bool activity = false)
    {
        try { await work(); retry.Reset(); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested || (ReferenceEquals(retry, captureRetry) && !State.Settings.SyncCaptures)) { }
        catch (ServerException error) when (error.Status == 401)
        {
            RejectCredential();
        }
        catch (Exception error)
        {
            retry.Fail(error is ServerException server
                ? server.RetryAfter ?? (server.Status == 429 ? TimeSpan.FromMinutes(1) : null) : null);
            if (activity) SyncStatus = message;
            else if (ReferenceEquals(retry, captureRetry)) CaptureStatus = message;
        }
    }

    private void RejectCredential()
    {
        credentialRejected = true;
        AccountsLoaded = false;
        Accounts = [];
        SyncStatus = "Envoi suspendu. L’association doit être rétablie.";
        AccountStatus = "Créez un nouveau code avec un compte déjà associé à ce PC.";
        ResetCapture();
    }

    private static string AccountMessage(Exception error) => error switch
    {
        ServerException { Code: "invalid_code" } => "Code expiré, déjà utilisé ou invalide.",
        ServerException { Code: "collector_required" } => "Utilisez un code d’un compte déjà associé à ce PC.",
        ServerException { Status: 401 } => "Association à rétablir avec le code d’un compte déjà associé.",
        ServerException { Status: 429 } => "Trop de tentatives. Patientez une minute.",
        CryptographicException or IOException or UnauthorizedAccessException => "Impossible d’enregistrer l’association Windows.",
        _ => "AirDesk n’a pas confirmé l’opération. Vérifiez la connexion et le code."
    };

    private bool Save(LocalState candidate)
    {
        if (Error is not null) return false;
        try { data.Save(candidate); State = candidate; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Error = "Journal local inaccessible. Collecte suspendue. Relancez AirDesk après avoir vérifié le disque.";
            Changed?.Invoke();
            return false;
        }
    }

    public string PerformanceReport() => performance.Export();

    public async Task<string> RefreshInventory()
    {
        var inventory = await Task.Run(WindowsTelemetry.Inventory, shutdown.Token);
        nextInventory = default;
        reportRetry.RequestNow();
        return $"{inventory.OsVersion} · inventaire du {inventory.CollectedAt.LocalDateTime:g}\n" +
            string.Join("\n", inventory.Applications.Select(app => app.Name + " · " + (app.Version ?? "version inconnue")));
    }

    public void Dispose()
    {
        shutdown.Cancel();
        api.Dispose();
        performance.Dispose();
    }

    private sealed class Retry
    {
        private int failures;
        private DateTimeOffset notBefore;
        private DateTimeOffset serverNotBefore;
        public bool Due(DateTimeOffset now) => now >= notBefore;
        public void Reset() { failures = 0; notBefore = serverNotBefore = default; }
        public void RequestNow() => notBefore = serverNotBefore;
        public void Fail(TimeSpan? retryAfter)
        {
            var now = DateTimeOffset.UtcNow;
            failures = Math.Min(7, failures + 1);
            serverNotBefore = now + (retryAfter ?? TimeSpan.Zero);
            var backoff = now.AddSeconds(Math.Min(300, 5 * (1 << (failures - 1))) + Random.Shared.NextDouble());
            notBefore = serverNotBefore > backoff ? serverNotBefore : backoff;
        }
    }
}
