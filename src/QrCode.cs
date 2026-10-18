// Faithful C# port of qrcodejs 1.0.0 (davidshimjs, MIT) as vendored inside
// Source\Login-Mooc\resources\app.asar -> duanshu_qr.js.
// Deliberately mirrors the original algorithm (including its quirks and its
// version-selection rule) so the output can be diffed bit-for-bit against the
// original running under node.  Tables live in QrTables.g.cs (auto-generated).
using System;
using System.Collections.Generic;
using System.Text;

namespace BiliGetter
{
    // ---------------------------------------------------------------- QR math
    internal static class QrMath
    {
        private static readonly int[] ExpTable = new int[256];
        private static readonly int[] LogTable = new int[256];
        static QrMath()
        {
            for (int i = 0; i < 8; i++) ExpTable[i] = 1 << i;
            for (int i = 8; i < 256; i++)
                ExpTable[i] = ExpTable[i - 4] ^ ExpTable[i - 5] ^ ExpTable[i - 6] ^ ExpTable[i - 8];
            for (int i = 0; i < 255; i++) LogTable[ExpTable[i]] = i;
        }
        public static int Glog(int n)
        {
            if (n < 1) throw new ArgumentException("glog(" + n + ")");
            return LogTable[n];
        }
        public static int Gexp(int n)
        {
            while (n < 0) n += 255;
            while (n >= 256) n -= 255;
            return ExpTable[n];
        }
    }

    // ---------------------------------------------------------- QR polynomial
    internal sealed class QrPolynomial
    {
        private readonly int[] _num;
        public QrPolynomial(int[] num, int shift)
        {
            int offset = 0;
            while (offset < num.Length && num[offset] == 0) offset++;
            _num = new int[num.Length - offset + shift];
            for (int i = 0; i < num.Length - offset; i++) _num[i] = num[i + offset];
        }
        public int Get(int index) { return _num[index]; }
        public int Length { get { return _num.Length; } }
        public QrPolynomial Multiply(QrPolynomial e)
        {
            int[] num = new int[Length + e.Length - 1];
            for (int i = 0; i < Length; i++)
                for (int j = 0; j < e.Length; j++)
                    num[i + j] ^= QrMath.Gexp(QrMath.Glog(Get(i)) + QrMath.Glog(e.Get(j)));
            return new QrPolynomial(num, 0);
        }
        public QrPolynomial Mod(QrPolynomial e)
        {
            if (Length - e.Length < 0) return this;
            int ratio = QrMath.Glog(Get(0)) - QrMath.Glog(e.Get(0));
            int[] num = new int[Length];
            for (int i = 0; i < Length; i++) num[i] = Get(i);
            for (int i = 0; i < e.Length; i++) num[i] ^= QrMath.Gexp(QrMath.Glog(e.Get(i)) + ratio);
            return new QrPolynomial(num, 0).Mod(e);
        }
    }

    // ------------------------------------------------------------ RS blocks
    internal sealed class QrRsBlock
    {
        public readonly int TotalCount;
        public readonly int DataCount;
        public QrRsBlock(int totalCount, int dataCount) { TotalCount = totalCount; DataCount = dataCount; }

        public static List<QrRsBlock> GetRsBlocks(int typeNumber, int errorCorrectLevel)
        {
            int[] rsBlock = GetRsBlockTable(typeNumber, errorCorrectLevel);
            int length = rsBlock.Length / 3;
            var list = new List<QrRsBlock>();
            for (int i = 0; i < length; i++)
            {
                int count = rsBlock[i * 3 + 0];
                int totalCount = rsBlock[i * 3 + 1];
                int dataCount = rsBlock[i * 3 + 2];
                for (int j = 0; j < count; j++) list.Add(new QrRsBlock(totalCount, dataCount));
            }
            return list;
        }

        private static int[] GetRsBlockTable(int typeNumber, int errorCorrectLevel)
        {
            int row;
            if (errorCorrectLevel == QrTables.LevelL) row = 4 * (typeNumber - 1) + 0;
            else if (errorCorrectLevel == QrTables.LevelM) row = 4 * (typeNumber - 1) + 1;
            else if (errorCorrectLevel == QrTables.LevelQ) row = 4 * (typeNumber - 1) + 2;
            else if (errorCorrectLevel == QrTables.LevelH) row = 4 * (typeNumber - 1) + 3;
            else throw new ArgumentException("bad errorCorrectLevel " + errorCorrectLevel);
            return QrTables.RsBlock[row];
        }
    }

    // --------------------------------------------------------- bit buffer
    internal sealed class QrBitBuffer
    {
        private readonly List<int> _buffer = new List<int>();
        private int _length;
        public bool Get(int index)
        {
            int b = index / 8;
            return ((_buffer[b] >> (7 - index % 8)) & 1) == 1;
        }
        public void Put(int num, int length)
        {
            for (int i = 0; i < length; i++) PutBit(((num >> (length - i - 1)) & 1) == 1);
        }
        public int LengthInBits { get { return _length; } }
        public void PutBit(bool bit)
        {
            int b = _length / 8;
            if (_buffer.Count <= b) _buffer.Add(0);
            if (bit) _buffer[b] |= 128 >> (_length % 8);
            _length++;
        }
        public int[] Buffer { get { return _buffer.ToArray(); } }
    }

    // ------------------------------------------------------- 8-bit byte data
    internal sealed class QrData
    {
        public const int Mode8BitByte = 4;
        public readonly int Mode = Mode8BitByte;
        private readonly byte[] _parsed;
        public readonly int SourceLength;   // JS string length (UTF-16 code units)

        public QrData(string data)
        {
            SourceLength = data.Length;
            var bytes = new List<byte>();
            for (int i = 0; i < data.Length; i++)
            {
                int f = (int)data[i];          // mimic charCodeAt: UTF-16 code unit
                if (f > 65536)
                {
                    bytes.Add((byte)(240 | ((1835008 & f) >> 18)));
                    bytes.Add((byte)(128 | ((258048 & f) >> 12)));
                    bytes.Add((byte)(128 | ((4032 & f) >> 6)));
                    bytes.Add((byte)(128 | (63 & f)));
                }
                else if (f > 2048)
                {
                    bytes.Add((byte)(224 | ((61440 & f) >> 12)));
                    bytes.Add((byte)(128 | ((4032 & f) >> 6)));
                    bytes.Add((byte)(128 | (63 & f)));
                }
                else if (f > 128)
                {
                    bytes.Add((byte)(192 | ((1984 & f) >> 6)));
                    bytes.Add((byte)(128 | (63 & f)));
                }
                else bytes.Add((byte)f);
            }
            // qrcodejs prepends a UTF-8 BOM whenever the byte count differs from
            // the JS string length (i.e. the text was not pure single-byte ASCII).
            if (bytes.Count != data.Length)
            {
                bytes.Insert(0, 191); bytes.Insert(0, 187); bytes.Insert(0, 239);
            }
            _parsed = bytes.ToArray();
        }

        public int GetLength() { return _parsed.Length; }

        public void Write(QrBitBuffer buffer)
        {
            for (int i = 0; i < _parsed.Length; i++) buffer.Put(_parsed[i], 8);
        }
    }

    // ------------------------------------------------------------------ util
    internal static class QrUtil
    {
        private const int G15 = 1335;
        private const int G18 = 7973;
        private const int G15Mask = 21522;

        public static int GetBchTypeInfo(int data)
        {
            int d = data << 10;
            while (GetBchDigit(d) - GetBchDigit(G15) >= 0) d ^= (G15 << (GetBchDigit(d) - GetBchDigit(G15)));
            return ((data << 10) | d) ^ G15Mask;
        }
        public static int GetBchTypeNumber(int data)
        {
            int d = data << 12;
            while (GetBchDigit(d) - GetBchDigit(G18) >= 0) d ^= (G18 << (GetBchDigit(d) - GetBchDigit(G18)));
            return (data << 12) | d;
        }
        private static int GetBchDigit(int data)
        {
            int digit = 0;
            while (data != 0) { digit++; data = (int)((uint)data >> 1); }
            return digit;
        }
        public static int[] GetPatternPosition(int typeNumber) { return QrTables.PatternPosition[typeNumber - 1]; }

        public static bool GetMask(int maskPattern, int i, int j)
        {
            switch (maskPattern)
            {
                case 0: return (i + j) % 2 == 0;
                case 1: return i % 2 == 0;
                case 2: return j % 3 == 0;
                case 3: return (i + j) % 3 == 0;
                case 4: return (i / 2 + j / 3) % 2 == 0;
                case 5: return (i * j) % 2 + (i * j) % 3 == 0;
                case 6: return ((i * j) % 2 + (i * j) % 3) % 2 == 0;
                case 7: return ((i * j) % 3 + (i + j) % 2) % 2 == 0;
                default: throw new ArgumentException("bad maskPattern:" + maskPattern);
            }
        }

        public static QrPolynomial GetErrorCorrectPolynomial(int errorCorrectLength)
        {
            var a = new QrPolynomial(new[] { 1 }, 0);
            for (int i = 0; i < errorCorrectLength; i++)
                a = a.Multiply(new QrPolynomial(new[] { 1, QrMath.Gexp(i) }, 0));
            return a;
        }

        public static int GetLengthInBits(int mode, int type)
        {
            if (type >= 1 && type < 10)
            {
                switch (mode)
                {
                    case 1: return 10;
                    case 2: return 9;
                    case 4: return 8;
                    case 8: return 8;
                    default: throw new ArgumentException("mode:" + mode);
                }
            }
            if (type < 27)
            {
                switch (mode)
                {
                    case 1: return 12;
                    case 2: return 11;
                    case 4: return 16;
                    case 8: return 10;
                    default: throw new ArgumentException("mode:" + mode);
                }
            }
            if (type < 41)
            {
                switch (mode)
                {
                    case 1: return 14;
                    case 2: return 13;
                    case 4: return 16;
                    case 8: return 12;
                    default: throw new ArgumentException("mode:" + mode);
                }
            }
            throw new ArgumentException("type:" + type);
        }

        // Penalty score.  qrcodejs accumulates this as a JS number and adds a
        // fractional term, so ties are broken on a double, not an int.
        public static double GetLostPoint(QrCode qr)
        {
            int moduleCount = qr.ModuleCount;
            double lostPoint = 0;

            for (int row = 0; row < moduleCount; row++)
            {
                for (int col = 0; col < moduleCount; col++)
                {
                    int sameCount = 0;
                    bool dark = qr.IsDark(row, col);
                    for (int r = -1; r <= 1; r++)
                    {
                        if (row + r < 0 || row + r >= moduleCount) continue;
                        for (int c = -1; c <= 1; c++)
                        {
                            if (col + c < 0 || col + c >= moduleCount) continue;
                            if (r == 0 && c == 0) continue;
                            if (dark == qr.IsDark(row + r, col + c)) sameCount++;
                        }
                    }
                    if (sameCount > 5) lostPoint += 3 + sameCount - 5;
                }
            }

            for (int row = 0; row < moduleCount - 1; row++)
            {
                for (int col = 0; col < moduleCount - 1; col++)
                {
                    int count = 0;
                    if (qr.IsDark(row, col)) count++;
                    if (qr.IsDark(row + 1, col)) count++;
                    if (qr.IsDark(row, col + 1)) count++;
                    if (qr.IsDark(row + 1, col + 1)) count++;
                    if (count == 0 || count == 4) lostPoint += 3;
                }
            }

            for (int row = 0; row < moduleCount; row++)
            {
                for (int col = 0; col < moduleCount - 6; col++)
                {
                    if (qr.IsDark(row, col) && !qr.IsDark(row, col + 1) && qr.IsDark(row, col + 2)
                        && qr.IsDark(row, col + 3) && qr.IsDark(row, col + 4) && !qr.IsDark(row, col + 5)
                        && qr.IsDark(row, col + 6)) lostPoint += 40;
                }
            }
            for (int col = 0; col < moduleCount; col++)
            {
                for (int row = 0; row < moduleCount - 6; row++)
                {
                    if (qr.IsDark(row, col) && !qr.IsDark(row + 1, col) && qr.IsDark(row + 2, col)
                        && qr.IsDark(row + 3, col) && qr.IsDark(row + 4, col) && !qr.IsDark(row + 5, col)
                        && qr.IsDark(row + 6, col)) lostPoint += 40;
                }
            }

            int darkCount = 0;
            for (int col = 0; col < moduleCount; col++)
                for (int row = 0; row < moduleCount; row++)
                    if (qr.IsDark(row, col)) darkCount++;

            double ratio = Math.Abs(100.0 * darkCount / moduleCount / moduleCount - 50) / 5;
            return lostPoint + 10 * ratio;
        }
    }

    // ----------------------------------------------------------------- model
    internal sealed class QrCode
    {
        public int ModuleCount { get; private set; }
        public int TypeNumber { get; private set; }
        public int ErrorCorrectLevel { get; private set; }
        private sbyte[,] _modules;          // -1 unset, 0 light, 1 dark
        private int[] _dataCache;
        private readonly List<QrData> _dataList = new List<QrData>();

        private const int Pad0 = 0xEC;
        private const int Pad1 = 0x11;

        public bool IsDark(int row, int col)
        {
            if (row < 0 || ModuleCount <= row || col < 0 || ModuleCount <= col)
                throw new ArgumentException(row + "," + col);
            return _modules[row, col] == 1;
        }

        public void AddData(string data)
        {
            _dataList.Add(new QrData(data));
            _dataCache = null;
        }

        public void Make() { MakeImpl(false, GetBestMaskPattern()); }

        private void MakeImpl(bool test, int maskPattern)
        {
            ModuleCount = 4 * TypeNumber + 17;
            _modules = new sbyte[ModuleCount, ModuleCount];
            for (int r = 0; r < ModuleCount; r++)
                for (int c = 0; c < ModuleCount; c++) _modules[r, c] = -1;

            SetupPositionProbePattern(0, 0);
            SetupPositionProbePattern(ModuleCount - 7, 0);
            SetupPositionProbePattern(0, ModuleCount - 7);
            SetupPositionAdjustPattern();
            SetupTimingPattern();
            SetupTypeInfo(test, maskPattern);
            if (TypeNumber >= 7) SetupTypeNumber(test);

            if (_dataCache == null) _dataCache = CreateData(TypeNumber, ErrorCorrectLevel, _dataList);
            MapData(_dataCache, maskPattern);
        }

        private void SetupPositionProbePattern(int row, int col)
        {
            for (int r = -1; r <= 7; r++)
            {
                if (row + r <= -1 || ModuleCount <= row + r) continue;
                for (int c = -1; c <= 7; c++)
                {
                    if (col + c <= -1 || ModuleCount <= col + c) continue;
                    _modules[row + r, col + c] =
                        (0 <= r && r <= 6 && (c == 0 || c == 6))
                        || (0 <= c && c <= 6 && (r == 0 || r == 6))
                        || (2 <= r && r <= 4 && 2 <= c && c <= 4) ? (sbyte)1 : (sbyte)0;
                }
            }
        }

        private int GetBestMaskPattern()
        {
            double minLostPoint = 0;
            int pattern = 0;
            for (int i = 0; i < 8; i++)
            {
                MakeImpl(true, i);
                double lostPoint = QrUtil.GetLostPoint(this);
                if (i == 0 || minLostPoint > lostPoint) { minLostPoint = lostPoint; pattern = i; }
            }
            return pattern;
        }

        private void SetupTimingPattern()
        {
            for (int r = 8; r < ModuleCount - 8; r++)
                if (_modules[r, 6] == -1) _modules[r, 6] = (sbyte)(r % 2 == 0 ? 1 : 0);
            for (int c = 8; c < ModuleCount - 8; c++)
                if (_modules[6, c] == -1) _modules[6, c] = (sbyte)(c % 2 == 0 ? 1 : 0);
        }

        private void SetupPositionAdjustPattern()
        {
            int[] pos = QrUtil.GetPatternPosition(TypeNumber);
            for (int i = 0; i < pos.Length; i++)
            {
                for (int j = 0; j < pos.Length; j++)
                {
                    int row = pos[i], col = pos[j];
                    if (_modules[row, col] != -1) continue;
                    for (int r = -2; r <= 2; r++)
                        for (int c = -2; c <= 2; c++)
                            _modules[row + r, col + c] =
                                (r == -2 || r == 2 || c == -2 || c == 2 || (r == 0 && c == 0)) ? (sbyte)1 : (sbyte)0;
                }
            }
        }

        private void SetupTypeNumber(bool test)
        {
            int bits = QrUtil.GetBchTypeNumber(TypeNumber);
            for (int i = 0; i < 18; i++)
            {
                bool mod = !test && ((bits >> i) & 1) == 1;
                _modules[i / 3, i % 3 + ModuleCount - 8 - 3] = (sbyte)(mod ? 1 : 0);
            }
            for (int i = 0; i < 18; i++)
            {
                bool mod = !test && ((bits >> i) & 1) == 1;
                _modules[i % 3 + ModuleCount - 8 - 3, i / 3] = (sbyte)(mod ? 1 : 0);
            }
        }

        private void SetupTypeInfo(bool test, int maskPattern)
        {
            int data = (ErrorCorrectLevel << 3) | maskPattern;
            int bits = QrUtil.GetBchTypeInfo(data);
            for (int i = 0; i < 15; i++)
            {
                bool mod = !test && ((bits >> i) & 1) == 1;
                if (i < 6) _modules[i, 8] = (sbyte)(mod ? 1 : 0);
                else if (i < 8) _modules[i + 1, 8] = (sbyte)(mod ? 1 : 0);
                else _modules[ModuleCount - 15 + i, 8] = (sbyte)(mod ? 1 : 0);
            }
            for (int i = 0; i < 15; i++)
            {
                bool mod = !test && ((bits >> i) & 1) == 1;
                if (i < 8) _modules[8, ModuleCount - i - 1] = (sbyte)(mod ? 1 : 0);
                else if (i < 9) _modules[8, 15 - i - 1 + 1] = (sbyte)(mod ? 1 : 0);
                else _modules[8, 15 - i - 1] = (sbyte)(mod ? 1 : 0);
            }
            _modules[ModuleCount - 8, 8] = (sbyte)(test ? 0 : 1);
        }

        private void MapData(int[] data, int maskPattern)
        {
            int inc = -1;
            int row = ModuleCount - 1;
            int bitIndex = 7;
            int byteIndex = 0;
            for (int col = ModuleCount - 1; col > 0; col -= 2)
            {
                if (col == 6) col--;
                while (true)
                {
                    for (int c = 0; c < 2; c++)
                    {
                        if (_modules[row, col - c] == -1)
                        {
                            bool dark = false;
                            if (byteIndex < data.Length) dark = ((data[byteIndex] >> bitIndex) & 1) == 1;
                            if (QrUtil.GetMask(maskPattern, row, col - c)) dark = !dark;
                            _modules[row, col - c] = (sbyte)(dark ? 1 : 0);
                            bitIndex--;
                            if (bitIndex == -1) { byteIndex++; bitIndex = 7; }
                        }
                    }
                    row += inc;
                    if (row < 0 || ModuleCount <= row) { row -= inc; inc = -inc; break; }
                }
            }
        }

        private static int[] CreateData(int typeNumber, int errorCorrectLevel, List<QrData> dataList)
        {
            List<QrRsBlock> rsBlocks = QrRsBlock.GetRsBlocks(typeNumber, errorCorrectLevel);
            var buffer = new QrBitBuffer();
            for (int i = 0; i < dataList.Count; i++)
            {
                QrData data = dataList[i];
                buffer.Put(data.Mode, 4);
                buffer.Put(data.GetLength(), QrUtil.GetLengthInBits(data.Mode, typeNumber));
                data.Write(buffer);
            }
            int totalDataCount = 0;
            for (int i = 0; i < rsBlocks.Count; i++) totalDataCount += rsBlocks[i].DataCount;
            if (buffer.LengthInBits > 8 * totalDataCount)
                throw new Exception("code length overflow. (" + buffer.LengthInBits + ">" + 8 * totalDataCount + ")");
            if (buffer.LengthInBits + 4 <= 8 * totalDataCount) buffer.Put(0, 4);
            while (buffer.LengthInBits % 8 != 0) buffer.PutBit(false);
            while (true)
            {
                if (buffer.LengthInBits >= 8 * totalDataCount) break;
                buffer.Put(Pad0, 8);
                if (buffer.LengthInBits >= 8 * totalDataCount) break;
                buffer.Put(Pad1, 8);
            }
            return CreateBytes(buffer, rsBlocks);
        }

        private static int[] CreateBytes(QrBitBuffer buffer, List<QrRsBlock> rsBlocks)
        {
            int offset = 0;
            int maxDcCount = 0, maxEcCount = 0;
            int[][] dcData = new int[rsBlocks.Count][];
            int[][] ecData = new int[rsBlocks.Count][];
            for (int r = 0; r < rsBlocks.Count; r++)
            {
                int dcCount = rsBlocks[r].DataCount;
                int ecCount = rsBlocks[r].TotalCount - dcCount;
                maxDcCount = Math.Max(maxDcCount, dcCount);
                maxEcCount = Math.Max(maxEcCount, ecCount);
                dcData[r] = new int[dcCount];
                int[] buf = buffer.Buffer;
                for (int i = 0; i < dcData[r].Length; i++) dcData[r][i] = 0xff & buf[i + offset];
                offset += dcCount;

                QrPolynomial rsPoly = QrUtil.GetErrorCorrectPolynomial(ecCount);
                var rawPoly = new QrPolynomial(dcData[r], rsPoly.Length - 1);
                QrPolynomial modPoly = rawPoly.Mod(rsPoly);
                ecData[r] = new int[rsPoly.Length - 1];
                for (int i = 0; i < ecData[r].Length; i++)
                {
                    int modIndex = i + modPoly.Length - ecData[r].Length;
                    ecData[r][i] = modIndex >= 0 ? modPoly.Get(modIndex) : 0;
                }
            }

            int totalCodeCount = 0;
            for (int i = 0; i < rsBlocks.Count; i++) totalCodeCount += rsBlocks[i].TotalCount;

            int[] data = new int[totalCodeCount];
            int index = 0;
            for (int i = 0; i < maxDcCount; i++)
                for (int r = 0; r < rsBlocks.Count; r++)
                    if (i < dcData[r].Length) data[index++] = dcData[r][i];
            for (int i = 0; i < maxEcCount; i++)
                for (int r = 0; r < rsBlocks.Count; r++)
                    if (i < ecData[r].Length) data[index++] = ecData[r][i];
            return data;
        }

        // ---- version selection: mirrors qrcodejs' rule, then widens if needed ----
        public static int ChooseTypeNumber(string text, int level)
        {
            var d = new QrData(text);
            // qrcodejs computes: b.length + (b.length != a ? 3 : 0), where `a` is
            // the original string.  That is a number-vs-string loose comparison,
            // so it is ALWAYS true (string coerces to NaN) and qrcodejs always
            // adds 3.  Replicated faithfully so the version chosen matches the
            // reference bit-for-bit; it only makes the symbol one step larger
            // than strictly necessary, which is still perfectly valid.
            int approx = d.GetLength() + 3;
            int col = LevelColumn(level);

            int chosen = -1;
            for (int v = 1; v <= 40; v++)
            {
                if (QrTables.Capacity[v - 1][col] >= approx) { chosen = v; break; }
            }
            if (chosen < 0) throw new Exception("Too long data");

            // qrcodejs can pick a version too small for the real bit stream; widen
            // until CreateData actually fits (this only triggers where the
            // original throws "code length overflow").
            for (int v = chosen; v <= 40; v++)
            {
                try { CreateData(v, level, new List<QrData> { d }); return v; }
                catch (Exception) { }
            }
            throw new Exception("Too long data");
        }

        private static int LevelColumn(int level)
        {
            if (level == QrTables.LevelL) return 0;
            if (level == QrTables.LevelM) return 1;
            if (level == QrTables.LevelQ) return 2;
            if (level == QrTables.LevelH) return 3;
            throw new ArgumentException("bad level " + level);
        }

        public static QrCode Encode(string text, int level)
        {
            var qr = new QrCode();
            qr.ErrorCorrectLevel = level;
            qr.TypeNumber = ChooseTypeNumber(text, level);
            qr.AddData(text);
            qr.Make();
            return qr;
        }
    }
}
