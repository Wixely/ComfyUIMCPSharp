using System.Net;
using ComfyUIMCPSharp.Configuration;
using ComfyUIMCPSharp.Hosting;
using ComfyUIMCPSharp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

namespace ComfyUIMCPSharp;

public static class Program
{
    public static int Main(string[] args)
    {
        var contentRoot = GetContentRoot();
        var isService = WindowsServiceHelpers.IsWindowsService();
        if (!isService)
        {
            McpSharpIcon.ApplyConsoleWindowIcon();
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "comfyuimcp-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

            builder.Configuration
                .SetBasePath(contentRoot)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.Local.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "ComfyUIMCPSharp.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"ComfyUIMCPSharp.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "ComfyUIMCPSharp.Local.json"), optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "COMFYUIMCP_")
                .AddCommandLine(args);

            if (isService)
            {
                var svcOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
                builder.Host.UseWindowsService(o => o.ServiceName = svcOptions.WindowsServiceName);
            }

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext());

            builder.Services.Configure<ComfyUIOptions>(
                builder.Configuration.GetSection(ComfyUIOptions.SectionName));
            builder.Services.Configure<ServerOptions>(
                builder.Configuration.GetSection(ServerOptions.SectionName));

            builder.Services.AddSingleton<ComfyUIService>();
            builder.Services.AddSingleton<ComfyProgressTracker>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<ComfyProgressTracker>());

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly();

            var server = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    k.ListenLocalhost(server.Port);
                }
                else if (IPAddress.TryParse(server.Host, out var ip))
                {
                    k.Listen(ip, server.Port);
                }
                else
                {
                    k.ListenAnyIP(server.Port);
                }
            });

            var app = builder.Build();

            app.UseSerilogRequestLogging();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };

            var comfy = app.Services.GetRequiredService<ComfyUIService>();
            LogStartup(
                "ComfyUIMCPSharp",
                $"http://{server.Host}:{server.Port}{server.Path}",
                "HTTP (Streamable)",
                isService ? "WindowsService" : "Console",
                contentRoot,
                $"Read-only: {comfy.IsReadOnly}",
                $"Allow queue control: {comfy.Options.AllowQueueControl}",
                $"Allow delete: {comfy.Options.AllowDelete}",
                $"Uploads enabled: {comfy.Options.EnableUploads}",
                $"Progress stream: {comfy.Options.EnableProgressStream}",
                $"Inline images: {comfy.Options.ReturnImagesInline}",
                $"ComfyUI base: {comfy.BaseUri}",
                $"ComfyUI websocket: {comfy.WebSocketUri}",
                $"Output directory: {comfy.ResolveOutputDirectory()}");

            app.UseMiddleware<McpPasswordMiddleware>();

            app.MapFavicon();
            app.MapGet("/healthz", (ComfyProgressTracker tracker) => new
            {
                status = "ok",
                server = "ComfyUIMCPSharp",
                path = server.Path,
                readOnly = comfy.IsReadOnly,
                allowQueueControl = comfy.Options.AllowQueueControl,
                allowDelete = comfy.Options.AllowDelete,
                comfyBaseUrl = comfy.BaseUri.ToString(),
                progressStream = comfy.Options.EnableProgressStream,
                progressStreamConnected = tracker.IsConnected,
                clientId = tracker.ClientId,
                timeUtc = DateTimeOffset.UtcNow,
            });
            app.MapMcp(server.Path);

            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void LogStartup(string serviceName, string endpoint, string transport, string mode, string contentRoot, params string[] details)
    {
        var startupLog = Log.ForContext("SourceContext", serviceName + ".Startup");
        startupLog.Information("{ServiceName} startup", serviceName);
        startupLog.Information("  Endpoint: {Endpoint}", endpoint);
        startupLog.Information("  Transport: {Transport}", transport);
        startupLog.Information("  Mode: {Mode}", mode);
        foreach (var detail in details)
        {
            startupLog.Information("  {Detail}", detail);
        }
        startupLog.Information("  Content root: {ContentRoot}", contentRoot);
    }

    private static string GetContentRoot() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string ResolveConfigFile(string contentRoot, string fileName)
    {
        if (File.Exists(Path.Combine(contentRoot, fileName)))
        {
            return fileName;
        }

        try
        {
            var match = Directory.EnumerateFiles(contentRoot, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));

            return match is null ? fileName : Path.GetFileName(match);
        }
        catch (DirectoryNotFoundException)
        {
            return fileName;
        }
    }
}
