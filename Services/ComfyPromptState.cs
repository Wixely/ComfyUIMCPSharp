using System.Text.Json.Nodes;

namespace ComfyUIMCPSharp.Services;

/// <summary>
/// Live execution state for one queued prompt, assembled from ComfyUI's /ws event stream.
/// Callers waiting on a prompt await <see cref="WaitForChangeAsync"/> so they can forward
/// each progress tick onward as an MCP progress notification without polling hard.
/// </summary>
public sealed class ComfyPromptState
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ComfyPromptState(string promptId)
    {
        PromptId = promptId;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public string PromptId { get; }

    /// <summary>queued | running | completed | error | interrupted</summary>
    public string Status { get; private set; } = "queued";

    /// <summary>Node id currently executing, as reported by the `executing` event.</summary>
    public string? CurrentNodeId { get; private set; }

    /// <summary>class_type of <see cref="CurrentNodeId"/>, resolved from the submitted graph.</summary>
    public string? CurrentNodeType { get; private set; }

    /// <summary>Steps done within the current node (e.g. sampler step), from the `progress` event.</summary>
    public int ProgressValue { get; private set; }

    /// <summary>Total steps for the current node, from the `progress` event.</summary>
    public int ProgressMax { get; private set; }

    /// <summary>Count of nodes that have finished executing.</summary>
    public int ExecutedNodeCount { get; private set; }

    /// <summary>Total nodes in the submitted graph, when known.</summary>
    public int TotalNodeCount { get; private set; }

    /// <summary>Nodes ComfyUI skipped because their result was cached.</summary>
    public int CachedNodeCount { get; private set; }

    /// <summary>Payload of `execution_error` / `execution_interrupted`, when one arrived.</summary>
    public JsonObject? Error { get; private set; }

    public DateTimeOffset? StartedUtc { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    /// <summary>Node id -> class_type for the submitted graph, so progress messages can be named.</summary>
    private IReadOnlyDictionary<string, string> _nodeTypes = new Dictionary<string, string>();

    public bool IsTerminal => Status is "completed" or "error" or "interrupted";

    public void AttachGraph(JsonObject workflow)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, node) in workflow)
        {
            if (node is JsonObject obj && obj["class_type"]?.GetValue<string>() is { } classType)
            {
                map[id] = classType;
            }
        }

        lock (_gate)
        {
            _nodeTypes = map;
            TotalNodeCount = map.Count;
        }
        Signal();
    }

    public void MarkStarted()
    {
        lock (_gate)
        {
            StartedUtc ??= DateTimeOffset.UtcNow;
            if (Status == "queued") Status = "running";
            UpdatedUtc = DateTimeOffset.UtcNow;
        }
        Signal();
    }

    public void MarkExecutingNode(string? nodeId)
    {
        lock (_gate)
        {
            if (nodeId is null)
            {
                // Legacy completion signal: `executing` with a null node ends the run.
                if (!IsTerminal) Status = "completed";
            }
            else
            {
                if (CurrentNodeId is not null && CurrentNodeId != nodeId) ExecutedNodeCount++;
                Status = "running";
                StartedUtc ??= DateTimeOffset.UtcNow;
                CurrentNodeId = nodeId;
                CurrentNodeType = _nodeTypes.TryGetValue(nodeId, out var t) ? t : null;
                ProgressValue = 0;
                ProgressMax = 0;
            }
            UpdatedUtc = DateTimeOffset.UtcNow;
        }
        Signal();
    }

    public void MarkNodeProgress(string? nodeId, int value, int max)
    {
        lock (_gate)
        {
            if (nodeId is not null && nodeId != CurrentNodeId)
            {
                CurrentNodeId = nodeId;
                CurrentNodeType = _nodeTypes.TryGetValue(nodeId, out var t) ? t : null;
            }
            Status = "running";
            StartedUtc ??= DateTimeOffset.UtcNow;
            ProgressValue = value;
            ProgressMax = max;
            UpdatedUtc = DateTimeOffset.UtcNow;
        }
        Signal();
    }

    public void MarkNodeExecuted()
    {
        lock (_gate)
        {
            ExecutedNodeCount++;
            UpdatedUtc = DateTimeOffset.UtcNow;
        }
        Signal();
    }

    public void MarkCachedNodes(int count)
    {
        lock (_gate)
        {
            CachedNodeCount = count;
            ExecutedNodeCount += count;
            UpdatedUtc = DateTimeOffset.UtcNow;
        }
        Signal();
    }

    public void MarkFinished(string status, JsonObject? error = null)
    {
        lock (_gate)
        {
            Status = status;
            Error = error;
            CurrentNodeId = null;
            CurrentNodeType = null;
            UpdatedUtc = DateTimeOffset.UtcNow;
        }
        Signal();
    }

    /// <summary>
    /// Fraction of the whole run completed, 0..1, when enough is known to estimate it.
    /// Node-level progress (sampler steps) is folded into the node currently running.
    /// </summary>
    public double? OverallFraction
    {
        get
        {
            lock (_gate)
            {
                if (IsTerminal) return 1.0;
                if (TotalNodeCount <= 0) return null;
                var withinNode = ProgressMax > 0 ? Math.Clamp((double)ProgressValue / ProgressMax, 0, 1) : 0;
                var done = Math.Clamp(ExecutedNodeCount + withinNode, 0, TotalNodeCount);
                return done / TotalNodeCount;
            }
        }
    }

    /// <summary>Human-readable one-liner used as the progress notification message.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (Status == "queued") return "queued in ComfyUI";
            if (IsTerminal) return $"{Status}";

            var node = CurrentNodeType is null
                ? (CurrentNodeId is null ? "graph" : $"node {CurrentNodeId}")
                : $"{CurrentNodeType} (node {CurrentNodeId})";
            var step = ProgressMax > 0 ? $" step {ProgressValue}/{ProgressMax}" : string.Empty;
            var nodes = TotalNodeCount > 0 ? $" [{Math.Min(ExecutedNodeCount, TotalNodeCount)}/{TotalNodeCount} nodes]" : string.Empty;
            return $"running {node}{step}{nodes}";
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                promptId = PromptId,
                status = Status,
                currentNodeId = CurrentNodeId,
                currentNodeType = CurrentNodeType,
                stepValue = ProgressValue,
                stepMax = ProgressMax,
                executedNodes = ExecutedNodeCount,
                cachedNodes = CachedNodeCount,
                totalNodes = TotalNodeCount,
                fraction = OverallFraction,
                startedUtc = StartedUtc,
                updatedUtc = UpdatedUtc,
                error = Error?.DeepClone(),
            };
        }
    }

    /// <summary>Completes as soon as any field changes, or when <paramref name="timeout"/> elapses.</summary>
    public async Task WaitForChangeAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task changed;
        lock (_gate)
        {
            changed = _changed.Task;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await changed.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out waiting for a change; the caller re-reads state and decides what to do.
        }
    }

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        previous.TrySetResult();
    }
}
