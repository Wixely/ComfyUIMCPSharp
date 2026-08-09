using System.ComponentModel;
using System.Text.Json.Nodes;
using ComfyUIMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ComfyUIMCPSharp.Tools;

/// <summary>
/// ComfyUI's execution history: what ran, whether it succeeded, and what it produced.
/// Reading is free; clearing or deleting entries is double-gated behind ComfyUI:AllowDelete.
/// </summary>
[McpServerToolType]
public static class HistoryTools
{
    [McpServerTool(Name = "comfy_list_history"),
     Description("List recent ComfyUI runs, newest last, with their prompt ids, success/failure status, and how many files each produced. Use comfy_get_history for the full record of one run.")]
    public static async Task<string> ListHistory(
        ComfyUIService svc,
        [Description("How many entries to return. Capped by ComfyUI:MaxHistoryItems.")] int? maxItems = null,
        [Description("Skip this many of the most recent entries first.")] int offset = 0,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(maxItems ?? svc.Options.MaxHistoryItems, 1, svc.Options.MaxHistoryItems);
        var node = await svc.GetJsonAsync($"history?max_items={take}&offset={Math.Max(0, offset)}", ct);

        if (node is not JsonObject history)
            throw new McpException("ComfyUI returned an unexpected shape for /history.");

        var entries = new List<object>();
        foreach (var (promptId, value) in history)
        {
            if (value is not JsonObject entry) continue;
            var outputs = ComfyWorkflows.ExtractOutputs(entry);

            entries.Add(new
            {
                promptId,
                completed = entry["status"]?["completed"]?.GetValue<bool?>(),
                statusStr = entry["status"]?["status_str"]?.GetValue<string>(),
                nodeCount = (entry["prompt"] as JsonArray)?.ElementAtOrDefault(2) is JsonObject graph ? graph.Count : (int?)null,
                outputCount = outputs.Count,
                outputs = outputs.Select(o => new
                {
                    nodeId = o.NodeId,
                    kind = o.Kind,
                    filename = o.Filename,
                    subfolder = string.IsNullOrEmpty(o.Subfolder) ? null : o.Subfolder,
                    type = o.Type,
                }),
            });
        }

        return ToolHelpers.Json(svc, new { count = entries.Count, offset, entries });
    }

    [McpServerTool(Name = "comfy_get_history"),
     Description("Get the full history record for one prompt_id: its status, per-node messages, the graph that ran, and every output file it produced. This is where a failed run's error detail lives.")]
    public static async Task<string> GetHistory(
        ComfyUIService svc,
        [Description("The prompt_id to look up.")] string promptId,
        [Description("Include the full workflow graph that was executed. Off by default because graphs are large.")] bool includeGraph = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promptId))
            throw new McpException("promptId is required.");

        var entry = await svc.GetHistoryEntryAsync(promptId, ct);
        if (entry is null)
        {
            return ToolHelpers.Json(svc, new
            {
                promptId,
                found = false,
                note = "No history entry — the prompt is still queued or running, was never accepted, or history has been cleared. Try comfy_get_prompt_status.",
            });
        }

        var result = entry.DeepClone()!.AsObject();
        if (!includeGraph) result.Remove("prompt");

        return ToolHelpers.Json(svc, new
        {
            promptId,
            found = true,
            outputs = ComfyWorkflows.ExtractOutputs(entry).Select(o => new
            {
                nodeId = o.NodeId,
                kind = o.Kind,
                filename = o.Filename,
                subfolder = string.IsNullOrEmpty(o.Subfolder) ? null : o.Subfolder,
                type = o.Type,
                url = svc.AbsoluteViewUrl(o.Filename, o.Subfolder, o.Type),
            }),
            entry = result,
        });
    }

    [McpServerTool(Name = "comfy_delete_history_items"),
     Description("Delete specific entries from ComfyUI's run history by prompt_id. The generated image files themselves stay on disk. Requires ComfyUI:ReadOnly=false and ComfyUI:AllowDelete=true.")]
    public static async Task<string> DeleteHistoryItems(
        ComfyUIService svc,
        [Description("Comma-separated prompt_ids to delete from history.")] string promptIds,
        CancellationToken ct = default)
    {
        svc.EnsureDeleteAllowed("comfy_delete_history_items");

        var ids = promptIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (ids.Count == 0)
            throw new McpException("Pass at least one prompt_id.");

        await svc.SendJsonAsync(HttpMethod.Post, "history", new { delete = ids }, ct);
        return ToolHelpers.Json(svc, new { deleted = ids });
    }

    [McpServerTool(Name = "comfy_clear_history"),
     Description("Clear ComfyUI's entire run history. Generated files stay on disk, but the record of which run produced what is lost. Requires ComfyUI:ReadOnly=false and ComfyUI:AllowDelete=true.")]
    public static async Task<string> ClearHistory(
        ComfyUIService svc,
        CancellationToken ct = default)
    {
        svc.EnsureDeleteAllowed("comfy_clear_history");
        await svc.SendJsonAsync(HttpMethod.Post, "history", new { clear = true }, ct);
        return ToolHelpers.Json(svc, new { cleared = true });
    }
}
