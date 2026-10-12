using Newtonsoft.Json.Linq;
using Serilog;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace dy.net.utils
{
    /// <summary>
    /// 扫码登录使用的浏览器实例。远程浏览器只断开连接；本地拉起的浏览器在释放时结束进程并清理临时目录。
    /// </summary>
    public sealed class QrBrowserHandle : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _userDataDir;

        public string WebSocketUrl { get; }
        public bool IsRemote { get; }

        public QrBrowserHandle(string webSocketUrl, Process process = null, string userDataDir = null)
        {
            WebSocketUrl = webSocketUrl;
            _process = process;
            _userDataDir = userDataDir;
            IsRemote = process == null && userDataDir == null;
        }

        public async ValueTask DisposeAsync()
        {
            if (IsRemote)
                return;

            // Windows 上启动进程可能马上退出、由它拉起的另一个进程才是真正的浏览器，
            // 只杀启动进程关不掉浏览器，所以先通过调试协议让浏览器自己退出
            if (!string.IsNullOrEmpty(WebSocketUrl))
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await using var cdp = await CdpClient.ConnectAsync(WebSocketUrl, timeout.Token);
                    await cdp.SendAsync("Browser.close", timeoutMs: 3000);
                }
                catch (Exception)
                {
                    // 浏览器已经退出或连接已断开
                }
            }

            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(true);
                        _process.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "结束扫码浏览器进程失败");
                }
                finally
                {
                    _process.Dispose();
                }
            }

            if (!string.IsNullOrEmpty(_userDataDir))
            {
                // 浏览器退出后文件句柄要过一会儿才释放，重试几次
                for (var i = 0; i < 20; i++)
                {
                    try
                    {
                        if (Directory.Exists(_userDataDir))
                            Directory.Delete(_userDataDir, true);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        await Task.Delay(300);
                    }
                }
                Log.Error("扫码浏览器临时目录清理失败：{Dir}", _userDataDir);
            }
        }
    }

    /// <summary>
    /// 为扫码登录提供一个 Chromium 内核浏览器：
    /// 1. 配置了 QrBrowserUrl 时连接远程浏览器（Docker 下的浏览器容器）；
    /// 2. 否则在本机查找 Chrome/Edge/Chromium 并以无头模式拉起。
    /// </summary>
    public static class QrBrowserProvider
    {
        /// <summary>远程浏览器调试地址，例如 http://dy-chrome:9222</summary>
        public const string RemoteUrlKey = "QrBrowserUrl";

        /// <summary>本机浏览器可执行文件路径（可选，未配置时自动查找）</summary>
        public const string LocalPathKey = "QrBrowserPath";

        public const string NoBrowserMessage =
            "未找到可用的浏览器，无法扫码。Docker 部署请按文档添加浏览器容器并配置 QrBrowserUrl，或改用手动粘贴 Cookie";

        public static async Task<QrBrowserHandle> AcquireAsync(CancellationToken cancellationToken)
        {
            var remoteUrl = GetSetting(RemoteUrlKey);
            if (!string.IsNullOrWhiteSpace(remoteUrl))
            {
                var webSocketUrl = await ResolveRemoteWebSocketUrlAsync(remoteUrl.Trim(), cancellationToken);
                return new QrBrowserHandle(webSocketUrl);
            }

            var executable = FindLocalBrowser();
            if (string.IsNullOrEmpty(executable))
                throw new InvalidOperationException(NoBrowserMessage);

            return await LaunchLocalAsync(executable, cancellationToken);
        }

        private static string GetSetting(string key)
        {
            try
            {
                return Appsettings.Configuration?[key];
            }
            catch
            {
                return null;
            }
        }

        public static string FindLocalBrowser()
        {
            var candidates = new List<string>
            {
                GetSetting(LocalPathKey),
                Environment.GetEnvironmentVariable("CHROME_PATH"),
                Environment.GetEnvironmentVariable("CHROME_BIN")
            };

            if (OperatingSystem.IsWindows())
            {
                var roots = new[]
                {
                    Environment.GetEnvironmentVariable("ProgramFiles"),
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                    Environment.GetEnvironmentVariable("LocalAppData")
                };
                foreach (var relative in new[]
                {
                    @"Google\Chrome\Application\chrome.exe",
                    @"Microsoft\Edge\Application\msedge.exe",
                    @"Chromium\Application\chrome.exe"
                })
                {
                    foreach (var root in roots)
                    {
                        if (!string.IsNullOrEmpty(root))
                            candidates.Add(Path.Combine(root, relative));
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
                candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
                candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
            }
            else
            {
                candidates.Add("/usr/bin/chromium");
                candidates.Add("/usr/bin/chromium-browser");
                candidates.Add("/usr/bin/google-chrome");
                candidates.Add("/usr/bin/google-chrome-stable");
                candidates.Add("/usr/bin/microsoft-edge");
                candidates.Add("/usr/bin/chrome-headless-shell");
                candidates.Add("/headless-shell/headless-shell");
            }

            return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        }

        private static async Task<QrBrowserHandle> LaunchLocalAsync(string executable, CancellationToken cancellationToken)
        {
            var userDataDir = Path.Combine(Path.GetTempPath(), "dysync-qr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDataDir);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("--headless=new");
            // 端口交给浏览器自己分配，实际端口从 DevToolsActivePort 文件读取
            startInfo.ArgumentList.Add("--remote-debugging-port=0");
            startInfo.ArgumentList.Add("--user-data-dir=" + userDataDir);
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            startInfo.ArgumentList.Add("--disable-gpu");
            startInfo.ArgumentList.Add("--disable-extensions");
            startInfo.ArgumentList.Add("--mute-audio");
            startInfo.ArgumentList.Add("--window-size=1280,900");
            if (OperatingSystem.IsLinux())
            {
                // 容器内通常以 root 运行且 /dev/shm 很小
                startInfo.ArgumentList.Add("--no-sandbox");
                startInfo.ArgumentList.Add("--disable-dev-shm-usage");
            }
            startInfo.ArgumentList.Add("about:blank");

            var portFile = Path.Combine(userDataDir, "DevToolsActivePort");
            Process process = null;
            try
            {
                process = Process.Start(startInfo);
                if (process == null)
                    throw new InvalidOperationException("浏览器启动失败：" + executable);

                // 不读取的话缓冲区写满会卡住浏览器进程
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 以调试端口文件出现为准，不看启动进程是否还活着：
                // Windows 上启动进程可能马上退出，由它拉起的另一个进程继续运行
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var webSocketUrl = TryReadDevToolsActivePort(portFile);
                    if (webSocketUrl != null)
                        return new QrBrowserHandle(webSocketUrl, process, userDataDir);

                    await Task.Delay(150, cancellationToken);
                }

                throw new InvalidOperationException(
                    process.HasExited
                        ? $"浏览器启动失败（退出码 {process.ExitCode}）：{executable}"
                        : "等待浏览器调试端口超时：" + executable);
            }
            catch
            {
                // 取消或超时的时候浏览器可能已经起来了，要拿到调试地址才能把它关掉
                string startedUrl = null;
                for (var i = 0; process != null && startedUrl == null && i < 20; i++)
                {
                    startedUrl = TryReadDevToolsActivePort(portFile);
                    if (startedUrl == null)
                        await Task.Delay(150);
                }
                await new QrBrowserHandle(startedUrl, process, userDataDir).DisposeAsync();
                throw;
            }
        }

        private static string TryReadDevToolsActivePort(string portFile)
        {
            try
            {
                if (!File.Exists(portFile))
                    return null;

                using var stream = new FileStream(portFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return ParseDevToolsActivePort(reader.ReadToEnd());
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// DevToolsActivePort 文件：第一行端口，第二行浏览器级 WebSocket 路径。
        /// 文件还没写完时返回 null。
        /// </summary>
        public static string ParseDevToolsActivePort(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port <= 0 || !lines[1].StartsWith("/"))
                return null;

            return $"ws://127.0.0.1:{port}{lines[1]}";
        }

        /// <summary>
        /// 把配置的远程地址解析成浏览器级 WebSocket 地址。
        /// ws/wss 地址原样使用；http/https 地址通过 /json/version 查询。
        /// </summary>
        public static async Task<string> ResolveRemoteWebSocketUrlAsync(string configured, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint))
                throw new InvalidOperationException($"{RemoteUrlKey} 配置无效：{configured}");

            if (endpoint.Scheme == "ws" || endpoint.Scheme == "wss")
                return configured;

            if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"{RemoteUrlKey} 只支持 http/https/ws/wss 地址：{configured}");

            // Chrome 调试端口会拒绝 Host 头不是 IP 或 localhost 的请求，容器名需要先解析成 IP
            var authority = endpoint.Scheme == Uri.UriSchemeHttp
                ? await ResolveAuthorityAsync(endpoint, cancellationToken)
                : endpoint.Authority;

            using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            string body;
            try
            {
                body = await http.GetStringAsync($"{endpoint.Scheme}://{authority}/json/version", cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException($"无法连接扫码浏览器 {configured}：{ex.Message}", ex);
            }

            var reported = JObject.Parse(body).Value<string>("webSocketDebuggerUrl");
            if (string.IsNullOrEmpty(reported))
                throw new InvalidOperationException($"扫码浏览器 {configured} 未返回调试地址");

            return RewriteWebSocketAuthority(reported, endpoint.Scheme, authority);
        }

        /// <summary>
        /// 浏览器返回的调试地址里是它自己看到的主机名（通常是 127.0.0.1），需要换成我们实际能访问到的地址。
        /// </summary>
        public static string RewriteWebSocketAuthority(string reportedWebSocketUrl, string endpointScheme, string authority)
        {
            var reported = new Uri(reportedWebSocketUrl);
            var scheme = endpointScheme == Uri.UriSchemeHttps ? "wss" : "ws";
            return $"{scheme}://{authority}{reported.PathAndQuery}";
        }

        private static async Task<string> ResolveAuthorityAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            var host = endpoint.Host;
            // localhost 也解析成 IPv4：浏览器默认只监听 127.0.0.1，先试 ::1 会白等几秒
            if (!IPAddress.TryParse(host, out _))
            {
                IPAddress[] addresses;
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                }
                catch (SocketException ex)
                {
                    throw new InvalidOperationException($"无法解析扫码浏览器地址 {host}：{ex.Message}", ex);
                }

                var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? addresses.FirstOrDefault();
                if (address == null)
                    throw new InvalidOperationException($"无法解析扫码浏览器地址 {host}");

                host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            }

            return $"{host}:{endpoint.Port}";
        }
    }
}
