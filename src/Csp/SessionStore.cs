using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CspPalette.Csp
{
    /// <summary>
    /// 配对信息与界面偏好的落盘存储。
    /// 位置：%APPDATA%\CspPaletteLCH\session.json
    /// **和 CspPalette 分开存**：两个面板的界面偏好不一样，
    /// 而且 Save() 是整份覆盖，共用同一个文件会互相抹掉对方的设置。
    /// </summary>
    public static class SessionStore
    {
        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CspPaletteLCH");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(DirectoryPath, "session.json"); }
        }

        private class Stored
        {
            public string Host;
            public int Port;
            public string Password;
            public string Generation;

            /// <summary>面板在改哪个颜色槽（0 主色 / 1 副色）。</summary>
            public int ColorIndex;

            // 面板位置与尺寸（未设置时为 NaN 的替代值）
            public double Left = double.NaN;
            public double Top = double.NaN;
            public double Width = 0;
            public double Height = 0;
        }

        private static Stored _current;
        private static readonly object Lock = new object();

        private static Stored Current
        {
            get
            {
                lock (Lock)
                {
                    if (_current == null) _current = Load();
                    return _current;
                }
            }
        }

        private static Stored Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new Stored();
                string text = File.ReadAllText(FilePath, Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> dict = ser.DeserializeObject(text) as Dictionary<string, object>;
                Stored s = new Stored();
                if (dict == null) return s;

                s.Host = AsString(dict, "Host");
                s.Port = AsInt(dict, "Port");
                s.Password = AsString(dict, "Password");
                s.Generation = AsString(dict, "Generation");
                s.ColorIndex = AsInt(dict, "ColorIndex");
                s.Left = AsDouble(dict, "Left", double.NaN);
                s.Top = AsDouble(dict, "Top", double.NaN);
                s.Width = AsDouble(dict, "Width", 0);
                s.Height = AsDouble(dict, "Height", 0);
                return s;
            }
            catch (Exception)
            {
                return new Stored();
            }
        }

        private static string AsString(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return null;
            return v.ToString();
        }

        private static int AsInt(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return 0;
            try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        private static double AsDouble(Dictionary<string, object> d, string k, double fallback)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return fallback;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }

        public static void Save()
        {
            lock (Lock)
            {
                Stored s = _current;
                if (s == null) return;
                try
                {
                    System.IO.Directory.CreateDirectory(DirectoryPath);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    Dictionary<string, object> dict = new Dictionary<string, object>();
                    dict["Host"] = s.Host;
                    dict["Port"] = s.Port;
                    dict["Password"] = s.Password;
                    dict["Generation"] = s.Generation;
                    dict["ColorIndex"] = s.ColorIndex;
                    dict["Left"] = double.IsNaN(s.Left) ? (object)null : s.Left;
                    dict["Top"] = double.IsNaN(s.Top) ? (object)null : s.Top;
                    dict["Width"] = s.Width;
                    dict["Height"] = s.Height;
                    File.WriteAllText(FilePath, ser.Serialize(dict), Encoding.UTF8);
                }
                catch (Exception)
                {
                    // 存不下就算了，不影响使用
                }
            }
        }

        public static void ClearPairing()
        {
            lock (Lock)
            {
                Stored s = Current;
                s.Host = null;
                s.Port = 0;
                s.Password = null;
                s.Generation = null;
            }
            Save();
        }

        public static void SavePairing(string host, int port, string password, string generation)
        {
            lock (Lock)
            {
                Stored s = Current;
                s.Host = host;
                s.Port = port;
                s.Password = password;
                s.Generation = generation;
            }
            Save();
        }

        /// <summary>只更新会话密码（每次握手成功后必须调用）。</summary>
        public static void UpdatePassword(string password)
        {
            lock (Lock)
            {
                Stored s = Current;
                s.Password = password;
            }
            Save();
        }

        public static bool HasPairing
        {
            get
            {
                Stored s = Current;
                return !string.IsNullOrEmpty(s.Host) && s.Port > 0 && !string.IsNullOrEmpty(s.Password);
            }
        }

        public static string GetHost() { return Current.Host; }
        public static int GetPort() { return Current.Port; }
        public static string GetPassword() { return Current.Password; }
        public static string GetGeneration() { return Current.Generation; }

        public static int GetColorIndex() { return Current.ColorIndex; }

        public static void GetWindowRect(out double left, out double top, out double width, out double height)
        {
            Stored s = Current;
            left = s.Left;
            top = s.Top;
            width = s.Width;
            height = s.Height;
        }

        public static void SetWindowRect(double left, double top, double width, double height)
        {
            lock (Lock)
            {
                Stored s = Current;
                s.Left = left;
                s.Top = top;
                s.Width = width;
                s.Height = height;
            }
            Save();
        }
    }
}