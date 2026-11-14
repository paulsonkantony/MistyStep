using System.Net;
using MistyStep.Server.Tests.Support;
using static MistyStep.Server.Tests.Support.TestApp;

namespace MistyStep.Server.Tests;

/// <summary>/v1/meta and /v1/data responses and delivery tracking.</summary>
public sealed class ContentTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await StartAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task<System.Text.Json.JsonElement> MetaAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync("/v1/meta" + query, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    // --- /v1/meta ---

    [Fact]
    public async Task Meta_text_is_returned_inline()
    {
        var (_, token) = _app.Pair();
        var item = _app.SetText("hello 👋 world");
        _app.Clock.Advance(12_400);

        var json = await MetaAsync(_app.CreateClient(token));

        Assert.Equal(1, json.GetProperty("v").GetInt32());
        Assert.Equal("text", json.GetProperty("type").GetString());
        Assert.Equal(item.Id, json.GetProperty("id").GetString());
        Assert.Equal("hello 👋 world", json.GetProperty("text").GetString());
        Assert.False(json.GetProperty("isUrl").GetBoolean());
        Assert.Equal(12, json.GetProperty("ageSeconds").GetInt64());
        Assert.Equal(PcName, json.GetProperty("pc").GetString());
    }

    [Fact]
    public async Task Meta_text_url_sets_isUrl()
    {
        var (_, token) = _app.Pair();
        _app.SetText("https://example.com/page");
        Assert.True((await MetaAsync(_app.CreateClient(token))).GetProperty("isUrl").GetBoolean());
    }

    [Fact]
    public async Task Meta_image_describes_without_bytes()
    {
        var (_, token) = _app.Pair();
        var item = _app.SetImage(new byte[183_322], "image/png", 1920, 1080);
        _app.Clock.Advance(4_000);

        var json = await MetaAsync(_app.CreateClient(token));

        Assert.Equal("image", json.GetProperty("type").GetString());
        Assert.Equal(item.Id, json.GetProperty("id").GetString());
        Assert.Equal("image/png", json.GetProperty("mime").GetString());
        Assert.Equal(183_322, json.GetProperty("bytes").GetInt64());
        Assert.Equal(1920, json.GetProperty("width").GetInt32());
        Assert.Equal(1080, json.GetProperty("height").GetInt32());
        Assert.Equal(4, json.GetProperty("ageSeconds").GetInt64());
        Assert.Equal(PcName, json.GetProperty("pc").GetString());
        Assert.False(json.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task Meta_empty()
    {
        var (_, token) = _app.Pair();
        var json = await MetaAsync(_app.CreateClient(token));
        AssertNone(json, "empty");
    }

    [Fact]
    public async Task Meta_expired()
    {
        var (_, token) = _app.Pair();
        _app.SetText("old");
        _app.Clock.Advance(10 * 60_000);
        AssertNone(await MetaAsync(_app.CreateClient(token)), "expired");
    }

    [Fact]
    public async Task Meta_paused_hides_the_current_item()
    {
        var (_, token) = _app.Pair();
        _app.SetText("secret-ish");
        _app.State.Paused = true;
        AssertNone(await MetaAsync(_app.CreateClient(token)), "paused");

        _app.State.Paused = false;
        Assert.Equal("text", (await MetaAsync(_app.CreateClient(token))).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Meta_new_returns_unchanged_after_delivery_but_other_devices_still_receive()
    {
        var (_, ipadToken) = _app.Pair();
        var (_, phoneToken) = _app.Pair(OtherTailnetAddress);
        _app.SetText("once");
        var ipad = _app.CreateClient(ipadToken);

        Assert.Equal("text", (await MetaAsync(ipad, "?new=1")).GetProperty("type").GetString());
        AssertNone(await MetaAsync(ipad, "?new=1"), "unchanged");

        var phone = _app.CreateClient(phoneToken, OtherTailnetAddress);
        Assert.Equal("text", (await MetaAsync(phone, "?new=1")).GetProperty("type").GetString());

        // Without new=1 the item is always returned.
        Assert.Equal("text", (await MetaAsync(ipad)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Meta_new_returns_a_newer_item()
    {
        var (_, token) = _app.Pair();
        var client = _app.CreateClient(token);
        _app.SetText("first");
        await MetaAsync(client, "?new=1");
        _app.SetText("first"); // same content, new id: counts as new
        Assert.Equal("text", (await MetaAsync(client, "?new=1")).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Meta_image_is_not_delivered_until_data_completes()
    {
        var (_, token) = _app.Pair();
        var client = _app.CreateClient(token);
        _app.SetImage([9, 9, 9]);

        Assert.Equal("image", (await MetaAsync(client, "?new=1")).GetProperty("type").GetString());
        Assert.Equal("image", (await MetaAsync(client, "?new=1")).GetProperty("type").GetString());

        using (var data = await client.GetAsync("/v1/data", Ct)) Assert.Equal(HttpStatusCode.OK, data.StatusCode);

        AssertNone(await MetaAsync(client, "?new=1"), "unchanged");
        Assert.Equal(1, _app.Registry.Devices.Single().FetchCount);
    }

    [Theory]
    [InlineData("?new=0")]
    [InlineData("?new=true")]
    [InlineData("?new=")]
    public async Task Meta_new_other_values_are_ignored(string query)
    {
        var (_, token) = _app.Pair();
        var client = _app.CreateClient(token);
        _app.SetText("x");
        await MetaAsync(client, query);
        Assert.Equal("text", (await MetaAsync(client, query)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Text_meta_counts_as_a_fetch_and_updates_last_seen()
    {
        var (device, token) = _app.Pair();
        _app.SetText("x");
        await MetaAsync(_app.CreateClient(token));
        var updated = _app.Registry.Devices.Single(d => d.Id == device.Id);
        Assert.Equal(1, updated.FetchCount);
        Assert.Equal(_app.Clock.UtcNow.UtcDateTime, updated.LastSeenUtc);
    }

    // --- /v1/data ---

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public async Task Data_serves_image_bytes_with_type_and_length(string mime)
    {
        var (_, token) = _app.Pair();
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var item = _app.SetImage(bytes, mime);

        using var response = await _app.CreateClient(token).GetAsync($"/v1/data?id={item.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mime, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Data_with_an_old_id_still_serves_the_current_image()
    {
        var (_, token) = _app.Pair();
        _app.SetImage([1]);
        _app.SetImage([2, 2]);
        using var response = await _app.CreateClient(token).GetAsync("/v1/data?id=0000", Ct);
        Assert.Equal(new byte[] { 2, 2 }, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Data_is_204_for_text()
    {
        var (_, token) = _app.Pair();
        _app.SetText("not an image");
        using var response = await _app.CreateClient(token).GetAsync("/v1/data", Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(0, _app.Registry.Devices.Single().FetchCount);
    }

    [Fact]
    public async Task Data_is_204_when_empty_expired_or_paused()
    {
        var (_, token) = _app.Pair();
        var client = _app.CreateClient(token);

        using (var empty = await client.GetAsync("/v1/data", Ct)) Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);

        _app.SetImage([1]);
        _app.State.Paused = true;
        using (var paused = await client.GetAsync("/v1/data", Ct)) Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);

        _app.State.Paused = false;
        _app.Clock.Advance(10 * 60_000);
        using (var expired = await client.GetAsync("/v1/data", Ct)) Assert.Equal(HttpStatusCode.NoContent, expired.StatusCode);
    }

    private static void AssertNone(System.Text.Json.JsonElement json, string reason)
    {
        Assert.Equal(1, json.GetProperty("v").GetInt32());
        Assert.Equal("none", json.GetProperty("type").GetString());
        Assert.Equal(reason, json.GetProperty("reason").GetString());
        Assert.Equal(PcName, json.GetProperty("pc").GetString());
    }
}
