using System;
using System.Security.Cryptography;
using System.Text;

namespace CspPalette.Csp
{
    /// <summary>
    /// 从二维码里解出来的 CSP 同伴模式连接信息。
    /// </summary>
    public class CompanionEndpoint
    {
        public string[] Ips;
        public int Port;
        public string Password;
        public string Generation;

        public string FirstReachableIp()
        {
            if (Ips == null || Ips.Length == 0) return null;
            return Ips[0];
        }
    }

    /// <summary>
    /// CSP 同伴模式的密钥与二维码解码。
    /// 这两个密钥是从官方 App 与桌面端通信中逆向得到的固定常量。
    /// </summary>
    public static class CompanionCrypto
    {
        /// <summary>二维码负载用的 7 字节轮转 XOR 密钥。</summary>
        public static readonly byte[] RemoteKey = new byte[] { 0x74, 0xB2, 0x92, 0x5B, 0x4A, 0x21, 0xDA };

        /// <summary>Authenticate 令牌用的 7 字节轮转 XOR 密钥。</summary>
        public static readonly byte[] AuthKey = new byte[] { 0xB6, 0xD5, 0x92, 0xC4, 0xA7, 0x83, 0xE1 };

        /// <summary>
        /// 重新连接时用来代替二维码密码的固定标记串（必须原样包含结尾的 CRLF，共 41 字节）。
        /// </summary>
        public const string ReconnectMarker = "{{(([[reconnection request marker]]))}}\r\n";

        /// <summary>二维码 URL 的固定前缀，用来判断扫到的码是不是我们要的。</summary>
        public const string QrUrlPrefix = "https://companion.clip-studio.com/";

        public static byte[] XorCycle(byte[] data, byte[] key)
        {
            byte[] outBuf = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                outBuf[i] = (byte)(data[i] ^ key[i % key.Length]);
            }
            return outBuf;
        }

        private static readonly char[] HexChars = "0123456789abcdef".ToCharArray();

        public static string ToHex(byte[] bytes)
        {
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = HexChars[bytes[i] >> 4];
                chars[i * 2 + 1] = HexChars[bytes[i] & 0x0F];
            }
            return new string(chars);
        }

        public static byte[] FromHex(string hex)
        {
            if (hex == null) return null;
            if (hex.Length % 2 != 0) return null;
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = HexValue(hex[i * 2]);
                int lo = HexValue(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return null;
                result[i] = (byte)((hi << 4) | lo);
            }
            return result;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>把一个明文串混淆成协议里用的令牌（XOR 后转 hex）。</summary>
        public static string ObfuscateAuth(string plain)
        {
            byte[] raw = Encoding.UTF8.GetBytes(plain);
            return ToHex(XorCycle(raw, AuthKey));
        }

        private static readonly char[] Base64NoPad = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/".ToCharArray();

        /// <summary>生成新的会话密码：6 字节随机数的 base64（去掉 = 填充）。</summary>
        public static string MakeNewPassword()
        {
            byte[] bytes = new byte[6];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return ToBase64NoPadding(bytes);
        }

        private static string ToBase64NoPadding(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i + 3 <= bytes.Length)
            {
                int n = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
                sb.Append(Base64NoPad[(n >> 18) & 63]);
                sb.Append(Base64NoPad[(n >> 12) & 63]);
                sb.Append(Base64NoPad[(n >> 6) & 63]);
                sb.Append(Base64NoPad[n & 63]);
                i += 3;
            }
            int rem = bytes.Length - i;
            if (rem == 1)
            {
                int n = bytes[i] << 16;
                sb.Append(Base64NoPad[(n >> 18) & 63]);
                sb.Append(Base64NoPad[(n >> 12) & 63]);
            }
            else if (rem == 2)
            {
                int n = (bytes[i] << 16) | (bytes[i + 1] << 8);
                sb.Append(Base64NoPad[(n >> 18) & 63]);
                sb.Append(Base64NoPad[(n >> 12) & 63]);
                sb.Append(Base64NoPad[(n >> 6) & 63]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 解析 CSP 二维码里的 URL，取出 ip/port/password/generation。
        /// 失败返回 null。
        /// </summary>
        public static CompanionEndpoint DecodeQrUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            string sValue = ExtractQueryValue(url, "s");
            if (string.IsNullOrEmpty(sValue)) return null;

            byte[] cipher = FromHex(sValue);
            if (cipher == null || cipher.Length == 0) return null;

            byte[] plain;
            try
            {
                plain = XorCycle(cipher, RemoteKey);
            }
            catch (Exception)
            {
                return null;
            }

            string text = Encoding.UTF8.GetString(plain);
            string[] parts = text.Split('\t');
            if (parts.Length < 4) return null;

            int port;
            if (!int.TryParse(parts[1], out port) || port <= 0 || port > 65535) return null;

            CompanionEndpoint ep = new CompanionEndpoint();
            ep.Ips = parts[0].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            ep.Port = port;
            ep.Password = parts[2];
            ep.Generation = parts[3];
            if (ep.Ips.Length == 0) return null;
            return ep;
        }

        /// <summary>从 URL 里取查询参数（不依赖 System.Web）。</summary>
        private static string ExtractQueryValue(string url, string key)
        {
            int q = url.IndexOf('?');
            if (q < 0) return null;
            string query = url.Substring(q + 1);
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] kv = pairs[i].Split(new char[] { '=' }, 2);
                if (kv.Length != 2) continue;
                if (kv[0] == key)
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }
            return null;
        }
    }
}