using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.Effects;
using Gpixel.Core.Scripting;

namespace Gpixel.Core.Model;

// 프로젝트 스크립트 표(13.5): 스크립트 효과의 원문을 해시로 한 번만 담는다.
public sealed partial class SpriteProject
{
    private readonly Dictionary<string, string> _scripts = new(StringComparer.Ordinal);

    /// <summary>
    /// 이 프로젝트에 넣은 스크립트 원문(해시 → 정리한 원문). 파일에서 읽은 것과 효과 창에서 쓰거나 가져온 것이다.
    /// 실행취소로 예전 원문의 효과가 돌아올 수 있으므로 열려 있는 동안은 빼지 않고, 저장할 때 효과가 쓰는 것만 쓴다(<see cref="ScriptsInUse"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Scripts => _scripts;

    /// <summary>원문을 표에 넣고(같은 원문이면 그대로) 해시를 돌려준다. 렌더러가 찾을 수 있게 <see cref="ScriptLibrary"/>에도 넣는다.</summary>
    public string AddScript(string source)
    {
        var hash = ScriptLibrary.Add(source);
        _scripts[hash] = ScriptLibrary.SourceOf(hash)!;
        return hash;
    }

    /// <summary>
    /// 해시의 원문: 이 표, 없으면 다른 프로젝트에서 옮겨 온 효과라 <see cref="ScriptLibrary"/>(모르면 null). 저장·레시피 담기·실행취소 기록이 쓴다.
    /// </summary>
    public string? ScriptSourceOf(string hash) => _scripts.GetValueOrDefault(hash) ?? ScriptLibrary.SourceOf(hash);

    /// <summary>레이어 효과가 쓰는 스크립트 해시(레이어·효과 순서, 겹치지 않음).</summary>
    public IReadOnlyList<string> ScriptHashes() => ScriptHashesOf(_layers.SelectMany(l => l.Effects));

    /// <summary>
    /// 효과들이 쓰는 스크립트 해시(순서대로, 겹치지 않음). 읽지 못해 원본 JSON으로 보존한 스크립트 효과(더 새 버전 등의 <see cref="MissingEffect"/>)도
    /// 그 JSON의 <c>script</c> 해시를 센다 — 저장할 때 그 원문이 빠지지 않게.
    /// </summary>
    public static IReadOnlyList<string> ScriptHashesOf(IEnumerable<LayerEffect> effects)
    {
        var hashes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in effects)
        {
            string? hash = effect switch
            {
                ScriptEffect script => script.ScriptHash,
                MissingEffect { TypeId: ScriptDefinition.Id } missing => MissingScriptHash(missing),
                _ => null,
            };
            if (hash is not null && seen.Add(hash)) hashes.Add(hash);
        }
        return hashes;
    }

    private static string? MissingScriptHash(MissingEffect missing)
    {
        try
        {
            return JsonNode.Parse(missing.Json)?[ScriptDefinition.ScriptKey] is JsonValue value && value.TryGetValue(out string? hash)
                                                                                && ScriptLibrary.IsHash(hash) ? hash : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 저장할 스크립트 표: 레이어 효과가 쓰는 해시 중 원문을 아는 것(<see cref="ScriptSourceOf"/>).
    /// 해시 순서로 정렬한다. 원문을 모르는 해시는 빠진다(효과는 남고 그리지 않음).
    /// </summary>
    public SortedDictionary<string, string> ScriptsInUse()
    {
        var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var hash in ScriptHashes())
            if (ScriptSourceOf(hash) is { } source)
                used[hash] = source;
        return used;
    }
}
