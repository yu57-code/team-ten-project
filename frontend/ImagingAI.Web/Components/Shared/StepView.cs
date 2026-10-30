using ImagingAI.Web.Models;

namespace ImagingAI.Web.Components.Shared;

/// <summary>垂直處理進度中的一個步驟。</summary>
public sealed record StepView(int Number, string Title, string Description, StageState State, int Progress = 0);

/// <summary>把病例或上傳任務轉成垂直處理進度要顯示的步驟。</summary>
public static class StepViewBuilder
{
    /// <summary>總覽頁：完整 7 步。</summary>
    public static IReadOnlyList<StepView> ForCase(CaseRecord c)
    {
        var stages = Pipeline.Stages;

        if (c.Status == CaseStatus.Duplicate)
        {
            return stages.Select((s, i) => i switch
            {
                0 => new StepView(1, s.Title, "SHA-256 已存在，直接回傳既有病例結果", StageState.Done),
                _ when i == stages.Count - 1 => new StepView(i + 1, s.Title, $"沿用 {c.DuplicateOf} 的 FHIR 結果", StageState.Todo),
                _ => new StepView(i + 1, s.Title, "已略過（重複病例）", StageState.Todo),
            }).ToList();
        }

        return stages.Select((s, i) =>
        {
            var state = c.StageIndex >= stages.Count || i < c.StageIndex
                ? StageState.Done
                : i > c.StageIndex ? StageState.Todo : c.StageState;

            var description = c.StageNotes.TryGetValue(i, out var note)
                ? note
                : state switch
                {
                    StageState.Done => s.DoneText,
                    StageState.Running or StageState.Waiting => s.RunningText,
                    _ => s.TodoText,
                };

            return new StepView(i + 1, s.Title, description, state, c.Progress);
        }).ToList();
    }

    /// <summary>上傳頁：前 5 步（到結構化資料驗證）。</summary>
    public static IReadOnlyList<StepView> ForUpload(UploadJob job)
    {
        var stages = Pipeline.Stages.Take(Pipeline.UploadStageCount).ToList();

        if (job.State == UploadJobState.Duplicate)
        {
            return stages.Select((s, i) => i == 0
                ? new StepView(1, s.Title, $"SHA-256 已存在，已回傳 {job.DuplicateOf} 的結果", StageState.Done)
                : new StepView(i + 1, s.Title, "已略過（重複病例）", StageState.Todo)).ToList();
        }

        return stages.Select((s, i) =>
        {
            var state = i < job.StageIndex ? StageState.Done : i == job.StageIndex ? StageState.Running : StageState.Todo;
            var description = state switch
            {
                StageState.Done => s.DoneText,
                StageState.Running => s.RunningText,
                _ => s.TodoText,
            };
            return new StepView(i + 1, s.Title, description, state, job.Progress);
        }).ToList();
    }
}
