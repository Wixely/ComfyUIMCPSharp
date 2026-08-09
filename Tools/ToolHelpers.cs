using System.Text.Json;
using System.Text.Json.Nodes;
using ComfyUIMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ComfyUIMCPSharp.Tools;

/// <summary>
/// Shared result shaping for the tool layer: turning a finished /history entry into a compact
/// JSON summary plus, where the client can use them, real MCP image blocks.
/// </summary>
internal static class ToolHelpers
{
    /// <summary>
    /// Build the standard "a run finished" result: one text block of JSON metadata followed by
    /// up to <c>MaxInlineImagesPerCall</c> image blocks a vision-capable client can actually see.
    /// Images too large to inline are written to disk and reported by path instead.
    /// </summary>
    public static async Task<CallToolResult> BuildRunResultAsync(
        ComfyUIService svc,
        string promptId,
        JsonObject? historyEntry,
        object? extra,
        bool includeImages,
        CancellationToken ct)
    {
        var outputs = historyEntry is null
            ? new List<ComfyWorkflows.OutputRef>()
            : ComfyWorkflows.ExtractOutputs(historyEntry);

        var status = historyEntry?["status"];
        var descriptions = new List<object>();
        var blocks = new List<ContentBlock>();
        var inlineBudget = includeImages && svc.Options.ReturnImagesInline
            ? Math.Max(0, svc.Options.MaxInlineImagesPerCall)
            : 0;

        foreach (var output in outputs)
        {
            string? savedPath = null;
            string? inlineNote = null;

            if (ComfyWorkflows.IsImage(output) && inlineBudget > 0)
            {
                try
                {
                    var (bytes, contentType) = await svc.DownloadBytesAsync(
                        ComfyUIService.ViewUrl(output.Filename, output.Subfolder, output.Type), ct);

                    if (bytes.Length <= svc.Options.MaxInlineImageBytes)
                    {
                        blocks.Add(ImageContentBlock.FromBytes(
                            bytes,
                            ComfyWorkflows.ResolveMediaType(output.Filename, contentType)));
                        inlineBudget--;
                    }
                    else
                    {
                        savedPath = await WriteToOutputDirectoryAsync(svc, output.Filename, bytes, ct);
                        inlineNote = $"too large to inline ({bytes.Length:N0} bytes > ComfyUI:MaxInlineImageBytes); written to disk";
                    }
                }
                catch (Exception ex) when (ex is McpException or HttpRequestException or IOException)
                {
                    // One unreadable file should not sink the whole result — the run still happened.
                    inlineNote = $"could not be fetched from ComfyUI: {ex.Message}";
                }
            }

            descriptions.Add(new
            {
                nodeId = output.NodeId,
                kind = output.Kind,
                filename = output.Filename,
                subfolder = string.IsNullOrEmpty(output.Subfolder) ? null : output.Subfolder,
                type = output.Type,
                url = svc.AbsoluteViewUrl(output.Filename, output.Subfolder, output.Type),
                savedPath,
                note = inlineNote,
            });
        }

        // ComfyUI writes a history entry for failed runs too, so "we got history back" is not the
        // same as "it worked". State the verdict explicitly rather than leaving the caller to
        // infer success from an empty output list.
        var succeeded = Succeeded(status);

        var payload = new Dictionary<string, object?>
        {
            ["promptId"] = promptId,
            ["completed"] = historyEntry is not null,
            ["succeeded"] = succeeded,
            ["status"] = status?.DeepClone(),
            ["outputCount"] = outputs.Count,
            ["outputs"] = descriptions,
            ["inlineImages"] = blocks.Count,
        };

        if (succeeded == false)
        {
            payload["failureHint"] =
                "ComfyUI reported this run as failed. Call comfy_get_history with this prompt_id for the per-node error detail.";
        }

        if (extra is not null)
        {
            foreach (var (key, value) in ToDictionary(extra))
            {
                payload[key] = value;
            }
        }

        if (outputs.Count > 0 && blocks.Count == 0 && includeImages && !svc.Options.ReturnImagesInline)
        {
            payload["hint"] = "Set ComfyUI:ReturnImagesInline=true to receive images directly, or call comfy_get_image / comfy_download_output.";
        }

        blocks.Insert(0, new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOpts.Default) });
        return new CallToolResult { Content = blocks };
    }

    /// <summary>
    /// Read ComfyUI's verdict off a history entry's status block. Returns null when the run has
    /// not finished, so "unknown" stays distinguishable from "failed".
    /// </summary>
    private static bool? Succeeded(JsonNode? status)
    {
        if (status is not JsonObject obj) return null;

        var statusStr = obj["status_str"]?.GetValue<string>();
        if (statusStr is not null) return string.Equals(statusStr, "success", StringComparison.OrdinalIgnoreCase);

        // Older builds only carry `completed`.
        return obj["completed"] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    }

    /// <summary>Write bytes under the configured output directory without clobbering earlier runs.</summary>
    public static async Task<string> WriteToOutputDirectoryAsync(
        ComfyUIService svc,
        string filename,
        byte[] bytes,
        CancellationToken ct)
    {
        var directory = svc.ResolveOutputDirectory();
        var safeName = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "output.bin";

        var target = Path.Combine(directory, safeName);
        var counter = 1;
        while (File.Exists(target))
        {
            var stem = Path.GetFileNameWithoutExtension(safeName);
            var extension = Path.GetExtension(safeName);
            target = Path.Combine(directory, $"{stem}_{counter++}{extension}");
        }

        await File.WriteAllBytesAsync(target, bytes, ct);
        return target;
    }

    /// <summary>
    /// Flatten an extra-payload object into key/value pairs so it can be merged into a result.
    /// Callers pass either an already-built dictionary or an anonymous object; indexers are
    /// skipped so a dictionary's Item property does not get reflected over.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, object?>> ToDictionary(object value)
    {
        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            return readOnly;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        return value.GetType().GetProperties()
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => new KeyValuePair<string, object?>(p.Name, p.GetValue(value)));
    }

    /// <summary>Serialize a JsonNode for return, capped at the configured ceiling.</summary>
    public static string Json(ComfyUIService svc, JsonNode? node, int? maxChars = null) =>
        Cap(svc, node?.ToJsonString(ComfyUIService.SerializerOptions) ?? "null", maxChars);

    public static string Json(ComfyUIService svc, object value, int? maxChars = null) =>
        Cap(svc, JsonSerializer.Serialize(value, JsonOpts.Default), maxChars);

    /// <summary>
    /// Apply the size ceiling without handing back malformed JSON. Cutting a serialized object
    /// mid-token would leave the client with something it cannot parse at all, so an oversized
    /// payload is wrapped in a small valid envelope that says what happened and carries the
    /// prefix as a string.
    /// </summary>
    private static string Cap(ComfyUIService svc, string json, int? maxChars)
    {
        var limit = maxChars ?? svc.Options.MaxJsonChars;
        if (limit <= 0 || json.Length <= limit) return json;

        // Leave room for the envelope itself so the result still respects the ceiling.
        var room = Math.Max(0, limit - 400);

        return JsonSerializer.Serialize(new
        {
            truncated = true,
            originalChars = json.Length,
            maxChars = limit,
            hint = "Payload exceeded ComfyUI:MaxJsonChars. `partial` holds the leading fragment as text, "
                 + "not parseable JSON. Narrow the request (filter or page the listing, ask for fewer "
                 + "node types) or raise ComfyUI:MaxJsonChars.",
            partial = json[..room],
        }, JsonOpts.Default);
    }
}
