using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Auth;
using MistyStep.Core.Network;

namespace MistyStep.Server;

/// <summary>
/// The request pipeline, in the spec's order: connection filter, method/path/Host gate, lockout,
/// authentication, handler. Security headers are set first so every response carries them.
/// </summary>
internal sealed class RequestPipeline(ServerDependencies deps, ILogger<RequestPipeline> logger)
{
    public const string MetaPath = "/v1/meta";
    public const string DataPath = "/v1/data";
    public const string PingPath = "/v1/ping";
    public const string PairPath = "/pair";

    private const string DeviceItemKey = "MistyStep.Device";

    private static readonly HashSet<string> KnownPaths = new(StringComparer.Ordinal) { MetaPath, DataPath, PingPath, PairPath };

    /// <summary>The authenticated device for a /v1 request.</summary>
    public static Device GetDevice(HttpContext context) => (Device)context.Items[DeviceItemKey]!;

    /// <summary>Kestrel connection middleware: drop disallowed callers before any HTTP parsing.</summary>
    public Func<ConnectionDelegate, ConnectionDelegate> ConnectionFilter => next => async connection =>
    {
        var remote = (connection.RemoteEndPoint as IPEndPoint)?.Address;
        if (!IsAllowed(remote))
        {
            logger.LogDebug("Refused connection from {Remote}", remote);
            connection.Abort();
            return;
        }

        await next(connection);
    };

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // 6 (set up front so every response has them). Server header is disabled in Kestrel options.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";

        // 1. Connection filter, repeated per request in case the network changed on a kept-alive connection.
        var remote = Normalize(context.Connection.RemoteIpAddress);
        if (!IsAllowed(remote))
        {
            logger.LogDebug("Refused request from {Remote}", remote);
            context.Abort();
            return;
        }

        // 2. Only GET without a body, on a known path, addressed to a known host.
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method)
            || !KnownPaths.Contains(request.Path.Value ?? "")
            || request.ContentLength is > 0
            || request.Headers.TransferEncoding.Count > 0
            || !HostValidator.IsAllowed(request.Headers.Host.ToString(), deps.State.Network, deps.State.Port))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var isApi = request.Path.StartsWithSegments("/v1", StringComparison.Ordinal);

        // 3. Lockout for this address.
        if (deps.Lockout.IsLockedOut(remote!))
        {
            logger.LogInformation("Refused request from {Remote}: locked out", remote);
            if (isApi) await Responses.WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, Responses.RateLimited);
            else await PairingPage.WriteLockedOutAsync(context);
            return;
        }

        // 4. Authentication on /v1.
        if (isApi)
        {
            var device = deps.Registry.Authenticate(ReadBearerToken(request));
            if (device is null)
            {
                deps.Lockout.RecordFailure(remote!);
                logger.LogInformation("Refused {Path} from {Remote}: unauthorized", request.Path.Value, remote);
                await Responses.WriteErrorAsync(context, StatusCodes.Status401Unauthorized, Responses.Unauthorized);
                return;
            }

            if (!deps.RateLimiter.TryAcquire(device.Id))
            {
                logger.LogInformation("Refused {Path} for {Device}: rate limited", request.Path.Value, device.Name);
                await Responses.WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, Responses.RateLimited);
                return;
            }

            deps.Registry.RecordSeen(device.Id);
            context.Items[DeviceItemKey] = device;
            LogShortcutVersion(request, device);
        }

        // 5. Handler.
        await next(context);
    }

    private bool IsAllowed(IPAddress? remote) =>
        remote is not null
        && AddressPolicy.IsAllowed(remote, deps.State.Network, deps.State.TailscaleEnabled, deps.State.LanEnabled);

    private static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>Reads <c>Authorization: Bearer &lt;token&gt;</c>. The scheme is case-insensitive.</summary>
    internal static string? ReadBearerToken(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1) return null;
        var header = values[0];
        const string scheme = "Bearer ";
        if (header is null || header.Length <= scheme.Length || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        var token = header[scheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private void LogShortcutVersion(HttpRequest request, Device device)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        var raw = request.Headers["X-MistyStep-Shortcut"].ToString();
        // Log only a short numeric version; the header is caller-controlled.
        var version = raw.Length is > 0 and <= 8 && raw.All(char.IsAsciiDigit) ? raw : "unknown";
        logger.LogDebug("{Device} uses Shortcut version {Version}", device.Name, version);
    }
}
