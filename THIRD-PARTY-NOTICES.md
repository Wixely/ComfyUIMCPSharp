# Third-Party Notices

ComfyUIMCPSharp is licensed under the MIT License (see `LICENSE`). It depends on
the third-party components listed below. Each remains under its own license;
this file is provided for attribution.

## NuGet packages

| Package | License |
| --- | --- |
| Microsoft.AspNetCore.App | MIT |
| Microsoft.Extensions.Hosting.WindowsServices | MIT |
| ModelContextProtocol.AspNetCore | Apache-2.0 |
| Serilog.AspNetCore | Apache-2.0 |
| Serilog.Enrichers.Environment | Apache-2.0 |
| Serilog.Enrichers.Process | Apache-2.0 |
| Serilog.Enrichers.Thread | Apache-2.0 |
| Serilog.Settings.Configuration | Apache-2.0 |
| Serilog.Sinks.Console | Apache-2.0 |
| Serilog.Sinks.File | Apache-2.0 |

The full text of the MIT and Apache-2.0 licenses is available at
<https://opensource.org/license/mit> and
<https://www.apache.org/licenses/LICENSE-2.0> respectively.

## ComfyUI protocol client

No ComfyUI client library is taken as a dependency. ComfyUI's HTTP routes
(`/prompt`, `/queue`, `/history`, `/view`, `/upload/*`, `/object_info`,
`/system_stats`, `/userdata`) and its `/ws` event stream are spoken directly
with `System.Net.Http.HttpClient` and `System.Net.WebSockets.ClientWebSocket`
from the base class library, in `Services/ComfyUIService.cs` and
`Services/ComfyProgressTracker.cs`. This keeps the dependency set MIT/Apache-2.0
and avoids pulling in ComfyUI's own GPL-3.0 code.

The workflow format helpers in `Services/ComfyWorkflows.cs` — including the
best-effort editor-format to API-format converter — are original work covered by
this project's MIT license. They are written against ComfyUI's documented
behaviour, not derived from its source.

## Trademarks

"ComfyUI" is the name of the project maintained by comfyanonymous and Comfy Org.
Use of the name here describes interoperability only and does not imply
endorsement by, or affiliation with, that project.
