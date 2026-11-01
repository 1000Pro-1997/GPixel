using System.Drawing;
using System.Text.Json.Nodes;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>
/// 기본 효과 한 종류의 UI 없는 정의: 영구 <see cref="TypeId"/>, 설정 data 버전, 모델 타입, 기본값, 명시적 설정 schema(<see cref="Parameters"/>),
/// 설정 JSON 읽기·쓰기, 렌더링·바깥 여백 함수와 능력 표시. 정의는 효과 파일(<c>Effects/Definitions/</c>)에 두고 <see cref="EffectRegistry"/>의 목록 한 줄로 등록한다.
/// 렌더링 때는 효과 하나마다 정의를 한 번 찾아 렌더러를 부른다(픽셀 루프 안에서는 찾지 않음).
/// </summary>
public sealed class EffectDefinition
{
    private readonly Func<LayerEffect> _create;
    private readonly Action<PixelImage, LayerEffect, Rectangle, EffectRunHooks?> _render;
    private readonly Func<LayerEffect, int, int, EffectPadding>? _padding;
    private readonly Func<LayerEffect, bool> _usesOtherFrames;
    private readonly Func<LayerEffect, bool>? _erasesSource;
    private readonly Func<JsonObject, LayerEffect, LayerEffect>? _legacyFixup;
    private readonly IReadOnlyDictionary<string, Func<JsonObject, LayerEffect>> _legacyAliases;
    private readonly EffectCodecHooks? _codec;

    private EffectDefinition(string typeId, int version, Type modelType, string? legacyId, Func<LayerEffect> create,
        Action<PixelImage, LayerEffect, Rectangle, EffectRunHooks?> render, Func<LayerEffect, int, int, EffectPadding>? padding,
        IReadOnlyList<EffectParameter> parameters, Func<LayerEffect, bool> usesOtherFrames, bool clipsBySource, Func<LayerEffect, bool>? erasesSource,
        Func<JsonObject, LayerEffect, LayerEffect>? legacyFixup, IReadOnlyDictionary<string, Func<JsonObject, LayerEffect>> legacyAliases,
        EffectCodecHooks? codec)
    {
        TypeId = typeId;
        Version = version;
        ModelType = modelType;
        LegacyIds = legacyId is null ? [.. legacyAliases.Keys] : [legacyId, .. legacyAliases.Keys];
        _create = create;
        _render = render;
        _padding = padding;
        Parameters = parameters;
        _usesOtherFrames = usesOtherFrames;
        ClipsBySource = clipsBySource;
        _erasesSource = erasesSource;
        _legacyFixup = legacyFixup;
        _legacyAliases = legacyAliases;
        _codec = codec;
        var defaults = create();
        foreach (var p in parameters) p.SetDefault(defaults);
    }

    /// <summary>영구 효과 종류 ID(<c>gpixel.effect.outline</c> 꼴). 이름을 바꿔도 바꾸지 않는다.</summary>
    public string TypeId { get; }

    /// <summary>이 효과 설정의 data 버전. 더 큰 버전으로 저장된 효과는 읽지 않고 원본을 보존한다(<see cref="MissingEffect"/>).</summary>
    public int Version { get; }

    /// <summary>모델 CLR 타입.</summary>
    public Type ModelType { get; }

    /// <summary>
    /// 예전 프로젝트·사용자 정의 효과 파일의 짧은 종류 ID(첫 번째가 이 효과의 이전 ID, 나머지는 이 효과로 바꿔 읽는 없어진 효과).
    /// 예전 형식이 없던 효과(스크립트 효과)는 빈 목록.
    /// </summary>
    public IReadOnlyList<string> LegacyIds { get; }

    /// <summary>프레임 범위 안 진행에 따라 모습이 바뀌는 효과(<see cref="AnimatedEffect"/>).</summary>
    public bool Animated => typeof(AnimatedEffect).IsAssignableFrom(ModelType);

    /// <summary>
    /// 효과 <paramref name="effect"/>가 다른 프레임 그림을 쓰는지(잔상, <c>ctx.frameSource</c>를 쓰는 스크립트) — 그릴 때 다른 프레임 원본을 넘겨야 하고,
    /// 셀 하나가 바뀌면 다른 프레임 결과도 바뀐다.
    /// </summary>
    public bool UsesOtherFrames(LayerEffect effect) => _usesOtherFrames(effect);

    /// <summary>
    /// 클리핑되면 기준 효과가 바꾼 픽셀만 원본으로 삼아 그리고, 기준 영역 밖에 생긴 결과는 그림 뒤에 놓는 효과(반사 — 픽셀을 옮기는 효과).
    /// 클리핑돼도 바깥 여백을 넓힌다. 아니면 결과 중 기준 영역 안의 픽셀만 남긴다.
    /// </summary>
    public bool ClipsBySource { get; }

    /// <summary>명시적 설정 schema(저장 키 순서). 움직이는 효과는 끝에 빠르기 곡선·반복·반복 횟수가 붙는다.</summary>
    public IReadOnlyList<EffectParameter> Parameters { get; }

    /// <summary>기본 설정의 새 효과(모든 프레임, 켜짐).</summary>
    public LayerEffect Create() => _create();

    /// <summary>
    /// 설정 JSON(schema 키 전부, 순서대로 — 스크립트 효과는 그 앞에 스크립트 설정). 켜짐·프레임 범위·클리핑·색 그라데이션은 넣지 않는다.
    /// </summary>
    public JsonObject WriteSettings(LayerEffect effect)
    {
        var json = _codec?.WriteSettings(effect) ?? [];
        foreach (var p in Parameters) json[p.Key] = p.Write(effect);
        return json;
    }

    /// <summary>설정 JSON을 읽은 새 효과. 없거나 모양이 틀린 키는 기본값, 모르는 키는 무시한다.</summary>
    public LayerEffect ReadSettings(JsonObject settings) => Read(settings, null) ?? Create();

    /// <summary>
    /// 효과 JSON(<paramref name="root"/>, 설정만 읽을 때는 null)의 설정으로 새 효과. 맨 위 키가 틀려 읽을 수 없으면 null(원본 보존 — <see cref="MissingEffect"/>).
    /// </summary>
    internal LayerEffect? Read(JsonObject settings, JsonObject? root)
    {
        var effect = Create();
        foreach (var p in Parameters)
            if (settings.TryGetPropertyValue(p.Key, out var value) && p.TryRead(effect, value, out var read))
                effect = read;
        return _codec is null ? effect : _codec.Read(effect, settings, root);
    }

    /// <summary>효과 JSON 맨 위에 이 효과만 쓰는 키를 더한다(스크립트 효과의 원문 해시).</summary>
    internal void WriteRoot(LayerEffect effect, JsonObject root) => _codec?.WriteRoot(effect, root);

    /// <summary>
    /// 예전 형식(짧은 ID, 설정이 효과 객체에 바로 있음) 효과. 설정마다 <see cref="EffectParameter.LegacyKeys"/>를 앞에서부터 찾고,
    /// 효과별 보정(예전 반사 위·아래, 회전 방향 등)을 덧붙인다. <paramref name="handlesGradients"/>가 true면 색 그라데이션까지 이미 읽었다(없어진 효과의 변환).
    /// </summary>
    internal LayerEffect ReadLegacy(string legacyId, JsonObject flat, out bool handlesGradients)
    {
        if (_legacyAliases.TryGetValue(legacyId, out var alias))
        {
            handlesGradients = true;
            return alias(flat);
        }
        handlesGradients = false;
        var effect = Create();
        foreach (var p in Parameters)
            foreach (var key in p.LegacyKeys)
                if (flat.TryGetPropertyValue(key, out var value) && value is not null)
                {
                    if (p.TryRead(effect, value, out var read)) effect = read;
                    break;
                }
        return _legacyFixup is null ? effect : _legacyFixup(flat, effect);
    }

    /// <summary>
    /// <paramref name="image"/>에 효과를 바로 입힌다(<paramref name="source"/> = 효과 원본이 놓인 자리). <paramref name="hooks"/>는 렌더링의 취소와
    /// 그리는 레이어(스크립트 효과의 실패 기록)다.
    /// </summary>
    internal void Render(PixelImage image, LayerEffect effect, Rectangle source, EffectRunHooks? hooks = null) => _render(image, effect, source, hooks);

    /// <summary>지금까지 넓힌 크기 <paramref name="width"/>×<paramref name="height"/>에 이 효과가 더하는 바깥 여백.</summary>
    internal EffectPadding Padding(LayerEffect effect, int width, int height) => _padding?.Invoke(effect, width, height) ?? default;

    /// <summary>
    /// 이 효과가 원본을 지워(투명하게) 선 같은 결과만 남기는지 — 그러면 투명해진 원본 자리는 클리핑 기준 영역에서 뺀다.
    /// </summary>
    internal bool ErasesSource(LayerEffect effect) => _erasesSource?.Invoke(effect) == true;

    /// <summary>
    /// 정의 만들기. <paramref name="schema"/>는 설정 목록, <paramref name="legacyId"/>는 예전 짧은 ID,
    /// <paramref name="legacyFixup"/>은 예전 형식에만 있던 키를 옮기는 보정, <paramref name="legacyAliases"/>는 이 효과로 바꿔 읽는 없어진 효과 ID와 그 변환이다.
    /// </summary>
    internal static EffectDefinition Create<T>(string typeId, string legacyId, Action<PixelImage, T, Rectangle> render,
        Func<EffectSchema<T>, IEnumerable<EffectParameter>> schema, int version = 1, Func<T, int, int, EffectPadding>? padding = null,
        bool usesOtherFrames = false, bool clipsBySource = false, Func<T, bool>? erasesSource = null,
        Func<JsonObject, T, T>? legacyFixup = null, IReadOnlyDictionary<string, Func<JsonObject, LayerEffect>>? legacyAliases = null)
        where T : LayerEffect, new()
    {
        var parameters = schema(new EffectSchema<T>()).ToList();
        if (typeof(AnimatedEffect).IsAssignableFrom(typeof(T))) parameters.AddRange(MotionParameters());
        return new EffectDefinition(typeId, version, typeof(T), legacyId, () => new T(),
            (image, effect, source, _) => render(image, (T)effect, source),
            padding is null ? null : (effect, width, height) => padding((T)effect, width, height),
            parameters, _ => usesOtherFrames, clipsBySource,
            erasesSource is null ? null : effect => erasesSource((T)effect),
            legacyFixup is null ? null : (flat, effect) => legacyFixup(flat, (T)effect),
            legacyAliases ?? new Dictionary<string, Func<JsonObject, LayerEffect>>(), null);
    }

    /// <summary>
    /// 효과 값마다 설정 schema가 다른 효과(스크립트 효과)의 정의. 렌더러는 렌더링 끼움(취소·레이어)을 받고, 다른 프레임을 쓰는지도 효과마다 정한다.
    /// 설정 JSON에는 <paramref name="codec"/>이 쓰는 효과 자신의 설정 뒤에 움직이는 효과 공통 설정이 붙는다. 예전 형식은 없다.
    /// </summary>
    internal static EffectDefinition CreateScripted<T>(string typeId, Action<PixelImage, T, Rectangle, EffectRunHooks?> render,
        Func<T, int, int, EffectPadding> padding, Func<T, bool> usesOtherFrames, EffectCodecHooks codec)
        where T : AnimatedEffect, new()
    {
        return new EffectDefinition(typeId, 1, typeof(T), null, () => new T(),
            (image, effect, source, hooks) => render(image, (T)effect, source, hooks),
            (effect, width, height) => padding((T)effect, width, height),
            MotionParameters(), effect => usesOtherFrames((T)effect), false, null, null, new Dictionary<string, Func<JsonObject, LayerEffect>>(), codec);
    }

    /// <summary>움직이는 효과 공통 설정(schema 끝): 빠르기 곡선·반복·반복 횟수.</summary>
    private static EffectParameter[] MotionParameters()
    {
        var motion = new EffectSchema<AnimatedEffect>();
        return
        [
            motion.Choice("easing", e => e.Easing, (e, v) => e with { Easing = v }),
            motion.Choice("repeat", e => e.Repeat, (e, v) => e with { Repeat = v }),
            motion.Int("repeatCount", 1, AnimatedEffect.MaxRepeatCount, e => e.RepeatCount, (e, v) => e with { RepeatCount = v }),
        ];
    }
}

/// <summary>
/// 효과 값마다 설정이 다른 효과의 저장: 설정 JSON 앞부분(효과 자신의 설정), 효과 JSON 맨 위 키, 둘을 읽어 schema 설정을 읽은 효과에 더하기
/// (<paramref name="Read"/>의 효과 JSON은 설정만 읽을 때 null — 맨 위 값은 그대로 둔다. 읽을 수 없으면 null).
/// </summary>
internal sealed record EffectCodecHooks(
    Func<LayerEffect, JsonObject> WriteSettings,
    Action<LayerEffect, JsonObject> WriteRoot,
    Func<LayerEffect, JsonObject, JsonObject?, LayerEffect?> Read);
