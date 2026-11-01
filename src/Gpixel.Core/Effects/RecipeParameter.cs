using System.Globalization;
using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>
/// 효과 레시피 매개변수의 값 종류(계획 14장 3단계: 색·정수·켜고 끄기·고르기부터). 연결된 기본 효과 설정(<see cref="EffectParameter"/>)의 종류와 같아야 한다.
/// </summary>
public enum RecipeParameterKind
{
    Color,
    Int,
    Bool,
    Choice,
}

/// <summary>레시피 매개변수 값 하나(종류에 맞는 칸만 씀). 저장 JSON 모양은 연결된 효과 설정과 같다.</summary>
public readonly record struct RecipeValue
{
    private readonly string? _choice;

    private RecipeValue(RecipeParameterKind kind, int number, Color32 color, string? choice)
    {
        Kind = kind;
        Int = number;
        Color = color;
        _choice = choice;
    }

    public RecipeParameterKind Kind { get; }

    /// <summary>정수 값(켜고 끄기는 0·1).</summary>
    public int Int { get; }

    public bool Bool => Int != 0;

    public Color32 Color { get; }

    /// <summary>고른 저장 ID(효과 설정의 <see cref="EffectParameter.Choices"/> 중 하나).</summary>
    public string Choice => _choice ?? "";

    public static RecipeValue Of(int value) => new(RecipeParameterKind.Int, value, default, null);

    public static RecipeValue Of(bool value) => new(RecipeParameterKind.Bool, value ? 1 : 0, default, null);

    public static RecipeValue Of(Color32 value) => new(RecipeParameterKind.Color, 0, value, null);

    public static RecipeValue OfChoice(string value) => new(RecipeParameterKind.Choice, 0, default, value);

    /// <summary>저장 JSON 값(효과 설정 JSON과 같은 모양: 색 "#RRGGBB(AA)", 정수, true/false, 저장 ID 글자).</summary>
    public JsonNode ToJson() => Kind switch
    {
        RecipeParameterKind.Color => JsonValue.Create(Color.ToHex()),
        RecipeParameterKind.Int => JsonValue.Create(Int),
        RecipeParameterKind.Bool => JsonValue.Create(Bool),
        _ => JsonValue.Create(Choice),
    };

    /// <summary><paramref name="kind"/> 모양의 JSON 값을 읽는다(모양이 틀리면 false).</summary>
    public static bool TryRead(RecipeParameterKind kind, JsonNode? node, out RecipeValue value)
    {
        value = default;
        switch (kind)
        {
            case RecipeParameterKind.Color when AssetJson.String(node) is { } hex && Color32.TryParseHex(hex, out var color):
                value = Of(color);
                return true;
            case RecipeParameterKind.Int when AssetJson.TryInt(node, out int number):
                value = Of(number);
                return true;
            case RecipeParameterKind.Bool when AssetJson.TryBool(node, out bool flag):
                value = Of(flag);
                return true;
            case RecipeParameterKind.Choice when AssetJson.String(node) is { Length: > 0 } choice:
                value = OfChoice(choice);
                return true;
            default:
                return false;
        }
    }

    public override string ToString() => Kind switch
    {
        RecipeParameterKind.Color => Color.ToHex(),
        RecipeParameterKind.Int => Int.ToString(CultureInfo.InvariantCulture),
        RecipeParameterKind.Bool => Bool ? "true" : "false",
        _ => Choice,
    };
}

/// <summary>매개변수가 바꾸는 기본 효과 설정 하나: 레시피 효과 목록의 <paramref name="Effect"/>번째 효과의 schema 키 <paramref name="Key"/>.</summary>
public readonly record struct RecipeBinding(int Effect, string Key);

/// <summary>
/// 레시피 작성자가 사용자에게 내보낸 값 하나(계획 6.3): 레시피 안 고유 ID, 표시 이름, 종류, 기본값, 정수 범위·고를 수 있는 값,
/// 연결된 기본 효과 설정들. 값을 바꾸면 연결된 설정을 모두 같은 값으로 바꾼다(임의 수식 없음).
/// 범위·고를 수 있는 값은 연결된 모든 설정의 schema 안이어야 한다(<see cref="RecipeParameters.Check"/>).
/// </summary>
public sealed record RecipeParameter
{
    /// <summary>레시피 안에서 고유한 영구 ID(영문·숫자·<c>_</c>·<c>-</c>·<c>.</c>). 표시 이름을 바꿔도 그대로다.</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public RecipeParameterKind Kind { get; init; }

    /// <summary>레시피를 넣을 때 연결된 설정에 넣는 값.</summary>
    public RecipeValue Default { get; init; }

    /// <summary>정수 매개변수의 최소·최대(양 끝 포함). 다른 종류는 0.</summary>
    public int Min { get; init; }

    public int Max { get; init; }

    /// <summary>고르기 매개변수가 내보내는 저장 ID(순서대로). 다른 종류는 빈 목록.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    public IReadOnlyList<RecipeBinding> Bindings { get; init; } = [];

    /// <summary>값을 이 매개변수가 받는 값으로 맞춘다: 정수는 범위로 자르고, 종류가 다르거나 내보내지 않은 고르기 값이면 기본값.</summary>
    public RecipeValue Clamp(RecipeValue value)
    {
        if (value.Kind != Kind) return Default;
        return Kind switch
        {
            RecipeParameterKind.Int => RecipeValue.Of(Math.Clamp(value.Int, Min, Math.Max(Min, Max))),
            RecipeParameterKind.Choice => Choices.Contains(value.Choice, StringComparer.Ordinal) ? value : Default,
            _ => value,
        };
    }

    public bool Equals(RecipeParameter? other)
        => other is not null && Id == other.Id && Name == other.Name && Kind == other.Kind && Default == other.Default
           && Min == other.Min && Max == other.Max && Choices.SequenceEqual(other.Choices, StringComparer.Ordinal) && Bindings.SequenceEqual(other.Bindings);

    public override int GetHashCode() => HashCode.Combine(Id, Kind, Default, Bindings.Count);
}

/// <summary>
/// 프로젝트에 넣은 매개변수 레시피 하나(효과 묶음): 넣을 때 만든 고유 ID, 레시피 이름, 출처 레시피 ID, 매개변수 정의 사본.
/// 구성 효과마다 <see cref="LayerEffect.Recipe"/>로 이 묶음을 가리킨다. 매개변수의 지금 값은 연결된 효과 설정 자체다(따로 두지 않아 어긋나지 않음).
/// 원본 패키지가 없어도 이 사본만으로 편집된다(계획 2.4) — <see cref="SourceId"/>는 출처 표시에만 쓴다.
/// </summary>
public sealed record EffectRecipeInstance
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>넣은 레시피의 에셋 ID(출처 표시용, 없으면 null).</summary>
    public string? SourceId { get; init; }

    public IReadOnlyList<RecipeParameter> Parameters { get; init; } = [];

    public bool Equals(EffectRecipeInstance? other)
        => other is not null && Id == other.Id && Name == other.Name && SourceId == other.SourceId && Parameters.SequenceEqual(other.Parameters);

    public override int GetHashCode() => HashCode.Combine(Id, Name, SourceId, Parameters.Count);
}

/// <summary>효과가 속한 묶음과 그 효과의 레시피 안 자리(레시피 효과 목록 번호 — 매개변수 연결의 <see cref="RecipeBinding.Effect"/>).</summary>
public sealed record RecipeLink(EffectRecipeInstance Instance, int Slot);
