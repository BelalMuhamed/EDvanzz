using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Interfaces;

// ════════════════════════════════════════════════════════════════════════════
// ONLINE EXAM MODULE — REPOSITORY PROJECTION TYPES
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// §3.2 reconciliation snapshot for one exam. <c>Assigned</c> comes from the live
/// scope resolution (never cached); <c>StatusCounts</c> comes straight from the
/// report table; <c>NotAttended</c> is computed by the caller as
/// <c>Assigned.Except(ReportedTeacherStudentIds)</c> — never by joining outward
/// from the assigned set (do-not-reintroduce #7).
/// </summary>
public sealed class OnlineExamReportStatusCounts
{
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Blocked { get; set; }
    public int InProgress { get; set; }
}

/// <summary>One exam's grouped-list row (§3.3 T4/S1) — counts only, no per-student detail.</summary>
public sealed class OnlineExamListAggregateRow
{
    public long OnlineExamId { get; set; }
    public int AssignedCount { get; set; }
    public OnlineExamReportStatusCounts StatusCounts { get; set; } = new();
}

/// <summary>
/// Teacher-facing question+options projection — includes <see cref="OnlineExamQuestionOptionRow.IsCorrect"/>.
/// Used by T12 (questions edit screen) and T10 (questions overview).
/// </summary>
public sealed class OnlineExamQuestionRow
{
    public long Id { get; set; }
    public string QuestionText { get; set; } = null!;
    public OnlineExamQuestionType QuestionType { get; set; }
    public decimal Degree { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Internal registry FK (FileObject.Id), repo-projected. Never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long? ImageFileInternalId { get; set; }

    /// <summary>
    /// The image's <c>fileId</c> (FileObject.PublicId), or null — populated by the service.
    /// This is what the edit screen resends in <c>imageFileId</c> to KEEP the image on a
    /// replace-questions call.
    /// </summary>
    public Guid? ImageFileId { get; set; }

    /// <summary>Gated image URL, populated by the service (not by the repo).</summary>
    public string? ImageUrl { get; set; }

    public List<OnlineExamQuestionOptionRow> Options { get; set; } = new();
}

public sealed class OnlineExamQuestionOptionRow
{
    public long Id { get; set; }
    public string OptionText { get; set; } = null!;
    public bool IsCorrect { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Student-facing take-screen projection (S2) — <c>IsCorrect</c> does not exist on
/// this type at all. This is the "dedicated projection — security" the plan calls
/// for: omission by shape, not by DTO-mapper discipline (do-not-reintroduce #9).
/// </summary>
public sealed class StudentOnlineExamQuestionRow
{
    public long Id { get; set; }
    public string QuestionText { get; set; } = null!;
    public OnlineExamQuestionType QuestionType { get; set; }
    public decimal Degree { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Internal registry FK (FileObject.Id), repo-projected. Never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long? ImageFileInternalId { get; set; }

    /// <summary>Gated image URL, populated by the service (not by the repo). Students only need the URL.</summary>
    public string? ImageUrl { get; set; }

    public List<StudentOnlineExamQuestionOptionRow> Options { get; set; } = new();
}

public sealed class StudentOnlineExamQuestionOptionRow
{
    public long Id { get; set; }
    public string OptionText { get; set; } = null!;
    public int SortOrder { get; set; }
}
/// <summary>Assigned-student identity row (§3.2/T7 grid) — name+code alongside the id.</summary>
public sealed class AssignedStudentRow
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = string.Empty;
    public string? StudentCode { get; set; }
    public long? SessionId { get; set; }
    public long? SessionGroupId { get; set; }
}

/// <summary>
/// S1 subject-label source (<see cref="IOnlineExamRepo.GetTeacherSubjectNameAsync"/>). Carries
/// the teacher's free-text <c>CustomSubject</c> plus the first linked ministry subject's
/// bilingual names; the service localizes to the request culture, replicating the canonical
/// StudentUserService subject-name resolution. Fetched ONCE per S1 call (never per exam → no N+1).
/// </summary>
public sealed class TeacherSubjectNameRow
{
    public string? CustomSubject { get; set; }
    public string? SubjectNameEn { get; set; }
    public string? SubjectNameAr { get; set; }
}
/// <summary>
/// One question's outcome across every FINALIZED attempt at an exam — the row
/// behind the teacher's per-question analysis.
///
/// Only finalized reports are counted: an in-progress answer can still change,
/// and a difficulty figure that moves while students are mid-exam is worse than
/// no figure. <see cref="AttemptedCount"/> is the denominator, not the assigned
/// headcount, so "8 of 10 got it right" always means eight of the ten who
/// actually answered it.
/// </summary>
public sealed class OnlineExamQuestionStatRow
{
    public long QuestionId { get; set; }

    /// <summary>Finalized attempts that answered this question at all.</summary>
    public int AttemptedCount { get; set; }

    /// <summary>Of <see cref="AttemptedCount"/>, how many earned the question's full degree.</summary>
    public int CorrectCount { get; set; }
}

/// <summary>
/// How often one wrong option was picked for a question. The most-picked wrong
/// option is the actionable half of question analysis — it names the specific
/// misconception a class shares, not merely that the question was hard.
/// </summary>
public sealed class OnlineExamWrongOptionRow
{
    public long QuestionId { get; set; }
    public long OptionId { get; set; }
    public string OptionText { get; set; } = string.Empty;
    public int PickedCount { get; set; }
}
