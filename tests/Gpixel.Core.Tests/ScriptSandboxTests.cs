using System.Drawing;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.Core.Scripting;

namespace Gpixel.Core.Tests;

/// <summary>스크립트 효과(13장): 선언 해석, 실행 한도, 격리와 host API.</summary>
public class ScriptSandboxTests
{
    // 시험이 빨리 끝나도록 작은 한도
    private static readonly ScriptLimits Small = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxStatements = 2_000_000,
        MemoryBytes = 32L << 20,
        MaxRecursion = 64,
    };

    private static ScriptEffectProgram Program(string source, ScriptLimits? limits = null)
    {
        var result = ScriptEffectProgram.Parse(source, limits ?? Small);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        return result.Program;
    }

    private static string WithRender(string body, string top = "") => top + "effect({ name: 'x', render(img, ctx, s) {\n" + body + "\n} });";

    private static PixelImage Filled(int w, int h, Color32 color)
    {
        var image = new PixelImage(w, h);
        image.Clear(color);
        return image;
    }

    private static ScriptEffectException RenderFails(ScriptEffectProgram program, PixelImage? image = null)
    {
        image ??= Filled(4, 4, new Color32(10, 20, 30));
        return Assert.Throws<ScriptEffectException>(() => program.Render(image, image.Bounds, ScriptFrameContext.Still));
    }

    // ───────────── 선언 ─────────────

    [Fact]
    public void Parse_reads_the_declaration_and_settings_schema()
    {
        var program = Program("""
            effect({
              name: "물결",
              animated: true,
              area: "local",
              radius: 3,
              settings: [
                { key: "amp", label: "세기", type: "int", min: 0, max: 16, default: 4 },
                { key: "speed", type: "number", min: 0.5, max: 2, step: 0.25 },
                { key: "on", type: "bool", default: true },
                { key: "tint", label: "색", type: "color", default: "#ff8000" },
                { key: "mode", type: "choice", choices: ["a", { value: "b", label: "비" }], default: "b" },
              ],
              padding: [0, 1, 2, 3],
              render(img, ctx, s) {},
            });
            """);
        Assert.Equal("물결", program.Name);
        Assert.True(program.Animated);
        Assert.Equal(ScriptEffectArea.Local, program.Area);
        Assert.Equal(3, program.Radius);
        Assert.Equal(new EffectPadding(0, 1, 2, 3), program.Padding(null, 10, 10));
        Assert.Equal(
        [
            new ScriptSetting("amp", "세기", ScriptSettingType.Int, 0, 16, null, 4, []),
            new ScriptSetting("speed", "speed", ScriptSettingType.Number, 0.5, 2, 0.25, 0.5, []),
            new ScriptSetting("on", "on", ScriptSettingType.Bool, null, null, null, true, []),
            new ScriptSetting("tint", "색", ScriptSettingType.Color, null, null, null, new Color32(255, 128, 0), []),
        ], program.Settings.Take(4));
        var mode = program.Settings[4];
        Assert.Equal("b", mode.Default);
        Assert.Equal([new ScriptChoice("a", "a"), new ScriptChoice("b", "비")], mode.Choices);
    }

    [Fact]
    public void Defaults_area_global_and_no_padding()
    {
        var program = Program(WithRender(""));
        Assert.False(program.Animated);
        Assert.Equal(ScriptEffectArea.Global, program.Area);
        Assert.Equal(0, program.Radius);
        Assert.True(program.Padding(null, 8, 8).IsNone);
        Assert.Empty(program.Settings);
    }

    [Fact]
    public void Content_hash_ignores_line_endings_and_bom_only()
    {
        const string lf = "effect({ name: 'x',\n  render() {} });\n";
        var hash = Program(lf).ContentHash;
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, Program("\uFEFF" + lf.Replace("\n", "\r\n")).ContentHash);
        Assert.Equal(hash, ScriptEffectProgram.HashOf(lf));
        Assert.NotEqual(hash, ScriptEffectProgram.HashOf(lf.Replace("'x'", "'y'")));
    }

    [Fact]
    public void Syntax_error_reports_line_and_column()
    {
        var result = ScriptEffectProgram.Parse("effect({\n  name: 'x',\n  render(img) { let a = ; },\n});", Small);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ScriptErrorKind.Syntax, error.Kind);
        Assert.Equal((3, 25), (error.Line, error.Column));
        Assert.StartsWith("문법 오류", error.Message);
    }

    [Fact]
    public void Bad_declarations_report_every_problem_with_its_line()
    {
        var result = ScriptEffectProgram.Parse("""
            effect({
              name: "",
              area: "everywhere",
              settings: [
                { key: "ok", type: "int", min: 0, max: 4, default: 2 },
                { key: "ok", type: "int" },
                { key: "size", type: "int", min: 5, max: 1 },
                { key: "kind", type: "list" },
                { key: "c", type: "color", default: "red-ish" },
              ],
              padding: [1, 2, -3, 4],
            });
            """, Small);
        Assert.False(result.Success);
        Assert.All(result.Errors, e => Assert.Equal(ScriptErrorKind.Declaration, e.Kind));
        Assert.Equal([2, 3, 1, 11, 6, 7, 8, 9], result.Errors.Select(e => e.Line)); // render가 없으면 effect( 위치
        Assert.Contains(result.Errors, e => e.Message.Contains("render"));
        Assert.Equal(
            [Localization.DomainStrings.FormatScript_DuplicateSettingKey(key: "ok"), Localization.DomainStrings.FormatScript_InvalidSettingRange(key: "size")],
            result.Errors.Skip(4).Take(2).Select(e => e.Message));
    }

    [Theory]
    [InlineData("const x = 1;", 0)]
    [InlineData("effect({ name: 'a', render() {} });\neffect({ name: 'b', render() {} });", 2)]
    [InlineData("effect(() => {});", 1)]
    public void Effect_must_be_declared_once_with_an_object(string source, int line)
    {
        var error = Assert.Single(ScriptEffectProgram.Parse(source, Small).Errors);
        Assert.Equal(ScriptErrorKind.Declaration, error.Kind);
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public void Settings_are_normalized_to_schema_types()
    {
        var program = Program("""
            effect({ name: 'x', render() {}, settings: [
              { key: "n", type: "int", min: 0, max: 10, default: 3 },
              { key: "f", type: "number", min: -1, max: 1 },
              { key: "c", type: "color" },
              { key: "m", type: "choice", choices: ["a", "b"] },
            ] });
            """);
        var values = program.NormalizeSettings(new Dictionary<string, object> { ["n"] = 12.6, ["f"] = "nope", ["c"] = "#00ff0080", ["m"] = "z", ["extra"] = 1 });
        Assert.Equal(10, values["n"]);
        Assert.Equal(0.0, values["f"]);
        Assert.Equal(new Color32(0, 255, 0, 128), values["c"]);
        Assert.Equal("a", values["m"]);
        Assert.False(values.ContainsKey("extra"));
        Assert.Equal(7, program.NormalizeSettings(new Dictionary<string, object> { ["n"] = 6.5f + 0.5f })["n"]);
    }

    [Fact]
    public void Padding_function_gets_settings_and_size_and_is_validated()
    {
        var program = Program("""
            effect({ name: 'x', render() {}, settings: [{ key: "amp", type: "int", min: 0, max: 2000, default: 2 }],
              padding: (s, w, h) => [0, s.amp, w, h] });
            """);
        Assert.Equal(new EffectPadding(0, 5, 7, 9), program.Padding(new Dictionary<string, object> { ["amp"] = 5 }, 7, 9));
        var error = Assert.Throws<ScriptEffectException>(() => program.Padding(new Dictionary<string, object> { ["amp"] = 1500 }, 1, 1));
        Assert.Equal((ScriptErrorKind.Runtime, 2), (error.Kind, error.Line));
    }

    // ───────────── 실행 한도 ─────────────

    [Fact]
    public void Infinite_loop_stops_at_the_statement_limit_with_its_line()
    {
        var error = RenderFails(Program(WithRender("let i = 0;\nwhile (true) { i++; }")));
        Assert.Equal(ScriptErrorKind.Statements, error.Kind);
        Assert.Equal(3, error.Line);
        AssertAlive();
    }

    [Fact]
    public void Infinite_loop_stops_at_the_time_limit()
    {
        var limits = Small with { Timeout = TimeSpan.FromMilliseconds(200), MaxStatements = int.MaxValue };
        var error = RenderFails(Program(WithRender("for (;;) {}"), limits));
        Assert.Equal(ScriptErrorKind.Timeout, error.Kind);
        AssertAlive();
    }

    [Theory]
    [InlineData("const all = []; for (;;) all.push({ n: all.length, text: 'item' + all.length });")]
    [InlineData("let text = 'xy'; for (;;) { text = text + text; text.charCodeAt(text.length - 1); }")]
    [InlineData("const a = new Array(1 << 30).fill(0);")]
    public void Object_string_and_array_bombs_stop_at_the_memory_limit(string body)
    {
        Assert.Equal(ScriptErrorKind.Memory, RenderFails(Program(WithRender(body))).Kind);
        AssertAlive();
    }

    [Fact]
    public void Deep_recursion_stops_at_the_recursion_limit()
    {
        var error = RenderFails(Program(WithRender("const f = (n) => f(n + 1) + 1;\nf(0);")));
        Assert.Equal(ScriptErrorKind.Recursion, error.Kind);
        AssertAlive();
    }

    [Fact]
    public void Script_cannot_catch_a_limit()
    {
        var program = Program(WithRender("try { for (;;) {} } catch (e) { img.data[0] = 99; }"));
        Assert.Equal(ScriptErrorKind.Statements, RenderFails(program).Kind);
    }

    [Fact]
    public void Limit_while_reading_the_declaration_is_a_parse_error()
    {
        var error = Assert.Single(ScriptEffectProgram.Parse("for (;;) {}\neffect({ name: 'x', render() {} });", Small).Errors);
        Assert.Equal((ScriptErrorKind.Statements, 1), (error.Kind, error.Line));
    }

    [Fact]
    public void Cancellation_stops_a_running_script_like_other_effects()
    {
        var limits = Small with { MaxStatements = int.MaxValue, Timeout = TimeSpan.FromSeconds(30) };
        var program = Program(WithRender("for (;;) {}"), limits);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var image = Filled(2, 2, Color32.White);
        Assert.ThrowsAny<OperationCanceledException>(() => program.Render(image, image.Bounds, ScriptFrameContext.Still, null, cts.Token));
        AssertAlive();
    }

    [Fact]
    public void Runtime_error_reports_line_and_leaves_the_image_unchanged()
    {
        var image = Filled(3, 3, new Color32(1, 2, 3));
        var before = image.Clone();
        var error = RenderFails(Program(WithRender("img.data[0] = 200;\nnull.boom;")), image);
        Assert.Equal((ScriptErrorKind.Runtime, 3), (error.Kind, error.Line));
        Assert.Contains("boom", error.Message);
        Assert.True(image.ContentEquals(before));
    }

    // 한도에 걸린 다음에도 같은 스레드 실행기로 정상 계산이 된다
    private static void AssertAlive()
    {
        var image = Filled(2, 1, new Color32(0, 0, 0));
        Program(WithRender("img.data[0] = 7;")).Render(image, image.Bounds, ScriptFrameContext.Still);
        Assert.Equal(new Color32(7, 0, 0), image.Pixels[0]);
    }

    // ───────────── 격리와 결정론 ─────────────

    [Theory]
    [InlineData("Math.random()")]
    [InlineData("Date.now()")]
    [InlineData("new Date()")]
    [InlineData("eval('1')")]
    [InlineData("new Function('return 1')()")]
    [InlineData("(function () {}).constructor('return 1')()")]
    [InlineData("Object.getPrototypeOf(function* () {}).constructor('yield 1')")]
    [InlineData("Object.getPrototypeOf(async function () {}).constructor('return 1')")]
    [InlineData("new ShadowRealm()")]
    [InlineData("importNamespace('System.IO')")]
    [InlineData("System.IO.File.ReadAllText('x')")]
    [InlineData("clr")]
    [InlineData("require('fs')")]
    [InlineData("fetch('https://example.com')")]
    [InlineData("new XMLHttpRequest()")]
    [InlineData("new Uint8Array(1 << 30)")]
    [InlineData("new ArrayBuffer(1 << 30)")]
    [InlineData("Intl.DateTimeFormat()")]
    [InlineData("new WeakRef({})")]
    public void Unsafe_and_nondeterministic_features_are_unavailable(string expression)
    {
        var error = RenderFails(Program(WithRender(expression + ";")));
        Assert.Equal(ScriptErrorKind.Runtime, error.Kind);
    }

    [Theory]
    [InlineData("Array.prototype.extra = 1;")]
    [InlineData("Math.sin = Math.cos;")]
    [InlineData("Object.prototype.polluted = true;")]
    [InlineData("String.prototype.trim = () => '';")]
    [InlineData("Object.getPrototypeOf([][Symbol.iterator]()).next = null;")]
    public void Built_in_objects_are_frozen(string statement)
        => Assert.Equal(ScriptErrorKind.Runtime, RenderFails(Program(WithRender(statement))).Kind);

    [Fact]
    public void State_left_by_a_call_never_reaches_the_next_call()
    {
        var program = Program(
            WithRender("count++; total += 1; globalThis.extra = (globalThis.extra ?? 0) + 1;\nimg.data[0] = count; img.data[1] = total; img.data[2] = extra;",
                "let count = 0;\nvar total = 0;\n"));
        for (int i = 0; i < 3; i++)
        {
            var image = Filled(1, 1, Color32.Black);
            program.Render(image, image.Bounds, ScriptFrameContext.Still);
            Assert.Equal(new Color32(1, 1, 1), image.Pixels[0]);
        }
    }

    // ───────────── host API ─────────────

    [Fact]
    public void Image_data_is_rgba_and_written_back_with_clamped_rounding()
    {
        var image = new PixelImage(2, 1);
        image.Pixels[0] = new Color32(10, 20, 30, 40);
        image.Pixels[1] = new Color32(50, 60, 70, 80);
        Program(WithRender("""
            const d = img.data;
            if (img.width !== 2 || img.height !== 1 || d.length !== 8 || d.join() !== '10,20,30,40,50,60,70,80') throw new Error('in');
            d[0] = 300; d[1] = -5; d[2] = 2.5; d[3] = 3.5; d[4] = NaN; d[5] = 254.4;
            """)).Render(image, image.Bounds, ScriptFrameContext.Still);
        Assert.Equal([new Color32(255, 0, 2, 4), new Color32(0, 254, 70, 80)], image.Pixels);
    }

    [Theory]
    [InlineData("img.data[1] = 'red';", 1)]
    [InlineData("img.data.length = 3;", 3)]
    public void Invalid_image_data_is_a_runtime_error(string body, int index)
    {
        var image = Filled(2, 2, Color32.White);
        var error = RenderFails(Program(WithRender(body)), image);
        Assert.Equal(ScriptErrorKind.Runtime, error.Kind);
        Assert.Equal(Localization.DomainStrings.FormatScript_InvalidImageData(length: 16, index: index), error.Message);
        Assert.True(image.ContentEquals(Filled(2, 2, Color32.White)));
    }

    [Fact]
    public void Context_gives_source_pivot_time_and_frame_values()
    {
        var image = new PixelImage(6, 1);
        var frame = new ScriptFrameContext { Time = 0.5, Progress = 0.25, FrameIndex = 7, FrameStep = 2, FrameSpan = 4, Pivot = new PointF(1.5f, 2) };
        Program(WithRender("""
            const r = ctx.source, v = [r.x, r.y, r.w, r.h, ctx.pivot.x * 10, ctx.pivot.y, ctx.t * 100, ctx.progress * 100, ctx.frame, ctx.step, ctx.span];
            v.forEach((value, i) => { img.data[i] = value; });
            """)).Render(image, new Rectangle(1, 2, 3, 4), frame);
        Assert.Equal([1, 2, 3, 4, 25, 4, 50, 25, 7, 2, 4], image.Pixels.SelectMany(c => new[] { c.R, c.G, c.B, c.A }).Take(11).Select(b => (int)b));
    }

    [Fact]
    public void Missing_pivot_is_the_source_center()
    {
        var image = new PixelImage(1, 1);
        Program(WithRender("img.data[0] = ctx.pivot.x * 10; img.data[1] = ctx.pivot.y * 10;")).Render(image, new Rectangle(2, 1, 5, 4), ScriptFrameContext.Still);
        Assert.Equal((45, 30), (image.Pixels[0].R, image.Pixels[0].G));
    }

    [Fact]
    public void Frame_source_gives_a_fresh_copy_or_null()
    {
        var other = Filled(2, 2, new Color32(9, 8, 7));
        var frame = ScriptFrameContext.Still with { FrameSource = i => i == 3 ? other : null };
        var image = new PixelImage(1, 1);
        Program(WithRender("""
            const a = ctx.frameSource(3);
            a.data[0] = 200;
            const b = ctx.frameSource(3);
            img.data[0] = b.data[0];
            img.data[1] = a.width * 10 + a.height;
            img.data[2] = ctx.frameSource(4) === null ? 1 : 0;
            """)).Render(image, image.Bounds, frame);
        Assert.Equal(new Color32(9, 22, 1, 0), image.Pixels[0]);
        Assert.Equal(new Color32(9, 8, 7), other.Pixels[0]);
    }

    [Fact]
    public void Color_settings_reach_ctx_color_and_settings_object()
    {
        var program = Program("""
            effect({ name: 'x', settings: [{ key: "tint", type: "color", default: "#102030" }, { key: "n", type: "int" }],
              render(img, ctx, s) {
                const c = ctx.color("tint", 0, 0);
                img.data[0] = c.r; img.data[1] = s.tint.g; img.data[2] = c.b; img.data[3] = Object.isFrozen(s) && Object.isFrozen(c) ? 1 : 0;
                try { ctx.color("n", 0, 0); } catch (e) { img.data[4] = 5; }
              } });
            """);
        var image = new PixelImage(2, 1);
        program.Render(image, image.Bounds, ScriptFrameContext.Still, new Dictionary<string, object> { ["tint"] = new Color32(40, 50, 60) });
        Assert.Equal([new Color32(40, 50, 60, 1), new Color32(5, 0, 0, 0)], image.Pixels);
    }

    [Fact]
    public void Hash_and_random_match_the_built_in_seeded_random()
    {
        var image = new PixelImage(1, 1);
        var frame = ScriptFrameContext.Still with { FrameIndex = 5 };
        Program(WithRender("img.data[0] = ctx.hash(3, 4, 99) * 255; img.data[1] = ctx.random(99) * 255; img.data[2] = ctx.hash(3, 4, 99) === ctx.hash(3, 4, 99) ? 1 : 0;"))
            .Render(image, image.Bounds, frame);
        Assert.Equal((byte)Math.Round(LayerEffects.Random(99, 3, 4, 0) * 255, MidpointRounding.ToEven), image.Pixels[0].R);
        Assert.Equal((byte)Math.Round(LayerEffects.Random(99, 5, 0, 1) * 255, MidpointRounding.ToEven), image.Pixels[0].G);
        Assert.Equal(1, image.Pixels[0].B);
    }

    [Fact]
    public void Color_helpers_match_color32()
    {
        var image = new PixelImage(2, 1);
        Program(WithRender("""
            const c = hsvToRgb(200, 0.5, 0.75);
            const h = rgbToHsv(30, 120, 210);
            img.data.splice(0, 8, c.r, c.g, c.b, c.a, Math.round(h.h), Math.round(h.s * 100), Math.round(h.v * 100), lerp(10, 30, 0.25));
            """)).Render(image, image.Bounds, ScriptFrameContext.Still);
        var (h, s, v) = new Color32(30, 120, 210).ToHsv();
        Assert.Equal([Color32.FromHsv(200, 0.5f, 0.75f), new Color32((byte)Math.Round(h), (byte)Math.Round(s * 100), (byte)Math.Round(v * 100), 15)], image.Pixels);
    }

    [Fact]
    public void Blank_and_blend_over_match_color32_blend()
    {
        var image = Filled(3, 2, new Color32(0, 0, 255, 128));
        Program(WithRender("""
            const layer = img.blank(2, 2);
            layer.data.splice(0, 4, 255, 0, 0, 100);
            layer.data.splice(4, 4, 0, 255, 0, 255);
            img.blendOver(layer, 1, 1);
            """)).Render(image, image.Bounds, ScriptFrameContext.Still);
        var bg = new Color32(0, 0, 255, 128);
        Assert.Equal(Color32.BlendOver(bg, new Color32(255, 0, 0, 100)), image.Pixels[4]);
        Assert.Equal(bg, image.Pixels[3]);
        Assert.Equal(bg, image.Pixels[0]);
    }

    [Theory]
    [InlineData("img.blank(0, 4)")]
    [InlineData("img.blank(5000, 5000)")]
    [InlineData("img.blendOver({ width: 1, height: 1, data: [1, 2, 3] })")]
    public void Bad_host_arguments_are_runtime_errors(string expression)
        => Assert.Equal(ScriptErrorKind.Runtime, RenderFails(Program(WithRender(expression + ";"))).Kind);

    [Fact]
    public void Image_over_the_array_limit_is_refused_before_running()
    {
        var program = Program(WithRender(""), Small with { MaxArrayLength = 1024 });
        Assert.Equal(ScriptErrorKind.Memory, RenderFails(program, new PixelImage(20, 13)).Kind);
    }
}
