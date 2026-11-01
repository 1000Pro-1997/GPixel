using System.Drawing;
using Gpixel.Core.Edit;
using Gpixel.Core.Localization;
using Gpixel.Core.Model;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

namespace Gpixel.Core.Scripting;

/// <summary>
/// 렌더 함수에 넘기는 host API(13.3). 그림은 호출마다 한 번에 넣고 한 번에 꺼낸다.
/// <para>
/// <c>img.data</c>는 RGBA 순서의 JS 배열(길이 = 너비 × 높이 × 4)이다. 7단계 첫 측정(256², Jint 4.17)에서 JS 배열이 typed array와
/// 픽셀마다 호스트 호출보다 빨랐다. 렌더가 끝나면 배열 값을 <c>Uint8ClampedArray</c> 규칙(NaN = 0, 0~255로 자르기, 가운데는 짝수로 반올림)으로
/// 그림에 되쓴다. 숫자가 아닌 값이나 길이 변경은 오류다.
/// </para>
/// </summary>
internal static class ScriptHostApi
{
    private const PropertyFlag Fixed = PropertyFlag.Enumerable; // 쓰기·지우기 불가, 열거 가능

    /// <summary>전역 도우미(<c>hsvToRgb</c>, <c>rgbToHsv</c>)를 넣고 그림 원형을 만든다. <c>lerp</c>는 JS로 잠금 단계에서 넣는다.</summary>
    public static ObjectInstance Install(ScriptSandbox sandbox)
    {
        var engine = sandbox.Engine;
        // Color32와 같은 계산(float)이라 기본 효과와 결과가 같다
        engine.SetValue("hsvToRgb", new ClrFunction(engine, "hsvToRgb", (_, args) =>
        {
            byte alpha = args.Length > 3 && !args[3].IsUndefined() ? ToByte(TypeConverter.ToNumber(args[3])) : (byte)255;
            var color = Color32.FromHsv((float)Number(args, 0), (float)Number(args, 1), (float)Number(args, 2), alpha);
            return ColorObject(engine, color, frozen: false);
        }, 3));
        engine.SetValue("rgbToHsv", new ClrFunction(engine, "rgbToHsv", (_, args) =>
        {
            var (h, s, v) = new Color32(ToByte(Number(args, 0)), ToByte(Number(args, 1)), ToByte(Number(args, 2))).ToHsv();
            var hsv = new JsObject(engine);
            hsv.CreateDataProperty("h", h);
            hsv.CreateDataProperty("s", s);
            hsv.CreateDataProperty("v", v);
            return hsv;
        }, 3));

        var prototype = new JsObject(engine);
        prototype.DefineOwnProperty("blank", new PropertyDescriptor(new ClrFunction(engine, "blank", (_, args) =>
        {
            int w = TypeConverter.ToInt32(Arg(args, 0)), h = TypeConverter.ToInt32(Arg(args, 1));
            if (w < 1 || h < 1 || w > PixelImage.MaxSize || h > PixelImage.MaxSize || (long)w * h * 4 > sandbox.Limits.MaxArrayLength)
                throw new JavaScriptException(engine.Intrinsics.TypeError,
                    DomainStrings.FormatScript_InvalidBlankSize(max: PixelImage.MaxSize, pixels: sandbox.Limits.MaxArrayLength / 4));
            var zero = JsNumber.Create(0);
            var values = new JsValue[w * h * 4];
            Array.Fill(values, zero);
            return Image(sandbox, w, h, new JsArray(engine, values));
        }, 2), Fixed));
        prototype.DefineOwnProperty("blendOver", new PropertyDescriptor(new ClrFunction(engine, "blendOver", (self, args) =>
        {
            BlendOver(engine, ReadImage(engine, self), ReadImage(engine, Arg(args, 0)),
                TypeConverter.ToInt32(Arg(args, 1)), TypeConverter.ToInt32(Arg(args, 2)));
            return self;
        }, 3), Fixed));
        prototype.PreventExtensions();
        return prototype;
    }

    /// <summary><paramref name="image"/>의 픽셀을 담은 <c>img</c>와 그 <c>data</c> 배열.</summary>
    public static (ObjectInstance Image, ArrayInstance Data) CreateImage(ScriptSandbox sandbox, PixelImage image)
    {
        var data = ToArray(sandbox.Engine, image);
        return (Image(sandbox, image.Width, image.Height, data), data);
    }

    /// <summary>렌더가 끝난 <paramref name="data"/>를 <paramref name="image"/>에 되쓴다. 값이 잘못되면 그림은 그대로 두고 오류.</summary>
    public static void CopyBack(ArrayInstance data, PixelImage image)
    {
        var px = image.Pixels;
        uint length = (uint)px.Length * 4;
        if (Length(data) != length) throw BadData(length, Length(data));
        var result = new Color32[px.Length];
        for (int i = 0; i < result.Length; i++)
        {
            uint at = (uint)i * 4;
            result[i] = new Color32(DataByte(data, at, length), DataByte(data, at + 1, length), DataByte(data, at + 2, length), DataByte(data, at + 3, length));
        }
        result.CopyTo(px, 0);
    }

    /// <summary>렌더 함수의 세 번째 인자 <c>s</c>: 설정 키 → 값(고정된 객체).</summary>
    public static ObjectInstance CreateSettings(Engine engine, IReadOnlyList<ScriptSetting> schema, IReadOnlyDictionary<string, object> values)
    {
        var settings = new JsObject(engine);
        foreach (var setting in schema)
        {
            var value = values[setting.Key];
            JsValue js = value switch
            {
                int i => JsNumber.Create(i),
                double d => JsNumber.Create(d),
                bool b => b ? JsBoolean.True : JsBoolean.False,
                Color32 c => ColorObject(engine, c, frozen: true),
                _ => new JsString((string)value),
            };
            settings.DefineOwnProperty(setting.Key, new PropertyDescriptor(js, Fixed));
        }
        settings.PreventExtensions();
        return settings;
    }

    /// <summary>렌더 함수의 두 번째 인자 <c>ctx</c>. <paramref name="settings"/>는 <see cref="CreateSettings"/>로 만든 <c>s</c>.</summary>
    public static ObjectInstance CreateContext(ScriptSandbox sandbox, IReadOnlyList<ScriptSetting> schema, ObjectInstance settings,
        Rectangle source, ScriptFrameContext frame)
    {
        var engine = sandbox.Engine;
        var ctx = new JsObject(engine);
        var rect = new JsObject(engine);
        Define(rect, "x", source.X);
        Define(rect, "y", source.Y);
        Define(rect, "w", source.Width);
        Define(rect, "h", source.Height);
        rect.PreventExtensions();
        var (pivotX, pivotY) = LayerEffects.PivotIn(frame.Pivot, source);
        var pivot = new JsObject(engine);
        Define(pivot, "x", pivotX);
        Define(pivot, "y", pivotY);
        pivot.PreventExtensions();
        ctx.DefineOwnProperty("source", new PropertyDescriptor(rect, Fixed));
        ctx.DefineOwnProperty("pivot", new PropertyDescriptor(pivot, Fixed));
        Define(ctx, "t", frame.Time);
        Define(ctx, "progress", frame.Progress);
        Define(ctx, "frame", frame.FrameIndex);
        Define(ctx, "step", frame.FrameStep);
        Define(ctx, "span", frame.FrameSpan);

        // 다른 프레임 원본: 부를 때마다 새 사본이라 고쳐도 원본과 다음 호출에 닿지 않는다
        var frameSource = frame.FrameSource;
        Method(ctx, engine, "frameSource", 1, args =>
            frameSource?.Invoke(TypeConverter.ToInt32(Arg(args, 0))) is { } other ? CreateImage(sandbox, other).Image : JsValue.Null);

        // 색 설정: 지금은 단색. x, y는 그라데이션 색(EffectColorGradient)을 넣을 때 쓴다
        Method(ctx, engine, "color", 3, args =>
        {
            var key = TypeConverter.ToString(Arg(args, 0));
            if (!schema.Any(s => s.Type == ScriptSettingType.Color && s.Key == key))
                throw new JavaScriptException(engine.Intrinsics.TypeError, DomainStrings.FormatScript_UnknownColorSetting(key: key));
            return settings.Get(key);
        });

        // 무작위: 위치·시드 해시(프레임과 무관)와 프레임·시드 값. 계산 순서에 따라 바뀌는 생성기는 두지 않는다(12.4)
        Method(ctx, engine, "hash", 3, args => LayerEffects.Random(
            TypeConverter.ToInt32(Arg(args, 2)), TypeConverter.ToInt32(Arg(args, 0)), TypeConverter.ToInt32(Arg(args, 1)), 0));
        int frameIndex = frame.FrameIndex;
        Method(ctx, engine, "random", 1, args => LayerEffects.Random(TypeConverter.ToInt32(Arg(args, 0)), frameIndex, 0, 1));
        ctx.PreventExtensions();
        return ctx;
    }

    private static ObjectInstance Image(ScriptSandbox sandbox, int width, int height, ArrayInstance data)
    {
        var image = new JsObject(sandbox.Engine);
        image.Prototype = sandbox.ImagePrototype;
        Define(image, "width", width);
        Define(image, "height", height);
        image.DefineOwnProperty("data", new PropertyDescriptor(data, Fixed));
        return image;
    }

    private static ArrayInstance ToArray(Engine engine, PixelImage image)
    {
        var px = image.Pixels;
        var values = new JsValue[px.Length * 4];
        for (int i = 0, j = 0; i < px.Length; i++, j += 4)
        {
            var c = px[i];
            values[j] = JsNumber.Create(c.R);
            values[j + 1] = JsNumber.Create(c.G);
            values[j + 2] = JsNumber.Create(c.B);
            values[j + 3] = JsNumber.Create(c.A);
        }
        return new JsArray(engine, values);
    }

    private readonly record struct ImageView(int Width, int Height, ArrayInstance Data);

    private static ImageView ReadImage(Engine engine, JsValue value)
    {
        if (value is ObjectInstance obj && obj.Get("data") is ArrayInstance data
            && obj.Get("width") is JsNumber w && obj.Get("height") is JsNumber h)
        {
            double width = w.AsNumber(), height = h.AsNumber();
            if (width >= 1 && height >= 1 && width == Math.Floor(width) && height == Math.Floor(height) && Length(data) == width * height * 4)
                return new ImageView((int)width, (int)height, data);
        }
        throw new JavaScriptException(engine.Intrinsics.TypeError, DomainStrings.Script_InvalidImageArgument);
    }

    // Color32.BlendOver로 src를 dst의 (dx, dy)에 겹친다(기본 효과의 알파 합성과 같은 결과)
    private static void BlendOver(Engine engine, ImageView dst, ImageView src, int dx, int dy)
    {
        int x0 = Math.Max(0, -dx), y0 = Math.Max(0, -dy);
        int x1 = (int)Math.Min(src.Width, (long)dst.Width - dx), y1 = (int)Math.Min(src.Height, (long)dst.Height - dy);
        for (int sy = y0; sy < y1; sy++)
            for (int sx = x0; sx < x1; sx++)
            {
                var top = ReadPixel(engine, src.Data, (uint)(sy * src.Width + sx) * 4);
                if (top.A == 0) continue;
                uint at = (uint)((sy + dy) * dst.Width + sx + dx) * 4;
                var blended = Color32.BlendOver(ReadPixel(engine, dst.Data, at), top);
                dst.Data[at] = JsNumber.Create(blended.R);
                dst.Data[at + 1] = JsNumber.Create(blended.G);
                dst.Data[at + 2] = JsNumber.Create(blended.B);
                dst.Data[at + 3] = JsNumber.Create(blended.A);
            }
    }

    private static Color32 ReadPixel(Engine engine, ArrayInstance data, uint at)
    {
        byte Channel(uint i) => data[i] is JsNumber n
            ? ToByte(n.AsNumber())
            : throw new JavaScriptException(engine.Intrinsics.TypeError, DomainStrings.FormatScript_InvalidImageData(length: Length(data), index: i));
        return new Color32(Channel(at), Channel(at + 1), Channel(at + 2), Channel(at + 3));
    }

    private static byte DataByte(ArrayInstance data, uint index, uint length)
        => data[index] is JsNumber n ? ToByte(n.AsNumber()) : throw BadData(length, index);

    private static ScriptEffectException BadData(uint length, uint index)
        => new(new(ScriptErrorKind.Runtime, DomainStrings.FormatScript_InvalidImageData(length: length, index: index), 0, 0));

    /// <summary>Uint8ClampedArray 규칙: NaN = 0, 0~255로 자르고 가운데 값은 짝수 쪽으로 반올림.</summary>
    internal static byte ToByte(double value)
        => double.IsNaN(value) ? (byte)0 : (byte)Math.Round(Math.Clamp(value, 0, 255), MidpointRounding.ToEven);

    private static ObjectInstance ColorObject(Engine engine, Color32 color, bool frozen)
    {
        var obj = new JsObject(engine);
        foreach (var (name, channel) in new[] { ("r", color.R), ("g", color.G), ("b", color.B), ("a", color.A) })
        {
            if (frozen) Define(obj, name, channel);
            else obj.CreateDataProperty(name, JsNumber.Create(channel));
        }
        if (frozen) obj.PreventExtensions();
        return obj;
    }

    private static void Define(ObjectInstance obj, string name, double value)
        => obj.DefineOwnProperty(name, new PropertyDescriptor(JsNumber.Create(value), Fixed));

    private static void Method(ObjectInstance obj, Engine engine, string name, int length, Func<JsValue[], JsValue> body)
        => obj.DefineOwnProperty(name, new PropertyDescriptor(new ClrFunction(engine, name, (_, args) => body(args), length), Fixed));

    /// <summary>배열의 <c>length</c>(스크립트가 바꿀 수 있으므로 그때그때 읽는다).</summary>
    public static uint Length(ArrayInstance array) => (uint)TypeConverter.ToNumber(array.Get("length"));

    private static JsValue Arg(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;

    private static double Number(JsValue[] args, int index) => TypeConverter.ToNumber(Arg(args, index));
}
