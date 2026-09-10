using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Extended repository interface for the Exams &amp; Homework Module (Module 6).
/// Centralizes all domain-specific query methods for assignment-related entities:
/// AssignmentTemplate, AssignmentScope, AssignmentOccurrence, StudentAssignmentObligation,
/// StudentObligationAuditLog, and AssignmentDeletionLog.
///
/// ARCHITECTURAL NOTE (same rationale as IUserRepo, IAttendanceRepo, IPaymentRepo):
/// All expression-based queries are encapsulated here in named methods. The Application
/// layer never builds raw predicates. If a query changes, you edit ONE method here —
/// not every service that uses it.
///
/// Inherits from IGenericRepo&lt;StudentAssignmentObligation, long&gt; for basic CRUD on the
/// primary (hot) entity. Other entities are accessed via named methods below.
///
/// CONCURRENCY NOTE:
/// The barcode-scan path uses <see cref="TryMarkScannedAsync"/>, which executes a
/// conditional ExecuteUpdateAsync rather than a tracked update. This avoids
/// DbUpdateConcurrencyException on duplicate scans (REQ-EXH-NFR-002 sub-1-second target;
/// design decision 5.3). The manual grade-entry path uses standard tracked updates with
/// the entity's RowVersion for optimistic concurrency.
/// </summary>
public interface IExamHomeworkRepo : IGenericRepo<StudentAssignmentObligation, long>
{
    // ══════════════════════════════════════════════
    // ASSIGNMENT TEMPLATE QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds a new assignment template to the change tracker.
    /// Service layer composes the template with its scopes and the first occurrence
    /// inside a single UnitOfWork transaction (REQ-EXH-007).
    /// </summary>
    Task AddTemplateAsync(AssignmentTemplate template);

    /// <summary>
    /// Marks an existing assignment template as modified.
    /// Used for REQ-EXH-034 edit operations (name, notes, date, scope, grading config).
    /// </summary>
    Task UpdateTemplateAsync(AssignmentTemplate template);

    /// <summary>
    /// Hard-deletes an assignment template. NoActions to AssignmentScopes,
    /// AssignmentOccurrences, and StudentAssignmentObligations per REQ-EXH-037.
    /// Service layer must persist the JSON snapshot to AssignmentDeletionLogs
    /// BEFORE invoking this in the same transaction.
    /// </summary>
    Task DeleteTemplateAsync(AssignmentTemplate template);

    /// <summary>
    /// Finds a template by Id, scoped to the teacher.
    /// Returns null if the template does not exist or belongs to another teacher.
    /// REQ-EXH-NFR-004: Multi-tenant isolation.
    /// </summary>
    Task<AssignmentTemplate?> GetTemplateByIdAndTeacherAsync(long templateId, long teacherId);

    /// <summary>
    /// Finds a template with its scopes eagerly loaded.
    /// Used during edit/view flows where the full template + targeting picture is needed.
    /// </summary>
    Task<AssignmentTemplate?> GetTemplateWithScopesAsync(long templateId, long teacherId);

    /// <summary>
    /// Determines whether a template's recurrence pattern is still editable.
    /// REQ-EXH-013: Pattern is locked once any occurrence has student data recorded
    /// (i.e., any obligation has Status != Pending).
    /// </summary>
    Task<bool> CanEditRecurrencePatternAsync(long templateId);

    /// <summary>
    /// Returns the paged Assignment Overview list with all filters applied.
    /// Replaces <c>BuildAssignmentOverviewQuery</c> — pagination is now performed
    /// inside the repo (consistent with <see cref="IExamHomeworkRepo.GetTrackingViewPagedAsync"/>
    /// and the rest of the paginated methods in this interface).
    ///
    /// REQ-EXH-033: Lists every assignment created by the tutor with filters.
    /// REQ-EXH-NFR-001: Backed by IX_AssignmentTemplates_TeacherList covering index;
    /// renders in &lt; 2 seconds at 50K rows.
    /// </summary>
    /// <param name="teacherId">The owning teacher (multi-tenant scope).</param>
    /// <param name="search">Optional partial match on Name or NameAr (case-insensitive via EF.Functions.Like).</param>
    /// <param name="assignmentType">Optional filter by Exam or Homework.</param>
    /// <param name="recurrencePattern">Optional filter by exact recurrence pattern.</param>
    /// <param name="isRecurring">Optional boolean filter on IsRecurring.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Page size; clamped by the caller.</param>
    Task<(IReadOnlyList<AssignmentTemplate> Items, int TotalCount)> GetAssignmentOverviewPagedAsync(
        long teacherId,
        string? search,
        AssignmentType? assignmentType,
        RecurrencePattern? recurrencePattern,
        bool? isRecurring,
        int page,
        int pageSize);

    // ══════════════════════════════════════════════
    // ASSIGNMENT SCOPE QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds multiple scope rows in bulk. Used at template creation time and when
    /// the tutor expands an existing assignment's target scope (REQ-EXH-035).
    /// </summary>
    Task AddScopesRangeAsync(IEnumerable<AssignmentScope> scopes);

    /// <summary>
    /// Retrieves all scope rows for a template, eagerly loading the referenced
    /// student / session / session-group target.
    /// Used by the occurrence generator to resolve and deduplicate students.
    /// REQ-EXH-003: De-duplication happens in service code; this method just loads scopes.
    /// </summary>
    Task<IReadOnlyList<AssignmentScope>> GetScopesByTemplateAsync(long templateId);

    /// <summary>
    /// Batch variant of <see cref="GetScopesByTemplateAsync"/> — returns scopes (with Session and
    /// SessionGroup eager-loaded) for a set of templates in one query. Used by the exam home screen to
    /// report each exam's selection mode + assigned sessions/groups without N+1.
    /// </summary>
    Task<IReadOnlyList<AssignmentScope>> GetScopesByTemplateIdsAsync(IEnumerable<long> templateIds);

    /// <summary>
    /// Removes scope rows. Used during template edit when the tutor changes targeting
    /// (e.g., removes a session from the scope set per REQ-EXH-034).
    /// </summary>
    Task DeleteScopesRangeAsync(IEnumerable<AssignmentScope> scopes);

    // ══════════════════════════════════════════════
    // ASSIGNMENT OCCURRENCE QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds a single occurrence. Used for one-time templates and as the first occurrence
    /// of recurring templates at creation time (REQ-EXH-007).
    /// </summary>
    Task AddOccurrenceAsync(AssignmentOccurrence occurrence);

    /// <summary>
    /// Adds multiple occurrences in bulk. Used by the recurrence scheduler when
    /// materializing a batch of future occurrences (REQ-EXH-011).
    /// </summary>
    Task AddOccurrencesRangeAsync(IEnumerable<AssignmentOccurrence> occurrences);

    /// <summary>
    /// Purges an exam template's materialized graph — obligations, occurrences, and scopes —
    /// leaf-first with set-based deletes, KEEPING the template. Used when a result-free exam is
    /// structurally re-edited (delivery type / sessions / date) and its graph must be rebuilt.
    /// </summary>
    Task PurgeExamGraphAsync(long templateId);

    /// <summary>
    /// Purges a STUDENT's exam/homework obligation graph leaf-first (obligation audit logs →
    /// obligations) — called by the student permanent-purge flow.
    /// <c>StudentAssignmentObligations.TeacherStudentId</c> is NON-nullable with a
    /// <c>Restrict</c> FK (and <c>StudentObligationAuditLogs.StudentObligationId</c> is
    /// <c>Restrict</c> too), so nothing is cleaned automatically and the roster record's hard
    /// delete used to fail with an FK violation (500) for any student who had ever been given
    /// an exam or homework.
    ///
    /// <c>AssignmentScopes.TeacherStudentId</c> is deliberately NOT touched — that FK is
    /// <c>SetNull</c>, so the DB nulls it on delete and the template/occurrence chain survives
    /// (the occurrence generator already skips null targets). Idempotent.
    /// </summary>
    Task PurgeStudentAssignmentDataAsync(long teacherStudentId);

    /// <summary>
    /// Marks an occurrence as modified. Used by the scheduler to flip Status from
    /// Pending to Active or Completed.
    /// </summary>
    Task UpdateOccurrenceAsync(AssignmentOccurrence occurrence);

    /// <summary>
    /// Finds an occurrence by Id, scoped to the teacher.
    /// </summary>
    Task<AssignmentOccurrence?> GetOccurrenceByIdAndTeacherAsync(long occurrenceId, long teacherId);

    /// <summary>
    /// Finds an occurrence with its parent template eagerly loaded.
    /// Used for grade-entry flows where the snapshot configuration must be read.
    /// </summary>
    Task<AssignmentOccurrence?> GetOccurrenceWithTemplateAsync(long occurrenceId, long teacherId);

    /// <summary>
    /// Returns occurrences within a date range for reporting.
    /// REQ-EXH-046: Session or Group Assignment Summary report uses this.
    /// REQ-EXH-NFR-001: Backed by IX_AssignmentOccurrences_DueDate covering index.
    /// </summary>
    Task<IReadOnlyList<AssignmentOccurrence>> GetOccurrencesByDateRangeAsync(
        long teacherId, DateTime startDate, DateTime endDate);

    /// <summary>
    /// Returns the highest <c>OccurrenceNumber</c> currently used for a template,
    /// or 0 if none exist. The scheduler increments from this value when materializing
    /// new occurrences.
    /// </summary>
    Task<int> GetHighestOccurrenceNumberAsync(long templateId);

    /// <summary>
    /// Returns the count of templates that are due for occurrence generation —
    /// recurring templates whose next scheduled occurrence date has arrived and is not stopped.
    /// Used by the recurrence scheduler.
    /// </summary>
    Task<IReadOnlyList<AssignmentTemplate>> GetTemplatesDueForOccurrenceGenerationAsync(DateTime asOfDate);

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — TRACKING VIEW (REQ-EXH-030/031/032)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds multiple obligation rows in bulk. Called once per occurrence-generation
    /// event with the deduplicated student list (REQ-EXH-007).
    /// </summary>
    Task AddObligationsRangeAsync(IEnumerable<StudentAssignmentObligation> obligations);

    /// <summary>
    /// Returns the set of TeacherStudentIds that ALREADY have an obligation row
    /// for a given occurrence. Used by the service layer to filter out duplicates
    /// before insert (REQ-EXH-003 write-time dedup).
    /// </summary>
    Task<IReadOnlyList<long>> GetExistingObligationStudentIdsAsync(
        long occurrenceId, IEnumerable<long> candidateStudentIds);

    /// <summary>
    /// Builds the Assignment Tracking View query — paged, searched, filtered.
    /// REQ-EXH-030: Full student list with status and grade.
    /// REQ-EXH-031: Search by name or code; filter by status, missing entries, grade thresholds.
    /// REQ-EXH-032: Backed by IX_StudentAssignmentObligations_Tracking covering index;
    /// supports virtual scrolling at 50,000 students.
    /// REQ-EXH-NFR-001: Renders in &lt; 2 seconds.
    ///
    /// Service layer wraps the result in PaginatedResponse&lt;TrackingRow&gt;.
    /// </summary>
    Task<(IReadOnlyList<TrackingViewRow> Items, int TotalCount)> GetTrackingViewPagedAsync(
        long teacherId, long occurrenceId,
        string? search,
        ObligationStatus? statusFilter,
        bool? missingEntries,
        decimal? gradeAboveThreshold,
        decimal? gradeBelowThreshold,
        bool? belowPassingGrade,
        int page, int pageSize);

    /// <summary>
    /// Builds the Grade Entry View query — only obligations awaiting grade entry.
    /// REQ-EXH-026-A: Filters to Status IN (Attended, DoneWithoutGrade), backed by
    /// the filtered index IX_StudentAssignmentObligations_PendingGrades.
    /// </summary>
    Task<(IReadOnlyList<TrackingViewRow> Items, int TotalCount)> GetGradeEntryViewPagedAsync(
        long teacherId, long occurrenceId,
        string? search,
        int page, int pageSize);

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — STUDENT HISTORY (REQ-EXH-040)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Returns the full assignment history for a single student across all occurrences.
    /// REQ-EXH-040: Single Student Assignment History Report.
    /// Backed by IX_StudentAssignmentObligations_StudentHistory.
    /// </summary>
    Task<(IReadOnlyList<StudentAssignmentObligation> Items, int TotalCount)> GetStudentHistoryPagedAsync(
        long teacherId, long teacherStudentId,
        DateTime? startDate, DateTime? endDate,
        AssignmentType? assignmentType,
        int page, int pageSize);

    /// <summary>
    /// Computes the cumulative homework completion rate for a student.
    /// REQ-EXH-040: % of completed homework out of total assigned.
    /// Returns (completedCount, totalAssignedCount). Service layer derives the percentage.
    /// </summary>
    Task<(int Completed, int TotalAssigned)> GetHomeworkCompletionStatsAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// Computes the cumulative exam attendance rate for a student.
    /// REQ-EXH-040: % of attended exams out of total assigned.
    /// </summary>
    Task<(int Attended, int TotalAssigned)> GetExamAttendanceStatsAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// Homework status breakdown for one student under one teacher (Parent Module
    /// requirements §9.3): total assigned, plus a three-way split by <see cref="ObligationStatus"/> —
    /// Pending (no status recorded yet), Submitted (Done / DoneWithGrade / DoneWithoutGrade —
    /// any state where the student turned it in), and NotSubmitted (NotDone). Same join path and
    /// single-GroupBy-query shape as <see cref="GetHomeworkCompletionStatsAsync"/>, kept as its own
    /// method rather than extending that one because the two return different, independently
    /// consumed shapes (existing callers depend on the (Completed, TotalAssigned) tuple as-is).
    /// </summary>
    Task<(int Total, int Pending, int Submitted, int NotSubmitted)> GetHomeworkStatusBreakdownAsync(
        long teacherId, long teacherStudentId);

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — REPORTS (REQ-EXH-039 through 046)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Returns all obligations for a single occurrence with student details.
    /// REQ-EXH-039: Single Assignment Report.
    /// </summary>
    Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsByOccurrenceAsync(
        long teacherId, long occurrenceId);

    /// <summary>
    /// Returns students with one or more NotDone homework entries, optionally filtered.
    /// REQ-EXH-041: Homework Absence Report.
    /// Backed by IX_StudentAssignmentObligations_Absence filtered index
    /// (Status IN (NotDone, DidNotAttend)).
    /// </summary>
    Task<(IReadOnlyList<AbsenceReportRow> Items, int TotalCount)> GetHomeworkAbsenceReportPagedAsync(
        long teacherId,
        long? sessionId, long? sessionGroupId, long? specificTemplateId,
        int page, int pageSize);

    /// <summary>
    /// Returns students with one or more DidNotAttend exam entries.
    /// REQ-EXH-042: Exam Absence Report. Same index as the homework variant.
    /// </summary>
    Task<(IReadOnlyList<AbsenceReportRow> Items, int TotalCount)> GetExamAbsenceReportPagedAsync(
        long teacherId,
        long? sessionId, long? sessionGroupId, long? specificTemplateId,
        int page, int pageSize);

    /// <summary>
    /// Returns grade-distribution statistics for a single exam occurrence.
    /// REQ-EXH-043: Grades Analysis Report — highest, lowest, average, above/below threshold.
    /// </summary>
    Task<ExamGradeAnalysis> GetExamGradeAnalysisAsync(long teacherId, long occurrenceId);

    /// <summary>
    /// Returns students who scored below the passing threshold for an occurrence,
    /// or across all exams in a session/group when occurrenceId is null.
    /// REQ-EXH-044: Below Passing Grade Report.
    /// </summary>
    Task<IReadOnlyList<StudentAssignmentObligation>> GetBelowPassingGradeAsync(
        long teacherId,
        long? occurrenceId, long? sessionId, long? sessionGroupId);

    /// <summary>
    /// Returns students who scored above a tutor-defined grade value for an occurrence.
    /// REQ-EXH-045: Above Grade Threshold Report.
    /// </summary>
    Task<IReadOnlyList<StudentAssignmentObligation>> GetAboveGradeThresholdAsync(
        long teacherId, long occurrenceId, decimal threshold);

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — IDEMPOTENT BARCODE SCAN (REQ-EXH-026, NFR-002)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Atomic conditional UPDATE for the barcode-scan hot path.
    /// REQ-EXH-026: Auto-marks Attended (exam) or Done (homework) on scan.
    /// REQ-EXH-NFR-002: Must complete in &lt; 1 second.
    ///
    /// Implements design decision 5.3: returns true when the row was updated
    /// (this scan won the race) and true when 0 rows were affected (another
    /// scan already marked it — idempotent success). The caller never sees a
    /// concurrency exception.
    ///
    /// Resolves the obligation by (TeacherId, OccurrenceId, TeacherStudentId)
    /// rather than by Id so the barcode-to-obligation lookup happens in one
    /// indexed UPDATE instead of a SELECT-then-UPDATE round trip.
    /// </summary>
    /// <param name="teacherId">Owning teacher (multi-tenant guard).</param>
    /// <param name="occurrenceId">The occurrence being marked.</param>
    /// <param name="teacherStudentId">The student resolved from the scanned barcode.</param>
    /// <param name="newStatus">Either Attended (exam) or Done (homework).</param>
    /// <param name="scannedAt">UTC timestamp of the scan.</param>
    /// <param name="scannedByUserId">The user who performed the scan.</param>
    /// <returns>The number of rows affected — 0 means another scan won the race.</returns>
    Task<int> TryMarkScannedAsync(
        long teacherId, long occurrenceId, long teacherStudentId,
        ObligationStatus newStatus, DateTime scannedAt, long scannedByUserId);

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — SINGLE-ROW LOOKUPS
    // ══════════════════════════════════════════════

    /// <summary>
    /// Finds a single obligation by Id, scoped to the teacher.
    /// Used by the manual grade-entry path so RowVersion can be applied via the tracker.
    /// </summary>
    Task<StudentAssignmentObligation?> GetObligationByIdAndTeacherAsync(
        long obligationId, long teacherId);

    /// <summary>
    /// Finds a single obligation by (occurrenceId, studentId) for service-level upsert flows.
    /// Used when adding students to an existing assignment (REQ-EXH-035) to detect
    /// pre-existing obligations.
    /// </summary>
    Task<StudentAssignmentObligation?> GetObligationByOccurrenceAndStudentAsync(
        long occurrenceId, long teacherStudentId);

    /// <summary>
    /// Removes an obligation. Used when the tutor removes a student from a scope
    /// (REQ-EXH-036), with confirmation if a status was already recorded.
    /// </summary>
    Task DeleteObligationAsync(StudentAssignmentObligation obligation);

    // ══════════════════════════════════════════════
    // AUDIT LOG QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds an audit-log entry. Service layer is responsible for capturing both
    /// the status delta and the grade delta in the same transaction as the
    /// obligation update.
    /// </summary>
    Task AddAuditLogAsync(StudentObligationAuditLog auditLog);

    /// <summary>
    /// Returns the full audit history for a single obligation, ordered chronologically.
    /// </summary>
    Task<IReadOnlyList<StudentObligationAuditLog>> GetAuditHistoryByObligationAsync(
        long obligationId);

    // ══════════════════════════════════════════════
    // DELETION LOG QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Persists the JSON snapshot before the cascading hard delete.
    /// REQ-EXH-037: Hard delete is final; this row is the only historical record.
    /// </summary>
    Task AddDeletionLogAsync(AssignmentDeletionLog log);

    /// <summary>
    /// Returns paginated deletion-log entries for a teacher, used by audit dashboards.
    /// </summary>
    Task<(IReadOnlyList<AssignmentDeletionLog> Items, int TotalCount)> GetDeletionLogsPagedAsync(
        long teacherId, DateTime? startDate, DateTime? endDate, int page, int pageSize);

    // ══════════════════════════════════════════════════════════════════════
    // OVERVIEW & OCCURRENCE AGGREGATES (REQ-EXH-029, 033)
    // O(2)-pattern support — replaces N+1 per-row queries.
    // ══════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Returns the latest <c>OccurrenceId</c> per template in the input list.
    /// Used by the Assignment Overview aggregate path so the per-row "completion
    /// summary" reflects the most recent iteration of a recurring template.
    /// One database round-trip; EF translates the <c>GroupBy</c>/<c>OrderByDescending</c>
    /// pair into a single CTE with ROW_NUMBER().
    /// </summary>
    Task<Dictionary<long, long>> GetLatestOccurrenceIdsByTemplateAsync(
        IEnumerable<long> templateIds);

    /// <summary>
    /// Returns per-occurrence completion summaries in a single grouped aggregate.
    /// Buckets every obligation status into:
    ///   - DoneOrAttended: Done | Attended | AttendedWithGrade | DoneWithoutGrade | DoneWithGrade
    ///   - NotDoneOrAbsent: NotDone | DidNotAttend
    ///   - Pending: Pending
    /// REQ-EXH-029 / REQ-EXH-033.
    /// </summary>
    Task<Dictionary<long, OccurrenceCompletionSummary>> GetCompletionSummariesByOccurrenceIdsAsync(
        IEnumerable<long> occurrenceIds);

    /// <summary>
    /// Returns (templateId → next future occurrence date OR last past occurrence date)
    /// for the Assignment Overview list.
    /// </summary>
    Task<Dictionary<long, DateTime?>> GetNextOrLastOccurrenceDatesAsync(
        IEnumerable<long> templateIds, DateTime today);
    // ══════════════════════════════════════════════════════════════════════
    // OCCURRENCE PAGINATION (REQ-EXH-011)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Paginated occurrences for a single template, ordered by OccurrenceNumber ASC.
    /// </summary>
    Task<(IReadOnlyList<AssignmentOccurrence> Items, int TotalCount)> GetOccurrencesByTemplatePagedAsync(
        long teacherId, long templateId, int page, int pageSize);

    /// <summary>
    /// Returns the first occurrence (OccurrenceNumber = 1) for a template, tracked.
    /// Used by UpdateTemplateAsync when the tutor edits the date on a non-recurring template.
    /// </summary>
    Task<AssignmentOccurrence?> GetFirstOccurrenceAsync(long templateId, long teacherId);


    /// <summary>
    /// Returns the highest-numbered (latest) occurrence for a template, no-tracking.
    /// Used by StopRecurrenceAsync to anchor the deletion-log entry (REQ-EXH-012).
    /// </summary>
    Task<AssignmentOccurrence?> GetLatestOccurrenceAsync(long templateId, long teacherId);

    // ══════════════════════════════════════════════════════════════════════
    // DELETION-TIME AGGREGATES (REQ-EXH-037)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Counts distinct students with any recorded data (non-Pending status OR a grade
    /// entered) across all occurrences of a template.
    /// </summary>
    Task<int> CountStudentsWithRecordedDataAsync(long templateId);

    /// <summary>Counts the occurrences of a template.</summary>
    Task<int> CountOccurrencesByTemplateAsync(long templateId);

    /// <summary>
    /// Counts the EXAM-type assignment templates owned by a teacher
    /// (free-tier quota key ModuleQuotaKeys.Exams).
    /// </summary>
    Task<int> CountExamTemplatesByTeacherAsync(long teacherId);

    /// <summary>
    /// Counts the HOMEWORK-type assignment templates owned by a teacher
    /// (free-tier quota key ModuleQuotaKeys.AssignmentTemplates).
    /// </summary>
    Task<int> CountHomeworkTemplatesByTeacherAsync(long teacherId);

    /// <summary>
    /// Returns the full audit-log history for all obligations under a template.
    /// Used by DeleteTemplateAsync to copy history into the deletion-log JSON snapshot
    /// BEFORE the cascading hard delete fires.
    /// </summary>
    Task<IReadOnlyList<StudentObligationAuditLog>> GetAuditLogsForTemplateAsync(long templateId);

    /// <summary>
    /// Bulk-deletes audit-log rows for all obligations under a template using a single
    /// SQL statement (ExecuteDeleteAsync). Called inside the delete transaction AFTER
    /// the rows have been serialized into the deletion log's JSON snapshot.
    /// </summary>
    Task DeleteAuditLogsForTemplateAsync(long templateId);
    // ══════════════════════════════════════════════════════════════════════
    // CONCURRENCY HELPER
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets the original RowVersion bytes on a tracked AssignmentTemplate entity so
    /// EF Core generates the correct WHERE clause for optimistic concurrency.
    ///
    /// Why this lives here: the service layer must not touch the EF change tracker
    /// directly. Wrapping the operation in a named repo method keeps the service
    /// LINQ-free and isolates EF concerns to Infrastructure.
    /// </summary>
    void SetTemplateOriginalRowVersion(AssignmentTemplate template, byte[] rowVersion);
    // ══════════════════════════════════════════════════════════════════════
    // ASSIGNMENT OVERVIEW LIST (REQ-EXH-033) — replaces BuildAssignmentOverviewQuery
    // ══════════════════════════════════════════════════════════════════════

   
    /// <summary>
    /// Returns scope counts (Individual / Session / Group) for a set of templates
    /// in a single grouped query. Used by GetOverviewAsync to render the
    /// "3 students · 2 sessions · 1 group" summary string.
    /// </summary>
    Task<Dictionary<long, ScopeCountAggregate>> GetScopeCountsByTemplateIdsAsync(
        IEnumerable<long> templateIds);


    // ══════════════════════════════════════════════════════════════════════
    // OBLIGATION WRITE PATH (status entry / grade entry)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Marks an existing obligation as Modified in the change tracker.
    /// Used by every status-entry and grade-entry path; the service layer mutates
    /// the entity then calls this so the EF Core change tracker picks up the changes.
    /// </summary>
    Task UpdateObligationAsync(StudentAssignmentObligation obligation);

    /// <summary>
    /// Sets the original RowVersion bytes on a tracked obligation so EF generates
    /// the WHERE [RowVersion] = @original clause for optimistic concurrency.
    /// REQ-EXH-027: Manual grade-entry surfaces a 409 if two users edit the same row.
    /// Mirrors <c>SetTemplateOriginalRowVersion</c> pattern.
    /// Used by the homework/assignment surface (<c>ExamHomeworkService</c>); the exams batch-grade
    /// path compares tokens itself so one stale row cannot roll back the whole batch.
    /// </summary>
    void SetObligationOriginalRowVersion(StudentAssignmentObligation obligation, byte[] rowVersion);

    /// <summary>
    /// Returns a tracked set of obligations by their ids, scoped to (teacher, occurrence).
    /// Used by <c>BulkUpdateStatusAsync</c> to load obligations for in-place mutation
    /// inside a single transaction.
    /// </summary>
    Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsByIdsAsync(
        long teacherId, long occurrenceId, IEnumerable<long> obligationIds);

    /// <summary>
    /// Tracked obligations for the given students within one occurrence, for the exam-attendance path
    /// keyed by student id. Resolved via the unique (OccurrenceId, TeacherStudentId) index.
    /// </summary>
    Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsByOccurrenceAndStudentsAsync(
        long teacherId, long occurrenceId, IEnumerable<long> teacherStudentIds);

    // ══════════════════════════════════════════════════════════════════════
    // PICKERS — typeahead and eligible-students
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Type-ahead picker for the manual-status entry flow (REQ-EXH-023).
    /// Returns a slim projection of obligations matching a name or code prefix.
    /// Capped at <paramref name="limit"/> rows; service layer clamps the limit.
    /// </summary>
    Task<IReadOnlyList<StudentPickerRow>> SearchStudentsInOccurrenceAsync(
        long teacherId, long occurrenceId, string query, int limit);

    /// <summary>
    /// Paginated list of students who belong to the teacher but are NOT yet in the
    /// resolved scope of a template (REQ-EXH-035). The "resolved scope" is computed
    /// as the union of:
    ///   - IndividualStudent scope rows
    ///   - All students in any Session referenced by a Session scope row
    ///   - All students in any Session in any SessionGroup referenced by a SessionGroup scope row
    ///
    /// Implementation note: the repo computes the included-set inline and excludes it
    /// from the candidate set. Optionally filters to a specific session via
    /// <paramref name="sessionId"/>.
    /// </summary>
    Task<(IReadOnlyList<EligibleStudentRow> Items, int TotalCount)> GetEligibleStudentsForTemplatePagedAsync(
        long teacherId, long templateId, long? sessionId,
        string? search, int page, int pageSize);

    // ══════════════════════════════════════════════════════════════════════
    // OBLIGATION DELETE PATH (REQ-EXH-036)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the set of obligation ids for one student across all FUTURE or CURRENT
    /// occurrences of a template (DueDate &gt;= today).
    /// Used by <c>RemoveStudentFromTemplateAsync</c> to delete only forward-looking rows;
    /// past occurrences are preserved (BR-EXH-002).
    /// </summary>
    Task<IReadOnlyList<StudentAssignmentObligation>> GetFutureObligationsForStudentAsync(
        long teacherId, long templateId, long teacherStudentId, DateTime asOfDate);

    /// <summary>
    /// Counts obligations for a student under a template that have recorded data
    /// (Status != Pending or IsGradeEntered = true).
    /// Drives the "force flag required" decision in <c>RemoveStudentFromTemplateAsync</c>
    /// (REQ-EXH-036).
    /// </summary>
    Task<int> CountStudentObligationsWithDataAsync(
        long teacherId, long templateId, long teacherStudentId);

    /// <summary>
    /// Removes a single AssignmentScope row, scoped to the teacher.
    /// </summary>
    Task<AssignmentScope?> GetScopeByIdAndTeacherAsync(long scopeId, long teacherId);

    /// <summary>
    /// Removes a single scope row from the change tracker. Used by
    /// <c>RemoveScopeAsync</c> and the "remove individual student" flow.
    /// </summary>
    Task DeleteScopeAsync(AssignmentScope scope);
    /// <summary>
    /// Returns the ids of recurring, non-stopped templates whose next expected
    /// occurrence date falls within [<paramref name="fromDate"/>, <paramref name="toDate"/>].
    ///
    /// "Next expected date" is approximated by the latest occurrence's DueDate plus
    /// the recurrence pattern's standard interval (7 days for EverySession, 14 for
    /// EveryTwoSessions, 1 month for Monthly). The materializer service computes the
    /// exact date and re-validates, so a slightly over-eager filter here is harmless.
    ///
    /// Backed by IX_AssignmentTemplates_TeacherList covering the IsRecurring filter
    /// plus a join to AssignmentOccurrences keyed by IX_AssignmentOccurrences_Template.
    /// </summary>
    Task<IReadOnlyList<long>> GetTemplateIdsDueForMaterializationAsync(
        DateTime fromDate, DateTime toDate);

    /// <summary>
    /// Loads a template inside an UPDLOCK row lock so concurrent materializer runs
    /// on the same template serialize. The lock is released when the surrounding
    /// transaction COMMITs or ROLLBACKs.
    ///
    /// Catalog §7.2 chose this pattern over Hangfire Pro's <c>[Mutex]</c> attribute
    /// (free, predictable, matches SQL Server semantics).
    /// </summary>
    Task<AssignmentTemplate?> LockTemplateForMaterializationAsync(long templateId);

    // ══════════════════════════════════════════════════════════════════════
    // EXAMS MODULE — home & opened-exam views (clean /api/exams surface)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// All exam occurrences for a teacher (one per session-date) with exam name, delivery type,
    /// session name and due date. Powers the Exam Home upcoming/past split.
    /// </summary>
    Task<IReadOnlyList<ExamHomeOccurrenceRow>> GetExamOccurrencesForHomeAsync(long teacherId);

    /// <summary>
    /// Paged exam-home occurrences for one bucket: upcoming (DueDate &gt;= today, ascending) or past
    /// (DueDate &lt; today, descending). Returns the page rows + total count for that bucket.
    /// </summary>
    Task<(IReadOnlyList<ExamHomeOccurrenceRow> Items, int TotalCount)> GetExamOccurrencesForHomePagedAsync(
        long teacherId, bool isPast, DateTime today, int page, int pageSize);

    /// <summary>
    /// The occurrences (session + date + snapshotted grading config) of a single exam, ordered by
    /// date. Powers the per-session grouping of the opened-exam view.
    /// </summary>
    Task<IReadOnlyList<ExamOccurrenceInfoRow>> GetExamOccurrencesByTemplateAsync(long teacherId, long templateId);

    /// <summary>
    /// The full student roster across all of an exam's occurrences in one query, ordered by student
    /// name. Grouped by occurrence in the service to build sessions + statistics.
    /// </summary>
    Task<IReadOnlyList<ExamRosterRow>> GetExamRosterByTemplateAsync(long teacherId, long templateId);

    /// <summary>
    /// Loads tracked obligations by id (teacher-scoped) with their occurrence + template eagerly
    /// included, for the batch grade-entry path. Tracked so per-row RowVersion concurrency applies.
    /// </summary>
    Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsForGradingByIdsAsync(
        long teacherId, IEnumerable<long> obligationIds);

    /// <summary>
    /// Resolves each student's obligation within a single exam (template), tracked, with Occurrence +
    /// Template included, for the batch grade path keyed by student id. A student with one primary
    /// session has exactly one obligation per exam; more than one signals a data anomaly (caller rejects).
    /// </summary>
    Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsForGradingByTemplateAndStudentsAsync(
        long teacherId, long templateId, IEnumerable<long> teacherStudentIds);

    /// <summary>Returns the ids of exam occurrences linked to a given session occurrence (during-session exams).</summary>
    Task<IReadOnlyList<long>> GetExamOccurrenceIdsBySessionOccurrenceAsync(long teacherId, long sessionOccurrenceId);

    /// <summary>
    /// From a candidate set of <c>SessionOccurrence</c> ids, returns the subset referenced by any exam
    /// <c>AssignmentOccurrence</c> (its DuringSession anchor). Consumed by the Attendance module before it
    /// rebuilds a session's occurrences on a recurrence edit (SES-1) so exam-anchored occurrences are
    /// preserved rather than SET NULL. Returns an empty list for an empty input.
    /// </summary>
    Task<IReadOnlyList<long>> GetReferencedSessionOccurrenceIdsAsync(IEnumerable<long> sessionOccurrenceIds);

    /// <summary>
    /// Exam occurrences due on a given date (during-session and separate-time), for the home dashboard's
    /// "today's exams" section and the "this session is an exam" flag.
    /// </summary>
    Task<IReadOnlyList<ExamHomeOccurrenceRow>> GetExamOccurrencesForDateAsync(long teacherId, DateTime date);

    /// <summary>
    /// Atomically sets attendance-derived status on an exam occurrence's obligations for the given
    /// students (attendance→exam sync and back-fill). Optionally clears the grade and/or skips
    /// already-graded rows (to preserve grades on "present"). Returns rows affected.
    /// </summary>
    Task<int> SetExamAttendanceByOccurrenceAsync(
        long teacherId, long occurrenceId, IEnumerable<long> teacherStudentIds,
        ObligationStatus newStatus, bool clearGrade, bool skipGraded, DateTime utcNow, long actingUserId);

    /// <summary>
    /// Reverts an exam occurrence's attendance-derived obligations (Attended / AttendedWithGrade /
    /// DidNotAttend) back to <c>Pending</c> (grade cleared) for every student NOT in
    /// <paramref name="markedStudentIds"/> — i.e. students whose backing session attendance record was
    /// deleted or re-marked away. <c>Pending</c> rows and homework statuses are never touched. Used by
    /// the attendance→exam reconcile so a during-session exam can't keep a stale mark. Returns rows affected.
    /// </summary>
    Task<int> RevertExamAttendanceForUnmarkedAsync(
        long teacherId, long occurrenceId, IReadOnlyCollection<long> markedStudentIds,
        DateTime utcNow, long actingUserId);

    /// <summary>
    /// Sets the per-occurrence grade snapshots (<c>MaxGradeSnapshot</c> / <c>PassingThresholdSnapshot</c>)
    /// on every occurrence of an exam template to the given bounds. Called when an exam's max/pass score
    /// is edited on the metadata path so grade validation and pass/fail stay in step with the template.
    /// </summary>
    Task UpdateExamGradeSnapshotsAsync(
        long teacherId, long templateId, decimal maxGrade, decimal passingThreshold);

    /// <summary>
    /// Inserts a during-session exam obligation (at <paramref name="status"/>) for any student in
    /// <paramref name="studentIds"/> who doesn't already have one on the occurrence. Lets a student
    /// assigned to the class AFTER the exam was created auto-join it once their attendance is taken
    /// (the "mirror the class" dynamic roster). No-op for students who already have a row; tenant-guarded
    /// on the occurrence.
    /// </summary>
    Task EnsureExamObligationsExistAsync(
        long teacherId, long occurrenceId, IReadOnlyCollection<long> studentIds,
        ObligationStatus status, DateTime utcNow, long actingUserId);
    /// <summary>
    /// Student-facing, paged: offline-exam occurrences (AssignmentType.Exam) the given student
    /// has an obligation for under this teacher, joined to its template for display fields.
    /// One row per occurrence, ordered by DueDate descending. Rides the same
    /// IX_StudentAssignmentObligations_StudentHistory index as GetStudentHistoryPagedAsync
    /// (same leading columns: TeacherId, TeacherStudentId).
    /// </summary>
    Task<(IReadOnlyList<StudentOfflineExamRow> Items, int TotalCount)> GetOfflineExamsForStudentPagedAsync(
        long teacherId, long teacherStudentId, int page, int pageSize);

    /// <summary>
    /// Batched leaderboard ranks for one student across a set of offline-exam occurrences — ONE query,
    /// no N+1 (feeds a paged list). For each occurrence in which the student has a GRADED obligation
    /// (<see cref="ObligationStatus.AttendedWithGrade"/> with a grade), returns the student's rank by
    /// grade DESCENDING within that occurrence's cohort. The cohort is every graded student sharing the
    /// same <see cref="AssignmentOccurrence"/> — i.e. the same session's exam-takers, since every exam
    /// occurrence is anchored to exactly one session (same grouping the roster/grade-analysis paths use).
    /// Competition ranking: rank = 1 + peers scoring STRICTLY higher, so ties share a rank (same grade =
    /// same rank). <c>GroupSize</c> = graded students in the cohort, INCLUDING this student. Soft-deleted
    /// students are excluded from both counts (matches <see cref="GetCompletionSummariesByOccurrenceIdsAsync"/>).
    /// Occurrences where the student is NOT graded are OMITTED from the result — the service surfaces a
    /// null rank/group-size for those. Rides IX_StudentAssignmentObligations_Tracking
    /// (TeacherId, OccurrenceId, Status) INCLUDE (TeacherStudentId, GradeValue).
    /// </summary>
    Task<IReadOnlyList<StudentExamRankRow>> GetStudentExamRanksAsync(
        long teacherId, long teacherStudentId, IEnumerable<long> occurrenceIds);

    /// <summary>
    /// The publishing teacher's subject pieces for student-facing display: the teacher's free-text
    /// <see cref="Teacher.CustomSubject"/> plus the first linked ministry <see cref="Subject"/>'s
    /// bilingual names. The service selects EN/AR by the student's language and applies the
    /// CustomSubject fallback — the canonical pattern in
    /// <c>StudentUserService.BuildDashboardTeacherDtoFromBatch</c>. Returns null if the teacher is absent.
    /// </summary>
    Task<TeacherSubjectDisplayRow?> GetTeacherSubjectDisplayAsync(long teacherId);
}

// ══════════════════════════════════════════════
// PROJECTION TYPES
// ══════════════════════════════════════════════

/// <summary>
/// Projection model for a single row in the Assignment Tracking View and Grade Entry View.
/// Lives in the Domain layer alongside the repo interface (same pattern as
/// <c>PagedAttendanceStudentRow</c>) since it is a query projection, not a DTO.
///
/// Materializing this projection avoids loading the full <c>StudentAssignmentObligation</c>
/// graph on the hot path — the tracking view can render with just these fields.
/// </summary>
public class TrackingViewRow
{
    public long ObligationId { get; set; }
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public ObligationStatus Status { get; set; }
    public decimal? GradeValue { get; set; }
    public bool IsGradeEntered { get; set; }

    /// <summary>
    /// True when the recorded grade is below the occurrence's snapshotted PassingThreshold.
    /// Computed at projection time so the UI can flag the row without a second query.
    /// REQ-EXH-030: Tracking view shows whether grade is above or below passing.
    /// </summary>
    public bool IsBelowPassing { get; set; }

    public bool MarkedByScan { get; set; }
    public DateTime? ScannedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] ObligationRowVersion { get; set; } = null!;
}

/// <summary>
/// Projection model for an absence-report row.
/// REQ-EXH-041 / REQ-EXH-042: Each entry shows student name, code, assigned session,
/// the specific assignments missed, and the cumulative miss count.
/// </summary>
public class AbsenceReportRow
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public long? SessionId { get; set; }
    public string? SessionName { get; set; }

    /// <summary>
    /// Names of the specific assignments the student missed within the report's filter scope.
    /// </summary>
    public IReadOnlyList<string> MissedAssignmentNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Total count of missed assignments across the student's full history,
    /// per REQ-EXH-041/042 ("total count of missed homework / exams across their full history").
    /// </summary>
    public int TotalMissedCount { get; set; }
}

/// <summary>
/// Aggregated grade-distribution model for an exam occurrence.
/// REQ-EXH-043: Highest, lowest, average grade; counts/percentages above and below
/// the passing threshold; full per-student listing handled separately.
/// </summary>
public class ExamGradeAnalysis
{
    public long OccurrenceId { get; set; }
    public int AttendedStudentsCount { get; set; }
    public decimal? HighestGrade { get; set; }
    public decimal? LowestGrade { get; set; }
    public decimal? AverageGrade { get; set; }
    public int AbovePassingCount { get; set; }
    public int BelowPassingCount { get; set; }
    public decimal? MaxGradeSnapshot { get; set; }
    public decimal? PassingThresholdSnapshot { get; set; }
}
/// <summary>
/// Per-occurrence completion summary projection.
/// Lives in the Domain layer alongside <c>TrackingViewRow</c> and <c>AbsenceReportRow</c>
/// since it is a query projection rather than a cross-layer DTO.
/// </summary>

// ════════════════════════════════════════════════════════════════════════════
// PROJECTION TYPES — add to the existing projections section of IExamHomeworkRepo.cs
// alongside TrackingViewRow and AbsenceReportRow.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Per-occurrence completion summary projection.
/// Lives in the Domain layer alongside <c>TrackingViewRow</c> since it is a query
/// projection, not a cross-layer DTO.
/// </summary>
public sealed class OccurrenceCompletionSummary
{
    public long OccurrenceId { get; set; }
    public int TotalStudents { get; set; }
    public int DoneOrAttended { get; set; }
    public int NotDoneOrAbsent { get; set; }
    public int Pending { get; set; }
}

/// <summary>
/// Per-template scope-count aggregate projection.
/// Used by the Assignment Overview list to render the human-readable scope summary.
/// </summary>
public sealed class ScopeCountAggregate
{
    public long TemplateId { get; set; }
    public int IndividualCount { get; set; }
    public int SessionCount { get; set; }
    public int GroupCount { get; set; }
}
/// <summary>
/// Slim projection for the manual-entry typeahead picker (REQ-EXH-023).
/// Includes the current obligation status so the UI can display "Currently: NotDone"
/// alongside the suggestion and skip showing students who already have a recorded value.
/// </summary>
public sealed class StudentPickerRow
{
    public long ObligationId { get; set; }
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public Edvanz.Domain.Enums.ObligationStatus CurrentStatus { get; set; }
}

/// <summary>
/// Slim projection for the "eligible students" picker shown when the tutor is adding
/// students to an existing template (REQ-EXH-035).
/// </summary>
public sealed class EligibleStudentRow
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public string? SessionName { get; set; }
}
/// <summary>
/// Student-facing projection for one offline-exam occurrence (Module 6, AssignmentType.Exam).
/// One row per occurrence the student has an obligation for — a recurring exam template
/// surfaces one row per DueDate. Score/percentage are computed in the service layer from
/// GradeValue and MaxGradeSnapshot; this projection only carries raw values.
/// </summary>
public sealed class StudentOfflineExamRow
{
    public long OccurrenceId { get; set; }
    public long TemplateId { get; set; }
    public string ExamName { get; set; } = null!;
    public string? Notes { get; set; }
    public DateTime DueDate { get; set; }
    public decimal? GradeValue { get; set; }
    public decimal? MaxGradeSnapshot { get; set; }
    public ObligationStatus Status { get; set; }
}

/// <summary>
/// Batched rank projection for one student in one offline-exam occurrence (audit F1 leaderboard).
/// <see cref="Rank"/> is competition-style (1 + peers scoring strictly higher; ties share a rank);
/// <see cref="GroupSize"/> is the number of graded students in the occurrence's cohort — the session's
/// exam-takers — INCLUDING this student. Only emitted for occurrences where the student is graded.
/// Lives in the Domain layer alongside the other query projections in this file (not a cross-layer DTO).
/// </summary>
public sealed class StudentExamRankRow
{
    public long OccurrenceId { get; set; }
    public int Rank { get; set; }
    public int GroupSize { get; set; }
}

/// <summary>
/// Raw subject pieces for language-aware display of a teacher's subject (audit F3). The service picks
/// EN/AR by the student's language, preferring the ministry subject name and falling back to
/// <see cref="CustomSubject"/> — the canonical pattern in <c>StudentUserService</c>. A query projection,
/// not a cross-layer DTO.
/// </summary>
public sealed class TeacherSubjectDisplayRow
{
    public string? CustomSubject { get; set; }
    public string? SubjectNameEn { get; set; }
    public string? SubjectNameAr { get; set; }
}