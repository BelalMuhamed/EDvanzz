using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Extended repository for the Online Exam Module — teacher-side aggregate
/// (<see cref="OnlineExam"/> + its Questions/Options/Scopes children).
///
/// Inherits <see cref="IGenericRepo{T, Tkey}"/> over <see cref="OnlineExam"/> for
/// basic CRUD on the primary entity — same pattern as <c>IExamHomeworkRepo</c> /
/// <c>IVideoAssetRepo</c>. Child entities are accessed via named methods; no raw
/// predicates cross into the Application layer.
///
/// PHASE NOTE: this interface carries only the CRUD-support surface needed for the
/// Phase 1 migration gate. Query methods for §3.1 (live assigned set), §3.2 (count
/// reconciliation), §3.3 (grouped list), and the questions±IsCorrect projections
/// land in Phase 2 against verified signatures — not invented here.
/// </summary>
public interface IOnlineExamRepo : IGenericRepo<OnlineExam, long>
{
    /// <summary>Loads an exam scoped to its owning teacher. Returns null on mismatch/not-found.</summary>
    Task<OnlineExam?> GetByIdAndTeacherAsync(long onlineExamId, long teacherId);

    /// <summary>
    /// Counts the online exams owned by a teacher (free-tier quota key
    /// ModuleQuotaKeys.OnlineExams). OnlineExam is hard-delete, so a plain count is exact.
    /// </summary>
    Task<int> CountByTeacherAsync(long teacherId);

    Task AddQuestionsRangeAsync(IEnumerable<OnlineExamQuestion> questions);

    Task AddScopesRangeAsync(IEnumerable<OnlineExamScope> scopes);

    /// <summary>
    /// Purges an exam's full graph leaf-first (§3.7), in the caller's ambient
    /// transaction: StudentQuestionAnswerOptions → StudentQuestionAnswers →
    /// StudentOnlineExamReports → OnlineExamQuestionOptions → OnlineExamQuestions →
    /// OnlineExamScopes → OnlineExam. Pattern: <c>ExamHomeworkRepo.PurgeExamGraphAsync</c>.
    /// Every child FK is NoAction, so nothing cascades on its own — this method owns
    /// the full teardown via set-based <c>ExecuteDeleteAsync</c> calls.
    /// </summary>
    Task PurgeExamGraphAsync(long onlineExamId);

    /// <summary>
    /// Hard-deletes every <c>OnlineExamScope</c> row targeting a given session
    /// (<c>ScopeType = Session</c>). Called by <c>SessionService.DeleteSessionAsync</c>
    /// BEFORE the session row is hard-deleted: the <c>OnlineExamScopes.SessionId</c>
    /// FK is <c>NoAction</c>, so any row still targeting the session would otherwise
    /// block the delete with a 409 "conflicts with existing data". The scope row is a
    /// live assignment rule, not history, and its CHECK constraint forbids nulling the
    /// FK — so the row is removed. Set-based <c>ExecuteDeleteAsync</c>, one round trip.
    /// </summary>
    Task DeleteScopesBySessionAsync(long sessionId);

    /// <summary>
    /// Hard-deletes every <c>OnlineExamScope</c> row targeting a given session group
    /// (<c>ScopeType = SessionGroup</c>). Session-group counterpart of
    /// <see cref="DeleteScopesBySessionAsync"/>, called by
    /// <c>SessionService.DeleteGroupAsync</c> so the <c>NoAction</c>
    /// <c>SessionGroupId</c> FK cannot block the group delete.
    /// </summary>
    Task DeleteScopesByGroupAsync(long sessionGroupId);
    // ══════════════════════════════════════════════════════════════════════
    // §3.1 — LIVE ASSIGNED SET (BR-VCM-01 pattern: composable, never materialized)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Composable query resolving the current assigned-student set for an exam —
    /// union of the Session branch (OnlineExamScopes⋈TeacherStudents on SessionId)
    /// and the SessionGroup branch (OnlineExamScopes⋈Sessions⋈TeacherStudents).
    /// Caller composes further (Count/Contains/Except) — never enumerated here.
    /// Reuses the <c>VideoAssetRepo.GetResolvedStudentIdsForVideoQuery</c> shape.
    /// </summary>
    IQueryable<long> BuildAssignedStudentIdsQuery(long onlineExamId, long teacherId);

    // ══════════════════════════════════════════════════════════════════════
    // §3.3 — GROUPED SCOPE FETCH (list page, 2 queries not 2N)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Batch scope fetch across a page of exam ids, Session/SessionGroup eager-loaded.</summary>
    Task<IReadOnlyList<OnlineExamScope>> GetScopesByExamIdsAsync(IEnumerable<long> onlineExamIds);

    // ══════════════════════════════════════════════════════════════════════
    // QUESTIONS + OPTIONS — WITH / WITHOUT IsCorrect
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Teacher-facing: full projection including IsCorrect (T10/T12).</summary>
    Task<IReadOnlyList<OnlineExamQuestionRow>> GetQuestionsForTeacherAsync(long onlineExamId);

    /// <summary>Student-facing: dedicated projection with no IsCorrect field on the shape at all (S2).</summary>
    Task<IReadOnlyList<StudentOnlineExamQuestionRow>> GetQuestionsForStudentAsync(long onlineExamId);

    /// <summary>
    /// Per-question outcome counts across FINALIZED attempts, plus how often each
    /// wrong option was picked. Two grouped aggregates, not one row per answer —
    /// the caller stitches them by QuestionId.
    ///
    /// Scoped to the exam only; the CALLER must have already proved the exam
    /// belongs to the tenant (GetByIdAndTeacherAsync), exactly as the other
    /// analysis reads on this repo do.
    /// </summary>
    Task<(IReadOnlyList<OnlineExamQuestionStatRow> Stats,
          IReadOnlyList<OnlineExamWrongOptionRow> WrongOptions,
          int FinalizedReportCount)>
        GetQuestionAnalysisAsync(long onlineExamId);

    /// <summary>Sum of Question.Degree — the exam's live-computed total grade (never stored, do-not-reintroduce #6).</summary>
    Task<decimal> GetTotalDegreeAsync(long onlineExamId);

    /// <summary>Session/SessionGroup ownership check for a scope target (mirrors IVideoAssetRepo.IsScopeTargetOwnedByTeacherAsync).</summary>
    Task<bool> IsScopeTargetOwnedByTeacherAsync(long teacherId, OnlineExamScopeType scopeType, long targetId);

    Task<(IReadOnlyList<OnlineExam> Items, int TotalCount)> GetPagedForTeacherAsync(
        long teacherId, OnlineExamStatus? status, string? search, int page, int pageSize);

    /// <summary>§3.3 T4 batch — one grouped query for a page's assigned counts, not one call per row.</summary>
    Task<Dictionary<long, int>> GetAssignedCountsByExamIdsAsync(IEnumerable<long> onlineExamIds, long teacherId);

    /// <summary>T4 batch — one grouped query for a page's per-exam question counts, not one call per row.</summary>
    Task<Dictionary<long, int>> GetQuestionCountsByExamIdsAsync(IEnumerable<long> onlineExamIds);

    /// <summary>T7 grid — assigned-student identities with name/code, materialized (small sets only — one exam's roster).</summary>
    Task<IReadOnlyList<AssignedStudentRow>> GetAssignedStudentsAsync(long onlineExamId, long teacherId);

    /// <summary>T13 replace-all questions (Draft-only) — leaf-first delete then insert, in the service's transaction.</summary>
    Task ReplaceQuestionsAsync(long onlineExamId, IEnumerable<OnlineExamQuestion> newQuestions);
    /// <summary>
    /// §T6 overview — per-scope assigned counts in 2 grouped queries (one for Session
    /// scopes, one for SessionGroup scopes), not one query per scope row.
    /// </summary>
    Task<Dictionary<long, int>> GetAssignedCountsPerScopeAsync(long onlineExamId, long teacherId);
    /// <summary>Student → Session/SessionGroup context, for reverse scope resolution (student sees "my exams").</summary>
    Task<(long? SessionId, long? SessionGroupId)> GetStudentSessionContextAsync(long teacherStudentId);

    /// <summary>
    /// Reverse of the teacher-side resolver: exams (any non-Draft status) whose scope
    /// includes this student's session or session-group, for this teacher. Questions
    /// eager-loaded (small per-exam cardinality) so the caller can compute Count/ΣDegree
    /// without extra round trips.
    /// </summary>
    Task<IReadOnlyList<OnlineExam>> GetExamsAssignedToStudentAsync(
        long teacherId, long? studentSessionId, long? studentSessionGroupId);
    /// <summary>T5s ownership guard — teacherStudentId must belong to this teacher.</summary>
    Task<bool> IsTeacherStudentOwnedByTeacherAsync(long teacherStudentId, long teacherId);

    /// <summary>
    /// Name + code for one of the teacher's own students, or null when the student
    /// does not belong to this tenant — so a caller that needs the name gets the
    /// ownership check for free rather than paying for two queries.
    /// </summary>
    Task<(string StudentName, string? StudentCode)?> GetOwnedStudentIdentityAsync(
        long teacherStudentId, long teacherId);

    /// <summary>
    /// S1 subject label — the teacher's <c>CustomSubject</c> plus their first linked ministry
    /// subject's EN/AR names, in one round trip. Fetched once per student exam-list call (a
    /// single teacher; never per exam → no N+1). Null when the teacher row is missing.
    /// </summary>
    Task<TeacherSubjectNameRow?> GetTeacherSubjectNameAsync(long teacherId);

    /// <summary>
    /// File-access policy support (<c>IFileAccessService</c>): true when <paramref name="fileObjectId"/>
    /// is the image of a question on an exam owned by <paramref name="teacherId"/> and the student
    /// <paramref name="teacherStudentId"/> is in that exam's live assigned set. Composes
    /// <see cref="BuildAssignedStudentIdsQuery"/> — never materializes it.
    /// </summary>
    Task<bool> IsQuestionImageAssignedToStudentAsync(long fileObjectId, long teacherId, long teacherStudentId);

    /// <summary>
    /// The non-null <c>ImageFileId</c>s of every question in an exam — the registry files to detach
    /// when questions are replaced or the exam is deleted. Empty if none.
    /// </summary>
    Task<IReadOnlyList<long>> GetQuestionImageFileIdsAsync(long onlineExamId);
}