using Acornima.Ast;

namespace Gpixel.Core.Scripting;

/// <summary>
/// 선언 오류의 줄과 칸을 문법 트리에서 찾는다. 선언은 실행해서 읽지만 실행 값에는 위치가 없으므로,
/// <c>effect({...})</c>에 객체·배열을 그대로 적은 경우 그 안의 속성 위치를, 아니면 <c>effect(</c> 호출 위치를 쓴다.
/// </summary>
internal static class ScriptSourceLocator
{
    /// <summary>소스 순서로 <paramref name="index"/>번째 <c>effect(...)</c> 호출 위치(1부터). 없으면 (0, 0).</summary>
    public static (int Line, int Column) EffectCall(Script program, int index)
    {
        var calls = EffectCalls(program);
        return index < calls.Count ? At(calls[index]) : (0, 0);
    }

    /// <summary>
    /// 선언 객체 안 <paramref name="path"/>(속성 이름 문자열과 배열 번호)의 위치. 따라갈 수 있는 데까지 가장 깊은 노드를 쓴다.
    /// </summary>
    public static (int Line, int Column) Declaration(Script program, params object[] path)
    {
        var calls = EffectCalls(program);
        if (calls.Count == 0) return (0, 0);
        Node found = calls[0];
        Node? value = calls[0].Arguments.Count > 0 ? calls[0].Arguments[0] : null;
        foreach (var step in path)
        {
            Node? next = null;
            if (step is string name && value is ObjectExpression obj)
            {
                foreach (var node in obj.Properties)
                    if (node is Property property && KeyName(property) == name)
                    {
                        found = property;
                        next = property.Value;
                    }
            }
            else if (step is int index && value is ArrayExpression array && index < array.Elements.Count && array.Elements[index] is { } element)
            {
                found = element;
                next = element;
            }
            if (next is null) break;
            value = next;
        }
        return At(found);
    }

    /// <summary>
    /// 첫 <c>effect({...})</c> 객체에 글자 그대로 적은 속성 <paramref name="name"/>의 문자열(문자열 literal이나 치환 없는 template). 아니면 null.
    /// 실행하지 않고 읽으므로 신뢰하기 전에도 쓸 수 있다.
    /// </summary>
    public static string? DeclaredString(Script program, string name)
    {
        var calls = EffectCalls(program);
        if (calls.Count == 0 || calls[0].Arguments.Count == 0 || calls[0].Arguments[0] is not ObjectExpression obj) return null;
        string? found = null;
        foreach (var node in obj.Properties)
            if (node is Property property && KeyName(property) == name)
                found = property.Value switch
                {
                    StringLiteral literal => literal.Value,
                    TemplateLiteral { Expressions.Count: 0, Quasis.Count: 1 } template => template.Quasis[0].Value.Cooked,
                    _ => null,
                };
        return found;
    }

    /// <summary>원문 어디든 이름(식별자·속성 이름)이나 문자열로 <paramref name="name"/>이 나오는지.</summary>
    public static bool Mentions(Script program, string name)
    {
        var pending = new Stack<Node>();
        pending.Push(program);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is Identifier id && id.Name == name || node is StringLiteral literal && literal.Value == name) return true;
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        return false;
    }

    private static string? KeyName(Property property) => property switch
    {
        { Computed: false, Key: Identifier id } => id.Name,
        { Key: StringLiteral literal } => literal.Value,
        _ => null,
    };

    private static (int Line, int Column) At(Node node) => (node.Location.Start.Line, node.Location.Start.Column + 1);

    private static List<CallExpression> EffectCalls(Script program)
    {
        var calls = new List<CallExpression>();
        var pending = new Stack<Node>();
        pending.Push(program);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is CallExpression { Callee: Identifier { Name: "effect" } } call) calls.Add(call);
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        calls.Sort((a, b) => a.Start.CompareTo(b.Start));
        return calls;
    }
}
