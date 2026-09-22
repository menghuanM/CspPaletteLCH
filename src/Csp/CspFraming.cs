using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace CspPalette.Csp
{
    public enum CspMsgType
    {
        Unknown = 0,
        Command = 0x01,
        Success = 0x06,
        Error = 0x15
    }

    public class CspMessage
    {
        public CspMsgType Type;
        public string Command;
        public int Serial;
        /// <summary>原始的 detail 文本（可能是空串、JSON 对象或 JSON 数组）。</summary>
        public string RawDetail;

        private object _detail;
        private bool _detailParsed;

        /// <summary>
        /// detail 解析结果：JSON 对象会得到 Dictionary&lt;string, object&gt;，
        /// 数组得到 object[]，空则为 null。
        /// </summary>
        public object Detail
        {
            get
            {
                if (!_detailParsed)
                {
                    _detailParsed = true;
                    _detail = CspFraming.ParseJson(RawDetail);
                }
                return _detail;
            }
        }

        /// <summary>把 Detail 当对象字典取字段，取不到返回 null。</summary>
        public object Get(string key)
        {
            Dictionary<string, object> dict = Detail as Dictionary<string, object>;
            if (dict == null) return null;
            object v;
            if (dict.TryGetValue(key, out v)) return v;
            return null;
        }

        public double? GetDouble(string key)
        {
            object v = Get(key);
            if (v == null) return null;
            try
            {
                return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool Has(string key)
        {
            Dictionary<string, object> dict = Detail as Dictionary<string, object>;
            return dict != null && dict.ContainsKey(key);
        }

        /// <summary>取一个布尔字段；字段缺失或类型不对时返回 null。</summary>
        public bool? GetBool(string key)
        {
            object v = Get(key);
            if (v == null) return null;
            if (v is bool) return (bool)v;
            try
            {
                return Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// CSP 同伴模式的报文封装。
    /// 单条记录结构：
    ///   TYPE(1B) $tcp_remote_command_protocol_version=1.0 RS $command=NAME RS $serial=N RS $detail=JSON RS NUL
    /// 其中 RS = 0x1E，NUL = 0x00。
    /// </summary>
    public static class CspFraming
    {
        public const byte RS = 0x1E;
        public const byte TERM = 0x00;

        /// <summary>协议里颜色分量用的 uint32 归一化除数。</summary>
        public const double MaxU32 = 4294967295.0;

        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = 8 * 1024 * 1024;
            s.RecursionLimit = 64;
            return s;
        }

        public static object ParseJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return null;
            try
            {
                return Json.DeserializeObject(trimmed);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string SerializeJson(object value)
        {
            return Json.Serialize(value);
        }

        public static byte[] Build(string command, int serial, string detailJson)
        {
            if (detailJson == null) detailJson = "";

            StringBuilder sb = new StringBuilder();
            sb.Append("$tcp_remote_command_protocol_version=1.0");
            sb.Append((char)RS);
            sb.Append("$command=").Append(command);
            sb.Append((char)RS);
            sb.Append("$serial=").Append(serial.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append((char)RS);
            sb.Append("$detail=").Append(detailJson);
            sb.Append((char)RS);

            byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
            byte[] outBuf = new byte[body.Length + 2];
            outBuf[0] = (byte)CspMsgType.Command;
            Array.Copy(body, 0, outBuf, 1, body.Length);
            outBuf[outBuf.Length - 1] = TERM;
            return outBuf;
        }

        /// <summary>
        /// 从缓冲区里取出所有完整记录，并把已消费的字节移除。
        /// 必须这样处理：一次 recv 里可能挤了好几条记录，也可能只有半条。
        /// </summary>
        public static List<CspMessage> Drain(List<byte> buffer)
        {
            List<CspMessage> result = new List<CspMessage>();

            while (true)
            {
                int termIndex = -1;
                for (int i = 0; i < buffer.Count; i++)
                {
                    if (buffer[i] == TERM)
                    {
                        termIndex = i;
                        break;
                    }
                }
                if (termIndex < 0) break;

                // 从终止符往前找最近的一个类型字节，那就是这条记录的起点。
                int start = 0;
                for (int j = termIndex - 1; j >= 0; j--)
                {
                    byte b = buffer[j];
                    if (b == (byte)CspMsgType.Command || b == (byte)CspMsgType.Success || b == (byte)CspMsgType.Error)
                    {
                        start = j;
                        break;
                    }
                }

                byte[] raw = new byte[termIndex - start + 1];
                buffer.CopyTo(start, raw, 0, raw.Length);
                buffer.RemoveRange(0, termIndex + 1);

                CspMessage msg = ParseRecord(raw);
                if (msg != null) result.Add(msg);
            }

            return result;
        }

        private static CspMessage ParseRecord(byte[] raw)
        {
            if (raw.Length < 6) return null;

            CspMessage msg = new CspMessage();
            msg.Type = (CspMsgType)raw[0];

            // 去掉首字节 TYPE 和末字节 NUL
            string body = Encoding.UTF8.GetString(raw, 1, raw.Length - 2);
            string[] parts = body.Split((char)RS);

            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length > 0 && p[0] == '$') p = p.Substring(1);

                int eq = p.IndexOf('=');
                if (eq <= 0) continue;
                string k = p.Substring(0, eq);
                string v = p.Substring(eq + 1);

                if (k == "command")
                {
                    msg.Command = v;
                }
                else if (k == "serial")
                {
                    int serial;
                    if (int.TryParse(v, out serial)) msg.Serial = serial;
                    else msg.Serial = -1;
                }
                else if (k == "detail")
                {
                    // detail 后面还跟着一个 RS，split 之后通常已经干净，
                    // 但保险起见把残留的分隔符去掉。
                    msg.RawDetail = v.TrimEnd((char)RS).TrimEnd('\0');
                }
            }

            if (msg.Command == null) msg.Command = "";
            return msg;
        }
    }
}