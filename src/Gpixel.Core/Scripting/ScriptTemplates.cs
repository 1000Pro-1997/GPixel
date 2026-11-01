namespace Gpixel.Core.Scripting;

/// <summary>앱에 들어 있는 스크립트 효과 틀(<c>Scripting/Templates/*.js</c>).</summary>
public static class ScriptTemplates
{
    /// <summary>빛 지나가기(<see cref="Model.ShineEffect"/>)를 JS로 옮긴 효과 — 같은 설정이면 기본 효과와 픽셀 단위로 같다. 효과 창 "새 스크립트"의 처음 원문.</summary>
    public static string Shine { get; } = Read("shine.js");

    private static string Read(string name)
    {
        using var stream = typeof(ScriptTemplates).Assembly.GetManifestResourceStream("Gpixel.Scripting." + name)
                           ?? throw new InvalidOperationException(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
