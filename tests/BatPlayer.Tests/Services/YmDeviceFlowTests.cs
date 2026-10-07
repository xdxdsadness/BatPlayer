using BatPlayer.Services.YandexMusic;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Tests for the Yandex ID OAuth Device Flow (Yandex Music sign-in): device-code/token
/// response parsing (pure YmJsonParser functions), poll error classification (YmTokenResult)
/// and the device_id format. No network or live login.
/// </summary>
public class YmDeviceFlowTests
{
    // ===================== ParseDeviceCode =====================

    [Fact]
    public void ParseDeviceCode_FullResponse_ParsesAllFields()
    {
        var json = """
            {"device_code":"9bc1e7b0e2f34a1c","user_code":"405-151-857",
             "verification_url":"https://oauth.yandex.ru/device",
             "expires_in":300,"interval":5}
            """;

        var code = YmJsonParser.ParseDeviceCode(json);

        Assert.NotNull(code);
        Assert.Equal("9bc1e7b0e2f34a1c", code!.DeviceCode);
        Assert.Equal("405-151-857", code.UserCode);
        Assert.Equal("https://oauth.yandex.ru/device", code.VerificationUrl);
        Assert.Equal(300, code.ExpiresInSeconds);
        Assert.Equal(5, code.IntervalSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>gateway error</html>")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"device_code\":\"abc\"}")] // no user_code
    [InlineData("{\"user_code\":\"123\"}")]   // no device_code
    public void ParseDeviceCode_GarbageOrIncomplete_ReturnsNull(string? json)
    {
        Assert.Null(YmJsonParser.ParseDeviceCode(json));
    }

    // ===================== ParseTokenResponse =====================

    [Fact]
    public void ParseTokenResponse_Success_ParsesTokenAndExpiry()
    {
        var json = """{"access_token":"y0_AgAAAABCdef123","token_type":"bearer","expires_in":31535645}""";

        var result = YmJsonParser.ParseTokenResponse(json);

        Assert.Equal("y0_AgAAAABCdef123", result.AccessToken);
        Assert.Equal(31535645, result.ExpiresInSeconds);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void ParseTokenResponse_SuccessWithoutExpiry_ExpiryIsNull()
    {
        var result = YmJsonParser.ParseTokenResponse("""{"access_token":"tok"}""");

        Assert.Equal("tok", result.AccessToken);
        Assert.Null(result.ExpiresInSeconds);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData("""{"error":"authorization_pending"}""", "authorization_pending")]
    [InlineData("""{"error":"slow_down"}""", "slow_down")]
    [InlineData("""{"error":"expired_token"}""", "expired_token")]
    [InlineData("""{"error":"access_denied"}""", "access_denied")]
    [InlineData("""{}""", "invalid_response")]          // neither token nor error
    [InlineData("""<html>502</html>""", "invalid_response")] // not JSON
    [InlineData(null, "invalid_response")]
    [InlineData("", "invalid_response")]
    public void ParseTokenResponse_ErrorBody_ReturnsErrorCode(string? json, string expected)
    {
        var result = YmJsonParser.ParseTokenResponse(json);

        Assert.Equal(string.Empty, result.AccessToken);
        Assert.Equal(expected, result.ErrorCode);
    }

    // ===================== YmTokenResult classification =====================

    [Fact]
    public void YmTokenResult_PendingAndSlowDown_Classified()
    {
        Assert.True(new YmTokenResult { ErrorCode = "authorization_pending" }.IsPending);
        Assert.True(new YmTokenResult { ErrorCode = "slow_down" }.IsSlowDown);
        Assert.True(new YmTokenResult { ErrorCode = "expired_token" }.IsExpired);
        Assert.False(new YmTokenResult { ErrorCode = "authorization_pending" }.IsSlowDown);
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("invalid_client")]
    [InlineData("unauthorized_client")]
    [InlineData("invalid_grant")]
    [InlineData("bad_verification_code")]
    public void YmTokenResult_FinalErrors_AreDenied(string errorCode)
    {
        Assert.True(new YmTokenResult { ErrorCode = errorCode }.IsDenied);
    }

    [Fact]
    public void YmTokenResult_Success_IsNotPendingOrDenied()
    {
        var result = new YmTokenResult { AccessToken = "tok" };

        Assert.False(result.IsPending);
        Assert.False(result.IsSlowDown);
        Assert.False(result.IsExpired);
        Assert.False(result.IsDenied);
    }

    // ===================== Constants and utilities =====================

    [Fact]
    public void GenerateDeviceId_ReturnsTenAlphanumericChars()
    {
        var id = YmService.GenerateDeviceId();

        Assert.Equal(10, id.Length);
        Assert.Matches("^[a-zA-Z0-9]{10}$", id);
    }

    [Fact]
    public void OAuthClientCredentials_AreOfficialYandexMusicClient()
    {
        // Yandex Music Android client pair (as in the unofficial yandex-music-api);
        // registered on oauth.yandex.ru, works with Device Flow.
        Assert.Equal("23cabbbdc6cd418abb4b39c32c41195d", YmService.OAuthClientId);
        Assert.Equal("53bc75238f0c4d08a118e51fe9203300", YmService.OAuthClientSecret);
    }
}
