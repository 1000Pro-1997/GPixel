namespace Gpixel.Core.IO;

/// <summary>
/// 오래 걸리는 저장·열기·내보내기의 진행률(0~1). 일하는 쪽(작업 스레드, 여럿이어도 됨)이 단계(<see cref="WorkStage"/>)를 채우고,
/// 화면은 <see cref="Fraction"/>을 읽어 그린다. 단계는 일을 시작하기 전에 모두 잡는다 — 도중에 전체가 늘어 막대가 뒤로 가지 않게.
/// </summary>
public sealed class WorkProgress
{
    private long _total, _done;

    /// <summary>지금까지 한 몫(0~1). 아무 단계도 없으면 0.</summary>
    public double Fraction
    {
        get
        {
            long total = Volatile.Read(ref _total);
            return total <= 0 ? 0 : Math.Clamp((double)Volatile.Read(ref _done) / total, 0, 1);
        }
    }

    /// <summary>
    /// 단계 하나의 몫(<paramref name="units"/>, 단위는 부르는 쪽이 정함 — 보통 처리할 픽셀 수)을 전체에 더한다.
    /// 단계 안에서 <see cref="WorkStage.Step"/>으로 채우고, 닫으면 못 채운 몫을 채운다.
    /// </summary>
    public WorkStage Begin(long units)
    {
        units = Math.Max(1, units);
        Interlocked.Add(ref _total, units);
        return new WorkStage(this, null, units);
    }

    internal void Advance(long units) => Interlocked.Add(ref _done, units);
}

/// <summary>
/// <see cref="WorkProgress"/>의 단계 하나. 여러 스레드에서 <see cref="Step"/>해도 되고, 잡은 몫을 넘게 채우지 않는다
/// (예상보다 일이 많으면 막대가 그 단계 끝에서 멈춰 있고, 적으면 닫을 때 한 번에 채운다).
/// </summary>
public sealed class WorkStage : IDisposable
{
    private readonly WorkProgress _root;
    private readonly WorkStage? _parent;
    private readonly long _units;
    private long _used;

    internal WorkStage(WorkProgress root, WorkStage? parent, long units)
    {
        _root = root;
        _parent = parent;
        _units = Math.Max(1, units);
    }

    /// <summary>이 단계 몫 안의 하위 단계(<paramref name="units"/>만큼을 하위 단계가 채운다).</summary>
    public WorkStage Sub(long units) => new(_root, this, units);

    /// <summary><paramref name="units"/>만큼 했다.</summary>
    public void Step(long units)
    {
        if (units <= 0) return;
        long after = Interlocked.Add(ref _used, units), before = after - units;
        long add = Math.Min(after, _units) - Math.Min(before, _units);
        if (add <= 0) return;
        if (_parent is not null) _parent.Step(add);
        else _root.Advance(add);
    }

    /// <summary>못 채운 몫을 채운다(단계 끝).</summary>
    public void Dispose() => Step(Math.Max(0, _units - Volatile.Read(ref _used)));
}
