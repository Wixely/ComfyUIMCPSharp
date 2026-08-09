using System.ComponentModel;
using ComfyUIMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ComfyUIMCPSharp.Tools;

/// <summary>
/// Getting produced files back out of ComfyUI — as viewable image content, as bytes on disk,
/// or as a plain listing. All read-only: these tools never queue work.
/// </summary>
[McpServerToolType]
public static class OutputTools
{
    [McpServerTool(Name = "comfy_list_outputs"),
     Description("List the files a finished run produced, with the filename/subfolder/type triple each one is addressed by and a direct ComfyUI URL. Pass one of these to comfy_get_image to look at it or comfy_download_output to save it.")]
    public static async Task<string> ListOutputs(
        ComfyUIService svc,
        [Description("The prompt_id of a finished run.")] string promptId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promptId))
            throw new McpException("promptId is required.");

        var entry = await svc.GetHistoryEntryAsync(promptId, ct)
            ?? throw new McpException(
                $"No history entry for prompt {promptId} — it has not finished, or history was cleared. Check comfy_get_prompt_status.");

        var outputs = ComfyWorkflows.ExtractOutputs(entry);

        return ToolHelpers.Json(svc, new
        {
            promptId,
            count = outputs.Count,
            outputs = outputs.Select(o => new
            {
                nodeId = o.NodeId,
                kind = o.Kind,
                filename = o.Filename,
                subfolder = string.IsNullOrEmpty(o.Subfolder) ? null : o.Subfolder,
                type = o.Type,
                isImage = ComfyWorkflows.IsImage(o),
                mediaType = ComfyWorkflows.MediaTypeFor(o.Filename),
                url = svc.AbsoluteViewUrl(o.Filename, o.Subfolder, o.Type),
            }),
        });
    }

    [McpServerTool(Name = "comfy_get_image"),
     Description("Fetch one image from ComfyUI and return it as viewable image content, so a vision-capable client can actually look at the result. Images above ComfyUI:MaxInlineImageBytes are written to disk and reported by path instead. Use the filename/subfolder/type from comfy_list_outputs.")]
    public static async Task<CallToolResult> GetImage(
        ComfyUIService svc,
        [Description("Filename as reported in the run's outputs, e.g. 'ComfyUIMCPSharp_00001_.png'.")] string filename,
        [Description("Subfolder the file sits in, as reported in the outputs. Usually empty.")] string? subfolder = null,
        [Description("Which ComfyUI folder to read from: 'output' (saved results), 'temp' (previews), or 'input' (uploaded sources).")] string type = "output",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filename))
            throw new McpException("filename is required.");

        var (bytes, contentType) = await svc.DownloadBytesAsync(
            ComfyUIService.ViewUrl(filename, subfolder, type), ct);

        var mediaType = contentType ?? ComfyWorkflows.MediaTypeFor(filename);
        var blocks = new List<ContentBlock>();

        var inline = svc.Options.ReturnImagesInline
            && bytes.Length <= svc.Options.MaxInlineImageBytes
            && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        string? savedPath = null;
        if (!inline)
        {
            savedPath = await ToolHelpers.WriteToOutputDirectoryAsync(svc, filename, bytes, ct);
        }

        blocks.Add(new TextContentBlock
        {
            Text = ToolHelpers.Json(svc, new
            {
                filename,
                subfolder,
                type,
                bytes = bytes.Length,
                mediaType,
                returnedInline = inline,
                savedPath,
                note = inline
                    ? null
                    : svc.Options.ReturnImagesInline
                        ? $"Larger than ComfyUI:MaxInlineImageBytes ({svc.Options.MaxInlineImageBytes:N0}); written to disk instead."
                        : "ComfyUI:ReturnImagesInline is false; written to disk instead.",
            }),
        });

        if (inline)
        {
            blocks.Add(ImageContentBlock.FromBytes(bytes, mediaType));
        }

        return new CallToolResult { Content = blocks };
    }

    [McpServerTool(Name = "comfy_download_output"),
     Description("Download one produced file from ComfyUI to disk under ComfyUI:OutputDirectory and return the local path. Use this for videos, audio, or images too large to view inline.")]
    public static async Task<string> DownloadOutput(
        ComfyUIService svc,
        [Description("Filename as reported in the run's outputs.")] string filename,
        [Description("Subfolder the file sits in, as reported in the outputs. Usually empty.")] string? subfolder = null,
        [Description("Which ComfyUI folder to read from: 'output', 'temp', or 'input'.")] string type = "output",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filename))
            throw new McpException("filename is required.");

        var (bytes, contentType) = await svc.DownloadBytesAsync(
            ComfyUIService.ViewUrl(filename, subfolder, type), ct);

        var path = await ToolHelpers.WriteToOutputDirectoryAsync(svc, filename, bytes, ct);

        return ToolHelpers.Json(svc, new
        {
            filename,
            subfolder,
            type,
            bytes = bytes.Length,
            mediaType = contentType ?? ComfyWorkflows.MediaTypeFor(filename),
            path,
        });
    }
}
