using dy.net.model.dto;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Collections.Concurrent;
using System.Text;

namespace dy.net.utils
{
    /// <summary>
    /// 一次扫码登录会话：驱动浏览器打开抖音登录页，把二维码转给前端，
    /// 用户扫码确认后从浏览器里取出 www.douyin.com 的 Cookie 和 sec_user_id。
    /// 二维码接口需要浏览器端安全 SDK 签名，所以必须由真实浏览器发起请求。
    /// </summary>
    public sealed class DouyinQrLoginSession
    {
        public const string LoginUrl = "https://sso.douyin.com/?service=https%3A%2F%2Fwww.douyin.com";
        private const string DouyinHome = "https://www.douyin.com/";
        private const string DouyinHomeHost = "www.douyin.com";
        private const string QrImageMissingMessage = "未能直接取到二维码图片，请扫描浏览器画面中的二维码";

        private const int ViewportWidth = 1280;
        private const int ViewportHeight = 900;

        // 从最后一次操作算起的等待时间；用户在浏览器画面里做身份验证时会不断顺延
        private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaxSessionTime = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan QrAppearTimeout = TimeSpan.FromSeconds(30);
        // 前端每隔一两秒轮询一次，长时间没人轮询说明页面已经关了
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan ResultRetention = TimeSpan.FromMinutes(2);

        private readonly object _sync = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, string> _trackedRequests = new();

        private CdpClient _cdp;
        private string _pageSessionId;
        private string _browserContextId;
        private string _targetId;

        private string _state = DouyinQrLoginStates.Starting;
        private string _message = "正在启动浏览器...";
        private string _qrImage;
        private int _qrVersion;
        private string _cookie;
        private string _secUserId;
        private string _nickname;
        private string _profileBody;
        private bool _showBrowser;
        private bool _mouseDown;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private DateTime _lastPolledUtc = DateTime.UtcNow;
        private DateTime _finishedUtc = DateTime.MaxValue;

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Start()
        {
            Completion = Task.Run(RunAsync);
        }

        public void Cancel()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public DouyinQrLoginStatusDto Snapshot(int knownQrVersion)
        {
            lock (_sync)
            {
                _lastPolledUtc = DateTime.UtcNow;
                var dto = new DouyinQrLoginStatusDto
                {
                    Id = Id,
                    State = _state,
                    Message = _message,
                    QrVersion = _qrVersion,
                    ShowBrowser = _showBrowser
                };

                if (_qrVersion != knownQrVersion)
                    dto.QrImage = _qrImage;

                if (_state == DouyinQrLoginStates.Success)
                {
                    if (DateTime.UtcNow - _finishedUtc > ResultRetention)
                    {
                        dto.State = DouyinQrLoginStates.Expired;
                        dto.Message = "扫码结果已过期，请重新扫码";
                    }
                    else
                    {
                        dto.Cookie = _cookie;
                        dto.SecUserId = _secUserId;
                        dto.Nickname = _nickname;
                    }
                }

                return dto;
            }
        }

        /// <summary>
        /// 当前浏览器画面（data URL）。二维码图片没取到，或需要看清页面上的提示时使用。
        /// </summary>
        public async Task<string> CaptureScreenshotAsync()
        {
            var cdp = _cdp;
            var pageSessionId = _pageSessionId;
            if (cdp == null || pageSessionId == null || IsFinal())
                return null;

            try
            {
                var result = await cdp.SendAsync(
                    "Page.captureScreenshot",
                    new { format = "jpeg", quality = 80 },
                    pageSessionId,
                    15000,
                    _cts.Token);
                var data = result.Value<string>("data");
                return string.IsNullOrEmpty(data) ? null : "data:image/jpeg;base64," + data;
            }
            catch (Exception ex) when (ex is CdpException || ex is TimeoutException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                return null;
            }
        }

        /// <summary>
        /// 把前端在浏览器画面上的操作转发给页面，用于完成扫码后的身份验证。
        /// 坐标是页面坐标（与截图像素一致）。
        /// </summary>
        public async Task<bool> PerformActionAsync(DouyinQrLoginActionDto action)
        {
            var cdp = _cdp;
            var pageSessionId = _pageSessionId;
            if (action == null || cdp == null || pageSessionId == null || IsFinal())
                return false;

            lock (_sync) { _lastActivityUtc = DateTime.UtcNow; }

            var x = Math.Clamp(action.X, 0, ViewportWidth - 1);
            var y = Math.Clamp(action.Y, 0, ViewportHeight - 1);
            Task<JObject> Send(string method, object parameters)
            {
                return cdp.SendAsync(method, parameters, pageSessionId, 10000, _cts.Token);
            }

            try
            {
                switch ((action.Type ?? string.Empty).ToLowerInvariant())
                {
                    case "down":
                        await Send("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y, button = "none", buttons = 0 });
                        await Send("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 });
                        _mouseDown = true;
                        return true;

                    case "move":
                        await Send("Input.dispatchMouseEvent", new
                        {
                            type = "mouseMoved",
                            x,
                            y,
                            button = _mouseDown ? "left" : "none",
                            buttons = _mouseDown ? 1 : 0
                        });
                        return true;

                    case "up":
                        await Send("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 });
                        _mouseDown = false;
                        return true;

                    case "scroll":
                        var deltaY = Math.Clamp(action.DeltaY, -2000, 2000);
                        await Send("Input.dispatchMouseEvent", new { type = "mouseWheel", x, y, deltaX = 0, deltaY });
                        return true;

                    case "text":
                        if (string.IsNullOrEmpty(action.Text) || action.Text.Length > 200)
                            return false;
                        await Send("Input.insertText", new { text = action.Text });
                        return true;

                    case "key":
                        if (action.Key == null || !SupportedKeys.TryGetValue(action.Key, out var virtualKeyCode))
                            return false;
                        // 回车需要带上字符才会触发表单提交，其余按键没有对应字符
                        var keyDown = new JObject
                        {
                            ["type"] = action.Key == "Enter" ? "keyDown" : "rawKeyDown",
                            ["key"] = action.Key,
                            ["code"] = action.Key,
                            ["windowsVirtualKeyCode"] = virtualKeyCode,
                            ["nativeVirtualKeyCode"] = virtualKeyCode
                        };
                        if (action.Key == "Enter")
                            keyDown["text"] = "\r";
                        await Send("Input.dispatchKeyEvent", keyDown);
                        await Send("Input.dispatchKeyEvent", new
                        {
                            type = "keyUp",
                            key = action.Key,
                            code = action.Key,
                            windowsVirtualKeyCode = virtualKeyCode,
                            nativeVirtualKeyCode = virtualKeyCode
                        });
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception ex) when (ex is CdpException || ex is TimeoutException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                return false;
            }
        }

        private static readonly Dictionary<string, int> SupportedKeys = new()
        {
            ["Enter"] = 13,
            ["Backspace"] = 8,
            ["Tab"] = 9,
            ["Escape"] = 27
        };

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            QrBrowserHandle browser = null;
            try
            {
                browser = await QrBrowserProvider.AcquireAsync(ct);
                _cdp = await CdpClient.ConnectAsync(browser.WebSocketUrl, ct);
                _cdp.EventReceived += OnCdpEvent;

                SetState(DouyinQrLoginStates.Starting, "正在加载抖音登录页...");
                await OpenLoginPageAsync(ct);

                if (!await WaitForLoginAsync(ct))
                    return;

                SetState(DouyinQrLoginStates.Confirming, "已确认登录，正在获取 Cookie...");
                await SettleOnDouyinHomeAsync(ct);

                var cookie = await ReadCookieHeaderAsync(ct);
                if (!HasLoginCookieName(cookie))
                {
                    SetState(DouyinQrLoginStates.Failed, "登录成功但没有取到 www.douyin.com 的会话 Cookie，请改用手动粘贴");
                    return;
                }

                var (secUserId, nickname) = await ReadSelfProfileAsync(ct);
                lock (_sync)
                {
                    _cookie = cookie;
                    _secUserId = secUserId;
                    _nickname = nickname;
                    _state = DouyinQrLoginStates.Success;
                    _message = string.IsNullOrEmpty(secUserId)
                        ? "扫码成功，但未能自动获取 secUserId，请手动填写"
                        : "扫码成功";
                    _finishedUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                SetState(DouyinQrLoginStates.Canceled, "已取消扫码");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "抖音扫码登录失败");
                // InvalidOperationException 的消息是写给用户看的（没有浏览器、连不上浏览器等）
                SetState(
                    DouyinQrLoginStates.Failed,
                    ex is InvalidOperationException ? ex.Message : "扫码登录出错：" + ex.Message);
            }
            finally
            {
                await CleanupAsync(browser);
            }
        }

        private async Task OpenLoginPageAsync(CancellationToken ct)
        {
            // 独立的浏览器上下文：不带任何已有登录态，远程浏览器被多次复用时也互不影响
            var context = await _cdp.SendAsync(
                "Target.createBrowserContext", new { disposeOnDetach = true }, cancellationToken: ct);
            _browserContextId = context.Value<string>("browserContextId");

            var target = await _cdp.SendAsync(
                "Target.createTarget",
                new { url = "about:blank", browserContextId = _browserContextId },
                cancellationToken: ct);
            _targetId = target.Value<string>("targetId");

            var attached = await _cdp.SendAsync(
                "Target.attachToTarget", new { targetId = _targetId, flatten = true }, cancellationToken: ct);
            _pageSessionId = attached.Value<string>("sessionId");

            await PageSendAsync("Page.enable", null, ct);
            await PageSendAsync("Network.enable", null, ct);

            var version = await _cdp.SendAsync("Browser.getVersion", cancellationToken: ct);
            await PageSendAsync(
                "Emulation.setUserAgentOverride",
                new
                {
                    userAgent = NormalizeUserAgent(version.Value<string>("userAgent")),
                    acceptLanguage = "zh-CN,zh;q=0.9"
                },
                ct);
            await PageSendAsync(
                "Emulation.setDeviceMetricsOverride",
                new { width = ViewportWidth, height = ViewportHeight, deviceScaleFactor = 1, mobile = false },
                ct);

            await PageSendAsync("Page.navigate", new { url = LoginUrl }, ct);
        }

        /// <summary>
        /// 等待用户扫码并确认。以浏览器里出现会话 Cookie 为准，不依赖接口返回格式。
        /// </summary>
        private async Task<bool> WaitForLoginAsync(CancellationToken ct)
        {
            var startedUtc = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(1000, ct);

                var cookies = await _cdp.SendAsync(
                    "Storage.getCookies", new { browserContextId = _browserContextId }, cancellationToken: ct);
                if (ContainsLoginCookie(cookies["cookies"] as JArray))
                    return true;

                var now = DateTime.UtcNow;
                bool idle, qrMissing, expired;
                DateTime lastActivityUtc;
                lock (_sync)
                {
                    lastActivityUtc = _lastActivityUtc > startedUtc ? _lastActivityUtc : startedUtc;
                    idle = now - _lastPolledUtc > IdleTimeout;
                    qrMissing = _state == DouyinQrLoginStates.Starting && now - startedUtc > QrAppearTimeout;
                    expired = _state == DouyinQrLoginStates.Expired;
                }

                if (expired)
                    return false;

                if (idle)
                {
                    SetState(DouyinQrLoginStates.Canceled, "页面已关闭，扫码已取消");
                    return false;
                }

                if (now - lastActivityUtc > ScanTimeout || now - startedUtc > MaxSessionTime)
                {
                    SetState(DouyinQrLoginStates.Expired, "扫码超时，请重新获取二维码");
                    return false;
                }

                if (qrMissing)
                {
                    // 没截获到二维码接口的返回（接口格式变化或页面弹了验证），前端会改为展示浏览器截图
                    SetState(DouyinQrLoginStates.Waiting, QrImageMissingMessage);
                }
            }
        }

        /// <summary>
        /// 登录成功后登录页会带票据跳回 www.douyin.com 并写入该域的 Cookie。
        /// 先等它自己跳完，等不到再主动打开首页。
        /// </summary>
        private async Task SettleOnDouyinHomeAsync(CancellationToken ct)
        {
            if (!await WaitForHomeAsync(TimeSpan.FromSeconds(12), false, ct))
                await PageSendAsync("Page.navigate", new { url = DouyinHome }, ct);

            await WaitForHomeAsync(TimeSpan.FromSeconds(20), true, ct);
            // 首页加载完成后还会陆续写入几个 Cookie
            await Task.Delay(2000, ct);
        }

        private async Task<bool> WaitForHomeAsync(TimeSpan timeout, bool requireLoaded, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var host = await EvaluateStringAsync("location.hostname", false, ct);
                if (host == DouyinHomeHost)
                {
                    if (!requireLoaded)
                        return true;
                    if (await EvaluateStringAsync("document.readyState", false, ct) == "complete")
                        return true;
                }
                await Task.Delay(500, ct);
            }
            return false;
        }

        private async Task<string> ReadCookieHeaderAsync(CancellationToken ct)
        {
            var result = await PageSendAsync("Network.getCookies", new { urls = new[] { DouyinHome } }, ct);
            return BuildCookieHeader(result["cookies"] as JArray);
        }

        private async Task<(string SecUserId, string Nickname)> ReadSelfProfileAsync(CancellationToken ct)
        {
            // 在页面内发请求，由页面里的安全 SDK 自动补签名
            const string script = @"(async () => {
  try {
    const r = await fetch('/aweme/v1/web/user/profile/self/?device_platform=webapp&aid=6383&channel=channel_pc_web&publish_video_strategy_type=2&pc_client_type=1&cookie_enabled=true&platform=PC', { credentials: 'include' });
    return await r.text();
  } catch (e) { return ''; }
})()";

            var parsed = ParseSelfProfile(await EvaluateStringAsync(script, true, ct));
            if (!string.IsNullOrEmpty(parsed.SecUserId))
                return parsed;

            // 首页自己也会请求这个接口，主动请求失败时用截获到的结果兜底
            string captured;
            lock (_sync) { captured = _profileBody; }
            return ParseSelfProfile(captured);
        }

        private async Task<string> EvaluateStringAsync(string expression, bool awaitPromise, CancellationToken ct)
        {
            try
            {
                var result = await PageSendAsync(
                    "Runtime.evaluate",
                    new { expression, awaitPromise, returnByValue = true },
                    ct);
                return result["result"]?["value"]?.Type == JTokenType.String
                    ? result["result"].Value<string>("value")
                    : null;
            }
            catch (Exception ex) when (ex is CdpException || ex is TimeoutException)
            {
                // 页面跳转过程中执行上下文会被销毁
                return null;
            }
        }

        private Task<JObject> PageSendAsync(string method, object parameters, CancellationToken ct)
        {
            return _cdp.SendAsync(method, parameters, _pageSessionId, cancellationToken: ct);
        }

        private void OnCdpEvent(string sessionId, string method, JObject parameters)
        {
            if (sessionId == null || sessionId != _pageSessionId)
                return;

            switch (method)
            {
                case "Network.responseReceived":
                    var resourceType = parameters.Value<string>("type");
                    if (resourceType != "XHR" && resourceType != "Fetch")
                        return;
                    var kind = ClassifyUrl(parameters["response"]?.Value<string>("url"));
                    var requestId = parameters.Value<string>("requestId");
                    if (kind != null && requestId != null)
                        _trackedRequests[requestId] = kind;
                    break;

                case "Network.loadingFinished":
                    var finishedId = parameters.Value<string>("requestId");
                    if (finishedId != null && _trackedRequests.TryRemove(finishedId, out var finishedKind))
                        _ = HandleResponseBodyAsync(finishedId, finishedKind);
                    break;

                case "Network.loadingFailed":
                    var failedId = parameters.Value<string>("requestId");
                    if (failedId != null)
                        _trackedRequests.TryRemove(failedId, out _);
                    break;
            }
        }

        private async Task HandleResponseBodyAsync(string requestId, string kind)
        {
            try
            {
                var result = await _cdp.SendAsync(
                    "Network.getResponseBody", new { requestId }, _pageSessionId, 10000, _cts.Token);
                var body = result.Value<string>("body");
                if (string.IsNullOrEmpty(body))
                    return;
                if (result.Value<bool?>("base64Encoded") == true)
                    body = Encoding.UTF8.GetString(Convert.FromBase64String(body));

                switch (kind)
                {
                    case RequestKinds.Qr:
                        ApplyQrResponse(body);
                        break;
                    case RequestKinds.Check:
                        ApplyCheckResponse(body);
                        break;
                    case RequestKinds.Profile:
                        lock (_sync) { _profileBody = body; }
                        break;
                }
            }
            catch (Exception ex) when (ex is CdpException || ex is TimeoutException || ex is OperationCanceledException
                || ex is ObjectDisposedException || ex is FormatException)
            {
                // 响应体取不到（页面已跳转、连接已关闭）时忽略，登录结果以 Cookie 为准
            }
        }

        private void ApplyQrResponse(string body)
        {
            var (image, error) = ParseQrResponse(body);
            lock (_sync)
            {
                if (DouyinQrLoginStates.IsFinal(_state) || _state == DouyinQrLoginStates.Confirming)
                    return;

                if (image != null)
                {
                    _qrImage = image;
                    _qrVersion++;
                    _state = DouyinQrLoginStates.Waiting;
                    _message = "请打开抖音 App 扫码登录";
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    _message = error;
                }
            }
        }

        private void ApplyCheckResponse(string body)
        {
            var (status, error) = ParseCheckResponse(body);
            lock (_sync)
            {
                if (DouyinQrLoginStates.IsFinal(_state) || _state == DouyinQrLoginStates.Confirming)
                    return;

                switch (status)
                {
                    case "1":
                    case "new":
                        // 页面已经在轮询扫码状态，说明二维码已显示；图片没截获到时让前端展示浏览器截图
                        _message = _qrImage == null ? QrImageMissingMessage : "请打开抖音 App 扫码登录";
                        _state = DouyinQrLoginStates.Waiting;
                        break;
                    case "2":
                    case "scanned":
                        _state = DouyinQrLoginStates.Scanned;
                        _message = "已扫码，请在手机上确认登录";
                        break;
                    case "3":
                    case "confirmed":
                        _message = "已确认，正在登录...";
                        break;
                    case "4":
                    case "5":
                    case "expired":
                        // 用户正在页面上做身份验证时不能因为二维码过期就关掉浏览器，交给整体超时兜底
                        if (_showBrowser)
                            break;
                        _state = DouyinQrLoginStates.Expired;
                        _message = "二维码已失效，请刷新";
                        _finishedUtc = DateTime.UtcNow;
                        break;
                    default:
                        // 风控、二次验证等情况抖音只给一段文字说明，原样展示给用户，
                        // 并让前端切到浏览器画面，由用户在页面上完成验证
                        if (!string.IsNullOrEmpty(error))
                        {
                            _message = error;
                            _showBrowser = true;
                        }
                        break;
                }
            }
        }

        private void SetState(string state, string message)
        {
            lock (_sync)
            {
                // 终态不再被覆盖（例如二维码失效后紧跟着的取消）
                if (DouyinQrLoginStates.IsFinal(_state))
                    return;

                _state = state;
                _message = message;
                if (DouyinQrLoginStates.IsFinal(state))
                    _finishedUtc = DateTime.UtcNow;
            }
        }

        private bool IsFinal()
        {
            lock (_sync) { return DouyinQrLoginStates.IsFinal(_state); }
        }

        private async Task CleanupAsync(QrBrowserHandle browser)
        {
            var cdp = _cdp;
            _cdp = null;
            if (cdp != null)
            {
                cdp.EventReceived -= OnCdpEvent;
                if (browser != null && browser.IsRemote)
                {
                    // 远程浏览器是共用的，只清理本次会话创建的页面和上下文
                    try
                    {
                        if (_targetId != null)
                            await cdp.SendAsync("Target.closeTarget", new { targetId = _targetId }, timeoutMs: 3000);
                        if (_browserContextId != null)
                            await cdp.SendAsync("Target.disposeBrowserContext", new { browserContextId = _browserContextId }, timeoutMs: 3000);
                    }
                    catch (Exception ex) when (ex is CdpException || ex is TimeoutException || ex is OperationCanceledException) { }
                }
                await cdp.DisposeAsync();
            }

            if (browser != null)
                await browser.DisposeAsync();
            _trackedRequests.Clear();
        }

        private static class RequestKinds
        {
            public const string Qr = "qr";
            public const string Check = "check";
            public const string Profile = "profile";
        }

        #region 纯函数（便于单元测试）

        public static string ClassifyUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            var path = uri.AbsolutePath;
            if (path.Contains("/wechat/", StringComparison.OrdinalIgnoreCase))
                return null;
            if (path.EndsWith("/get_qrcode/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/get_qrcode", StringComparison.OrdinalIgnoreCase))
                return RequestKinds.Qr;
            if (path.EndsWith("/check_qrconnect/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/check_qrconnect", StringComparison.OrdinalIgnoreCase))
                return RequestKinds.Check;
            if (path.Contains("/user/profile/self", StringComparison.OrdinalIgnoreCase))
                return RequestKinds.Profile;
            return null;
        }

        /// <summary>
        /// 无头模式的 UA 带有 HeadlessChrome 字样，换成普通浏览器的写法。
        /// </summary>
        public static string NormalizeUserAgent(string userAgent)
        {
            return string.IsNullOrWhiteSpace(userAgent)
                ? DouyinRequestParamManager.DY_USER_AGENT
                : userAgent.Replace("HeadlessChrome/", "Chrome/");
        }

        /// <summary>
        /// 解析 get_qrcode 接口返回，得到可直接展示的二维码图片（data URL）或错误说明。
        /// </summary>
        public static (string Image, string Error) ParseQrResponse(string body)
        {
            var root = TryParseObject(body);
            if (root == null)
                return (null, null);

            var data = root["data"] as JObject;
            var qrcode = data?.Value<string>("qrcode");
            if (!string.IsNullOrWhiteSpace(qrcode))
            {
                if (qrcode.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    return (qrcode, null);
                // 其余情况必须是 base64 图片；如果哪天改成了链接，这里取不到图，前端会退回到浏览器截图
                if (!qrcode.Contains("://") && IsBase64(qrcode))
                    return ("data:image/png;base64," + qrcode, null);
            }

            return (null, ReadError(root, data));
        }

        /// <summary>
        /// 解析 check_qrconnect 接口返回，得到扫码状态或错误说明。
        /// </summary>
        public static (string Status, string Error) ParseCheckResponse(string body)
        {
            var root = TryParseObject(body);
            if (root == null)
                return (null, null);

            var data = root["data"] as JObject;
            var status = data?["status"]?.ToString();
            if (!string.IsNullOrWhiteSpace(status))
                return (status.Trim().ToLowerInvariant(), null);

            return (null, ReadError(root, data));
        }

        public static (string SecUserId, string Nickname) ParseSelfProfile(string body)
        {
            var user = TryParseObject(body)?["user"] as JObject;
            if (user == null)
                return (null, null);

            var secUserId = user.Value<string>("sec_uid");
            return (string.IsNullOrWhiteSpace(secUserId) ? null : secUserId, user.Value<string>("nickname"));
        }

        /// <summary>
        /// 浏览器里是否已经有登录后的会话 Cookie。
        /// </summary>
        public static bool ContainsLoginCookie(JArray cookies)
        {
            if (cookies == null)
                return false;

            foreach (var cookie in cookies.OfType<JObject>())
            {
                var domain = cookie.Value<string>("domain") ?? string.Empty;
                if (!domain.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsLoginCookieName(cookie.Value<string>("name")) && !string.IsNullOrEmpty(cookie.Value<string>("value")))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 把 CDP 返回的 Cookie 列表拼成请求头里的 Cookie 字符串。
        /// </summary>
        public static string BuildCookieHeader(JArray cookies)
        {
            if (cookies == null)
                return string.Empty;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var builder = new StringBuilder();
            foreach (var cookie in cookies.OfType<JObject>())
            {
                var name = cookie.Value<string>("name");
                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                    continue;
                if (builder.Length > 0)
                    builder.Append("; ");
                builder.Append(name).Append('=').Append(cookie.Value<string>("value"));
            }
            return builder.ToString();
        }

        public static bool HasLoginCookieName(string cookieHeader)
        {
            if (string.IsNullOrWhiteSpace(cookieHeader))
                return false;

            foreach (var part in cookieHeader.Split(';'))
            {
                var index = part.IndexOf('=');
                if (index > 0 && index < part.Length - 1 && IsLoginCookieName(part.Substring(0, index).Trim()))
                    return true;
            }
            return false;
        }

        private static bool IsLoginCookieName(string name)
        {
            return name == "sessionid" || name == "sessionid_ss";
        }

        private static string ReadError(JObject root, JObject data)
        {
            var description = data?.Value<string>("description");
            if (string.IsNullOrWhiteSpace(description))
                description = root["description"]?.Type == JTokenType.String ? root.Value<string>("description") : null;
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }

        private static JObject TryParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                return JObject.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsBase64(string value)
        {
            if (value.Length < 64)
                return false;
            foreach (var c in value)
            {
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '+' || c == '/' || c == '=' || c == '\r' || c == '\n';
                if (!ok)
                    return false;
            }
            return true;
        }

        #endregion
    }
}
