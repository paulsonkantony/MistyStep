using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Settings;

namespace MistyStep.App;

/// <summary>
/// The opt-in weekly update check, and the app's only outbound network call (spec, Security model).
/// It runs only while the <c>updateCheck</c> setting is on: two minutes after start, then every 7 days.
/// The request carries a generic User-Agent and nothing else about the user, the PC or the devices.
/// It never downloads or installs anything; a newer release only adds a tray menu link.
/// </summary>
internal sealed class UpdateChecker : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);

    private readonly AppHost _host;
    private readonly HttpClient _http;
    private readonly System.Threading.Timer _timer;

    public UpdateChecker(AppHost host)
    {
        _host = host;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MistyStep-UpdateCheck", "1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(), null, FirstCheckDelay, UpdateCheck.Interval);
    }

    /// <summary>Raised on a pool thread when a newer release exists.</summary>
    public event Action<ReleaseInfo>? UpdateAvailable;

    private async Task CheckAsync()
    {
        if (!_host.Persistence.Settings.UpdateCheck) return;
        try
        {
            var json = await _http.GetStringAsync(new Uri(ProjectInfo.LatestReleaseApi)).ConfigureAwait(false);
            var release = UpdateCheck.ParseLatest(json);
            if (release is null)
            {
                _host.Log.LogInformation("Update check: no usable release found");
                return;
            }

            var newer = UpdateCheck.IsNewer(release, _host.Version);
            _host.Log.LogInformation("Update check: latest {Latest}, running {Running}", release.Version, _host.Version);
            if (newer) UpdateAvailable?.Invoke(release);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _host.Log.LogInformation("Update check failed: {ErrorType}", ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
