using System.ComponentModel.DataAnnotations;

namespace Edvanz.Application.Dtos.Exams;

/// <summary>
/// Batch grade save — distinct per-student grades committed together ("Submit (N) students" /
/// "Saved (N) changes"). Each item carries its own grade and concurrency token.
/// </summary>
public class BatchGradeDto
{
    /// <summary>The exam (template) these grades belong to. Each student resolves to their single obligation in it.</summary>
    public long ExamId { get; set; }

    public List<GradeItemDto> Items { get; set; } = new();
}

/// <summary>One student's grade within a batch save.</summary>
public class GradeItemDto
{
    /// <summary>The student to grade; resolved to their obligation within <see cref="BatchGradeDto.ExamId"/>.</summary>
    public long TeacherStudentId { get; set; }

    /// <summary>
    /// The grade to record: 0 ≤ grade ≤ the exam's max. Send <c>null</c> to CLEAR a previously entered
    /// grade — the student reverts from AttendedWithGrade to Attended (attendance is preserved). A null
    /// on an already-ungraded or absent row is a harmless no-op.
    /// </summary>
    public decimal? Grade { get; set; }

    /// <summary>Concurrency token from the last read of this student's row (base64 is also accepted by binding).
    /// Required — omitting it would otherwise bind to null and surface as a misleading 409 conflict.</summary>
    [Required]
    public byte[] RowVersion { get; set; } = null!;
}

/// <summary>
/// Result of a batch grade save. Valid rows are applied; invalid rows are reported per-item with a
/// stable code (the whole batch still returns 200 — check <see cref="AllSucceeded"/> / per-item
/// <see cref="BatchGradeItemResultDto.Success"/>). A row whose concurrency token is stale is ONE
/// such per-item failure (code <c>ObligationConcurrencyConflict</c>), carrying the current server
/// status/grade and a fresh token; the rest of the batch still saves. The 409 remains only for a
/// row that changes between this request's read and its write.
/// </summary>
public class BatchGradeResultDto
{
    public int UpdatedCount { get; set; }
    public bool AllSucceeded { get; set; }
    public List<BatchGradeItemResultDto> Items { get; set; } = new();
}

public class BatchGradeItemResultDto
{
    /// <summary>The student this result is for (echoes the request).</summary>
    public long TeacherStudentId { get; set; }

    /// <summary>The resolved obligation id (null when the student could not be resolved).</summary>
    public long? ObligationId { get; set; }

    public bool Success { get; set; }

    /// <summary>Stable failure code when <see cref="Success"/> is false (e.g. "GradeExceedsMax").</summary>
    public string? Code { get; set; }

    /// <summary>Status name after the save; on an <c>ObligationConcurrencyConflict</c> row, the
    /// CURRENT server status instead (what the row became while the client held it).</summary>
    public string? Status { get; set; }

    /// <summary>Grade after the save; on a conflict row, the current server grade.</summary>
    public decimal? Grade { get; set; }

    /// <summary>Fresh base64 concurrency token for this row — after a successful save, or on a
    /// conflict row so the client can re-apply the teacher's value without a reload.</summary>
    public string? RowVersion { get; set; }
}
