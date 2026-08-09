using System.Net.Http.Headers;
using System.Text;
using ComfyUIMCPSharp.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ComfyUIMCPSharp.Hosting;

public sealed class McpPasswordMiddleware
{
    private const string HeaderName = "X-MCP-Password";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<ServerOptions> _optionsMonitor;

    public McpPasswordMiddleware(RequestDelegate next, IOptionsMonitor<ServerOptions> optionsMonitor)
    {
        _next = next;
        _optionsMonitor = optionsMonitor;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!context.Request.Path.StartsWithSegments(options.Path, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(options.Password))
        {
            await _next(context);
            return;
        }

        if (PasswordMatches(context.Request, options.Password))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer, Basic";
        await context.Response.WriteAsync("MCP password required.");
    }

    private static bool PasswordMatches(HttpRequest request, string expected)
    {
        if (request.Headers.TryGetValue(HeaderName, out var passwordHeader)
            && string.Equals(passwordHeader.ToString(), expected, StringComparison.Ordinal))
        {
            return true;
        }

        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var auth))
        {
            return false;
        }

        if (string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(auth.Parameter, expected, StringComparison.Ordinal);
        }

        if (string.Equals(auth.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(auth.Parameter))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter));
                var separator = decoded.IndexOf(':');
                return separator >= 0 && string.Equals(decoded[(separator + 1)..], expected, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }
}
