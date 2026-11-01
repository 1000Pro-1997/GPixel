using System.Buffers;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>
/// 효과 그리기의 줄(또는 열) 단위 병렬 실행. 줄마다 다른 줄과 겹치지 않는 메모리만 쓰고, 그 반복이 쓰는 곳을 읽지 않는 반복에만 쓴다 —
/// 그러면 줄을 어떤 순서로 돌려도 결과가 순차 실행과 바이트 단위로 같다.
/// </summary>
internal static class EffectParallel
{
    internal enum Mode
    {
        /// <summary>작업량(줄 수 × 줄 너비)이 <see cref="AutoThreshold"/> 이상일 때만 병렬.</summary>
        Auto,
        /// <summary>늘 차례대로.</summary>
        Sequential,
        /// <summary>줄이 둘 이상이면 늘 나눠서(시험용).</summary>
        Parallel,
    }

    /// <summary>
    /// 자동일 때 병렬로 도는 최소 작업량(px). 이보다 작으면 일을 나눠 맡기는 비용이 아끼는 시간보다 크다
    /// (10코어 Apple Silicon에서 효과별로 차례대로·나눠서를 재 보니 96×96에서는 외곽선·페이드가 나눠서 더 느리고,
    /// 128×128부터는 외곽선이 비슷하고 나머지 효과는 빨랐다).
    /// </summary>
    internal const int AutoThreshold = 128 * 128;

    /// <summary>일꾼 하나당 묶음 수: 늦게 끝나는 일꾼의 몫을 다른 일꾼이 가져갈 수 있을 만큼만 잘게.</summary>
    private const int BatchesPerWorker = 4;

    [ThreadStatic] private static Mode t_mode;

    /// <summary>이 스레드의 실행 방식을 바꾼다(시험용). 돌려받은 것을 닫으면 앞의 방식으로 돌아간다.</summary>
    internal static IDisposable Use(Mode mode)
    {
        var scope = new Scope(t_mode);
        t_mode = mode;
        return scope;
    }

    /// <summary>
    /// [<paramref name="from"/>, <paramref name="to"/>)의 줄마다 <paramref name="body"/>를 부른다. 줄은 함께 돌 수 있다 — 부르는 쪽이 줄마다 겹치지 않는 메모리만 쓰게 한다.
    /// <paramref name="rowWidth"/> = 줄 하나의 작업량(보통 px 수). 실행 방식은 들어올 때 부르는 스레드의 것을 쓴다.
    /// </summary>
    internal static void For(int from, int to, int rowWidth, Action<int> body)
    {
        int rows = to - from;
        if (rows <= 0) return;
        int workers = Environment.ProcessorCount;
        bool parallel = rows > 1 && t_mode switch
        {
            Mode.Sequential => false,
            Mode.Parallel => true,
            _ => workers > 1 && (long)rows * rowWidth >= AutoThreshold,
        };
        if (!parallel)
        {
            for (int y = from; y < to; y++) body(y);
            return;
        }
        // 이어진 줄 묶음으로 나눠 맡긴다(줄마다 맡기면 나누는 비용이 커짐)
        int batches = Math.Min(rows, workers * BatchesPerWorker);
        int size = (rows + batches - 1) / batches;
        batches = (rows + size - 1) / size;
        Parallel.For(0, batches, batch =>
        {
            int start = from + batch * size, end = Math.Min(to, start + size);
            for (int y = start; y < end; y++) body(y);
        });
    }

    private sealed class Scope(Mode previous) : IDisposable
    {
        public void Dispose() => t_mode = previous;
    }
}

/// <summary>
/// 효과 그리기 안에서만 쓰고 버리는 큰 임시 배열(<see cref="ArrayPool{T}.Shared"/>에서 빌림). 빌린 배열은 요청한 길이보다 길 수 있고 앞서 쓴 내용이 남아 있다 —
/// 요청한 길이만 쓰고, 읽기 전에 지우거나 모두 채우며, finally에서 돌려준다. 그리기 밖으로 나가는 배열(결과 이미지 등)에는 쓰지 않는다.
/// </summary>
internal static class EffectBuffers
{
    /// <summary>길이 <paramref name="length"/> 이상인 빌린 배열(내용은 아무것이나).</summary>
    internal static T[] Rent<T>(int length) => ArrayPool<T>.Shared.Rent(length);

    /// <summary>앞 <paramref name="source"/>.Length칸에 <paramref name="source"/>를 복사한 빌린 배열.</summary>
    internal static Color32[] RentCopy(Color32[] source)
    {
        var copy = Rent<Color32>(source.Length);
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    internal static void Return<T>(T[] array) => ArrayPool<T>.Shared.Return(array);
}
