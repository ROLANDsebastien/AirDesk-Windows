using System.Net;
using System.Text.Json;
using AirDesk.Companion;

var id = Guid.NewGuid();
var device = Guid.NewGuid();
var token = new string('a', 64);
var now = DateTimeOffset.Parse("2026-11-08T13:00:00+00:00");
using var client = new AirDeskClient(new Handler(async request =>
{
    var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
    Check(request.Headers.Contains("apikey"), "Missing key");
    Check(body.GetProperty("installation_id").GetGuid() == device, "Wrong installation");
    return body.GetProperty("action").GetString() switch
    {
        "redeem-code" when !request.Headers.Contains("x-airdesk-token") => $$"""{"device_id":"{{device}}","collector_token":"{{token}}"}""",
        "collector-schedule" when request.Headers.GetValues("x-airdesk-token").Single() == token => """
          {"policy":{"enabled":true,"timeZone":"Europe/Brussels","windows":[{"weekday":1,"startMinute":810,"endMinute":900}],"exceptions":[{"date":"2026-11-15","suspendAllDay":false,"windows":[]}]},"revision":2,"validUntil":1800000000}
          """,
        "ingest" when body.GetProperty("observations")[0].GetProperty("app_bundle_id").GetString() == "windows.msedge"
            && !body.GetProperty("observations")[0].TryGetProperty("browser_url", out _) => $$"""{"acknowledged_ids":["{{id}}"]}""",
        "report-system" when body.GetProperty("report").GetProperty("inventory").GetProperty("macOSVersion").GetString() == "Windows"
            && body.GetProperty("report").GetProperty("inventory").GetProperty("applications")[0].TryGetProperty("bundleID", out _) => """{"accepted":true}""",
        _ => throw new Exception("Unexpected protocol payload")
    };
}));
var submitted = new Observation(id, now, 400, now.AddSeconds(-500), "active", 3, "windows.msedge", "Edge", "example.com", null, 55, false, true);
using (var bad = new AirDeskClient(new Handler(_ => Task.FromResult($$"""{"acknowledged_ids":["{{Guid.NewGuid()}}"]}"""))))
{
    try
    {
        await bad.Upload(device, token, [submitted], default);
        throw new Exception("Unexpected acknowledgement accepted");
    }
    catch (InvalidDataException) { }
}
var linked = await client.Pair(device, new string('b',32), "Test Windows", default);
Check(linked.DeviceId == device && linked.CollectorToken == token, "Pairing parsing");
var policy = await client.Schedule(device, token, null, default);
Check(!policy.AllowsCapture(now), "Weekly privacy restriction");
Check(policy.AllowsCapture(now.AddDays(7)), "Exception overrides weekly window");
Check(!policy.AllowsCapture(DateTimeOffset.FromUnixTimeSeconds(1800000000)), "Expired schedule");
Check((await client.Upload(device, token, [submitted], default)).SetEquals([id]), "Ingest confirmation");
await client.Report(device, token, new { macOSVersion = "Windows", applications = new[] { new { BundleID = "windows.test" } } }, default);
Console.WriteLine("Pairing, telemetry acknowledgement, schedule exceptions, expiry and inventory JSON passed.");

static void Check(bool passed, string message) { if (!passed) throw new Exception(message); }

sealed class Handler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
        new(HttpStatusCode.OK) { Content = new StringContent(await respond(request)) };
}
namespace AirDesk.Companion { internal sealed record LocalCapture(Guid Id, DateTimeOffset CapturedAt); }
