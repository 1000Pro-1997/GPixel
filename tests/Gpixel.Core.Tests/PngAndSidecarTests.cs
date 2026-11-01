using System.Drawing;
using System.IO.Compression;
using System.Buffers.Binary;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

public class PngAndSidecarTests
{
    [Fact]
    public void Png_RoundTrip_IsLossless_IncludingTransparentRgb()
    {
        var img = TestUtil.RandomImage(37, 23);
        img.Set(0, 0, new Color32(10, 20, 30, 0)); // 완전 투명이어도 RGB 보존
        using var ms = new MemoryStream();
        PngIO.Save(img, ms);
        ms.Position = 0;
        var back = PngIO.Load(ms);
        Assert.True(img.ContentEquals(back));
    }

    [Fact]
    public void Png_RoundTrip_LargeImage_CompressedInParallelChunks_IsLossless()
    {
        // 거른 데이터가 8MiB를 넘어 줄 묶음마다 나눠 압축하는 크기(묶음 경계가 줄 중간이 아닌 곳, 마지막 묶음은 짧음).
        var img = TestUtil.RandomImage(1500, 1501, seed: 3);
        using var ms = new MemoryStream();
        PngIO.Save(img, ms);
        ms.Position = 0;
        Assert.True(img.ContentEquals(PngIO.Load(ms)));
    }

    [Fact]
    public void Png_SaveToFile_ThenReadSizeAndLoad()
    {
        using var dir = new TempDir();
        var path = dir.File("a.png");
        var img = TestUtil.RandomImage(5, 9, seed: 7);
        PngIO.Save(img, path);
        Assert.Equal((5, 9), PngIO.ReadSize(path));
        Assert.True(img.ContentEquals(PngIO.Load(path)));
        Assert.False(File.Exists(path + ".gpixel-tmp"));
    }

    [Fact]
    public void Png_LoadsUnityFixtures()
    {
        var player = PngIO.Load(TestUtil.Fixture("player@2x.png"));
        Assert.Equal(32, player.Width);
        Assert.Equal(32, player.Height);
        var fantasy = PngIO.Load(TestUtil.Fixture("fantasy-8x.png"));
        Assert.Equal(256, fantasy.Width);
        Assert.Equal(8, fantasy.Height);
        Assert.False(player.IsRegionEmpty(player.Bounds));
    }

    [Fact]
    public void Png_DecodesPalettedAndInterlacedImages()
    {
        // 2비트 팔레트 + tRNS, 인터레이스(Adam7) PNG를 직접 만들어 읽는다.
        const int w = 5, h = 3;
        int[,] idx = { { 0, 1, 2, 3, 0 }, { 1, 2, 3, 0, 1 }, { 3, 3, 2, 1, 0 } };
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
        byte[] alpha = [0, 128];
        var raw = new MemoryStream();
        (int xs, int ys, int dx, int dy)[] passes =
            [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        foreach (var (xs, ys, dx, dy) in passes)
        {
            for (int y = ys; y < h; y += dy)
            {
                var cols = new List<int>();
                for (int x = xs; x < w; x += dx) cols.Add(idx[y, x]);
                if (cols.Count == 0) continue;
                var row = new byte[(cols.Count * 2 + 7) / 8];
                for (int i = 0; i < cols.Count; i++) row[i / 4] |= (byte)(cols[i] << (6 - (i % 4) * 2));
                raw.WriteByte(0);
                raw.Write(row);
            }
        }
        var png = BuildPng(w, h, bitDepth: 2, colorType: 3, interlace: 1, raw.ToArray(),
            ("PLTE", palette), ("tRNS", alpha));
        var img = PngIO.Load(new MemoryStream(png));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = idx[y, x];
                byte a = i < alpha.Length ? alpha[i] : (byte)255;
                Assert.Equal(new Color32(palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2], a), img.Get(x, y));
            }
    }

    [Fact]
    public void Png_Decodes16BitGrayAlpha()
    {
        // 16비트 회색+알파, 필터 1(Sub)
        var raw = new byte[1 + 8];
        raw[0] = 1;
        byte[] samples = [0x12, 0x34, 0xFF, 0xFF, 0x80, 0x00, 0x00, 0x00];
        for (int i = 0; i < 8; i++) raw[1 + i] = (byte)(samples[i] - (i >= 4 ? samples[i - 4] : 0));
        var png = BuildPng(2, 1, bitDepth: 16, colorType: 4, interlace: 0, raw);
        var img = PngIO.Load(new MemoryStream(png));
        Assert.Equal(new Color32(0x12, 0x12, 0x12, 0xFF), img.Get(0, 0));
        Assert.Equal(new Color32(0x80, 0x80, 0x80, 0x00), img.Get(1, 0));
    }

    [Fact]
    public void Png_RejectsNonPng()
        => Assert.Throws<InvalidDataException>(() => PngIO.Load(new MemoryStream("not a png"u8.ToArray())));

    private static byte[] BuildPng(int w, int h, byte bitDepth, byte colorType, byte interlace, byte[] raw,
        params (string Type, byte[] Data)[] extra)
    {
        var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[12] = interlace;
        Chunk(ms, "IHDR", ihdr);
        foreach (var (t, d) in extra) Chunk(ms, t, d);
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            s.Write(len);
            s.Write(System.Text.Encoding.ASCII.GetBytes(type));
            s.Write(data);
            s.Write(new byte[4]); // CRC는 읽을 때 검사하지 않는다
        }
    }

    [Fact]
    public void Sidecar_ReadsSpecExample()
    {
        const string json = """
            {
              "version": 1,
              "spriteMode": "Multiple",
              "pixelsPerUnit": 16,
              "defaultPivot": { "x": 0.5, "y": 0.0 },
              "slices": [
                { "name": "hero_0", "x": 0, "y": 0, "w": 16, "h": 32, "pivot": { "x": 0.5, "y": 0.0 }, "border": [0,0,0,0] }
              ],
              "animations": [
                { "name": "walk_down", "loop": true, "frames": [ { "slice": "hero_0", "ms": 100 } ] }
              ],
              "grid": { "cellW": 16, "cellH": 32, "offsetX": 0, "offsetY": 0, "padX": 0, "padY": 0 }
            }
            """;
        var doc = Document.CreateNew(16, 32);
        SidecarIO.Deserialize(json, doc);
        Assert.Equal(SpriteMode.Multiple, doc.Sprite.Mode);
        Assert.Equal(Pivot.BottomCenter, doc.Slices[0].Pivot);
        Assert.Equal(100, doc.Animations[0].Frames[0].Ms);
        Assert.Equal(32, doc.Grid.CellH);
    }

    [Fact]
    public void Palette_HexAndGpl()
    {
        var colors = new List<Color32> { new(255, 0, 0), new(0, 128, 255, 128) };
        var text = PaletteIO.ToHex(colors);
        Assert.Equal("FF0000\n0080FF80\n", text);
        Assert.Equal(colors, PaletteIO.ParseHex("; comment\n#FF0000\n0080ff80\n\nzzz\n"));
        var gpl = "GIMP Palette\nName: test\nColumns: 4\n#\n255   0   0\tRed\n  0 255   0 Green\n";
        Assert.Equal([new Color32(255, 0, 0), new Color32(0, 255, 0)], PaletteIO.ParseGpl(gpl));

        var img = new PixelImage(3, 1);
        img.Set(0, 0, new Color32(1, 2, 3));
        img.Set(1, 0, new Color32(1, 2, 3));
        img.Set(2, 0, new Color32(9, 9, 9));
        Assert.Equal([new Color32(1, 2, 3), new Color32(9, 9, 9)], PaletteIO.ExtractFromImage(img));
    }

    [Fact]
    public void Color_HexAndHsv()
    {
        Assert.True(Color32.TryParseHex("#12345678", out var c));
        Assert.Equal(new Color32(0x12, 0x34, 0x56, 0x78), c);
        Assert.True(Color32.TryParseHex("abc", out var s));
        Assert.Equal(new Color32(0xAA, 0xBB, 0xCC), s);
        var (h, sat, v) = new Color32(255, 0, 0).ToHsv();
        Assert.Equal((0f, 1f, 1f), (h, sat, v));
        Assert.Equal(new Color32(0, 255, 0), Color32.FromHsv(120, 1, 1));
        var orig = new Color32(37, 140, 201);
        var hsv = orig.ToHsv();
        Assert.Equal(orig, Color32.FromHsv(hsv.H, hsv.S, hsv.V));
    }
}
