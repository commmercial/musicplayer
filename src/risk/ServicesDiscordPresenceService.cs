using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Vybe.App.Services;

/// <summary>Publishes listening activity to the Discord desktop client over local RPC.</summary>
public sealed class DiscordPresenceService : IDisposable
{
    NamedPipeClientStream? _pipe;
    readonly SemaphoreSlim _gate = new(1, 1);
    string _applicationId = "";

    public bool IsAuthorized => _pipe?.IsConnected == true;

    // Kept as the existing UI entry point. Rich Presence uses the local Discord client
    // connection directly, so the user does not need an OAuth consent/token flow.
    public async Task AuthorizeAsync(string applicationId) => await ConnectAsync(applicationId);

    public async Task ConnectAsync(string applicationId)
    {
        if (!ulong.TryParse(applicationId, out _))
            throw new InvalidOperationException("The Discord application ID is invalid.");

        await _gate.WaitAsync();
        try { await EnsureConnectedAsync(applicationId); }
        finally { _gate.Release(); }
    }

    public async Task UpdateAsync(string applicationId, string title, string artist, string artworkUrl, TimeSpan duration, TimeSpan position)
    {
        if (!ulong.TryParse(applicationId, out _)) throw new InvalidOperationException("Invalid Discord application ID.");
        await _gate.WaitAsync();
        try
        {
            await EnsureConnectedAsync(applicationId);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var startedAt = Math.Max(0, now - Math.Max(0, (long)position.TotalSeconds));
            var timestamps = new Dictionary<string, long> { ["start"] = startedAt };
            if (duration > TimeSpan.Zero) timestamps["end"] = startedAt + (long)duration.TotalSeconds;

            var activity = new Dictionary<string, object?>
            {
                ["type"] = 2, // Listening
                ["details"] = Clip(title, 128),
                ["state"] = Clip(string.IsNullOrWhiteSpace(artist) ? "Listening to music" : artist, 128),
                ["timestamps"] = timestamps
            };
            if (Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artwork) &&
                (artwork.Scheme == Uri.UriSchemeHttps || artwork.Scheme == Uri.UriSchemeHttp))
                activity["assets"] = new Dictionary<string, string>
                {
                    ["large_image"] = Clip(artwork.AbsoluteUri, 300)
                };

            using var response = await SendCommandAsync("SET_ACTIVITY", new
            {
                pid = Environment.ProcessId,
                activity
            });
        }
        catch
        {
            await DisconnectPipeAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_pipe?.IsConnected == true)
            {
                using var response = await SendCommandAsync("SET_ACTIVITY", new
                {
                    pid = Environment.ProcessId,
                    activity = (object?)null
                });
            }
        }
        catch { await DisconnectPipeAsync(); }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_pipe?.IsConnected == true)
            {
                try
                {
                    using var response = await SendCommandAsync("SET_ACTIVITY", new
                    {
                        pid = Environment.ProcessId,
                        activity = (object?)null
                    });
                }
                catch { }
                finally { await DisconnectPipeAsync(); }
            }
            else await DisconnectPipeAsync();
        }
        finally { _gate.Release(); }
    }

    async Task EnsureConnectedAsync(string appId)
    {
        if (_pipe?.IsConnected == true && _applicationId == appId) return;
        await DisconnectPipeAsync();
        Exception? last = null;
        for (var i = 0; i < 10; i++)
        {
            var candidate = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync(500);
                _pipe = candidate;
                _applicationId = appId;
                await WriteFrameAsync(0, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = appId }));
                var ready = await ReadFrameAsync(candidate, TimeSpan.FromSeconds(3));
                using var document = JsonDocument.Parse(ready.Payload);
                if (document.RootElement.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                    throw RpcError(document.RootElement);
                if (!document.RootElement.TryGetProperty("evt", out evt) || evt.GetString() != "READY")
                    throw new IOException("Discord did not finish opening its Rich Presence connection.");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                candidate.Dispose();
                _pipe = null;
            }
        }
        throw new IOException("Could not connect to Discord Desktop. Open the Discord app and try again.", last);
    }

    async Task<JsonDocument> SendCommandAsync(string command, object args)
    {
        var pipe = _pipe ?? throw new IOException("Discord is not connected.");
        var nonce = Guid.NewGuid().ToString("N");
        await WriteFrameAsync(1, JsonSerializer.SerializeToUtf8Bytes(new { cmd = command, args, nonce }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var frame = await ReadFrameAsync(pipe, timeout.Token);
            var document = JsonDocument.Parse(frame.Payload);
            var root = document.RootElement;
            if (root.TryGetProperty("nonce", out var responseNonce) && responseNonce.GetString() == nonce)
            {
                if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                {
                    var error = RpcError(root);
                    document.Dispose();
                    throw error;
                }
                return document;
            }
            document.Dispose();
        }
    }

    async Task WriteFrameAsync(int opcode, byte[] payload)
    {
        var pipe = _pipe ?? throw new IOException("Discord is not connected.");
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(payload);
        await pipe.FlushAsync();
    }

    static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(Stream stream, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        return await ReadFrameAsync(stream, cancellation.Token);
    }

    static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length < 0 || length > 1024 * 1024) throw new IOException("Invalid Discord RPC message.");
        var body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken);
        return (BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4)), body);
    }

    static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (buffer.Length > 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) throw new EndOfStreamException("Discord closed its Rich Presence connection.");
            buffer = buffer[read..];
        }
    }

    static Exception RpcError(JsonElement root)
    {
        var message = root.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var msg)
            ? msg.GetString() : null;
        var code = root.TryGetProperty("data", out data) && data.TryGetProperty("code", out var codeElement)
            ? codeElement.ToString() : "";
        if (code == "4006")
            message = "Discord rejected this Rich Presence connection. Make sure the app ID is correct and the Discord desktop app is open.";
        return new IOException(string.IsNullOrWhiteSpace(message) ? "Discord rejected the activity request." : message);
    }

    async Task DisconnectPipeAsync()
    {
        _pipe?.Dispose();
        _pipe = null;
        _applicationId = "";
        await Task.CompletedTask;
    }

    static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
    public void Dispose() { _pipe?.Dispose(); _gate.Dispose(); }
}

