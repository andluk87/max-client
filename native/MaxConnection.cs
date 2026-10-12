using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MaxClient;
internal sealed class MaxConnection : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> pending = new();
    private Task? reader, heartbeat;
    private int sequence;
    private int disposed;
    public event Action<int, JsonNode>? Push;
    public event Action? Disconnected;
    public bool Connected => socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested;
    public async Task ConnectAsync(string deviceId, CancellationToken token)
    {
        const string agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MAX-Native-Client/0.2";
        socket.Options.SetRequestHeader("Origin", "https://web.max.ru");
        socket.Options.SetRequestHeader("User-Agent", agent);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await socket.ConnectAsync(new Uri("wss://ws-api.oneme.ru/websocket"), timeout.Token);
        reader = ReceiveAsync();
        await RequestAsync(6, new {
            deviceId,
            userAgent = new {
                deviceType = "WEB", pushDeviceType = "WEBPUSH", locale = "ru", deviceLocale = "ru",
                osVersion = "Windows 10", deviceName = "MAX Native Client", headerUserAgent = agent,
                appVersion = "26.6.17", screen = "1100x780 1.0x", timezone = "Europe/Moscow"
            }
        }, token);
        heartbeat = HeartbeatAsync();
    }
    private int NextSequence() => (Interlocked.Increment(ref sequence) & 0xffff);
    private async Task SendAsync(int seq, int opcode, object payload, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { ver = 11, cmd = 0, seq, opcode, payload });
        await sendLock.WaitAsync(token);
        try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token); }
        finally { sendLock.Release(); }
    }
    public async Task<JsonNode> RequestAsync(int opcode, object payload, CancellationToken token)
    {
        if (!Connected) throw new IOException("Соединение с MAX закрыто.");
        var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seq;
        do { seq = NextSequence(); } while (!pending.TryAdd(seq, completion));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await SendAsync(seq, opcode, payload, timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException("MAX не ответил за 30 секунд."); }
        finally { pending.TryRemove(seq, out _); }
    }
    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                await RequestAsync(1, new { interactive = true }, lifetime.Token);
        }
        catch (Exception) when (!lifetime.IsCancellationRequested) { socket.Abort(); }
        catch (OperationCanceledException) { }
    }
    private async Task ReceiveAsync()
    {
        var buffer = new byte[16384];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                    if (frame.MessageType == WebSocketMessageType.Close) return;
                    if (frame.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Изменился формат протокола MAX.");
                    message.Write(buffer, 0, frame.Count);
                    if (message.Length > 16 * 1024 * 1024) throw new InvalidDataException("Ответ MAX слишком большой.");
                } while (!frame.EndOfMessage);
                var root = JsonNode.Parse(message.ToArray()) ?? throw new InvalidDataException("Пустой ответ MAX.");
                var command = Data.Number(root, "cmd");
                var seq = (int)Data.Number(root, "seq");
                var opcode = (int)Data.Number(root, "opcode");
                var payload = root["payload"]?.DeepClone() ?? new JsonObject();
                if ((command is 1 or 3) && pending.TryGetValue(seq, out var completion))
                {
                    var error = Data.Text(payload, "error");
                    if (command == 3 || error.Length > 0)
                    {
                        // Display only short error identifiers, never tokens, URLs or raw payloads.
                        var safeCode = error.Length is > 0 and <= 100 && error.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                            ? error : "server.error";
                        completion.TrySetException(new MaxException(safeCode));
                    }
                    else completion.TrySetResult(payload);
                }
                else if (command == 0)
                {
                    if (opcode == 1) _ = ReplyPingAsync();
                    else Push?.Invoke(opcode, payload);
                }
            }
        }
        catch (Exception) when (!lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { }
        finally
        {
            socket.Abort();
            foreach (var request in pending.Values) request.TrySetException(new IOException("Соединение с MAX закрыто."));
            if (!lifetime.IsCancellationRequested) Disconnected?.Invoke();
        }
    }
    private async Task ReplyPingAsync()
    {
        try { await RequestAsync(1, new { interactive = true }, lifetime.Token); }
        catch (Exception) { /* Reader/heartbeat owns disconnect reporting. */ }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); socket.Abort();
        if (reader != null) { try { await reader; } catch { } }
        if (heartbeat != null) { try { await heartbeat; } catch { } }
        socket.Dispose();
        // Outstanding request continuations may still be using lifetime and sendLock.
        // They are reclaimed by GC after cancellation, rather than disposed during a race.
    }
}
