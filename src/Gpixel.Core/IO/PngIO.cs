using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Gpixel.Core.Model;
using Gpixel.Core.Localization;

namespace Gpixel.Core.IO;

/// <summary>
/// 순수 C# PNG 읽기/쓰기. 모든 표준 PNG(회색·RGB·팔레트·알파, 1~16비트, 인터레이스)를 RGBA로 읽고,
/// 8비트 RGBA PNG로 무손실 저장한다. 감마(gAMA) 등 색 보정은 적용하지 않아 픽셀 값이 그대로 유지된다.
/// </summary>
public static class PngIO
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static PixelImage Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Load(fs);
    }

    /// <summary>PNG 스트림을 읽는다. 형식이 잘못되면 <see cref="InvalidDataException"/>.</summary>
    public static PixelImage Load(Stream stream)
    {
        var header = ReadChunks(stream, readData: true);
        return Decode(header);
    }

    /// <summary>픽셀을 읽지 않고 크기만 읽는다(썸네일 목록 등).</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        using var fs = File.OpenRead(path);
        var h = ReadChunks(fs, readData: false);
        return (h.Width, h.Height);
    }

    /// <summary>
    /// 8비트 RGBA PNG로 저장한다. 같은 폴더의 임시 파일에 먼저 쓴 뒤 바꿔치기하므로
    /// 실패해도 기존 파일이 깨지지 않는다.
    /// </summary>
    public static void Save(PixelImage image, string path)
    {
        var full = Path.GetFullPath(path);
        var temp = full + ".gpixel-tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                Save(image, fs);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void Save(PixelImage image, Stream stream) => Save(image, stream, null);

    /// <summary>
    /// 거른 데이터가 이보다 크면 줄 묶음(<see cref="ChunkBytes"/>)마다 나눠 여러 코어에서 거르고 압축한다. 묶음마다 압축 사전이 새로 시작해
    /// 파일이 아주 조금 커지는 대신, 큰 시트(8k×8k)를 여러 배 빨리 쓴다. 이보다 작은 그림(셀 대부분)은 한 번에 압축해 예전과 같은 바이트를 쓴다.
    /// </summary>
    private const int ParallelMinBytes = 8 << 20;

    /// <summary>나눠 압축할 때 묶음 하나의 거른 데이터 크기(대략).</summary>
    private const int ChunkBytes = 2 << 20;

    /// <param name="stage">진행(단위: 픽셀, 모두 <c>Width × Height</c>).</param>
    internal static void Save(PixelImage image, Stream stream, WorkStage? stage)
    {
        int w = image.Width, h = image.Height, stride = w * 4;
        stream.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filter
        ihdr[12] = 0; // no interlace
        WriteChunk(stream, "IHDR", ihdr);

        int rowBytes = stride + 1;
        if ((long)rowBytes * h <= ParallelMinBytes)
        {
            using var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var prev = new byte[stride];
                var cur = new byte[stride];
                var candidate = new byte[stride];
                var best = new byte[rowBytes];
                for (int y = 0; y < h; y++)
                {
                    FilterRow(image.Pixels, w, y, prev, cur, candidate, best);
                    z.Write(best);
                    (prev, cur) = (cur, prev);
                    stage?.Step(w);
                }
            }
            WriteChunk(stream, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }
        else WriteChunk(stream, "IDAT", CompressParallel(image, rowBytes, stage));
        WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// 줄 묶음마다 따로 거르고 raw deflate로 압축해 이어 붙인 zlib 데이터. 마지막이 아닌 묶음은 동기 flush(바이트 경계, 끝 블록 아님)에서 끊어
    /// 이어 붙여도 하나의 deflate 흐름이 되고, Adler-32는 묶음마다 구해 합친다. 필터는 줄마다 그 줄과 윗줄 원본만 보므로 나눠도 같다.
    /// </summary>
    private static byte[] CompressParallel(PixelImage image, int rowBytes, WorkStage? stage)
    {
        int w = image.Width, h = image.Height, stride = w * 4;
        int rowsPerChunk = Math.Max(1, ChunkBytes / rowBytes);
        int chunks = (h + rowsPerChunk - 1) / rowsPerChunk;
        var parts = new byte[chunks][];
        var adlers = new uint[chunks];
        Parallel.For(0, chunks, c =>
        {
            int y0 = c * rowsPerChunk, y1 = Math.Min(h, y0 + rowsPerChunk), length = (y1 - y0) * rowBytes;
            var filtered = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var prev = new byte[stride];
                var cur = new byte[stride];
                var candidate = new byte[stride];
                if (y0 > 0) RawRow(image.Pixels, w, y0 - 1, prev);
                for (int y = y0; y < y1; y++)
                {
                    FilterRow(image.Pixels, w, y, prev, cur, candidate, filtered.AsSpan((y - y0) * rowBytes, rowBytes));
                    (prev, cur) = (cur, prev);
                }
                adlers[c] = Adler32(filtered.AsSpan(0, length));
                using var ms = new MemoryStream(length / 4 + 64);
                var deflate = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true);
                deflate.Write(filtered, 0, length);
                if (c < chunks - 1)
                {
                    deflate.Flush(); // 동기 flush: 끝 블록 없이 바이트 경계까지 내보낸다
                    parts[c] = ms.ToArray();
                    deflate.Dispose(); // 끝 블록은 버린다(이미 복사함)
                }
                else
                {
                    deflate.Dispose();
                    parts[c] = ms.ToArray();
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(filtered);
            }
            stage?.Step((long)(y1 - y0) * w);
        });

        uint adler = adlers[0];
        for (int c = 1; c < chunks; c++)
        {
            int rows = Math.Min(h, (c + 1) * rowsPerChunk) - c * rowsPerChunk;
            adler = Adler32Combine(adler, adlers[c], (long)rows * rowBytes);
        }
        var result = new byte[2 + parts.Sum(p => (long)p.Length) + 4];
        result[0] = 0x78; // deflate, 32K 창
        result[1] = 0xDA; // 최대 압축(헤더 검사값 포함)
        int at = 2;
        foreach (var p in parts)
        {
            p.CopyTo(result, at);
            at += p.Length;
        }
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at), adler);
        return result;
    }

    /// <summary><paramref name="y"/>행 원본 RGBA 바이트.</summary>
    private static void RawRow(Color32[] px, int w, int y, byte[] row)
    {
        int start = y * w;
        for (int x = 0; x < w; x++)
        {
            var c = px[start + x];
            int i = x * 4;
            row[i] = c.R; row[i + 1] = c.G; row[i + 2] = c.B; row[i + 3] = c.A;
        }
    }

    /// <summary>
    /// <paramref name="y"/>행을 거른 줄(맨 앞 1바이트 = 필터 종류)을 <paramref name="dest"/>에 쓴다. 행마다 절댓값 합이 가장 작은 필터를 고른다(표준 휴리스틱).
    /// <paramref name="prev"/> = 윗줄 원본(첫 줄이면 0), <paramref name="cur"/>에는 이 줄 원본이 남는다.
    /// </summary>
    private static void FilterRow(Color32[] px, int w, int y, byte[] prev, byte[] cur, byte[] candidate, Span<byte> dest)
    {
        RawRow(px, w, y, cur);
        long bestScore = long.MaxValue;
        for (byte f = 0; f <= 4; f++)
        {
            long score = ApplyFilter(f, cur, prev, candidate, 4);
            if (score >= bestScore) continue;
            bestScore = score;
            dest[0] = f;
            candidate.CopyTo(dest[1..]);
        }
    }

    private const uint AdlerMod = 65521;

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const int NMax = 5552; // 이만큼 더해도 uint가 넘치지 않는다
        uint a = 1, b = 0;
        while (data.Length > 0)
        {
            int n = Math.Min(NMax, data.Length);
            foreach (var v in data[..n])
            {
                a += v;
                b += a;
            }
            a %= AdlerMod;
            b %= AdlerMod;
            data = data[n..];
        }
        return (b << 16) | a;
    }

    /// <summary>앞 조각의 Adler-32와 뒤 조각(길이 <paramref name="length2"/>)의 Adler-32로 이어 붙인 데이터의 값(zlib adler32_combine).</summary>
    private static uint Adler32Combine(uint adler1, uint adler2, long length2)
    {
        ulong rem = (ulong)(length2 % AdlerMod);
        ulong sum1 = adler1 & 0xffff;
        ulong sum2 = rem * sum1 % AdlerMod;
        sum1 += (adler2 & 0xffff) + AdlerMod - 1;
        sum2 += ((adler1 >> 16) & 0xffff) + ((adler2 >> 16) & 0xffff) + AdlerMod - rem;
        if (sum1 >= AdlerMod) sum1 -= AdlerMod;
        if (sum1 >= AdlerMod) sum1 -= AdlerMod;
        if (sum2 >= (ulong)AdlerMod << 1) sum2 -= (ulong)AdlerMod << 1;
        if (sum2 >= AdlerMod) sum2 -= AdlerMod;
        return (uint)(sum1 | (sum2 << 16));
    }

    // ───────────── 쓰기 도우미 ─────────────

    private static long ApplyFilter(byte filter, byte[] cur, byte[] prev, byte[] output, int bpp)
    {
        long score = 0;
        for (int i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0;
            int b = prev[i];
            int c = i >= bpp ? prev[i - bpp] : 0;
            int v = filter switch
            {
                0 => cur[i],
                1 => cur[i] - a,
                2 => cur[i] - b,
                3 => cur[i] - ((a + b) >> 1),
                _ => cur[i] - Paeth(a, b, c),
            };
            var bv = (byte)v;
            output[i] = bv;
            score += (sbyte)bv < 0 ? -(sbyte)bv : bv;
        }
        return score;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        Encoding.ASCII.GetBytes(type, buf[4..]);
        s.Write(buf);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, buf[4..8]), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf[..4]);
    }

    // ───────────── 읽기 ─────────────

    internal sealed class PngData
    {
        public int Width, Height, BitDepth, ColorType, Interlace;
        public byte[]? Palette;      // RGB 3바이트씩
        public byte[]? PaletteAlpha; // tRNS(팔레트)
        public ushort[]? TransparentKey; // tRNS(회색 1개 / RGB 3개, 16비트 원값)
        public MemoryStream Idat = new();
    }

    private static PngData ReadChunks(Stream stream, bool readData)
    {
        Span<byte> sig = stackalloc byte[8];
        ReadExactly(stream, sig);
        if (!sig.SequenceEqual(Signature)) throw new InvalidDataException(CoreStrings.Png_InvalidFile);

        var d = new PngData();
        bool sawHeader = false;
        Span<byte> head = stackalloc byte[8];
        while (true)
        {
            if (!TryReadExactly(stream, head)) break; // IEND 없이 끝난 파일도 허용
            int length = BinaryPrimitives.ReadInt32BigEndian(head);
            if (length < 0) throw new InvalidDataException(CoreStrings.Png_InvalidChunkLength);
            string type = Encoding.ASCII.GetString(head[4..8]);
            if (type == "IEND") break;

            bool needed = type is "IHDR" or "PLTE" or "tRNS" || (type == "IDAT" && readData);
            if (!needed)
            {
                Skip(stream, length + 4L);
                continue;
            }
            var data = new byte[length];
            ReadExactly(stream, data);
            Skip(stream, 4); // CRC

            switch (type)
            {
                case "IHDR":
                    if (length < 13) throw new InvalidDataException(CoreStrings.Png_InvalidHeader);
                    d.Width = BinaryPrimitives.ReadInt32BigEndian(data);
                    d.Height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                    d.BitDepth = data[8];
                    d.ColorType = data[9];
                    d.Interlace = data[12];
                    sawHeader = true;
                    if (!readData) return d;
                    break;
                case "PLTE":
                    d.Palette = data;
                    break;
                case "tRNS":
                    ReadTransparency(d, data);
                    break;
                case "IDAT":
                    d.Idat.Write(data);
                    break;
            }
        }
        if (!sawHeader) throw new InvalidDataException(CoreStrings.Png_HeaderMissing);
        return d;
    }

    /// <summary>tRNS 내용을 색 형식에 맞게 담는다(IHDR 다음에).</summary>
    internal static void ReadTransparency(PngData d, byte[] data)
    {
        if (d.ColorType == 3) d.PaletteAlpha = data;
        else if (d.ColorType == 0 && data.Length >= 2)
            d.TransparentKey = [BinaryPrimitives.ReadUInt16BigEndian(data)];
        else if (d.ColorType == 2 && data.Length >= 6)
            d.TransparentKey =
            [
                BinaryPrimitives.ReadUInt16BigEndian(data),
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)),
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4)),
            ];
    }

    internal static PixelImage Decode(PngData d)
    {
        int channels = d.ColorType switch
        {
            0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4,
            _ => throw new InvalidDataException(CoreStrings.FormatPng_UnsupportedColorType(colorType: d.ColorType)),
        };
        if (d.BitDepth is not (1 or 2 or 4 or 8 or 16))
            throw new InvalidDataException(CoreStrings.FormatPng_UnsupportedBitDepth(bitDepth: d.BitDepth));
        if (d.ColorType == 3 && d.Palette is null)
            throw new InvalidDataException(CoreStrings.Png_PaletteMissing);
        if (d.Width < 1 || d.Height < 1 || d.Width > PixelImage.MaxSize || d.Height > PixelImage.MaxSize)
            throw new InvalidDataException(CoreStrings.FormatPng_ImageTooLarge(width: d.Width, height: d.Height));

        d.Idat.Position = 0;
        using var z = new ZLibStream(d.Idat, CompressionMode.Decompress);
        var image = new PixelImage(d.Width, d.Height);
        int bitsPerPixel = channels * d.BitDepth;
        int bpp = Math.Max(1, bitsPerPixel / 8);

        if (d.Interlace == 0)
        {
            DecodePass(z, d, image, 0, 0, 1, 1, d.Width, d.Height, bitsPerPixel, bpp);
        }
        else
        {
            ReadOnlySpan<(int xs, int ys, int dx, int dy)> passes =
                [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
            foreach (var (xs, ys, dx, dy) in passes)
            {
                int pw = (d.Width - xs + dx - 1) / dx;
                int ph = (d.Height - ys + dy - 1) / dy;
                if (pw <= 0 || ph <= 0) continue;
                DecodePass(z, d, image, xs, ys, dx, dy, pw, ph, bitsPerPixel, bpp);
            }
        }
        return image;
    }

    private static void DecodePass(Stream z, PngData d, PixelImage image, int xs, int ys, int dx, int dy,
        int pw, int ph, int bitsPerPixel, int bpp)
    {
        int stride = (pw * bitsPerPixel + 7) / 8;
        var prev = new byte[stride];
        var cur = new byte[stride];
        Span<byte> filterByte = stackalloc byte[1];
        for (int row = 0; row < ph; row++)
        {
            ReadExactly(z, filterByte);
            ReadExactly(z, cur);
            Unfilter(filterByte[0], cur, prev, bpp);
            int y = ys + row * dy;
            for (int col = 0; col < pw; col++)
                image.Pixels[y * image.Width + xs + col * dx] = ReadPixel(d, cur, col);
            (prev, cur) = (cur, prev);
        }
    }

    private static void Unfilter(byte filter, byte[] cur, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1:
                for (int i = bpp; i < cur.Length; i++) cur[i] += cur[i - bpp];
                break;
            case 2:
                for (int i = 0; i < cur.Length; i++) cur[i] += prev[i];
                break;
            case 3:
                for (int i = 0; i < cur.Length; i++)
                    cur[i] += (byte)(((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1);
                break;
            case 4:
                for (int i = 0; i < cur.Length; i++)
                    cur[i] += (byte)Paeth(i >= bpp ? cur[i - bpp] : 0, prev[i], i >= bpp ? prev[i - bpp] : 0);
                break;
            default:
                throw new InvalidDataException(CoreStrings.FormatPng_InvalidFilter(filter: filter));
        }
    }

    private static Color32 ReadPixel(PngData d, byte[] row, int col)
    {
        int depth = d.BitDepth;
        switch (d.ColorType)
        {
            case 0:
            {
                int raw = Sample(row, col, depth);
                byte g = Scale(raw, depth);
                byte a = d.TransparentKey is { } k && k[0] == raw ? (byte)0 : (byte)255;
                return new Color32(g, g, g, a);
            }
            case 2:
            {
                int r = Sample(row, col * 3, depth), g = Sample(row, col * 3 + 1, depth), b = Sample(row, col * 3 + 2, depth);
                byte a = d.TransparentKey is { Length: 3 } k && k[0] == r && k[1] == g && k[2] == b ? (byte)0 : (byte)255;
                return new Color32(Scale(r, depth), Scale(g, depth), Scale(b, depth), a);
            }
            case 3:
            {
                int idx = Sample(row, col, depth);
                var pal = d.Palette!;
                if (idx * 3 + 2 >= pal.Length) return Color32.Transparent;
                byte a = d.PaletteAlpha is { } pa && idx < pa.Length ? pa[idx] : (byte)255;
                return new Color32(pal[idx * 3], pal[idx * 3 + 1], pal[idx * 3 + 2], a);
            }
            case 4:
            {
                byte g = Scale(Sample(row, col * 2, depth), depth);
                return new Color32(g, g, g, Scale(Sample(row, col * 2 + 1, depth), depth));
            }
            default:
                return new Color32(
                    Scale(Sample(row, col * 4, depth), depth),
                    Scale(Sample(row, col * 4 + 1, depth), depth),
                    Scale(Sample(row, col * 4 + 2, depth), depth),
                    Scale(Sample(row, col * 4 + 3, depth), depth));
        }
    }

    /// <summary>행에서 n번째 표본(채널 값)을 원래 비트 깊이 그대로 꺼낸다.</summary>
    private static int Sample(byte[] row, int index, int depth)
    {
        switch (depth)
        {
            case 8: return row[index];
            case 16: return (row[index * 2] << 8) | row[index * 2 + 1];
            default:
                int bit = index * depth;
                int shift = 8 - depth - (bit & 7);
                return (row[bit >> 3] >> shift) & ((1 << depth) - 1);
        }
    }

    private static byte Scale(int value, int depth) => depth switch
    {
        8 => (byte)value,
        16 => (byte)(value >> 8),
        _ => (byte)(value * 255 / ((1 << depth) - 1)),
    };

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        if (!TryReadExactly(s, buffer)) throw new InvalidDataException(CoreStrings.Png_Truncated);
    }

    private static bool TryReadExactly(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer[total..]);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    private static void Skip(Stream s, long count)
    {
        if (s.CanSeek)
        {
            s.Seek(count, SeekOrigin.Current);
            return;
        }
        Span<byte> tmp = stackalloc byte[4096];
        while (count > 0)
        {
            int n = s.Read(tmp[..(int)Math.Min(tmp.Length, count)]);
            if (n == 0) return;
            count -= n;
        }
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
