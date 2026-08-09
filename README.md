# ComfyUIMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **[ComfyUI](https://github.com/comfyanonymous/ComfyUI)** over Streamable HTTP. It speaks ComfyUI's own HTTP API and `/ws` event stream, so an agent can inspect what a local ComfyUI can do, queue work on it, watch the render progress live, and get the finished images back.

Default port: **5715**. MCP endpoint: `http://localhost:5715/mcp`.

## Features

- HTTP MCP server on the Streamable HTTP transport.
- **Live progress streaming.** The server holds a WebSocket open to ComfyUI and turns its execution events into MCP progress notifications, so the client sees `KSampler (node 3) step 12/20 [4/7 nodes]` while the tool call is still running.
- **Images come back as images.** Finished renders are returned as MCP image content, so a vision-capable client can look at what it just made. Oversized files spill to disk instead.
- **Read-only by default** — nothing gets queued on your GPU until you set `ComfyUI:ReadOnly=false`. Interrupting or deleting other people's work needs a second gate on top.
- One-shot text-to-image without a workflow file, plus full arbitrary API-format graph submission for everything else.
- Node catalogue and model discovery, so an agent can find out which checkpoints, LoRAs, samplers, and node types this instance actually has before it builds a graph.
- Best-effort converter from ComfyUI's editor save format to the API format `POST /prompt` requires.
- Configuration via `ComfyUIMCPSharp.json`, environment variables, or command line.
- Serilog logging to console and rolling files (daily + 50 MB rollover, 14-file retention).
- Runs as a console app, Windows Service, or Docker container.

## Quick start

Point it at your ComfyUI and allow it to generate:

```sh
dotnet run -- --ComfyUI:BaseUrl=http://127.0.0.1:8188/ --ComfyUI:ReadOnly=false
```

Then point your MCP client at `http://localhost:5715/mcp` and ask it to generate something. `http://localhost:5715/healthz` confirms the server is up and shows whether the ComfyUI event stream is connected.

Startup logs the operating posture so a misconfigured deployment is obvious:

```text
ComfyUIMCPSharp startup
  Endpoint: http://localhost:5715/mcp
  Transport: HTTP (Streamable)
  Mode: Console
  Read-only: False
  Allow queue control: False
  Allow delete: False
  Uploads enabled: True
  Progress stream: True
  Inline images: True
  ComfyUI base: http://127.0.0.1:8188/
  ComfyUI websocket: ws://127.0.0.1:8188/ws
  Content root: C:\Services\ComfyUIMCPSharp
```

## Configuration

Configure via `ComfyUIMCPSharp.json` or environment variables. Environment variables win over JSON; in Docker use the `COMFYUIMCP_` prefix and `__` for nested keys.

| Setting | Default | Description |
| --- | --- | --- |
| `ComfyUI:BaseUrl` | `http://127.0.0.1:8188/` | ComfyUI's HTTP address. The `/ws` URL is derived from it. |
| `ComfyUI:ApiKey` | _(none)_ | Optional `Authorization: Bearer` value for a proxied or hosted ComfyUI. |
| `ComfyUI:ComfyUser` | _(none)_ | `Comfy-User` header, needed by `/userdata` when ComfyUI runs multi-user. |
| `ComfyUI:ReadOnly` | `true` | When `true`, nothing can be queued, uploaded, or deleted. **Set `false` to generate.** |
| `ComfyUI:AllowQueueControl` | `false` | Second gate for `comfy_interrupt`, `comfy_clear_queue`, `comfy_delete_queue_items`, `comfy_free_memory`. |
| `ComfyUI:AllowDelete` | `false` | Second gate for history deletion. |
| `ComfyUI:EnableUploads` | `true` | Allows `comfy_upload_image` / `comfy_upload_mask` (still needs `ReadOnly=false`). |
| `ComfyUI:UploadSourceDirectory` | _(none)_ | Directory uploads may read `sourcePath` from. Blank refuses `sourcePath` and allows only inline `contentBase64`. |
| `ComfyUI:EnableWorkflowSave` | `false` | Allows `comfy_save_workflow`. |
| `ComfyUI:EnableProgressStream` | `true` | Keep a WebSocket open for live progress. When `false`, waits poll `/history` instead. |
| `ComfyUI:ReturnImagesInline` | `true` | Return renders as MCP image content rather than only paths. |
| `ComfyUI:MaxInlineImageBytes` | `4000000` | Images above this are written to disk instead of inlined. |
| `ComfyUI:MaxInlineImagesPerCall` | `4` | Cap on images returned inline per call, so a big batch can't flood the client. |
| `ComfyUI:OutputDirectory` | _(temp)_ | Where downloads land. Defaults to `<TEMP>/ComfyUIMCPSharp`. |
| `ComfyUI:WorkflowDirectory` | _(none)_ | Local directory of API-format workflow templates runnable by name. |
| `ComfyUI:DefaultWaitSeconds` | `300` | Default block time for wait-enabled tools. |
| `ComfyUI:MaxWaitSeconds` | `1800` | Hard ceiling on any requested wait. |
| `ComfyUI:PollIntervalMs` | `750` | History poll interval while waiting. |
| `ComfyUI:MaxNodeTypesReturned` | `200` | Cap on `comfy_list_node_types` results. |
| `ComfyUI:MaxHistoryItems` | `25` | Cap on `comfy_list_history` results. |
| `ComfyUI:MaxJsonChars` | `60000` | Cap on any single JSON blob echoed back. |
| `ComfyUI:DefaultCheckpoint` | _(none)_ | Checkpoint for `comfy_generate_image` when none is given. Blank = first installed. |
| `ComfyUI:DefaultSampler` / `DefaultScheduler` | `euler` / `normal` | Defaults for `comfy_generate_image`. |
| `ComfyUI:RequestTimeoutSeconds` | `120` | HTTP timeout for calls to ComfyUI. |
| `ComfyUI:UserAgent` | `ComfyUIMCPSharp` | UA header. |
| `ComfyUI:AllowInvalidCertificate` | `false` | Skip TLS verification (self-signed reverse proxies only). |
| `Server:Host` | `localhost` | Host to bind. |
| `Server:Port` | `5715` | HTTP port. |
| `Server:Path` | `/mcp` | MCP endpoint path. |
| `Server:WindowsServiceName` | `ComfyUIMCPSharp` | Service name when running under SCM. |
| `Server:Password` | blank | Optional MCP endpoint password; blank disables password auth. |

When `Server:Password` is set, MCP requests must provide it as `Authorization: Bearer <password>`, the Basic auth password, or `X-MCP-Password`.

Nested keys use `__`, for example `COMFYUIMCP_ComfyUI__BaseUrl=http://gpu-box:8188/`. Booleans use `true` or `false`.

## Tools

### Discovery
- `comfy_system_stats` — reachability, versions, per-device VRAM/RAM, and this server's current safety posture.
- `comfy_list_node_types` — installed node types with category and description, filterable and paged.
- `comfy_get_node_info` — full input/output schema for named nodes, including every allowed dropdown value.
- `comfy_list_model_folders`, `comfy_list_models` — what checkpoints, LoRAs, VAEs, and ControlNets are installed.
- `comfy_list_embeddings`, `comfy_get_features`.

### Generating
- `comfy_generate_image` — text-to-image with no workflow file: prompt, negative, checkpoint, size, steps, cfg, sampler, scheduler, seed, batch. Waits and streams progress by default, then returns the images.
- `comfy_submit_prompt` — queue any API-format workflow, with optional input overrides.
- `comfy_run_workflow` — run a stored workflow by name with overrides.
- `comfy_wait_for_prompt` — block on an already-queued run, streaming progress.
- `comfy_get_prompt_status` — non-blocking check: queue position, current node and step, or finished with outputs.

Overrides are a JSON object keyed `"<node-id-or-title-or-class>.<input>"`, e.g. `{"6.text": "a red fox", "3.seed": 42}`.

### Queue
- `comfy_get_queue` — running and pending jobs.
- `comfy_interrupt`, `comfy_delete_queue_items`, `comfy_clear_queue`, `comfy_free_memory` — need `AllowQueueControl=true`.

### History and outputs
- `comfy_list_history`, `comfy_get_history` — what ran, whether it succeeded, and the error detail when it didn't.
- `comfy_delete_history_items`, `comfy_clear_history` — need `AllowDelete=true`.
- `comfy_list_outputs` — the files a run produced.
- `comfy_get_image` — fetch one image back as viewable image content.
- `comfy_download_output` — save any produced file to `OutputDirectory`.

### Inputs and workflows
- `comfy_upload_image`, `comfy_upload_mask` — put source images into ComfyUI's input folder for img2img/inpainting/ControlNet.
- `comfy_list_workflows`, `comfy_get_workflow`, `comfy_save_workflow`.
- `comfy_convert_workflow_to_api` — best-effort conversion of an editor-saved workflow into the API format.

## Workflow formats

ComfyUI has two workflow shapes and only one of them can be queued:

- **API format** — an object keyed by node id, each with `class_type` and `inputs`. This is what `POST /prompt` accepts, and what `comfy_submit_prompt` / `comfy_run_workflow` need. Export it from the ComfyUI editor with **Workflow → Export (API)**.
- **UI/editor format** — has a `nodes` array and stores widget values positionally. This is what the editor saves and what `comfy_list_workflows` finds under ComfyUI's own saved workflows.

Passing the UI format to a run tool fails with an explanation rather than a confusing backend error. `comfy_convert_workflow_to_api` will convert it by recovering widget names from `/object_info`, which is faithful for core nodes but can drift for custom node packs with bespoke frontend widgets — it reports every uncertainty in `warnings` and sets `reliable: false` when there are any. Re-exporting from the editor is always the safer route.

## Progress streaming

`comfy_generate_image`, `comfy_submit_prompt`, `comfy_run_workflow`, and `comfy_wait_for_prompt` all report progress while they block. The server submits every prompt with the client id of its own `/ws` session, so ComfyUI addresses execution events back to it; those events become MCP progress notifications carrying a percentage and a message naming the current node and sampler step.

If the WebSocket is down — ComfyUI restarting, `EnableProgressStream=false`, a proxy that blocks upgrades — waits fall back to polling `/history` and `/queue`. Progress messages get coarser, but nothing breaks. `/healthz` reports `progressStreamConnected` so you can tell which mode you are in.

## Docker

Tagged releases publish a multi-arch image to GitHub Container Registry:

```sh
docker pull ghcr.io/wixely/comfyuimcpsharp:<version>
docker run --rm -p 5715:5715 \
  -e COMFYUIMCP_ComfyUI__BaseUrl=http://host.docker.internal:8188/ \
  -e COMFYUIMCP_ComfyUI__ReadOnly=false \
  -e COMFYUIMCP_Server__Password=change-me \
  -v comfyuimcp-outputs:/app/outputs \
  ghcr.io/wixely/comfyuimcpsharp:<version>
```

The image supports `linux/amd64` and `linux/arm64`. Read-only mode is on by default; set `COMFYUIMCP_ComfyUI__ReadOnly=false` to let the agent queue work.

## Running as a Windows Service

The host detects when it's launched by the Service Control Manager and switches to service mode automatically (config and logs resolve from the executable directory, not the SCM's `C:\Windows\System32` working directory).

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\ComfyUIMCPSharp

sc.exe create ComfyUIMCPSharp `
    binPath= "C:\Services\ComfyUIMCPSharp\ComfyUIMCPSharp.exe" `
    start= auto `
    DisplayName= "ComfyUI MCP (C#)"
sc.exe description ComfyUIMCPSharp "MCP server for ComfyUI."
sc.exe start ComfyUIMCPSharp
```

Put any API key in `C:\Services\ComfyUIMCPSharp\ComfyUIMCPSharp.Local.json` (or set `COMFYUIMCP_ComfyUI__ApiKey` as a machine-level env var) — never in `ComfyUIMCPSharp.json`, which is checked in.

To remove:

```powershell
sc.exe stop ComfyUIMCPSharp
sc.exe delete ComfyUIMCPSharp
```

Logs land in `<install-dir>\logs\comfyuimcp-*.log`.

## Safety model

- **Read-only by default.** Queueing work spends GPU time and writes files, so it counts as a write. Every such tool calls `EnsureWriteAllowed` and fails with an error naming the exact config key to flip.
- **Queue control is double-gated.** Interrupting, clearing, or freeing memory affects runs this agent may not have started, so those need `AllowQueueControl=true` as well.
- **Deletes are double-gated.** History deletion needs `AllowDelete=true`.
- **Feature toggles.** Uploads and workflow saving can each be switched off entirely.
- **Payload caps.** Inline images, node listings, history listings, and echoed JSON are all capped; oversized content spills to disk, or comes back in a small valid envelope carrying the leading fragment, rather than flooding the MCP channel or returning JSON cut mid-token.
- **Path containment.** Workflow names resolve under `WorkflowDirectory`, and upload `sourcePath` values under `UploadSourceDirectory`; anything escaping either root is refused. Uploads land in ComfyUI's input folder, which `comfy_get_image(type: "input")` can read back — so `sourcePath` stays disabled until you name a directory for it, and inline `contentBase64` is the default route.

## Reading a result

Every run-shaped result carries both `completed` (a history entry exists) and `succeeded` (ComfyUI's own verdict). They are not the same thing: **ComfyUI writes a history entry for failed runs too**, so check `succeeded` rather than inferring success from `completed` or from an empty output list. When `succeeded` is `false`, `comfy_get_history` has the per-node error detail.
