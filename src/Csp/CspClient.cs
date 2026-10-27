using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CspPalette.ColorLib;

namespace CspPalette.Csp
{
    public enum CspLinkState
    {
        /// <summary>还没有配对信息，需要扫二维码。</summary>
        Unpaired,
        /// <summary>正在尝试连接。</summary>
        Connecting,
        /// <summary>已连接并握手成功。</summary>
        Connected,
        /// <summary>曾经连上但断了，正在重试。</summary>
        Disconnected
    }

    /// <summary>
    /// 一次颜色同步里读到的两个颜色槽，外加"CSP 现在在改哪个槽"。
    /// 主色和副色是分开报的；CurrentColorIndex 变了就说明用户在 CSP 里切了槽，
    /// 面板要跟着切（否则拖滑条会把颜色写进另一个槽）。
    /// </summary>
    public class CspColors
    {
        public bool HasMain;
        public byte MainR, MainG, MainB;
        public bool HasSub;
        public byte SubR, SubG, SubB;
        /// <summary>这条消息里带没带 CurrentColorIndex。</summary>
        public bool HasIndex;
        /// <summary>CSP 当前在改的槽：0 主色 / 1 副色。</summary>
        public int Index;
    }

    /// <summary>
    /// CSP 同伴模式客户端：冒充手机端连上桌面版 CSP，读写笔刷颜色。
    /// 所有网络动作都在一个后台线程里跑，事件也在该线程触发，
    /// 界面层需要自己切回 UI 线程。
    /// </summary>
    public class CspClient
    {
        public const string CmdAuthenticate = "Authenticate";
        public const string CmdHeartbeat = "TellHeartbeat";
        public const string CmdSyncColor = "SyncColorCircleUIState";
        public const string CmdSetColor = "SetCurrentColor";
        public const string CmdGetModifyKey = "GetModifyKeyString";
        public const string CmdGetTabKind = "GetServerSelectedTabKind";
        public const string CmdSetTabKind = "SetServerSelectedTabKind";

        private const string ActiveHeartbeat = "{\"IdleTimerResetRequested\":true}";

        /// <summary>
        /// 读颜色时必须先声明一个「过期状态」，CSP 才会回全量颜色字段；
        /// 否则在状态没变化时它只回一个空对象。
        /// </summary>
        private const string StaleColorState =
            "{\"IsManipulating\":false,\"HSVColorMainH\":0,\"HSVColorMainS\":0,\"HSVColorMainV\":0," +
            "\"CurrentColorIndex\":0,\"ColorSelectionModel\":\"HSV\"}";

        /// <summary>
        /// 连续连不上多少次就放弃。CSP 关着的时候端口是死的，再连也是白连，
        /// 而且状态文字来回变会让设置窗口一下高一下矮。
        /// </summary>
        public const int MaxConnectAttempts = 10;

        private const int HeartbeatIntervalMs = 2000;
        private const int ColorPollIntervalMs = 1000;
        /// <summary>刚写完颜色后这段时间内不刷新读值，避免自己写出去的值回声覆盖滑块。</summary>
        private const int WriteEchoGuardMs = 800;

        // ---- 对外事件 ----
        public event Action<CspLinkState, string> StateChanged;
        /// <summary>每次拿到颜色同步结果都会触发，主色和副色一起带回来。</summary>
        public event Action<CspColors> ColorsRead;

        // ---- 配对信息 ----
        private readonly object _sync = new object();
        private string _host;
        private int _port;
        private string _password;
        private string _generation;
        /// <summary>true 表示当前密码来自刚扫到的二维码，还没用于认证过。</summary>
        private bool _freshPairing;

        // ---- 运行状态 ----
        private Thread _worker;
        private volatile bool _running;
        /// <summary>当前连接。UI 线程会调 CloseSocket（SetPairing），所以按 volatile 用，别缓存。</summary>
        private volatile Socket _socket;
        /// <summary>收到但还没凑成完整记录的字节。只在 _sync 里读写（UI 线程也会清它）。</summary>
        private readonly List<byte> _rxBuffer = new List<byte>();
        /// <summary>接收用的缓冲区，复用同一个（每轮 new 一个 8KB 数组太浪费）。</summary>
        private readonly byte[] _rxChunk = new byte[8192];
        /// <summary>握手里"发出去等回应"的响应（只有 SendAndWait 会来看）。</summary>
        private readonly List<CspMessage> _pending = new List<CspMessage>();
        private int _serial;
        /// <summary>状态。事件在后台线程触发、界面线程随时读，用 volatile 保证读得到最新的。</summary>
        private volatile CspLinkState _state = CspLinkState.Unpaired;
        private volatile string _stateDetail = "";

        private int _connectFailures;
        /// <summary>
        /// 对端明确拒绝了这次认证（密码不匹配）。这不是网络抖动，拿同一个密码
        /// 再连多少次都一样，所以别记重试次数、直接等新的配对信息。
        /// </summary>
        private volatile bool _authRejected;
        private DateTime _lastHeartbeat = DateTime.MinValue;
        private DateTime _lastColorPoll = DateTime.MinValue;
        private DateTime _lastWrite = DateTime.MinValue;

        // ---- 待发送的颜色（拖动时高频写入，这里只保留最新值）----
        private bool _colorDirty;
        private byte _pendR, _pendG, _pendB;
        private int _pendIndex;

        /// <summary>有人要求立刻读一次颜色（见 RequestColorSync），下一轮就别等轮询间隔了。</summary>
        private bool _forceColorPoll;

        public CspLinkState State
        {
            get { return _state; }
        }

        public string StateDetail
        {
            get { return _stateDetail; }
        }

        /// <summary>从磁盘恢复配对信息（走重连流程，不需要重新扫码）。</summary>
        public void LoadStoredPairing()
        {
            lock (_sync)
            {
                _host = SessionStore.GetHost();
                _port = SessionStore.GetPort();
                _password = SessionStore.GetPassword();
                _generation = SessionStore.GetGeneration();
                _freshPairing = false;
                _connectFailures = 0;
                _authRejected = false;
            }
        }

        /// <summary>扫码或手动粘贴拿到的新配对信息（走首次认证流程）。</summary>
        public void SetPairing(string host, int port, string password, string generation)
        {
            lock (_sync)
            {
                _host = host;
                _port = port;
                _password = password;
                _generation = generation;
                _freshPairing = true;
                _connectFailures = 0;   // 新配对信息，重新给满次数
                _authRejected = false;  // 换了密码，上一轮的"被拒绝"就不算数了
            }
            CloseSocket();
            SetState(CspLinkState.Connecting, host + ":" + port + "正在连接");
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "CspClient";
            _worker.Start();
        }

        public void Stop()
        {
            _running = false;
            Thread t = _worker;
            if (t != null && t.IsAlive)
            {
                try { t.Join(1500); }
                catch (Exception) { }
            }
            _worker = null;
            CloseSocket();
        }

        /// <summary>拖动滑块时调用；只保留最新值，避免把 CSP 刷爆。</summary>
        public void QueueColor(byte r, byte g, byte b, int colorIndex)
        {
            lock (_sync)
            {
                _pendR = r;
                _pendG = g;
                _pendB = b;
                _pendIndex = colorIndex;
                _colorDirty = true;
            }
        }

        /// <summary>
        /// 立刻读一次颜色，不等每秒那趟轮询。
        /// 右键吸色把面板叫出来时用：用户刚吸完的颜色得马上出现在面板上，
        /// 按轮询的节奏最多要等一秒，那就看得出来了。
        /// </summary>
        public void RequestColorSync()
        {
            lock (_sync)
            {
                _forceColorPoll = true;
            }
        }

        // ==================================================================
        // 后台线程
        // ==================================================================

        private void WorkerLoop()
        {
            while (_running)
            {
                try
                {
                    if (_socket == null)
                    {
                        if (!HasPairing())
                        {
                            SetState(CspLinkState.Unpaired, "等待扫码");
                            Thread.Sleep(300);
                            continue;
                        }
                        // 已经放弃重试了（连不上太多次 / 密码被明确拒绝），安静等着；
                        // 重新配对（SetPairing / LoadStoredPairing）会把标记清零，那时才继续连
                        if (_authRejected || _connectFailures >= MaxConnectAttempts)
                        {
                            Thread.Sleep(300);
                            continue;
                        }
                        if (!TryConnectAndAuthenticate())
                        {
                            // 被明确拒绝的不记次、也不再重试（见 _authRejected）
                            if (!_authRejected) CountConnectFailure();
                            Thread.Sleep(1500);
                            continue;
                        }
                        _connectFailures = 0;
                    }

                    PumpIncoming();

                    if (!IsSocketAlive())
                    {
                        CloseSocket();
                        SetState(CspLinkState.Disconnected, "连接断开");
                        continue;
                    }

                    DoPeriodicWork();
                    Thread.Sleep(10);
                }
                catch (Exception ex)
                {
                    CloseSocket();
                    SetState(CspLinkState.Disconnected, ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>
        /// 记一次连接失败；到上限就停下来，不再自己重试
        /// （状态文字只报地址和结果，不教用户怎么恢复）。
        /// </summary>
        private void CountConnectFailure()
        {
            _connectFailures++;
            if (_connectFailures < MaxConnectAttempts) return;

            string where;
            lock (_sync) { where = _host + ":" + _port; }
            SetState(CspLinkState.Disconnected, where + "连接失败");
        }

        private bool HasPairing()
        {
            lock (_sync)
            {
                return !string.IsNullOrEmpty(_host) && _port > 0 && !string.IsNullOrEmpty(_password);
            }
        }

        private bool TryConnectAndAuthenticate()
        {
            string host, password, generation;
            int port;
            bool fresh;
            lock (_sync)
            {
                host = _host;
                port = _port;
                password = _password;
                generation = _generation;
                fresh = _freshPairing;
            }

            SetState(CspLinkState.Connecting, host + ":" + port + "正在连接");

            Socket sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IAsyncResult ar = sock.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(3000))
                {
                    sock.Close();
                    SetState(CspLinkState.Disconnected, "连接超时");
                    return false;
                }
                sock.EndConnect(ar);
                sock.NoDelay = true;
            }
            catch (Exception ex)
            {
                if (sock != null) try { sock.Close(); } catch (Exception) { }
                SetState(CspLinkState.Disconnected, host + ":" + port + " " + ex.Message);
                return false;
            }

            _socket = sock;
            _rxBuffer.Clear();

            string newPassword;
            string currToken;
            if (fresh)
            {
                // 首次：用二维码里的密码，并把新密码换成我们自己生成的随机串
                newPassword = CompanionCrypto.MakeNewPassword();
                currToken = CompanionCrypto.ObfuscateAuth(password);
            }
            else
            {
                // 重连：用固定标记串，并原样重发上次握手时发出去的那个新密码
                newPassword = password;
                currToken = CompanionCrypto.ObfuscateAuth(CompanionCrypto.ReconnectMarker);
            }
            string newToken = CompanionCrypto.ObfuscateAuth(newPassword);

            object[] detail = new object[] { generation, currToken, newToken };
            string detailJson = CspFraming.SerializeJson(detail);

            CspMessage resp = SendAndWait(CmdAuthenticate, detailJson, 4000);
            if (resp == null || resp.Type != CspMsgType.Success)
            {
                string why = resp == null ? "没有收到响应" : (resp.Type == CspMsgType.Error ? "CSP 拒绝（密码不匹配）" : "响应异常");
                CloseSocket();
                SetState(CspLinkState.Disconnected, "失败：" + why);

                // Error 是对端**明确**说"这个密码不认"：不是网络抖动，再拿同一个
                // 密码连十次也是十次拒绝，每次还要等最多 4 秒握手超时。
                // 标记一下，让后台线程别再自己重试——等新的二维码（SetPairing）来救。
                if (resp != null && resp.Type == CspMsgType.Error)
                {
                    _authRejected = true;
                }
                return false;
            }

            // 认证成功后，新密码成为下次重连要用的密码，必须落盘
            lock (_sync)
            {
                _password = newPassword;
                _freshPairing = false;
                _authRejected = false;
            }
            SessionStore.SavePairing(host, port, newPassword, generation);

            Activate();
            SetState(CspLinkState.Connected, host + ":" + port + "已连接");
            return true;
        }

        /// <summary>握手成功后的激活动作，缺了它 CSP 不会给完整状态。</summary>
        private void Activate()
        {
            Send(CmdHeartbeat, ActiveHeartbeat);
            Send(CmdGetModifyKey, "{\"CtrlPushed\":false,\"AltPushed\":false,\"ShiftPushed\":false}");
            Send(CmdGetTabKind, "");
            Send(CmdSetTabKind, "");
            Send(CmdHeartbeat, ActiveHeartbeat);
        }

        private void DoPeriodicWork()
        {
            DateTime now = DateTime.Now;

            if ((now - _lastHeartbeat).TotalMilliseconds >= HeartbeatIntervalMs)
            {
                _lastHeartbeat = now;
                Send(CmdHeartbeat, ActiveHeartbeat);
            }

            bool wroteRecently = (now - _lastWrite).TotalMilliseconds < WriteEchoGuardMs;

            // 先处理待写入的颜色，再去读，避免刚写完又被读回来覆盖。
            // 这里只把最新的值抄出来：**发 socket 放到锁外面**。拖动滑条时 UI 线程
            // 正在 QueueColor 里等这把锁，send 万一卡住就等于把界面也卡住。
            bool forced;
            bool dirty;
            byte pendR, pendG, pendB;
            int pendIndex;
            lock (_sync)
            {
                forced = _forceColorPoll;
                _forceColorPoll = false;

                dirty = _colorDirty;
                _colorDirty = false;
                pendR = _pendR;
                pendG = _pendG;
                pendB = _pendB;
                pendIndex = _pendIndex;
            }

            if (dirty)
            {
                WriteColor(pendR, pendG, pendB, pendIndex);
                _lastWrite = DateTime.Now;
                wroteRecently = true;
            }

            bool due = (now - _lastColorPoll).TotalMilliseconds >= ColorPollIntervalMs;
            if (!wroteRecently && (forced || due))
            {
                _lastColorPoll = now;
                Send(CmdSyncColor, StaleColorState);
            }
        }

        private void WriteColor(byte r, byte g, byte b, int colorIndex)
        {
            double h, s, v;
            ColorMath.RgbToHsv(r, g, b, out h, out s, out v);

            // H 在 0..360，CSP 要的是 0..MAX_U32
            double hNorm = h / 360.0;
            if (hNorm < 0) hNorm = 0;
            if (hNorm >= 1) hNorm = 0.999999999;

            long hu = (long)Math.Round(hNorm * CspFraming.MaxU32);
            long su = (long)Math.Round(s * CspFraming.MaxU32);
            long vu = (long)Math.Round(v * CspFraming.MaxU32);

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"ColorSpaceKind\":\"HSV\",\"IsColorTransparent\":false");
            sb.Append(",\"HSVColorH\":").Append(hu.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"HSVColorS\":").Append(su.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"HSVColorV\":").Append(vu.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"ColorIndex\":").Append(colorIndex.ToString(CultureInfo.InvariantCulture));
            sb.Append("}");

            Send(CmdSetColor, sb.ToString());
        }

        // ==================================================================
        // 收发
        // ==================================================================

        private void Send(string command, string detailJson)
        {
            Socket sock = _socket;
            if (sock == null) return;
            int serial = ++_serial;
            byte[] bytes = CspFraming.Build(command, serial, detailJson);
            try
            {
                sock.Send(bytes);
            }
            catch (Exception)
            {
                CloseSocket();
            }
        }

        /// <summary>发送并等待指定 serial 的响应（只在握手阶段用）。</summary>
        private CspMessage SendAndWait(string command, string detailJson, int timeoutMs)
        {
            Socket sock = _socket;
            if (sock == null) return null;

            int serial = ++_serial;
            byte[] bytes = CspFraming.Build(command, serial, detailJson);
            try
            {
                sock.Send(bytes);
            }
            catch (Exception)
            {
                return null;
            }

            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                PumpIncoming();

                lock (_sync)
                {
                    for (int i = 0; i < _pending.Count; i++)
                    {
                        if (_pending[i].Serial == serial)
                        {
                            CspMessage m = _pending[i];
                            _pending.RemoveAt(i);
                            return m;
                        }
                    }
                }

                if (!IsSocketAlive()) return null;
                Thread.Sleep(10);
            }
            return null;
        }

        private bool IsSocketAlive()
        {
            // 真正判断断开是在 PumpIncoming 里（收到 0 字节就 CloseSocket）。
            // 这里只要 socket 还在，就认为还活着。
            return _socket != null;
        }

        private void PumpIncoming()
        {
            Socket sock = _socket;
            if (sock == null) return;

            bool closed = false;
            try
            {
                while (sock.Poll(0, SelectMode.SelectRead))
                {
                    if (sock.Available == 0)
                    {
                        // 对端关闭
                        closed = true;
                        break;
                    }
                    byte[] buf = _rxChunk;
                    int n = sock.Receive(buf);
                    if (n <= 0)
                    {
                        closed = true;
                        break;
                    }
                    // 缓冲区各线程都可能清（SetPairing / Stop 会 CloseSocket），进锁里加
                    lock (_sync)
                    {
                        for (int i = 0; i < n; i++) _rxBuffer.Add(buf[i]);
                    }
                }
            }
            catch (SocketException)
            {
                closed = true;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (closed)
            {
                CloseSocket();
                return;
            }

            List<CspMessage> msgs;
            lock (_sync)
            {
                msgs = CspFraming.Drain(_rxBuffer);
            }
            for (int i = 0; i < msgs.Count; i++)
            {
                HandleMessage(msgs[i]);
            }
        }

        private void HandleMessage(CspMessage msg)
        {
            // 只有握手阶段会"发出去等回应"（SendAndWait），别的响应没人来接，
            // 记下来只会越堆越多——SetColor 每拖一下发一条，一天下来就是十几万条。
            if (msg.Command == CmdAuthenticate)
            {
                lock (_sync) { _pending.Add(msg); }
            }

            AbsorbColor(msg);
        }

        /// <summary>任何一条消息里只要带了颜色（或当前色槽）字段就顺手更新，省得漏掉推送。</summary>
        private void AbsorbColor(CspMessage msg)
        {
            bool hasMain = msg.Has("HSVColorMainV");
            bool hasSub = msg.Has("HSVColorSubV");
            // CSP 每次回颜色状态都会带上"现在在改哪个槽"（0 主色 / 1 副色）。
            // 用户在 CSP 里切槽时要让面板跟着切，所以这条也要传出去。
            int? index = msg.GetInt("CurrentColorIndex");
            if (!hasMain && !hasSub && index == null) return;

            CspColors colors = new CspColors();
            if (index != null)
            {
                colors.HasIndex = true;
                colors.Index = index.Value;
            }

            if (hasMain)
            {
                byte r, g, b;
                if (TryReadHsv(msg, "Main", out r, out g, out b))
                {
                    colors.HasMain = true;
                    colors.MainR = r; colors.MainG = g; colors.MainB = b;
                }
            }

            if (hasSub)
            {
                byte r, g, b;
                if (TryReadHsv(msg, "Sub", out r, out g, out b))
                {
                    colors.HasSub = true;
                    colors.SubR = r; colors.SubG = g; colors.SubB = b;
                }
            }

            if (!colors.HasMain && !colors.HasSub && !colors.HasIndex) return;

            Action<CspColors> handler = ColorsRead;
            if (handler != null)
            {
                try { handler(colors); }
                catch (Exception) { }
            }
        }

        /// <summary>把一个槽的 HSV 字段换算成 sRGB 字节。</summary>
        private static bool TryReadHsv(CspMessage msg, string prefix, out byte r, out byte g, out byte b)
        {
            r = 0; g = 0; b = 0;

            double? h = msg.GetDouble("HSVColor" + prefix + "H");
            double? s = msg.GetDouble("HSVColor" + prefix + "S");
            double? v = msg.GetDouble("HSVColor" + prefix + "V");
            if (h == null || s == null || v == null) return false;

            // 协议里是 uint32 归一化：H 映射到 0..360 度，S/V 映射到 0..100%
            ColorMath.HsvToRgb(
                h.Value / CspFraming.MaxU32 * 360.0,
                s.Value / CspFraming.MaxU32,
                v.Value / CspFraming.MaxU32,
                out r, out g, out b);
            return true;
        }

        private void CloseSocket()
        {
            Socket sock = _socket;
            _socket = null;
            if (sock != null)
            {
                try { sock.Shutdown(SocketShutdown.Both); }
                catch (Exception) { }
                try { sock.Close(); }
                catch (Exception) { }
            }
            lock (_sync)
            {
                _pending.Clear();
                _rxBuffer.Clear();
            }
        }

        private void SetState(CspLinkState state, string detail)
        {
            // 状态和文字都没变就别惊动界面：Unpaired 那句「等待扫码」每 300ms
            // 就会重复报一次，每次都往 UI 线程塞一个 BeginInvoke，纯属白忙。
            if (_state == state && string.Equals(_stateDetail, detail, StringComparison.Ordinal)) return;

            _state = state;
            _stateDetail = detail;
            Action<CspLinkState, string> handler = StateChanged;
            if (handler != null)
            {
                try { handler(state, detail); }
                catch (Exception) { }
            }
        }
    }
}