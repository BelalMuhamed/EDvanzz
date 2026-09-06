using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;

namespace Edvanz.Application.Dtos;

/// <summary>S1 per-exam row. StudentDegree/StudentStatus null when not yet attempted (upcoming).</summary>
public sealed class OnlineExamStudentListItemDto
{
    public long ExamId { get; set; }
    public string ExamName { get; set; } = string.Empty;

    /// <summary>
    /// Teacher's subject label — the free-text <c>CustomSubject</c>, else the first linked
    /// ministry subject localized to the caller's language (Accept-Language → the same culture
    /// the localizer uses), replicating the canonical StudentUserService subject-name pattern.
    /// Resolved ONCE per list call (no N+1). Empty string when the teacher has no subject set.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    public DateOnly ExamDate { get; set; }
    public TimeOnly ExamTime { get; set; }

    /// <summary>
    /// O3: the exam WINDOW length (<c>EndDateTime − StartDateTime</c>), a convenience for the
    /// client. This is NOT a per-attempt time allowance and there is intentionally NO
    /// server-side per-attempt timer / DurationMinutes field — the countdown is driven entirely
    /// by the window (start = <see cref="ExamDate"/>+<see cref="ExamTime"/>; end = start + this),
    /// and the window End is the single hard stop (enforced by submission-window validation and
    /// §3.5 auto-finalize). Leaving and reopening never resets time.
    /// </summary>
    public TimeSpan Duration { get; set; }

    public int QuestionsCount { get; set; }
    public decimal ExamDegree { get; set; }
    public decimal? StudentDegree { get; set; }
    public string? StudentStatus { get; set; }
}

/// <summary>S1 response — split per QD.</summary>
public sealed class StudentOnlineExamListDto
{
    public List<OnlineExamStudentListItemDto> Upcoming { get; set; } = new();
    public List<OnlineExamStudentListItemDto> Past { get; set; } = new();
}

/// <summary>S2 take-screen response — no IsCorrect anywhere on this shape (security by projection, do-not-reintroduce #9).</summary>
public sealed class OnlineExamTakeScreenDto
{
    public long ExamId { get; set; }
    public string ExamName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Instructions { get; set; }

    /// <summary>
    /// O3: the exam window opens at <see cref="StartDateTime"/> and closes at
    /// <see cref="EndDateTime"/>. The client drives the countdown from these two values
    /// (<c>remaining = EndDateTime − now</c>; window length = <c>EndDateTime − StartDateTime</c>).
    /// There is intentionally NO per-attempt server-side timer and NO DurationMinutes/time-limit
    /// field — the window End is the single hard stop, enforced server-side by submission-window
    /// validation and §3.5 window-end auto-finalize. Leaving and reopening the take screen never
    /// resets time.
    /// </summary>
    public DateTime StartDateTime { get; set; }

    /// <inheritdoc cref="StartDateTime"/>
    public DateTime EndDateTime { get; set; }

    public decimal ExamDegree { get; set; }

    /// <summary>
    /// The caller's attempt status — <c>InProgress</c> / <c>Passed</c> / <c>Failed</c> /
    /// <c>Blocked</c> — or null when the student has never touched this exam. Lets the app
    /// render a blocked/finalized state on re-entry instead of discovering it at submit.
    /// </summary>
    public string? StudentStatus { get; set; }

    // ── Anti-cheat (leave-the-exam tolerance) ──
    /// <summary>When true, the app must warn on violations and block once <see cref="MaxViolations"/> is reached.</summary>
    public bool BlockOnViolation { get; set; }
    /// <summary>Violations tolerated before auto-block. The app warns before, blocks on the Nth.</summary>
    public int MaxViolations { get; set; }
    /// <summary>The caller's current server-side violation count, so re-entry after backgrounding shows the correct remaining tolerance.</summary>
    public int ViolationCount { get; set; }

    public List<StudentOnlineExamQuestionRow> Questions { get; set; } = new();
}

public sealed class OnlineExamReviewOptionDto
{
    public long OptionId { get; set; }
    public string OptionText { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
    /// <summary>Null until Finalized=true (do-not-reintroduce #9).</summary>
    public bool? IsCorrect { get; set; }
}

public sealed class OnlineExamReviewQuestionDto
{
    public long QuestionId { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public OnlineExamQuestionType QuestionType { get; set; }
    public decimal Degree { get; set; }
    /// <summary>Null until Finalized=true.</summary>
    public decimal? AwardedDegree { get; set; }

    /// <summary>Gated question-image URL, or null — same URL the take screen showed.</summary>
    public string? ImageUrl { get; set; }

    public List<OnlineExamReviewOptionDto> Options { get; set; } = new();
}

/// <summary>S6 response.</summary>
public sealed class OnlineExamReviewDto
{
    public long ExamId { get; set; }
    public string ExamName { get; set; } = string.Empty;
    public bool Finalized { get; set; }
    public string? ReportStatus { get; set; }
    public decimal? Score { get; set; }
    public decimal? Percentage { get; set; }

    /// <summary>
    /// Whose sheet this is. Filled ONLY by the teacher-facing answer-sheet
    /// endpoint (T16) so the screen can title itself; the student's own review
    /// leaves it null — they already know who they are.
    /// </summary>
    public string? StudentName { get; set; }

    /// <inheritdoc cref="StudentName"/>
    public string? StudentCode { get; set; }

    public List<OnlineExamReviewQuestionDto> Questions { get; set; } = new();
}
public sealed class SubmitOnlineExamAnswerRequest
{
    public long QuestionId { get; set; }
    public List<long> SelectedOptionIds { get; set; } = new();
}

/// <summary>S3 bulk body — any subset of questions; unanswered ones stay NotAnswered.</summary>
public sealed class SubmitOnlineExamRequest
{
    public List<SubmitOnlineExamAnswerRequest> Answers { get; set; } = new();
}

/// <summary>Shared stats shape — S3/S4/S5, the teacher T5s block, and the O1 student self-block.</summary>
public sealed class OnlineExamStatsDto
{
    public decimal Percentage { get; set; }
    public int Stars { get; set; }
    public int NotAnswered { get; set; }
    public int Correct { get; set; }
    public int Wrong { get; set; }

    /// <summary>
    /// The report status these stats belong to — <c>InProgress</c> / <c>Passed</c> /
    /// <c>Failed</c> / <c>Blocked</c> — so a result read is distinguishable from
    /// not-attempted and a blocked attempt is visible on every stats surface. Null only
    /// on paths that carry no report (kept nullable for wire back-compat).
    /// </summary>
    public string? Status { get; set; }

    /// <summary>The caller's current anti-cheat violation count (0 when no report / not applicable).</summary>
    public int ViolationCount { get; set; }
}

/// <summary>Response of the O2 violation endpoint — server-authoritative tolerance state.</summary>
public sealed class ViolationRecordedDto
{
    /// <summary>The caller's violation count AFTER this violation was recorded.</summary>
    public int ViolationCount { get; set; }
    /// <summary>The exam's configured tolerance (block on the Nth). Echoed so the app shows the right warning copy.</summary>
    public int MaxViolations { get; set; }
    /// <summary>True when this violation pushed the report to Blocked (count reached the tolerance and BlockOnViolation is on).</summary>
    public bool IsBlocked { get; set; }
}