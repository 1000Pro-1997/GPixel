using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.Core.Effects;

namespace Gpixel.Core.Tests;

/// <summary>로컬 에셋 저장소: 설치·켜기·끄기·삭제·되돌리기·업데이트, 끊긴 설치 복구, 손상된 상태 파일, 프로젝트와의 독립.</summary>
public class AssetStoreTests
{
    private const string Id = AssetSamples.PackageId;

    private static string RootOf(TempDir dir) => Path.Combine(dir.Path, "Assets");

    /// <summary>버전과 외곽선 두께만 다른 패키지(두께로 어느 버전이 활성인지 본다).</summary>
    private static string Package(TempDir dir, string version, int thickness)
        => AssetSamples.Save(dir.Path, AssetSamples.Build(AssetSamples.Header(version), AssetSamples.Recipe(thickness: thickness)), $"pack-{version}-{thickness}.gpixel-asset");

    private static int ActiveThickness(AssetStore store)
        => Assert.IsType<OutlineEffect>(Assert.Single(store.Catalog.Recipes, e => e.PackageId == Id).Recipe!.Effects[0]).Thickness;

    private static string[] VersionFolders(AssetStore store)
    {
        string dir = Path.Combine(store.PackagesDirectory, Id);
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal).ToArray() : [];
    }

    private static string[] InstalledIds(AssetStore store)
        => (JsonNode.Parse(File.ReadAllBytes(store.InstalledPath))!["packages"]!.AsArray()).Select(p => p!["id"]!.GetValue<string>()).ToArray();

    /// <summary>폴더 아래 모든 파일·폴더(상대 경로 → 내용 해시). 저장소가 바꾸는 동안 쥐는 잠금 파일은 뺀다(상태가 아님).</summary>
    private static SortedDictionary<string, string> Tree(string root)
        => new(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Where(path => !IsLock(root, path)).ToDictionary(
            path => Path.GetRelativePath(root, path).Replace('\\', '/'),
            path => File.Exists(path) ? AssetPackageWriter.Hash(File.ReadAllBytes(path)) : "<dir>"), StringComparer.Ordinal);

    private static bool IsLock(string root, string path) => Path.GetRelativePath(root, path) == AssetStore.LockFileName;

    /// <summary>끊긴 순간의 저장소 사본(바꾸는 중이라 쥐고 있는 잠금 파일은 빼고 — 프로세스가 죽으면 잠금도 풀린다).</summary>
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories).Where(file => !IsLock(from, file)))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    // ───────────── 설치와 카탈로그 ─────────────

    [Fact]
    public void Install_AddsPackageAssetsToCatalogImmediately_AndSurvivesReopen()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        int changed = 0;
        store.Changed += () => changed++;
        var personal = store.SavePersonalRecipe(EffectRecipe.Capture("", "내 효과", [new FadeEffect()]));

        var installed = store.Install(Package(dir, "1.0.0", 2));
        Assert.True(changed >= 2);
        Assert.Equal((Id, "1.0.0", true, false), (installed.Id, installed.Version, installed.Enabled, installed.IsBroken));
        // 개인 에셋 다음에 패키지 에셋
        Assert.Equal([personal.Id, Id + ".neon-hit"], store.Catalog.Recipes.Select(e => e.Id).ToArray());
        var recipe = store.Catalog.Recipes[1];
        Assert.Equal((AssetOrigin.Package, Id, "1.0.0", "Neon Pack"), (recipe.Origin, recipe.PackageId, recipe.PackageVersion, recipe.PackageName));
        Assert.Equal(Id + ".neon-blue", Assert.Single(store.Catalog.Gradients).Id);
        Assert.Equal([Id], InstalledIds(store));
        Assert.Empty(Directory.GetFileSystemEntries(store.StagingDirectory));

        var reopened = AssetStore.Open(store.Root);
        Assert.Equal(store.Catalog.Recipes.Select(e => e.Id), reopened.Catalog.Recipes.Select(e => e.Id));
        AssetSamples.AssertSameRecipe(recipe.Recipe!, reopened.Catalog.Recipes[1].Recipe!);
        Assert.Empty(reopened.LoadErrors);
    }

    [Fact]
    public void Disable_HidesAssetsButKeepsFilesAndPackage_EnableReturnsThem()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 2));
        var files = Tree(store.VersionFolder(Id, "1.0.0"));

        store.SetEnabled(Id, false);
        Assert.Empty(store.Catalog.Recipes);
        Assert.Empty(store.Catalog.Gradients);
        var package = Assert.Single(store.Catalog.Packages);
        Assert.False(package.Enabled);
        Assert.All(package.Entries, e => Assert.False(e.Enabled));
        Assert.Equal(files, Tree(store.VersionFolder(Id, "1.0.0")));
        // 끈 상태도 파일에 남는다
        Assert.Empty(AssetStore.Open(store.Root).Catalog.Recipes);

        store.SetEnabled(Id, true);
        Assert.Single(store.Catalog.Recipes);
        Assert.Single(store.Catalog.Gradients);
        Assert.Single(AssetStore.Open(store.Root).Catalog.Recipes);
    }

    [Fact]
    public void PackageAssets_CanBeHiddenIndependentlyAcrossRestartAndUpdate()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var package = store.Install(Package(dir, "1.0.0", 2));
        var recipeId = Assert.Single(package.Entries, e => e.Kind == AssetKinds.EffectRecipe).Id;
        var gradientId = Assert.Single(package.Entries, e => e.Kind == AssetKinds.Gradient).Id;

        store.SetPackageAssetEnabled(recipeId, false);
        Assert.Empty(store.Catalog.Recipes);
        Assert.Equal(gradientId, Assert.Single(store.Catalog.Gradients).Id);
        store = AssetStore.Open(store.Root);
        Assert.Empty(store.Catalog.Recipes);
        Assert.Single(store.Catalog.Gradients);
        store.Install(Package(dir, "2.0.0", 3));
        Assert.Empty(store.Catalog.Recipes);
        Assert.Single(store.Catalog.Gradients);

        store.SetEnabled(Id, false);
        store.Install(Package(dir, "3.0.0", 4));
        Assert.Empty(store.Catalog.Recipes);
        Assert.Empty(store.Catalog.Gradients);
        store.SetPackageAssetEnabled(recipeId, true);
        Assert.Equal(recipeId, Assert.Single(store.Catalog.Recipes).Id);
        Assert.Empty(store.Catalog.Gradients);
        store = AssetStore.Open(store.Root);
        Assert.Single(store.Catalog.Recipes);
        Assert.Empty(store.Catalog.Gradients);
        store.Delete(Id);
        store.RestoreDeleted(Assert.Single(store.DeletedPackages));
        Assert.Single(store.Catalog.Recipes);
        Assert.Empty(store.Catalog.Gradients);
    }

    [Fact]
    public void OfficialCatalogPackage_CanBeDisabledButNotDeleted()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var manifest = AssetSamples.Header(id: "gpixel.effects.demo") with { Author = new AssetAuthor("gpixel", "Gpixel") };
        var path = AssetSamples.Save(dir.Path, AssetSamples.Build(manifest));
        var package = store.Install(path, source: "remote");
        Assert.True(package.IsOfficial);

        store.SetEnabled(package.Id, false);
        Assert.False(store.Catalog.FindPackage(package.Id)!.Enabled);
        Assert.Throws<InvalidOperationException>(() => store.Delete(package.Id));
        Assert.NotNull(store.Catalog.FindPackage(package.Id));
        Assert.Empty(store.DeletedPackages);
        store.SetEnabled(package.Id, true);
        Assert.True(AssetStore.Open(store.Root).Catalog.FindPackage(package.Id)!.Enabled);
    }

    // ───────────── 삭제와 보관함 ─────────────

    [Fact]
    public void Delete_ArchivesPackageWithoutTouchingPersonalAssets_RestoreBringsItBack()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var personal = store.SavePersonalGradient(null, AssetSamples.Gradient("내 그라데이션") with { Favorite = true });
        store.Install(Package(dir, "1.0.0", 2));
        store.Install(Package(dir, "1.1.0", 3));
        var packageFiles = Tree(Path.Combine(store.PackagesDirectory, Id));
        byte[] personalFile = File.ReadAllBytes(store.PersonalPath);

        store.Delete(Id);
        Assert.False(Directory.Exists(Path.Combine(store.PackagesDirectory, Id)));
        Assert.Empty(InstalledIds(store));
        Assert.Empty(store.Catalog.Packages);
        Assert.Empty(store.Catalog.Recipes);
        Assert.Equal([personal.Id], store.Catalog.Gradients.Select(e => e.Id).ToArray());
        Assert.True(store.Catalog.Gradients[0].Favorite);
        Assert.Equal(personalFile, File.ReadAllBytes(store.PersonalPath));
        var deleted = Assert.Single(store.DeletedPackages);
        Assert.Equal((Id, "1.1.0", "Neon Pack"), (deleted.Id, deleted.Version, deleted.Name));
        // 다시 열어도 지운 상태(보관함은 남음)
        var reopened = AssetStore.Open(store.Root);
        Assert.Empty(reopened.Catalog.Packages);
        Assert.Single(reopened.DeletedPackages);

        reopened.RestoreDeleted(reopened.DeletedPackages[0]);
        Assert.Empty(reopened.DeletedPackages);
        Assert.Equal(3, ActiveThickness(reopened));
        Assert.Equal(["1.0.0"], reopened.Catalog.FindPackage(Id)!.State.PreviousVersions);
        Assert.Equal(packageFiles, Tree(Path.Combine(reopened.PackagesDirectory, Id)));
        Assert.Equal([Id], InstalledIds(reopened));
        Assert.True(reopened.Rollback(Id));
        Assert.Equal(2, ActiveThickness(reopened));
    }

    [Fact]
    public void DeletedArchive_RemainsRestorableBeyondThirtyDaysWhileWithinLatestTen()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 2));
        store.Delete(Id);
        string archive = Assert.Single(store.DeletedPackages).Folder;

        var entryPath = Path.Combine(archive, "entry.json");
        var entry = JsonNode.Parse(File.ReadAllBytes(entryPath))!.AsObject();
        entry["deletedAt"] = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        File.WriteAllText(entryPath, entry.ToJsonString());
        var reopened = AssetStore.Open(store.Root);
        Assert.Single(reopened.DeletedPackages);
        reopened.RestoreDeleted(reopened.DeletedPackages[0]);
        Assert.NotNull(reopened.Catalog.FindPackage(Id));
    }

    [Fact]
    public void RestoreDeleted_WhenSameIdIsInstalledAgain_IsVersionConflict()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 2));
        store.Delete(Id);
        store.Install(Package(dir, "1.1.0", 3));
        var ex = Assert.Throws<AssetPackageException>(() => store.RestoreDeleted(store.DeletedPackages[0]));
        Assert.Equal(AssetIssueCode.VersionConflict, Assert.Single(ex.Issues).Code);
        Assert.Equal(3, ActiveThickness(store));
        Assert.Single(store.DeletedPackages);
    }

    // ───────────── 업데이트 ─────────────

    [Fact]
    public void Update_KeepsPreviousVersionForRollback_AndPrunesOlderOnes()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 1));
        store.Install(Package(dir, "1.1.0", 3));
        Assert.Equal(3, ActiveThickness(store));
        Assert.Equal(["1.0.0"], store.Catalog.FindPackage(Id)!.State.PreviousVersions);
        Assert.Equal(["1.0.0", "1.1.0"], VersionFolders(store));

        Assert.True(store.Rollback(Id));
        Assert.Equal(1, ActiveThickness(store));
        Assert.Equal("1.0.0", store.Catalog.FindPackage(Id)!.Version);
        Assert.Equal(["1.1.0"], store.Catalog.FindPackage(Id)!.State.PreviousVersions);
        Assert.Equal(1, ActiveThickness(AssetStore.Open(store.Root)));

        // 이전 버전은 하나만 남긴다
        store.Install(Package(dir, "1.2.0", 2));
        Assert.Equal(2, ActiveThickness(store));
        Assert.Equal(["1.0.0"], store.Catalog.FindPackage(Id)!.State.PreviousVersions);
        Assert.Equal(["1.0.0", "1.2.0"], VersionFolders(store));
        Assert.False(store.Rollback("nobody.pack"));
    }

    [Fact]
    public void FailedUpdate_KeepsOldVersionActiveAndStoreUnchanged()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 2));
        var before = Tree(store.Root);

        var entries = AssetSamples.Unzip(AssetSamples.Build(AssetSamples.Header("1.1.0"), AssetSamples.Recipe(thickness: 3)));
        entries.Add(("README.md", "목록에 없음"u8.ToArray()));
        string broken = AssetSamples.Save(dir.Path, AssetSamples.Zip(entries), "broken.gpixel-asset");
        var ex = Assert.Throws<AssetPackageException>(() => store.Install(broken));
        Assert.Equal(AssetIssueCode.UnlistedFile, Assert.Single(ex.Issues).Code);

        Assert.Equal(before, Tree(store.Root));
        Assert.Equal(2, ActiveThickness(store));
        Assert.Equal(2, ActiveThickness(AssetStore.Open(store.Root)));
    }

    [Fact]
    public void SameVersionDifferentContent_IsVersionConflict_IdenticalReinstallIsAccepted()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        string original = Package(dir, "1.0.0", 2);
        store.Install(original);
        store.SetEnabled(Id, false);
        var before = Tree(store.VersionFolder(Id, "1.0.0"));

        var ex = Assert.Throws<AssetPackageException>(() => store.Install(Package(dir, "1.0.0", 3)));
        var issue = Assert.Single(ex.Issues);
        Assert.Equal((AssetIssueCode.VersionConflict, $"{Id} 1.0.0"), (issue.Code, issue.Detail));
        Assert.Equal(before, Tree(store.VersionFolder(Id, "1.0.0")));

        // 같은 내용을 다시 설치하면 그 버전을 켠다
        var again = store.Install(original);
        Assert.True(again.Enabled);
        Assert.Equal(2, ActiveThickness(store));
        Assert.Equal(["1.0.0"], VersionFolders(store));
        Assert.Equal(before, Tree(store.VersionFolder(Id, "1.0.0")));
    }

    // ───────────── 끊긴 설치·삭제 ─────────────

    /// <summary>
    /// 이전 버전이 설치된 저장소에 새 버전을 설치하면서 단계마다 저장소 전체를 복사해 둔다(그 순간 프로세스가 죽은 것과 같음).
    /// 각 사본을 열면: 상태 파일을 쓰기 전이면 이전 버전, 쓴 뒤면 새 버전이 활성이고, Staging과 남은 버전 폴더는 정리된다.
    /// </summary>
    [Theory]
    [InlineData("staged", "1.0.0", new[] { "1.0.0" })]
    [InlineData("extracted", "1.0.0", new[] { "1.0.0" })]
    [InlineData("moved", "1.0.0", new[] { "1.0.0" })]
    [InlineData("committed", "1.1.0", new[] { "1.0.0", "1.1.0" })]
    public void InterruptedInstall_ReopensToConsistentState(string step, string active, string[] folders)
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var personal = store.SavePersonalRecipe(EffectRecipe.Capture("", "내 효과", [new FadeEffect()]));
        store.Install(Package(dir, "1.0.0", 1));
        string snapshot = Path.Combine(dir.Path, "snapshot-" + step);
        var seen = new List<string>();
        store.StepHook = name =>
        {
            seen.Add(name);
            if (name == step) CopyTree(store.Root, snapshot);
        };
        store.Install(Package(dir, "1.1.0", 3));
        Assert.Contains(step, seen);
        // 사본에는 끊긴 설치의 흔적(Staging의 파일, 옮긴 뒤면 새 버전 폴더)이 있다
        Assert.NotEmpty(Directory.GetFileSystemEntries(Path.Combine(snapshot, "Staging")));
        Assert.Equal(step is "moved" or "committed", Directory.Exists(Path.Combine(snapshot, "Packages", Id, "1.1.0")));

        var recovered = AssetStore.Open(snapshot);
        var package = Assert.Single(recovered.Catalog.Packages);
        Assert.False(package.IsBroken);
        Assert.Equal(active, package.Version);
        Assert.Equal(active == "1.0.0" ? 1 : 3, ActiveThickness(recovered));
        Assert.Empty(Directory.GetFileSystemEntries(recovered.StagingDirectory));
        Assert.Equal(folders, VersionFolders(recovered));
        Assert.Equal([personal.Id], recovered.Catalog.Personal.Select(e => e.Id).ToArray());
        Assert.Empty(recovered.LoadErrors);

        // 복구한 저장소에서 설치를 다시 하면 끝까지 된다
        recovered.Install(Package(dir, "1.1.0", 3));
        Assert.Equal(3, ActiveThickness(recovered));
        Assert.Equal(["1.0.0"], recovered.Catalog.FindPackage(Id)!.State.PreviousVersions);
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("committed")]
    public void InterruptedDelete_ReopensWithDeleteCompletedAndRestorable(string step)
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var personal = store.SavePersonalGradient(null, AssetSamples.Gradient("내 것"));
        store.Install(Package(dir, "1.0.0", 2));
        string snapshot = Path.Combine(dir.Path, "snapshot-" + step);
        store.StepHook = name =>
        {
            if (name == step) CopyTree(store.Root, snapshot);
        };
        store.Delete(Id);
        Assert.True(Directory.Exists(snapshot));

        var recovered = AssetStore.Open(snapshot);
        Assert.Empty(recovered.Catalog.Packages);
        Assert.Empty(InstalledIds(recovered));
        Assert.False(Directory.Exists(Path.Combine(recovered.PackagesDirectory, Id)));
        Assert.Equal([personal.Id], recovered.Catalog.Gradients.Select(e => e.Id).ToArray());
        var deleted = Assert.Single(recovered.DeletedPackages);
        recovered.RestoreDeleted(deleted);
        Assert.Equal(2, ActiveThickness(recovered));
    }

    [Fact]
    public void Cancellation_LeavesStoreUnchangedAndStagingClean()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 2));
        var before = Tree(store.Root);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.Install(Package(dir, "1.1.0", 3), cancellation: cancelled.Token));
        Assert.Equal(before, Tree(store.Root));

        // 풀기 직전에 취소
        using var later = new CancellationTokenSource();
        store.StepHook = name =>
        {
            if (name == "staged") later.Cancel();
        };
        Assert.ThrowsAny<OperationCanceledException>(() => store.Install(Package(dir, "1.1.0", 3), cancellation: later.Token));
        Assert.Equal(before, Tree(store.Root));
        Assert.Equal(2, ActiveThickness(store));
    }

    [Fact]
    public void DiskWriteFailure_DuringExtraction_LeavesStoreUnchanged()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 2));
        var before = Tree(store.Root);
        store.StepHook = name =>
        {
            if (name == "extracted") throw new IOException("디스크가 가득 참");
        };
        Assert.Throws<IOException>(() => store.Install(Package(dir, "1.1.0", 3)));
        Assert.Equal(before, Tree(store.Root));
        Assert.Equal(2, ActiveThickness(store));

        // 옮긴 뒤 상태를 쓰기 전에 실패: 상태는 그대로, 반쯤 된 폴더는 다음에 열 때 지운다
        store.StepHook = name =>
        {
            if (name == "moved") throw new IOException("디스크가 가득 참");
        };
        Assert.Throws<IOException>(() => store.Install(Package(dir, "1.1.0", 3)));
        Assert.Equal(2, ActiveThickness(store));
        Assert.Equal(before[AssetStore.InstalledFileName], Tree(store.Root)[AssetStore.InstalledFileName]);
        var reopened = AssetStore.Open(store.Root);
        Assert.Equal(before, Tree(reopened.Root));
        Assert.Equal(2, ActiveThickness(reopened));
    }

    // ───────────── 손상된 상태 파일 ─────────────

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"packages\": [")]
    [InlineData("{ \"schemaVersion\": 2, \"packages\": [] }")]
    [InlineData("[]")]
    public void UnreadableInstalledFile_BlocksPackageChangesWithoutOverwritingIt(string content)
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 2));
        File.WriteAllText(store.InstalledPath, content);
        var packageFiles = Tree(store.PackagesDirectory);

        var broken = AssetStore.Open(store.Root);
        Assert.NotNull(broken.PackagesError);
        Assert.NotEmpty(broken.LoadErrors);
        Assert.Throws<InvalidOperationException>(() => broken.Install(Package(dir, "1.1.0", 3)));
        Assert.Equal(content, File.ReadAllText(broken.InstalledPath));
        // 읽지 못한 상태로 패키지 폴더를 지우지 않는다
        Assert.Equal(packageFiles, Tree(broken.PackagesDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(broken.StagingDirectory));
        // 개인 에셋은 따로 쓸 수 있다
        broken.SavePersonalRecipe(EffectRecipe.Capture("", "내 효과", [new FadeEffect()]));
        Assert.Single(broken.Catalog.Recipes);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"assets\": [")]
    [InlineData("{ \"schemaVersion\": 9, \"assets\": [], \"favorites\": [] }")]
    public void UnreadablePersonalFile_BlocksPersonalChangesWithoutOverwritingIt(string content)
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var saved = store.SavePersonalGradient(null, AssetSamples.Gradient());
        File.WriteAllText(store.PersonalPath, content);

        var broken = AssetStore.Open(store.Root);
        Assert.NotNull(broken.PersonalError);
        Assert.NotEmpty(broken.LoadErrors);
        Assert.Throws<InvalidOperationException>(() => broken.SavePersonalRecipe(EffectRecipe.Capture("", "새 효과", [new FadeEffect()])));
        Assert.Throws<InvalidOperationException>(() => broken.SavePersonalGradient(null, AssetSamples.Gradient("새 것")));
        Assert.Throws<InvalidOperationException>(() => broken.SetFavorite(saved.Id, true));
        Assert.Throws<InvalidOperationException>(() => broken.RemovePersonal(saved.Id));
        Assert.Throws<InvalidOperationException>(() => broken.RestoreBuiltInGradients());
        Assert.Equal(content, File.ReadAllText(broken.PersonalPath));
        // 마이그레이션도 덮어쓰지 않는다
        Assert.False(AssetMigration.Run(broken, dir.Path).Ran);
        Assert.Equal(content, File.ReadAllText(broken.PersonalPath));
        // 패키지 설치는 따로 된다
        broken.Install(Package(dir, "1.0.0", 2));
        Assert.Single(broken.Catalog.Recipes);
    }

    [Fact]
    public void PackageFilesChangedOnDisk_AreReportedBrokenAndHidden()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var installed = store.Install(Package(dir, "1.0.0", 2));
        string recipeFile = Path.Combine(installed.Folder, "assets", "effects", "neon-hit.json");
        File.WriteAllText(recipeFile, File.ReadAllText(recipeFile).Replace("Neon Hit", "Evil Hit"));

        var reopened = AssetStore.Open(store.Root);
        var package = Assert.Single(reopened.Catalog.Packages);
        Assert.True(package.IsBroken);
        Assert.Contains(package.Issues, i => i.Code == AssetIssueCode.HashMismatch && i.Path == "assets/effects/neon-hit.json");
        Assert.Empty(reopened.Catalog.Recipes);
        Assert.Empty(reopened.Catalog.Gradients);
    }

    // ───────────── 프로젝트와의 독립 ─────────────

    /// <summary>레시피를 넣은 프로젝트는 효과 값 사본을 가지므로 패키지를 끄거나 지워도, 저장했다 다시 열어도 그림이 같다.</summary>
    [Fact]
    public void ProjectRender_IsUnchangedAfterDisablingOrDeletingPackage()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 2));

        var project = new SpriteProject(24, 24);
        project.DuplicateFrame(0);
        var cel = new PixelImage(24, 24);
        for (int y = 8; y < 16; y++)
            for (int x = 8; x < 16; x++)
                cel[x, y] = new Color32(200, 40, 40);
        project.SetCel(0, 0, cel);
        project.SetCel(0, 1, cel);
        var plain = project.Flatten(0).Pixels.ToArray();
        project.SetLayerEffects(0, store.Catalog.Recipes.Single().Recipe!.Instantiate(project, null));
        var expected = Enumerable.Range(0, 2).Select(f => project.Flatten(f).Pixels.ToArray()).ToArray();
        Assert.NotEqual(plain, expected[0]);
        string file = Path.Combine(dir.Path, "hero.gpixel");
        ProjectIO.Save(project, file);

        void AssertSameRender(SpriteProject p)
        {
            for (int f = 0; f < 2; f++) Assert.Equal(expected[f], p.Flatten(f).Pixels);
        }

        store.SetEnabled(Id, false);
        AssertSameRender(project);
        store.Delete(Id);
        Assert.Empty(store.Catalog.Packages);
        AssertSameRender(project);
        var reloaded = ProjectIO.Load(file);
        AssertSameRender(reloaded);
        Assert.Equal(AssetSamples.EffectJson(project.Layers[0].Effects), AssetSamples.EffectJson(reloaded.Layers[0].Effects));
        // 같은 ID로 다른 내용의 새 버전을 설치해도 프로젝트는 그대로
        store.Install(Package(dir, "2.0.0", 3));
        AssertSameRender(ProjectIO.Load(file));
    }

    // ───────────── 즐겨찾기와 개인 에셋 ─────────────

    [Fact]
    public void PackageGradientFavorite_PersistsAcrossReopenAndReinstall()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        store.Install(Package(dir, "1.0.0", 2));
        string gradientId = Assert.Single(store.Catalog.Gradients).Id;

        store.SetFavorite(gradientId, true);
        var favorite = Assert.Single(store.Catalog.Gradients);
        Assert.True(favorite.Favorite);
        Assert.True(favorite.Gradient!.Favorite);
        Assert.True(Assert.Single(AssetStore.Open(store.Root).Catalog.Gradients).Favorite);

        store.Delete(Id);
        store.Install(Package(dir, "1.0.0", 2));
        Assert.True(Assert.Single(AssetStore.Open(store.Root).Catalog.Gradients).Favorite);

        store.SetFavorite(gradientId, false);
        Assert.False(Assert.Single(AssetStore.Open(store.Root).Catalog.Gradients).Favorite);
    }

    [Fact]
    public void PersonalAssets_UpdateInPlace_AndExportedPackageInstallsElsewhereWithSameValues()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var recipe = store.SavePersonalRecipe(AssetSamples.Recipe("내 네온"));
        Assert.StartsWith(AssetStore.PersonalIdPrefix, recipe.Id);
        var renamed = store.SavePersonalRecipe(recipe.Recipe! with { Name = "새 이름" });
        Assert.Equal(recipe.Id, renamed.Id);
        var gradient = store.SavePersonalGradient(null, AssetSamples.Gradient("Mine"));
        Assert.Equal([recipe.Id, gradient.Id], AssetStore.Open(store.Root).Catalog.Personal.Select(e => e.Id).ToArray());
        Assert.Equal("새 이름", AssetStore.Open(store.Root).Catalog.Recipes.Single().Name);

        using var exported = new MemoryStream();
        var manifest = store.ExportPersonal([recipe.Id, gradient.Id], AssetSamples.Header() with { Id = "me.exported" }, exported, e => AssetPreview.Png(e.Data));
        // 한국어 이름은 ID 조각이 되지 못해 종류 이름을 쓴다
        Assert.Equal(["me.exported.effect-recipe", "me.exported.mine"], manifest.Assets.Select(a => a.Id).ToArray());

        using var other = new TempDir();
        var target = AssetStore.Open(RootOf(other));
        target.Install(AssetSamples.Save(other.Path, exported.ToArray()));
        AssetSamples.AssertSameRecipe(AssetSamples.Recipe("새 이름"), Assert.Single(target.Catalog.Recipes).Recipe!);
        AssetSamples.AssertSameGradient(AssetSamples.Gradient("Mine"), Assert.Single(target.Catalog.Gradients).Gradient!);

        store.RemovePersonal(recipe.Id);
        Assert.Equal([gradient.Id], AssetStore.Open(store.Root).Catalog.Personal.Select(e => e.Id).ToArray());
    }

    /// <summary>같은 저장소를 연 두 앱이 번갈아 바꿔도 서로의 설치·개인 에셋을 덮어 지우지 않는다.</summary>
    [Fact]
    public void TwoOpenStores_DoNotOverwriteEachOthersChanges()
    {
        using var dir = new TempDir();
        var first = AssetStore.Open(RootOf(dir));
        var second = AssetStore.Open(first.Root);

        var recipe = first.SavePersonalRecipe(EffectRecipe.Capture("", "앞 앱", [new FadeEffect()]));
        var gradient = second.SavePersonalGradient(null, AssetSamples.Gradient("뒤 앱"));
        first.Install(Package(dir, "1.0.0", 2));
        second.SetFavorite(gradient.Id, true);

        var reopened = AssetStore.Open(first.Root);
        Assert.Equal([recipe.Id, gradient.Id], reopened.Catalog.Personal.Select(e => e.Id).ToArray());
        Assert.Single(reopened.Catalog.Packages);
        Assert.True(reopened.Catalog.Find(gradient.Id)!.Gradient!.Favorite);
    }

    /// <summary>이 앱이 모르는 효과가 든 개인 레시피는 패키지로 내보낼 수 없고, 출력에는 아무것도 쓰지 않는다.</summary>
    [Fact]
    public void ExportPersonal_WithUnknownEffect_FailsWithoutWritingOutput()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var unknown = Gpixel.Core.Effects.EffectCodec.Read(JsonNode.Parse("""{ "type": "gpixel.effect.future", "version": 1, "settings": {} }""")!.AsObject());
        Assert.IsType<MissingEffect>(unknown);
        var recipe = store.SavePersonalRecipe(EffectRecipe.Capture("", "Future", [new FadeEffect(), unknown]));

        using var output = new MemoryStream();
        var ex = Assert.Throws<AssetPackageException>(() => store.ExportPersonal([recipe.Id], AssetSamples.Header(), output));
        Assert.Contains(ex.Issues, i => i.Code == AssetIssueCode.UnknownEffect);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void BuiltInEffectDisabledIds_PersistWithoutRemovingEffectDefinitions()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var effect = EffectRegistry.BuiltIn[0];
        Assert.Contains(store.Catalog.BuiltInEffects, d => d.TypeId == effect.TypeId);

        store.SetBuiltInEffectEnabled(effect.TypeId, false);
        Assert.False(store.IsBuiltInEffectEnabled(effect.TypeId));
        Assert.DoesNotContain(store.Catalog.BuiltInEffects, d => d.TypeId == effect.TypeId);
        Assert.Same(effect, EffectRegistry.Find(effect.TypeId));
        Assert.IsType<OutlineEffect>(EffectCodec.Read(EffectCodec.Write(new OutlineEffect())));
        var reopened = AssetStore.Open(store.Root);
        Assert.False(reopened.IsBuiltInEffectEnabled(effect.TypeId));
        Assert.DoesNotContain(reopened.Catalog.BuiltInEffects, d => d.TypeId == effect.TypeId);
        reopened.SetBuiltInEffectEnabled(effect.TypeId, true);
        Assert.True(AssetStore.Open(store.Root).IsBuiltInEffectEnabled(effect.TypeId));
    }

    [Fact]
    public void DeletedPersonal_RestoresSameValueIdFavoriteAndDisabledStateAfterReopen()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var saved = store.SavePersonalGradient(null, AssetSamples.Gradient("Custom") with { Favorite = true });
        store.SetPersonalEnabled(saved.Id, false);
        store.RemovePersonal(saved.Id);
        Assert.Null(store.Catalog.Find(saved.Id));
        var archived = Assert.Single(store.DeletedPersonalAssets);
        Assert.Equal((saved.Id, saved.Name, AssetKinds.Gradient), (archived.Id, archived.Name, archived.Kind));
        var reopened = AssetStore.Open(store.Root);
        reopened.RestoreDeletedPersonal(Assert.Single(reopened.DeletedPersonalAssets));
        Assert.Empty(reopened.DeletedPersonalAssets);
        var restored = reopened.Catalog.Find(saved.Id)!;
        Assert.False(restored.Enabled);
        Assert.True(restored.Favorite);
        AssetSamples.AssertSameGradient(saved.Gradient!, restored.Gradient!);
        Assert.False(AssetStore.Open(store.Root).Catalog.Find(saved.Id)!.Enabled);
    }

    [Fact]
    public void InterruptedPersonalArchive_DoesNotDuplicateStillActiveAsset()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var asset = store.SavePersonalRecipe(AssetSamples.Recipe("Safe"));
        var original = File.ReadAllBytes(store.PersonalPath);
        store.RemovePersonal(asset.Id);
        Assert.Single(store.DeletedPersonalAssets);

        // Archive committed but the atomic personal-file replacement did not happen.
        File.WriteAllBytes(store.PersonalPath, original);
        var reopened = AssetStore.Open(store.Root);
        Assert.NotNull(reopened.Catalog.Find(asset.Id));
        Assert.Empty(reopened.DeletedPersonalAssets);
    }

    [Fact]
    public void MixedTrash_KeepsLatestTenAndEvictsOldestAcrossReopen()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(RootOf(dir));
        var personal = Enumerable.Range(0, 10)
            .Select(i => store.SavePersonalGradient(null, AssetSamples.Gradient($"Custom {i}"))).ToArray();
        store.RemovePersonal(personal[0].Id);
        string oldestFolder = Assert.Single(store.DeletedPersonalAssets).Folder;
        string archivePath = Path.Combine(oldestFolder, "personal.json");
        var archive = JsonNode.Parse(File.ReadAllBytes(archivePath))!.AsObject();
        archive["deletedAt"] = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        File.WriteAllText(archivePath, archive.ToJsonString());
        foreach (var asset in personal.Skip(1).Take(8)) store.RemovePersonal(asset.Id);
        store.Install(Package(dir, "1.0.0", 2));
        store.Delete(Id);

        var reopened = AssetStore.Open(store.Root);
        Assert.Equal(9, reopened.DeletedPersonalAssets.Count);
        Assert.Single(reopened.DeletedPackages);
        reopened.RemovePersonal(personal[9].Id);
        Assert.Equal(AssetStore.MaxDeletedItems, reopened.DeletedPersonalAssets.Count + reopened.DeletedPackages.Count);
        Assert.DoesNotContain(reopened.DeletedPersonalAssets, d => d.Id == personal[0].Id);
        Assert.False(Directory.Exists(oldestFolder));

        var final = AssetStore.Open(store.Root);
        foreach (var deleted in final.DeletedPersonalAssets) final.RestoreDeletedPersonal(deleted);
        final.RestoreDeleted(Assert.Single(final.DeletedPackages));
        Assert.Equal(personal.Skip(1).Select(e => e.Id).Order(StringComparer.Ordinal),
            final.Catalog.Personal.Select(e => e.Id).Order(StringComparer.Ordinal));
        Assert.NotNull(final.Catalog.FindPackage(Id));
        Assert.Empty(final.DeletedPersonalAssets);
        Assert.Empty(final.DeletedPackages);
    }
}
