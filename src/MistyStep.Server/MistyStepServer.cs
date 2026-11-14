using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MistyStep.Server;

/// <summary>Builds the in-process Kestrel host: limits, pipeline and the four GET routes.</summary>
public static class MistyStepServer
{
    /// <summary>
    /// Creates a builder with Kestrel and the server's services. Callers may add logging providers
    /// (or, in tests, swap in a test server) before calling <see cref="Build"/>.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(ServerDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);

        // The empty builder reads no appsettings or ASPNETCORE_* variables, so nothing can add endpoints behind our back.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "MistyStep" });

        builder.Services.AddSingleton(deps);
        builder.Services.AddSingleton<RequestPipeline>();
        builder.Services.AddSingleton<Endpoints>();
        builder.Services.AddRoutingCore();

        builder.Logging.SetMinimumLevel(LogLevel.Information);
        // ASP.NET Core logs request paths and query strings at Information, and /pair?c= carries a pairing code.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // Hosting diagnostics also opens a per-request log scope holding the request path. Off entirely.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);

        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel((context, options) =>
        {
            ConfigureLimits(options);
            var pipeline = options.ApplicationServices.GetRequiredService<RequestPipeline>();
            options.Listen(deps.BindAddress, deps.State.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.Use(pipeline.ConnectionFilter);
            });
        });

        return builder;
    }

    /// <summary>Builds the app and wires the pipeline and routes.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var app = builder.Build();

        var pipeline = app.Services.GetRequiredService<RequestPipeline>();
        var endpoints = app.Services.GetRequiredService<Endpoints>();

        app.Use(pipeline.InvokeAsync);
        app.UseRouting();
        app.MapGet(RequestPipeline.MetaPath, endpoints.MetaAsync);
        app.MapGet(RequestPipeline.DataPath, endpoints.DataAsync);
        app.MapGet(RequestPipeline.PingPath, endpoints.PingAsync);
        app.MapGet(RequestPipeline.PairPath, endpoints.PairAsync);
        return app;
    }

    internal static void ConfigureLimits(KestrelServerOptions options)
    {
        options.AddServerHeader = false;
        var limits = options.Limits;
        limits.MaxRequestBodySize = 0;
        limits.MaxRequestHeadersTotalSize = 8 * 1024;
        limits.MaxRequestLineSize = 2 * 1024;
        limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        limits.MaxConcurrentConnections = 16;
    }
}

/// <summary>The port is taken. The app shows an error naming the port and never picks another one.</summary>
public sealed class PortInUseException(int port, Exception inner)
    : Exception($"Port {port} is already in use.", inner)
{
    public int Port { get; } = port;
}

/// <summary>A running server. Dispose or <see cref="StopAsync"/> to shut down within 2 seconds.</summary>
public sealed class ServerHost : IAsyncDisposable
{
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly WebApplication _app;

    private ServerHost(WebApplication app) => _app = app;

    public IServiceProvider Services => _app.Services;

    /// <summary>Starts listening. Throws <see cref="PortInUseException"/> if the port is taken.</summary>
    public static async Task<ServerHost> StartAsync(
        ServerDependencies deps, Action<ILoggingBuilder>? configureLogging = null, CancellationToken cancellationToken = default)
    {
        var builder = MistyStepServer.CreateBuilder(deps);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownTimeout);
        configureLogging?.Invoke(builder.Logging);
        var app = MistyStepServer.Build(builder);

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is AddressInUseException || ex.InnerException is AddressInUseException)
        {
            await app.DisposeAsync();
            throw new PortInUseException(deps.State.Port, ex);
        }

        return new ServerHost(app);
    }

    public async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(ShutdownTimeout);
        await _app.StopAsync(timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _app.DisposeAsync();
    }
}
