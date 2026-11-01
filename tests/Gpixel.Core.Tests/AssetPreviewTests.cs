using Gpixel.Core.Assets;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

public class AssetPreviewTests
{
    [Fact]
    public void SampleSprite_UsesProvidedTransparentPixelChick()
    {
        var sprite = AssetPreview.SampleSprite();

        Assert.Equal(32, sprite.Width);
        Assert.Equal(32, sprite.Height);
        Assert.Equal(0, sprite[0, 0].A);
        Assert.Equal(0, sprite[31, 31].A);
        var body = sprite[16, 17];
        Assert.Equal(255, body.A);
        Assert.True(body.R > 220 && body.G > 170 && body.B < 130);
        Assert.Contains(sprite.Pixels, p => p.A > 0 && p.R < 60 && p.G < 60 && p.B < 60); // eyes/outline
        Assert.Contains(sprite.Pixels, p => p.A > 0 && p.R > 180 && p.G < 160 && p.B < 85); // beak/feet

        sprite[16, 17] = Color32.Transparent;
        Assert.Equal(body, AssetPreview.SampleSprite()[16, 17]);
    }
}
