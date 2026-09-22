using System;
using System.Globalization;

namespace CspPalette.ColorLib
{
    /// <summary>
    /// 零依赖颜色空间转换库（仅 C# 5 语法，仅用 .NET Framework 基础类库）。
    /// 支持：sRGB(0..255) &lt;-&gt; OKLCH / CIELAB(D65) / HSV，
    /// 以及线性 sRGB 色域判断与十六进制互转。
    /// 供基于 OKLCH / CIELAB 的取色器使用（滑块渐变、色域灰化、CSP 回读定位）。
    /// </summary>
    public static class ColorMath
    {
        // ==================== 常量 ====================

        /// <summary>
        /// 色域判断的相对容差：允许线性 sRGB 略微越界，避免 8bit 边界值抖动。
        /// 实测 8bit 颜色绕一圈回来误差不超过 2e-7（相对量级），这里留 50 倍余量。
        /// 必须相对——见 InGamut 里的说明。
        /// </summary>
        private const double GamutEpsilon = 1e-5;

        // sRGB 传递函数（gamma）分段阈值
        private const double SrgbDecodeThreshold = 0.04045;   // 解码：字节/255 侧
        private const double SrgbEncodeThreshold = 0.0031308; // 编码：线性光侧

        // CIE Lab 的 f(t) 分段常数
        private const double CieThreshold = 0.008856451679035631; // (6/29)^3
        private const double CieSlope = 7.787037037037037;         // 3*(29/6)^2 的倒数
        private const double CieOffset = 0.13793103448275862;      // 4/29

        // D65 白点（Photoshop Lab 习惯）
        private const double WhiteXn = 0.95047;
        private const double WhiteYn = 1.0;
        private const double WhiteZn = 1.08883;

        // chroma 小于该值时视为灰色，色相无意义，固定输出 0（避免 NaN / 抖动）
        private const double HueEpsilon = 1e-6;

        // ==================== 公开 API：OKLCH ====================

        /// <summary>
        /// sRGB 字节 -&gt; OKLCH。L: 0..1，C: 0..约0.4，H: 0..360。
        /// C 接近 0 时为灰色，H 稳定输出 0，不产生 NaN。
        /// </summary>
        public static void RgbToOklch(byte r, byte g, byte b, out double L, out double C, out double H)
        {
            // 先把 8bit sRGB 做 gamma 解码到线性光
            double lr = SrgbToLinear(r / 255.0);
            double lg = SrgbToLinear(g / 255.0);
            double lb = SrgbToLinear(b / 255.0);

            double a;
            double bb;
            LinearRgbToOklab(lr, lg, lb, out L, out a, out bb);

            // 直角坐标 (a,b) -> 极坐标 (C,H)
            C = Math.Sqrt(a * a + bb * bb);
            if (C < HueEpsilon)
            {
                H = 0.0; // 灰色：色相无意义，给稳定值
            }
            else
            {
                H = Math.Atan2(bb, a) * 180.0 / Math.PI;
                if (H < 0.0) H += 360.0;
                if (H >= 360.0) H -= 360.0;
            }
        }

        /// <summary>
        /// OKLCH -&gt; sRGB（gamma 编码后）。结果可能超出 0..1（色域外），调用方自行判断/钳制。
        /// </summary>
        public static void OklchToRgb(double L, double C, double H, out double r, out double g, out double b)
        {
            double lr;
            double lg;
            double lb;
            OklchToLinearRgb(L, C, H, out lr, out lg, out lb);

            // 线性光 -> gamma 编码（越界值保持符号，不做钳制）
            r = LinearToSrgb(lr);
            g = LinearToSrgb(lg);
            b = LinearToSrgb(lb);
        }

        /// <summary>
        /// OKLCH -&gt; 线性 sRGB。0..1 之外表示超出色域；色域判断和渐变生成用它更准。
        /// </summary>
        public static void OklchToLinearRgb(double L, double C, double H, out double r, out double g, out double b)
        {
            // 极坐标 (C,H) -> 直角坐标 (a,b)
            double hr = H * Math.PI / 180.0;
            double a = C * Math.Cos(hr);
            double bb = C * Math.Sin(hr);
            OklabToLinearRgb(L, a, bb, out r, out g, out b);
        }

        // ==================== 公开 API：OKLab ====================

        /// <summary>
        /// sRGB 字节 -&gt; OKLab。L: 0..1，a/b: 约 -0.4..0.4。
        /// OKLCH 就是它的极坐标形式（C = √(a²+b²)、H = atan2(b, a)），
        /// 这里单独开出来是给"OKLab"那个色彩空间用的（滑条直接调 a/b）。
        /// </summary>
        public static void RgbToOklab(byte r, byte g, byte b, out double L, out double a, out double bb)
        {
            double lr = SrgbToLinear(r / 255.0);
            double lg = SrgbToLinear(g / 255.0);
            double lb = SrgbToLinear(b / 255.0);
            LinearRgbToOklab(lr, lg, lb, out L, out a, out bb);
        }

        /// <summary>OKLab -&gt; sRGB（gamma 编码后，可能越界）。</summary>
        public static void OklabToRgb(double L, double a, double b, out double r, out double g, out double bb)
        {
            double lr;
            double lg;
            double lb;
            OklabToLinearRgb(L, a, b, out lr, out lg, out lb);

            r = LinearToSrgb(lr);
            g = LinearToSrgb(lg);
            bb = LinearToSrgb(lb);
        }

        // ==================== 公开 API：CIELAB (D65) ====================

        /// <summary>sRGB 字节 -&gt; CIELAB(D65)。L: 0..100，a/b: 约 -128..127。</summary>
        public static void RgbToLab(byte r, byte g, byte b, out double L, out double a, out double bb)
        {
            double lr = SrgbToLinear(r / 255.0);
            double lg = SrgbToLinear(g / 255.0);
            double lb = SrgbToLinear(b / 255.0);

            // 线性 sRGB -> CIE XYZ (D65)
            double X = 0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb;
            double Y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb;
            double Z = 0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb;

            // 白点归一化 + 非线性压缩
            double fx = CieF(X / WhiteXn);
            double fy = CieF(Y / WhiteYn);
            double fz = CieF(Z / WhiteZn);

            L = 116.0 * fy - 16.0;
            a = 500.0 * (fx - fy);
            bb = 200.0 * (fy - fz);
        }

        /// <summary>CIELAB(D65) -&gt; sRGB（gamma 编码后，可能越界）。</summary>
        public static void LabToRgb(double L, double a, double b, out double r, out double g, out double bb)
        {
            double lr;
            double lg;
            double lb;
            LabToLinearRgb(L, a, b, out lr, out lg, out lb);

            r = LinearToSrgb(lr);
            g = LinearToSrgb(lg);
            bb = LinearToSrgb(lb);
        }

        /// <summary>CIELAB(D65) -&gt; 线性 sRGB（0..1 之外表示超出色域）。</summary>
        public static void LabToLinearRgb(double L, double a, double b, out double r, out double g, out double bb)
        {
            // Lab -> 归一化 XYZ
            double fy = (L + 16.0) / 116.0;
            double fx = fy + a / 500.0;
            double fz = fy - b / 200.0;

            double X = WhiteXn * CieFInv(fx);
            double Y = WhiteYn * CieFInv(fy);
            double Z = WhiteZn * CieFInv(fz);

            // CIE XYZ (D65) -> 线性 sRGB
            r = 3.2404542 * X - 1.5371385 * Y - 0.4985314 * Z;
            g = -0.9692660 * X + 1.8760108 * Y + 0.0415560 * Z;
            bb = 0.0556434 * X - 0.2040259 * Y + 1.0572252 * Z;
        }

        // ==================== 公开 API：HSV ====================

        /// <summary>sRGB 字节 -&gt; HSV。h: 0..360，s: 0..1，v: 0..1。</summary>
        public static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;

            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double d = max - min;

            v = max;
            s = (max <= 0.0) ? 0.0 : d / max; // 防 max=0 除零（纯黑）

            if (d <= 0.0)
            {
                h = 0.0; // 灰色无有效色相
                return;
            }

            if (max == rd)
            {
                h = 60.0 * (((gd - bd) / d) % 6.0);
            }
            else if (max == gd)
            {
                h = 60.0 * ((bd - rd) / d + 2.0);
            }
            else
            {
                h = 60.0 * ((rd - gd) / d + 4.0);
            }

            if (h < 0.0) h += 360.0;
            if (h >= 360.0) h -= 360.0;
        }

        /// <summary>HSV -&gt; sRGB 字节。h: 0..360，s/v: 0..1。</summary>
        public static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            // 色相归一化到 [0,360)，防负数与越界
            h = ((h % 360.0) + 360.0) % 360.0;
            s = Clamp01(s);
            v = Clamp01(v);

            double c = v * s;
            double hp = h / 60.0;
            double x = c * (1.0 - Math.Abs((hp % 2.0) - 1.0));
            double m = v - c;

            double rd = 0.0;
            double gd = 0.0;
            double bd = 0.0;
            if (hp < 1.0) { rd = c; gd = x; }
            else if (hp < 2.0) { rd = x; gd = c; }
            else if (hp < 3.0) { gd = c; bd = x; }
            else if (hp < 4.0) { gd = x; bd = c; }
            else if (hp < 5.0) { rd = x; bd = c; }
            else { rd = c; bd = x; }

            r = ByteFromUnit(rd + m);
            g = ByteFromUnit(gd + m);
            b = ByteFromUnit(bd + m);
        }

        // ==================== 公开 API：VHSV（SAI 2 Mode 0） ====================

        /// <summary>
        /// VHSV -&gt; sRGB 字节。h: 0..360，s/v: 0..1。照 PaintTool SAI 2 的 Mode 0 复刻
        /// （colorink 的 vhsv_to_rgb）。
        ///
        /// 和标准 HSV 的差别**只在饱和度怎么参与运算**：
        /// 标准 HSV 减掉的是 V·S，VHSV 减掉的是 adj_s = S + (V-1)·0.5·S²。
        /// 在 V=1 时两者相等；V 越低 adj_s 相对 V·S 越大，于是"减得更多"、颜色更纯——
        /// 也就是 SAI 那个手感：把明度拉暗，色相不会被冲淡成灰。
        /// 六个扇区的插值骨架和 HSV 完全一样，只是把 S 换成 adj_s。
        /// </summary>
        public static void VhsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            h = ((h % 360.0) + 360.0) % 360.0;
            s = Clamp01(s);
            v = Clamp01(v);

            double adj = s + (v - 1.0) * 0.5 * s * s;

            double hp = h / 60.0;
            int sector = (int)Math.Floor(hp);
            if (sector > 5) sector = 5;
            if (sector < 0) sector = 0;
            double f = hp - sector;

            // 六个扇区各由「停在 V 的通道 / 降到最低的通道 / 正在爬升的通道」拼成。
            // 注意爬升有两种写法，**不能合成一个**（照 SAI 那张表的排法）：
            //   up = V - adj + f·adj   从最低爬到 V，f=0 时就是最低
            //   dn = V - f·adj         从 V 降到最低，f=0 时就是 V
            // 合成一个的话 h=60 这种扇区边界会直接跳到错的颜色（绿色而不是黄色）。
            double low = v - adj;                 // 最低的那个通道
            double up = v - adj + f * adj;
            double dn = v - f * adj;
            double rd, gd, bd;
            switch (sector)
            {
                case 0: rd = v; gd = up; bd = low; break;
                case 1: rd = dn; gd = v; bd = low; break;
                case 2: rd = low; gd = v; bd = up; break;
                case 3: rd = low; gd = dn; bd = v; break;
                case 4: rd = up; gd = low; bd = v; break;
                default: rd = v; gd = low; bd = dn; break;
            }

            r = ByteFromUnit(rd);
            g = ByteFromUnit(gd);
            b = ByteFromUnit(bd);
        }

        /// <summary>
        /// sRGB 字节 -&gt; VHSV。h: 0..360，s/v: 0..1。
        /// 从 delta = V - adj_s 反解 adj_s = S + (V-1)·0.5·S² 这个二次方程，
        /// 取 S 较小的那个根（和 colorink 的 rgb_to_vhsv 一致）。
        /// V=1 时退化成标准 HSV 的 S = delta。
        /// </summary>
        public static void RgbToVhsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;

            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double d = max - min;

            v = max;

            if (d <= 0.0)
            {
                h = 0.0; // 灰色无有效色相
                s = 0.0;
                return;
            }

            if (max == rd) h = 60.0 * (((gd - bd) / d) % 6.0);
            else if (max == gd) h = 60.0 * ((bd - rd) / d + 2.0);
            else h = 60.0 * ((rd - gd) / d + 4.0);
            if (h < 0.0) h += 360.0;
            if (h >= 360.0) h -= 360.0;

            // adj_s = delta  →  k·S² + S - delta = 0，其中 k = 0.5·(V-1)
            double k = 0.5 * (v - 1.0);
            if (Math.Abs(k) < 1e-9)
            {
                s = d;   // V=1：和标准 HSV 一样
            }
            else
            {
                double disc = 0.25 + k * d;
                if (disc < 0.0) disc = 0.0;
                s = (Math.Sqrt(disc) - 0.5) / k;
            }
            s = Clamp01(s);
        }

        // ==================== 公开 API：HLS（HSL） ====================

        /// <summary>
        /// sRGB 字节 -&gt; HLS。h: 0..360，l: 0..1，s: 0..1。
        /// 参数顺序按 "HLS" 这个名字来（H、L、S），和 HSV 的 (H,S,V) 不同，别搞混。
        /// 色相公式和 HSV 完全一样，只是明暗轴换成 (max+min)/2。
        /// </summary>
        public static void RgbToHls(byte r, byte g, byte b, out double h, out double l, out double s)
        {
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;

            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double d = max - min;

            l = (max + min) / 2.0;

            if (d <= 0.0)
            {
                h = 0.0; // 灰色无有效色相
                s = 0.0;
                return;
            }

            // 分母 1 - |2l-1| 在 l=0 / l=1 时为 0，但那时必然 d=0，已经在上面返回了
            double denom = 1.0 - Math.Abs(2.0 * l - 1.0);
            s = (denom <= 0.0) ? 0.0 : d / denom;

            if (max == rd)
            {
                h = 60.0 * (((gd - bd) / d) % 6.0);
            }
            else if (max == gd)
            {
                h = 60.0 * ((bd - rd) / d + 2.0);
            }
            else
            {
                h = 60.0 * ((rd - gd) / d + 4.0);
            }

            if (h < 0.0) h += 360.0;
            if (h >= 360.0) h -= 360.0;
        }

        /// <summary>HLS -&gt; sRGB 字节。h: 0..360，l/s: 0..1。</summary>
        public static void HlsToRgb(double h, double l, double s, out byte r, out byte g, out byte b)
        {
            h = ((h % 360.0) + 360.0) % 360.0;
            l = Clamp01(l);
            s = Clamp01(s);

            double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
            double hp = h / 60.0;
            double x = c * (1.0 - Math.Abs((hp % 2.0) - 1.0));
            double m = l - c / 2.0;

            double rd = 0.0;
            double gd = 0.0;
            double bd = 0.0;
            if (hp < 1.0) { rd = c; gd = x; }
            else if (hp < 2.0) { rd = x; gd = c; }
            else if (hp < 3.0) { gd = c; bd = x; }
            else if (hp < 4.0) { gd = x; bd = c; }
            else if (hp < 5.0) { rd = x; bd = c; }
            else { rd = c; bd = x; }

            r = ByteFromUnit(rd + m);
            g = ByteFromUnit(gd + m);
            b = ByteFromUnit(bd + m);
        }

        // ==================== 公开 API：工具 ====================

        /// <summary>
        /// 线性 sRGB 是否在色域内。容差按颜色自身的量级走（相对容差）：
        /// 8bit 颜色来回换算的误差只有 2e-7 上下，但**暗部的线性值本身才 1e-3 量级**，
        /// 用固定的 1e-4 绝对容差会把近黑的一大片误判成在色域内——OKLCH 方块的底部
        /// 因此鼓出一块"折角"（真实的色域在 L→0 处是收成一点的）。
        /// </summary>
        public static bool InGamut(double linearR, double linearG, double linearB)
        {
            double scale = Math.Max(Math.Abs(linearR), Math.Max(Math.Abs(linearG), Math.Abs(linearB)));
            double eps = GamutEpsilon * Math.Max(scale, 1e-6);

            // NaN 的任何比较都为 false，会被自然判为越界，符合预期
            return linearR >= -eps && linearR <= 1.0 + eps
                && linearG >= -eps && linearG <= 1.0 + eps
                && linearB >= -eps && linearB <= 1.0 + eps;
        }

        /// <summary>把可能越界的线性 sRGB 钳制到 0..1，gamma 编码后输出 0..255 字节。</summary>
        public static void LinearRgbToBytesClamped(double lr, double lg, double lb, out byte r, out byte g, out byte b)
        {
            r = ByteFromUnit(LinearToSrgb(Clamp01(lr)));
            g = ByteFromUnit(LinearToSrgb(Clamp01(lg)));
            b = ByteFromUnit(LinearToSrgb(Clamp01(lb)));
        }

        /// <summary>0..255 sRGB 字节 -&gt; 十六进制 "#RRGGBB"。</summary>
        public static string ToHex(byte r, byte g, byte b)
        {
            return "#"
                + r.ToString("X2", CultureInfo.InvariantCulture)
                + g.ToString("X2", CultureInfo.InvariantCulture)
                + b.ToString("X2", CultureInfo.InvariantCulture);
        }

        /// <summary>解析 "#RRGGBB" 或 "RRGGBB"（允许首尾空白），失败返回 false，不抛异常。</summary>
        public static bool TryParseHex(string s, out byte r, out byte g, out byte b)
        {
            r = 0;
            g = 0;
            b = 0;

            if (s == null) return false;

            string t = s.Trim();
            if (t.Length > 0 && t[0] == '#') t = t.Substring(1);
            if (t.Length != 6) return false;

            byte rr;
            byte gg;
            byte bb;
            if (!byte.TryParse(t.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rr)) return false;
            if (!byte.TryParse(t.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out gg)) return false;
            if (!byte.TryParse(t.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bb)) return false;

            r = rr;
            g = gg;
            b = bb;
            return true;
        }

        // ==================== 内部：OKLab 核心（Björn Ottosson 标准矩阵） ====================

        /// <summary>线性 sRGB -> OKLab。</summary>
        private static void LinearRgbToOklab(double r, double g, double b, out double L, out double a, out double bb)
        {
            // 线性 sRGB -> LMS（长/中/短锥体响应）
            double l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
            double m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
            double s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

            // 非线性压缩：立方根
            double l_ = Cbrt(l);
            double m_ = Cbrt(m);
            double s_ = Cbrt(s);

            // LMS' -> OKLab
            L = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_;
            a = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_;
            bb = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_;
        }

        /// <summary>OKLab -&gt; 线性 sRGB（0..1 之外表示超出色域）。</summary>
        public static void OklabToLinearRgb(double L, double a, double bb, out double r, out double g, out double b)
        {
            // OKLab -> LMS'（逆矩阵）
            double l_ = L + 0.3963377774 * a + 0.2158037573 * bb;
            double m_ = L - 0.1055613458 * a - 0.0638541728 * bb;
            double s_ = L - 0.0894841775 * a - 1.2914855480 * bb;

            // 逆非线性
            double l = l_ * l_ * l_;
            double m = m_ * m_ * m_;
            double s = s_ * s_ * s_;

            // LMS -> 线性 sRGB（逆矩阵）
            r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
            g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
            b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        }

        // ==================== 内部：sRGB gamma 与 CIE f() 辅助 ====================

        /// <summary>.NET 4.8 无 Math.Cbrt，手写立方根（兼容负数）。</summary>
        private static double Cbrt(double x)
        {
            if (x < 0.0) return -Math.Pow(-x, 1.0 / 3.0);
            return Math.Pow(x, 1.0 / 3.0);
        }

        /// <summary>sRGB 分量(0..1) -> 线性光。</summary>
        private static double SrgbToLinear(double c)
        {
            if (c <= SrgbDecodeThreshold) return c / 12.92;
            double t = (c + 0.055) / 1.055;
            return Math.Pow(t, 2.4);
        }

        /// <summary>线性光 -> sRGB 分量(0..1)，越界值保持符号。</summary>
        private static double LinearToSrgb(double c)
        {
            // 负值（色域外）走线性分支，保持符号，不会产生 NaN
            if (c <= SrgbEncodeThreshold) return c * 12.92;
            return 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
        }

        /// <summary>CIE Lab 的 f(t)。</summary>
        private static double CieF(double t)
        {
            if (t > CieThreshold) return Cbrt(t);
            return CieSlope * t + CieOffset;
        }

        /// <summary>CIE Lab 的 f^-1(t)。</summary>
        private static double CieFInv(double t)
        {
            if (t > 0.20689655172413793) return t * t * t; // 6/29
            return (t - CieOffset) / CieSlope;
        }

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v)) return 0.0;
            if (v < 0.0) return 0.0;
            if (v > 1.0) return 1.0;
            return v;
        }

        private static byte ByteFromUnit(double c)
        {
            double v = Math.Round(Clamp01(c) * 255.0, MidpointRounding.AwayFromZero);
            if (v < 0.0) v = 0.0;
            if (v > 255.0) v = 255.0;
            return (byte)v;
        }
    }
}