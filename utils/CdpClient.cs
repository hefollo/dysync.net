using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace dy.net.utils
{
    /// <summary>
    /// CDP 命令返回 error 时抛出
    /// </summary>
    public class CdpException : Exception
    {
        public CdpException(string message) : base(message) { }
    }

    /// <summary>
    /// 极简 Chrome DevTools Protocol 客户端（基于 WebSocket），仅供扫码登录使用。
    /// 不引入 Playwright/Puppeteer，避免发布包和镜像体积增大。
    /// </summary>
    public sealed class CdpClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending = new();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private Task _receiveLoop;
        private int _nextId;

        /// <summary>
        /// 浏览器推送的事件：sessionId（浏览器级事件为 null）、方法名、参数。
        /// 回调在接收循环里同步触发，处理方不能在回调内同步等待 SendAsync，否则会死锁。
        /// </summary>
        public event Action<string, string, JObject> EventReceived;

        private CdpClient() { }

        public static async Task<CdpClient> ConnectAsync(string webSocketUrl, CancellationToken cancellationToken)
        {
            var client = new CdpClient();
            // 本机/局域网浏览器不能走系统代理
            client._socket.Options.Proxy = null;
            client._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                await client._socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken);
            }
            catch
            {
                client._socket.Dispose();
                throw;
            }
            client._receiveLoop = Task.Run(client.ReceiveLoopAsync);
            return client;
        }

        public async Task<JObject> SendAsync(
            string method,
            object parameters = null,
            string sessionId = null,
            int timeoutMs = 30000,
            CancellationToken cancellationToken = default)
        {
            var id = Interlocked.Increment(ref _nextId);
            var message = new JObject
            {
                ["id"] = id,
                ["method"] = method
            };
            if (parameters != null)
                message["params"] = parameters as JObject ?? JObject.FromObject(parameters);
            if (!string.IsNullOrEmpty(sessionId))
                message["sessionId"] = sessionId;

            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            linked.CancelAfter(timeoutMs);
            using var registration = linked.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out var pending))
                {
                    if (cancellationToken.IsCancellationRequested)
                        pending.TrySetCanceled(cancellationToken);
                    else
                        pending.TrySetException(new TimeoutException($"CDP 命令 {method} 超时或连接已关闭"));
                }
            });

            var bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            await _sendLock.WaitAsync(linked.Token);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, linked.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            return await tcs.Task;
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            using var stream = new MemoryStream();
            try
            {
                while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    stream.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return;
                        stream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    JObject message;
                    try
                    {
                        message = JObject.Parse(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    Dispatch(message);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                FailPending(new CdpException("浏览器连接已断开"));
            }
        }

        private void Dispatch(JObject message)
        {
            var idToken = message["id"];
            if (idToken != null && idToken.Type == JTokenType.Integer)
            {
                if (!_pending.TryRemove(idToken.Value<int>(), out var tcs))
                    return;

                if (message["error"] is JObject error)
                    tcs.TrySetException(new CdpException(error.Value<string>("message") ?? "CDP 命令执行失败"));
                else
                    tcs.TrySetResult(message["result"] as JObject ?? new JObject());
                return;
            }

            var method = message.Value<string>("method");
            if (string.IsNullOrEmpty(method))
                return;

            try
            {
                EventReceived?.Invoke(
                    message.Value<string>("sessionId"),
                    method,
                    message["params"] as JObject ?? new JObject());
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "处理 CDP 事件 {Method} 失败", method);
            }
        }

        private void FailPending(Exception exception)
        {
            foreach (var id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out var tcs))
                    tcs.TrySetException(exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, closeTimeout.Token);
                }
            }
            catch { }

            if (_receiveLoop != null)
            {
                try { await Task.WhenAny(_receiveLoop, Task.Delay(2000)); } catch { }
            }

            _socket.Dispose();
            _sendLock.Dispose();
            _cts.Dispose();
        }
    }
}
