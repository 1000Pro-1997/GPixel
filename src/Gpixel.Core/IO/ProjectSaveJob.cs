using System.Runtime.ExceptionServices;
using Gpixel.Core.Model;

namespace Gpixel.Core.IO;

/// <summary>
/// 나눠 부르는 저장(<see cref="ProjectIO.BeginSave"/>): <see cref="Run"/>이 파일을 쓰고(프로젝트는 읽기만), <see cref="Finish"/>가 결과를 프로젝트에 반영한다.
/// 둘을 이어 부르면 <see cref="ProjectIO.SaveWithExport"/>와 같다. 사본으로 준비했으면 <see cref="Run"/>은 사본을 쓰고 결과는 원래 프로젝트에 반영한다.
/// </summary>
public sealed class ProjectSaveJob
{
    private readonly SpriteProject _project;
    /// <summary>파일에 쓰는 프로젝트(원래 프로젝트 또는 그 사본).</summary>
    private readonly SpriteProject _source;
    private readonly IReadOnlyList<string>? _previousRecord;
    private readonly ExportPlan? _plan;
    private readonly Exception? _planError;
    private bool _ran;
    private ExceptionDispatchInfo? _saveError, _exportError;
    private ProjectExportResult? _export;
    /// <summary>준비할 때의 <see cref="SpriteProject.ChangeStamp"/> — 쓰는 동안 바뀌었으면 끝나도 수정됨을 끄지 않는다.</summary>
    private readonly long _stamp;

    internal ProjectSaveJob(SpriteProject project, SpriteProject source, string fullPath, IReadOnlyList<string>? previousRecord, ExportPlan? plan, Exception? planError)
    {
        _project = project;
        _source = source;
        ProjectPath = fullPath;
        _previousRecord = previousRecord;
        _plan = plan;
        _planError = planError;
        _stamp = project.ChangeStamp;
    }

    /// <summary>저장할 프로젝트 파일 전체 경로.</summary>
    public string ProjectPath { get; }

    /// <summary>
    /// <c>.gpixel</c>을 쓰고, 내보내기가 켜져 있으면 결과 파일도 쓴다. 실패는 던지지 않고 담아 두었다가 <see cref="Finish"/>에서 던진다.
    /// 한 번만 부른다. 사본이 아니면 도는 동안 프로젝트를 고치면 안 된다.
    /// </summary>
    /// <param name="progress">진행: 셀 저장 → 내보내기 단계를 시작할 때 모두 잡는다.</param>
    public void Run(WorkProgress? progress = null)
    {
        if (_ran) throw new InvalidOperationException(nameof(Run));
        _ran = true;
        var save = progress?.Begin(ProjectIO.CelUnits(_source));
        var export = _plan is null ? null : progress?.Begin(ProjectExport.EstimateWork(_source, _plan));
        try
        {
            ProjectIO.WriteFile(_source, ProjectPath, save);
        }
        catch (Exception ex)
        {
            _saveError = ExceptionDispatchInfo.Capture(ex);
            return;
        }
        finally
        {
            save?.Dispose();
        }
        if (_plan is null) return;
        try
        {
            _export = ProjectExport.Write(_source, _plan, export);
        }
        catch (Exception ex)
        {
            _exportError = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            export?.Dispose();
        }
    }

    /// <summary>
    /// 결과 반영: 쓰지 못했으면 기록을 되돌리고 그 예외를, 썼으면 경로·수정됨을 갱신한 뒤 준비·내보내기 실패가 있으면 그 예외를 던진다.
    /// 쓰는 동안 프로젝트가 바뀌었으면(<see cref="SpriteProject.ChangeStamp"/>) 파일에 없는 변경이 있으므로 수정됨을 그대로 둔다.
    /// </summary>
    public ProjectSaveResult Finish()
    {
        if (!_ran) throw new InvalidOperationException(nameof(Finish));
        if (_saveError is not null)
        {
            _project.ExportedFiles = _previousRecord;
            _saveError.Throw();
        }
        bool unchanged = _project.ChangeStamp == _stamp;
        _project.Path = ProjectPath;
        if (unchanged) _project.IsDirty = false;
        if (_planError is not null) ExceptionDispatchInfo.Throw(_planError);
        _exportError?.Throw();
        return new ProjectSaveResult(ProjectPath, _export);
    }
}
