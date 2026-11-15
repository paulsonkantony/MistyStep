using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace MistyStep.Server;

// JSON bodies for /v1. The Shortcut branches only on these bodies, never on status codes or headers.
// Fields may be added in later versions but never renamed or removed.

internal sealed record MetaTextResponse(int V, string Type, string Id, string Text, bool IsUrl, long AgeSeconds, string Pc);

internal sealed record MetaImageResponse(int V, string Type, string Id, string Mime, long Bytes, int Width, int Height, long AgeSeconds, string Pc);

internal sealed record MetaNoneResponse(int V, string Type, string Reason, string Pc);

internal sealed record ErrorResponse(int V, string Error, string Message);

internal sealed record PingResponse(int V, bool Ok, string Pc, string Version);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetaTextResponse))]
[JsonSerializable(typeof(MetaImageResponse))]
[JsonSerializable(typeof(MetaNoneResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(PingResponse))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;

internal static class Responses
{
    public const int ApiVersion = 1;

    public static readonly ErrorResponse Unauthorized =
        new(ApiVersion, "unauthorized", "This device is not paired. Pair again from Misty Step on your PC.");

    public static readonly ErrorResponse RateLimited =
        new(ApiVersion, "rate_limited", "Too many requests. Wait a few minutes and try again.");

    public static async Task WriteJsonAsync<T>(HttpContext context, int status, T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    /// <summary>
    /// Writes an error body with status 200, the plan's fallback for Shortcuts not receiving a 401 body.
    /// A 200 always reaches the Shortcut, which branches on the "error" key and never on the status.
    /// The alert is confirmed on iPadOS 18 with 200; a 401 body was never shown to reach it.
    /// </summary>
    public static Task WriteErrorAsync(HttpContext context, ErrorResponse error) =>
        WriteJsonAsync(context, StatusCodes.Status200OK, error, ServerJsonContext.Default.ErrorResponse);
}
