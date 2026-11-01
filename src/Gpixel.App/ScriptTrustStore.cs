using Gpixel.Core.Model;
using Gpixel.Core.Scripting;

namespace Gpixel.App;

/// <summary>
/// 스크립트 효과 신뢰 결정(13.5)을 앱 설정(<see cref="AppSettings.ScriptTrust"/>)에 두고 Core의 <see cref="ScriptLibrary"/>와 맞춘다.
/// 다른 사람이 만든 프로젝트를 열면 처음 보는 스크립트를 묻고(<see cref="ScriptTrustDialog"/>), 이 컴퓨터에서 쓰거나 가져온 스크립트는 바로 믿는다.
/// 실행할 스크립트가 있으면 작업 스레드에서 실행기를 미리 띄운다(13.4).
/// </summary>
internal static class ScriptTrustStore
{
    /// <summary>앱 시작: 저장해 둔 결정을 Core에 넣는다.</summary>
    public static void Load(AppSettings settings) => ScriptLibrary.LoadTrust(settings.ScriptTrust ??= new());

    /// <summary>결정을 정하고 설정 파일에 바로 쓴다(앱이 비정상 종료해도 다시 묻지 않게).</summary>
    public static void Decide(IReadOnlyDictionary<string, bool> decisions)
    {
        if (decisions.Count == 0) return;
        var saved = App.Settings.ScriptTrust ??= new();
        foreach (var (hash, run) in decisions) saved[hash] = run;
        App.Settings.Save();
        foreach (var (hash, run) in decisions) ScriptLibrary.SetTrust(hash, run);
    }

    /// <summary>이 컴퓨터에서 쓰거나 가져온 스크립트: 묻지 않고 믿는다.</summary>
    public static void TrustLocal(string hash)
    {
        if (ScriptLibrary.TrustOf(hash) != ScriptTrust.Trusted) Decide(new Dictionary<string, bool> { [hash] = true });
    }

    /// <summary>
    /// 프로젝트를 연 뒤: 원문이 있는데 아직 묻지 않은 스크립트를 묻고(<paramref name="interactive"/>일 때만 — 아니면 실행하지 않은 채로 두고
    /// 효과 창에서 허용할 수 있다), 실행할 스크립트는 작업 스레드에서 미리 해석하고 실행기를 띄운다.
    /// </summary>
    public static void Opened(Window owner, SpriteProject project, bool interactive)
    {
        var used = project.ScriptHashes();
        if (used.Count == 0) return;
        var unseen = used.Where(h => project.Scripts.ContainsKey(h) && ScriptLibrary.TrustOf(h) == ScriptTrust.Unknown).ToList();
        if (interactive && unseen.Count > 0)
        {
            var dialog = new ScriptTrustDialog(unseen);
            dialog.ShowDialog(owner);
            Decide(dialog.Decisions);
        }
        Prewarm(used);
    }

    /// <summary>믿는 스크립트를 작업 스레드에서 미리 해석하고 그 스레드의 실행기를 띄운다(Jint 첫 실행이 느려서, 13.4).</summary>
    public static void Prewarm(IEnumerable<string> hashes)
    {
        var trusted = hashes.Where(h => ScriptLibrary.TrustOf(h) == ScriptTrust.Trusted).ToList();
        if (trusted.Count > 0) Task.Run(() => ScriptLibrary.Prewarm(trusted));
    }
}
