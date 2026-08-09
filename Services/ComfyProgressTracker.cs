using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComfyUIMCPSharp.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace ComfyUIMCPSharp.Services;

/// <summary>
/// Keeps one WebSocket open to ComfyUI's /ws for the lifetime of the process and folds the
/// execution events it emits into per-prompt <see cref="ComfyPromptState"/> objects.
///
/// The connection carries a stable client id which is also sent with every prompt this server
/// submits — that is what makes ComfyUI address execution events back to us rather than to a
/// browser tab. Waiting tools drain those events into MCP progress notifications, which the
/// Streamable HTTP transport pushes to the client while the tool call is still in flight.
///
/// Everything here is best-effort: if the socket is down, waits fall back to polling
/// /history and /queue, so no tool depends on the stream being up.
/// </summary>
public sealed class ComfyProgressTracker : BackgroundService
{
    private const int MaxTrackedPrompts = 250;

    private readonly ComfyUIService _comfy;
    private readonly ComfyUIOptions _options;
    private readonly ILogger<ComfyProgressTracker> _logger;
    private readonly ConcurrentDictionary<string, ComfyPromptState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public ComfyProgressTracker(
        ComfyUIService comfy,
        IOptions<ComfyUIOptions> options,
        ILogger<ComfyProgressTracker> logger)
    {
        _comfy = comfy;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Stable id sent both on /ws and with every submitted prompt.</summary>
    public string ClientId { get; } = Guid.NewGuid().ToString("N");

    public bool IsConnected { get; private set; }

    /// <summary>Queue depth from the most recent `status` event, when one has arrived.</summary>
    public int? QueueRemaining { get; private set; }

    public ComfyPromptState? TryGetState(string promptId) =>
        _states.TryGetValue(promptId, out var state) ? state : null;

    /// <summary>Start tracking a prompt we just submitted, naming its nodes for progress messages.</summary>
    public ComfyPromptState Register(string promptId, JsonObject? workflow)
    {
        var state = GetOrAdd(promptId);
        if (workflow is not null) state.AttachGraph(workflow);
        return state;
    }

    private ComfyPromptState GetOrAdd(string promptId)
    {
        var state = _states.GetOrAdd(promptId, id =>
        {
            _order.Enqueue(id);
            return new ComfyPromptState(id);
        });

        while (_order.Count > MaxTrackedPrompts && _order.TryDequeue(out var oldest))
        {
            _states.TryRemove(oldest, out _);
        }

        return state;
    }

    // ---------------------------------------------------------------- waiting

    /// <summary>
    /// Block until the prompt leaves the queue, reporting progress as it goes. Returns the
    /// /history entry for the prompt. Throws <see cref="McpException"/> on timeout or when
    /// ComfyUI reports an execution error.
    /// </summary>
    public async Task<JsonObject> WaitForCompletionAsync(
        string promptId,
        int waitSeconds,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken ct)
    {
        var state = GetOrAdd(promptId);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(100, _options.PollIntervalMs));
        var tick = IsConnected ? TimeSpan.FromMilliseconds(200) : pollInterval;
        var nextPoll = DateTimeOffset.MinValue;
        string? lastMessage = null;
        double lastFraction = -1;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            ReportProgress(progress, state, promptId, ref lastMessage, ref lastFraction, ct);

            if (state.Status == "error" || state.Status == "interrupted")
            {
                var detail = state.Error?.ToJsonString(ComfyUIService.SerializerOptions) ?? "(no detail)";
                // The history entry usually carries the same error plus any partial outputs.
                var partial = await SafeGetHistoryAsync(promptId, ct);
                if (partial is not null) return partial;
                throw new McpException($"ComfyUI prompt {promptId} {state.Status}: {_comfy.Truncate(detail, 4000)}");
            }

            if (DateTimeOffset.UtcNow >= nextPoll || state.Status == "completed")
            {
                nextPoll = DateTimeOffset.UtcNow.Add(pollInterval);
                var history = await SafeGetHistoryAsync(promptId, ct);
                if (history is not null)
                {
                    if (!state.IsTerminal) state.MarkFinished("completed");
                    ReportProgress(progress, state, promptId, ref lastMessage, ref lastFraction, ct, force: true);
                    return history;
                }

                if (state.Status == "completed")
                {
                    // ComfyUI said done but history has not landed yet; give it one more beat.
                    await Task.Delay(pollInterval, ct);
                    history = await SafeGetHistoryAsync(promptId, ct);
                    if (history is not null) return history;
                }

                if (!IsConnected)
                {
                    // Without the event stream, /queue is the only source of liveness.
                    var position = await _comfy.GetQueuePositionAsync(promptId, ct);
                    if (position is null && state.Status == "queued")
                    {
                        // Neither queued nor in history: either it finished between polls or it
                        // was never accepted. One more history check settles it on the next loop.
                        state.MarkStarted();
                    }
                    else if (position is { Running: true })
                    {
                        state.MarkStarted();
                    }
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new McpException(
                    $"Timed out after {waitSeconds}s waiting for ComfyUI prompt {promptId}. " +
                    $"Last known state: {state.Describe()}. The prompt may still be running — " +
                    $"poll comfy_get_prompt_status or comfy_get_history with this prompt_id.");
            }

            await state.WaitForChangeAsync(tick, ct);
        }
    }

    private async Task<JsonObject?> SafeGetHistoryAsync(string promptId, CancellationToken ct)
    {
        try
        {
            return await _comfy.GetHistoryEntryAsync(promptId, ct);
        }
        catch (McpException)
        {
            // A transient 5xx while ComfyUI is busy should not abort the wait.
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private void ReportProgress(
        IProgress<ProgressNotificationValue>? progress,
        ComfyPromptState state,
        string promptId,
        ref string? lastMessage,
        ref double lastFraction,
        CancellationToken ct,
        bool force = false)
    {
        if (progress is null || ct.IsCancellationRequested) return;

        var fraction = state.OverallFraction;
        var message = state.Describe();
        var pct = fraction is null ? 0 : Math.Round(fraction.Value * 100, 1);

        if (!force && message == lastMessage && Math.Abs(pct - lastFraction) < 0.5) return;

        lastMessage = message;
        lastFraction = pct;

        progress.Report(new ProgressNotificationValue
        {
            Progress = (float)pct,
            Total = 100,
            Message = $"{promptId[..Math.Min(8, promptId.Length)]}: {message}",
        });
    }

    // ---------------------------------------------------------------- websocket pump

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableProgressStream)
        {
            _logger.LogInformation(
                "ComfyUI progress stream disabled (ComfyUI:EnableProgressStream=false); waits will poll /history.");
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _logger.LogDebug(ex, "ComfyUI websocket disconnected; retrying in {Backoff}", backoff);
            }

            IsConnected = false;
            try { await Task.Delay(backoff, stoppingToken); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(maxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", _options.UserAgent);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_options.ApiKey}");
        }
        if (!string.IsNullOrWhiteSpace(_options.ComfyUser))
        {
            socket.Options.SetRequestHeader("Comfy-User", _options.ComfyUser);
        }
        if (_options.AllowInvalidCertificate)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var url = new UriBuilder(_comfy.WebSocketUri) { Query = $"clientId={ClientId}" }.Uri;
        await socket.ConnectAsync(url, ct);
        IsConnected = true;
        _logger.LogInformation("Connected to ComfyUI event stream at {Url} (clientId {ClientId})", url, ClientId);

        var buffer = new byte[32 * 1024];
        var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Binary frames are live latent previews; we do not surface those over MCP.
                if (result.EndOfMessage) message.SetLength(0);
                continue;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);

            try
            {
                Dispatch(text);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                _logger.LogDebug(ex, "Unparseable ComfyUI event: {Event}", text.Length > 500 ? text[..500] : text);
            }
        }

        IsConnected = false;
    }

    /// <summary>
    /// Translate one /ws frame into state changes. ComfyUI's event vocabulary has grown over
    /// time, so both the legacy (`executing` with a null node) and current
    /// (`execution_success`) completion signals are honoured.
    /// </summary>
    private void Dispatch(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return;
        var type = root["type"]?.GetValue<string>();
        var data = root["data"] as JsonObject;
        if (type is null) return;

        switch (type)
        {
            case "status":
                QueueRemaining = data?["status"]?["exec_info"]?["queue_remaining"]?.GetValue<int?>();
                return;

            case "execution_start":
                WithState(data, s => s.MarkStarted());
                return;

            case "execution_cached":
                WithState(data, s =>
                {
                    var count = (data?["nodes"] as JsonArray)?.Count ?? 0;
                    if (count > 0) s.MarkCachedNodes(count);
                });
                return;

            case "executing":
            {
                // Older builds send this without a prompt_id when the run ends.
                var nodeId = NodeIdOf(data?["node"]);
                var promptId = data?["prompt_id"]?.GetValue<string>();
                if (promptId is not null)
                {
                    GetOrAdd(promptId).MarkExecutingNode(nodeId);
                }
                else if (nodeId is null)
                {
                    foreach (var running in _states.Values.Where(s => s.Status == "running"))
                    {
                        running.MarkFinished("completed");
                    }
                }
                return;
            }

            case "progress":
            {
                var value = data?["value"]?.GetValue<int?>() ?? 0;
                var max = data?["max"]?.GetValue<int?>() ?? 0;
                var nodeId = NodeIdOf(data?["node"]);
                WithState(data, s => s.MarkNodeProgress(nodeId, value, max));
                return;
            }

            case "progress_state":
            {
                // Newer aggregate event: {prompt_id, nodes:{id:{value,max,state}}}.
                WithState(data, s =>
                {
                    if (data?["nodes"] is not JsonObject nodes) return;
                    foreach (var (nodeId, node) in nodes)
                    {
                        if (node is not JsonObject n) continue;
                        if (n["state"]?.GetValue<string>() is "running" or null)
                        {
                            var value = (int)(n["value"]?.GetValue<double?>() ?? 0);
                            var max = (int)(n["max"]?.GetValue<double?>() ?? 0);
                            if (max > 0) s.MarkNodeProgress(nodeId, value, max);
                        }
                    }
                });
                return;
            }

            case "executed":
                WithState(data, s => s.MarkNodeExecuted());
                return;

            case "execution_success":
                WithState(data, s => s.MarkFinished("completed"));
                return;

            case "execution_error":
                WithState(data, s => s.MarkFinished("error", data?.DeepClone() as JsonObject));
                return;

            case "execution_interrupted":
                WithState(data, s => s.MarkFinished("interrupted", data?.DeepClone() as JsonObject));
                return;

            default:
                return;
        }
    }

    private void WithState(JsonObject? data, Action<ComfyPromptState> action)
    {
        var promptId = data?["prompt_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(promptId)) return;
        action(GetOrAdd(promptId));
    }

    /// <summary>Node ids come through as either a JSON string or a number depending on the build.</summary>
    private static string? NodeIdOf(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<long>(out var l)) return l.ToString();
            if (value.TryGetValue<double>(out var d)) return ((long)d).ToString();
        }
        return null;
    }
}
