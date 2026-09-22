using System;
using System.Threading;
using CspPalette.Csp;
using CspPalette.Qr;

namespace CspPalette.Screen
{
    /// <summary>
    /// 后台循环截取 CSP 窗口，尝试识别「连接手机」弹出的二维码。
    /// 识别成功就触发 Found 事件，然后自动停下。
    /// </summary>
    public class PairingScanner
    {
        private const int ScanIntervalMs = 1200;
        /// <summary>截屏后把最长边缩到这个尺寸再解码，兼顾速度和识别率。</summary>
        private const int MaxDimension = 1600;
        /// <summary>自动扫描最多尝试几次。屏幕上没有二维码时不要一直扫下去。</summary>
        public const int AutoScanAttempts = 10;

        public event Action<CompanionEndpoint> Found;
        /// <summary>参数：状态文字</summary>
        public event Action<string> StatusChanged;

        private Thread _thread;
        private volatile bool _running;
        private volatile bool _busy;
        private int _maxScans;
        private int _scanCount;

        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>
        /// 开始扫描，扫满 maxScans 次还没找到就自动停下，并把次数清零等下次再来。
        /// 自动配对和设置里的「立即扫描」都传 <see cref="AutoScanAttempts"/>。
        /// </summary>
        public void Start(int maxScans)
        {
            if (_running) return;
            _maxScans = maxScans;
            _scanCount = 0;
            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "PairingScanner";
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            Thread t = _thread;
            if (t != null && t.IsAlive)
            {
                try { t.Join(1000); }
                catch (Exception) { }
            }
            _thread = null;
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    ScanOnce();
                }
                catch (Exception)
                {
                    // 后台线程里漏出异常会直接终结整个进程，这里必须兜住
                }
                if (!_running) break;

                _scanCount++;
                if (_scanCount >= _maxScans)
                {
                    _running = false;
                    RaiseStatus("已扫描 " + _maxScans + " 次仍未找到二维码，可在设置里点「立即扫描」重试");
                    break;
                }

                for (int i = 0; i < ScanIntervalMs / 100 && _running; i++)
                {
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>
        /// 扫一次。返回解出来的连接信息；没扫到返回 null。
        /// 手动「立即扫描」按钮也调这个。
        /// </summary>
        public CompanionEndpoint ScanOnce()
        {
            if (_busy) return null;
            _busy = true;
            try
            {
                CompanionEndpoint ep = CaptureAndDecode();
                if (ep != null)
                {
                    _running = false;
                    RaiseStatus("已识别二维码");
                    Action<CompanionEndpoint> handler = Found;
                    if (handler != null)
                    {
                        try { handler(ep); }
                        catch (Exception) { }
                    }
                }
                else
                {
                    RaiseStatus("未在屏幕上找到二维码，请保持 CSP 的「连接手机」窗口可见");
                }
                return ep;
            }
            finally
            {
                _busy = false;
            }
        }

        private CompanionEndpoint CaptureAndDecode()
        {
            // 优先只看 CSP 窗口，又快又不容易误识别；找不到窗口就退回整个桌面。
            IntPtr hwnd = ScreenCapture.FindCspWindow();
            CaptureRect region;
            if (hwnd != IntPtr.Zero && ScreenCapture.TryGetWindowRect(hwnd, out region))
            {
                CompanionEndpoint ep = DecodeRegion(region);
                if (ep != null) return ep;
                return null;
            }

            return DecodeRegion(ScreenCapture.GetVirtualScreenRect());
        }

        private CompanionEndpoint DecodeRegion(CaptureRect region)
        {
            int w, h;
            byte[] gray = ScreenCapture.CaptureGray(region, MaxDimension, out w, out h);
            if (gray == null || w <= 0 || h <= 0) return null;

            string text;
            try
            {
                text = QrDecoder.Decode(gray, w, h);
            }
            catch (Exception)
            {
                return null;
            }

            if (string.IsNullOrEmpty(text)) return null;
            if (text.IndexOf(CompanionCrypto.QrUrlPrefix, StringComparison.OrdinalIgnoreCase) != 0) return null;

            return CompanionCrypto.DecodeQrUrl(text);
        }

        private void RaiseStatus(string text)
        {
            Action<string> handler = StatusChanged;
            if (handler != null)
            {
                try { handler(text); }
                catch (Exception) { }
            }
        }
    }
}