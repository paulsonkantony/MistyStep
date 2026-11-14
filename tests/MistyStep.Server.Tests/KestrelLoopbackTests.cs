using System.Net;
using System.Net.Sockets;
using System.Text;
using MistyStep.Core.Auth;
using MistyStep.Core.Network;
using MistyStep.Core.Store;
using MistyStep.Server.Tests.Support;
using static MistyStep.Server.Tests.Support.TestApp;

namespace MistyStep.Server.Tests;

/// <summary>
/// A few checks that only real Kestrel can answer: header-size limits, the Server header and port clashes.
/// They bind 127.0.0.1 only, so they need no firewall rule and are invisible to the network.
/// </summary>
public sealed class KestrelLoopbackTests
{
    private static ServerDependencies NewDeps(int port, FakeClock clock, out ServerState state)
    {
        state = new ServerState(port, PcName, AppVersion, NetworkSnapshot.Empty(PcName));
        return new ServerDependencies(new ClipStore(clock, 10), new DeviceRegistry(clock), new AuthLockout(clock),
            new DeviceRateLimiter(clock), clock, state) { BindAddress = IPAddress.Loopback };
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Sends raw bytes and returns the status line, or null if the server closed the connection.</summary>
    private static async Task<string?> SendRawAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadLineAsync(Ct);
    }

    [Fact]
    public async Task Taken_port_raises_PortInUseException_naming_the_port()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var ex = await Assert.ThrowsAsync<PortInUseException>(() => ServerHost.StartAsync(NewDeps(port, new FakeClock(), out _), cancellationToken: Ct));
        Assert.Equal(port, ex.Port);
        Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_kestrel_enforces_limits_and_sends_no_server_header()
    {
        var port = FreePort();
        var clock = new FakeClock();
        var deps = NewDeps(port, clock, out _);
        await using var host = await ServerHost.StartAsync(deps, cancellationToken: Ct);

        var code = deps.Registry.CreatePairingCode($"http://127.0.0.1:{port}", PcName);
        var redeemed = (RedeemResult.Redeemed)deps.Registry.Redeem(code.Code, IPAddress.Loopback, null);
        MistyStep.Core.Pairing.SetupCode.TryDecode(redeemed.SetupCode, out var info);
        var host127 = $"127.0.0.1:{port}";

        // A normal request works and carries no Server header.
        using (var http = new HttpClient { BaseAddress = new Uri($"http://{host127}/") })
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", info!.Token);
            using var response = await http.GetAsync("/v1/ping", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("Server"));
            Assert.Equal(new Version(1, 1), response.Version);
        }

        // Headers over 8 KB are refused (431).
        var bigHeader = new string('a', 9 * 1024);
        var oversize = await SendRawAsync(port, $"GET /v1/ping HTTP/1.1\r\nHost: {host127}\r\nX-Big: {bigHeader}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 431", oversize);

        // A request line over 2 KB is refused (414).
        var longLine = await SendRawAsync(port, $"GET /v1/ping?x={new string('a', 3 * 1024)} HTTP/1.1\r\nHost: {host127}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 414", longLine);

        // A request with a body is refused.
        var withBody = await SendRawAsync(port, $"POST /v1/meta HTTP/1.1\r\nHost: {host127}\r\nContent-Length: 5\r\n\r\nhello");
        Assert.NotNull(withBody);
        Assert.DoesNotContain(" 200 ", withBody, StringComparison.Ordinal);

        // HTTP/1.0 or HTTP/2 prior knowledge is not served as HTTP/2.
        var h2 = await SendRawAsync(port, "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
        Assert.True(h2 is null || !h2.StartsWith("HTTP/2", StringComparison.Ordinal));
    }
}
