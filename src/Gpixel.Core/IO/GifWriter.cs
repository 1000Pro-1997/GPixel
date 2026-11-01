using System.Buffers.Binary;
using Gpixel.Core.Model;

namespace Gpixel.Core.IO;

/// <summary>애니메이션 GIF의 프레임 하나: 그림(RGBA)과 보이는 시간(ms).</summary>
public sealed record GifFrame(PixelImage Image, int DurationMs);

/// <summary>GIF 쓰기 설정.</summary>
public sealed record GifOptions
{
    /// <summary>정수 배율(1~<see cref="MaxScale"/>, 최근접 — 픽셀 그대로 키움).</summary>
    public int Scale { get; init; } = 1;

    /// <summary>재생 횟수. 0 = 무한 반복, 1 = 한 번(반복 확장 없음), N = N번 재생.</summary>
    public int PlayCount { get; init; }

    /// <summary>배경색. null이면 투명(알파 &lt; 128 → 투명 색), 있으면 그 색 위에 합성해 불투명하게.</summary>
    public Color32? Background { get; init; }

    public const int MaxScale = 64;
}

/// <summary>쓴 GIF 요약: 크기, 프레임 수, 전체 길이(ms, 실제 쓴 지연 기준), 색 수(전역 팔레트면 그 색 수, 아니면 프레임 최대), 무손실 전역 팔레트 여부, 투명 사용 여부.</summary>
public sealed record GifInfo(int Width, int Height, int FrameCount, int TotalMs, int Colors, bool ExactPalette, bool Transparent);

/// <summary>
/// 애니메이션 GIF89a 쓰기(순수 C#).
/// <list type="bullet">
/// <item>팔레트: 모든 프레임의 불투명 색이 255개 이하(투명이 없으면 256개)면 전역 팔레트 하나로 무손실.
/// 넘으면 프레임마다 지역 팔레트 — 그 프레임 색이 넘치지 않으면 그대로, 넘치면 median cut으로 줄여 가장 가까운 색(디더링 없음).</item>
/// <item>투명: 배경색이 없으면 알파 &lt; 128은 투명 색, 나머지는 불투명(RGB 그대로). 투명이 있으면 처리 방법 2(배경으로 되돌림)로 프레임이 겹쳐 남지 않게,
/// 없으면 1(그대로 둠).</item>
/// <item>지연: 1/100초 단위. 누적 시간을 반올림해 프레임마다 나눠 전체 길이가 어긋나지 않게 하고, 브라우저가 더 짧은 값을 느리게 바꾸므로 최소 2(20ms).</item>
/// <item>반복: NETSCAPE2.0 확장(0 = 무한). 한 번만 재생이면 확장을 쓰지 않는다.</item>
/// <item>LZW: 코드 길이를 12비트까지 늘리고, 표가 차면(4096) 지우기 코드로 다시 시작.</item>
/// </list>
/// 임시 파일에 쓴 뒤 바꿔치기한다(<see cref="ImageWriter"/>와 같음).
/// </summary>
public static class GifWriter
{
    /// <summary>브라우저가 이보다 짧은 지연을 느리게(보통 100ms) 바꾸므로 쓰는 최소 지연(1/100초).</summary>
    public const int MinDelayCs = 2;

    /// <param name="progress">진행(프레임마다 한 몫).</param>
    public static GifInfo Save(IReadOnlyList<GifFrame> frames, string path, GifOptions options, WorkProgress? progress = null)
    {
        var full = Path.GetFullPath(path);
        var temp = full + ".gpixel-tmp";
        try
        {
            GifInfo info;
            using (var stage = progress?.Begin(frames.Count))
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                info = Write(frames, fs, options, stage);
            File.Move(temp, full, overwrite: true);
            return info;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static GifInfo Write(IReadOnlyList<GifFrame> frames, Stream stream, GifOptions options) => Write(frames, stream, options, null);

    private static GifInfo Write(IReadOnlyList<GifFrame> frames, Stream stream, GifOptions options, WorkStage? stage)
    {
        if (frames.Count == 0) throw new ArgumentException(Gpixel.Core.Localization.DomainStrings.Gif_NoFrames, nameof(frames));
        int w = frames[0].Image.Width, h = frames[0].Image.Height;
        if (frames.Any(f => f.Image.Width != w || f.Image.Height != h)) throw new ArgumentException(Gpixel.Core.Localization.DomainStrings.Gif_FrameSizeMismatch, nameof(frames));
        int scale = Math.Clamp(options.Scale, 1, GifOptions.MaxScale);
        int sw = w * scale, sh = h * scale;
        if (sw > ushort.MaxValue || sh > ushort.MaxValue) throw new ArgumentException(Gpixel.Core.Localization.DomainStrings.FormatGif_SizeTooLarge(max: ushort.MaxValue, width: sw, height: sh), nameof(options));

        // 1) 프레임마다 불투명 색(배경 합성 또는 알파 문턱)으로 정리: 0 = 투명, 나머지는 0xFF RRGGBB.
        var pixels = frames.Select(f => Flatten(f.Image, options.Background)).ToList();
        bool transparent = pixels.Any(p => Array.IndexOf(p, 0u) >= 0);
        int limit = transparent ? 255 : 256;
        var all = new HashSet<uint>();
        foreach (var p in pixels)
        {
            foreach (var c in p)
                if (c != 0) all.Add(c);
            if (all.Count > limit) break;
        }
        bool exact = all.Count <= limit;
        uint[]? global = exact ? BuildExactPalette(all, transparent) : null;

        var delays = DelaysCs(frames.Select(f => f.DurationMs));
        var bw = new BinaryWriter(stream);
        bw.Write("GIF89a"u8);
        WriteU16(bw, sw);
        WriteU16(bw, sh);
        if (global is not null)
        {
            bw.Write((byte)(0x80 | 0x70 | (TableBits(global.Length) - 1))); // 전역 팔레트, 색 해상도 8비트
            bw.Write((byte)0); // 배경 색 번호(투명이면 투명 색 = 0)
            bw.Write((byte)0);
            WriteTable(bw, global);
        }
        else
        {
            bw.Write((byte)0x70);
            bw.Write((byte)0);
            bw.Write((byte)0);
        }
        if (options.PlayCount != 1)
        {
            bw.Write((byte)0x21);
            bw.Write((byte)0xFF);
            bw.Write((byte)11);
            bw.Write("NETSCAPE2.0"u8);
            bw.Write((byte)3);
            bw.Write((byte)1);
            WriteU16(bw, options.PlayCount <= 0 ? 0 : Math.Min(options.PlayCount - 1, ushort.MaxValue));
            bw.Write((byte)0);
        }

        int maxColors = global?.Length ?? 0;
        var indices = new byte[w * h];
        for (int fi = 0; fi < frames.Count; fi++)
        {
            var px = pixels[fi];
            uint[] table;
            if (global is not null) table = global;
            else
            {
                var colors = new Dictionary<uint, int>();
                foreach (var c in px)
                    if (c != 0) colors[c] = colors.GetValueOrDefault(c) + 1;
                table = colors.Count <= limit ? BuildExactPalette(colors.Keys, transparent) : MedianCut(colors, limit, transparent);
                maxColors = Math.Max(maxColors, table.Length - (transparent ? 1 : 0));
            }
            MapIndices(px, table, transparent, indices);

            // 그래픽 제어 확장
            bw.Write((byte)0x21);
            bw.Write((byte)0xF9);
            bw.Write((byte)4);
            bw.Write((byte)((transparent ? 2 : 1) << 2 | (transparent ? 1 : 0)));
            WriteU16(bw, delays[fi]);
            bw.Write((byte)0); // 투명 색 번호
            bw.Write((byte)0);

            // 그림 설명자
            bw.Write((byte)0x2C);
            WriteU16(bw, 0);
            WriteU16(bw, 0);
            WriteU16(bw, sw);
            WriteU16(bw, sh);
            int bits = TableBits(table.Length);
            if (global is null)
            {
                bw.Write((byte)(0x80 | (bits - 1)));
                WriteTable(bw, table);
            }
            else bw.Write((byte)0);
            int minCodeSize = Math.Max(2, bits);
            bw.Write((byte)minCodeSize);
            bw.Flush();
            LzwEncode(Scaled(indices, w, h, scale), minCodeSize, stream);
            stage?.Step(1);
        }
        bw.Write((byte)0x3B);
        bw.Flush();
        if (global is not null) maxColors = all.Count;
        return new GifInfo(sw, sh, frames.Count, delays.Sum() * 10, maxColors, exact, transparent);
    }

    /// <summary>
    /// 프레임 길이(ms) → GIF 지연(1/100초). 누적 시간을 반올림해 그 차이를 쓰므로 전체 길이가 어긋나지 않고(최소값 때문에 늘어난 만큼은 뒤에서 덜 씀),
    /// 각 값은 <see cref="MinDelayCs"/> 이상 65535 이하.
    /// </summary>
    public static int[] DelaysCs(IEnumerable<int> durationsMs)
    {
        var list = new List<int>();
        long elapsedMs = 0, writtenCs = 0;
        foreach (var ms in durationsMs)
        {
            elapsedMs += Math.Max(0, ms);
            long target = (elapsedMs + 5) / 10;
            int d = (int)Math.Clamp(target - writtenCs, MinDelayCs, ushort.MaxValue);
            writtenCs += d;
            list.Add(d);
        }
        return [.. list];
    }

    /// <summary>GIF에 쓰일 그림(전역 팔레트일 때 그대로): 배경 합성 또는 알파 &lt; 128 투명, 나머지 불투명. 미리보기용.</summary>
    public static PixelImage Prepare(PixelImage image, Color32? background)
        => new(image.Width, image.Height, Array.ConvertAll(Flatten(image, background), c => new Color32(c)));

    /// <summary>알파·배경 처리 후 불투명 색(0xFF RRGGBB) 또는 투명(0).</summary>
    private static uint[] Flatten(PixelImage image, Color32? background)
    {
        var src = image.Pixels;
        var dst = new uint[src.Length];
        if (background is { } bg)
        {
            for (int i = 0; i < src.Length; i++)
            {
                var c = src[i];
                int a = c.A;
                if (a == 255) { dst[i] = c.Argb; continue; }
                int r = (c.R * a + bg.R * (255 - a) + 127) / 255;
                int g = (c.G * a + bg.G * (255 - a) + 127) / 255;
                int b = (c.B * a + bg.B * (255 - a) + 127) / 255;
                dst[i] = 0xFF000000u | (uint)(r << 16 | g << 8 | b);
            }
        }
        else
            for (int i = 0; i < src.Length; i++)
                dst[i] = src[i].A < 128 ? 0u : src[i].Argb | 0xFF000000u;
        return dst;
    }

    /// <summary>색 목록 그대로의 팔레트(투명이 있으면 0번이 투명 자리).</summary>
    private static uint[] BuildExactPalette(IEnumerable<uint> colors, bool transparent)
    {
        var list = new List<uint>();
        if (transparent) list.Add(0);
        list.AddRange(colors.Order());
        if (list.Count == 0) list.Add(0);
        return [.. list];
    }

    /// <summary>팔레트 칸 수를 담는 비트 수(1~8, 표 크기 = 2^비트).</summary>
    private static int TableBits(int count)
    {
        int bits = 1;
        while ((1 << bits) < count) bits++;
        return bits;
    }

    private static void WriteTable(BinaryWriter bw, uint[] table)
    {
        int size = 1 << TableBits(table.Length);
        for (int i = 0; i < size; i++)
        {
            uint c = i < table.Length ? table[i] : 0;
            bw.Write((byte)(c >> 16));
            bw.Write((byte)(c >> 8));
            bw.Write((byte)c);
        }
    }

    private static void WriteU16(BinaryWriter bw, int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
        bw.Write(b);
    }

    /// <summary>색 → 팔레트 번호(팔레트에 없으면 가장 가까운 색). 투명(0)은 0번.</summary>
    private static void MapIndices(uint[] px, uint[] table, bool transparent, byte[] indices)
    {
        var lookup = new Dictionary<uint, byte>();
        int start = transparent ? 1 : 0;
        for (int i = start; i < table.Length; i++) lookup.TryAdd(table[i], (byte)i);
        for (int i = 0; i < px.Length; i++)
        {
            uint c = px[i];
            if (c == 0 && transparent)
            {
                indices[i] = 0;
                continue;
            }
            if (!lookup.TryGetValue(c, out var idx))
            {
                idx = (byte)Nearest(table, start, c);
                lookup[c] = idx;
            }
            indices[i] = idx;
        }
    }

    private static int Nearest(uint[] table, int start, uint c)
    {
        int r = (byte)(c >> 16), g = (byte)(c >> 8), b = (byte)c;
        int best = start, bestD = int.MaxValue;
        for (int i = start; i < table.Length; i++)
        {
            uint t = table[i];
            int dr = r - (byte)(t >> 16), dg = g - (byte)(t >> 8), db = b - (byte)t;
            int d = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Median cut: 색(+개수)을 상자로 나눠 <paramref name="limit"/>개 이하의 평균색 팔레트(투명이면 0번 투명 자리 포함).</summary>
    private static uint[] MedianCut(Dictionary<uint, int> counts, int limit, bool transparent)
    {
        var colors = counts.Select(kv => (Color: kv.Key, Count: kv.Value)).ToArray();
        var boxes = new List<(int Start, int Length)> { (0, colors.Length) };
        static int Channel(uint c, int ch) => (byte)(c >> (16 - 8 * ch));
        (int Axis, int Range) Widest((int Start, int Length) box)
        {
            int axis = 0, range = -1;
            for (int ch = 0; ch < 3; ch++)
            {
                int lo = 255, hi = 0;
                for (int i = box.Start; i < box.Start + box.Length; i++)
                {
                    int v = Channel(colors[i].Color, ch);
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                if (hi - lo > range) { range = hi - lo; axis = ch; }
            }
            return (axis, range);
        }
        while (boxes.Count < limit)
        {
            // 가장 넓은(범위 × 픽셀 수가 큰) 상자를 나눈다.
            int pick = -1;
            long score = -1;
            int pickAxis = 0;
            for (int b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Length < 2) continue;
                var (axis, range) = Widest(boxes[b]);
                long pop = 0;
                for (int i = boxes[b].Start; i < boxes[b].Start + boxes[b].Length; i++) pop += colors[i].Count;
                long s = range * (long)Math.Sqrt(pop) + range;
                if (range > 0 && s > score) { score = s; pick = b; pickAxis = axis; }
            }
            if (pick < 0) break;
            var (start, length) = boxes[pick];
            int ax = pickAxis;
            Array.Sort(colors, start, length, Comparer<(uint Color, int Count)>.Create((x, y) => Channel(x.Color, ax).CompareTo(Channel(y.Color, ax))));
            long total = 0;
            for (int i = start; i < start + length; i++) total += colors[i].Count;
            long half = total / 2, acc = 0;
            int cut = start + 1;
            for (int i = start; i < start + length - 1; i++)
            {
                acc += colors[i].Count;
                cut = i + 1;
                if (acc >= half) break;
            }
            boxes[pick] = (start, cut - start);
            boxes.Add((cut, start + length - cut));
        }
        var palette = new List<uint>(boxes.Count + 1);
        if (transparent) palette.Add(0);
        foreach (var (start, length) in boxes)
        {
            long r = 0, g = 0, b = 0, n = 0;
            for (int i = start; i < start + length; i++)
            {
                var (c, k) = colors[i];
                r += (byte)(c >> 16) * (long)k;
                g += (byte)(c >> 8) * (long)k;
                b += (byte)c * (long)k;
                n += k;
            }
            palette.Add(0xFF000000u | (uint)((r + n / 2) / n) << 16 | (uint)((g + n / 2) / n) << 8 | (uint)((b + n / 2) / n));
        }
        return [.. palette];
    }

    private static byte[] Scaled(byte[] indices, int w, int h, int scale)
    {
        if (scale == 1) return indices;
        int sw = w * scale;
        var dst = new byte[sw * h * scale];
        for (int y = 0; y < h; y++)
        {
            int row = y * scale * sw;
            for (int x = 0; x < w; x++)
                dst.AsSpan(row + x * scale, scale).Fill(indices[y * w + x]);
            for (int k = 1; k < scale; k++)
                Array.Copy(dst, row, dst, row + k * sw, sw);
        }
        return dst;
    }

    private const int MaxCodes = 4096;
    private const int HashSize = 5003; // 4096개보다 넉넉한 소수(80% 이하로 참)

    /// <summary>GIF LZW 압축(가변 길이 코드, 표가 차면 지우기 코드) → 255바이트 이하 하위 블록들 + 끝 블록.</summary>
    internal static void LzwEncode(byte[] data, int minCodeSize, Stream output)
    {
        var blocks = new SubBlockWriter(output);
        int clear = 1 << minCodeSize, eoi = clear + 1;
        int codeSize = minCodeSize + 1, next = eoi + 1;
        var keys = new int[HashSize];
        var codes = new short[HashSize];
        Array.Fill(keys, -1);
        int bitBuf = 0, bitCount = 0;

        void Emit(int code)
        {
            bitBuf |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                blocks.Write((byte)bitBuf);
                bitBuf >>= 8;
                bitCount -= 8;
            }
        }

        Emit(clear);
        if (data.Length > 0)
        {
            int prefix = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                int c = data[i];
                int key = prefix << 8 | c;
                int h = (int)((uint)(key * 31 + c) % HashSize);
                while (keys[h] != -1 && keys[h] != key) h = h + 1 == HashSize ? 0 : h + 1;
                if (keys[h] == key)
                {
                    prefix = codes[h];
                    continue;
                }
                Emit(prefix);
                // 디코더는 이 코드를 읽은 뒤 표에 하나 더해 next가 2^codeSize가 되면 코드 길이를 늘린다 — 같은 때에 늘림.
                if (next == 1 << codeSize && codeSize < 12) codeSize++;
                if (next < MaxCodes)
                {
                    keys[h] = key;
                    codes[h] = (short)next++;
                }
                else
                {
                    Emit(clear);
                    Array.Fill(keys, -1);
                    codeSize = minCodeSize + 1;
                    next = eoi + 1;
                }
                prefix = c;
            }
            Emit(prefix);
            if (next == 1 << codeSize && codeSize < 12) codeSize++;
        }
        Emit(eoi);
        if (bitCount > 0) blocks.Write((byte)bitBuf);
        blocks.Finish();
    }

    private sealed class SubBlockWriter(Stream output)
    {
        private readonly byte[] _buf = new byte[256];
        private int _n;

        public void Write(byte b)
        {
            _buf[1 + _n++] = b;
            if (_n == 255) Flush();
        }

        private void Flush()
        {
            if (_n == 0) return;
            _buf[0] = (byte)_n;
            output.Write(_buf, 0, _n + 1);
            _n = 0;
        }

        public void Finish()
        {
            Flush();
            output.WriteByte(0);
        }
    }

    /// <summary>
    /// 프로젝트 애니메이션 → GIF 프레임: 태그면 재생 순서(정방향·역방향·왕복, <see cref="AnimationTag.GetPlaybackOrder"/>), 없으면 모든 프레임 순서대로.
    /// 그림은 프레임 시트와 똑같이 합성한다(<see cref="ProjectExport.CompositeFrame"/>: 레이어 효과, 지금 착장, 숨긴·내보내기에서 뺀 레이어 제외).
    /// </summary>
    /// <param name="progress">진행(합성할 프레임마다 한 몫).</param>
    public static List<GifFrame> ProjectFrames(SpriteProject project, AnimationTag? tag, WorkProgress? progress = null)
    {
        var order = tag?.GetPlaybackOrder() ?? Enumerable.Range(0, project.Frames.Count).ToList();
        var cache = new Dictionary<int, PixelImage>();
        var effects = ProjectExport.NewCache();
        var list = new List<GifFrame>(order.Count);
        using var stage = progress?.Begin(order.Distinct().Count());
        foreach (var fi in order)
        {
            if (fi < 0 || fi >= project.Frames.Count) continue;
            if (!cache.TryGetValue(fi, out var img))
            {
                cache[fi] = img = ProjectExport.CompositeFrame(project, fi, effects);
                stage?.Step(1);
            }
            list.Add(new GifFrame(img, project.Frames[fi].DurationMs));
        }
        return list;
    }
}
