using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace AirDesk.Companion;

internal sealed record BrowserRequest(string Type, bool Focused, string? Grant, string? Url, string? Browser, bool NormalWindow, string? Title);
internal sealed record BrowserReply(string Type, bool Connected, string? Grant = null, bool FullLinks = false, bool MatchTitle = false, string? Reason = null);

internal static class BrowserBridge
{
    private static readonly string PipeName = "AirDeskCompanion.Browser." + System.Diagnostics.Process.GetCurrentProcess().SessionId;
    private const int MaximumMessage = 64 * 1024;

    public static bool IsHostInvocation(string[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith("chrome-extension://", StringComparison.Ordinal)) return false;
        // This manifest is shipped beside the executable. Edge and Chrome independently check the same origin.
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "browser-host.json")));
            return manifest.RootElement.GetProperty("allowed_origins").EnumerateArray().Any(item => item.GetString() == args[0]);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or KeyNotFoundException) { return false; }
    }

    public static async Task Serve(Func<BrowserRequest, CancellationToken, Task<BrowserReply>> handle, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var request = await Read<BrowserRequest>(pipe, timeout.Token).ConfigureAwait(false);
                if (request is null) continue;
                var reply = await handle(request, timeout.Token).ConfigureAwait(false);
                await Write(pipe, reply, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException or InvalidOperationException) { }
        }
    }

    public static async Task RunHost()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        try
        {
            var disconnected = 0;
            while (await Read<BrowserRequest>(input, default).ConfigureAwait(false) is { } request)
            {
                var reply = new BrowserReply("status", false);
                try
                {
                    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
                    await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await Write(pipe, request, timeout.Token).ConfigureAwait(false);
                    reply = await Read<BrowserReply>(pipe, timeout.Token).ConfigureAwait(false) ?? reply;
                }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }

                await Write(output, reply, default).ConfigureAwait(false);
                disconnected = reply.Connected ? 0 : disconnected + 1;
                if (disconnected >= 2) return;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { }
    }

    private static async Task<T?> Read<T>(Stream stream, CancellationToken cancel) where T : class
    {
        var header = new byte[sizeof(int)];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancel).ConfigureAwait(false) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancel).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumMessage) throw new InvalidDataException("Message navigateur invalide.");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancel).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(buffer, AirDeskClient.Local);
    }

    private static async Task Write<T>(Stream stream, T value, CancellationToken cancel)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AirDeskClient.Local);
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancel).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }
}
