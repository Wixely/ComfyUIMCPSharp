using System.ComponentModel;
using System.Text.Json.Nodes;
using ComfyUIMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ComfyUIMCPSharp.Tools;

/// <summary>
/// Stored workflows from two places: this server's own ComfyUI:WorkflowDirectory (API-format
/// templates the agent can run by name) and ComfyUI's saved user workflows (what the editor
/// writes, which is the UI format and needs converting before it can be queued).
/// </summary>
[McpServerToolType]
public static class WorkflowTools
{
    private const string ComfyWorkflowDir = "workflows";

    [McpServerTool(Name = "comfy_list_workflows"),
     Description("List stored workflows this server can run by name: API-format templates from ComfyUI:WorkflowDirectory, plus the workflows saved in ComfyUI's own UI. Editor-saved workflows are in the UI format and must go through comfy_convert_workflow_to_api before they can be queued.")]
    public static async Task<string> ListWorkflows(
        ComfyUIService svc,
        [Description("Optional case-insensitive substring filter on the workflow name.")] string? search = null,
        CancellationToken ct = default)
    {
        var entries = new List<object>();

        var localRoot = svc.ResolveWorkflowDirectory();
        if (localRoot is not null)
        {
            foreach (var file in Directory.EnumerateFiles(localRoot, "*.json", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(localRoot, file).Replace('\\', '/');
                if (!Matches(name, search)) continue;

                var info = new FileInfo(file);
                entries.Add(new
                {
                    name,
                    source = "local",
                    path = file,
                    bytes = info.Length,
                    modifiedUtc = info.LastWriteTimeUtc,
                });
            }
        }

        string? comfyError = null;
        try
        {
            var listing = await svc.GetJsonAsync($"userdata?dir={ComfyWorkflowDir}&recurse=true&split=false", ct);
            if (listing is JsonArray files)
            {
                foreach (var item in files)
                {
                    var path = item?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                    var name = path.Replace('\\', '/');
                    if (!Matches(name, search)) continue;

                    entries.Add(new
                    {
                        name,
                        source = "comfyui",
                        path,
                        format = "ui (editor save) — convert before running",
                    });
                }
            }
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException)
        {
            comfyError = ex.Message;
        }

        return ToolHelpers.Json(svc, new
        {
            count = entries.Count,
            localDirectory = localRoot,
            localDirectoryConfigured = localRoot is not null,
            comfyUserWorkflowsError = comfyError,
            workflows = entries,
        });
    }

    [McpServerTool(Name = "comfy_get_workflow"),
     Description("Fetch a stored workflow's JSON by name and report which format it is in (api = runnable as-is, ui = editor save that needs converting). Large workflows are truncated at ComfyUI:MaxJsonChars.")]
    public static async Task<string> GetWorkflow(
        ComfyUIService svc,
        [Description("Workflow name or path exactly as reported by comfy_list_workflows.")] string name,
        CancellationToken ct = default)
    {
        var (json, source) = await LoadWorkflowAsync(svc, name, ct);
        var graph = ComfyWorkflows.ParseGraph(json, $"workflow '{name}'");
        var format = ComfyWorkflows.DetectFormat(graph);

        return ToolHelpers.Json(svc, new
        {
            name,
            source,
            format = format.ToString().ToLowerInvariant(),
            nodeCount = format == ComfyWorkflowFormat.Ui
                ? (graph["nodes"] as JsonArray)?.Count
                : graph.Count,
            runnable = format == ComfyWorkflowFormat.Api,
            note = format == ComfyWorkflowFormat.Ui
                ? "UI/editor format. Run it through comfy_convert_workflow_to_api first, or re-export it from ComfyUI with Workflow > Export (API)."
                : null,
            workflow = graph,
        });
    }

    [McpServerTool(Name = "comfy_save_workflow"),
     Description("Save an API-format workflow into ComfyUI:WorkflowDirectory so it can be re-run later by name with comfy_run_workflow. Requires ComfyUI:ReadOnly=false and ComfyUI:EnableWorkflowSave=true.")]
    public static async Task<string> SaveWorkflow(
        ComfyUIService svc,
        [Description("Name to save it under, e.g. 'portrait-sdxl.json'. A .json extension is added if missing.")] string name,
        [Description("The API-format workflow as a JSON string.")] string workflowJson,
        [Description("Overwrite an existing file with the same name.")] bool overwrite = false,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("comfy_save_workflow");
        svc.EnsureFeature(svc.Options.EnableWorkflowSave, "comfy_save_workflow", "ComfyUI:EnableWorkflowSave");

        var root = svc.ResolveWorkflowDirectory()
            ?? throw new McpException("ComfyUI:WorkflowDirectory is not configured, so there is nowhere to save workflows.");

        var graph = ComfyWorkflows.EnsureApiFormat(
            ComfyWorkflows.ParseGraph(workflowJson, nameof(workflowJson)), nameof(workflowJson));

        var fileName = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json";
        var target = ResolveUnderRoot(root, fileName);

        if (File.Exists(target) && !overwrite)
            throw new McpException($"'{fileName}' already exists. Pass overwrite=true to replace it.");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, graph.ToJsonString(ComfyUIService.SerializerOptions), ct);

        return ToolHelpers.Json(svc, new { saved = true, name = fileName, path = target, nodeCount = graph.Count });
    }

    [McpServerTool(Name = "comfy_convert_workflow_to_api"),
     Description("Convert a ComfyUI editor-format workflow (the shape the UI saves, with a 'nodes' array) into the API format POST /prompt accepts. Widget values are positional in the editor format, so their names are recovered from this instance's /object_info — the conversion is best-effort and every uncertainty is listed in `warnings`. Check the result before relying on it. Supply either `workflowJson` or `name`.")]
    public static async Task<string> ConvertWorkflowToApi(
        ComfyUIService svc,
        [Description("The UI-format workflow as a JSON string. Use this or `name`.")] string? workflowJson = null,
        [Description("Name of a stored workflow from comfy_list_workflows. Use this or `workflowJson`.")] string? name = null,
        CancellationToken ct = default)
    {
        var hasJson = !string.IsNullOrWhiteSpace(workflowJson);
        var hasName = !string.IsNullOrWhiteSpace(name);
        if (hasJson == hasName)
            throw new McpException("Supply exactly one of workflowJson or name.");

        string json;
        string source;
        if (hasName)
        {
            (json, source) = await LoadWorkflowAsync(svc, name!, ct);
        }
        else
        {
            json = workflowJson!;
            source = "inline";
        }

        var graph = ComfyWorkflows.ParseGraph(json, hasName ? $"workflow '{name}'" : nameof(workflowJson));

        if (ComfyWorkflows.DetectFormat(graph) == ComfyWorkflowFormat.Api)
        {
            return ToolHelpers.Json(svc, new
            {
                source,
                converted = false,
                note = "This workflow is already in API format and can be passed straight to comfy_submit_prompt.",
                nodeCount = graph.Count,
                workflow = graph,
            });
        }

        var objectInfo = await svc.GetJsonAsync("object_info", ct) as JsonObject
            ?? throw new McpException("Could not read /object_info from ComfyUI, which is required to name the editor's positional widget values.");

        var result = ComfyWorkflows.ConvertUiToApi(graph, objectInfo);

        return ToolHelpers.Json(svc, new
        {
            source,
            converted = true,
            nodesConverted = result.NodesConverted,
            nodesSkipped = result.NodesSkipped,
            warnings = result.Warnings,
            reliable = result.Warnings.Count == 0,
            note = result.Warnings.Count == 0
                ? "Every node mapped cleanly. Pass `workflow` to comfy_submit_prompt."
                : "Some values could not be mapped with confidence — review `warnings` before running this.",
            workflow = result.ApiGraph,
        });
    }

    // ---------------------------------------------------------------- loading

    /// <summary>
    /// Resolve a workflow name to its JSON, preferring this server's local template directory
    /// and falling back to ComfyUI's saved user workflows.
    /// </summary>
    internal static async Task<(string Json, string Source)> LoadWorkflowAsync(
        ComfyUIService svc,
        string name,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new McpException("Workflow name is required. Call comfy_list_workflows to see what is available.");

        var root = svc.ResolveWorkflowDirectory();
        if (root is not null)
        {
            foreach (var candidate in new[] { name, name + ".json" })
            {
                var path = TryResolveUnderRoot(root, candidate);
                if (path is not null && File.Exists(path))
                {
                    return (await File.ReadAllTextAsync(path, ct), "local");
                }
            }
        }

        var remoteName = name.Replace('\\', '/');
        var candidates = new List<string>();
        if (remoteName.StartsWith(ComfyWorkflowDir + "/", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(remoteName);
        }
        else
        {
            candidates.Add($"{ComfyWorkflowDir}/{remoteName}");
            candidates.Add(remoteName);
        }

        foreach (var candidate in candidates.ToList())
        {
            if (!candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) candidates.Add(candidate + ".json");
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var text = await svc.DownloadTextAsync($"userdata/{Uri.EscapeDataString(candidate)}", ct);
                if (!string.IsNullOrWhiteSpace(text)) return (text, "comfyui");
            }
            catch (ComfyHttpException ex) when (ex.IsNotFound)
            {
                // Just this spelling missing; try the next. A transport failure propagates so an
                // unreachable ComfyUI is not reported as "no workflow by that name".
            }
        }

        throw new McpException(
            $"No workflow named '{name}' was found in ComfyUI:WorkflowDirectory or in ComfyUI's saved workflows. " +
            "Call comfy_list_workflows to see the exact names.");
    }

    // ---------------------------------------------------------------- path safety

    /// <summary>Resolve a caller-supplied name under the template root, refusing to escape it.</summary>
    private static string ResolveUnderRoot(string root, string relative) =>
        TryResolveUnderRoot(root, relative)
        ?? throw new McpException($"'{relative}' resolves outside ComfyUI:WorkflowDirectory, which is not allowed.");

    private static string? TryResolveUnderRoot(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) return null;

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relative));

        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static bool Matches(string value, string? search) =>
        string.IsNullOrWhiteSpace(search) || value.Contains(search, StringComparison.OrdinalIgnoreCase);
}
