using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CspPalette.Input
{
    /// <summary>哪个键。</summary>
    public enum PointerButton
    {
        Left,
        Right
    }

    /// <summary>一次按键事件。坐标是**屏幕物理像素**，和 GetCursorPos / GetWindowRect 同一坐标系。</summary>
    public class PointerEvent
    {
        public PointerButton Button;
        /// <summary>true = 按下，false = 松开。</summary>
        public bool Down;
        public int X;
        public int Y;
    }

    /// <summary>
    /// 全局鼠标按键观察器（WH_MOUSE_LL）。**只听，不拦**：回调里永远
    /// CallNextHookEx 原样放行，CSP 自己该收到的右键一个不少——画笔工具右键是吸色，
    /// 那正是我们要看的那一下。
    ///
    /// 钩子装在**自己的线程**上，带自己的消息泵：低层钩子的回调是在装钩子的那个线程上
    /// 同步跑的，装在 UI 线程上就会被面板的布局/绘图拖住，连累全系统的鼠标手感。
    /// 放到独立线程之后，回调只做「取坐标 → 抛事件」，剩下的活由订阅方自己切回 UI 线程。
    /// </summary>
    public class PointerHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int HC_ACTION = 0;

        private const int WM_QUIT = 0x0012;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>又一次按键。<b>在装钩子的那个后台线程上触发</b>，订阅方要自己切线程。</summary>
        public event Action<PointerEvent> Activity;

        private Thread _thread;
        private uint _threadId;
        private volatile bool _running;
        private IntPtr _hook = IntPtr.Zero;
        /// <summary>回调委托得留个引用，不然会被 GC 回收，钩子就指向野指针了。</summary>
        private HookProc _proc;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);

        /// <summary>装上钩子。装不上（比如被安全软件拦了）就默默算了，面板其余功能照旧。</summary>
        public void Start()
        {
            if (_thread != null) return;

            _running = true;
            _ready.Reset();
            _thread = new Thread(ThreadProc);
            _thread.IsBackground = true;
            _thread.Name = "PointerHook";
            _thread.Start();

            // 最多等一秒：等的是「钩子装好了没有」，不是等它跑完
            _ready.WaitOne(1000);
        }

        private void ThreadProc()
        {
            _threadId = GetCurrentThreadId();
            _proc = HookCallback;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            _ready.Set();

            if (_hook == IntPtr.Zero) return;

            // 低层钩子靠消息泵把回调送过来，这里只要个循环，收到什么都不用处理
            MSG msg;
            while (_running && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
            }

            // 钩子必须在装它的线程上卸
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // nCode < 0 时必须原样往下传，别的什么都别做
            if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

            try
            {
                if (nCode == HC_ACTION)
                {
                    int msg = wParam.ToInt32();
                    PointerButton button;
                    bool down;

                    if (msg == WM_LBUTTONDOWN) { button = PointerButton.Left; down = true; }
                    else if (msg == WM_LBUTTONUP) { button = PointerButton.Left; down = false; }
                    else if (msg == WM_RBUTTONDOWN) { button = PointerButton.Right; down = true; }
                    else if (msg == WM_RBUTTONUP) { button = PointerButton.Right; down = false; }
                    else return CallNextHookEx(_hook, nCode, wParam, lParam);

                    Action<PointerEvent> handler = Activity;
                    if (handler != null)
                    {
                        MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                            lParam, typeof(MSLLHOOKSTRUCT));
                        PointerEvent e = new PointerEvent();
                        e.Button = button;
                        e.Down = down;
                        e.X = info.pt.X;
                        e.Y = info.pt.Y;
                        handler(e);
                    }
                }
            }
            catch (Exception)
            {
                // 回调里出事绝不能把系统鼠标的状态机带坏
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            _running = false;

            Thread t = _thread;
            _thread = null;
            if (t == null) return;

            // 让那个线程的 GetMessage 返回 0，它自己收尾卸钩子
            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            try { t.Join(1000); }
            catch (Exception) { }

            _ready.Close();
        }
    }
}
