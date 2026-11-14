using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Auth;
using MistyStep.Core.Models;

namespace MistyStep.Server;

/// <summary>Handlers for the four routes. They run only after the pipeline has accepted the request.</summary>
internal sealed class Endpoints(ServerDependencies deps, ILogger<Endpoints> logger)
{
    /// <summary>GET /v1/meta[?new=1]: describe the current item; text is returned inline.</summary>
    public async Task MetaAsync(HttpContext context)
    {
        var device = RequestPipeline.GetDevice(context);
        var state = deps.State;
        var pc = state.PcName;

        if (state.Paused)
        {
            await WriteNoneAsync(context, NoneReason.Paused, pc, device);
            return;
        }

        var snapshot = deps.Store.GetCurrent();
        if (snapshot.Item is not { } item)
        {
            await WriteNoneAsync(context, snapshot.Reason, pc, device);
            return;
        }

        var onlyNew = context.Request.Query["new"] == "1";
        if (onlyNew && deps.Registry.HasReceived(device.Id, item.Id))
        {
            await WriteNoneAsync(context, NoneReason.Unchanged, pc, device);
            return;
        }

        var age = Math.Max(0, deps.Clock.MonotonicMs - item.CapturedAt) / 1000;
        if (item.Type == ClipType.Text)
        {
            var body = new MetaTextResponse(Responses.ApiVersion, "text", item.Id, item.Text ?? "", item.IsUrl, age, pc);
            await Responses.WriteJsonAsync(context, StatusCodes.Status200OK, body, ServerJsonContext.Default.MetaTextResponse);
            // Text is delivered once the response is written.
            if (!context.RequestAborted.IsCancellationRequested) deps.Registry.RecordDelivery(device.Id, item.Id);
            logger.LogInformation("Served text ({Chars} chars) to {Device}", item.Text?.Length ?? 0, device.Name);
        }
        else
        {
            var body = new MetaImageResponse(Responses.ApiVersion, "image", item.Id, item.Mime, item.Bytes.Length,
                item.Width, item.Height, age, pc);
            await Responses.WriteJsonAsync(context, StatusCodes.Status200OK, body, ServerJsonContext.Default.MetaImageResponse);
            logger.LogInformation("Described image ({Mime}, {Bytes} bytes) to {Device}", item.Mime, item.Bytes.Length, device.Name);
        }
    }

    /// <summary>GET /v1/data: bytes of the current image, or 204 if the current item is not an image.</summary>
    public async Task DataAsync(HttpContext context)
    {
        var device = RequestPipeline.GetDevice(context);
        var snapshot = deps.State.Paused ? default : deps.Store.GetCurrent();
        if (snapshot.Item is not { Type: ClipType.Image } item)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            logger.LogInformation("No image for {Device}", device.Name);
            return;
        }

        // ?id= is for diagnostics only: the current image is always served.
        var requestedId = context.Request.Query["id"].ToString();
        if (requestedId.Length > 0 && requestedId != item.Id)
            logger.LogDebug("{Device} asked for an older item; serving the current one", device.Name);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = item.Mime;
        context.Response.ContentLength = item.Bytes.Length;
        try
        {
            await context.Response.Body.WriteAsync(item.Bytes, context.RequestAborted);
            await context.Response.CompleteAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            logger.LogDebug("{Device} disconnected during image download", device.Name);
            return;
        }

        if (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("{Device} disconnected during image download", device.Name);
            return;
        }

        deps.Registry.RecordDelivery(device.Id, item.Id);
        logger.LogInformation("Served image ({Mime}, {Bytes} bytes) to {Device}", item.Mime, item.Bytes.Length, device.Name);
    }

    /// <summary>GET /v1/ping: connectivity check.</summary>
    public Task PingAsync(HttpContext context)
    {
        var body = new PingResponse(Responses.ApiVersion, true, deps.State.PcName, deps.State.AppVersion);
        return Responses.WriteJsonAsync(context, StatusCodes.Status200OK, body, ServerJsonContext.Default.PingResponse);
    }

    /// <summary>GET /pair?c=&lt;code&gt;: redeem a pairing code and show the setup steps.</summary>
    public async Task PairAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress!;
        var code = context.Request.Query["c"].ToString();
        var userAgent = context.Request.Headers.UserAgent.ToString();

        var result = deps.Registry.Redeem(code.Length == 0 ? null : code, remote, userAgent.Length == 0 ? null : userAgent);
        switch (result)
        {
            case RedeemResult.Redeemed redeemed:
                logger.LogInformation("Pairing page shown to {Device}", redeemed.Device.Name);
                await PairingPage.WriteSetupAsync(context, redeemed.SetupCode, redeemed.Device.Name, deps.State.PcName, deps.State.ShortcutUrl);
                break;
            case RedeemResult.Gone gone:
                logger.LogInformation("Pairing page refused from {Remote}: {Failure}", remote, gone.Failure);
                await PairingPage.WriteGoneAsync(context);
                break;
        }
    }

    private Task WriteNoneAsync(HttpContext context, NoneReason reason, string pc, Device device)
    {
        var name = reason switch
        {
            NoneReason.Empty => "empty",
            NoneReason.Expired => "expired",
            NoneReason.Paused => "paused",
            NoneReason.Unchanged => "unchanged",
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
        logger.LogInformation("Nothing to serve to {Device}: {Reason}", device.Name, name);
        var body = new MetaNoneResponse(Responses.ApiVersion, "none", name, pc);
        return Responses.WriteJsonAsync(context, StatusCodes.Status200OK, body, ServerJsonContext.Default.MetaNoneResponse);
    }
}
