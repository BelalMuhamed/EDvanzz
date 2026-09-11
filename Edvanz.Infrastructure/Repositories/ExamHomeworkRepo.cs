using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// Extended repository for the Exams &amp; Homework Module (Module 6).
/// Centralizes all domain-specific query logic for assignment-related entities.
///
/// ARCHITECTURAL NOTE:
/// Inherits GenericRepo&lt;StudentAssignmentObligation, long&gt; for basic CRUD on the primary
/// (hot) entity. All other entities (AssignmentTemplate, AssignmentScope, AssignmentOccurrence,
/// StudentObligationAuditLog, AssignmentDeletionLog) are accessed via _context directly through
/// named methods — same pattern as PaymentRepo and AttendanceRepo.
///
/// QUERY PATTERNS:
/// - Paged queries use CountAsync + Skip/Take.
/// - Search uses EF.Functions.Like for SQL Server index-friendly case-insensitive matching.
///   Never ToLower().Contains() (forces full scan).
/// - Hot tracking-view paths project directly to TrackingViewRow inside SQL via Select —
///   avoids materializing full StudentAssignmentObligation entities.
/// - Barcode-scan path uses ExecuteUpdateAsync for atomic conditional UPDATE — no
///   DbUpdateConcurrencyException on the &lt; 1-second hot path (design decision 5.3).
/// - All queries include teacherId guard for tenant isolation (REQ-EXH-NFR-004).
/// </summary>
public class ExamHomeworkRepo : GenericRepo<StudentAssignmentObligation, long>, IExamHomeworkRepo
{
    public ExamHomeworkRepo(EdvanzDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<int> CountExamTemplatesByTeacherAsync(long teacherId)
    {
        // AssignmentTemplate is hard-delete (no IsDeleted), so live rows == the real count.
        return await _context.AssignmentTemplates
            .CountAsync(t => t.TeacherId == teacherId && t.AssignmentType == AssignmentType.Exam);
    }

    /// <inheritdoc />
    public async Task<int> CountHomeworkTemplatesByTeacherAsync(long teacherId)
    {
        return await _context.AssignmentTemplates
            .CountAsync(t => t.TeacherId == teacherId && t.AssignmentType == AssignmentType.Homework);
    }

    // ══════════════════════════════════════════════
    // ASSIGNMENT TEMPLATE QUERIES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddTemplateAsync(AssignmentTemplate template)
    {
        await _context.AssignmentTemplates.AddAsync(template);
    }

    /// <inheritdoc />
    public async Task UpdateTemplateAsync(AssignmentTemplate template)
    {
        _context.Entry(template).State = EntityState.Modified;
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteTemplateAsync(AssignmentTemplate template)
    {
        // Hard delete per REQ-EXH-037. Every child FK is NoAction, so the database
        // cascades NOTHING — and client-side cascade can't run either: Remove(template)
        // throws "association ... has been severed" for the required Scopes that
        // GetTemplateWithScopesAsync loads into the tracker (this 500'd every template
        // delete). Delete leaf-first with set-based statements instead; they run inside
        // the caller's ambient transaction. Obligation audit logs (Restrict FK) are
        // archived and deleted by the service BEFORE this call. IgnoreQueryFilters keeps
        // this a true purge even if soft-delete filters are added to these entities later.
        long templateId = template.Id;

        await _context.StudentAssignmentObligations
            .IgnoreQueryFilters()
            .Where(o => o.Occurrence.TemplateId == templateId)
            .ExecuteDeleteAsync();

        await _context.AssignmentOccurrences
            .IgnoreQueryFilters()
            .Where(o => o.TemplateId == templateId)
            .ExecuteDeleteAsync();

        await _context.AssignmentScopes
            .IgnoreQueryFilters()
            .Where(s => s.TemplateId == templateId)
            .ExecuteDeleteAsync();

        await _context.AssignmentTemplates
            .IgnoreQueryFilters()
            .Where(t => t.Id == templateId)
            .ExecuteDeleteAsync();

        // The template (and any included scopes) may still sit in the change tracker as
        // Unchanged; the caller's SaveChangesAsync then persists only the deletion log.
    }

    /// <inheritdoc />
    public async Task PurgeExamGraphAsync(long templateId)
    {
        // Leaf-first set-based deletes (children have NoAction FKs) — same pattern as
        // DeleteTemplateAsync, but the template row itself is preserved for the rebuild.
        await _context.StudentAssignmentObligations
            .IgnoreQueryFilters()
            .Where(o => o.Occurrence.TemplateId == templateId)
            .ExecuteDeleteAsync();

        await _context.AssignmentOccurrences
            .IgnoreQueryFilters()
            .Where(o => o.TemplateId == templateId)
            .ExecuteDeleteAsync();

        await _context.AssignmentScopes
            .IgnoreQueryFilters()
            .Where(s => s.TemplateId == templateId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task PurgeStudentAssignmentDataAsync(long teacherStudentId)
    {
        // Leaf-first: the audit log's FK to the obligation is Restrict, so it blocks the
        // obligation delete, which in turn blocks the roster record's hard delete.
        await _context.StudentObligationAuditLogs
            .IgnoreQueryFilters()
            .Where(l => l.StudentObligation.TeacherStudentId == teacherStudentId)
            .ExecuteDeleteAsync();

        await _context.StudentAssignmentObligations
            .IgnoreQueryFilters()
            .Where(o => o.TeacherStudentId == teacherStudentId)
            .ExecuteDeleteAsync();
    }
    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<(IReadOnlyList<StudentOfflineExamRow> Items, int TotalCount)> GetOfflineExamsForStudentPagedAsync(
        long teacherId, long teacherStudentId, int page, int pageSize)
    {
        var query = _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.TeacherStudentId == teacherStudentId
                     && o.Occurrence.Template.AssignmentType == AssignmentType.Exam);

        int totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(o => o.Occurrence.DueDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new StudentOfflineExamRow
            {
                OccurrenceId = o.OccurrenceId,
                TemplateId = o.Occurrence.TemplateId,
                ExamName = o.Occurrence.Template.Name,
                Notes = o.Occurrence.Template.Notes,
                DueDate = o.Occurrence.DueDate,
                GradeValue = o.GradeValue,
                MaxGradeSnapshot = o.Occurrence.MaxGradeSnapshot,
                Status = o.Status,
            })
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentExamRankRow>> GetStudentExamRanksAsync(
        long teacherId, long teacherStudentId, IEnumerable<long> occurrenceIds)
    {
        var ids = occurrenceIds.Distinct().ToList();
        if (ids.Count == 0) return new List<StudentExamRankRow>();

        // "Graded" == the terminal exam grade state, identical to GetExamGradeAnalysisAsync (REQ-EXH-043):
        // AttendedWithGrade with a grade. The cohort for an occurrence is every graded, non-soft-deleted
        // obligation sharing that OccurrenceId — one occurrence == one session's exam-takers, so this is
        // the same "session assignment" grouping the roster/grade-analysis paths use. Competition ranking:
        // rank = 1 + peers scoring STRICTLY higher (ties share a rank). ONE round trip for the whole page:
        // the outer query is the student's own graded rows across the page's occurrences, each carrying two
        // correlated COUNTs over its cohort (no per-exam round trip). Referencing peer.TeacherStudent makes
        // the global !IsDeleted filter apply; the explicit predicate mirrors GetCompletionSummariesByOccurrenceIdsAsync.
        return await _context.StudentAssignmentObligations
            .Where(self => self.TeacherId == teacherId
                        && self.TeacherStudentId == teacherStudentId
                        && ids.Contains(self.OccurrenceId)
                        && self.Status == ObligationStatus.AttendedWithGrade
                        && self.GradeValue.HasValue)
            .Select(self => new StudentExamRankRow
            {
                OccurrenceId = self.OccurrenceId,
                Rank = 1 + _context.StudentAssignmentObligations.Count(peer =>
                    peer.OccurrenceId == self.OccurrenceId
                    && peer.TeacherId == teacherId
                    && peer.Status == ObligationStatus.AttendedWithGrade
                    && peer.GradeValue.HasValue
                    && peer.GradeValue.Value > self.GradeValue!.Value
                    && !peer.TeacherStudent.IsDeleted),
                GroupSize = _context.StudentAssignmentObligations.Count(peer =>
                    peer.OccurrenceId == self.OccurrenceId
                    && peer.TeacherId == teacherId
                    && peer.Status == ObligationStatus.AttendedWithGrade
                    && peer.GradeValue.HasValue
                    && !peer.TeacherStudent.IsDeleted),
            })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<TeacherSubjectDisplayRow?> GetTeacherSubjectDisplayAsync(long teacherId)
    {
        // One row: CustomSubject + the first linked ministry subject's bilingual names (ordered for
        // determinism; teachers normally have exactly one subject). The service selects EN/AR by the
        // student's language and applies the CustomSubject fallback (canonical pattern, StudentUserService).
        return await _context.Teachers
            .Where(t => t.Id == teacherId)
            .Select(t => new TeacherSubjectDisplayRow
            {
                CustomSubject = t.CustomSubject,
                SubjectNameEn = t.TeacherSubjects
                    .OrderBy(ts => ts.Id)
                    .Select(ts => ts.Subject.NameEn)
                    .FirstOrDefault(),
                SubjectNameAr = t.TeacherSubjects
                    .OrderBy(ts => ts.Id)
                    .Select(ts => ts.Subject.NameAr)
                    .FirstOrDefault(),
            })
            .AsNoTracking()
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<AssignmentTemplate?> GetTemplateByIdAndTeacherAsync(long templateId, long teacherId)
    {
        return await _context.AssignmentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<AssignmentTemplate?> GetTemplateWithScopesAsync(long templateId, long teacherId)
    {
        return await _context.AssignmentTemplates
            .Include(t => t.Scopes)
                .ThenInclude(s => s.TeacherStudent)
            .Include(t => t.Scopes)
                .ThenInclude(s => s.Session)
            .Include(t => t.Scopes)
                .ThenInclude(s => s.SessionGroup)
            .FirstOrDefaultAsync(t => t.Id == templateId && t.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<bool> CanEditRecurrencePatternAsync(long templateId)
    {
        // REQ-EXH-013: Pattern is locked once any obligation has Status != Pending.
        // Returns true if NO non-pending obligations exist for any occurrence of this template.
        bool hasRecordedData = await _context.StudentAssignmentObligations
            .AnyAsync(o => o.Occurrence.TemplateId == templateId
                        && o.Status != ObligationStatus.Pending);

        return !hasRecordedData;
    }

    /// <inheritdoc />
    public IQueryable<AssignmentTemplate> BuildAssignmentOverviewQuery(
        long teacherId,
        string? search = null,
        AssignmentType? assignmentType = null,
        bool? recurrenceFilter = null)
    {
        var query = _context.AssignmentTemplates
            .Where(t => t.TeacherId == teacherId)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{ArabicTextNormalizer.Normalize(search.Trim())}%";
            query = query.Where(t =>
                EF.Functions.Like(DbSearch.ArabicNormalize(t.Name), pattern)
                || EF.Functions.Like(DbSearch.ArabicNormalize(t.NameAr), pattern));
        }

        if (assignmentType.HasValue)
            query = query.Where(t => t.AssignmentType == assignmentType.Value);

        if (recurrenceFilter.HasValue)
            query = query.Where(t => t.IsRecurring == recurrenceFilter.Value);

        // Default ordering — newest first (REQ-EXH-033 lists "every assignment ever created").
        return query.OrderByDescending(t => t.CreateAt);
    }

    // ══════════════════════════════════════════════
    // ASSIGNMENT SCOPE QUERIES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddScopesRangeAsync(IEnumerable<AssignmentScope> scopes)
    {
        await _context.AssignmentScopes.AddRangeAsync(scopes);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignmentScope>> GetScopesByTemplateAsync(long templateId)
    {
        return await _context.AssignmentScopes
            .Where(s => s.TemplateId == templateId)
            .Include(s => s.TeacherStudent)
            .Include(s => s.Session)
            .Include(s => s.SessionGroup)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignmentScope>> GetScopesByTemplateIdsAsync(
        IEnumerable<long> templateIds)
    {
        var ids = templateIds.Distinct().ToList();
        if (ids.Count == 0) return new List<AssignmentScope>();

        return await _context.AssignmentScopes
            .Where(s => ids.Contains(s.TemplateId))
            .Include(s => s.Session)
            .Include(s => s.SessionGroup)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task DeleteScopesRangeAsync(IEnumerable<AssignmentScope> scopes)
    {
        _context.AssignmentScopes.RemoveRange(scopes);
        await Task.CompletedTask;
    }

    // ══════════════════════════════════════════════
    // ASSIGNMENT OCCURRENCE QUERIES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddOccurrenceAsync(AssignmentOccurrence occurrence)
    {
        await _context.AssignmentOccurrences.AddAsync(occurrence);
    }

    /// <inheritdoc />
    public async Task AddOccurrencesRangeAsync(IEnumerable<AssignmentOccurrence> occurrences)
    {
        await _context.AssignmentOccurrences.AddRangeAsync(occurrences);
    }

    /// <inheritdoc />
    public async Task UpdateOccurrenceAsync(AssignmentOccurrence occurrence)
    {
        _context.Entry(occurrence).State = EntityState.Modified;
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<AssignmentOccurrence?> GetOccurrenceByIdAndTeacherAsync(long occurrenceId, long teacherId)
    {
        return await _context.AssignmentOccurrences
            .FirstOrDefaultAsync(o => o.Id == occurrenceId && o.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<AssignmentOccurrence?> GetOccurrenceWithTemplateAsync(long occurrenceId, long teacherId)
    {
        return await _context.AssignmentOccurrences
            .Include(o => o.Template)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId && o.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignmentOccurrence>> GetOccurrencesByDateRangeAsync(
        long teacherId, DateTime startDate, DateTime endDate)
    {
        // Backed by IX_AssignmentOccurrences_DueDate (Section 7.2 index #7).
        return await _context.AssignmentOccurrences
            .Where(o => o.TeacherId == teacherId
                     && o.DueDate >= startDate.Date
                     && o.DueDate <= endDate.Date)
            .OrderBy(o => o.DueDate)
            .ThenBy(o => o.OccurrenceNumber)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> GetHighestOccurrenceNumberAsync(long templateId)
    {
        // Returns 0 if no occurrences exist yet — first occurrence will be number 1.
        var max = await _context.AssignmentOccurrences
            .Where(o => o.TemplateId == templateId)
            .Select(o => (int?)o.OccurrenceNumber)
            .MaxAsync();

        return max ?? 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignmentTemplate>> GetTemplatesDueForOccurrenceGenerationAsync(
        DateTime asOfDate)
    {
        // Backed by IX_AssignmentTemplates_RecurrenceScheduler (filtered partial index).
        // Service layer decides which dates to materialize based on RecurrencePattern.
        return await _context.AssignmentTemplates
            .Where(t => t.IsRecurring
                     && !t.IsRecurrenceStopped
                     && (t.RecurrenceEndDate == null || t.RecurrenceEndDate >= asOfDate.Date))
            .AsNoTracking()
            .ToListAsync();
    }

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — TRACKING VIEW (REQ-EXH-030/031/032)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddObligationsRangeAsync(IEnumerable<StudentAssignmentObligation> obligations)
    {
        await _context.StudentAssignmentObligations.AddRangeAsync(obligations);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetExistingObligationStudentIdsAsync(
        long occurrenceId, IEnumerable<long> candidateStudentIds)
    {
        // REQ-EXH-003 / REQ-EXH-035: Service layer calls this before bulk-inserting
        // obligations to filter out students who already have one for this occurrence.
        // The unique index UX_StudentAssignmentObligations_Occurrence_Student is the
        // safety net behind this check.
        var ids = candidateStudentIds.ToList();
        return await _context.StudentAssignmentObligations
            .Where(o => o.OccurrenceId == occurrenceId && ids.Contains(o.TeacherStudentId)).AsNoTracking()
            .Select(o => o.TeacherStudentId)
            .ToListAsync();
    }

    /// <inheritdoc />
    /// Hot-path query — projects directly to TrackingViewRow without materializing
    /// the full obligation graph. Backed by IX_StudentAssignmentObligations_Tracking
    /// (covering index, Section 7.2 index #1). Drives REQ-EXH-NFR-001 (&lt; 2 seconds).
    public async Task<(IReadOnlyList<TrackingViewRow> Items, int TotalCount)> GetTrackingViewPagedAsync(
        long teacherId, long occurrenceId,
        string? search,
        ObligationStatus? statusFilter,
        bool? missingEntries,
        decimal? gradeAboveThreshold,
        decimal? gradeBelowThreshold,
        bool? belowPassingGrade,
        bool? gradeEntered,
        int page, int pageSize)
    {
        // Look up the passing-threshold snapshot once so the IsBelowPassing flag
        // can be computed inside SQL.
        decimal? passingThreshold = await _context.AssignmentOccurrences
            .Where(o => o.Id == occurrenceId && o.TeacherId == teacherId)
            .Select(o => o.PassingThresholdSnapshot)
            .FirstOrDefaultAsync();

        var query = _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId && o.OccurrenceId == occurrenceId)
            // BUG-8 precedent: the rows are projected THROUGH TeacherStudent, so a
            // purged or soft-deleted student's obligation is dropped by that join —
            // but TotalCount was taken before it and counted them anyway. The list
            // then claimed 162 students and could only ever render 161, and paging
            // asked for a page that did not exist. Counting the same population the
            // rows come from is what makes the header, the pages and the grade
            // screen's chips agree.
            .Where(o => o.TeacherStudent != null);

        // REQ-EXH-031 — search by name or code.
        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{ArabicTextNormalizer.Normalize(search.Trim())}%";
            query = query.Where(o =>
                EF.Functions.Like(DbSearch.ArabicNormalize(o.TeacherStudent.StudentName), pattern)
                || EF.Functions.Like(DbSearch.ArabicNormalize(o.TeacherStudent.StudentCode), pattern));
        }

        // REQ-EXH-031 filter 1 — by completion / attendance status.
        if (statusFilter.HasValue)
            query = query.Where(o => o.Status == statusFilter.Value);

        // REQ-EXH-031 filter 2 — students who have not submitted homework or did not attend.
        if (missingEntries == true)
        {
            query = query.Where(o =>
                o.Status == ObligationStatus.NotDone
                || o.Status == ObligationStatus.DidNotAttend
                || o.Status == ObligationStatus.Pending);
        }

        // REQ-EXH-031 filter 3 — grades above a specified value (exam only).
        if (gradeAboveThreshold.HasValue)
            query = query.Where(o => o.GradeValue.HasValue && o.GradeValue.Value > gradeAboveThreshold.Value);

        // REQ-EXH-031 filter 4 — grades below a specified value (exam only).
        if (gradeBelowThreshold.HasValue)
            query = query.Where(o => o.GradeValue.HasValue && o.GradeValue.Value < gradeBelowThreshold.Value);

        // Grade-entry chips: has a grade / still waiting for one. Applied before the
        // count so the chip's number and its page always describe the same set.
        //
        // The predicate is deliberately the SAME one ExamService.ComputeStats uses for
        // GradedCount (flag AND value), which is the number the chip shows: a row
        // flagged graded but carrying no value would otherwise be counted as graded
        // and listed as not graded, and the two would disagree by one forever.
        if (gradeEntered == true)
            query = query.Where(o => o.IsGradeEntered && o.GradeValue.HasValue);
        else if (gradeEntered == false)
            query = query.Where(o => !o.IsGradeEntered || !o.GradeValue.HasValue);

        // REQ-EXH-031 filter 5 — students who scored below the passing threshold.
        if (belowPassingGrade == true && passingThreshold.HasValue)
        {
            query = query.Where(o => o.GradeValue.HasValue && o.GradeValue.Value < passingThreshold.Value);
        }

        int totalCount = await query.CountAsync();

        // Project to TrackingViewRow inside SQL — avoids materializing entities.
        var items = await query
            // Student names repeat in a large roster; without a unique tiebreaker SQL Server is free
            // to order ties differently per page, which repeats some students and skips others.
            .OrderBy(o => o.TeacherStudent.StudentName)
            .ThenBy(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new TrackingViewRow
            {
                ObligationId = o.Id,
                TeacherStudentId = o.TeacherStudentId,
                StudentName = o.TeacherStudent.StudentName,
                StudentCode = o.TeacherStudent.StudentCode,
                Status = o.Status,
                GradeValue = o.GradeValue,
                IsGradeEntered = o.IsGradeEntered,
                IsBelowPassing = passingThreshold.HasValue
                                  && o.GradeValue.HasValue
                                  && o.GradeValue.Value < passingThreshold.Value,
                MarkedByScan = o.MarkedByScan,
                ScannedAt = o.ScannedAt,
                UpdatedAt = o.UpdatedAt,
                ObligationRowVersion = o.RowVersion, // For concurrency control on the grade-entry path.
            })
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    /// REQ-EXH-026-A: Backed by IX_StudentAssignmentObligations_PendingGrades filtered index
    /// (Status IN (3, 6) — Attended and DoneWithoutGrade). The filtered index makes this
    /// query touch only grade-pending rows.
    public async Task<(IReadOnlyList<TrackingViewRow> Items, int TotalCount)> GetGradeEntryViewPagedAsync(
        long teacherId, long occurrenceId,
        string? search,
        int page, int pageSize)
    {
        decimal? passingThreshold = await _context.AssignmentOccurrences
            .Where(o => o.Id == occurrenceId && o.TeacherId == teacherId)
            .Select(o => o.PassingThresholdSnapshot)
            .FirstOrDefaultAsync();

        var query = _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.OccurrenceId == occurrenceId
                     && (o.Status == ObligationStatus.Attended
                      || o.Status == ObligationStatus.DoneWithoutGrade));

        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{ArabicTextNormalizer.Normalize(search.Trim())}%";
            query = query.Where(o =>
                EF.Functions.Like(DbSearch.ArabicNormalize(o.TeacherStudent.StudentName), pattern)
                || EF.Functions.Like(DbSearch.ArabicNormalize(o.TeacherStudent.StudentCode), pattern));
        }

        int totalCount = await query.CountAsync();

        var items = await query
            // Student names repeat in a large roster; without a unique tiebreaker SQL Server is free
            // to order ties differently per page, which repeats some students and skips others.
            .OrderBy(o => o.TeacherStudent.StudentName)
            .ThenBy(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new TrackingViewRow
            {
                ObligationId = o.Id,
                TeacherStudentId = o.TeacherStudentId,
                StudentName = o.TeacherStudent.StudentName,
                StudentCode = o.TeacherStudent.StudentCode,
                Status = o.Status,
                GradeValue = o.GradeValue,
                IsGradeEntered = o.IsGradeEntered,
                IsBelowPassing = false, // Grade pending — not applicable.
                MarkedByScan = o.MarkedByScan,
                ScannedAt = o.ScannedAt,
                UpdatedAt = o.UpdatedAt,
                ObligationRowVersion=o.RowVersion, // For concurrency control on the grade-entry path.
            })
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — STUDENT HISTORY (REQ-EXH-040)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<(IReadOnlyList<StudentAssignmentObligation> Items, int TotalCount)>
        GetStudentHistoryPagedAsync(
            long teacherId, long teacherStudentId,
            DateTime? startDate, DateTime? endDate,
            AssignmentType? assignmentType,
            int page, int pageSize)
    {
        // Backed by IX_StudentAssignmentObligations_StudentHistory (Section 7.2 index #2).
        var query = _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId && o.TeacherStudentId == teacherStudentId)
            .Include(o => o.Occurrence)
                .ThenInclude(occ => occ.Template)
            .AsQueryable();

        if (startDate.HasValue)
            query = query.Where(o => o.Occurrence.DueDate >= startDate.Value.Date);
        if (endDate.HasValue)
            query = query.Where(o => o.Occurrence.DueDate <= endDate.Value.Date);
        if (assignmentType.HasValue)
            query = query.Where(o => o.Occurrence.Template.AssignmentType == assignmentType.Value);

        int totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(o => o.CreateAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<(int Completed, int TotalAssigned)> GetHomeworkCompletionStatsAsync(
        long teacherId, long teacherStudentId)
    {
        // REQ-EXH-040 — single GroupBy projection, evaluated entirely in SQL.
        var stats = await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.TeacherStudentId == teacherStudentId
                     && o.Occurrence.Template.AssignmentType == AssignmentType.Homework)
            .GroupBy(o => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Completed = g.Count(o => o.Status == ObligationStatus.Done
                                      || o.Status == ObligationStatus.DoneWithGrade
                                      || o.Status == ObligationStatus.DoneWithoutGrade)
            })
            .FirstOrDefaultAsync();

        return stats == null ? (0, 0) : (stats.Completed, stats.Total);
    }

    /// <inheritdoc />
    public async Task<(int Attended, int TotalAssigned)> GetExamAttendanceStatsAsync(
        long teacherId, long teacherStudentId)
    {
        var stats = await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.TeacherStudentId == teacherStudentId
                     && o.Occurrence.Template.AssignmentType == AssignmentType.Exam)
            .GroupBy(o => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Attended = g.Count(o => o.Status == ObligationStatus.Attended
                                     || o.Status == ObligationStatus.AttendedWithGrade)
            })
            .FirstOrDefaultAsync();

        return stats == null ? (0, 0) : (stats.Attended, stats.Total);
    }

    /// <inheritdoc />
    public async Task<(int Total, int Pending, int Submitted, int NotSubmitted)> GetHomeworkStatusBreakdownAsync(
        long teacherId, long teacherStudentId)
    {
        // Same join path and single-GroupBy-query shape as GetHomeworkCompletionStatsAsync —
        // kept as its own method because it returns a different shape (Pending/Submitted/
        // NotSubmitted breakdown for the Parent dashboard) that existing callers don't need.
        var stats = await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.TeacherStudentId == teacherStudentId
                     && o.Occurrence.Template.AssignmentType == AssignmentType.Homework)
            .GroupBy(o => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Pending = g.Count(o => o.Status == ObligationStatus.Pending),
                Submitted = g.Count(o => o.Status == ObligationStatus.Done
                                      || o.Status == ObligationStatus.DoneWithGrade
                                      || o.Status == ObligationStatus.DoneWithoutGrade),
                NotSubmitted = g.Count(o => o.Status == ObligationStatus.NotDone)
            })
            .FirstOrDefaultAsync();

        return stats == null
            ? (0, 0, 0, 0)
            : (stats.Total, stats.Pending, stats.Submitted, stats.NotSubmitted);
    }

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — REPORTS (REQ-EXH-039 through 046)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsByOccurrenceAsync(
        long teacherId, long occurrenceId)
    {
        // REQ-EXH-039 — Single Assignment Report.
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId && o.OccurrenceId == occurrenceId)
            .Include(o => o.TeacherStudent)
            .OrderBy(o => o.TeacherStudent.StudentName)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<AbsenceReportRow> Items, int TotalCount)>
        GetHomeworkAbsenceReportPagedAsync(
            long teacherId,
            long? sessionId, long? sessionGroupId, long? specificTemplateId,
            int page, int pageSize)
    {
        return await BuildAbsenceReportAsync(
            teacherId, AssignmentType.Homework,
            new[] { ObligationStatus.NotDone },
            sessionId, sessionGroupId, specificTemplateId,
            page, pageSize);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<AbsenceReportRow> Items, int TotalCount)>
        GetExamAbsenceReportPagedAsync(
            long teacherId,
            long? sessionId, long? sessionGroupId, long? specificTemplateId,
            int page, int pageSize)
    {
        return await BuildAbsenceReportAsync(
            teacherId, AssignmentType.Exam,
            new[] { ObligationStatus.DidNotAttend },
            sessionId, sessionGroupId, specificTemplateId,
            page, pageSize);
    }

    /// <summary>
    /// Shared implementation for REQ-EXH-041 and REQ-EXH-042. Backed by
    /// IX_StudentAssignmentObligations_Absence filtered index (Status IN (2, 5)).
    /// </summary>
    private async Task<(IReadOnlyList<AbsenceReportRow> Items, int TotalCount)> BuildAbsenceReportAsync(
        long teacherId,
        AssignmentType assignmentType,
        ObligationStatus[] absenceStatuses,
        long? sessionId, long? sessionGroupId, long? specificTemplateId,
        int page, int pageSize)
    {
        // First — filter obligations to the absence states scoped by tenant and assignment type.
        var absentObligations = _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && absenceStatuses.Contains(o.Status)
                     && o.Occurrence.Template.AssignmentType == assignmentType);

        if (specificTemplateId.HasValue)
            absentObligations = absentObligations.Where(o => o.Occurrence.TemplateId == specificTemplateId.Value);

        if (sessionId.HasValue)
        {
            absentObligations = absentObligations.Where(o =>
                o.TeacherStudent.SessionId == sessionId.Value);
        }

        if (sessionGroupId.HasValue)
        {
            absentObligations = absentObligations.Where(o =>
                o.TeacherStudent.Session != null
                && o.TeacherStudent.Session.SessionGroupId == sessionGroupId.Value);
        }

        // Group by student — each row is one student with their list of missed assignments.
        var grouped = absentObligations
            .GroupBy(o => new
            {
                o.TeacherStudentId,
                o.TeacherStudent.StudentName,
                o.TeacherStudent.StudentCode,
                o.TeacherStudent.SessionId,
                SessionName = o.TeacherStudent.Session != null ? o.TeacherStudent.Session.SessionName : null,
            })
            .Select(g => new
            {
                g.Key,
                MissedNames = g.Select(o => o.Occurrence.Template.Name).ToList(),
                TotalMissed = g.Count(),
            });

        int totalCount = await grouped.CountAsync();

        var paged = await grouped
            .OrderByDescending(g => g.TotalMissed)
            .ThenBy(g => g.Key.StudentName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = paged.Select(p => new AbsenceReportRow
        {
            TeacherStudentId = p.Key.TeacherStudentId,
            StudentName = p.Key.StudentName,
            StudentCode = p.Key.StudentCode,
            SessionId = p.Key.SessionId,
            SessionName = p.Key.SessionName,
            MissedAssignmentNames = p.MissedNames,
            TotalMissedCount = p.TotalMissed,
        }).ToList();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<ExamGradeAnalysis> GetExamGradeAnalysisAsync(long teacherId, long occurrenceId)
    {
        // Single round trip: fetch the occurrence's snapshots and aggregate stats together.
        var occurrence = await _context.AssignmentOccurrences
            .Where(o => o.Id == occurrenceId && o.TeacherId == teacherId)
            .Select(o => new
            {
                o.Id,
                o.MaxGradeSnapshot,
                o.PassingThresholdSnapshot,
            })
            .FirstOrDefaultAsync();

        if (occurrence == null)
        {
            return new ExamGradeAnalysis { OccurrenceId = occurrenceId };
        }

        var threshold = occurrence.PassingThresholdSnapshot;

        // REQ-EXH-043 — only consider attended students with a grade entered.
        var attendedWithGrade = _context.StudentAssignmentObligations
            .Where(o => o.OccurrenceId == occurrenceId
                     && o.TeacherId == teacherId
                     && o.Status == ObligationStatus.AttendedWithGrade
                     && o.GradeValue.HasValue);

        var stats = await attendedWithGrade
            .GroupBy(o => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Highest = g.Max(o => o.GradeValue),
                Lowest = g.Min(o => o.GradeValue),
                Average = g.Average(o => o.GradeValue),
                AbovePassing = threshold.HasValue
                    ? g.Count(o => o.GradeValue!.Value >= threshold.Value)
                    : 0,
                BelowPassing = threshold.HasValue
                    ? g.Count(o => o.GradeValue!.Value < threshold.Value)
                    : 0,
            })
            .FirstOrDefaultAsync();

        return new ExamGradeAnalysis
        {
            OccurrenceId = occurrenceId,
            MaxGradeSnapshot = occurrence.MaxGradeSnapshot,
            PassingThresholdSnapshot = occurrence.PassingThresholdSnapshot,
            AttendedStudentsCount = stats?.Count ?? 0,
            HighestGrade = stats?.Highest,
            LowestGrade = stats?.Lowest,
            AverageGrade = stats?.Average,
            AbovePassingCount = stats?.AbovePassing ?? 0,
            BelowPassingCount = stats?.BelowPassing ?? 0,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentAssignmentObligation>> GetBelowPassingGradeAsync(
        long teacherId,
        long? occurrenceId, long? sessionId, long? sessionGroupId)
    {
        // REQ-EXH-044 — Below Passing Grade Report. Comparison against the snapshotted
        // PassingThreshold on the occurrence row (NOT the live template, per REQ-EXH-043
        // historical reproducibility).
        var query = _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.Status == ObligationStatus.AttendedWithGrade
                     && o.GradeValue.HasValue
                     && o.Occurrence.PassingThresholdSnapshot.HasValue
                     && o.GradeValue.Value < o.Occurrence.PassingThresholdSnapshot.Value);

        if (occurrenceId.HasValue)
            query = query.Where(o => o.OccurrenceId == occurrenceId.Value);

        if (sessionId.HasValue)
            query = query.Where(o => o.TeacherStudent.SessionId == sessionId.Value);

        if (sessionGroupId.HasValue)
        {
            query = query.Where(o =>
                o.TeacherStudent.Session != null
                && o.TeacherStudent.Session.SessionGroupId == sessionGroupId.Value);
        }

        return await query
            .Include(o => o.TeacherStudent)
            .Include(o => o.Occurrence)
                .ThenInclude(occ => occ.Template)
            .OrderBy(o => o.GradeValue)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentAssignmentObligation>> GetAboveGradeThresholdAsync(
        long teacherId, long occurrenceId, decimal threshold)
    {
        // REQ-EXH-045 — Above Grade Threshold Report (tutor-defined threshold, not the passing one).
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.OccurrenceId == occurrenceId
                     && o.Status == ObligationStatus.AttendedWithGrade
                     && o.GradeValue.HasValue
                     && o.GradeValue.Value > threshold)
            .Include(o => o.TeacherStudent)
            .OrderByDescending(o => o.GradeValue)
            .AsNoTracking()
            .ToListAsync();
    }

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — IDEMPOTENT BARCODE SCAN (REQ-EXH-026, NFR-002)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    /// Atomic conditional UPDATE — design decision 5.3.
    /// One round-trip; no SELECT-then-UPDATE; no DbUpdateConcurrencyException on duplicate scan.
    /// Resolves the obligation by (TeacherId, OccurrenceId, TeacherStudentId), which hits
    /// the unique index UX_StudentAssignmentObligations_Occurrence_Student.
    /// Returns rows affected — 0 means another scan won the race (idempotent success).
    public async Task<int> TryMarkScannedAsync(
        long teacherId, long occurrenceId, long teacherStudentId,
        ObligationStatus newStatus, DateTime scannedAt, long scannedByUserId)
    {
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.OccurrenceId == occurrenceId
                     && o.TeacherStudentId == teacherStudentId
                     && o.Status == ObligationStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, newStatus)
                .SetProperty(o => o.MarkedByScan, true)
                .SetProperty(o => o.ScannedAt, scannedAt)
                .SetProperty(o => o.LastUpdatedByUserId, (long?)scannedByUserId)
                .SetProperty(o => o.UpdatedAt, DateTime.UtcNow));
    }

    // ══════════════════════════════════════════════
    // OBLIGATION QUERIES — SINGLE-ROW LOOKUPS
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<StudentAssignmentObligation?> GetObligationByIdAndTeacherAsync(
        long obligationId, long teacherId)
    {
        // Tracked: caller (manual grade-entry) modifies and saves with RowVersion check.
        return await _context.StudentAssignmentObligations
            .FirstOrDefaultAsync(o => o.Id == obligationId && o.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<StudentAssignmentObligation?> GetObligationByOccurrenceAndStudentAsync(
        long occurrenceId, long teacherStudentId)
    {
        // Hits UX_StudentAssignmentObligations_Occurrence_Student — O(1) lookup.
        return await _context.StudentAssignmentObligations
            .FirstOrDefaultAsync(o => o.OccurrenceId == occurrenceId
                                   && o.TeacherStudentId == teacherStudentId);
    }

    /// <inheritdoc />
    public async Task DeleteObligationAsync(StudentAssignmentObligation obligation)
    {
        _context.StudentAssignmentObligations.Remove(obligation);
        await Task.CompletedTask;
    }

    // ══════════════════════════════════════════════
    // AUDIT LOG QUERIES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddAuditLogAsync(StudentObligationAuditLog auditLog)
    {
        await _context.StudentObligationAuditLogs.AddAsync(auditLog);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentObligationAuditLog>> GetAuditHistoryByObligationAsync(
        long obligationId)
    {
        // Backed by IX_StudentObligationAuditLogs_Obligation_ChangedAt.
        return await _context.StudentObligationAuditLogs
            .Where(a => a.StudentObligationId == obligationId)
            .Include(a => a.ChangedByUser)
            .OrderByDescending(a => a.ChangedAt)
            .AsNoTracking()
            .ToListAsync();
    }

    // ══════════════════════════════════════════════
    // DELETION LOG QUERIES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddDeletionLogAsync(AssignmentDeletionLog log)
    {
        await _context.AssignmentDeletionLogs.AddAsync(log);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<AssignmentDeletionLog> Items, int TotalCount)>
        GetDeletionLogsPagedAsync(
            long teacherId, DateTime? startDate, DateTime? endDate, int page, int pageSize)
    {
        var query = _context.AssignmentDeletionLogs
            .Where(d => d.TeacherId == teacherId);

        if (startDate.HasValue)
            query = query.Where(d => d.DeletedAt >= startDate.Value);
        if (endDate.HasValue)
            query = query.Where(d => d.DeletedAt <= endDate.Value.Date.AddDays(1));

        int totalCount = await query.CountAsync();

        var items = await query
            .Include(d => d.DeletedByUser)
            .OrderByDescending(d => d.DeletedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }


    // For documentation only; do not actually create this class. Copy the methods
    // below into the existing ExamHomeworkRepo class.



 

  
    // For documentation only. Do not actually create a class. Copy the methods below
    // into the existing ExamHomeworkRepo class. The `_context` references resolve
    // correctly when these methods live alongside the existing ones.

    // ══════════════════════════════════════════════
    // SCOPE COUNTS PER TEMPLATE
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public  async Task<Dictionary<long, ScopeCountAggregate>> GetScopeCountsByTemplateIdsAsync(
        IEnumerable<long> templateIds)
    {
        var idList = templateIds.ToList();
        if (idList.Count == 0) return new Dictionary<long, ScopeCountAggregate>();

        var rows = await _context.AssignmentScopes
            .Where(s => idList.Contains(s.TemplateId))
            .GroupBy(s => s.TemplateId)
            .Select(g => new ScopeCountAggregate
            {
                TemplateId = g.Key,
                IndividualCount = g.Count(s => s.ScopeType == AssignmentScopeType.IndividualStudent),
                SessionCount = g.Count(s => s.ScopeType == AssignmentScopeType.Session),
                GroupCount = g.Count(s => s.ScopeType == AssignmentScopeType.SessionGroup),
            })
            .AsNoTracking()
            .ToListAsync();

        return rows.ToDictionary(r => r.TemplateId);
    }




    // ══════════════════════════════════════════════
    // ASSIGNMENT OVERVIEW LIST — replaces BuildAssignmentOverviewQuery
    // ══════════════════════════════════════════════

    /// <inheritdoc />
   public async Task<(IReadOnlyList<AssignmentTemplate> Items, int TotalCount)>
        GetAssignmentOverviewPagedAsync(
            long teacherId,
            string? search,
            AssignmentType? assignmentType,
            RecurrencePattern? recurrencePattern,
            bool? isRecurring,
            int page,
            int pageSize)
    {
        // Backed by IX_AssignmentTemplates_TeacherList (Section 7.2 covering index).
        var query = _context.AssignmentTemplates
            .Where(t => t.TeacherId == teacherId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{ArabicTextNormalizer.Normalize(search.Trim())}%";
            query = query.Where(t =>
                EF.Functions.Like(DbSearch.ArabicNormalize(t.Name), pattern)
             || EF.Functions.Like(DbSearch.ArabicNormalize(t.NameAr), pattern));
        }

        if (assignmentType.HasValue)
            query = query.Where(t => t.AssignmentType == assignmentType.Value);

        if (recurrencePattern.HasValue)
            query = query.Where(t => t.RecurrencePattern == recurrencePattern.Value);

        if (isRecurring.HasValue)
            query = query.Where(t => t.IsRecurring == isRecurring.Value);

        int totalCount = await query.CountAsync();

        // Newest first (REQ-EXH-033 lists "every assignment ever created").
        var items = await query
            .OrderByDescending(t => t.CreateAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }




    // ══════════════════════════════════════════════
    // OVERVIEW & OCCURRENCE AGGREGATES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Dictionary<long, long>> GetLatestOccurrenceIdsByTemplateAsync(
        IEnumerable<long> templateIds)
    {
        var idList = templateIds.ToList();
        if (idList.Count == 0) return new Dictionary<long, long>();

        // Single query: GROUP BY templateId, take the row with MAX(OccurrenceNumber).
        var rows = await _context.AssignmentOccurrences
            .Where(o => idList.Contains(o.TemplateId))
            .GroupBy(o => o.TemplateId)
            .Select(g => new
            {
                TemplateId = g.Key,
                LatestOccurrenceId = g.OrderByDescending(o => o.OccurrenceNumber)
                                      .Select(o => o.Id)
                                      .First()
            })
            .AsNoTracking()
            .ToListAsync();

        return rows.ToDictionary(r => r.TemplateId, r => r.LatestOccurrenceId);
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, OccurrenceCompletionSummary>>
        GetCompletionSummariesByOccurrenceIdsAsync(IEnumerable<long> occurrenceIds)
    {
        var idList = occurrenceIds.ToList();
        if (idList.Count == 0) return new Dictionary<long, OccurrenceCompletionSummary>();

        var rows = await _context.StudentAssignmentObligations
            // Skip soft-deleted students so these home-card counts match the opened-exam roster
            // (which INNER-JOINs TeacherStudent and drops them) — B3.
            .Where(o => idList.Contains(o.OccurrenceId) && !o.TeacherStudent.IsDeleted)
            .GroupBy(o => o.OccurrenceId)
            .Select(g => new OccurrenceCompletionSummary
            {
                OccurrenceId = g.Key,
                TotalStudents = g.Count(),
                DoneOrAttended = g.Count(o =>
                    o.Status == ObligationStatus.Done
                 || o.Status == ObligationStatus.Attended
                 || o.Status == ObligationStatus.AttendedWithGrade
                 || o.Status == ObligationStatus.DoneWithoutGrade
                 || o.Status == ObligationStatus.DoneWithGrade),
                NotDoneOrAbsent = g.Count(o =>
                    o.Status == ObligationStatus.NotDone
                 || o.Status == ObligationStatus.DidNotAttend),
                Pending = g.Count(o => o.Status == ObligationStatus.Pending),
            })
            .AsNoTracking()
            .ToListAsync();

        return rows.ToDictionary(r => r.OccurrenceId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExamHomeOccurrenceRow>> GetExamOccurrencesForHomeAsync(long teacherId)
    {
        return await _context.AssignmentOccurrences
            .Where(o => o.TeacherId == teacherId && o.Template.AssignmentType == AssignmentType.Exam)
            .OrderBy(o => o.DueDate)
            .Select(o => new ExamHomeOccurrenceRow
            {
                OccurrenceId = o.Id,
                TemplateId = o.TemplateId,
                ExamName = o.Template.Name,
                DeliveryType = o.Template.ExamDeliveryType,
                SessionId = o.SessionId,
                SessionName = o.Session != null ? o.Session.SessionName : null,
                SessionOccurrenceId = o.SessionOccurrenceId,
                DueDate = o.DueDate,
            })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<ExamHomeOccurrenceRow> Items, int TotalCount)>
        GetExamOccurrencesForHomePagedAsync(long teacherId, bool isPast, DateTime today, int page, int pageSize)
    {
        var day = today.Date;
        var baseQuery = _context.AssignmentOccurrences
            .Where(o => o.TeacherId == teacherId && o.Template.AssignmentType == AssignmentType.Exam)
            .Where(o => isPast ? o.DueDate < day : o.DueDate >= day);

        int totalCount = await baseQuery.CountAsync();

        var ordered = isPast
            ? baseQuery.OrderByDescending(o => o.DueDate)
            : baseQuery.OrderBy(o => o.DueDate);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new ExamHomeOccurrenceRow
            {
                OccurrenceId = o.Id,
                TemplateId = o.TemplateId,
                ExamName = o.Template.Name,
                DeliveryType = o.Template.ExamDeliveryType,
                SessionId = o.SessionId,
                SessionName = o.Session != null ? o.Session.SessionName : null,
                SessionOccurrenceId = o.SessionOccurrenceId,
                DueDate = o.DueDate,
            })
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExamHomeOccurrenceRow>> GetExamOccurrencesForDateAsync(
        long teacherId, DateTime date)
    {
        var day = date.Date;
        return await _context.AssignmentOccurrences
            .Where(o => o.TeacherId == teacherId
                     && o.DueDate == day
                     && o.Template.AssignmentType == AssignmentType.Exam)
            .OrderBy(o => o.DueDate)
            .Select(o => new ExamHomeOccurrenceRow
            {
                OccurrenceId = o.Id,
                TemplateId = o.TemplateId,
                ExamName = o.Template.Name,
                DeliveryType = o.Template.ExamDeliveryType,
                SessionId = o.SessionId,
                SessionName = o.Session != null ? o.Session.SessionName : null,
                SessionOccurrenceId = o.SessionOccurrenceId,
                DueDate = o.DueDate,
            })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExamOccurrenceInfoRow>> GetExamOccurrencesByTemplateAsync(
        long teacherId, long templateId)
    {
        return await _context.AssignmentOccurrences
            .Where(o => o.TeacherId == teacherId && o.TemplateId == templateId)
            .OrderBy(o => o.DueDate).ThenBy(o => o.OccurrenceNumber)
            .Select(o => new ExamOccurrenceInfoRow
            {
                OccurrenceId = o.Id,
                SessionId = o.SessionId,
                SessionName = o.Session != null ? o.Session.SessionName : null,
                DueDate = o.DueDate,
                MaxGradeSnapshot = o.MaxGradeSnapshot,
                PassingThresholdSnapshot = o.PassingThresholdSnapshot,
            })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExamRosterRow>> GetExamRosterByTemplateAsync(
        long teacherId, long templateId)
    {
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId && o.Occurrence.TemplateId == templateId)
            .OrderBy(o => o.TeacherStudent.StudentName)
            .Select(o => new ExamRosterRow
            {
                OccurrenceId = o.OccurrenceId,
                ObligationId = o.Id,
                TeacherStudentId = o.TeacherStudentId,
                StudentName = o.TeacherStudent.StudentName,
                StudentCode = o.TeacherStudent.StudentCode,
                Status = o.Status,
                GradeValue = o.GradeValue,
                IsGradeEntered = o.IsGradeEntered,
                ObligationRowVersion = o.RowVersion,
            })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsForGradingByIdsAsync(
        long teacherId, IEnumerable<long> obligationIds)
    {
        var ids = obligationIds.Distinct().ToList();
        if (ids.Count == 0) return new List<StudentAssignmentObligation>();

        // Tracked (no AsNoTracking) so the batch grade path can apply RowVersion concurrency.
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId && ids.Contains(o.Id))
            .Include(o => o.Occurrence)
                .ThenInclude(occ => occ.Template)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsForGradingByTemplateAndStudentsAsync(
        long teacherId, long templateId, IEnumerable<long> teacherStudentIds)
    {
        var ids = teacherStudentIds.Distinct().ToList();
        if (ids.Count == 0) return new List<StudentAssignmentObligation>();

        // Tracked so the batch grade path can apply RowVersion concurrency. Resolves each student's
        // obligation within this exam (one primary session → one row; >1 signals a data anomaly).
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.Occurrence.TemplateId == templateId
                     && ids.Contains(o.TeacherStudentId))
            .Include(o => o.Occurrence)
                .ThenInclude(occ => occ.Template)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetExamOccurrenceIdsBySessionOccurrenceAsync(
        long teacherId, long sessionOccurrenceId)
    {
        return await _context.AssignmentOccurrences
            .Where(o => o.TeacherId == teacherId
                     && o.SessionOccurrenceId == sessionOccurrenceId
                     && o.Template.AssignmentType == AssignmentType.Exam)
            .Select(o => o.Id)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetReferencedSessionOccurrenceIdsAsync(
        IEnumerable<long> sessionOccurrenceIds)
    {
        var ids = sessionOccurrenceIds.Distinct().ToList();
        if (ids.Count == 0)
            return Array.Empty<long>();

        return await _context.AssignmentOccurrences
            .Where(o => o.SessionOccurrenceId.HasValue && ids.Contains(o.SessionOccurrenceId.Value))
            .Select(o => o.SessionOccurrenceId!.Value)
            .Distinct()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> SetExamAttendanceByOccurrenceAsync(
        long teacherId, long occurrenceId, IEnumerable<long> teacherStudentIds,
        ObligationStatus newStatus, bool clearGrade, bool skipGraded, DateTime utcNow, long actingUserId)
    {
        var ids = teacherStudentIds.Distinct().ToList();
        if (ids.Count == 0) return 0;

        var query = _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.OccurrenceId == occurrenceId
                     && ids.Contains(o.TeacherStudentId));

        // Preserve already-entered grades when marking "present".
        if (skipGraded)
            query = query.Where(o => o.Status != ObligationStatus.AttendedWithGrade);

        if (clearGrade)
        {
            // Only rows that actually change. RowVersion is a SQL rowversion, so an UPDATE that
            // writes identical values still bumps it — and this runs for the WHOLE class roster on
            // every attendance write (the reconcile). Without this guard one scan invalidated the
            // concurrency token of every obligation on the occurrence, so a teacher grading on
            // another device lost her entire batch to a 409. See SaveGradesAsync.
            return await query
                .Where(o => o.Status != newStatus || o.GradeValue != null || o.IsGradeEntered)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(o => o.Status, newStatus)
                    .SetProperty(o => o.GradeValue, (decimal?)null)
                    .SetProperty(o => o.IsGradeEntered, false)
                    .SetProperty(o => o.LastUpdatedByUserId, (long?)actingUserId)
                    .SetProperty(o => o.UpdatedAt, utcNow));
        }

        return await query
            .Where(o => o.Status != newStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, newStatus)
                .SetProperty(o => o.LastUpdatedByUserId, (long?)actingUserId)
                .SetProperty(o => o.UpdatedAt, utcNow));
    }

    /// <inheritdoc />
    public async Task<int> RevertExamAttendanceForUnmarkedAsync(
        long teacherId, long occurrenceId, IReadOnlyCollection<long> markedStudentIds,
        DateTime utcNow, long actingUserId)
    {
        var marked = markedStudentIds as ICollection<long> ?? markedStudentIds.ToList();

        // Only attendance-derived statuses are reverted; Pending (already unmarked) and homework
        // statuses are left alone. An empty "marked" set (every record removed) correctly reverts all.
        var query = _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.OccurrenceId == occurrenceId
                     && !marked.Contains(o.TeacherStudentId)
                     && (o.Status == ObligationStatus.Attended
                      || o.Status == ObligationStatus.AttendedWithGrade
                      || o.Status == ObligationStatus.DidNotAttend));

        return await query.ExecuteUpdateAsync(setters => setters
            .SetProperty(o => o.Status, ObligationStatus.Pending)
            .SetProperty(o => o.GradeValue, (decimal?)null)
            .SetProperty(o => o.IsGradeEntered, false)
            .SetProperty(o => o.LastUpdatedByUserId, (long?)actingUserId)
            .SetProperty(o => o.UpdatedAt, utcNow));
    }

    /// <inheritdoc />
    public async Task UpdateExamGradeSnapshotsAsync(
        long teacherId, long templateId, decimal maxGrade, decimal passingThreshold)
    {
        await _context.AssignmentOccurrences
            .Where(o => o.TeacherId == teacherId && o.TemplateId == templateId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.MaxGradeSnapshot, (decimal?)maxGrade)
                .SetProperty(o => o.PassingThresholdSnapshot, (decimal?)passingThreshold));
    }

    /// <inheritdoc />
    public async Task EnsureExamObligationsExistAsync(
        long teacherId, long occurrenceId, IReadOnlyCollection<long> studentIds,
        ObligationStatus status, DateTime utcNow, long actingUserId)
    {
        if (studentIds.Count == 0) return;

        // Tenant + existence guard: only materialize into the teacher's own occurrence.
        bool ownsOccurrence = await _context.AssignmentOccurrences
            .AnyAsync(o => o.Id == occurrenceId && o.TeacherId == teacherId);
        if (!ownsOccurrence) return;

        var existing = await _context.StudentAssignmentObligations
            .Where(o => o.OccurrenceId == occurrenceId && studentIds.Contains(o.TeacherStudentId))
            .Select(o => o.TeacherStudentId)
            .ToListAsync();
        var missing = studentIds.Except(existing).ToList();
        if (missing.Count == 0) return;

        var rows = missing.Select(sid => new StudentAssignmentObligation
        {
            OccurrenceId = occurrenceId,
            TeacherId = teacherId,
            TeacherStudentId = sid,
            Status = status,
            IsGradeEntered = false,
            MarkedByScan = false,
            LastUpdatedByUserId = actingUserId,
            UpdatedAt = utcNow,
            CreateAt = utcNow,
        });
        await _context.StudentAssignmentObligations.AddRangeAsync(rows);
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, DateTime?>> GetNextOrLastOccurrenceDatesAsync(
        IEnumerable<long> templateIds, DateTime today)
    {
        var idList = templateIds.ToList();
        if (idList.Count == 0) return new Dictionary<long, DateTime?>();

        DateTime cutoff = today.Date;

        var nextRows = await _context.AssignmentOccurrences
            .Where(o => idList.Contains(o.TemplateId) && o.DueDate >= cutoff)
            .GroupBy(o => o.TemplateId)
            .Select(g => new { TemplateId = g.Key, Date = g.Min(o => (DateTime?)o.DueDate) })
            .AsNoTracking()
            .ToListAsync();

        var lastRows = await _context.AssignmentOccurrences
            .Where(o => idList.Contains(o.TemplateId) && o.DueDate < cutoff)
            .GroupBy(o => o.TemplateId)
            .Select(g => new { TemplateId = g.Key, Date = g.Max(o => (DateTime?)o.DueDate) })
            .AsNoTracking()
            .ToListAsync();

        var nextDict = nextRows.ToDictionary(r => r.TemplateId, r => r.Date);
        var lastDict = lastRows.ToDictionary(r => r.TemplateId, r => r.Date);

        return idList.ToDictionary(
            id => id,
            id => nextDict.GetValueOrDefault(id) ?? lastDict.GetValueOrDefault(id));
    }

    // ══════════════════════════════════════════════
    // OCCURRENCE PAGINATION
    // ══════════════════════════════════════════════

    /// <inheritdoc />
   public  async Task<(IReadOnlyList<AssignmentOccurrence> Items, int TotalCount)>
        GetOccurrencesByTemplatePagedAsync(long teacherId, long templateId, int page, int pageSize)
    {
        var query = _context.AssignmentOccurrences
            .Where(o => o.TeacherId == teacherId && o.TemplateId == templateId);

        int totalCount = await query.CountAsync();

        var items = await query
            .OrderBy(o => o.OccurrenceNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<AssignmentOccurrence?> GetFirstOccurrenceAsync(long templateId, long teacherId)
    {
        // Tracked: caller (UpdateTemplate) edits DueDate.
        return await _context.AssignmentOccurrences
            .FirstOrDefaultAsync(o => o.TemplateId == templateId
                                   && o.TeacherId == teacherId
                                   && o.OccurrenceNumber == 1);
    }

    /// <inheritdoc />
    public async Task<AssignmentOccurrence?> GetLatestOccurrenceAsync(long templateId, long teacherId)
    {
        return await _context.AssignmentOccurrences
            .Where(o => o.TemplateId == templateId && o.TeacherId == teacherId)
            .OrderByDescending(o => o.OccurrenceNumber)
            .AsNoTracking()
            .FirstOrDefaultAsync();
    }

    // ══════════════════════════════════════════════
    // DELETION-TIME AGGREGATES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<int> CountStudentsWithRecordedDataAsync(long templateId)
    {
        return await _context.StudentAssignmentObligations
            .Where(o => o.Occurrence.TemplateId == templateId
                     && (o.Status != ObligationStatus.Pending || o.IsGradeEntered))
            .Select(o => o.TeacherStudentId)
            .Distinct()
            .CountAsync();
    }

    /// <inheritdoc />
    public async Task<int> CountOccurrencesByTemplateAsync(long templateId)
    {
        return await _context.AssignmentOccurrences
            .CountAsync(o => o.TemplateId == templateId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentObligationAuditLog>> GetAuditLogsForTemplateAsync(
        long templateId)
    {
        return await _context.StudentObligationAuditLogs
            .Where(a => a.StudentObligation.Occurrence.TemplateId == templateId)
            .OrderBy(a => a.ChangedAt)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task DeleteAuditLogsForTemplateAsync(long templateId)
    {
        // Single SQL DELETE — avoids materializing rows. Runs inside the ambient
        // transaction opened by the caller (EF Core 7+ shares the connection's
        // current transaction with ExecuteDeleteAsync).
        await _context.StudentObligationAuditLogs
            .Where(a => a.StudentObligation.Occurrence.TemplateId == templateId)
            .ExecuteDeleteAsync();
    }

    // ══════════════════════════════════════════════
    // CONCURRENCY HELPER
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public void SetTemplateOriginalRowVersion(AssignmentTemplate template, byte[] rowVersion)
    {
        // Set the original RowVersion bytes on the tracked entity so EF generates the
        // correct WHERE [RowVersion] = @original clause on save. If the entity is not
        // tracked (e.g., loaded via AsNoTracking), Attach it first.
        var entry = _context.Entry(template);
        if (entry.State == EntityState.Detached)
        {
            _context.Attach(template);
            entry.State = EntityState.Modified;
        }
        entry.OriginalValues["RowVersion"] = rowVersion;
    }


    /// <inheritdoc />
    public async Task UpdateObligationAsync(StudentAssignmentObligation obligation)
    {
        _context.Entry(obligation).State = EntityState.Modified;
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetObligationOriginalRowVersion(
             StudentAssignmentObligation obligation, byte[] rowVersion)
    {
        var entry = _context.Entry(obligation);
        if (entry.State == EntityState.Detached)
        {
            _context.Attach(obligation);
            entry.State = EntityState.Modified;
        }
        entry.OriginalValues["RowVersion"] = rowVersion;
    }

    public async Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsByIdsAsync(
            long teacherId, long occurrenceId, IEnumerable<long> obligationIds)
    {
        var idList = obligationIds.ToList();
        if (idList.Count == 0) return Array.Empty<StudentAssignmentObligation>();

        // Tracked: callers (BulkUpdateStatusAsync) mutate Status/Grade in place.
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.OccurrenceId == occurrenceId
                     && idList.Contains(o.Id))
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentAssignmentObligation>> GetObligationsByOccurrenceAndStudentsAsync(
        long teacherId, long occurrenceId, IEnumerable<long> teacherStudentIds)
    {
        var ids = teacherStudentIds.Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<StudentAssignmentObligation>();

        // Tracked: the exam-attendance path mutates Status/Grade in place.
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.OccurrenceId == occurrenceId
                     && ids.Contains(o.TeacherStudentId))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<StudentPickerRow>> SearchStudentsInOccurrenceAsync(
            long teacherId, long occurrenceId, string query, int limit)
    {
        string trimmed = ArabicTextNormalizer.Normalize(query?.Trim() ?? string.Empty);
        string pattern = $"%{trimmed}%";

        // Backed by IX_StudentAssignmentObligations_Tracking (TeacherId, OccurrenceId).
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.OccurrenceId == occurrenceId
                     && (string.IsNullOrEmpty(trimmed)
                         || EF.Functions.Like(DbSearch.ArabicNormalize(o.TeacherStudent.StudentName), pattern)
                         || EF.Functions.Like(DbSearch.ArabicNormalize(o.TeacherStudent.StudentCode), pattern)))
            .OrderBy(o => o.TeacherStudent.StudentName)
            .Take(limit)
            .Select(o => new StudentPickerRow
            {
                ObligationId = o.Id,
                TeacherStudentId = o.TeacherStudentId,
                StudentName = o.TeacherStudent.StudentName,
                StudentCode = o.TeacherStudent.StudentCode,
                CurrentStatus = o.Status,
            })
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<(IReadOnlyList<EligibleStudentRow> Items, int TotalCount)>
            GetEligibleStudentsForTemplatePagedAsync(
                long teacherId, long templateId, long? sessionId,
                string? search, int page, int pageSize)
    {
        // ── Step 1: Compute the set of student ids ALREADY in the template's scope ──
        // Union of three buckets: individual scope rows, students in scoped sessions,
        // students in sessions belonging to scoped session-groups. We compute these
        // as IQueryables to keep the work in SQL.

        var individualScopeIds = _context.AssignmentScopes
            .Where(s => s.TemplateId == templateId
                     && s.ScopeType == AssignmentScopeType.IndividualStudent
                     && s.TeacherStudentId.HasValue)
            .Select(s => s.TeacherStudentId!.Value);

        var sessionScopeStudentIds = _context.AssignmentScopes
            .Where(s => s.TemplateId == templateId
                     && s.ScopeType == AssignmentScopeType.Session
                     && s.SessionId.HasValue)
            .SelectMany(s => _context.TeacherStudents
                .Where(ts => ts.SessionId == s.SessionId && !ts.IsDeleted)
                .Select(ts => ts.Id));

        var groupScopeStudentIds = _context.AssignmentScopes
            .Where(s => s.TemplateId == templateId
                     && s.ScopeType == AssignmentScopeType.SessionGroup
                     && s.SessionGroupId.HasValue)
            .SelectMany(s => _context.TeacherStudents
                .Where(ts => !ts.IsDeleted
                          && ts.Session != null
                          && ts.Session.SessionGroupId == s.SessionGroupId)
                .Select(ts => ts.Id));

        // ── Step 2: candidates = teacher's active students MINUS the included set ──
        var includedIds = individualScopeIds
            .Concat(sessionScopeStudentIds)
            .Concat(groupScopeStudentIds);

        var query = _context.TeacherStudents
            .Where(ts => ts.TeacherId == teacherId && !ts.IsDeleted)
            .Where(ts => !includedIds.Contains(ts.Id));

        if (sessionId.HasValue)
            query = query.Where(ts => ts.SessionId == sessionId.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{ArabicTextNormalizer.Normalize(search.Trim())}%";
            query = query.Where(ts =>
                EF.Functions.Like(DbSearch.ArabicNormalize(ts.StudentName), pattern)
             || EF.Functions.Like(DbSearch.ArabicNormalize(ts.StudentCode), pattern));
        }

        int totalCount = await query.CountAsync();

        var items = await query
            .OrderBy(ts => ts.StudentName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ts => new EligibleStudentRow
            {
                TeacherStudentId = ts.Id,
                StudentName = ts.StudentName,
                StudentCode = ts.StudentCode,
                SessionName = ts.Session != null ? ts.Session.SessionName : null,
            })
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<IReadOnlyList<StudentAssignmentObligation>> GetFutureObligationsForStudentAsync(
            long teacherId, long templateId, long teacherStudentId, DateTime asOfDate)
    {
        DateTime cutoff = asOfDate.Date;
        // Tracked: caller deletes these rows.
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.TeacherStudentId == teacherStudentId
                     && o.Occurrence.TemplateId == templateId
                     && o.Occurrence.DueDate >= cutoff)
            .Include(o => o.Occurrence)
            .ToListAsync();
    }

    /// <inheritdoc />
  public  async Task<int> CountStudentObligationsWithDataAsync(
        long teacherId, long templateId, long teacherStudentId)
    {
        return await _context.StudentAssignmentObligations
            .Where(o => o.TeacherId == teacherId
                     && o.TeacherStudentId == teacherStudentId
                     && o.Occurrence.TemplateId == templateId
                     && (o.Status != ObligationStatus.Pending || o.IsGradeEntered))
            .CountAsync();
    }

    /// <inheritdoc />
    public async Task<AssignmentScope?> GetScopeByIdAndTeacherAsync(long scopeId, long teacherId)
    {
        return await _context.AssignmentScopes
            .FirstOrDefaultAsync(s => s.Id == scopeId && s.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task DeleteScopeAsync(AssignmentScope scope)
    {
        _context.AssignmentScopes.Remove(scope);
        await Task.CompletedTask;
    }

    public /// <inheritdoc />
        async Task<IReadOnlyList<long>> GetTemplateIdsDueForMaterializationAsync(
            DateTime fromDate, DateTime toDate)
    {
        // Two-pass query for clarity over a single mega-query:
        //
        // Pass 1: latest occurrence DueDate per template, restricted to recurring
        // and non-stopped templates. Single grouped read; backed by IX_Occurrences_Template.
        var latestPerTemplate = await _context.AssignmentTemplates
            .Where(t => t.IsRecurring && !t.IsRecurrenceStopped)
            .Select(t => new
            {
                t.Id,
                t.RecurrencePattern,
                t.RecurrenceEndDate,
                LastDueDate = _context.AssignmentOccurrences
                    .Where(o => o.TemplateId == t.Id)
                    .Max(o => (DateTime?)o.DueDate),
            })
            .Where(x => x.LastDueDate != null)
            .AsNoTracking()
            .ToListAsync();

        // Pass 2: in-memory filter applying the per-pattern interval. Cheap because
        // pass-1 already narrowed to recurring+active templates. Doing this in SQL
        // would require a CASE expression and a CASE-aware index — not worth it.
        DateTime cutoff = toDate.Date;

        var due = latestPerTemplate
            .Where(x => x.LastDueDate.HasValue)
            .Where(x =>
            {
                DateTime? next = ComputeApproximateNextDate(x.RecurrencePattern, x.LastDueDate!.Value);
                if (next is null) return false;
                if (x.RecurrenceEndDate.HasValue && next.Value > x.RecurrenceEndDate.Value.Date)
                    return false;
                return next.Value <= cutoff;
            })
            .Select(x => x.Id)
            .ToList();

        return due;
    }

    public  /// <inheritdoc />
        async Task<AssignmentTemplate?> LockTemplateForMaterializationAsync(long templateId)
    {
        // SQL Server table hint UPDLOCK + ROWLOCK serializes concurrent writers on
        // this row without escalating to a page or table lock. HOLDLOCK extends the
        // lock to the end of the transaction. The hint is supplied via FromSql so
        // EF Core does not strip it from the generated query.
        //
        // The query is parameterized (FormattableString interpolation = SqlParameter)
        // so SQL injection is impossible.
        var template = await _context.AssignmentTemplates
            .FromSql($@"
                    SELECT * FROM [AssignmentTemplates] WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
                    WHERE [Id] = {templateId}")
            .FirstOrDefaultAsync();

        return template;
    }
    // ──────────────────────────────────────────────────────────────────
    // Local helper — keep in sync with the materializer service's
    // ComputeNextDueDate. Duplicated intentionally so the eligibility
    // query stays self-contained and the service can refine its logic
    // (e.g., real session-schedule lookups) without changing the repo.
    // ──────────────────────────────────────────────────────────────────
    static DateTime? ComputeApproximateNextDate(RecurrencePattern pattern, DateTime lastDueDate)
        => pattern switch
        {
            RecurrencePattern.OneTime => null,
            RecurrencePattern.EverySession => lastDueDate.Date.AddDays(7),
            RecurrencePattern.EveryTwoSessions => lastDueDate.Date.AddDays(14),
            RecurrencePattern.Monthly => lastDueDate.Date.AddMonths(1),
            _ => null,
        };

}
