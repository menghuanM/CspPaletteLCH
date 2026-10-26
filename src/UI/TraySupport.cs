using System;
using System.Runtime.InteropServices;

namespace CspPalette.UI
{
    /// <summary>面板被 ✕ 收起时能去哪儿。</summary>
    internal enum PanelCloseMode
    {
        /// <summary>藏进托盘（正常情况：托盘图标在，双击能叫回来）。</summary>
        HideToTray,
        /// <summary>托盘图标指望不上，退回任务栏最小化——至少用户点得到。</summary>
        MinimizeToTaskbar
    }

    /// <summary>
    /// 托盘图标到底可不可靠。
    ///
    /// 为什么需要这个：面板窗口带 WS_EX_TOOLWINDOW（不上任务栏、不进 Alt+Tab，
    /// 这是用户明确要求的）。代价是**一旦托盘图标真的没注册上，按 ✕ 之后这个
    /// 窗口就彻底找不回来了**。而 Shell_NotifyIcon 失败是静默的，WinForms 的
    /// NotifyIcon 把返回值吞掉了。
    ///
    /// 重要原则：**这里只做只读检查，绝不碰通知区域。**
    /// 早先的版本会自己 NIM_ADD 一个临时图标去"问外壳收不收"，再根据结果决定
    /// 要不要把真图标藏起来——那个做法在真图标本来好好的时候反而把它弄没了
    /// （用户实测：旧版有图标，加了这个自检的版本没有图标）。自检只配决定
    /// ✕ 怎么收，没有资格动真实图标的可见性。
    ///
    /// 现在只认两种**确定**的阻塞原因：
    ///   1. 当前会话压根没有任务栏（explorer 没在跑）；
    ///   2. 本进程完整性级别低于 Medium——任务栏是 Medium，UIPI 会拦掉
    ///      Shell_NotifyIcon 的跨进程调用（实测 err=5）。
    /// 拿不准的时候一律假定"托盘是好的"：正常收进托盘，别没事找事。
    /// </summary>
    internal static class TraySupport
    {
        /// <summary>
        /// 纯逻辑部分，单独拿出来是为了能不碰外壳直接测
        /// （见 tests\TraySupportTest）。
        /// </summary>
        public static PanelCloseMode ResolveCloseMode(bool trayIconCreated, bool trayUsable)
        {
            return (trayIconCreated && trayUsable)
                ? PanelCloseMode.HideToTray
                : PanelCloseMode.MinimizeToTaskbar;
        }

        // ---- 只读检查 ----

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int len, out int retLen);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenIntegrityLevel = 25;
        private const int MediumIntegrityRid = 0x2000;

        /// <summary>
        /// 检查托盘能不能用。detail 是给人看的说明，会写进日志。
        /// **只读**：不创建任何东西、不往通知区域加减任何图标。
        /// </summary>
        public static bool IsTrayUsable(out string detail)
        {
            try
            {
                // 1. 任务栏在不在
                if (FindWindow("Shell_TrayWnd", null) == IntPtr.Zero)
                {
                    detail = "当前会话没有任务栏（Shell_TrayWnd 找不到，explorer 没在跑？）";
                    return false;
                }

                // 2. 完整性级别够不够。低完整性会被 UIPI 拦在任务栏外面，
                //    这是实测过的硬阻塞（Shell_NotifyIcon 返回 err=5）。
                int rid;
                if (!TryGetIntegrityRid(out rid))
                {
                    detail = "任务栏在；完整性级别读不出来，按可用处理";
                    return true;
                }
                if (rid < MediumIntegrityRid)
                {
                    detail = "任务栏在，但本进程完整性级别是 0x" + rid.ToString("X")
                             + "（低于 Medium），会被 UIPI 拦住，托盘注册必然失败";
                    return false;
                }

                detail = "任务栏在，完整性级别 0x" + rid.ToString("X") + "（>= Medium），正常";
                return true;
            }
            catch (Exception ex)
            {
                detail = "检查托盘时抛异常：" + ex.GetType().Name + ": " + ex.Message + "，按可用处理";
                return true;
            }
        }

        /// <summary>读当前进程令牌的完整性级别 RID。读不出来返回 false。</summary>
        public static bool TryGetIntegrityRid(out int rid)
        {
            rid = -1;
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;
                int needed;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out needed);
                if (needed <= 0) return false;

                buffer = Marshal.AllocHGlobal(needed);
                int got;
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out got)) return false;

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero) return false;
                byte count = Marshal.ReadByte(sid, 1);
                if (count == 0) return false;
                rid = Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        /// <summary>完整性级别 RID 的可读名字，日志和诊断模式都用它。</summary>
        public static string DescribeIntegrity(int rid)
        {
            switch (rid)
            {
                case 0x0000: return "Untrusted";
                case 0x1000: return "Low（会被任务栏拒绝）";
                case 0x2000: return "Medium（普通用户程序，正常）";
                case 0x2100: return "Medium Plus";
                case 0x3000: return "High（管理员）";
                case 0x4000: return "System";
                default: return "Unknown";
            }
        }
    }
}
