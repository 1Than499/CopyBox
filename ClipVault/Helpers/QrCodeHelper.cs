using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipVault.Helpers
{
    /// <summary>
    /// v3.0 零外部依赖原生 ISO/IEC 18004 标准二维码生成引擎 (Byte Mode, ECC Level L, 支持 Version 1~6 自适应)
    /// 无需引入任何重型第三方 NuGet 包，100% 兼容 iOS 相机、Android 浏览器、微信扫一扫
    /// </summary>
    public static class QrCodeHelper
    {
        // Version 1..6 在 ECC Level L 下的总码字数、数据码字数、EC码字数、对齐图案坐标
        private static readonly int[] TotalCodewords = { 0, 26, 44, 70, 100, 134, 172 };
        private static readonly int[] DataCodewordsL = { 0, 19, 34, 55, 80, 108, 136 };
        private static readonly int[] EcCodewordsPerBlockL = { 0, 7, 10, 15, 20, 26, 18 };
        private static readonly int[] NumBlocksL = { 0, 1, 1, 1, 1, 1, 2 };
        private static readonly int[][] AlignmentCoords =
        {
            Array.Empty<int>(),
            Array.Empty<int>(),
            new[] { 6, 18 },
            new[] { 6, 22 },
            new[] { 6, 26 },
            new[] { 6, 30 },
            new[] { 6, 34 }
        };

        /// <summary>
        /// 将指定 URL 或文本生成为高清无锯齿的 WPF BitmapSource 二维码图像
        /// </summary>
        public static BitmapSource GenerateQrBitmap(string text, int pixelSize = 220)
        {
            bool[,] matrix = EncodeToMatrix(text ?? string.Empty);
            int modules = matrix.GetLength(0);
            int quietZone = 4;
            int totalModules = modules + quietZone * 2;
            int scale = Math.Max(4, pixelSize / totalModules);
            int imgDim = totalModules * scale;

            var wb = new WriteableBitmap(imgDim, imgDim, 96, 96, PixelFormats.Bgra32, null);
            int[] pixels = new int[imgDim * imgDim];

            // 纯白背景 (#FFFFFFFF) 与纯黑模块 (#FF000000)，确保最高扫码对比度
            int white = unchecked((int)0xFFFFFFFF);
            int dark = unchecked((int)0xFF000000);

            Array.Fill(pixels, white);

            for (int r = 0; r < modules; r++)
            {
                for (int c = 0; c < modules; c++)
                {
                    if (!matrix[r, c]) continue;
                    int startY = (r + quietZone) * scale;
                    int startX = (c + quietZone) * scale;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        int rowOffset = (startY + dy) * imgDim + startX;
                        for (int dx = 0; dx < scale; dx++)
                        {
                            pixels[rowOffset + dx] = dark;
                        }
                    }
                }
            }

            wb.WritePixels(new Int32Rect(0, 0, imgDim, imgDim), pixels, imgDim * 4, 0);
            wb.Freeze();
            return wb;
        }

        /// <summary>
        /// 生成布尔矩阵 (true = 黑色模块, false = 白色模块)
        /// </summary>
        public static bool[,] EncodeToMatrix(string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);

            // 选择合适的 Version (1..6)
            int version = 1;
            while (version <= 6 && payload.Length + 3 > DataCodewordsL[version])
            {
                version++;
            }
            if (version > 6)
            {
                // 超长截断保护
                version = 6;
                Array.Resize(ref payload, DataCodewordsL[6] - 3);
            }

            int dataCap = DataCodewordsL[version];
            byte[] dataCw = BuildDataCodewords(payload, dataCap);
            byte[] allCw = BuildFinalMessage(dataCw, version);

            int size = 17 + 4 * version;
            bool[,] modules = new bool[size, size];
            bool[,] isFunction = new bool[size, size];

            DrawFunctionPatterns(modules, isFunction, version, size);
            DrawCodewords(modules, isFunction, allCw, size);

            // 选择惩罚分数最低的最佳掩码 (0..7)
            int bestMask = 0;
            int minPenalty = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                bool[,] candidate = (bool[,])modules.Clone();
                ApplyMask(candidate, isFunction, m, size);
                DrawFormatBits(candidate, isFunction, m, size);
                int penalty = CalculatePenalty(candidate, size);
                if (penalty < minPenalty)
                {
                    minPenalty = penalty;
                    bestMask = m;
                }
            }

            ApplyMask(modules, isFunction, bestMask, size);
            DrawFormatBits(modules, isFunction, bestMask, size);
            return modules;
        }

        private static byte[] BuildDataCodewords(byte[] payload, int capacityBytes)
        {
            int maxBits = capacityBytes * 8;
            int[] bits = new int[maxBits];
            int bitLen = 0;

            void AppendBits(int val, int len)
            {
                for (int i = len - 1; i >= 0 && bitLen < maxBits; i--)
                {
                    bits[bitLen++] = (val >> i) & 1;
                }
            }

            // Byte mode indicator (0100) + 8-bit char count (Version 1..9)
            AppendBits(0b0100, 4);
            AppendBits(payload.Length, 8);
            foreach (byte b in payload)
            {
                AppendBits(b, 8);
            }

            // Terminator (up to 4 zero bits)
            AppendBits(0, Math.Min(4, maxBits - bitLen));

            // Pad to byte boundary
            if (bitLen % 8 != 0)
            {
                AppendBits(0, 8 - (bitLen % 8));
            }

            byte[] result = new byte[capacityBytes];
            for (int i = 0; i < bitLen / 8; i++)
            {
                int b = 0;
                for (int j = 0; j < 8; j++) b = (b << 1) | bits[i * 8 + j];
                result[i] = (byte)b;
            }

            byte[] padBytes = { 0xEC, 0x11 };
            int padIdx = 0;
            for (int i = bitLen / 8; i < capacityBytes; i++)
            {
                result[i] = padBytes[padIdx++ & 1];
            }

            return result;
        }

        private static byte[] BuildFinalMessage(byte[] dataCw, int version)
        {
            int numBlocks = NumBlocksL[version];
            int ecLen = EcCodewordsPerBlockL[version];
            int totalLen = TotalCodewords[version];

            if (numBlocks == 1)
            {
                byte[] ec = ComputeReedSolomon(dataCw, ecLen);
                byte[] finalMsg = new byte[totalLen];
                Buffer.BlockCopy(dataCw, 0, finalMsg, 0, dataCw.Length);
                Buffer.BlockCopy(ec, 0, finalMsg, dataCw.Length, ec.Length);
                return finalMsg;
            }
            else
            {
                int blockDataLen = dataCw.Length / numBlocks;
                byte[][] dataBlocks = new byte[numBlocks][];
                byte[][] ecBlocks = new byte[numBlocks][];

                for (int b = 0; b < numBlocks; b++)
                {
                    dataBlocks[b] = new byte[blockDataLen];
                    Buffer.BlockCopy(dataCw, b * blockDataLen, dataBlocks[b], 0, blockDataLen);
                    ecBlocks[b] = ComputeReedSolomon(dataBlocks[b], ecLen);
                }

                byte[] finalMsg = new byte[totalLen];
                int idx = 0;
                for (int i = 0; i < blockDataLen; i++)
                    for (int b = 0; b < numBlocks; b++)
                        finalMsg[idx++] = dataBlocks[b][i];

                for (int i = 0; i < ecLen; i++)
                    for (int b = 0; b < numBlocks; b++)
                        finalMsg[idx++] = ecBlocks[b][i];

                return finalMsg;
            }
        }

        private static byte[] ComputeReedSolomon(byte[] data, int degree)
        {
            byte[] gen = new byte[degree];
            gen[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    gen[j] = GfMultiply(gen[j], (byte)root);
                    if (j + 1 < degree) gen[j] ^= gen[j + 1];
                }
                root = GfMultiply((byte)root, 0x02);
            }

            byte[] result = new byte[degree];
            foreach (byte b in data)
            {
                byte factor = (byte)(b ^ result[0]);
                Array.Copy(result, 1, result, 0, degree - 1);
                result[degree - 1] = 0;
                for (int j = 0; j < degree; j++)
                {
                    result[j] ^= GfMultiply(gen[j], factor);
                }
            }
            return result;
        }

        private static byte GfMultiply(byte x, byte y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, int version, int size)
        {
            // Timing patterns
            for (int i = 0; i < size; i++)
            {
                SetFunctionModule(modules, isFunction, 6, i, i % 2 == 0);
                SetFunctionModule(modules, isFunction, i, 6, i % 2 == 0);
            }

            // 3 Finder patterns
            DrawFinderPattern(modules, isFunction, 3, 3, size);
            DrawFinderPattern(modules, isFunction, size - 4, 3, size);
            DrawFinderPattern(modules, isFunction, 3, size - 4, size);

            // Alignment pattern (Version >= 2)
            int[] align = AlignmentCoords[version];
            if (align.Length >= 2)
            {
                int ar = align[1], ac = align[1];
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        SetFunctionModule(modules, isFunction, ar + dy, ac + dx, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }

            // Reserve Format Info areas + Dark Module
            DrawFormatBits(modules, isFunction, 0, size);
        }

        private static void DrawFinderPattern(bool[,] modules, bool[,] isFunction, int cr, int cc, int size)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int r = cr + dy, c = cc + dx;
                    if (r >= 0 && r < size && c >= 0 && c < size)
                    {
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        SetFunctionModule(modules, isFunction, r, c, dist != 2 && dist != 4);
                    }
                }
            }
        }

        private static void DrawFormatBits(bool[,] modules, bool[,] isFunction, int mask, int size)
        {
            // ECC Level L = 01
            int data = (0b01 << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) SetFunctionModule(modules, isFunction, i, 8, ((bits >> i) & 1) != 0);
            SetFunctionModule(modules, isFunction, 7, 8, ((bits >> 6) & 1) != 0);
            SetFunctionModule(modules, isFunction, 8, 8, ((bits >> 7) & 1) != 0);
            SetFunctionModule(modules, isFunction, 8, 7, ((bits >> 8) & 1) != 0);
            for (int i = 9; i < 15; i++) SetFunctionModule(modules, isFunction, 8, 14 - i, ((bits >> i) & 1) != 0);

            for (int i = 0; i < 8; i++) SetFunctionModule(modules, isFunction, 8, size - 1 - i, ((bits >> i) & 1) != 0);
            for (int i = 8; i < 15; i++) SetFunctionModule(modules, isFunction, size - 15 + i, 8, ((bits >> i) & 1) != 0);
            SetFunctionModule(modules, isFunction, size - 8, 8, true); // Always dark module
        }

        private static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int r, int c, bool isDark)
        {
            modules[r, c] = isDark;
            isFunction[r, c] = true;
        }

        private static void DrawCodewords(bool[,] modules, bool[,] isFunction, byte[] allCw, int size)
        {
            int i = 0;
            int totalBits = allCw.Length * 8;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int c = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int r = upward ? (size - 1 - vert) : vert;
                        if (!isFunction[r, c] && i < totalBits)
                        {
                            modules[r, c] = ((allCw[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
                }
            }
        }

        private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask, int size)
        {
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    if (isFunction[r, c]) continue;
                    bool invert = mask switch
                    {
                        0 => (r + c) % 2 == 0,
                        1 => r % 2 == 0,
                        2 => c % 3 == 0,
                        3 => (r + c) % 3 == 0,
                        4 => (r / 2 + c / 3) % 2 == 0,
                        5 => (r * c) % 2 + (r * c) % 3 == 0,
                        6 => ((r * c) % 2 + (r * c) % 3) % 2 == 0,
                        _ => ((r + c) % 2 + (r * c) % 3) % 2 == 0
                    };
                    if (invert) modules[r, c] = !modules[r, c];
                }
            }
        }

        private static int CalculatePenalty(bool[,] modules, int size)
        {
            int penalty = 0;
            for (int r = 0; r < size; r++)
            {
                int runLen = 1;
                for (int c = 1; c < size; c++)
                {
                    if (modules[r, c] == modules[r, c - 1])
                    {
                        runLen++;
                        if (runLen == 5) penalty += 3;
                        else if (runLen > 5) penalty++;
                    }
                    else runLen = 1;
                }
            }
            for (int c = 0; c < size; c++)
            {
                int runLen = 1;
                for (int r = 1; r < size; r++)
                {
                    if (modules[r, c] == modules[r - 1, c])
                    {
                        runLen++;
                        if (runLen == 5) penalty += 3;
                        else if (runLen > 5) penalty++;
                    }
                    else runLen = 1;
                }
            }
            return penalty;
        }
    }
}
