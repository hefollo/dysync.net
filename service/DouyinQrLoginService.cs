using dy.net.model.dto;
using dy.net.utils;

namespace dy.net.service
{
    /// <summary>
    /// 抖音扫码登录。同一时间只保留一个扫码会话，新会话会顶掉旧会话。
    /// 需要注册为单例（见 Program.cs），会话状态保存在内存里。
    /// </summary>
    public class DouyinQrLoginService : IDisposable
    {
        private readonly object _gate = new object();
        private DouyinQrLoginSession _current;

        public DouyinQrLoginStatusDto Start()
        {
            var session = new DouyinQrLoginSession();
            DouyinQrLoginSession previous;
            lock (_gate)
            {
                previous = _current;
                _current = session;
            }

            previous?.Cancel();
            session.Start();
            return session.Snapshot(0);
        }

        public DouyinQrLoginStatusDto GetStatus(string id, int knownQrVersion)
        {
            return Find(id)?.Snapshot(knownQrVersion);
        }

        public async Task<string> CaptureScreenshotAsync(string id)
        {
            var session = Find(id);
            return session == null ? null : await session.CaptureScreenshotAsync();
        }

        public async Task<bool> PerformActionAsync(string id, DouyinQrLoginActionDto action)
        {
            var session = Find(id);
            return session != null && await session.PerformActionAsync(action);
        }

        /// <summary>
        /// 取消并丢弃会话（包括已经取到的 Cookie）
        /// </summary>
        public void Cancel(string id)
        {
            DouyinQrLoginSession session;
            lock (_gate)
            {
                session = _current;
                if (session == null || session.Id != id)
                    return;
                _current = null;
            }
            session.Cancel();
        }

        private DouyinQrLoginSession Find(string id)
        {
            lock (_gate)
            {
                return _current != null && _current.Id == id ? _current : null;
            }
        }

        public void Dispose()
        {
            DouyinQrLoginSession session;
            lock (_gate)
            {
                session = _current;
                _current = null;
            }

            if (session == null)
                return;

            // 应用退出时等浏览器进程收尾，避免留下孤儿进程
            session.Cancel();
            try { session.Completion.Wait(TimeSpan.FromSeconds(8)); } catch { }
        }
    }
}
