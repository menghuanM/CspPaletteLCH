using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CspPalette.Csp
{
    /// <summary>
    /// 配对信息与界面偏好的落盘存储。
    /// 位置：%APPDATA%\CspPaletteLCH\session.json
    /// **和 CspPalette 分开存**：两个面板的界面偏好不一样，
    /// 而且 Save() 是整份覆盖，共用同一个文件会互相抹掉对方的设置。
    ///
    /// 这个文件在窗口显示**之前**就要读（窗口位置、当前颜色槽），所以刻意不用
    /// JavaScriptSerializer：它会把 System.Web.Extensions 一起拉进来初始化，
    /// 实测整个进程的启动要多花 ~90ms（空跑 74ms / 用 JavaScriptSerializer 157ms）。
    /// 协议报文的 JSON 仍然走 JavaScriptSerializer（那是在后台线程上、连上之后才用），
    /// 这里只用一个认「扁平对象」的小解析器——文件格式是我们自己写的，就这么简单。
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

            /// <summary>尺寸是否被图钉锁住（锁住后不能拖边缘缩放）。</summary>
            public bool SizeLocked;
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
                Dictionary<string, object> dict = ParseFlat(text);
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
                s.SizeLocked = AsBool(dict, "SizeLocked", false);
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
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        private static double AsDouble(Dictionary<string, object> d, string k, double fallback)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }

        private static bool AsBool(Dictionary<string, object> d, string k, bool fallback)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return fallback;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
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
                    Directory.CreateDirectory(DirectoryPath);
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
                    dict["SizeLocked"] = s.SizeLocked;
                    File.WriteAllText(FilePath, WriteFlat(dict), Encoding.UTF8);
                }
                catch (Exception)
                {
                    // 存不下就算了，不影响使用
                }
            }
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

        /// <summary>尺寸是否被图钉锁住。</summary>
        public static bool GetSizeLocked() { return Current.SizeLocked; }

        /// <summary>记下锁定的开关状态并落盘。</summary>
        public static void SetSizeLocked(bool locked)
        {
            lock (Lock)
            {
                Stored s = Current;
                if (s.SizeLocked == locked) return;
                s.SizeLocked = locked;
            }
            Save();
        }

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

        // ==================================================================
        // 极简 JSON：只认「一层扁平对象」，值只支持 字符串 / 数字 / true / false / null
        // （session.json 就是我们自己写出来的这一种形状；读坏了就退回默认值）
        // ==================================================================

        private static Dictionary<string, object> ParseFlat(string text)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(text)) return dict;

            int i = 0;
            SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '{') return dict;
            i++;

            while (i < text.Length)
            {
                SkipWs(text, ref i);
                if (i >= text.Length || text[i] == '}') break;
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '"') break;              // 键名不是字符串：文件坏了，剩下的不读了

                string key = ReadString(text, ref i);
                if (key == null) break;

                SkipWs(text, ref i);
                if (i >= text.Length || text[i] != ':') break;
                i++;
                SkipWs(text, ref i);

                object value;
                if (i < text.Length && text[i] == '"')
                {
                    value = ReadString(text, ref i);
                }
                else if (Match(text, ref i, "null")) { value = null; }
                else if (Match(text, ref i, "true")) { value = true; }
                else if (Match(text, ref i, "false")) { value = false; }
                else
                {
                    double number;
                    value = ReadNumber(text, ref i, out number) ? (object)number : null;
                }
                dict[key] = value;
            }
            return dict;
        }

        private static void SkipWs(string text, ref int i)
        {
            while (i < text.Length)
            {
                char c = text[i];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n') break;
                i++;
            }
        }

        /// <summary>当前位置是不是某个字面量（是的话跳过它）。</summary>
        private static bool Match(string text, ref int i, string literal)
        {
            if (i + literal.Length > text.Length) return false;
            if (string.CompareOrdinal(text, i, literal, 0, literal.Length) != 0) return false;
            i += literal.Length;
            return true;
        }

        /// <summary>读一个带引号的字符串，失败返回 null。支持标准的反斜杠转义。</summary>
        private static string ReadString(string text, ref int i)
        {
            if (i >= text.Length || text[i] != '"') return null;
            i++;

            StringBuilder sb = new StringBuilder();
            while (i < text.Length)
            {
                char c = text[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= text.Length) return null;
                char e = text[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > text.Length) return null;
                        int code;
                        if (!int.TryParse(text.Substring(i, 4), NumberStyles.HexNumber,
                                          CultureInfo.InvariantCulture, out code)) return null;
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: return null;
                }
            }
            return null;
        }

        /// <summary>读一个数字（到逗号 / 右括号 / 空白为止），失败返回 false。</summary>
        private static bool ReadNumber(string text, ref int i, out double value)
        {
            int start = i;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == ',' || c == '}' || c == ' ' || c == '\t' || c == '\r' || c == '\n') break;
                i++;
            }
            value = 0;
            if (i == start) return false;
            return double.TryParse(text.Substring(start, i - start), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out value);
        }

        private static string WriteFlat(Dictionary<string, object> dict)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                AppendString(sb, kv.Key);
                sb.Append(':');

                object v = kv.Value;
                if (v == null) sb.Append("null");
                else if (v is string) AppendString(sb, (string)v);
                else if (v is bool) sb.Append((bool)v ? "true" : "false");
                else
                {
                    double d;
                    if (v is double && !double.IsNaN((double)v) && !double.IsInfinity((double)v))
                    {
                        d = (double)v;
                        sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                    }
                }
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}