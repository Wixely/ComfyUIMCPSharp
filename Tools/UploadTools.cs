using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ComfyUIMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ComfyUIMCPSharp.Tools;

/// <summary>
/// Putting source images into ComfyUI's input folder so workflows (img2img, inpainting,
/// ControlNet) can reference them by filename. Gated by ComfyUI:ReadOnly and
/// ComfyUI:EnableUploads because this writes files onto the ComfyUI host.
/// </summary>
[McpServerToolType]
public static class UploadTools
{
    [McpServerTool(Name = "comfy_upload_image"),
     Description("Upload an image into ComfyUI's input folder so a LoadImage node can use it. Supply the picture as `contentBase64`, or as `sourcePath` if ComfyUI:UploadSourceDirectory is configured. Returns the filename to put in the workflow. Requires ComfyUI:ReadOnly=false and ComfyUI:EnableUploads=true.")]
    public static async Task<string> UploadImage(
        ComfyUIService svc,
        [Description("Filename to store it as in ComfyUI, including extension, e.g. 'source.png'.")] string filename,
        [Description("Path to a source file, resolved under ComfyUI:UploadSourceDirectory. Disabled unless that option is set. Use this or contentBase64, not both.")] string? sourcePath = null,
        [Description("Image bytes as base64. Use this or sourcePath, not both.")] string? contentBase64 = null,
        [Description("Subfolder within ComfyUI's input directory. Empty for the root.")] string? subfolder = null,
        [Description("Overwrite an existing file with the same name instead of having ComfyUI rename it.")] bool overwrite = false,
        [Description("Target folder: 'input' (normal) or 'temp'.")] string type = "input",
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("comfy_upload_image");
        svc.EnsureFeature(svc.Options.EnableUploads, "comfy_upload_image", "ComfyUI:EnableUploads");

        var bytes = await ReadSourceAsync(svc, sourcePath, contentBase64, ct);
        if (string.IsNullOrWhiteSpace(filename))
            throw new McpException("filename is required — it is how the workflow will refer to this image.");

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(ComfyWorkflows.MediaTypeFor(filename));
        content.Add(file, "image", Path.GetFileName(filename));
        content.Add(new StringContent(overwrite ? "true" : "false"), "overwrite");
        content.Add(new StringContent(string.IsNullOrWhiteSpace(type) ? "input" : type), "type");
        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            content.Add(new StringContent(subfolder), "subfolder");
        }

        var response = await svc.PostMultipartAsync("upload/image", content, ct);

        return ToolHelpers.Json(svc, new
        {
            uploaded = true,
            bytes = bytes.Length,
            response = response?.DeepClone(),
            note = "Use the returned `name`/`filename` (and `subfolder`, if any) as the LoadImage node's `image` input.",
        });
    }

    [McpServerTool(Name = "comfy_upload_mask"),
     Description("Upload a mask and composite it onto an image already in ComfyUI's input folder, the way the UI's mask editor does. The mask's alpha channel becomes the referenced image's alpha. Requires ComfyUI:ReadOnly=false and ComfyUI:EnableUploads=true.")]
    public static async Task<string> UploadMask(
        ComfyUIService svc,
        [Description("Filename to store the masked result as, e.g. 'source.png'.")] string filename,
        [Description("The image already in ComfyUI that the mask applies to, as {\"filename\":\"source.png\",\"subfolder\":\"\",\"type\":\"input\"}.")] string originalRefJson,
        [Description("Path to a mask file, resolved under ComfyUI:UploadSourceDirectory. Disabled unless that option is set. Use this or contentBase64.")] string? sourcePath = null,
        [Description("Mask bytes as base64 (RGBA PNG, alpha marks the masked area). Use this or sourcePath.")] string? contentBase64 = null,
        [Description("Subfolder within ComfyUI's input directory.")] string? subfolder = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("comfy_upload_mask");
        svc.EnsureFeature(svc.Options.EnableUploads, "comfy_upload_mask", "ComfyUI:EnableUploads");

        if (string.IsNullOrWhiteSpace(filename))
            throw new McpException("filename is required.");

        var reference = ComfyWorkflows.ParseGraph(originalRefJson, nameof(originalRefJson));
        if (reference["filename"] is null)
            throw new McpException("originalRefJson must contain a 'filename' naming the image already uploaded to ComfyUI.");

        var bytes = await ReadSourceAsync(svc, sourcePath, contentBase64, ct);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(ComfyWorkflows.MediaTypeFor(filename));
        content.Add(file, "image", Path.GetFileName(filename));
        content.Add(new StringContent(reference.ToJsonString()), "original_ref");
        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            content.Add(new StringContent(subfolder), "subfolder");
        }

        var response = await svc.PostMultipartAsync("upload/mask", content, ct);

        return ToolHelpers.Json(svc, new
        {
            uploaded = true,
            bytes = bytes.Length,
            response = response?.DeepClone(),
        });
    }

    private static async Task<byte[]> ReadSourceAsync(
        ComfyUIService svc,
        string? sourcePath,
        string? contentBase64,
        CancellationToken ct)
    {
        var hasPath = !string.IsNullOrWhiteSpace(sourcePath);
        var hasBase64 = !string.IsNullOrWhiteSpace(contentBase64);

        if (hasPath == hasBase64)
            throw new McpException("Supply exactly one of sourcePath or contentBase64.");

        if (hasPath)
        {
            var resolved = ResolveSourcePath(svc, sourcePath!);
            if (!File.Exists(resolved))
                throw new McpException($"sourcePath does not exist or is not readable by this server: {sourcePath}");
            return await File.ReadAllBytesAsync(resolved, ct);
        }

        try
        {
            return Convert.FromBase64String(contentBase64!);
        }
        catch (FormatException)
        {
            throw new McpException("contentBase64 is not valid base64.");
        }
    }

    /// <summary>
    /// Contain <c>sourcePath</c> to ComfyUI:UploadSourceDirectory. Uploads land in ComfyUI's input
    /// folder, which comfy_get_image can read back, so an unrestricted path here would let the
    /// agent read any file this process can open.
    /// </summary>
    private static string ResolveSourcePath(ComfyUIService svc, string sourcePath)
    {
        var root = svc.Options.UploadSourceDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new McpException(
                "Uploading by sourcePath is disabled. Set ComfyUI:UploadSourceDirectory to the directory this " +
                "server may read source images from, or pass the image inline as contentBase64 instead.");
        }

        var rootFull = Path.GetFullPath(root!);
        var candidate = Path.GetFullPath(Path.IsPathRooted(sourcePath)
            ? sourcePath
            : Path.Combine(rootFull, sourcePath));

        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"sourcePath resolves outside ComfyUI:UploadSourceDirectory ({rootFull}), which is not allowed.");
        }

        return candidate;
    }
}
