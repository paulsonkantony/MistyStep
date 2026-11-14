using System.Text;
using MistyStep.Core.Pairing;
using MistyStep.Core.Security;

namespace MistyStep.Core.Tests.Pairing;

public class SetupCodeTests
{
    [Fact]
    public void Round_trip_with_non_ascii_pc_name()
    {
        var info = new SetupInfo("http://pc.tailnet.ts.net:45654", Secrets.NewToken(), "Zoë's Büro-PC ✓ 東京");
        var code = SetupCode.Encode(info);

        Assert.StartsWith("MS1.", code);
        Assert.True(SetupCode.TryDecode(code, out var decoded));
        Assert.Equal(info, decoded);
    }

    [Fact]
    public void Payload_is_standard_base64_of_compact_json_with_short_keys()
    {
        var code = SetupCode.Encode(new SetupInfo("http://100.101.102.103:45654", "tok", "MY-LAPTOP"));
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(code["MS1.".Length..]));
        Assert.Equal("""{"u":"http://100.101.102.103:45654","t":"tok","n":"MY-LAPTOP"}""", json);
    }

    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        var code = SetupCode.Encode(new SetupInfo("http://a:1", "t", "n"));
        Assert.True(SetupCode.TryDecode($"  {code}\n", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MS1.")]
    [InlineData("MS1.not base64!")]
    public void Malformed_codes_do_not_decode(string? code)
    {
        Assert.False(SetupCode.TryDecode(code, out var info));
        Assert.Null(info);
    }

    [Theory]
    [InlineData("MS2.", """{"u":"http://a:1","t":"t","n":"n"}""")] // wrong prefix
    [InlineData("MS1.", "not json")]
    [InlineData("MS1.", "null")]
    [InlineData("MS1.", """{"u":"","t":"t","n":"n"}""")]            // empty url
    [InlineData("MS1.", """{"u":"http://a:1","t":"","n":"n"}""")]   // empty token
    [InlineData("MS1.", """{"u":"not a url","t":"t","n":"n"}""")]   // relative url
    [InlineData("MS1.", """{"u":"http://a:1","t":"t"}""")]          // missing name
    public void Invalid_payloads_do_not_decode(string prefix, string json)
    {
        var code = prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Assert.False(SetupCode.TryDecode(code, out var info));
        Assert.Null(info);
    }

    [Fact]
    public void A_valid_hand_built_payload_decodes()
    {
        var code = "MS1." + Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"u":"http://a:1","t":"t","n":"n"}"""));
        Assert.True(SetupCode.TryDecode(code, out var info));
        Assert.Equal(new SetupInfo("http://a:1", "t", "n"), info);
    }

    [Fact]
    public void Token_is_43_base64url_chars()
    {
        var token = Secrets.NewToken();
        Assert.Equal(43, token.Length);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }
}
