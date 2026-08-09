using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ComfyUIMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ComfyUIMCPSharp.Tools;

/// <summary>
/// Everything that puts work on the ComfyUI queue. All of it is gated behind
/// ComfyUI:ReadOnly=false, and all of it can either return immediately with a prompt id or
/// block until the run finishes while streaming MCP progress notifications to the client.
/// </summary>
[McpServerToolType]
public static class GenerationTools
{
    [McpServerTool(Name = "comfy_generate_image"),
     Description("Generate an image from a text prompt using the standard checkpoint → CLIP → KSampler → VAE → SaveImage graph, without needing a workflow file. Waits for the render by default and streams progress (per sampler step) while it runs, then returns the finished image(s). For anything beyond plain text-to-image (LoRAs, img2img, ControlNet, upscaling), build a graph and use comfy_submit_prompt. Requires ComfyUI:ReadOnly=false.")]
    public static async Task<CallToolResult> GenerateImage(
        ComfyUIService svc,
        ComfyProgressTracker tracker,
        IProgress<ProgressNotificationValue> progress,
        [Description("What to generate. This is the positive prompt fed to CLIP.")] string prompt,
        [Description("What to avoid, fed to CLIP as the negative prompt. Defaults to empty.")] string? negativePrompt = null,
        [Description("Checkpoint filename, exactly as listed by comfy_list_models('checkpoints'). Defaults to ComfyUI:DefaultCheckpoint, or the first checkpoint installed.")] string? checkpoint = null,
        [Description("Image width in pixels. Use 512 for SD1.5, 1024 for SDXL/Flux.")] int width = 512,
        [Description("Image height in pixels.")] int height = 512,
        [Description("Sampling steps. 20-30 is typical.")] int steps = 20,
        [Description("Classifier-free guidance scale. 7-8 suits SD1.5; SDXL often prefers 4-7.")] double cfg = 7.5,
        [Description("Sampler name, e.g. 'euler', 'dpmpp_2m'. Must be one of KSampler's allowed values — see comfy_get_node_info('KSampler').")] string? sampler = null,
        [Description("Scheduler name, e.g. 'normal', 'karras'. Must be one of KSampler's allowed values.")] string? scheduler = null,
        [Description("Seed for reproducibility. Omit for a random seed; the seed used is always reported back.")] long? seed = null,
        [Description("How many images to generate in one batch.")] int batchSize = 1,
        [Description("Filename prefix for the saved images, relative to ComfyUI's output directory.")] string? filenamePrefix = null,
        [Description("Block until the render finishes and return the images. Set false to queue and return the prompt_id immediately.")] bool wait = true,
        [Description("Seconds to wait when wait=true. Defaults to ComfyUI:DefaultWaitSeconds, capped by ComfyUI:MaxWaitSeconds.")] int? waitSeconds = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("comfy_generate_image");

        if (string.IsNullOrWhiteSpace(prompt))
            throw new McpException("prompt is required — describe what to generate.");
        if (width <= 0 || height <= 0)
            throw new McpException("width and height must be positive.");
        if (steps <= 0)
            throw new McpException("steps must be positive.");
        if (batchSize <= 0)
            throw new McpException("batchSize must be at least 1.");

        var resolvedCheckpoint = await ResolveCheckpointAsync(svc, checkpoint, ct);
        var resolvedSeed = seed ?? RandomNumberGenerator.GetInt32(int.MaxValue);

        var graph = ComfyWorkflows.BuildTextToImage(
            resolvedCheckpoint,
            prompt,
            negativePrompt ?? string.Empty,
            width,
            height,
            steps,
            cfg,
            string.IsNullOrWhiteSpace(sampler) ? svc.Options.DefaultSampler : sampler!,
            string.IsNullOrWhiteSpace(scheduler) ? svc.Options.DefaultScheduler : scheduler!,
            resolvedSeed,
            batchSize,
            denoise: 1.0,
            string.IsNullOrWhiteSpace(filenamePrefix) ? "ComfyUIMCPSharp" : filenamePrefix!);

        return await SubmitAndMaybeWaitAsync(
            svc,
            tracker,
            progress,
            graph,
            wait,
            waitSeconds,
            front: false,
            extra: new
            {
                checkpoint = resolvedCheckpoint,
                seed = resolvedSeed,
                width,
                height,
                steps,
                cfg,
                sampler = string.IsNullOrWhiteSpace(sampler) ? svc.Options.DefaultSampler : sampler,
                scheduler = string.IsNullOrWhiteSpace(scheduler) ? svc.Options.DefaultScheduler : scheduler,
                batchSize,
            },
            ct);
    }

    [McpServerTool(Name = "comfy_submit_prompt"),
     Description("Queue an arbitrary API-format workflow on ComfyUI. `workflowJson` must be the API shape — an object keyed by node id where each value has `class_type` and `inputs` (ComfyUI exports this with Workflow > Export (API); the editor's own save format is different and is rejected with an explanation). Optional `overridesJson` patches input values before submitting. Waits and streams progress by default. Requires ComfyUI:ReadOnly=false.")]
    public static async Task<CallToolResult> SubmitPrompt(
        ComfyUIService svc,
        ComfyProgressTracker tracker,
        IProgress<ProgressNotificationValue> progress,
        [Description("The API-format workflow as a JSON string.")] string workflowJson,
        [Description("Optional JSON object of input overrides, keyed '<node-id-or-title-or-class>.<input>', e.g. {\"6.text\":\"a red fox\",\"3.seed\":42}.")] string? overridesJson = null,
        [Description("Block until the run finishes and return its outputs. Set false to queue and return the prompt_id immediately.")] bool wait = true,
        [Description("Seconds to wait when wait=true. Defaults to ComfyUI:DefaultWaitSeconds, capped by ComfyUI:MaxWaitSeconds.")] int? waitSeconds = null,
        [Description("Put this prompt at the front of the queue instead of the back.")] bool front = false,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("comfy_submit_prompt");

        var graph = ComfyWorkflows.EnsureApiFormat(
            ComfyWorkflows.ParseGraph(workflowJson, nameof(workflowJson)), nameof(workflowJson));

        var applied = ApplyOverrides(graph, overridesJson);

        return await SubmitAndMaybeWaitAsync(
            svc, tracker, progress, graph, wait, waitSeconds, front,
            extra: new { nodeCount = graph.Count, overridesApplied = applied },
            ct);
    }

    [McpServerTool(Name = "comfy_run_workflow"),
     Description("Run a stored API-format workflow by name — either from this server's ComfyUI:WorkflowDirectory or from ComfyUI's own saved user workflows — with optional input overrides. Use comfy_list_workflows to see what is available. Requires ComfyUI:ReadOnly=false.")]
    public static async Task<CallToolResult> RunWorkflow(
        ComfyUIService svc,
        ComfyProgressTracker tracker,
        IProgress<ProgressNotificationValue> progress,
        [Description("Workflow name or path exactly as reported by comfy_list_workflows.")] string name,
        [Description("Optional JSON object of input overrides, keyed '<node-id-or-title-or-class>.<input>', e.g. {\"6.text\":\"a red fox\"}.")] string? overridesJson = null,
        [Description("Block until the run finishes and return its outputs.")] bool wait = true,
        [Description("Seconds to wait when wait=true.")] int? waitSeconds = null,
        [Description("Put this prompt at the front of the queue.")] bool front = false,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("comfy_run_workflow");

        var (json, source) = await WorkflowTools.LoadWorkflowAsync(svc, name, ct);
        var graph = ComfyWorkflows.EnsureApiFormat(ComfyWorkflows.ParseGraph(json, $"workflow '{name}'"), $"workflow '{name}'");
        var applied = ApplyOverrides(graph, overridesJson);

        return await SubmitAndMaybeWaitAsync(
            svc, tracker, progress, graph, wait, waitSeconds, front,
            extra: new { workflow = name, source, nodeCount = graph.Count, overridesApplied = applied },
            ct);
    }

    [McpServerTool(Name = "comfy_wait_for_prompt"),
     Description("Block until an already-queued prompt finishes, streaming progress while it runs, then return its outputs. Use this after submitting with wait=false, or to re-attach to a run that outlived an earlier call's timeout.")]
    public static async Task<CallToolResult> WaitForPrompt(
        ComfyUIService svc,
        ComfyProgressTracker tracker,
        IProgress<ProgressNotificationValue> progress,
        [Description("The prompt_id returned when the work was queued.")] string promptId,
        [Description("Seconds to wait. Defaults to ComfyUI:DefaultWaitSeconds, capped by ComfyUI:MaxWaitSeconds.")] int? waitSeconds = null,
        [Description("Return the finished images inline as image content, subject to ComfyUI:ReturnImagesInline.")] bool includeImages = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promptId))
            throw new McpException("promptId is required.");

        var seconds = svc.ClampWaitSeconds(waitSeconds);
        var history = await tracker.WaitForCompletionAsync(promptId, seconds, progress, ct);
        return await ToolHelpers.BuildRunResultAsync(svc, promptId, history, new { waitedSeconds = seconds }, includeImages, ct);
    }

    [McpServerTool(Name = "comfy_get_prompt_status"),
     Description("Check on a prompt without blocking: whether it is queued, how many jobs are ahead of it, which node is executing and at what step, or that it has finished with its outputs. Cheap to poll.")]
    public static async Task<string> GetPromptStatus(
        ComfyUIService svc,
        ComfyProgressTracker tracker,
        [Description("The prompt_id returned when the work was queued.")] string promptId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promptId))
            throw new McpException("promptId is required.");

        var history = await svc.GetHistoryEntryAsync(promptId, ct);
        var live = tracker.TryGetState(promptId);
        var position = history is null ? await svc.GetQueuePositionAsync(promptId, ct) : null;

        var status = history is not null
            ? "completed"
            : position is { Running: true }
                ? "running"
                : position is not null
                    ? "queued"
                    : live?.Status ?? "unknown";

        return ToolHelpers.Json(svc, new
        {
            promptId,
            status,
            queuePosition = position is null ? (int?)null : position.Running ? 0 : position.Ahead,
            live = live?.Snapshot(),
            progressStreamConnected = tracker.IsConnected,
            outputs = history is null ? null : ComfyWorkflows.ExtractOutputs(history).Select(o => new
            {
                nodeId = o.NodeId,
                kind = o.Kind,
                filename = o.Filename,
                subfolder = string.IsNullOrEmpty(o.Subfolder) ? null : o.Subfolder,
                type = o.Type,
            }),
            historyStatus = history?["status"]?.DeepClone(),
            note = status == "unknown"
                ? "This server has no record of that prompt_id and ComfyUI does not have it queued or in history."
                : null,
        });
    }

    // ---------------------------------------------------------------- internals

    private static List<string> ApplyOverrides(JsonObject graph, string? overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson)) return [];

        var overrides = ComfyWorkflows.ParseGraph(overridesJson!, nameof(overridesJson));
        return ComfyWorkflows.ApplyOverrides(graph, overrides);
    }

    private static async Task<CallToolResult> SubmitAndMaybeWaitAsync(
        ComfyUIService svc,
        ComfyProgressTracker tracker,
        IProgress<ProgressNotificationValue> progress,
        JsonObject graph,
        bool wait,
        int? waitSeconds,
        bool front,
        object extra,
        CancellationToken ct)
    {
        var clientId = svc.Options.EnableProgressStream ? tracker.ClientId : null;
        var submission = await svc.SubmitPromptAsync(graph, clientId, front, extraData: null, ct);
        tracker.Register(submission.PromptId, graph);

        if (!wait)
        {
            return await ToolHelpers.BuildRunResultAsync(
                svc,
                submission.PromptId,
                historyEntry: null,
                extra: Merge(extra, new
                {
                    queueNumber = submission.Number,
                    queued = true,
                    note = "Queued. Poll comfy_get_prompt_status, or call comfy_wait_for_prompt to block until it finishes.",
                }),
                includeImages: false,
                ct);
        }

        var seconds = svc.ClampWaitSeconds(waitSeconds);
        var history = await tracker.WaitForCompletionAsync(submission.PromptId, seconds, progress, ct);

        return await ToolHelpers.BuildRunResultAsync(
            svc,
            submission.PromptId,
            history,
            Merge(extra, new { queueNumber = submission.Number }),
            includeImages: true,
            ct);
    }

    /// <summary>Combine two payload objects into one flat dictionary.</summary>
    private static Dictionary<string, object?> Merge(object first, object second)
    {
        var result = new Dictionary<string, object?>();
        foreach (var source in new[] { first, second })
        {
            foreach (var (key, value) in ToolHelpers.ToDictionary(source))
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static async Task<string> ResolveCheckpointAsync(ComfyUIService svc, string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested!;
        if (!string.IsNullOrWhiteSpace(svc.Options.DefaultCheckpoint)) return svc.Options.DefaultCheckpoint;

        var node = await svc.GetJsonAsync("models/checkpoints", ct);
        var first = (node as JsonArray)?
            .Select(n => n?.GetValue<string>())
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        if (first is null)
        {
            throw new McpException(
                "No checkpoint was given and this ComfyUI instance reports no installed checkpoints. " +
                "Install a model, pass `checkpoint` explicitly, or set ComfyUI:DefaultCheckpoint.");
        }

        return first;
    }
}
