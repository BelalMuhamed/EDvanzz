using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// Extended repository for the Online Exam Module — teacher-side aggregate.
/// Inherits <see cref="GenericRepo{OnlineExam, long}"/> for basic CRUD; all other
/// entities (Questions, Options, Scopes) accessed via <c>_context</c> through named
/// methods — same pattern as <c>ExamHomeworkRepo</c>/<c>VideoAssetRepo</c>.
/// </summary>
public class OnlineExamRepo : GenericRepo<OnlineExam, long>, IOnlineExamRepo
{
    public OnlineExamRepo(EdvanzDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<OnlineExam?> GetByIdAndTeacherAsync(long onlineExamId, long teacherId)
    {
        return await _context.OnlineExams
            .FirstOrDefaultAsync(e => e.Id == onlineExamId && e.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<int> CountByTeacherAsync(long teacherId)
    {
        // OnlineExam is hard-delete, so live rows == the real count (free-tier quota input).
        return await _context.OnlineExams.CountAsync(e => e.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task AddQuestionsRangeAsync(IEnumerable<OnlineExamQuestion> questions)
    {
        await _context.OnlineExamQuestions.AddRangeAsync(questions);
    }

    /// <inheritdoc />
    public async Task AddScopesRangeAsync(IEnumerable<OnlineExamScope> scopes)
    {
        await _context.OnlineExamScopes.AddRangeAsync(scopes);
    }

    /// <inheritdoc />
    public async Task DeleteAllScopesForExamAsync(long onlineExamId, long teacherId)
    {
        await _context.OnlineExamScopes
            .Where(s => s.OnlineExamId == onlineExamId && s.TeacherId == teacherId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task PurgeExamGraphAsync(long onlineExamId)
    {
        // Leaf-first set-based deletes (every child FK is NoAction — nothing cascades
        // on its own). Order per §3.7. Report-graph rows purged first via
        // IStudentOnlineExamReportRepo.PurgeReportsForExamAsync — called by the
        // service BEFORE this method, same two-repo split as ExamHomeworkRepo's
        // obligation-then-template teardown.
        await _context.OnlineExamQuestionOptions
            .IgnoreQueryFilters()
            .Where(o => o.Question.OnlineExamId == onlineExamId)
            .ExecuteDeleteAsync();

        await _context.OnlineExamQuestions
            .IgnoreQueryFilters()
            .Where(q => q.OnlineExamId == onlineExamId)
            .ExecuteDeleteAsync();

        await _context.OnlineExamScopes
            .IgnoreQueryFilters()
            .Where(s => s.OnlineExamId == onlineExamId)
            .ExecuteDeleteAsync();

        await _context.OnlineExams
            .IgnoreQueryFilters()
            .Where(e => e.Id == onlineExamId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task DeleteScopesBySessionAsync(long sessionId)
    {
        await _context.OnlineExamScopes
            .Where(s => s.SessionId == sessionId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task DeleteScopesByGroupAsync(long sessionGroupId)
    {
        await _context.OnlineExamScopes
            .Where(s => s.SessionGroupId == sessionGroupId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public IQueryable<long> BuildAssignedStudentIdsQuery(long onlineExamId, long teacherId)
    {
        var sessionBranch = _context.OnlineExamScopes
            .Where(s => s.OnlineExamId == onlineExamId
                     && s.TeacherId == teacherId
                     && s.SessionId != null)
            .Join(_context.TeacherStudents,
                s => s.SessionId,
                ts => ts.SessionId,
                (s, ts) => ts.Id)
            .Where(id => id != 0); // placeholder no-op to keep both branches same shape for Union

        var groupBranch = _context.OnlineExamScopes
            .Where(s => s.OnlineExamId == onlineExamId
                     && s.TeacherId == teacherId
                     && s.SessionGroupId != null)
            .Join(_context.Sessions,
                s => s.SessionGroupId,
                sess => sess.SessionGroupId,
                (s, sess) => sess.Id)
            .Join(_context.TeacherStudents,
                sessionId => sessionId,
                ts => ts.SessionId,
                (sessionId, ts) => ts.Id);

        // Composable — caller applies Distinct()/Count()/Contains()/Except() on top.
        // Not materialized here (BR-VCM-01 — re-resolved live on every access).
        return sessionBranch.Union(groupBranch).Distinct();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OnlineExamScope>> GetScopesByExamIdsAsync(IEnumerable<long> onlineExamIds)
    {
        return await _context.OnlineExamScopes
            .Where(s => onlineExamIds.Contains(s.OnlineExamId))
            .Include(s => s.Session)
            .Include(s => s.SessionGroup)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OnlineExamQuestionRow>> GetQuestionsForTeacherAsync(long onlineExamId)
    {
        return await _context.OnlineExamQuestions
            .Where(q => q.OnlineExamId == onlineExamId)
            .OrderBy(q => q.SortOrder)
            .Select(q => new OnlineExamQuestionRow
            {
                Id = q.Id,
                QuestionText = q.QuestionText,
                QuestionType = q.QuestionType,
                Degree = q.Degree,
                SortOrder = q.SortOrder,
                ImageFileInternalId = q.ImageFileId,
                Options = q.Options
                    .OrderBy(o => o.SortOrder)
                    .Select(o => new OnlineExamQuestionOptionRow
                    {
                        Id = o.Id,
                        OptionText = o.OptionText,
                        IsCorrect = o.IsCorrect,
                        SortOrder = o.SortOrder
                    })
                    .ToList()
            })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentOnlineExamQuestionRow>> GetQuestionsForStudentAsync(long onlineExamId)
    {
        // IsCorrect never touches this query — the projected type has no such field.
        return await _context.OnlineExamQuestions
            .Where(q => q.OnlineExamId == onlineExamId)
            .OrderBy(q => q.SortOrder)
            .Select(q => new StudentOnlineExamQuestionRow
            {
                Id = q.Id,
                QuestionText = q.QuestionText,
                QuestionType = q.QuestionType,
                Degree = q.Degree,
                SortOrder = q.SortOrder,
                ImageFileInternalId = q.ImageFileId,
                Options = q.Options
                    .OrderBy(o => o.SortOrder)
                    .Select(o => new StudentOnlineExamQuestionOptionRow
                    {
                        Id = o.Id,
                        OptionText = o.OptionText,
                        SortOrder = o.SortOrder
                    })
                    .ToList()
            })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<decimal> GetTotalDegreeAsync(long onlineExamId)
    {
        return await _context.OnlineExamQuestions
            .Where(q => q.OnlineExamId == onlineExamId)
            .SumAsync(q => (decimal?)q.Degree) ?? 0m;
    }
    /// <inheritdoc />
    public async Task<bool> IsScopeTargetOwnedByTeacherAsync(long teacherId, OnlineExamScopeType scopeType, long targetId)
    {
        return scopeType switch
        {
            OnlineExamScopeType.Session => await _context.Sessions
                .AnyAsync(s => s.Id == targetId && s.TeacherId == teacherId),
            OnlineExamScopeType.SessionGroup => await _context.SessionGroups
                .AnyAsync(g => g.Id == targetId && g.TeacherId == teacherId),
            _ => false,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetQuestionImageFileIdsAsync(long onlineExamId)
    {
        return await _context.OnlineExamQuestions
            .Where(q => q.OnlineExamId == onlineExamId && q.ImageFileId != null)
            .Select(q => q.ImageFileId!.Value)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<bool> IsQuestionImageAssignedToStudentAsync(
        long fileObjectId, long teacherId, long teacherStudentId)
    {
        // The image belongs to exactly one question → one exam (single-valued attach).
        long? examId = await _context.OnlineExamQuestions
            .Where(q => q.ImageFileId == fileObjectId && q.OnlineExam.TeacherId == teacherId)
            .Select(q => (long?)q.OnlineExamId)
            .FirstOrDefaultAsync();

        if (examId is null)
            return false;

        // Live assigned-set membership — composed, never materialized (BR-VCM-01).
        return await BuildAssignedStudentIdsQuery(examId.Value, teacherId)
            .AnyAsync(id => id == teacherStudentId);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<OnlineExam> Items, int TotalCount)> GetPagedForTeacherAsync(
        long teacherId, OnlineExamStatus? status, string? search, int page, int pageSize)
    {
        var query = _context.OnlineExams.Where(e => e.TeacherId == teacherId);

        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var titleTerm = ArabicTextNormalizer.Normalize(search.Trim());
            query = query.Where(e => EF.Functions.Like(DbSearch.ArabicNormalize(e.Title), $"%{titleTerm}%"));
        }

        int totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(e => e.CreateAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, int>> GetAssignedCountsByExamIdsAsync(
        IEnumerable<long> onlineExamIds, long teacherId)
    {
        var examIds = onlineExamIds.ToList();

        var sessionBranch = _context.OnlineExamScopes
            .Where(s => examIds.Contains(s.OnlineExamId) && s.TeacherId == teacherId && s.SessionId != null)
            .Join(_context.TeacherStudents, s => s.SessionId, ts => ts.SessionId,
                (s, ts) => new { s.OnlineExamId, StudentId = ts.Id });

        var groupBranch = _context.OnlineExamScopes
            .Where(s => examIds.Contains(s.OnlineExamId) && s.TeacherId == teacherId && s.SessionGroupId != null)
            .Join(_context.Sessions, s => s.SessionGroupId, sess => sess.SessionGroupId,
                (s, sess) => new { s.OnlineExamId, sess.Id })
            .Join(_context.TeacherStudents, x => x.Id, ts => ts.SessionId,
                (x, ts) => new { x.OnlineExamId, StudentId = ts.Id });

        var grouped = await sessionBranch.Union(groupBranch)
            .GroupBy(x => x.OnlineExamId)
            .Select(g => new { OnlineExamId = g.Key, Count = g.Select(x => x.StudentId).Distinct().Count() })
            .ToListAsync();

        return grouped.ToDictionary(x => x.OnlineExamId, x => x.Count);
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, int>> GetQuestionCountsByExamIdsAsync(IEnumerable<long> onlineExamIds)
    {
        var examIds = onlineExamIds.ToList();
        if (examIds.Count == 0)
            return new Dictionary<long, int>();

        var grouped = await _context.OnlineExamQuestions
            .Where(q => examIds.Contains(q.OnlineExamId))
            .GroupBy(q => q.OnlineExamId)
            .Select(g => new { OnlineExamId = g.Key, Count = g.Count() })
            .ToListAsync();

        return grouped.ToDictionary(x => x.OnlineExamId, x => x.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignedStudentRow>> GetAssignedStudentsAsync(long onlineExamId, long teacherId)
    {
        var sessionBranch = _context.OnlineExamScopes
            .Where(s => s.OnlineExamId == onlineExamId && s.TeacherId == teacherId && s.SessionId != null)
            .Join(_context.TeacherStudents, s => s.SessionId, ts => ts.SessionId,
                (s, ts) => new AssignedStudentRow
                {
                    TeacherStudentId = ts.Id,
                    StudentName = ts.StudentName ?? string.Empty,
                    StudentCode = ts.StudentCode,
                    SessionId = ts.SessionId,          // the scoped session
                    SessionGroupId = null,             // session scope: group N/A
                });

        var groupBranch = _context.OnlineExamScopes
            .Where(s => s.OnlineExamId == onlineExamId && s.TeacherId == teacherId && s.SessionGroupId != null)
            .Join(_context.Sessions, s => s.SessionGroupId, sess => sess.SessionGroupId,
                (s, sess) => new { s.SessionGroupId, SessionId = sess.Id })
            .Join(_context.TeacherStudents, x => x.SessionId, ts => ts.SessionId,
                (x, ts) => new AssignedStudentRow
                {
                    TeacherStudentId = ts.Id,
                    StudentName = ts.StudentName ?? string.Empty,
                    StudentCode = ts.StudentCode,
                    SessionId = ts.SessionId,          // the student's session
                    SessionGroupId = x.SessionGroupId, // the scoped group
                });

        var rows = await sessionBranch.Union(groupBranch).AsNoTracking().ToListAsync();
        return rows.GroupBy(r => r.TeacherStudentId).Select(g => g.First()).ToList();
    }

    /// <inheritdoc />
    public async Task ReplaceQuestionsAsync(long onlineExamId, IEnumerable<OnlineExamQuestion> newQuestions)
    {
        // Leaf-first: options before questions (NoAction FKs, nothing cascades).
        await _context.OnlineExamQuestionOptions
            .IgnoreQueryFilters()
            .Where(o => o.Question.OnlineExamId == onlineExamId)
            .ExecuteDeleteAsync();

        await _context.OnlineExamQuestions
            .IgnoreQueryFilters()
            .Where(q => q.OnlineExamId == onlineExamId)
            .ExecuteDeleteAsync();

        // Associate every replacement question with the target exam BEFORE insert.
        // BuildQuestionEntities leaves OnlineExamId unset, so without this the new
        // rows insert with OnlineExamId = 0 → FK violation → DbUpdateException →
        // HTTP 409 "DatabaseConflict" (the add path sets this in the service; the
        // replace path must set it here since this method owns the exam association).
        var questions = newQuestions.ToList();
        foreach (var q in questions)
            q.OnlineExamId = onlineExamId;

        await _context.OnlineExamQuestions.AddRangeAsync(questions);
    }
    /// <inheritdoc />
    public async Task<Dictionary<long, int>> GetAssignedCountsPerScopeAsync(long onlineExamId, long teacherId)
    {
        var scopes = await _context.OnlineExamScopes
            .Where(s => s.OnlineExamId == onlineExamId && s.TeacherId == teacherId)
            .Select(s => new { s.Id, s.ScopeType, s.SessionId, s.SessionGroupId })
            .AsNoTracking()
            .ToListAsync();

        var result = new Dictionary<long, int>();

        // ── Session-scoped rows: one grouped query for all of them ──
        var sessionScopes = scopes.Where(s => s.ScopeType == OnlineExamScopeType.Session && s.SessionId.HasValue).ToList();
        if (sessionScopes.Count > 0)
        {
            var sessionIds = sessionScopes.Select(s => s.SessionId!.Value).Distinct().ToList();

            var countsBySession = await _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && !ts.IsDeleted
                          && ts.SessionId.HasValue && sessionIds.Contains(ts.SessionId.Value))
                .GroupBy(ts => ts.SessionId!.Value)
                .Select(g => new { SessionId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.SessionId, g => g.Count);

            foreach (var s in sessionScopes)
                result[s.Id] = countsBySession.TryGetValue(s.SessionId!.Value, out var c) ? c : 0;
        }

        // ── Group-scoped rows: one grouped query for all of them ──
        var groupScopes = scopes.Where(s => s.ScopeType == OnlineExamScopeType.SessionGroup && s.SessionGroupId.HasValue).ToList();
        if (groupScopes.Count > 0)
        {
            var groupIds = groupScopes.Select(s => s.SessionGroupId!.Value).Distinct().ToList();

            var countsByGroup = await _context.Sessions
                .Where(sess => sess.TeacherId == teacherId && sess.SessionGroupId.HasValue && groupIds.Contains(sess.SessionGroupId.Value))
                .Join(_context.TeacherStudents.Where(ts => ts.TeacherId == teacherId && !ts.IsDeleted),
                    sess => sess.Id, ts => ts.SessionId,
                    (sess, ts) => new { sess.SessionGroupId, ts.Id })
                .GroupBy(x => x.SessionGroupId!.Value)
                .Select(g => new { SessionGroupId = g.Key, Count = g.Select(x => x.Id).Distinct().Count() })
                .ToDictionaryAsync(g => g.SessionGroupId, g => g.Count);

            foreach (var s in groupScopes)
                result[s.Id] = countsByGroup.TryGetValue(s.SessionGroupId!.Value, out var c) ? c : 0;
        }

        return result;
    }
    /// <inheritdoc />
    public async Task<(long? SessionId, long? SessionGroupId)> GetStudentSessionContextAsync(long teacherStudentId)
    {
        var sessionId = await _context.TeacherStudents
            .Where(ts => ts.Id == teacherStudentId)
            .Select(ts => ts.SessionId)
            .FirstOrDefaultAsync();

        if (sessionId is null) return (null, null);

        var groupId = await _context.Sessions
            .Where(s => s.Id == sessionId.Value)
            .Select(s => (long?)s.SessionGroupId)
            .FirstOrDefaultAsync();

        return (sessionId, groupId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OnlineExam>> GetExamsAssignedToStudentAsync(
        long teacherId, long? studentSessionId, long? studentSessionGroupId)
    {
        var examIds = await _context.OnlineExamScopes
            .Where(s => s.TeacherId == teacherId &&
                ((s.ScopeType == OnlineExamScopeType.Session && s.SessionId == studentSessionId)
              || (s.ScopeType == OnlineExamScopeType.SessionGroup && studentSessionGroupId != null && s.SessionGroupId == studentSessionGroupId)))
            .Select(s => s.OnlineExamId)
            .Distinct()
            .ToListAsync();

        if (examIds.Count == 0) return Array.Empty<OnlineExam>();

        // Draft exams are never assignment-visible to students, even if scoped — the
        // scope row can predate publish.
        return await _context.OnlineExams
            .Where(e => examIds.Contains(e.Id) && e.Status != OnlineExamStatus.Draft)
            .Include(e => e.Questions)
            .AsNoTracking()
            .ToListAsync();
    }
    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<(string StudentName, string? StudentCode)?> GetOwnedStudentIdentityAsync(
        long teacherStudentId, long teacherId)
    {
        var row = await _context.TeacherStudents
            .Where(ts => ts.Id == teacherStudentId && ts.TeacherId == teacherId)
            .Select(ts => new { ts.StudentName, ts.StudentCode })
            .AsNoTracking()
            .FirstOrDefaultAsync();

        // Null means "not this teacher's student" — the caller treats that as the
        // ownership failure, so no separate existence check is needed.
        return row is null ? null : (row.StudentName, row.StudentCode);
    }

    public async Task<bool> IsTeacherStudentOwnedByTeacherAsync(long teacherStudentId, long teacherId)
    {
        return await _context.TeacherStudents
            .AnyAsync(ts => ts.Id == teacherStudentId && ts.TeacherId == teacherId && !ts.IsDeleted);
    }

    /// <inheritdoc />
    public async Task<TeacherSubjectNameRow?> GetTeacherSubjectNameAsync(long teacherId)
    {
        // One round trip: the teacher's custom subject + the first linked ministry subject's
        // bilingual names (correlated subquery, ordered for determinism). The service picks the
        // language (canonical StudentUserService.cs:420-429 subject-name pattern).
        return await _context.Teachers
            .Where(t => t.Id == teacherId)
            .Select(t => new TeacherSubjectNameRow
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
    public async Task<(IReadOnlyList<OnlineExamQuestionStatRow> Stats,
                       IReadOnlyList<OnlineExamWrongOptionRow> WrongOptions,
                       int FinalizedReportCount)>
        GetQuestionAnalysisAsync(long onlineExamId)
    {
        // Finalized attempts only — a report still open can change its answers,
        // and a difficulty figure that shifts mid-exam misleads the teacher.
        var finalizedReportIds = _context.StudentOnlineExamReports
            .Where(r => r.OnlineExamId == onlineExamId && r.SubmittedAt != null)
            .Select(r => r.Id);

        int finalizedCount = await finalizedReportIds.CountAsync();
        if (finalizedCount == 0)
        {
            return (Array.Empty<OnlineExamQuestionStatRow>(),
                    Array.Empty<OnlineExamWrongOptionRow>(),
                    0);
        }

        // An answer row with no selected option is a question the student
        // opened but left blank — not an attempt at it.
        var answered = _context.StudentQuestionAnswers
            .Where(a => finalizedReportIds.Contains(a.StudentReportId)
                     && a.Question.OnlineExamId == onlineExamId
                     && a.SelectedOptions.Any());

        var stats = await answered
            .GroupBy(a => a.QuestionId)
            .Select(g => new OnlineExamQuestionStatRow
            {
                QuestionId = g.Key,
                AttemptedCount = g.Count(),
                // Full marks = correct. Multiple-choice awards partial credit,
                // and a half-right answer is not a right one.
                CorrectCount = g.Count(a => a.AwardedDegree >= a.Question.Degree),
            })
            .AsNoTracking()
            .ToListAsync();

        // Joined, not a correlated EXISTS: `answered` is itself a filtered query,
        // and an EXISTS would re-evaluate it once per selected-option row.
        var wrongOptions = await (
            from o in _context.StudentQuestionAnswerOptions
            join a in answered on o.StudentQuestionAnswerId equals a.Id
            where !o.QuestionOption.IsCorrect
            select new { a.QuestionId, o.QuestionOptionId, o.QuestionOption.OptionText })
            .GroupBy(x => new
            {
                x.QuestionId,
                OptionId = x.QuestionOptionId,
                x.OptionText,
            })
            .Select(g => new OnlineExamWrongOptionRow
            {
                QuestionId = g.Key.QuestionId,
                OptionId = g.Key.OptionId,
                OptionText = g.Key.OptionText,
                PickedCount = g.Count(),
            })
            .AsNoTracking()
            .ToListAsync();

        return (stats, wrongOptions, finalizedCount);
    }
}
