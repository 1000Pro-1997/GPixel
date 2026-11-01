using Gpixel.Core.Assets;

namespace CatalogBuilder;

/// <summary>
/// <c>catalog/</c>(제출 패키지 + review.json)를 검사해 <c>docs/catalog/</c>(쪽 index JSON, 패키지, 미리보기)를 만든다.
/// <c>--check</c>는 쓰지 않고, 검사 실패나 커밋한 결과가 최신이 아니면 1로 끝난다(CI).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        bool check = args.Contains("--check");
        if (args.Any(arg => arg != "--check"))
        {
            Console.Error.WriteLine("usage: dotnet run --project tools/CatalogBuilder -- [--check]");
            return 2;
        }
        string root = RepoRoot();
        string source = Path.Combine(root, "catalog");
        string output = Path.Combine(root, "docs", "catalog");

        var build = RemoteCatalogBuilder.Build(source, output);
        if (!build.Ok)
        {
            foreach (var issue in build.Issues) Console.Error.WriteLine("catalog/" + issue.Message);
            Console.Error.WriteLine($"{build.Issues.Count} problem(s) in catalog/.");
            return 1;
        }
        var stale = RemoteCatalogBuilder.Stale(build, output);
        if (check)
        {
            if (stale.Count == 0)
            {
                Console.WriteLine($"docs/catalog is up to date ({build.Entries.Count} package(s)).");
                return 0;
            }
            foreach (var path in stale) Console.Error.WriteLine("stale: docs/catalog/" + path);
            Console.Error.WriteLine("docs/catalog is out of date. Run: dotnet run --project tools/CatalogBuilder");
            return 1;
        }
        RemoteCatalogBuilder.Write(build, output);
        foreach (var path in stale) Console.WriteLine("updated: docs/catalog/" + path);
        Console.WriteLine($"docs/catalog: {build.Entries.Count} package(s), {stale.Count} file(s) changed.");
        return 0;
    }

    /// <summary>저장소 루트: 지금 폴더, 없으면 실행 파일 위쪽에서 Gpixel.sln을 찾는다.</summary>
    private static string RepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Gpixel.sln"))) return dir.FullName;
        throw new InvalidOperationException("Gpixel.sln not found above " + Environment.CurrentDirectory);
    }
}
