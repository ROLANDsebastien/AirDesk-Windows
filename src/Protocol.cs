using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirDesk.Companion;

internal sealed record Observation(
    Guid Id, DateTimeOffset CapturedAt, double AwakeSeconds, DateTimeOffset? BootAt, string State,
    double? IdleSeconds, string? AppBundleId, string? AppName, string? BrowserDomain,
    string? BrowserUrl, int? BatteryPercent, bool? BatteryCharging, bool? BatteryExternalPower);

internal sealed record Registration(Guid DeviceId, string CollectorToken);
internal sealed record Account(Guid Id, string? Email);
internal sealed record AccountList(Account[] Accounts);
internal sealed record Delivery(Guid[] AcknowledgedIds);
internal sealed record CaptureDelivery(Guid AcknowledgedId);
internal sealed record ReportDelivery(bool Accepted);

internal sealed record ScheduleReceipt(SchedulePolicy? Policy, int Revision, double ValidUntil)
{
    public bool AllowsCapture(DateTimeOffset now)
    {
        if (Policy is null) return true;
        if (!double.IsFinite(ValidUntil) || now.ToUnixTimeMilliseconds() / 1000d >= ValidUntil) return false;
        if (!Policy.Enabled) return true;

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(Policy.TimeZone, out var zone)) return false;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var minute = local.Hour * 60 + local.Minute;
        var date = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var exception = Policy.Exceptions?.FirstOrDefault(item => item.Date == date);
        if (exception is not null)
            return !exception.SuspendAllDay && !exception.Windows.Any(window => window.Contains(minute));

        return !Policy.Windows.Any(window => window.Weekday == (int)local.DayOfWeek + 1 && window.Contains(minute));
    }
}

internal sealed record SchedulePolicy(bool Enabled, string TimeZone, WeeklyWindow[] Windows, DayException[]? Exceptions);
internal sealed record DayException(string Date, bool SuspendAllDay, MinuteWindow[] Windows);
internal record MinuteWindow(int StartMinute, int EndMinute)
{
    public bool Contains(int minute) => minute >= StartMinute && minute < EndMinute;
}
internal sealed record WeeklyWindow(int Weekday, int StartMinute, int EndMinute) : MinuteWindow(StartMinute, EndMinute);

internal sealed class ServerException(int status, string code, TimeSpan? retryAfter) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class AirDeskClient : IDisposable
{
    internal static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    internal static readonly JsonSerializerOptions Local = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient http;
    private readonly Uri endpoint;

    public AirDeskClient(HttpMessageHandler? handler = null, Uri? endpoint = null)
    {
        this.endpoint = endpoint ?? new Uri(CloudConfiguration.Endpoint);
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
    }

    public Task<Registration> Pair(Guid installation, string code, string name, CancellationToken cancel) =>
        Post<Registration>(new { action = "redeem-code", installationId = installation, code, displayName = name }, null, cancel);

    public Task<AccountList> Accounts(Guid installation, string token, string? code, Guid? remove, CancellationToken cancel) =>
        Post<AccountList>(new
        {
            action = code is not null ? "add-account" : remove is not null ? "remove-account" : "list-accounts",
            installationId = installation,
            code,
            userId = remove
        }, token, cancel);

    public Task<ScheduleReceipt> Schedule(Guid installation, string token, int? revision, CancellationToken cancel) =>
        Post<ScheduleReceipt>(new
        {
            action = "collector-schedule", installationId = installation,
            appliedRevision = revision, supportsScheduleExceptions = true
        }, token, cancel, Local);

    public async Task<HashSet<Guid>> Upload(Guid installation, string token, Observation[] observations, CancellationToken cancel)
    {
        var response = await Post<Delivery>(new { action = "ingest", installationId = installation, observations }, token, cancel);
        var accepted = response.AcknowledgedIds.ToHashSet();
        if (accepted.Count != response.AcknowledgedIds.Length || !accepted.IsSubsetOf(observations.Select(item => item.Id)))
            throw new InvalidDataException("Accusé de réception AirDesk invalide.");
        return accepted;
    }

    public async Task Report(Guid installation, string token, object inventory, CancellationToken cancel)
    {
        var report = JsonSerializer.SerializeToElement(new { inventory, updates = Array.Empty<object>() }, Local);
        var result = await Post<ReportDelivery>(new { action = "report-system", installationId = installation, report }, token, cancel);
        if (!result.Accepted) throw new InvalidDataException("Inventaire refusé.");
    }

    public async Task UploadCapture(Guid installation, string token, LocalCapture capture, byte[] data, CancellationToken cancel)
    {
        using var request = Request(token);
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType = new("image/jpeg");
        request.Headers.Add("x-airdesk-action", "upload-capture");
        request.Headers.Add("x-airdesk-installation", installation.ToString());
        request.Headers.Add("x-airdesk-capture-id", capture.Id.ToString());
        request.Headers.Add("x-airdesk-captured-at", capture.CapturedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));
        var result = await Send<CaptureDelivery>(request, Wire, cancel);
        if (result.AcknowledgedId != capture.Id) throw new InvalidDataException("Capture non confirmée.");
    }

    private async Task<T> Post<T>(object body, string? token, CancellationToken cancel, JsonSerializerOptions? options = null)
    {
        using var request = Request(token);
        request.Content = JsonContent.Create(body, options: Wire);
        return await Send<T>(request, options ?? Wire, cancel);
    }

    private HttpRequestMessage Request(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("apikey", CloudConfiguration.PublishableKey);
        if (token is not null) request.Headers.Add("x-airdesk-token", token);
        return request;
    }

    private async Task<T> Send<T>(HttpRequestMessage request, JsonSerializerOptions options, CancellationToken cancel)
    {
        using var response = await http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            string code = "request_failed";
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancel);
                if (error.ValueKind == JsonValueKind.Object &&
                    (error.TryGetProperty("error_code", out var value) || error.TryGetProperty("error", out value)) &&
                    value.ValueKind == JsonValueKind.String)
                    code = value.GetString() ?? code;
            }
            catch (JsonException) { }

            var retry = response.Headers.RetryAfter;
            throw new ServerException((int)response.StatusCode, code, retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow));
        }

        return await response.Content.ReadFromJsonAsync<T>(options, cancel)
            ?? throw new InvalidDataException("Réponse AirDesk vide.");
    }

    public void Dispose() => http.Dispose();
}
