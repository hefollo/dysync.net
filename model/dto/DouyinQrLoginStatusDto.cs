namespace dy.net.model.dto
{
    /// <summary>
    /// 扫码登录状态
    /// </summary>
    public static class DouyinQrLoginStates
    {
        /// <summary>正在启动浏览器、加载登录页</summary>
        public const string Starting = "starting";
        /// <summary>二维码已就绪，等待扫码</summary>
        public const string Waiting = "waiting";
        /// <summary>已扫码，等待手机端确认</summary>
        public const string Scanned = "scanned";
        /// <summary>已确认，正在提取 Cookie</summary>
        public const string Confirming = "confirming";
        public const string Success = "success";
        /// <summary>二维码失效或扫码超时</summary>
        public const string Expired = "expired";
        public const string Failed = "failed";
        public const string Canceled = "canceled";

        public static bool IsFinal(string state)
        {
            return state == Success || state == Expired || state == Failed || state == Canceled;
        }
    }

    public class DouyinQrLoginStatusDto
    {
        public string Id { get; set; }

        /// <summary>
        /// 取值见 <see cref="DouyinQrLoginStates"/>
        /// </summary>
        public string State { get; set; }

        public string Message { get; set; }

        /// <summary>
        /// 二维码版本号，前端带上已知版本轮询，版本没变时不重复下发图片
        /// </summary>
        public int QrVersion { get; set; }

        /// <summary>
        /// 二维码图片（data URL）。为空且处于等待状态时，前端改用浏览器截图展示
        /// </summary>
        public string QrImage { get; set; }

        /// <summary>
        /// 仅 success 状态返回
        /// </summary>
        public string Cookie { get; set; }

        public string SecUserId { get; set; }

        public string Nickname { get; set; }

        /// <summary>
        /// 抖音要求额外验证等需要用户在页面上操作的情况，前端应切换到浏览器画面
        /// </summary>
        public bool ShowBrowser { get; set; }
    }

    /// <summary>
    /// 前端在浏览器画面上的一次操作
    /// </summary>
    public class DouyinQrLoginActionDto
    {
        /// <summary>
        /// down / move / up（鼠标）、scroll（滚轮）、text（输入文字）、key（按键）
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// 页面坐标，与截图像素一致
        /// </summary>
        public double X { get; set; }

        public double Y { get; set; }

        public double DeltaY { get; set; }

        public string Text { get; set; }

        /// <summary>
        /// Enter / Backspace / Tab / Escape
        /// </summary>
        public string Key { get; set; }
    }
}
