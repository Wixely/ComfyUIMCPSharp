namespace ComfyUIMCPSharp.Configuration;

/// <summary>
/// Domain options for the ComfyUI MCP server. Points the server at one ComfyUI instance
/// (normally a local one), sets the safety gates that decide what the agent is allowed to
/// queue or destroy, and caps the payload sizes that flow back through the MCP channel.
/// </summary>
public sealed class ComfyUIOptions
{
    public const string SectionName = "ComfyUI";

    /// <summary>
    /// Base URL of the ComfyUI instance, including scheme and port. ComfyUI's default listener
    /// is <c>http://127.0.0.1:8188/</c>. The WebSocket URL is derived from this automatically.
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188/";

    /// <summary>
    /// Optional API key sent as <c>Authorization: Bearer &lt;value&gt;</c>. Vanilla local ComfyUI
    /// needs no auth; this exists for reverse proxies and hosted deployments that add one.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional value for the <c>Comfy-User</c> header, required by ComfyUI's <c>/userdata</c>
    /// routes when it runs in multi-user mode. Blank for the normal single-user setup.
    /// </summary>
    public string ComfyUser { get; set; } = string.Empty;

    /// <summary>
    /// Master safety switch. When true, every tool that queues work or mutates ComfyUI
    /// (<c>comfy_submit_prompt</c>, <c>comfy_generate_image</c>, <c>comfy_run_workflow</c>,
    /// uploads, queue control, deletes) refuses with a clear error. Inspection tools stay
    /// available. Default true — flip it to <c>false</c> to let the agent actually generate.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>
    /// Second gate for tools that disturb work already in flight: <c>comfy_interrupt</c>,
    /// <c>comfy_clear_queue</c>, <c>comfy_delete_queue_items</c>, <c>comfy_free_memory</c>.
    /// Requires ReadOnly=false as well. Default false.
    /// </summary>
    public bool AllowQueueControl { get; set; } = false;

    /// <summary>
    /// Second gate for destructive tools: history deletion and workflow file deletion.
    /// Requires ReadOnly=false as well. Default false.
    /// </summary>
    public bool AllowDelete { get; set; } = false;

    /// <summary>When false, <c>comfy_upload_image</c> / <c>comfy_upload_mask</c> are refused.</summary>
    public bool EnableUploads { get; set; } = true;

    /// <summary>
    /// Directory the upload tools may read <c>sourcePath</c> files from. Blank (the default)
    /// refuses <c>sourcePath</c> entirely and allows only inline <c>contentBase64</c>.
    ///
    /// This matters because uploaded files land in ComfyUI's input folder, which
    /// <c>comfy_get_image(type: "input")</c> can read straight back out — so an unrestricted
    /// <c>sourcePath</c> would turn these two tools into an arbitrary local-file reader.
    /// Point this at a scratch/import directory to enable path-based uploads.
    /// </summary>
    public string? UploadSourceDirectory { get; set; }

    /// <summary>When false, <c>comfy_save_workflow</c> is refused even with ReadOnly=false.</summary>
    public bool EnableWorkflowSave { get; set; } = false;

    /// <summary>
    /// When true the server keeps a WebSocket open to ComfyUI's <c>/ws</c> and turns its
    /// execution events into live MCP progress notifications while a wait-enabled tool runs.
    /// When false the server falls back to polling <c>/queue</c> and <c>/history</c>.
    /// </summary>
    public bool EnableProgressStream { get; set; } = true;

    /// <summary>
    /// When true, image outputs are returned as real MCP image content blocks so a
    /// vision-capable client can see them. When false only metadata and file paths come back.
    /// </summary>
    public bool ReturnImagesInline { get; set; } = true;

    /// <summary>
    /// Largest image (bytes) returned inline. Anything bigger is written to
    /// <see cref="OutputDirectory"/> and reported as a path instead.
    /// </summary>
    public int MaxInlineImageBytes { get; set; } = 4_000_000;

    /// <summary>
    /// Maximum number of images a single tool call returns inline, so a 16-image batch does not
    /// flood the client. Remaining images are still listed as metadata.
    /// </summary>
    public int MaxInlineImagesPerCall { get; set; } = 4;

    /// <summary>Where downloaded outputs are written. Falls back to <c>&lt;TEMP&gt;/ComfyUIMCPSharp</c>.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Optional local directory of API-format workflow JSON templates the agent can list and run
    /// by name. These are read from disk by this server, independent of ComfyUI's own user data.
    /// </summary>
    public string? WorkflowDirectory { get; set; }

    /// <summary>Default seconds a wait-enabled tool blocks for a prompt to finish.</summary>
    public int DefaultWaitSeconds { get; set; } = 300;

    /// <summary>Hard ceiling on the wait a tool call may request, whatever the caller asks for.</summary>
    public int MaxWaitSeconds { get; set; } = 1800;

    /// <summary>Poll interval (ms) used when waiting without the WebSocket progress stream.</summary>
    public int PollIntervalMs { get; set; } = 750;

    /// <summary>Cap on node types returned by <c>comfy_list_node_types</c> in one call.</summary>
    public int MaxNodeTypesReturned { get; set; } = 200;

    /// <summary>Cap on entries returned by <c>comfy_list_history</c> in one call.</summary>
    public int MaxHistoryItems { get; set; } = 25;

    /// <summary>Cap on characters of any single JSON blob echoed back (node schemas, workflows).</summary>
    public int MaxJsonChars { get; set; } = 60_000;

    /// <summary>
    /// Checkpoint filename used by <c>comfy_generate_image</c> when the caller omits one.
    /// Blank means the tool resolves the first checkpoint reported by ComfyUI.
    /// </summary>
    public string DefaultCheckpoint { get; set; } = string.Empty;

    /// <summary>Default sampler for <c>comfy_generate_image</c>.</summary>
    public string DefaultSampler { get; set; } = "euler";

    /// <summary>Default scheduler for <c>comfy_generate_image</c>.</summary>
    public string DefaultScheduler { get; set; } = "normal";

    /// <summary>HTTP request timeout in seconds for calls to ComfyUI.</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>User-Agent header sent to ComfyUI.</summary>
    public string UserAgent { get; set; } = "ComfyUIMCPSharp";

    /// <summary>If true, ignore TLS certificate errors. Only for self-signed reverse proxies.</summary>
    public bool AllowInvalidCertificate { get; set; } = false;
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5715;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "ComfyUIMCPSharp";

    /// <summary>Optional MCP endpoint password. Blank disables MCP password auth.</summary>
    public string Password { get; set; } = string.Empty;
}
