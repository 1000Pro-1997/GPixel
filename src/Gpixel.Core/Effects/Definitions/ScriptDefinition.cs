using System.Text.Json.Nodes;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.Core.Scripting;

namespace Gpixel.Core.Effects;

/// <summary>
/// 스크립트 효과 정의(13.5). 저장 JSON은 다른 효과와 같은 모양에 원문 해시가 붙는다:
/// <code>{ "type": "gpixel.effect.script", "version": 1, "enabled": true, "clipToPrevious": false,
///   "settings": { "angle": 45, "color": "#FFFFFF", …, "easing": "Linear", "repeat": "Once", "repeatCount": 1 }, "script": "&lt;SHA-256&gt;" }</code>
/// 설정은 스크립트 설정(키 순서) 뒤에 움직이는 효과 공통 설정이 붙는다. 실행할 수 있는 원문을 알면 읽을 때 schema에 맞추고,
/// 모르면(원문이 없거나 아직 허락하지 않음) 값을 그대로 보존한다. 해시가 틀린 모양이면 <see cref="MissingEffect"/>로 원본을 보존한다.
/// </summary>
public static class ScriptDefinition
{
    public const string Id = "gpixel.effect.script";

    /// <summary>효과 JSON에서 원문 해시를 담는 맨 위 키.</summary>
    public const string ScriptKey = "script";

    public static EffectDefinition Definition { get; } = EffectDefinition.CreateScripted<ScriptEffect>(Id,
        (image, effect, source, hooks) =>
        {
            // 실패한 실행의 결과(효과를 건너뛴 그림)는 중간 결과·잔상 캐시에 담지 않게 알린다
            if (!ScriptEffectRuntime.Render(image, effect, source, hooks?.Owner, hooks?.Cancellation ?? CancellationToken.None)) hooks?.ReportScriptFailure();
        },
        Padding, UsesOtherFrames, new EffectCodecHooks(WriteSettings, WriteRoot, Read));

    // 여백 함수가 실패하면 넓히지 않는다 — 실패는 기억해 두고 렌더러가 그 효과의 실패로 센다(ScriptEffectRuntime.Render)
    private static EffectPadding Padding(ScriptEffect effect, int width, int height)
        => ScriptLibrary.Runnable(effect.ScriptHash) is { } program ? ScriptLibrary.Padding(effect.ScriptHash, program, effect.Settings, width, height) : default;

    // 실행하는 스크립트가 ctx.frameSource를 쓰면 다른 프레임 원본을 넘긴다(원문에 그 이름이 나오는지로 정함)
    private static bool UsesOtherFrames(ScriptEffect effect)
        => ScriptLibrary.TrustOf(effect.ScriptHash) == ScriptTrust.Trusted && ScriptLibrary.InfoOf(effect.ScriptHash) is { ReadsOtherFrames: true };

    private static JsonObject WriteSettings(LayerEffect effect) => ((ScriptEffect)effect).Settings.ToJson();

    private static void WriteRoot(LayerEffect effect, JsonObject root) => root[ScriptKey] = ((ScriptEffect)effect).ScriptHash;

    private static LayerEffect? Read(LayerEffect effect, JsonObject settings, JsonObject? root)
    {
        var script = (ScriptEffect)effect;
        var hash = script.ScriptHash;
        if (root is not null)
        {
            hash = EffectJson.String(root[ScriptKey]) ?? "";
            if (!ScriptLibrary.IsHash(hash)) return null;
        }
        var values = ScriptSettingValues.FromJson(settings, key => !ScriptEffectProgram.ReservedSettingKeys.Contains(key));
        if (ScriptLibrary.Runnable(hash) is { } program) values = ScriptSettingValues.Normalized(program, values);
        return script with { ScriptHash = hash, Settings = values };
    }
}
