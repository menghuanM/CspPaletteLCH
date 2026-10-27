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
        /// <summary>
        /// 两次扫描之间歇多久。3 秒：一轮 10 次约半分钟，够用户切回 CSP
        /// 打开「连接手机」，又不至于扫得太稀。
        /// </summary>
        private const int ScanIntervalMs = 3000;
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
        private volatile string _lastStatus;
        /// <summary>
        /// 第几轮扫描。每轮 Start 会 +1，上一轮的线程发现对不上就自己收工——
        /// 免得"停一下又马上重开"时两个线程同时扫（共用 _scanCount，进度会乱跳）。
        /// </summary>
        private int _generation;

        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>
        /// 最近一条状态文字（「未找到二维码 [3/10]」「未找到二维码（已试 10 次）」……）。
        /// 界面在扫描器停了之后还靠它记住最后发生了什么，别把顶栏退回一句空泛的
        /// 「等待扫码」；每轮 Start 会把它清空。
        /// </summary>
        public string LastStatus
        {
            get { return _lastStatus; }
        }

        /// <summary>
        /// 开始扫描，扫满 maxScans 次还没找到就自动停下，并把次数清零等下次再来。
        /// 自动配对和托盘里的「重新扫描」都传 <see cref="AutoScanAttempts"/>。
        /// </summary>
        public void Start(int maxScans)
        {
            if (_running) return;
            int generation = ++_generation;
            _maxScans = maxScans;
            _scanCount = 0;
            _lastStatus = null;
            _running = true;
            _thread = new Thread(new ThreadStart(delegate { Loop(generation); }));
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

        private void Loop(int generation)
        {
            try
            {
                while (_running && generation == _generation)
                {
                    // 先记这一轮是第几次：状态栏要显示 [3/10] 这种进度，
                    // 所以次数得在 ScanOnce 之前就位
                    _scanCount++;
                    try
                    {
                        ScanOnceCore(_scanCount);
                    }
                    catch (Exception)
                    {
                        // 后台线程里漏出异常会直接终结整个进程，这里必须兜住
                    }

                    // 这一轮已经被换掉了（用户又点了一次重新扫描）：别再改 _scanCount，
                    // 直接收工，把扫描让给新线程
                    if (generation != _generation) return;
                    if (!_running) break;

                    if (_scanCount >= _maxScans)
                    {
                        _running = false;
                        RaiseStatus("未找到二维码（已试 " + _maxScans + " 次）");
                        break;
                    }

                    // 分段睡：按 100ms 一小段睡，Stop() 最多等 100ms 就能退出。
                    // 用 remaining 而不是循环次数，间隔换成非 100 整倍数的值也不会睡少。
                    int remaining = ScanIntervalMs;
                    while (_running && generation == _generation && remaining > 0)
                    {
                        int step = Math.Min(100, remaining);
                        Thread.Sleep(step);
                        remaining -= step;
                    }
                }
            }
            finally
            {
                // 这一轮的截屏缓冲（一两张 8MB）用不上了，交还给系统；下一轮再按需申请
                ScreenCapture.ReleaseBuffers();
            }
        }

        /// <summary>
        /// 扫一次。返回解出来的连接信息；没扫到返回 null。
        /// 循环里（<see cref="Start"/> 起的线程）走 ScanOnceCore，带上进度；
        /// 手动重新扫描也是 Start 起线程，不走这里。
        /// </summary>
        public CompanionEndpoint ScanOnce()
        {
            return ScanOnceCore(0);
        }

        /// <summary>attempt 是循环里的第几次（从 1 开始）；0 表示不带进度的单次扫描。</summary>
        private CompanionEndpoint ScanOnceCore(int attempt)
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
                    RaiseStatus("未找到二维码" + Progress(attempt));
                }
                return ep;
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>循环扫描的进度后缀，例如 " [3/10]"；单次扫描（attempt 为 0）没有后缀。</summary>
        private string Progress(int attempt)
        {
            if (attempt <= 0 || _maxScans <= 0) return string.Empty;
            return " [" + attempt + "/" + _maxScans + "]";
        }

        private CompanionEndpoint CaptureAndDecode()
        {
            // 优先只看 CSP 窗口，又快又不容易误识别。
            IntPtr hwnd = ScreenCapture.FindCspWindow();
            CaptureRect region;
            if (hwnd != IntPtr.Zero && ScreenCapture.TryGetWindowRect(hwnd, out region))
            {
                CompanionEndpoint ep = DecodeRegion(region);
                if (ep != null) return ep;

                // CSP 窗口里没有就再看整个桌面，别在这一步就放弃：
                // 「连接手机」是**另一个顶层窗口**，主窗口不一定盖得住它；
                // CSP 挪到第二块屏、或者窗口被最小化时，只盯主窗口会一直扫不到
                // （实测：CSP 开着但二维码小窗在别处时，原来会一直"未找到二维码"）。
                // 有 QrUrlPrefix 兜着，扫全屏也不会误认成别人的码。
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
            _lastStatus = text;
            Action<string> handler = StatusChanged;
            if (handler != null)
            {
                try { handler(text); }
                catch (Exception) { }
            }
        }
    }
}