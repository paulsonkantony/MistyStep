using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Auth;
using MistyStep.Core.Models;
using MistyStep.Core.Network;
using MistyStep.Core.Pairing;
using MistyStep.Core.Store;
using MistyStep.Core.Time;

namespace MistyStep.Server.Tests.Support;

public sealed class FakeClock : IClock
{
    public long MonotonicMs { get; set; } = 1_000_000;
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public void Advance(long ms)
    {
        MonotonicMs += ms;
        UtcNow = UtcNow.AddMilliseconds(ms);
    }
}

/// <summary>The server on a TestHost, with fake time and a fake caller address. No real port is opened.</summary>
public sealed class TestApp : IAsyncDisposable
{
    public const int Port = 45654;
    public const string PcName = "MY-LAPTOP";
    public const string AppVersion = "0.1.0-test";
    public const string TailscaleHost = "100.101.102.103:45654";
    public const string RemoteHeader = "X-Test-Remote";

    public static readonly IPAddress IpadAddress = IPAddress.Parse("100.90.1.2");
    public static readonly IPAddress OtherTailnetAddress = IPAddress.Parse("100.90.9.9");
    public static readonly IPAddress LanAddress = IPAddress.Parse("192.168.1.77");
    public static readonly IPAddress PublicAddress = IPAddress.Parse("203.0.113.9");

    public static readonly NetworkSnapshot Network = new(
        TailscaleAddress: IPAddress.Parse("100.101.102.103"),
        MagicDnsName: "my-laptop.tailnet-name.ts.net.",
        Hostname: PcName,
        Interfaces: [new LocalInterface(IPAddress.Parse("192.168.1.20"), 24, NetworkCategory.Private, InterfaceKind.Physical)]);

    private TestApp(WebApplication app, FakeClock clock, ClipStore store, DeviceRegistry registry, ServerState state, LogCapture logs)
    {
        App = app;
        Clock = clock;
        Store = store;
        Registry = registry;
        State = state;
        Logs = logs;
    }

    public WebApplication App { get; }
    public FakeClock Clock { get; }
    public ClipStore Store { get; }
    public DeviceRegistry Registry { get; }
    public ServerState State { get; }
    public LogCapture Logs { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<TestApp> StartAsync()
    {
        var clock = new FakeClock();
        var store = new ClipStore(clock, 10);
        var registry = new DeviceRegistry(clock);
        var state = new ServerState(Port, PcName, AppVersion, Network) { TailscaleEnabled = true };
        var deps = new ServerDependencies(store, registry, new AuthLockout(clock), new DeviceRateLimiter(clock), clock, state);
        var logs = new LogCapture();

        var builder = MistyStepServer.CreateBuilder(deps);
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IStartupFilter, FakeRemoteAddressFilter>();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);

        var app = MistyStepServer.Build(builder);
        await app.StartAsync(Ct);
        return new TestApp(app, clock, store, registry, state, logs);
    }

    public HttpClient CreateClient(string? token = null, IPAddress? remote = null)
    {
        var client = App.GetTestClient();
        client.BaseAddress = new Uri($"http://{TailscaleHost}/");
        client.DefaultRequestHeaders.Add(RemoteHeader, (remote ?? IpadAddress).ToString());
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Pairs a device through the registry and returns its token.</summary>
    public (Device Device, string Token) Pair(IPAddress? remote = null)
    {
        var code = Registry.CreatePairingCode($"http://{TailscaleHost}", PcName);
        var redeemed = (RedeemResult.Redeemed)Registry.Redeem(code.Code, remote ?? IpadAddress, "Mozilla/5.0 (iPad)");
        SetupCode.TryDecode(redeemed.SetupCode, out var info);
        return (redeemed.Device, info!.Token);
    }

    public ClipItem SetText(string text)
    {
        var item = ClipItem.CreateText(text, Clock.MonotonicMs);
        Store.Set(item);
        return item;
    }

    public ClipItem SetImage(byte[] bytes, string mime = "image/png", int width = 1920, int height = 1080)
    {
        var item = ClipItem.CreateImage(bytes, mime, width, height, Clock.MonotonicMs);
        Store.Set(item);
        return item;
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        var text = await response.Content.ReadAsStringAsync(Ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
    }

    /// <summary>TestHost gives no remote address; take it from a test header, before the real pipeline runs.</summary>
    private sealed class FakeRemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext context, RequestDelegate nextDelegate) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[RemoteHeader].ToString(), out var address))
                    context.Connection.RemoteIpAddress = address;
                context.Request.Headers.Remove(RemoteHeader);
                await nextDelegate(context);
            });
            next(app);
        };
    }
}

/// <summary>Captures every log line, its structured values and any exception text.</summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            lines.Enqueue($"{category} scope: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" | ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : "";
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} [{values}] {exception}");
        }
    }
}
