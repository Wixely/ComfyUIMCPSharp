using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace ComfyUIMCPSharp.Services;

/// <summary>Where a workflow JSON blob came from and which of ComfyUI's two shapes it is in.</summary>
public enum ComfyWorkflowFormat
{
    /// <summary>`{"3": {"class_type": …, "inputs": {…}}}` — the only shape POST /prompt accepts.</summary>
    Api,

    /// <summary>`{"nodes": […], "links": […]}` — what the ComfyUI editor saves and the UI loads.</summary>
    Ui,

    Unknown,
}

/// <summary>
/// Pure functions over ComfyUI workflow graphs: format detection, parameter overrides, the
/// built-in text-to-image graph, output extraction, and a best-effort UI -> API converter.
/// No I/O happens here so the behaviour is easy to reason about from the tool layer.
/// </summary>
public static class ComfyWorkflows
{
    /// <summary>Nodes the editor draws but the backend never executes.</summary>
    private static readonly HashSet<string> VirtualNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Reroute", "PrimitiveNode", "Note", "MarkdownNote", "PrimitiveInt", "PrimitiveFloat",
        "PrimitiveString", "PrimitiveStringMultiline", "PrimitiveBoolean",
    };

    // ---------------------------------------------------------------- parsing / detection

    public static JsonObject ParseGraph(string json, string parameterName)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new McpException($"{parameterName} is not valid JSON: {ex.Message}");
        }

        if (node is not JsonObject obj)
            throw new McpException($"{parameterName} must be a JSON object, not {node?.GetValueKind().ToString() ?? "null"}.");

        return obj;
    }

    public static ComfyWorkflowFormat DetectFormat(JsonObject graph)
    {
        if (graph["nodes"] is JsonArray) return ComfyWorkflowFormat.Ui;

        foreach (var (_, value) in graph)
        {
            if (value is JsonObject entry && entry["class_type"] is not null)
                return ComfyWorkflowFormat.Api;
        }

        return ComfyWorkflowFormat.Unknown;
    }

    /// <summary>Validate that a graph is runnable, with an error that tells the agent how to fix it.</summary>
    public static JsonObject EnsureApiFormat(JsonObject graph, string parameterName)
    {
        return DetectFormat(graph) switch
        {
            ComfyWorkflowFormat.Api => graph,
            ComfyWorkflowFormat.Ui => throw new McpException(
                $"{parameterName} is in ComfyUI's UI/editor format (it has a 'nodes' array), which POST /prompt does not accept. " +
                "Either export it from ComfyUI with Workflow > Export (API), or pass it through comfy_convert_workflow_to_api first."),
            _ => throw new McpException(
                $"{parameterName} does not look like a ComfyUI workflow. Expected an API-format object keyed by node id, " +
                "where each value has 'class_type' and 'inputs'."),
        };
    }

    // ---------------------------------------------------------------- overrides

    /// <summary>
    /// Apply <c>{"nodeRef.inputName": value}</c> overrides to an API-format graph.
    /// <c>nodeRef</c> is a node id, a node title (from <c>_meta.title</c>), or a class_type;
    /// the last two must resolve to exactly one node.
    /// </summary>
    public static List<string> ApplyOverrides(JsonObject apiGraph, JsonObject overrides)
    {
        var applied = new List<string>();

        foreach (var (key, value) in overrides)
        {
            var separator = key.LastIndexOf('.');
            if (separator <= 0 || separator == key.Length - 1)
            {
                throw new McpException(
                    $"Override key '{key}' is malformed. Use '<node-id-or-title-or-class>.<input-name>', e.g. '6.text' or 'KSampler.seed'.");
            }

            var nodeRef = key[..separator];
            var inputName = key[(separator + 1)..];
            var nodeId = ResolveNodeId(apiGraph, nodeRef);

            if (apiGraph[nodeId] is not JsonObject node)
                throw new McpException($"Override key '{key}' refers to node '{nodeId}', which is not in the workflow.");

            if (node["inputs"] is not JsonObject inputs)
            {
                inputs = new JsonObject();
                node["inputs"] = inputs;
            }

            if (!inputs.ContainsKey(inputName))
            {
                var known = string.Join(", ", inputs.Select(i => i.Key));
                throw new McpException(
                    $"Node '{nodeId}' ({node["class_type"]?.GetValue<string>() ?? "?"}) has no input '{inputName}'. Known inputs: {known}.");
            }

            inputs[inputName] = value?.DeepClone();
            applied.Add($"{nodeId}.{inputName}");
        }

        return applied;
    }

    private static string ResolveNodeId(JsonObject apiGraph, string nodeRef)
    {
        if (apiGraph.ContainsKey(nodeRef)) return nodeRef;

        var byTitle = apiGraph
            .Where(kv => kv.Value is JsonObject o
                && string.Equals(o["_meta"]?["title"]?.GetValue<string>(), nodeRef, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        if (byTitle.Count == 1) return byTitle[0];
        if (byTitle.Count > 1)
            throw new McpException($"Node title '{nodeRef}' is ambiguous — it matches nodes {string.Join(", ", byTitle)}. Use the node id instead.");

        var byClass = apiGraph
            .Where(kv => kv.Value is JsonObject o
                && string.Equals(o["class_type"]?.GetValue<string>(), nodeRef, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        if (byClass.Count == 1) return byClass[0];
        if (byClass.Count > 1)
            throw new McpException($"Class type '{nodeRef}' is ambiguous — it matches nodes {string.Join(", ", byClass)}. Use the node id instead.");

        throw new McpException($"No node in the workflow matches '{nodeRef}' by id, title, or class_type.");
    }

    // ---------------------------------------------------------------- built-in text-to-image

    /// <summary>
    /// The stock checkpoint -> CLIP -> KSampler -> VAEDecode -> SaveImage graph, in API format.
    /// Deliberately built from core nodes only so it runs on a vanilla ComfyUI install.
    /// </summary>
    public static JsonObject BuildTextToImage(
        string checkpoint,
        string prompt,
        string negativePrompt,
        int width,
        int height,
        int steps,
        double cfg,
        string samplerName,
        string scheduler,
        long seed,
        int batchSize,
        double denoise,
        string filenamePrefix)
    {
        static JsonObject Node(string classType, string title, JsonObject inputs) => new()
        {
            ["class_type"] = classType,
            ["inputs"] = inputs,
            ["_meta"] = new JsonObject { ["title"] = title },
        };

        static JsonArray Link(string nodeId, int slot) => new() { nodeId, slot };

        return new JsonObject
        {
            ["4"] = Node("CheckpointLoaderSimple", "Load Checkpoint", new JsonObject
            {
                ["ckpt_name"] = checkpoint,
            }),
            ["5"] = Node("EmptyLatentImage", "Empty Latent Image", new JsonObject
            {
                ["width"] = width,
                ["height"] = height,
                ["batch_size"] = batchSize,
            }),
            ["6"] = Node("CLIPTextEncode", "Positive Prompt", new JsonObject
            {
                ["text"] = prompt,
                ["clip"] = Link("4", 1),
            }),
            ["7"] = Node("CLIPTextEncode", "Negative Prompt", new JsonObject
            {
                ["text"] = negativePrompt,
                ["clip"] = Link("4", 1),
            }),
            ["3"] = Node("KSampler", "KSampler", new JsonObject
            {
                ["seed"] = seed,
                ["steps"] = steps,
                ["cfg"] = cfg,
                ["sampler_name"] = samplerName,
                ["scheduler"] = scheduler,
                ["denoise"] = denoise,
                ["model"] = Link("4", 0),
                ["positive"] = Link("6", 0),
                ["negative"] = Link("7", 0),
                ["latent_image"] = Link("5", 0),
            }),
            ["8"] = Node("VAEDecode", "VAE Decode", new JsonObject
            {
                ["samples"] = Link("3", 0),
                ["vae"] = Link("4", 2),
            }),
            ["9"] = Node("SaveImage", "Save Image", new JsonObject
            {
                ["filename_prefix"] = filenamePrefix,
                ["images"] = Link("8", 0),
            }),
        };
    }

    // ---------------------------------------------------------------- outputs

    /// <summary>One file produced by a run, as referenced in a /history entry's outputs block.</summary>
    public sealed record OutputRef(string NodeId, string Kind, string Filename, string Subfolder, string Type);

    /// <summary>
    /// Pull every file reference out of a /history entry. ComfyUI groups outputs per node under
    /// keys that vary by node pack ("images", "gifs", "audio", "videos", …), so anything that
    /// looks like a list of {filename, subfolder, type} objects is collected.
    /// </summary>
    public static List<OutputRef> ExtractOutputs(JsonObject historyEntry)
    {
        var results = new List<OutputRef>();
        if (historyEntry["outputs"] is not JsonObject outputs) return results;

        foreach (var (nodeId, nodeOutputs) in outputs)
        {
            if (nodeOutputs is not JsonObject group) continue;

            foreach (var (kind, value) in group)
            {
                if (value is not JsonArray array) continue;

                foreach (var item in array)
                {
                    if (item is not JsonObject file) continue;
                    var filename = file["filename"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(filename)) continue;

                    results.Add(new OutputRef(
                        nodeId,
                        kind,
                        filename!,
                        file["subfolder"]?.GetValue<string>() ?? string.Empty,
                        file["type"]?.GetValue<string>() ?? "output"));
                }
            }
        }

        return results;
    }

    public static bool IsImage(OutputRef output) =>
        Path.GetExtension(output.Filename).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";

    public static string MediaTypeFor(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        _ => "application/octet-stream",
    };

    // ---------------------------------------------------------------- UI -> API conversion

    public sealed record ConversionResult(JsonObject ApiGraph, List<string> Warnings, int NodesConverted, int NodesSkipped);

    /// <summary>
    /// Best-effort conversion of an editor-format workflow into the API format POST /prompt wants.
    ///
    /// The editor stores widget values positionally in <c>widgets_values</c>, so their names have
    /// to be recovered from <c>/object_info</c>. That mapping is faithful for core nodes but can
    /// drift for custom nodes with bespoke frontend widgets — every uncertainty is reported in
    /// <see cref="ConversionResult.Warnings"/> rather than silently guessed.
    /// </summary>
    public static ConversionResult ConvertUiToApi(JsonObject uiGraph, JsonObject objectInfo)
    {
        var warnings = new List<string>();

        if (uiGraph["nodes"] is not JsonArray nodes)
            throw new McpException("Workflow has no 'nodes' array, so it is not in ComfyUI's UI/editor format.");

        // link id -> (origin node id, origin output slot)
        var linkOrigins = new Dictionary<long, (string NodeId, int Slot)>();
        if (uiGraph["links"] is JsonArray links)
        {
            foreach (var link in links)
            {
                if (link is not JsonArray l || l.Count < 5) continue;
                var linkId = AsLong(l[0]);
                var originId = AsString(l[1]);
                var originSlot = (int)(AsLong(l[2]) ?? 0);
                if (linkId is null || originId is null) continue;
                linkOrigins[linkId.Value] = (originId, originSlot);
            }
        }

        var nodesById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is not JsonObject n) continue;
            var id = AsString(n["id"]);
            if (id is not null) nodesById[id] = n;
        }

        var api = new JsonObject();
        var converted = 0;
        var skipped = 0;

        foreach (var (id, node) in nodesById)
        {
            var classType = AsString(node["type"]) ?? AsString(node["properties"]?["Node name for S&R"]);
            if (classType is null)
            {
                warnings.Add($"Node {id} has no type and was skipped.");
                skipped++;
                continue;
            }

            var mode = (int)(AsLong(node["mode"]) ?? 0);
            if (mode is 2 or 4)
            {
                // 2 = muted, 4 = bypassed. Neither reaches the backend.
                skipped++;
                continue;
            }

            if (VirtualNodeTypes.Contains(classType))
            {
                skipped++;
                continue;
            }

            if (objectInfo[classType] is not JsonObject spec)
            {
                warnings.Add(
                    $"Node {id} is of type '{classType}', which this ComfyUI instance does not report in /object_info. " +
                    "Its widget values could not be named and it was skipped — install the custom node pack, or convert this workflow in the ComfyUI UI.");
                skipped++;
                continue;
            }

            var inputs = new JsonObject();
            var linkedInputNames = new HashSet<string>(StringComparer.Ordinal);

            // 1. Connected inputs become ["<origin node id>", <slot>].
            if (node["inputs"] is JsonArray nodeInputs)
            {
                foreach (var input in nodeInputs)
                {
                    if (input is not JsonObject i) continue;
                    var name = AsString(i["name"]);
                    if (name is null) continue;

                    var linkId = AsLong(i["link"]);
                    if (linkId is null)
                    {
                        // An unconnected converted-to-input widget: its value lives in widgets_values
                        // only on older saves. Leave it to the widget pass below.
                        continue;
                    }

                    linkedInputNames.Add(name);
                    var origin = ResolveOrigin(linkId.Value, linkOrigins, nodesById, warnings, id, name);
                    if (origin is null)
                    {
                        warnings.Add($"Node {id}.{name} is connected to a node that does not execute; the connection was dropped.");
                        continue;
                    }

                    inputs[name] = new JsonArray { origin.Value.NodeId, origin.Value.Slot };
                }
            }

            // 2. Widget values are positional; recover their names from /object_info.
            var widgetValues = node["widgets_values"];
            AssignWidgetValues(id, classType, spec, widgetValues, linkedInputNames, inputs, warnings);

            var entry = new JsonObject
            {
                ["class_type"] = classType,
                ["inputs"] = inputs,
            };

            var title = AsString(node["title"]);
            if (!string.IsNullOrWhiteSpace(title))
            {
                entry["_meta"] = new JsonObject { ["title"] = title };
            }

            api[id] = entry;
            converted++;
        }

        if (converted == 0)
            throw new McpException("Nothing in this workflow could be converted to API format. " + string.Join(" ", warnings));

        PruneDanglingLinks(api, warnings);

        return new ConversionResult(api, warnings, converted, skipped);
    }

    /// <summary>
    /// Drop any link that points at a node which did not make it into the converted graph.
    /// A node can be left out for several reasons — muted, missing from /object_info, untyped —
    /// and a reference to one would be rejected by ComfyUI with an error that names the missing
    /// node rather than the real cause. Removing the edge and saying so is far more useful.
    /// </summary>
    private static void PruneDanglingLinks(JsonObject api, List<string> warnings)
    {
        foreach (var (nodeId, node) in api)
        {
            if (node is not JsonObject entry || entry["inputs"] is not JsonObject inputs) continue;

            var dead = new List<(string InputName, string OriginId)>();
            foreach (var (inputName, value) in inputs)
            {
                // A link is ["<origin node id>", <slot>]; anything else is a literal widget value.
                if (value is not JsonArray link || link.Count < 1) continue;
                var originId = AsString(link[0]);
                if (originId is null || api.ContainsKey(originId)) continue;
                dead.Add((inputName, originId));
            }

            foreach (var (inputName, originId) in dead)
            {
                inputs.Remove(inputName);
                warnings.Add(
                    $"Node {nodeId}.{inputName} referenced node {originId}, which is not part of the executable graph " +
                    "(muted, bypassed with nothing behind it, or an uninstalled custom node); the connection was removed. " +
                    "That input now has no value, so supply one before running this workflow.");
            }
        }
    }

    /// <summary>Follow a link back to a node that actually executes, hopping reroutes and bypasses.</summary>
    private static (string NodeId, int Slot)? ResolveOrigin(
        long linkId,
        Dictionary<long, (string NodeId, int Slot)> linkOrigins,
        Dictionary<string, JsonObject> nodesById,
        List<string> warnings,
        string forNodeId,
        string forInputName,
        int depth = 0)
    {
        if (depth > 32)
        {
            warnings.Add($"Node {forNodeId}.{forInputName}: gave up following a reroute chain more than 32 hops long.");
            return null;
        }

        if (!linkOrigins.TryGetValue(linkId, out var origin)) return null;
        if (!nodesById.TryGetValue(origin.NodeId, out var originNode)) return origin;

        var originType = AsString(originNode["type"]) ?? string.Empty;
        var originMode = (int)(AsLong(originNode["mode"]) ?? 0);

        // A muted node produces nothing, so the edge is dead however it is wired.
        // This has to be checked before the passthrough test: a muted node is not a passthrough,
        // and returning it here would leave a link to a node absent from the converted graph.
        if (originMode == 2) return null;

        var isPassthrough = VirtualNodeTypes.Contains(originType) || originMode == 4;
        if (!isPassthrough) return origin;

        // Reroute / bypassed node: hop to whatever feeds its matching input.
        if (originNode["inputs"] is not JsonArray originInputs || originInputs.Count == 0)
        {
            return null;
        }

        // For a bypassed node ComfyUI matches by type; a reroute has exactly one input.
        var wantedType = AsString((originNode["outputs"] as JsonArray)?.ElementAtOrDefault(origin.Slot)?["type"]);
        JsonObject? chosen = null;
        foreach (var candidate in originInputs.OfType<JsonObject>())
        {
            if (wantedType is null || string.Equals(AsString(candidate["type"]), wantedType, StringComparison.OrdinalIgnoreCase))
            {
                chosen = candidate;
                break;
            }
        }
        chosen ??= originInputs.OfType<JsonObject>().FirstOrDefault();

        var nextLink = AsLong(chosen?["link"]);
        if (nextLink is null) return null;

        return ResolveOrigin(nextLink.Value, linkOrigins, nodesById, warnings, forNodeId, forInputName, depth + 1);
    }

    /// <summary>
    /// Walk the node's declared widget inputs in <c>input_order</c> sequence and pull values off
    /// <c>widgets_values</c> positionally, accounting for the extra slots the editor inserts for
    /// seed randomisation (<c>control_after_generate</c>) and image upload buttons.
    /// </summary>
    private static void AssignWidgetValues(
        string nodeId,
        string classType,
        JsonObject spec,
        JsonNode? widgetValues,
        HashSet<string> linkedInputNames,
        JsonObject inputs,
        List<string> warnings)
    {
        if (widgetValues is null) return;

        // Newer saves use an object keyed by widget name — no positional guessing needed.
        if (widgetValues is JsonObject named)
        {
            foreach (var (name, value) in named)
            {
                if (linkedInputNames.Contains(name)) continue;
                inputs[name] = value?.DeepClone();
            }
            return;
        }

        if (widgetValues is not JsonArray values) return;

        var inputSpec = spec["input"] as JsonObject;
        var order = new List<string>();
        if (spec["input_order"] is JsonObject inputOrder)
        {
            foreach (var section in new[] { "required", "optional" })
            {
                if (inputOrder[section] is JsonArray names)
                {
                    order.AddRange(names.Select(n => AsString(n)).Where(n => n is not null)!);
                }
            }
        }
        else if (inputSpec is not null)
        {
            foreach (var section in new[] { "required", "optional" })
            {
                if (inputSpec[section] is JsonObject sectionObj) order.AddRange(sectionObj.Select(kv => kv.Key));
            }
        }

        var index = 0;
        foreach (var name in order)
        {
            if (index >= values.Count) break;

            var definition = FindInputDefinition(inputSpec, name);
            if (definition is null) continue;
            if (!IsWidgetInput(definition)) continue;      // link-only input (MODEL, CLIP, IMAGE, …)
            if (linkedInputNames.Contains(name))
            {
                // The editor still stores a placeholder for widgets converted to inputs.
                index++;
                continue;
            }

            inputs[name] = values[index]?.DeepClone();
            index++;

            if (HasExtraWidgetSlot(definition, name)) index++;
        }

        if (index < values.Count)
        {
            warnings.Add(
                $"Node {nodeId} ({classType}): {values.Count - index} widget value(s) could not be matched to a named input " +
                "and were dropped. Check this node's parameters before relying on the result.");
        }
    }

    private static JsonNode? FindInputDefinition(JsonObject? inputSpec, string name)
    {
        if (inputSpec is null) return null;
        foreach (var section in new[] { "required", "optional" })
        {
            if (inputSpec[section] is JsonObject sectionObj && sectionObj.TryGetPropertyValue(name, out var def))
                return def;
        }
        return null;
    }

    /// <summary>
    /// An input is a widget when its declared type is a combo (a list of choices) or one of the
    /// scalar types the editor renders as a control. Everything else arrives over a link.
    /// </summary>
    private static bool IsWidgetInput(JsonNode definition)
    {
        if (definition is not JsonArray def || def.Count == 0) return false;

        if (def[0] is JsonArray) return true; // combo box: [["a","b"], {...}]

        var type = AsString(def[0]);
        return type is "INT" or "FLOAT" or "STRING" or "BOOLEAN" or "COMBO";
    }

    /// <summary>
    /// The editor appends a hidden widget after certain controls: a control_after_generate mode
    /// selector for seeds, and an upload button for image pickers. Both occupy a slot in
    /// <c>widgets_values</c> without corresponding to a backend input.
    /// </summary>
    private static bool HasExtraWidgetSlot(JsonNode definition, string name)
    {
        var opts = definition is JsonArray def && def.Count > 1 ? def[1] as JsonObject : null;

        if (IsTrue(opts?["control_after_generate"])) return true;
        if (IsTrue(opts?["image_upload"])) return true;

        return name is "seed" or "noise_seed";
    }

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    // ---------------------------------------------------------------- small JSON helpers

    private static string? AsString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var d)) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static long? AsLong(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return (long)d;
        if (value.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }
}
