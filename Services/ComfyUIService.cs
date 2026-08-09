using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComfyUIMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace ComfyUIMCPSharp.Services;

/// <summary>
/// Thin client over the ComfyUI HTTP API (the same routes the ComfyUI web app itself uses:
/// /prompt, /queue, /history, /view, /upload, /object_info, /system_stats, /userdata, …).
/// Every safety gate lives here so the tool classes stay declarative.
/// </summary>
public sealed class ComfyUIService : IDisposable
{
    private readonly ComfyUIOptions _options;
    private readonly HttpClient _http;
    private bool _disposed;

    public ComfyUIService(IOptions<ComfyUIOptions> options)
    {
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("ComfyUI:BaseUrl is not configured.");

        var baseUrl = _options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException($"ComfyUI:BaseUrl is not a valid absolute URL: '{_options.BaseUrl}'.");
        BaseUri = baseUri;

        var handler = new HttpClientHandler();
        if (_options.AllowInvalidCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds)),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(_options.ComfyUser))
        {
            _http.DefaultRequestHeaders.Add("Comfy-User", _options.ComfyUser);
        }
    }

    public ComfyUIOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;
    public HttpClient Http => _http;

    /// <summary>Normalized ComfyUI base URL, always ending in '/'.</summary>
    public Uri BaseUri { get; }

    /// <summary>The /ws endpoint derived from <see cref="BaseUri"/> (http -&gt; ws, https -&gt; wss).</summary>
    public Uri WebSocketUri
    {
        get
        {
            var builder = new UriBuilder(BaseUri)
            {
                Scheme = BaseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                Path = BaseUri.AbsolutePath.TrimEnd('/') + "/ws",
                Query = string.Empty,
            };
            return builder.Uri;
        }
    }

    // ---------------------------------------------------------------- safety gates

    public void EnsureWriteAllowed(string operation)
    {
        if (_options.ReadOnly)
        {
            throw new McpException(
                $"MCP tool '{operation}' is blocked by server configuration. " +
                "Set ComfyUI:ReadOnly=false to let this server queue work on ComfyUI.");
        }
    }

    public void EnsureQueueControlAllowed(string operation)
    {
        EnsureWriteAllowed(operation);
        if (!_options.AllowQueueControl)
        {
            throw new McpException(
                $"MCP tool '{operation}' requires ComfyUI:AllowQueueControl=true (in addition to ComfyUI:ReadOnly=false). " +
                "It interrupts or discards work that is already queued or running.");
        }
    }

    public void EnsureDeleteAllowed(string operation)
    {
        EnsureWriteAllowed(operation);
        if (!_options.AllowDelete)
        {
            throw new McpException(
                $"MCP tool '{operation}' requires ComfyUI:AllowDelete=true (in addition to ComfyUI:ReadOnly=false).");
        }
    }

    public void EnsureFeature(bool flag, string operation, string configKey)
    {
        if (!flag)
        {
            throw new McpException($"MCP tool '{operation}' is disabled. Set {configKey}=true to enable it.");
        }
    }

    // ---------------------------------------------------------------- paths

    public string ResolveOutputDirectory()
    {
        var dir = string.IsNullOrWhiteSpace(_options.OutputDirectory)
            ? Path.Combine(Path.GetTempPath(), "ComfyUIMCPSharp")
            : _options.OutputDirectory!;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Local template directory, or null when ComfyUI:WorkflowDirectory is unset.</summary>
    public string? ResolveWorkflowDirectory()
    {
        if (string.IsNullOrWhiteSpace(_options.WorkflowDirectory)) return null;
        Directory.CreateDirectory(_options.WorkflowDirectory!);
        return _options.WorkflowDirectory;
    }

    /// <summary>Clamp a caller-supplied wait to the configured ceiling.</summary>
    public int ClampWaitSeconds(int? requested)
    {
        var seconds = requested ?? _options.DefaultWaitSeconds;
        if (seconds <= 0) seconds = _options.DefaultWaitSeconds;
        return Math.Min(seconds, Math.Max(1, _options.MaxWaitSeconds));
    }

    // ---------------------------------------------------------------- HTTP helpers

    /// <summary>
    /// Single funnel for every outbound call, so a ComfyUI that is simply not running produces
    /// an actionable message naming the URL and config key rather than a bare transport error
    /// the MCP client would replace with "an error occurred".
    /// </summary>
    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, completionOption, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException(
                $"Could not reach ComfyUI at {BaseUri} ({request.Method} {request.RequestUri}): {ex.Message} " +
                "Check that ComfyUI is running and that ComfyUI:BaseUrl points at it.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new McpException(
                $"Request to ComfyUI at {BaseUri} ({request.Method} {request.RequestUri}) timed out after " +
                $"{_options.RequestTimeoutSeconds}s. Raise ComfyUI:RequestTimeoutSeconds if the instance is just slow.", ex);
        }
    }

    public async Task<JsonNode?> GetJsonAsync(string relativePath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        using var response = await SendCoreAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, ct);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }

    public async Task<JsonNode?> SendJsonAsync(HttpMethod method, string relativePath, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, relativePath);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: SerializerOptions);
        }
        using var response = await SendCoreAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, ct);

        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return null;

        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }

    public async Task<(byte[] Bytes, string? ContentType)> DownloadBytesAsync(string relativePath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        using var response = await SendCoreAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return (bytes, response.Content.Headers.ContentType?.MediaType);
    }

    public async Task<string> DownloadTextAsync(string relativePath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        using var response = await SendCoreAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<JsonNode?> PostMultipartAsync(string relativePath, MultipartFormDataContent content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath) { Content = content };
        using var response = await SendCoreAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }

    // ---------------------------------------------------------------- ComfyUI operations

    /// <summary>
    /// POST /prompt. <paramref name="clientId"/> is what ties the run to our WebSocket session,
    /// so progress events for this prompt come back to us and not to a browser tab.
    /// </summary>
    public async Task<ComfySubmitResult> SubmitPromptAsync(
        JsonObject apiWorkflow,
        string? clientId,
        bool front,
        JsonObject? extraData,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["prompt"] = apiWorkflow.DeepClone(),
        };
        if (!string.IsNullOrWhiteSpace(clientId)) body["client_id"] = clientId;
        if (front) body["front"] = true;
        if (extraData is not null) body["extra_data"] = extraData.DeepClone();

        using var request = new HttpRequestMessage(HttpMethod.Post, "prompt")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await SendCoreAsync(request, HttpCompletionOption.ResponseContentRead, ct);

        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
        catch (JsonException) { /* fall through to raw text below */ }

        if (!response.IsSuccessStatusCode)
        {
            // ComfyUI returns 400 with {error:{…}, node_errors:{…}} for graph validation failures.
            // That detail is exactly what the agent needs to repair its workflow, so surface it.
            var detail = node?.ToJsonString(SerializerOptions) ?? Truncate(text, 4000);
            throw new McpException(
                $"ComfyUI rejected the prompt ({(int)response.StatusCode} {response.ReasonPhrase}). {detail}");
        }

        var promptId = node?["prompt_id"]?.GetValue<string>()
            ?? throw new McpException($"ComfyUI accepted the prompt but returned no prompt_id. Response: {Truncate(text, 1000)}");
        var number = node?["number"]?.GetValue<int?>() ?? 0;
        var nodeErrors = node?["node_errors"] as JsonObject;

        return new ComfySubmitResult(promptId, number, nodeErrors);
    }

    /// <summary>GET /history/{id}. Returns the entry for that prompt, or null while it is still queued.</summary>
    public async Task<JsonObject?> GetHistoryEntryAsync(string promptId, CancellationToken ct)
    {
        var node = await GetJsonAsync($"history/{Uri.EscapeDataString(promptId)}", ct);
        if (node is not JsonObject obj) return null;
        // The route returns {"<prompt_id>": {...}} — or {} when the prompt has not finished.
        if (obj.TryGetPropertyValue(promptId, out var entry) && entry is JsonObject entryObj)
            return entryObj;

        // Defensive: accept a single unkeyed entry, but only if it actually looks like one, so a
        // response for some other prompt can never be mistaken for this one's result.
        return obj.Count == 1
            && obj.First().Value is JsonObject only
            && (only.ContainsKey("outputs") || only.ContainsKey("status"))
                ? only
                : null;
    }

    /// <summary>Queue position for a prompt: 0 = running, N = N ahead of it, null = not queued.</summary>
    public async Task<ComfyQueuePosition?> GetQueuePositionAsync(string promptId, CancellationToken ct)
    {
        var node = await GetJsonAsync("queue", ct);
        if (node is not JsonObject obj) return null;

        if (obj["queue_running"] is JsonArray running)
        {
            foreach (var item in running)
            {
                if (PromptIdOf(item) == promptId) return new ComfyQueuePosition(true, 0);
            }
        }

        if (obj["queue_pending"] is JsonArray pending)
        {
            // queue_pending is not sorted by execution order; rank by the queue number at index 0.
            var ranked = pending
                .Select(item => (Id: PromptIdOf(item), Number: NumberOf(item)))
                .Where(x => x.Id is not null)
                .OrderBy(x => x.Number)
                .ToList();
            var index = ranked.FindIndex(x => x.Id == promptId);
            if (index >= 0) return new ComfyQueuePosition(false, index + 1);
        }

        return null;
    }

    /// <summary>Queue entries are positional arrays: [number, prompt_id, prompt, extra_data, outputs].</summary>
    public static string? PromptIdOf(JsonNode? queueItem) =>
        queueItem is JsonArray arr && arr.Count > 1 ? arr[1]?.GetValue<string>() : null;

    public static long NumberOf(JsonNode? queueItem)
    {
        if (queueItem is not JsonArray arr || arr.Count == 0) return long.MaxValue;
        try { return arr[0]?.GetValue<long>() ?? long.MaxValue; }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return long.MaxValue; }
    }

    /// <summary>Build the relative /view URL for one output file reference.</summary>
    public static string ViewUrl(string filename, string? subfolder, string? type)
    {
        var qs = new List<string> { $"filename={Uri.EscapeDataString(filename)}" };
        qs.Add($"subfolder={Uri.EscapeDataString(subfolder ?? string.Empty)}");
        qs.Add($"type={Uri.EscapeDataString(string.IsNullOrWhiteSpace(type) ? "output" : type)}");
        return "view?" + string.Join('&', qs);
    }

    public string AbsoluteViewUrl(string filename, string? subfolder, string? type) =>
        new Uri(BaseUri, ViewUrl(filename, subfolder, type)).ToString();

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Cap free text (error detail, not JSON) at the configured or given ceiling.</summary>
    public string Truncate(string value, int? maxChars = null)
    {
        var limit = maxChars ?? _options.MaxJsonChars;
        if (limit <= 0 || value.Length <= limit) return value;
        return value[..limit] + $"…(truncated at {limit} chars)";
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { body = string.Empty; }
        if (body.Length > 2000) body = body[..2000] + "…(truncated)";
        throw new ComfyHttpException(
            response.StatusCode,
            $"ComfyUI returned {(int)response.StatusCode} {response.ReasonPhrase} for " +
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}. Body: {body}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

/// <summary>
/// A response from ComfyUI that carried a non-success status. Keeping the status code on the
/// exception lets callers tell "this route/node does not exist on this build" (404) apart from
/// "ComfyUI is not running" — which surfaces as a plain <see cref="McpException"/> from the
/// transport layer and must not be reported as a missing feature.
/// </summary>
public sealed class ComfyHttpException : McpException
{
    public ComfyHttpException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }

    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;
}

public sealed record ComfySubmitResult(string PromptId, int Number, JsonObject? NodeErrors);

public sealed record ComfyQueuePosition(bool Running, int Ahead);
