using System.Buffers.Binary;
using Gpixel.Core.Model;

namespace Gpixel.Core.IO;

/// <summary>
/// 내보내기 이미지 쓰기: PNG(<see cref="PngIO"/>), JPG(기본 baseline, 4:4:4 — 픽셀 아트 색 번짐 최소화, 투명은 흰색 위에 합성),
/// TGA(무압축 32비트, 위→아래), BMP(32비트 BI_BITFIELDS + 알파 마스크). 임시 파일에 쓴 뒤 바꿔치기한다.
/// </summary>
public static class ImageWriter
{
    public static void Save(PixelImage image, string path, ExportImageFormat format, int jpegQuality = 90) => Save(image, path, format, jpegQuality, null);

    /// <param name="stage">진행(PNG만 줄마다, 단위: 픽셀).</param>
    internal static void Save(PixelImage image, string path, ExportImageFormat format, int jpegQuality, WorkStage? stage)
    {
        var full = Path.GetFullPath(path);
        var temp = full + ".gpixel-tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                Save(image, fs, format, jpegQuality, stage);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void Save(PixelImage image, Stream stream, ExportImageFormat format, int jpegQuality = 90) => Save(image, stream, format, jpegQuality, null);

    private static void Save(PixelImage image, Stream stream, ExportImageFormat format, int jpegQuality, WorkStage? stage)
    {
        switch (format)
        {
            case ExportImageFormat.Jpg: JpegEncoder.Write(image, stream, jpegQuality); break;
            case ExportImageFormat.Tga: WriteTga(image, stream); break;
            case ExportImageFormat.Bmp: WriteBmp(image, stream); break;
            default: PngIO.Save(image, stream, stage); break;
        }
    }

    private static void WriteTga(PixelImage image, Stream stream)
    {
        int w = image.Width, h = image.Height;
        Span<byte> header = stackalloc byte[18];
        header.Clear();
        header[2] = 2; // 무압축 트루컬러
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)h);
        header[16] = 32;
        header[17] = 0x28; // 알파 8비트 + 위→아래
        stream.Write(header);
        WriteBgraRows(image, stream, topDown: true);
    }

    private static void WriteBmp(PixelImage image, Stream stream)
    {
        int w = image.Width, h = image.Height;
        const int fileHeader = 14, infoHeader = 108; // BITMAPV4HEADER
        int dataSize = w * h * 4;
        Span<byte> hdr = stackalloc byte[fileHeader + infoHeader];
        hdr.Clear();
        hdr[0] = (byte)'B'; hdr[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(hdr[2..], fileHeader + infoHeader + dataSize);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[10..], fileHeader + infoHeader);
        var info = hdr[fileHeader..];
        BinaryPrimitives.WriteInt32LittleEndian(info, infoHeader);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], w);
        BinaryPrimitives.WriteInt32LittleEndian(info[8..], h); // 양수 = 아래→위(호환성 가장 좋음)
        BinaryPrimitives.WriteUInt16LittleEndian(info[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(info[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(info[16..], 3); // BI_BITFIELDS
        BinaryPrimitives.WriteInt32LittleEndian(info[20..], dataSize);
        BinaryPrimitives.WriteInt32LittleEndian(info[24..], 2835); // 72 DPI
        BinaryPrimitives.WriteInt32LittleEndian(info[28..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(info[40..], 0x00FF0000); // R
        BinaryPrimitives.WriteUInt32LittleEndian(info[44..], 0x0000FF00); // G
        BinaryPrimitives.WriteUInt32LittleEndian(info[48..], 0x000000FF); // B
        BinaryPrimitives.WriteUInt32LittleEndian(info[52..], 0xFF000000); // A
        // CSType(56)은 0(LCS_CALIBRATED_RGB, 끝점 0) 그대로 — macOS ImageIO가 sRGB/Win 표시가 있으면 읽지 못한다.
        stream.Write(hdr);
        WriteBgraRows(image, stream, topDown: false);
    }

    private static void WriteBgraRows(PixelImage image, Stream stream, bool topDown)
    {
        int w = image.Width, h = image.Height;
        var row = new byte[w * 4];
        var px = image.Pixels;
        for (int r = 0; r < h; r++)
        {
            int y = topDown ? r : h - 1 - r;
            for (int x = 0; x < w; x++)
            {
                var c = px[y * w + x];
                int i = x * 4;
                row[i] = c.B; row[i + 1] = c.G; row[i + 2] = c.R; row[i + 3] = c.A;
            }
            stream.Write(row);
        }
    }
}

/// <summary>기본(baseline) JPEG 인코더: YCbCr 4:4:4, 표준 양자화·허프만 표(IJG 품질 배율).</summary>
internal static class JpegEncoder
{
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly byte[] StdLumQ =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly byte[] StdChrQ =
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly byte[] DcLumBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChrBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcVals = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] AcLumBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static readonly byte[] AcChrBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    private static readonly byte[] AcLumVals =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    private static readonly byte[] AcChrVals =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    /// <summary>cos((2x+1)uπ/16) × C(u)/2 (C(0)=1/√2).</summary>
    private static readonly float[] DctTable = BuildDctTable();

    private static float[] BuildDctTable()
    {
        var t = new float[64];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                t[u * 8 + x] = (float)((u == 0 ? 1 / Math.Sqrt(2) : 1) / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return t;
    }

    private readonly record struct HuffTable(ushort[] Codes, byte[] Sizes);

    private static HuffTable Build(byte[] bits, byte[] vals)
    {
        var codes = new ushort[256];
        var sizes = new byte[256];
        int code = 0, k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < bits[len - 1]; i++)
            {
                codes[vals[k]] = (ushort)code;
                sizes[vals[k]] = (byte)len;
                code++; k++;
            }
            code <<= 1;
        }
        return new HuffTable(codes, sizes);
    }

    private static byte[] ScaleQuant(byte[] std, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var q = new byte[64];
        for (int i = 0; i < 64; i++) q[i] = (byte)Math.Clamp((std[i] * scale + 50) / 100, 1, 255);
        return q;
    }

    private sealed class BitWriter(Stream s)
    {
        private int _acc, _count;

        public void Write(int code, int size)
        {
            for (int i = size - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((code >> i) & 1);
                if (++_count == 8) Emit();
            }
        }

        private void Emit()
        {
            s.WriteByte((byte)_acc);
            if (_acc == 0xFF) s.WriteByte(0); // 바이트 채우기
            _acc = 0; _count = 0;
        }

        /// <summary>남은 비트를 1로 채운다.</summary>
        public void Flush()
        {
            while (_count != 0) Write(1, 1);
        }
    }

    public static void Write(PixelImage image, Stream stream, int quality)
    {
        int w = image.Width, h = image.Height;
        var qLum = ScaleQuant(StdLumQ, quality);
        var qChr = ScaleQuant(StdChrQ, quality);
        var dcLum = Build(DcLumBits, DcVals);
        var dcChr = Build(DcChrBits, DcVals);
        var acLum = Build(AcLumBits, AcLumVals);
        var acChr = Build(AcChrBits, AcChrVals);

        Marker(stream, 0xD8);
        // APP0 JFIF
        Segment(stream, 0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        var dqt = new byte[130];
        dqt[0] = 0;
        dqt[65] = 1;
        for (int i = 0; i < 64; i++) { dqt[1 + i] = qLum[ZigZag[i]]; dqt[66 + i] = qChr[ZigZag[i]]; }
        Segment(stream, 0xDB, dqt);
        Segment(stream, 0xC0,
        [
            8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3,
            1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1,
        ]);
        using (var dht = new MemoryStream())
        {
            void Table(int cls, byte[] bits, byte[] vals) { dht.WriteByte((byte)cls); dht.Write(bits); dht.Write(vals); }
            Table(0x00, DcLumBits, DcVals);
            Table(0x10, AcLumBits, AcLumVals);
            Table(0x01, DcChrBits, DcVals);
            Table(0x11, AcChrBits, AcChrVals);
            Segment(stream, 0xC4, dht.ToArray());
        }
        Segment(stream, 0xDA, [3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);

        var bw = new BitWriter(stream);
        var px = image.Pixels;
        var yb = new float[64];
        var cbb = new float[64];
        var crb = new float[64];
        var tmp = new float[64];
        var coef = new int[64];
        int dcY = 0, dcCb = 0, dcCr = 0;
        for (int by = 0; by < h; by += 8)
        {
            for (int bx = 0; bx < w; bx += 8)
            {
                for (int y = 0; y < 8; y++)
                {
                    int sy = Math.Min(by + y, h - 1);
                    for (int x = 0; x < 8; x++)
                    {
                        var c = px[sy * w + Math.Min(bx + x, w - 1)];
                        // 투명은 흰색 위에 합성
                        float a = c.A / 255f;
                        float r = c.R * a + 255 * (1 - a), g = c.G * a + 255 * (1 - a), b = c.B * a + 255 * (1 - a);
                        int i = y * 8 + x;
                        yb[i] = 0.299f * r + 0.587f * g + 0.114f * b - 128;
                        cbb[i] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                        crb[i] = 0.5f * r - 0.418688f * g - 0.081312f * b;
                    }
                }
                dcY = EncodeBlock(bw, yb, tmp, coef, qLum, dcY, dcLum, acLum);
                dcCb = EncodeBlock(bw, cbb, tmp, coef, qChr, dcCb, dcChr, acChr);
                dcCr = EncodeBlock(bw, crb, tmp, coef, qChr, dcCr, dcChr, acChr);
            }
        }
        bw.Flush();
        Marker(stream, 0xD9);
    }

    private static int EncodeBlock(BitWriter bw, float[] block, float[] tmp, int[] coef, byte[] q, int prevDc, HuffTable dc, HuffTable ac)
    {
        // 2D DCT(행 → 열)
        for (int y = 0; y < 8; y++)
            for (int u = 0; u < 8; u++)
            {
                float s = 0;
                for (int x = 0; x < 8; x++) s += DctTable[u * 8 + x] * block[y * 8 + x];
                tmp[y * 8 + u] = s;
            }
        for (int u = 0; u < 8; u++)
            for (int v = 0; v < 8; v++)
            {
                float s = 0;
                for (int y = 0; y < 8; y++) s += DctTable[v * 8 + y] * tmp[y * 8 + u];
                coef[v * 8 + u] = (int)MathF.Round(s / q[v * 8 + u]);
            }

        int diff = coef[0] - prevDc;
        WriteValue(bw, diff, dc, 0);
        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int c = coef[ZigZag[k]];
            if (c == 0) { run++; continue; }
            while (run > 15) { bw.Write(ac.Codes[0xF0], ac.Sizes[0xF0]); run -= 16; }
            WriteValue(bw, c, ac, run);
            run = 0;
        }
        if (run > 0) bw.Write(ac.Codes[0x00], ac.Sizes[0x00]);
        return coef[0];
    }

    /// <summary>(run, 크기 분류) 기호 + 값 비트(음수는 1의 보수).</summary>
    private static void WriteValue(BitWriter bw, int value, HuffTable table, int run)
    {
        int abs = Math.Abs(value);
        int size = 0;
        while (abs >> size != 0) size++;
        int symbol = (run << 4) | size;
        bw.Write(table.Codes[symbol], table.Sizes[symbol]);
        if (size > 0) bw.Write(value < 0 ? value + (1 << size) - 1 : value, size);
    }

    private static void Marker(Stream s, byte m) { s.WriteByte(0xFF); s.WriteByte(m); }

    private static void Segment(Stream s, byte m, byte[] data)
    {
        Marker(s, m);
        int len = data.Length + 2;
        s.WriteByte((byte)(len >> 8));
        s.WriteByte((byte)len);
        s.Write(data);
    }
}
