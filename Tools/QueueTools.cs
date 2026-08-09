using System.ComponentModel;
using System.Text.Json.Nodes;
using ComfyUIMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ComfyUIMCPSharp.Tools;

/// <summary>
/// Inspecting and controlling the ComfyUI job queue. Reading it is free; anything that
/// interrupts or discards work in flight needs ComfyUI:AllowQueueControl=true on top of
/// ComfyUI:ReadOnly=false, because it affects runs this agent may not have started.
/// </summary>
[McpServerToolType]
public static class QueueTools
{
    [McpServerTool(Name = "comfy_get_queue"),
     Description("Show what ComfyUI is working on: the running job and everything pending behind it, with prompt ids and node counts. Use this to see whether the GPU is busy before queueing more work.")]
    public static async Task<string> GetQueue(
        ComfyUIService svc,
        ComfyProgressTracker tracker,
        [Description("Include each queued item's full workflow graph. Off by default because graphs are large.")] bool includeGraphs = false,
        CancellationToken ct = default)
    {
        var node = await svc.GetJsonAsync("queue", ct);
        if (node is not JsonObject queue)
            throw new McpException("ComfyUI returned an unexpected shape for /queue.");

        return ToolHelpers.Json(svc, new
        {
            running = Describe(queue["queue_running"] as JsonArray, includeGraphs),
            pending = Describe(queue["queue_pending"] as JsonArray, includeGraphs)
                .OrderBy(item => item.number)
                .ToList(),
            queueRemaining = tracker.QueueRemaining,
            progressStreamConnected = tracker.IsConnected,
        });
    }

    [McpServerTool(Name = "comfy_interrupt"),
     Description("Interrupt the job ComfyUI is currently executing. Pending jobs stay queued and start immediately after. Requires ComfyUI:ReadOnly=false and ComfyUI:AllowQueueControl=true.")]
    public static async Task<string> Interrupt(
        ComfyUIService svc,
        [Description("Optional prompt_id to interrupt only that run, on builds that support targeted interruption. Omit to interrupt whatever is running.")] string? promptId = null,
        CancellationToken ct = default)
    {
        svc.EnsureQueueControlAllowed("comfy_interrupt");

        object? body = string.IsNullOrWhiteSpace(promptId) ? new { } : new { prompt_id = promptId };
        await svc.SendJsonAsync(HttpMethod.Post, "interrupt", body, ct);

        return ToolHelpers.Json(svc, new { interrupted = true, promptId });
    }

    [McpServerTool(Name = "comfy_delete_queue_items"),
     Description("Remove specific pending jobs from the queue by prompt_id. Does not stop a job that is already running — use comfy_interrupt for that. Requires ComfyUI:ReadOnly=false and ComfyUI:AllowQueueControl=true.")]
    public static async Task<string> DeleteQueueItems(
        ComfyUIService svc,
        [Description("Comma-separated prompt_ids to remove from the pending queue.")] string promptIds,
        CancellationToken ct = default)
    {
        svc.EnsureQueueControlAllowed("comfy_delete_queue_items");

        var ids = promptIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (ids.Count == 0)
            throw new McpException("Pass at least one prompt_id.");

        await svc.SendJsonAsync(HttpMethod.Post, "queue", new { delete = ids }, ct);
        return ToolHelpers.Json(svc, new { deleted = ids });
    }

    [McpServerTool(Name = "comfy_clear_queue"),
     Description("Drop every pending job from the queue. The running job is unaffected. Requires ComfyUI:ReadOnly=false and ComfyUI:AllowQueueControl=true.")]
    public static async Task<string> ClearQueue(
        ComfyUIService svc,
        CancellationToken ct = default)
    {
        svc.EnsureQueueControlAllowed("comfy_clear_queue");
        await svc.SendJsonAsync(HttpMethod.Post, "queue", new { clear = true }, ct);
        return ToolHelpers.Json(svc, new { cleared = true });
    }

    [McpServerTool(Name = "comfy_free_memory"),
     Description("Ask ComfyUI to unload models and/or free cached VRAM. Useful when a later job needs more VRAM than is currently free; the next run pays a reload cost. Requires ComfyUI:ReadOnly=false and ComfyUI:AllowQueueControl=true.")]
    public static async Task<string> FreeMemory(
        ComfyUIService svc,
        [Description("Unload models from VRAM.")] bool unloadModels = true,
        [Description("Free ComfyUI's cached memory.")] bool freeMemory = true,
        CancellationToken ct = default)
    {
        svc.EnsureQueueControlAllowed("comfy_free_memory");
        await svc.SendJsonAsync(HttpMethod.Post, "free", new { unload_models = unloadModels, free_memory = freeMemory }, ct);
        return ToolHelpers.Json(svc, new { unloadModels, freeMemory, freed = true });
    }

    /// <summary>Queue entries are positional arrays: [number, prompt_id, prompt, extra_data, outputs].</summary>
    private static List<QueueItemView> Describe(JsonArray? items, bool includeGraphs)
    {
        var results = new List<QueueItemView>();
        if (items is null) return results;

        foreach (var item in items)
        {
            if (item is not JsonArray entry) continue;

            var graph = entry.Count > 2 ? entry[2] as JsonObject : null;
            results.Add(new QueueItemView
            {
                number = ComfyUIService.NumberOf(entry),
                promptId = ComfyUIService.PromptIdOf(entry),
                nodeCount = graph?.Count ?? 0,
                outputNodes = entry.Count > 4 ? (entry[4] as JsonArray)?.Count : null,
                graph = includeGraphs ? graph?.DeepClone() : null,
            });
        }

        return results;
    }

    private sealed class QueueItemView
    {
        public long number { get; init; }
        public string? promptId { get; init; }
        public int nodeCount { get; init; }
        public int? outputNodes { get; init; }
        public JsonNode? graph { get; init; }
    }
}
