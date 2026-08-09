using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComfyUIMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ComfyUIMCPSharp.Tools;

/// <summary>
/// Read-only introspection of the ComfyUI instance: hardware, feature flags, the installed node
/// catalogue, and the model files available to load. Everything an agent needs to work out what
/// it can actually build a workflow from before it queues anything.
/// </summary>
[McpServerToolType]
public static class SystemTools
{
    [McpServerTool(Name = "comfy_system_stats"),
     Description("Report the ComfyUI instance's status: reachable or not, python/ComfyUI versions, OS, and per-device VRAM/RAM totals and free space. Call this first to confirm the server is up and see how much VRAM is available before queueing a large job.")]
    public static async Task<string> SystemStats(
        ComfyUIService svc,
        ComfyProgressTracker tracker,
        CancellationToken ct = default)
    {
        JsonNode? stats = null;
        string? error = null;
        try
        {
            stats = await svc.GetJsonAsync("system_stats", ct);
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or TaskCanceledException)
        {
            error = ex.Message;
        }

        return ToolHelpers.Json(svc, new
        {
            baseUrl = svc.BaseUri.ToString(),
            reachable = error is null,
            error,
            readOnly = svc.IsReadOnly,
            allowQueueControl = svc.Options.AllowQueueControl,
            allowDelete = svc.Options.AllowDelete,
            progressStreamEnabled = svc.Options.EnableProgressStream,
            progressStreamConnected = tracker.IsConnected,
            queueRemaining = tracker.QueueRemaining,
            stats = stats?.DeepClone(),
        });
    }

    [McpServerTool(Name = "comfy_list_node_types"),
     Description("List the node types installed on this ComfyUI instance (core plus any custom node packs), with their category and one-line description. Filter with `search` or `category` — the full catalogue is thousands of nodes. Use comfy_get_node_info for a node's exact inputs and outputs.")]
    public static async Task<string> ListNodeTypes(
        ComfyUIService svc,
        [Description("Case-insensitive substring matched against the node name, display name, description, and category.")] string? search = null,
        [Description("Exact category prefix to filter on, e.g. 'loaders', 'sampling', 'conditioning', 'image'.")] string? category = null,
        [Description("Skip this many matches before returning results, for paging through a large catalogue.")] int offset = 0,
        [Description("Maximum results to return. Capped by ComfyUI:MaxNodeTypesReturned.")] int? limit = null,
        CancellationToken ct = default)
    {
        var node = await svc.GetJsonAsync("object_info", ct);
        if (node is not JsonObject catalogue)
            throw new McpException("ComfyUI returned an unexpected shape for /object_info.");

        var take = Math.Clamp(limit ?? svc.Options.MaxNodeTypesReturned, 1, svc.Options.MaxNodeTypesReturned);
        var matches = new List<object>();
        var total = 0;

        foreach (var (name, value) in catalogue)
        {
            if (value is not JsonObject spec) continue;

            var displayName = spec["display_name"]?.GetValue<string>();
            var nodeCategory = spec["category"]?.GetValue<string>();
            var description = spec["description"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(category)
                && !(nodeCategory ?? string.Empty).StartsWith(category, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(search)
                && !Contains(name, search)
                && !Contains(displayName, search)
                && !Contains(description, search)
                && !Contains(nodeCategory, search))
                continue;

            total++;
            if (total <= offset || matches.Count >= take) continue;

            matches.Add(new
            {
                name,
                displayName,
                category = nodeCategory,
                description = Shorten(description, 200),
                outputs = (spec["output_name"] as JsonArray)?.Select(o => o?.GetValue<string>()).ToArray()
                    ?? (spec["output"] as JsonArray)?.Select(o => o?.ToString()).ToArray(),
                isOutputNode = spec["output_node"]?.GetValue<bool?>(),
            });
        }

        return ToolHelpers.Json(svc, new
        {
            totalInstalled = catalogue.Count,
            matched = total,
            offset,
            returned = matches.Count,
            truncated = total > offset + matches.Count,
            nodes = matches,
        });
    }

    [McpServerTool(Name = "comfy_get_node_info"),
     Description("Get the full input/output schema for one or more node types: every input name, its type, defaults, min/max, and the exact list of allowed values for dropdowns (checkpoints, samplers, LoRAs, …). This is the authoritative reference for constructing a valid API-format workflow.")]
    public static async Task<string> GetNodeInfo(
        ComfyUIService svc,
        [Description("Node class name(s), comma-separated. Use the `name` field from comfy_list_node_types, e.g. 'KSampler,CheckpointLoaderSimple'.")] string nodeTypes,
        CancellationToken ct = default)
    {
        var names = nodeTypes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
            throw new McpException("Pass at least one node type name.");

        var results = new JsonObject();
        var missing = new List<string>();

        foreach (var name in names)
        {
            JsonNode? info;
            try
            {
                info = await svc.GetJsonAsync($"object_info/{Uri.EscapeDataString(name)}", ct);
            }
            catch (ComfyHttpException ex) when (ex.IsNotFound)
            {
                // Only a genuine 404 means "no such node". A transport failure must propagate,
                // or an unreachable ComfyUI would be reported as every node being missing.
                missing.Add(name);
                continue;
            }

            // The single-node route answers with {"<name>": {...}}; unwrap it for convenience.
            if (info is JsonObject wrapper && wrapper.TryGetPropertyValue(name, out var inner) && inner is not null)
            {
                results[name] = inner.DeepClone();
            }
            else if (info is not null)
            {
                results[name] = info.DeepClone();
            }
            else
            {
                missing.Add(name);
            }
        }

        if (results.Count == 0)
            throw new McpException($"None of these node types exist on this ComfyUI instance: {string.Join(", ", missing)}.");

        var payload = new JsonObject { ["nodes"] = results };
        if (missing.Count > 0)
        {
            payload["notFound"] = new JsonArray(missing.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        }

        return ToolHelpers.Json(svc, payload);
    }

    [McpServerTool(Name = "comfy_list_model_folders"),
     Description("List the model folder types ComfyUI knows about (checkpoints, loras, vae, controlnet, embeddings, …). Use comfy_list_models to see the files inside one.")]
    public static async Task<string> ListModelFolders(
        ComfyUIService svc,
        CancellationToken ct = default)
    {
        var node = await svc.GetJsonAsync("models", ct);
        return ToolHelpers.Json(svc, node);
    }

    [McpServerTool(Name = "comfy_list_models"),
     Description("List the model files available in one folder, e.g. folder='checkpoints' for base models or folder='loras' for LoRAs. The filenames returned here are exactly the values a loader node's input expects.")]
    public static async Task<string> ListModels(
        ComfyUIService svc,
        [Description("Folder name from comfy_list_model_folders, e.g. 'checkpoints', 'loras', 'vae', 'controlnet'.")] string folder,
        [Description("Optional case-insensitive substring filter on the filename.")] string? search = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new McpException("folder is required. Call comfy_list_model_folders to see the options.");

        var node = await svc.GetJsonAsync($"models/{Uri.EscapeDataString(folder)}", ct);
        var files = (node as JsonArray)?.Select(f => f?.GetValue<string>()).Where(f => f is not null).ToList() ?? [];

        if (!string.IsNullOrWhiteSpace(search))
        {
            files = files.Where(f => Contains(f, search)).ToList();
        }

        return ToolHelpers.Json(svc, new { folder, count = files.Count, files });
    }

    [McpServerTool(Name = "comfy_list_embeddings"),
     Description("List textual-inversion embeddings installed on this instance. Reference one in a prompt as `embedding:<name>`.")]
    public static async Task<string> ListEmbeddings(
        ComfyUIService svc,
        CancellationToken ct = default)
    {
        var node = await svc.GetJsonAsync("embeddings", ct);
        return ToolHelpers.Json(svc, node);
    }

    [McpServerTool(Name = "comfy_get_features"),
     Description("Report the ComfyUI server's feature flags. Useful for telling an older build from a newer one when a route or event type behaves unexpectedly.")]
    public static async Task<string> GetFeatures(
        ComfyUIService svc,
        CancellationToken ct = default)
    {
        try
        {
            var node = await svc.GetJsonAsync("features", ct);
            return ToolHelpers.Json(svc, node);
        }
        catch (ComfyHttpException ex) when (ex.IsNotFound)
        {
            // /features only exists on newer builds; a 404 is itself the answer. Anything else
            // (including ComfyUI being down) propagates rather than masquerading as "unsupported".
            return ToolHelpers.Json(svc, new
            {
                supported = false,
                detail = "This ComfyUI build does not expose /features.",
                error = ex.Message,
            });
        }
    }

    private static bool Contains(string? haystack, string? needle) =>
        haystack is not null && needle is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? Shorten(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "…";
}
