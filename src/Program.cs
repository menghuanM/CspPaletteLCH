using System;
using System.Windows;
using System.Windows.Threading;
using CspPalette.Csp;
using CspPalette.Screen;
using CspPalette.UI;

namespace CspPalette
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            try
            {
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += OnUnhandledException;

                CspClient client = new CspClient();
                PairingScanner scanner = new PairingScanner();

                // 扫描线程里找到二维码后，回到 UI 线程去建立连接
                scanner.Found += delegate(CompanionEndpoint ep)
                {
                    app.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        string ip = ep.FirstReachableIp();
                        if (string.IsNullOrEmpty(ip)) return;
                        client.SetPairing(ip, ep.Port, ep.Password, ep.Generation);
                        scanner.Stop();
                    }));
                };

                bool stored = SessionStore.HasPairing;
                if (stored)
                {
                    client.LoadStoredPairing();
                }

                PaletteWindow palette = new PaletteWindow(client, scanner);
                app.MainWindow = palette;
                palette.Show();

                // 等窗口订阅好事件再让后台线程动起来：本地重连是毫秒级的，
                // 反过来的话「已连接」会发生在订阅之前，顶栏就一直挂着启动时那句状态。
                client.Start();
                if (!stored)
                {
                    // 自动扫描有次数上限，屏幕上没二维码时不会一直扫下去
                    scanner.Start(PairingScanner.AutoScanAttempts);
                }

                app.Run();

                client.Stop();
                scanner.Stop();
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：\r\n" + ex, "CSP 取色面板 OKLCH",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // 单条消息出错不该让整个面板挂掉
            e.Handled = true;
            try
            {
                MessageBox.Show("出现未处理的错误：\r\n" + e.Exception.Message, "CSP 取色面板 OKLCH",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception)
            {
            }
        }
    }
}