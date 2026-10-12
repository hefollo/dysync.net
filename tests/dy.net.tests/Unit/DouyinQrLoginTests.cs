using dy.net.utils;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dy.net.tests.Unit;

public class DouyinQrLoginTests
{
    private static readonly string Base64Png = new string('A', 80) + "==";

    [Theory]
    [InlineData("https://sso.douyin.com/get_qrcode/?service=https%3A%2F%2Fwww.douyin.com&a_bogus=x", "qr")]
    [InlineData("https://www.douyin.com/passport/web/get_qrcode/?next=x", "qr")]
    [InlineData("https://sso.douyin.com/check_qrconnect/?token=abc", "check")]
    [InlineData("https://www.douyin.com/aweme/v1/web/user/profile/self/?aid=6383", "profile")]
    [InlineData("https://sso.douyin.com/wechat/get_qrcode/", null)]
    [InlineData("https://www.douyin.com/aweme/v1/web/aweme/post/", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void ClassifyUrl_RecognizesLoginEndpoints(string url, string expected)
    {
        Assert.Equal(expected, DouyinQrLoginSession.ClassifyUrl(url));
    }

    [Fact]
    public void ParseQrResponse_WrapsBase64AsDataUrl()
    {
        var body = new JObject
        {
            ["data"] = new JObject { ["qrcode"] = Base64Png, ["token"] = "t" },
            ["message"] = "success"
        }.ToString();

        var (image, error) = DouyinQrLoginSession.ParseQrResponse(body);

        Assert.Equal("data:image/png;base64," + Base64Png, image);
        Assert.Null(error);
    }

    [Fact]
    public void ParseQrResponse_KeepsExistingDataUrl()
    {
        var body = "{\"data\":{\"qrcode\":\"data:image/jpeg;base64,xyz\"}}";

        Assert.Equal("data:image/jpeg;base64,xyz", DouyinQrLoginSession.ParseQrResponse(body).Image);
    }

    [Fact]
    public void ParseQrResponse_ReturnsDescriptionOnRiskControl()
    {
        var body = "{\"data\":{\"captcha\":\"\",\"description\":\"已阻止此次访问\",\"error_code\":4031},\"message\":\"error\"}";

        var (image, error) = DouyinQrLoginSession.ParseQrResponse(body);

        Assert.Null(image);
        Assert.Equal("已阻止此次访问", error);
    }

    [Theory]
    [InlineData("{\"data\":{\"qrcode\":\"https://example.com/qr.png\"}}")]
    [InlineData("<!doctype html><html></html>")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseQrResponse_ReturnsNoImageForUnusableBody(string body)
    {
        Assert.Null(DouyinQrLoginSession.ParseQrResponse(body).Image);
    }

    [Theory]
    [InlineData("{\"data\":{\"status\":\"1\"}}", "1")]
    [InlineData("{\"data\":{\"status\":2}}", "2")]
    [InlineData("{\"data\":{\"status\":\"Scanned\"}}", "scanned")]
    [InlineData("{\"data\":{\"status\":\"expired\"}}", "expired")]
    public void ParseCheckResponse_NormalizesStatus(string body, string expected)
    {
        var (status, error) = DouyinQrLoginSession.ParseCheckResponse(body);

        Assert.Equal(expected, status);
        Assert.Null(error);
    }

    [Fact]
    public void ParseCheckResponse_ReturnsDescriptionWhenStatusMissing()
    {
        var body = "{\"data\":{\"description\":\"请完成身份验证\",\"error_code\":2046},\"message\":\"error\"}";

        var (status, error) = DouyinQrLoginSession.ParseCheckResponse(body);

        Assert.Null(status);
        Assert.Equal("请完成身份验证", error);
    }

    [Fact]
    public void ParseSelfProfile_ReadsSecUidAndNickname()
    {
        var body = "{\"status_code\":0,\"user\":{\"sec_uid\":\"MS4wLjABAAAA\",\"nickname\":\"测试\",\"uid\":\"1\"}}";

        var (secUserId, nickname) = DouyinQrLoginSession.ParseSelfProfile(body);

        Assert.Equal("MS4wLjABAAAA", secUserId);
        Assert.Equal("测试", nickname);
    }

    [Theory]
    [InlineData("{\"status_code\":8,\"status_msg\":\"用户未登录\"}")]
    [InlineData("{\"user\":{\"sec_uid\":\"\"}}")]
    [InlineData("not json")]
    [InlineData(null)]
    public void ParseSelfProfile_ReturnsNullSecUidWhenMissing(string body)
    {
        Assert.Null(DouyinQrLoginSession.ParseSelfProfile(body).SecUserId);
    }

    [Fact]
    public void ContainsLoginCookie_RequiresNonEmptySessionCookieOnDouyinDomain()
    {
        var anonymous = JArray.Parse("[{\"name\":\"ttwid\",\"value\":\"a\",\"domain\":\".douyin.com\"},{\"name\":\"sessionid\",\"value\":\"\",\"domain\":\".douyin.com\"}]");
        var otherSite = JArray.Parse("[{\"name\":\"sessionid\",\"value\":\"abc\",\"domain\":\".example.com\"}]");
        var loggedIn = JArray.Parse("[{\"name\":\"ttwid\",\"value\":\"a\",\"domain\":\".douyin.com\"},{\"name\":\"sessionid_ss\",\"value\":\"abc\",\"domain\":\".douyin.com\"}]");

        Assert.False(DouyinQrLoginSession.ContainsLoginCookie(anonymous));
        Assert.False(DouyinQrLoginSession.ContainsLoginCookie(otherSite));
        Assert.False(DouyinQrLoginSession.ContainsLoginCookie(null));
        Assert.True(DouyinQrLoginSession.ContainsLoginCookie(loggedIn));
    }

    [Fact]
    public void BuildCookieHeader_JoinsCookiesAndSkipsDuplicates()
    {
        var cookies = JArray.Parse(
            "[{\"name\":\"ttwid\",\"value\":\"1%7Cabc\"},{\"name\":\"sessionid\",\"value\":\"s1\"},{\"name\":\"ttwid\",\"value\":\"other\"},{\"name\":\"\",\"value\":\"x\"}]");

        Assert.Equal("ttwid=1%7Cabc; sessionid=s1", DouyinQrLoginSession.BuildCookieHeader(cookies));
        Assert.Equal(string.Empty, DouyinQrLoginSession.BuildCookieHeader(null));
    }

    [Theory]
    [InlineData("ttwid=a; sessionid=abc; odin_tt=x", true)]
    [InlineData("sessionid_ss=abc", true)]
    [InlineData("ttwid=a; sessionid=", false)]
    [InlineData("ttwid=a; my_sessionid=abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasLoginCookieName_DetectsSessionCookie(string header, bool expected)
    {
        Assert.Equal(expected, DouyinQrLoginSession.HasLoginCookieName(header));
    }

    [Fact]
    public void NormalizeUserAgent_RemovesHeadlessMarker()
    {
        var headless = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/130.0.0.0 Safari/537.36";

        var normalized = DouyinQrLoginSession.NormalizeUserAgent(headless);

        Assert.DoesNotContain("Headless", normalized);
        Assert.Contains("Chrome/130.0.0.0", normalized);
        Assert.Equal(DouyinRequestParamManager.DY_USER_AGENT, DouyinQrLoginSession.NormalizeUserAgent(null));
    }

    [Theory]
    [InlineData("9222\n/devtools/browser/abc-123\n", "ws://127.0.0.1:9222/devtools/browser/abc-123")]
    [InlineData("51234\r\n/devtools/browser/x", "ws://127.0.0.1:51234/devtools/browser/x")]
    [InlineData("9222\n", null)]
    [InlineData("abc\n/devtools/browser/x", null)]
    [InlineData("", null)]
    public void ParseDevToolsActivePort_BuildsWebSocketUrl(string content, string expected)
    {
        Assert.Equal(expected, QrBrowserProvider.ParseDevToolsActivePort(content));
    }

    [Theory]
    [InlineData("ws://127.0.0.1:9222/devtools/browser/abc", "http", "172.18.0.3:9222", "ws://172.18.0.3:9222/devtools/browser/abc")]
    [InlineData("ws://localhost/devtools/browser/abc?x=1", "https", "chrome.example.com", "wss://chrome.example.com/devtools/browser/abc?x=1")]
    public void RewriteWebSocketAuthority_UsesReachableAddress(string reported, string scheme, string authority, string expected)
    {
        Assert.Equal(expected, QrBrowserProvider.RewriteWebSocketAuthority(reported, scheme, authority));
    }
}
