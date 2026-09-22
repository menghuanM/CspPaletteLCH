// QrDecoder.cs
// 零依赖（仅使用 .NET Framework 自带类库）的 QR 码解码器，C# 5 语法。
// 适用场景：程序自行截屏得到的 QR 图像（基本正对、可能整数倍缩放、轻微旋转 ±10°、四周有留白或紧贴边缘）。
// 全部失败路径统一返回 null，绝不向调用方抛异常。
using System;
using System.Collections.Generic;
using System.Text;

namespace CspPalette.Qr
{
    public static class QrDecoder
    {
        /// <summary>
        /// 解码灰度图中的 QR 码。
        /// gray: 长度 = width*height，行优先，每字节一个像素，0 = 纯黑，255 = 纯白。
        /// 成功返回解码出的文本；失败返回 null（不抛异常）。
        /// </summary>
        public static string Decode(byte[] gray, int width, int height)
        {
            try
            {
                if (gray == null || width <= 0 || height <= 0) return null;
                if ((long)width * (long)height > gray.Length) return null;
                int total = width * height;

                // 第一轮：原图 + 多个候选阈值（正常截图 Otsu 即可命中）
                string r = TryImage(gray, width, height, total);
                if (r != null) return r;

                // 第二轮兜底：轻度 3x3 均值平滑后再试（抗噪 / 抗缩放抗锯齿）
                byte[] blur = BoxBlur3(gray, width, height);
                r = TryImage(blur, width, height, total);
                if (r != null) return r;

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string TryImage(byte[] img, int width, int height, int total)
        {
            int[] thresholds = BuildThresholds(img, total);
            for (int i = 0; i < thresholds.Length; i++)
            {
                bool[] bin = Binarize(img, total, thresholds[i]);
                string s = DecodeBinary(bin, width, height);
                if (s != null) return s;
            }
            return null;
        }

        /// <summary>3x3 均值平滑（边界取最近像素），仅在首轮解码失败时作为兜底。</summary>
        private static byte[] BoxBlur3(byte[] gray, int w, int h)
        {
            byte[] dst = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = y > 0 ? y - 1 : 0;
                int y1 = y < h - 1 ? y + 1 : h - 1;
                for (int x = 0; x < w; x++)
                {
                    int x0 = x > 0 ? x - 1 : 0;
                    int x1 = x < w - 1 ? x + 1 : w - 1;
                    int sum = 0, cnt = 0;
                    for (int yy = y0; yy <= y1; yy++)
                    {
                        int baseIdx = yy * w;
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            sum += gray[baseIdx + xx];
                            cnt++;
                        }
                    }
                    dst[y * w + x] = (byte)(sum / cnt);
                }
            }
            return dst;
        }

        // ==================== 1. 二值化 ====================

        private static int[] BuildThresholds(byte[] gray, int total)
        {
            int[] hist = new int[256];
            long sum = 0;
            int mn = 255, mx = 0;
            for (int i = 0; i < total; i++)
            {
                int v = gray[i];
                hist[v]++;
                sum += v;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            List<int> list = new List<int>();
            AddThreshold(list, Otsu(hist, total));          // 首选 Otsu
            AddThreshold(list, (mn + mx) / 2);              // 高低对比中点
            if (total > 0) AddThreshold(list, (int)(sum / total)); // 均值
            AddThreshold(list, 128);                        // 固定阈值兜底
            return list.ToArray();
        }

        private static void AddThreshold(List<int> list, int t)
        {
            if (t < 1) t = 1;
            if (t > 254) t = 254;
            for (int i = 0; i < list.Count; i++)
            {
                if (Math.Abs(list[i] - t) <= 8) return;
            }
            list.Add(t);
        }

        private static int Otsu(int[] hist, int total)
        {
            long sumAll = 0;
            for (int i = 0; i < 256; i++) sumAll += (long)i * hist[i];
            long sumB = 0, wB = 0;
            double best = -1;
            int bestT = 128;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF <= 0) break;
                sumB += (long)t * hist[t];
                double mB = (double)sumB / wB;
                double mF = (double)(sumAll - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best)
                {
                    best = between;
                    bestT = t;
                }
            }
            return bestT;
        }

        private static bool[] Binarize(byte[] gray, int total, int threshold)
        {
            bool[] bin = new bool[total];
            for (int i = 0; i < total; i++) bin[i] = gray[i] < threshold; // true = 黑
            return bin;
        }

        // ==================== 2. 定位图案（1:1:3:1:1）检测 ====================

        private class Finder
        {
            public double X;
            public double Y;
            public double Module;
            public int Count;
        }

        private class Triple
        {
            public Finder TL;
            public Finder TR;
            public Finder BL;
            public double Score;
        }

        private static List<Finder> FindFinders(bool[] bin, int w, int h)
        {
            List<Finder> raw = new List<Finder>();
            int[] rLen = new int[w + 1];
            int[] rStart = new int[w + 1];
            bool[] rDark = new bool[w + 1];

            for (int y = 0; y < h; y++)
            {
                int nr = RowRuns(bin, w, y, rLen, rStart, rDark);
                for (int i = 0; i + 4 < nr; i++)
                {
                    // 需要「黑-白-黑-白-黑」五段
                    if (!rDark[i] || rDark[i + 1] || !rDark[i + 2] || rDark[i + 3] || !rDark[i + 4]) continue;
                    int total = rLen[i] + rLen[i + 1] + rLen[i + 2] + rLen[i + 3] + rLen[i + 4];
                    if (total < 7) continue;

                    double unit;
                    if (!RatioOk(rLen, i, total, out unit)) continue;

                    double cx = rStart[i + 2] + rLen[i + 2] / 2.0;
                    int moduleV;
                    double cy;
                    if (!CrossCheckVertical(bin, w, h, (int)Math.Round(cx), y, out cy, out moduleV)) continue;
                    // 用垂直方向得到的中心行再做一次水平复核，修正 cx
                    int moduleH;
                    double cx2;
                    if (!CrossCheckHorizontal(bin, w, h, (int)Math.Round(cx), (int)Math.Round(cy), out cx2, out moduleH)) continue;

                    Finder f = new Finder();
                    f.X = cx2;
                    f.Y = cy;
                    f.Module = (moduleV + moduleH) / 2.0;
                    f.Count = 1;
                    raw.Add(f);
                }
            }

            // 聚类合并（同一图案会被多行扫描重复命中）
            List<Finder> merged = new List<Finder>();
            for (int i = 0; i < raw.Count; i++)
            {
                Finder f = raw[i];
                bool hit = false;
                for (int j = 0; j < merged.Count; j++)
                {
                    Finder m = merged[j];
                    double dx = m.X - f.X, dy = m.Y - f.Y;
                    double lim = Math.Max(m.Module, f.Module) * 2.5 + 2.0;
                    if (dx * dx + dy * dy <= lim * lim)
                    {
                        double n = m.Count + 1;
                        m.X = (m.X * m.Count + f.X) / n;
                        m.Y = (m.Y * m.Count + f.Y) / n;
                        m.Module = (m.Module * m.Count + f.Module) / n;
                        m.Count++;
                        hit = true;
                        break;
                    }
                }
                if (!hit) merged.Add(f);
            }

            // 优先保留被多次命中的（更可信），若不足 3 个则放宽
            List<Finder> res = new List<Finder>();
            for (int i = 0; i < merged.Count; i++)
            {
                if (merged[i].Count >= 2) res.Add(merged[i]);
            }
            if (res.Count < 3) res = merged;

            res.Sort(delegate(Finder a, Finder b) { return b.Count.CompareTo(a.Count); });
            if (res.Count > 25) res.RemoveRange(25, res.Count - 25);
            return res;
        }

        /// <summary>把一行像素压缩成游程：颜色、长度、起始列。</summary>
        private static int RowRuns(bool[] bin, int w, int y, int[] len, int[] start, bool[] dark)
        {
            int baseIdx = y * w;
            int n = 0;
            int x = 0;
            while (x < w)
            {
                bool c = bin[baseIdx + x];
                int s = x;
                while (x < w && bin[baseIdx + x] == c) x++;
                if (n < len.Length)
                {
                    len[n] = x - s;
                    start[n] = s;
                    dark[n] = c;
                    n++;
                }
            }
            return n;
        }

        /// <summary>检查游程数组中从 offset 开始的 5 段是否符合 1:1:3:1:1。</summary>
        private static bool RatioOk(int[] len, int offset, int total, out double unit)
        {
            unit = total / 7.0;
            double tol = unit * 0.8;
            double tol3 = unit * 1.8;
            if (Math.Abs(len[offset] - unit) > tol) return false;
            if (Math.Abs(len[offset + 1] - unit) > tol) return false;
            if (Math.Abs(len[offset + 2] - 3 * unit) > tol3) return false;
            if (Math.Abs(len[offset + 3] - unit) > tol) return false;
            if (Math.Abs(len[offset + 4] - unit) > tol) return false;
            return true;
        }

        private static bool RatioOk5(int[] c, out int moduleSize)
        {
            moduleSize = 1;
            int total = c[0] + c[1] + c[2] + c[3] + c[4];
            if (total < 7) return false;
            double unit = total / 7.0;
            double tol = unit * 0.8;
            double tol3 = unit * 1.8;
            if (Math.Abs(c[0] - unit) > tol) return false;
            if (Math.Abs(c[1] - unit) > tol) return false;
            if (Math.Abs(c[2] - 3 * unit) > tol3) return false;
            if (Math.Abs(c[3] - unit) > tol) return false;
            if (Math.Abs(c[4] - unit) > tol) return false;
            int m = (int)Math.Round(unit);
            moduleSize = m < 1 ? 1 : m;
            return true;
        }

        /// <summary>在 (x,y) 所在列上做垂直的 1:1:3:1:1 校验。</summary>
        private static bool CrossCheckVertical(bool[] bin, int w, int h, int x, int y, out double centerY, out int moduleSize)
        {
            centerY = 0;
            moduleSize = 1;
            if (x < 0 || x >= w || y < 0 || y >= h) return false;
            if (!bin[y * w + x]) return false;

            int[] cnt = new int[5];
            int yy = y;
            while (yy >= 0 && bin[yy * w + x]) { cnt[2]++; yy--; }
            int top = yy + 1;
            while (yy >= 0 && !bin[yy * w + x]) { cnt[1]++; yy--; }
            while (yy >= 0 && bin[yy * w + x]) { cnt[0]++; yy--; }
            if (cnt[0] == 0 || cnt[1] == 0) return false;

            yy = y + 1;
            while (yy < h && bin[yy * w + x]) { cnt[2]++; yy++; }
            int bottom = yy - 1;
            while (yy < h && !bin[yy * w + x]) { cnt[3]++; yy++; }
            while (yy < h && bin[yy * w + x]) { cnt[4]++; yy++; }
            if (cnt[3] == 0 || cnt[4] == 0) return false;

            if (!RatioOk5(cnt, out moduleSize)) return false;
            centerY = (top + bottom) / 2.0;
            return true;
        }

        /// <summary>在 (x,y) 所在行上做水平的 1:1:3:1:1 校验。</summary>
        private static bool CrossCheckHorizontal(bool[] bin, int w, int h, int x, int y, out double centerX, out int moduleSize)
        {
            centerX = 0;
            moduleSize = 1;
            if (x < 0 || x >= w || y < 0 || y >= h) return false;
            if (!bin[y * w + x]) return false;

            int[] cnt = new int[5];
            int baseIdx = y * w;
            int xx = x;
            while (xx >= 0 && bin[baseIdx + xx]) { cnt[2]++; xx--; }
            int left = xx + 1;
            while (xx >= 0 && !bin[baseIdx + xx]) { cnt[1]++; xx--; }
            while (xx >= 0 && bin[baseIdx + xx]) { cnt[0]++; xx--; }
            if (cnt[0] == 0 || cnt[1] == 0) return false;

            xx = x + 1;
            while (xx < w && bin[baseIdx + xx]) { cnt[2]++; xx++; }
            int right = xx - 1;
            while (xx < w && !bin[baseIdx + xx]) { cnt[3]++; xx++; }
            while (xx < w && bin[baseIdx + xx]) { cnt[4]++; xx++; }
            if (cnt[3] == 0 || cnt[4] == 0) return false;

            if (!RatioOk5(cnt, out moduleSize)) return false;
            centerX = (left + right) / 2.0;
            return true;
        }

        // ==================== 3. 三个定位图案的组合（直角等腰三角形） ====================

        private static double Dist(Finder a, Finder b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static List<Triple> FindTriples(List<Finder> fs)
        {
            List<Triple> list = new List<Triple>();
            int n = fs.Count;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    for (int k = j + 1; k < n; k++)
                    {
                        Finder a = fs[i], b = fs[j], c = fs[k];
                        double dab = Dist(a, b), dac = Dist(a, c), dbc = Dist(b, c);

                        // 最长边为斜边，其对角顶点即左上角定位图案
                        Finder tl, p, q;
                        double hyp, l1, l2;
                        if (dab >= dac && dab >= dbc) { tl = c; p = a; q = b; hyp = dab; l1 = dac; l2 = dbc; }
                        else if (dac >= dab && dac >= dbc) { tl = b; p = a; q = c; hyp = dac; l1 = dab; l2 = dbc; }
                        else { tl = a; p = b; q = c; hyp = dbc; l1 = dab; l2 = dac; }

                        if (l1 < 10 || l2 < 10) continue;
                        double legMax = Math.Max(l1, l2);
                        double legMin = Math.Min(l1, l2);
                        if (legMax - legMin > 0.35 * legMax) continue;
                        if (Math.Abs(hyp - legMax * 1.41421356) > 0.30 * hyp) continue;

                        double mAvg = (tl.Module + p.Module + q.Module) / 3.0;
                        if (mAvg < 0.5) continue;
                        if (Math.Abs(tl.Module - mAvg) > 0.7 * mAvg) continue;
                        if (Math.Abs(p.Module - mAvg) > 0.7 * mAvg) continue;
                        if (Math.Abs(q.Module - mAvg) > 0.7 * mAvg) continue;

                        // 叉积判方向：>0 为非镜像（图像 y 向下时，正确朝向的叉积为正）
                        double v1x = p.X - tl.X, v1y = p.Y - tl.Y;
                        double v2x = q.X - tl.X, v2y = q.Y - tl.Y;
                        double cross = v1x * v2y - v1y * v2x;
                        Finder tr, bl;
                        if (cross > 0) { tr = p; bl = q; }
                        else { tr = q; bl = p; }

                        Triple t = new Triple();
                        t.TL = tl;
                        t.TR = tr;
                        t.BL = bl;
                        double leg = (l1 + l2) / 2.0;
                        t.Score = Math.Abs(l1 - l2) / legMax
                                + Math.Abs(hyp - leg * 1.41421356) / hyp
                                + Math.Abs(tl.Module - mAvg) / mAvg;
                        list.Add(t);
                    }
                }
            }
            list.Sort(delegate(Triple x, Triple y) { return x.Score.CompareTo(y.Score); });
            return list;
        }

        // ==================== 4. 顶层解码流程 ====================

        private static string DecodeBinary(bool[] bin, int w, int h)
        {
            List<Finder> fs = FindFinders(bin, w, h);
            if (fs.Count < 3) return null;
            List<Triple> triples = FindTriples(fs);
            if (triples.Count == 0) return null;

            int limit = triples.Count < 4 ? triples.Count : 4;
            for (int ti = 0; ti < limit; ti++)
            {
                Triple t = triples[ti];
                double leg = (Dist(t.TL, t.TR) + Dist(t.TL, t.BL)) / 2.0;
                double ms = (t.TL.Module + t.TR.Module + t.BL.Module) / 3.0;
                if (ms < 0.5 || leg < 10) continue;

                double dimEst = leg / ms + 7.0;       // 估算矩阵边长（模块数）
                int[] dims = CandidateDims(dimEst);
                for (int di = 0; di < dims.Length; di++)
                {
                    string s = TryDecodeDim(bin, w, h, t, dims[di], ti == 0);
                    if (s != null) return s;
                }
            }
            return null;
        }

        /// <summary>返回与估算值最接近的 3 个合法边长（21,25,...,97）。</summary>
        private static int[] CandidateDims(double est)
        {
            List<int> all = new List<int>();
            for (int v = 1; v <= 20; v++) all.Add(17 + 4 * v);
            all.Sort(delegate(int a, int b) { return Math.Abs(a - est).CompareTo(Math.Abs(b - est)); });
            return new int[] { all[0], all[1], all[2] };
        }

        private static string TryDecodeDim(bool[] bin, int w, int h, Triple t, int dim, bool allowBrute)
        {
            bool[] mat;
            if (!SampleGrid(bin, w, h, t.TL, t.TR, t.BL, dim, out mat)) return null;

            int level, mask;
            bool fmtOk = ReadFormat(mat, dim, out level, out mask);
            if (fmtOk)
            {
                string s = DecodeMatrix(mat, dim, level, mask);
                if (s != null) return s;
            }
            if (allowBrute)
            {
                // 格式信息读取失败时的兜底：暴力尝试 4 个纠错等级 × 8 个掩码
                for (int lv = 0; lv < 4; lv++)
                {
                    for (int mk = 0; mk < 8; mk++)
                    {
                        if (fmtOk && lv == level && mk == mask) continue;
                        string s = DecodeMatrix(mat, dim, lv, mk);
                        if (s != null) return s;
                    }
                }
            }
            return null;
        }

        // ==================== 5. 仿射采样：模块坐标 -> 像素 ====================

        private static bool SampleGrid(bool[] bin, int w, int h, Finder tl, Finder tr, Finder bl, int dim, out bool[] mat)
        {
            mat = null;
            if (dim < 21 || dim > 97) return false;
            double d = dim - 7.0;
            double e1x = (tr.X - tl.X) / d, e1y = (tr.Y - tl.Y) / d;   // 列方向（每模块）
            double e2x = (bl.X - tl.X) / d, e2y = (bl.Y - tl.Y) / d;   // 行方向（每模块）

            // 用较小的边长决定采样内缩量，避免采到相邻模块
            double m1 = Math.Sqrt(e1x * e1x + e1y * e1y);
            double m2 = Math.Sqrt(e2x * e2x + e2y * e2y);
            double mm = Math.Min(m1, m2);
            double off = mm * 0.22;

            bool[] m = new bool[dim * dim];
            for (int row = 0; row < dim; row++)
            {
                for (int col = 0; col < dim; col++)
                {
                    double u = col + 0.5 - 3.5;
                    double v = row + 0.5 - 3.5;
                    double px = tl.X + u * e1x + v * e2x;
                    double py = tl.Y + u * e1y + v * e2y;

                    // 五点采样取多数，抗噪
                    int dark = 0;
                    int okCnt = 0;
                    for (int s = 0; s < 5; s++)
                    {
                        double ox = 0, oy = 0;
                        if (s == 1) { ox = off; }
                        else if (s == 2) { ox = -off; }
                        else if (s == 3) { oy = off; }
                        else if (s == 4) { oy = -off; }
                        int sx = (int)Math.Round(px + ox);
                        int sy = (int)Math.Round(py + oy);
                        if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                        okCnt++;
                        if (bin[sy * w + sx]) dark++;
                    }
                    if (okCnt == 0) return false;       // 采到图像外：放弃
                    m[row * dim + col] = dark * 2 > okCnt;
                }
            }
            mat = m;
            return true;
        }

        // ==================== 6. 格式信息（BCH(15,5)） ====================

        private static bool MBit(bool[] mat, int dim, int x, int y)
        {
            return mat[y * dim + x];
        }

        /// <summary>由 5 位数据算 15 位格式码字（含 BCH 校验），最后异或掩码 0x5412。</summary>
        private static int FormatCodeword(int data)
        {
            int poly = data << 10;
            for (int i = 14; i >= 10; i--)
            {
                if (((poly >> i) & 1) != 0) poly ^= 0x537 << (i - 10);
            }
            int cw = (data << 10) | (poly & 0x3FF);
            return cw ^ 0x5412;   // 注意：异或必须作用于整个 15 位码字
        }

        private static bool ReadFormat(bool[] mat, int dim, out int level, out int mask)
        {
            level = -1;
            mask = -1;

            // 副本 1：左上（跳过 x=6 的时序图案）
            int f1 = 0;
            for (int i = 0; i < 6; i++) f1 = (f1 << 1) | (MBit(mat, dim, i, 8) ? 1 : 0);
            f1 = (f1 << 1) | (MBit(mat, dim, 7, 8) ? 1 : 0);
            f1 = (f1 << 1) | (MBit(mat, dim, 8, 8) ? 1 : 0);
            f1 = (f1 << 1) | (MBit(mat, dim, 8, 7) ? 1 : 0);
            for (int j = 5; j >= 0; j--) f1 = (f1 << 1) | (MBit(mat, dim, 8, j) ? 1 : 0);

            // 副本 2：右上（第 8 行）+ 左下（第 8 列）
            int f2 = 0;
            for (int j = dim - 1; j >= dim - 7; j--) f2 = (f2 << 1) | (MBit(mat, dim, 8, j) ? 1 : 0);
            for (int i = dim - 8; i < dim; i++) f2 = (f2 << 1) | (MBit(mat, dim, i, 8) ? 1 : 0);

            int bestDist = 99;
            int bestData = -1;
            for (int data = 0; data < 32; data++)
            {
                int cw = FormatCodeword(data);
                int dist = PopCount(f1 ^ cw);
                int d2 = PopCount(f2 ^ cw);
                if (d2 < dist) dist = d2;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestData = data;
                }
            }
            if (bestData < 0 || bestDist > 3) return false;

            int ec = bestData >> 3;   // 2 位纠错等级指示：0->M 1->L 2->H 3->Q
            mask = bestData & 7;
            if (ec == 0) level = 1;
            else if (ec == 1) level = 0;
            else if (ec == 2) level = 3;
            else level = 2;
            return true;
        }

        private static int PopCount(int v)
        {
            int c = 0;
            while (v != 0)
            {
                c += v & 1;
                v >>= 1;
            }
            return c;
        }

        // ==================== 7. 去掩码 + zigzag 读码字 ====================

        private static bool MaskBit(int mask, int i, int j)
        {
            switch (mask)
            {
                case 0: return ((i + j) & 1) == 0;
                case 1: return (i & 1) == 0;
                case 2: return j % 3 == 0;
                case 3: return (i + j) % 3 == 0;
                case 4: return ((i / 2) + (j / 3)) % 2 == 0;
                case 5: return (i * j) % 2 + (i * j) % 3 == 0;
                case 6: return ((i * j) % 2 + (i * j) % 3) % 2 == 0;
                case 7: return ((i + j) % 2 + (i * j) % 3) % 2 == 0;
                default: return false;
            }
        }

        private static byte[] ReadCodewords(bool[] mat, int dim, bool[] fn, int mask)
        {
            List<byte> result = new List<byte>();
            int cur = 0, bits = 0;
            bool up = true;
            for (int j = dim - 1; j > 0; j -= 2)
            {
                if (j == 6) j--;   // 跳过竖直时序图案所在列
                for (int count = 0; count < dim; count++)
                {
                    int i = up ? dim - 1 - count : count;
                    for (int col = 0; col < 2; col++)
                    {
                        int x = j - col;
                        if (fn[i * dim + x]) continue;   // 功能模块不参与
                        bool bit = mat[i * dim + x];
                        if (MaskBit(mask, i, x)) bit = !bit;
                        cur = (cur << 1) | (bit ? 1 : 0);
                        bits++;
                        if (bits == 8)
                        {
                            result.Add((byte)cur);
                            cur = 0;
                            bits = 0;
                        }
                    }
                }
                up = !up;
            }
            return result.ToArray();
        }

        // ==================== 8. 功能模块地图 / 对齐图案 ====================

        private static void SetRegion(bool[] m, int dim, int left, int top, int rw, int rh)
        {
            for (int y = top; y < top + rh; y++)
            {
                for (int x = left; x < left + rw; x++)
                {
                    if (x >= 0 && y >= 0 && x < dim && y < dim) m[y * dim + x] = true;
                }
            }
        }

        private static int[] AlignmentCenters(int version)
        {
            if (version == 1) return new int[0];
            int dim = 17 + 4 * version;
            int num = version / 7 + 2;
            int step = (version == 32) ? 26 : ((version * 4 + num * 2 + 1) / (num * 2 - 2) * 2);
            int[] res = new int[num];
            res[0] = 6;
            int pos = dim - 7;
            for (int i = num - 1; i >= 1; i--)
            {
                res[i] = pos;
                pos -= step;
            }
            return res;
        }

        private static bool[] BuildFunctionPattern(int version)
        {
            int dim = 17 + 4 * version;
            bool[] m = new bool[dim * dim];

            // 三个定位图案 + 分隔符（9x9 / 8x9 / 9x8 同时覆盖了格式信息区与黑模块）
            SetRegion(m, dim, 0, 0, 9, 9);
            SetRegion(m, dim, dim - 8, 0, 8, 9);
            SetRegion(m, dim, 0, dim - 8, 9, 8);

            // 对齐图案
            int[] ac = AlignmentCenters(version);
            for (int i = 0; i < ac.Length; i++)
            {
                for (int j = 0; j < ac.Length; j++)
                {
                    if ((ac[i] == 6 && ac[j] == 6) ||
                        (ac[i] == 6 && ac[j] == dim - 7) ||
                        (ac[i] == dim - 7 && ac[j] == 6)) continue;
                    SetRegion(m, dim, ac[i] - 2, ac[j] - 2, 5, 5);
                }
            }

            // 时序图案
            SetRegion(m, dim, 6, 9, 1, dim - 17);
            SetRegion(m, dim, 9, 6, dim - 17, 1);

            // 版本信息（版本 >= 7）
            if (version > 6)
            {
                SetRegion(m, dim, dim - 11, 0, 3, 6);
                SetRegion(m, dim, 0, dim - 11, 6, 3);
            }
            return m;
        }

        // ==================== 9. 分块结构表（版本 1..20，顺序 L,M,Q,H） ====================
        // 每项 = { 每块纠错码字数, 组1块数, 组1每块数据码字, 组2块数, 组2每块数据码字 }
        private static readonly int[][][] EcBlocks = new int[][][]
        {
            null,
            new int[][] { new int[]{7,1,19,0,0},   new int[]{10,1,16,0,0},  new int[]{13,1,13,0,0},  new int[]{17,1,9,0,0} },      // v1
            new int[][] { new int[]{10,1,34,0,0},  new int[]{16,1,28,0,0},  new int[]{22,1,22,0,0},  new int[]{28,1,16,0,0} },     // v2
            new int[][] { new int[]{15,1,55,0,0},  new int[]{26,1,44,0,0},  new int[]{18,2,17,0,0},  new int[]{22,2,13,0,0} },     // v3
            new int[][] { new int[]{20,1,80,0,0},  new int[]{18,2,32,0,0},  new int[]{26,2,24,0,0},  new int[]{16,4,9,0,0} },      // v4
            new int[][] { new int[]{26,1,108,0,0}, new int[]{24,2,43,0,0},  new int[]{18,2,15,2,16}, new int[]{22,2,11,2,12} },     // v5
            new int[][] { new int[]{18,2,68,0,0},  new int[]{16,4,27,0,0},  new int[]{24,4,19,0,0},  new int[]{28,4,15,0,0} },     // v6
            new int[][] { new int[]{20,2,78,0,0},  new int[]{18,4,31,0,0},  new int[]{18,2,14,4,15}, new int[]{26,4,13,1,14} },     // v7
            new int[][] { new int[]{24,2,97,0,0},  new int[]{22,2,38,2,39}, new int[]{22,4,18,2,19}, new int[]{26,4,14,2,15} },     // v8
            new int[][] { new int[]{30,2,116,0,0}, new int[]{22,3,36,2,37}, new int[]{20,4,16,4,17}, new int[]{24,4,12,4,13} },     // v9
            new int[][] { new int[]{18,2,68,2,69}, new int[]{26,4,43,1,44}, new int[]{24,6,19,2,20}, new int[]{28,6,15,2,16} },     // v10
            new int[][] { new int[]{20,4,81,0,0},  new int[]{30,1,50,4,51}, new int[]{28,4,22,4,23}, new int[]{24,3,12,8,13} },     // v11
            new int[][] { new int[]{24,2,92,2,93}, new int[]{22,6,36,2,37}, new int[]{26,4,20,6,21}, new int[]{28,7,14,4,15} },     // v12
            new int[][] { new int[]{26,4,107,0,0}, new int[]{22,8,37,1,38}, new int[]{24,8,20,4,21}, new int[]{22,12,11,4,12} },    // v13
            new int[][] { new int[]{30,3,115,1,116}, new int[]{24,4,40,5,41}, new int[]{20,11,16,5,17}, new int[]{24,11,12,5,13} }, // v14
            new int[][] { new int[]{22,5,87,1,88}, new int[]{24,5,41,5,42}, new int[]{30,5,24,7,25}, new int[]{24,11,12,7,13} },    // v15
            new int[][] { new int[]{24,5,98,1,99}, new int[]{28,7,45,3,46}, new int[]{24,15,19,2,20}, new int[]{30,3,15,13,16} },   // v16
            new int[][] { new int[]{28,1,107,5,108}, new int[]{28,10,46,1,47}, new int[]{28,1,22,15,23}, new int[]{28,2,14,17,15} },// v17
            new int[][] { new int[]{30,5,120,1,121}, new int[]{26,9,43,4,44}, new int[]{28,17,22,1,23}, new int[]{28,2,14,19,15} },// v18
            new int[][] { new int[]{28,3,113,4,114}, new int[]{26,3,44,11,45}, new int[]{26,17,21,4,22}, new int[]{26,9,13,16,14} },// v19
            new int[][] { new int[]{28,3,107,5,108}, new int[]{26,3,41,13,42}, new int[]{30,15,24,5,25}, new int[]{28,15,15,10,16} } // v20
        };

        // ==================== 10. 单块解码（去交织 + RS 纠错 + 解析） ====================

        private static string DecodeMatrix(bool[] mat, int dim, int level, int mask)
        {
            int version = (dim - 17) / 4;
            if (version < 1 || version > 20) return null;
            if (dim != 17 + 4 * version) return null;

            bool[] fn = BuildFunctionPattern(version);
            byte[] raw = ReadCodewords(mat, dim, fn, mask);
            if (raw.Length == 0) return null;

            int[] b = EcBlocks[version][level];
            int ecPer = b[0], n1 = b[1], d1 = b[2], n2 = b[3], d2 = b[4];
            int totalBlocks = n1 + n2;
            int dataTotal = n1 * d1 + n2 * d2;
            int totalCw = dataTotal + ecPer * totalBlocks;
            if (raw.Length != totalCw) return null;

            byte[] data = DeinterleaveAndCorrect(raw, ecPer, n1, d1, n2, d2);
            if (data == null) return null;

            return ParseBits(data, version);
        }

        private static byte[] DeinterleaveAndCorrect(byte[] raw, int ecPer, int n1, int d1, int n2, int d2)
        {
            int totalBlocks = n1 + n2;
            int[] dLen = new int[totalBlocks];
            for (int i = 0; i < n1; i++) dLen[i] = d1;
            for (int i = 0; i < n2; i++) dLen[n1 + i] = d2;

            int[][] dataBytes = new int[totalBlocks][];
            int[][] ecBytes = new int[totalBlocks][];
            for (int i = 0; i < totalBlocks; i++)
            {
                dataBytes[i] = new int[dLen[i]];
                ecBytes[i] = new int[ecPer];
            }

            int idx = 0;
            int maxD = Math.Max(n2 > 0 ? d2 : 0, d1);
            for (int i = 0; i < maxD; i++)
            {
                for (int blk = 0; blk < totalBlocks; blk++)
                {
                    if (i < dLen[blk])
                    {
                        if (idx >= raw.Length) return null;
                        dataBytes[blk][i] = raw[idx++];
                    }
                }
            }
            for (int i = 0; i < ecPer; i++)
            {
                for (int blk = 0; blk < totalBlocks; blk++)
                {
                    if (idx >= raw.Length) return null;
                    ecBytes[blk][i] = raw[idx++];
                }
            }
            if (idx != raw.Length) return null;

            List<byte> outp = new List<byte>();
            for (int blk = 0; blk < totalBlocks; blk++)
            {
                int dl = dLen[blk];
                byte[] buf = new byte[dl + ecPer];
                for (int i = 0; i < dl; i++) buf[i] = (byte)dataBytes[blk][i];
                for (int i = 0; i < ecPer; i++) buf[dl + i] = (byte)ecBytes[blk][i];
                if (!ReedSolomon.Decode(buf, ecPer)) return null;   // RS 真纠错
                for (int i = 0; i < dl; i++) outp.Add(buf[i]);
            }
            return outp.ToArray();
        }

        // ==================== 11. 比特流解析 ====================

        private const string AlnumTable = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

        private static int ReadBits(byte[] d, int pos, int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                int p = pos + i;
                int bit = (d[p >> 3] >> (7 - (p & 7))) & 1;
                v = (v << 1) | bit;
            }
            return v;
        }

        private static int CountBits(int mode, int version)
        {
            if (mode == 1) return version <= 9 ? 10 : 12;   // 数字
            if (mode == 2) return version <= 9 ? 9 : 11;    // 字母数字
            if (mode == 4) return version <= 9 ? 8 : 16;    // 字节
            return 0;
        }

        private static string ParseBits(byte[] data, int version)
        {
            int bitLen = data.Length * 8;
            StringBuilder sb = new StringBuilder();
            int pos = 0;

            while (pos + 4 <= bitLen)
            {
                int mode = ReadBits(data, pos, 4);
                pos += 4;
                if (mode == 0) break;   // 终止符

                if (mode == 1)          // 数字
                {
                    int cb = version <= 9 ? 10 : (version <= 26 ? 12 : 14);
                    if (pos + cb > bitLen) return null;
                    int cnt = ReadBits(data, pos, cb);
                    pos += cb;
                    while (cnt >= 3)
                    {
                        if (pos + 10 > bitLen) return null;
                        int v = ReadBits(data, pos, 10);
                        pos += 10;
                        if (v > 999) return null;
                        sb.Append((char)('0' + v / 100));
                        sb.Append((char)('0' + (v / 10) % 10));
                        sb.Append((char)('0' + v % 10));
                        cnt -= 3;
                    }
                    if (cnt == 2)
                    {
                        if (pos + 7 > bitLen) return null;
                        int v = ReadBits(data, pos, 7);
                        pos += 7;
                        if (v > 99) return null;
                        sb.Append((char)('0' + v / 10));
                        sb.Append((char)('0' + v % 10));
                    }
                    else if (cnt == 1)
                    {
                        if (pos + 4 > bitLen) return null;
                        int v = ReadBits(data, pos, 4);
                        pos += 4;
                        if (v > 9) return null;
                        sb.Append((char)('0' + v));
                    }
                }
                else if (mode == 2)     // 字母数字
                {
                    int cb = version <= 9 ? 9 : (version <= 26 ? 11 : 13);
                    if (pos + cb > bitLen) return null;
                    int cnt = ReadBits(data, pos, cb);
                    pos += cb;
                    while (cnt >= 2)
                    {
                        if (pos + 11 > bitLen) return null;
                        int v = ReadBits(data, pos, 11);
                        pos += 11;
                        if (v >= 45 * 45) return null;
                        sb.Append(AlnumTable[v / 45]);
                        sb.Append(AlnumTable[v % 45]);
                        cnt -= 2;
                    }
                    if (cnt == 1)
                    {
                        if (pos + 6 > bitLen) return null;
                        int v = ReadBits(data, pos, 6);
                        pos += 6;
                        if (v >= 45) return null;
                        sb.Append(AlnumTable[v]);
                    }
                }
                else if (mode == 4)     // 字节
                {
                    int cb = version <= 9 ? 8 : (version <= 26 ? 16 : 16);
                    if (pos + cb > bitLen) return null;
                    int cnt = ReadBits(data, pos, cb);
                    pos += cb;
                    if (cnt < 0 || (long)pos + (long)cnt * 8 > bitLen) return null;
                    byte[] bs = new byte[cnt];
                    for (int i = 0; i < cnt; i++)
                    {
                        bs[i] = (byte)ReadBits(data, pos, 8);
                        pos += 8;
                    }
                    sb.Append(DecodeBytes(bs));
                }
                else if (mode == 7)     // ECI：跳过指示符后继续（本工具场景下几乎不会出现）
                {
                    if (pos + 8 > bitLen) return null;
                    int b0 = ReadBits(data, pos, 8);
                    pos += 8;
                    if ((b0 & 0x80) == 0) { /* 单字节 */ }
                    else if ((b0 & 0xC0) == 0x80) pos += 8;
                    else if ((b0 & 0xE0) == 0xC0) pos += 16;
                    else return null;
                    if (pos > bitLen) return null;
                }
                else
                {
                    return null;        // 汉字 / 结构链接 / FNC1 等一律放弃
                }
            }
            if (sb.Length == 0) return null;
            return sb.ToString();
        }

        private static string DecodeBytes(byte[] bs)
        {
            bool ascii = true;
            for (int i = 0; i < bs.Length; i++)
            {
                if (bs[i] >= 0x80) { ascii = false; break; }
            }
            if (!ascii)
            {
                try
                {
                    return new UTF8Encoding(false, true).GetString(bs);
                }
                catch (Exception)
                {
                    // 非法 UTF-8：退化为逐字节映射（Latin-1 语义）
                }
            }
            char[] cs = new char[bs.Length];
            for (int i = 0; i < bs.Length; i++) cs[i] = (char)bs[i];
            return new string(cs);
        }
    }

    // ==================== Reed-Solomon over GF(256)，本原多项式 0x11D ====================
    internal static class RsGf
    {
        public static readonly int[] Exp = new int[512];
        public static readonly int[] Log = new int[256];

        static RsGf()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                Exp[i] = x;
                Log[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }

        public static int Mul(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return Exp[Log[a] + Log[b]];
        }

        public static int Inv(int a)
        {
            return Exp[255 - Log[a]];
        }
    }

    internal static class ReedSolomon
    {
        /// <summary>
        /// 对 recv（前段数据码字 + 后段纠错码字）做 RS 纠错，就地修改 recv。
        /// 纠正失败（错误数超过 t/2）返回 false。
        /// </summary>
        public static bool Decode(byte[] recv, int ecLen)
        {
            if (ecLen <= 0) return true;
            int n = recv.Length;
            int t = ecLen;

            // 1) 计算伴随式 S_i = r(alpha^i)，i = 0..t-1
            int[] syn = new int[t];
            bool anyErr = false;
            for (int i = 0; i < t; i++)
            {
                int xi = RsGf.Exp[i];
                int s = 0;
                for (int j = 0; j < n; j++) s = RsGf.Mul(s, xi) ^ recv[j];
                syn[i] = s;
                if (s != 0) anyErr = true;
            }
            if (!anyErr) return true;   // 无错误

            // 2) Berlekamp-Massey 求错误定位多项式 sigma
            int[] C = new int[t + 1];
            int[] B = new int[t + 1];
            C[0] = 1;
            B[0] = 1;
            int L = 0, m = 1, b = 1;
            for (int k = 0; k < t; k++)
            {
                int d = syn[k];
                for (int i = 1; i <= L; i++)
                {
                    if (k - i >= 0) d ^= RsGf.Mul(C[i], syn[k - i]);
                }
                if (d == 0)
                {
                    m++;
                    continue;
                }
                if (2 * L <= k)
                {
                    int[] T = (int[])C.Clone();
                    int coef = RsGf.Mul(d, RsGf.Inv(b));
                    for (int i = 0; i + m <= t; i++) C[i + m] ^= RsGf.Mul(coef, B[i]);
                    L = k + 1 - L;
                    B = T;
                    b = d;
                    m = 1;
                }
                else
                {
                    int coef = RsGf.Mul(d, RsGf.Inv(b));
                    for (int i = 0; i + m <= t; i++) C[i + m] ^= RsGf.Mul(coef, B[i]);
                    m++;
                }
            }
            if (L <= 0 || L > t / 2) return false;   // 错误数超出纠错能力

            // 3) Chien 搜索找错误位置
            int[] errX = new int[L];
            int[] errPos = new int[L];
            int found = 0;
            for (int p = 0; p < n; p++)
            {
                int X = RsGf.Exp[(n - 1 - p) % 255];
                int xinv = RsGf.Inv(X);
                int v = 0;
                for (int i = L; i >= 0; i--) v = RsGf.Mul(v, xinv) ^ C[i];
                if (v == 0)
                {
                    if (found < L)
                    {
                        errX[found] = X;
                        errPos[found] = p;
                    }
                    found++;
                }
            }
            if (found != L) return false;

            // 4) Forney 算法求错误幅值：Omega = S*sigma mod x^t
            int[] omega = new int[t];
            for (int i = 0; i < t; i++)
            {
                int acc = 0;
                for (int j = 0; j <= i && j <= L; j++) acc ^= RsGf.Mul(C[j], syn[i - j]);
                omega[i] = acc;
            }
            int[] dSigma = new int[L > 0 ? L : 1];
            for (int i = 1; i <= L; i += 2) dSigma[i - 1] = C[i];

            for (int e = 0; e < L; e++)
            {
                int X = errX[e];
                int xinv = RsGf.Inv(X);
                int num = EvalPoly(omega, xinv);
                int den = EvalPoly(dSigma, xinv);
                if (den == 0) return false;
                int mag = RsGf.Mul(X, RsGf.Mul(num, RsGf.Inv(den)));
                recv[errPos[e]] ^= (byte)mag;
            }

            // 5) 复核：纠正后伴随式必须全零
            for (int i = 0; i < t; i++)
            {
                int xi = RsGf.Exp[i];
                int s = 0;
                for (int j = 0; j < n; j++) s = RsGf.Mul(s, xi) ^ recv[j];
                if (s != 0) return false;
            }
            return true;
        }

        private static int EvalPoly(int[] poly, int x)
        {
            int v = 0;
            for (int i = poly.Length - 1; i >= 0; i--) v = RsGf.Mul(v, x) ^ poly[i];
            return v;
        }
    }
}