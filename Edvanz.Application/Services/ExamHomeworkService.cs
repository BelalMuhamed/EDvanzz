using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.ExamHomework;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Net;
using System.Text.Json;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements the Application contract for the Exams &amp; Homework Module (Module 6).
///
/// LAYER POSITION: Application. Depends on <see cref="IUnitOfWork"/> for persistence
/// and <see cref="IStringLocalizer{Messages}"/> for bilingual messages. Zero direct
/// dependency on EF Core or any Infrastructure type.
///
/// SCOPE OF THIS FILE:
/// Phase 2 — template lifecycle (create, list, get, update, delete, stop-recurrence,
/// list-occurrences). Phase 3 methods are stubbed and will be filled in next.
///
/// DESIGN DECISIONS REFLECTED HERE:
/// - REQ-EXH-NFR-004: every read &amp; write is keyed by <c>teacherId</c> (resolved from
///   JWT in the controller, never from the body or route).
/// - REQ-EXH-007: first occurrence is materialized synchronously inside the create
///   transaction so the obligation list is visible immediately.
/// - REQ-EXH-013: recurrence-pattern edits are locked once any obligation is non-Pending
///   (returns 422 — see review note: this is a business rule violation, not a 409 race).
/// - REQ-EXH-034 + snapshots: edits to grading config NEVER overwrite snapshots on
///   already-materialized occurrences. Historical reproducibility wins over recompute.
/// - BR-EXH-002: stopping a recurrence preserves all previously generated occurrences.
/// - REQ-EXH-037: hard delete is final, but a JSON snapshot survives in
///   <c>AssignmentDeletionLogs</c>; audit-log rows are archived into the snapshot then
///   bulk-deleted (their FK to obligations is Restrict, so NoAction would otherwise fail).
///
/// ARCHITECTURAL RULE FOLLOWED:
/// Every database operation goes through a NAMED method on a repo. No raw expression
/// predicates and no <c>GetQueryable()</c> calls in this file. Same rule the codebase
/// already enforces for PaymentService, AttendanceService, etc.
/// </summary>
public class ExamHomeworkService : IExamHomeworkService
{
    // ══════════════════════════════════════════════════════════════════════
    // CONSTANTS — extracted to remove magic numbers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cap on per-template student resolution at create time. Above this, the
    /// synchronous first-occurrence materialization risks blowing the transaction
    /// budget. REQ-EXH-007 promises immediate visibility — that contract stops here.
    /// Larger scopes must use Hangfire materialization (out of scope for v1).
    /// </summary>
    private const int MaxStudentsPerOccurrence = 5_000;

    /// <summary>
    /// Cap on per-student detail in deletion-log JSON snapshots. Above this we
    /// store a summary instead of the full per-student list, per
    /// <see cref="AssignmentDeletionLog.TemplateSnapshotJson"/> documentation.
    /// </summary>
    private const int DeletionSnapshotPerStudentCap = 10_000;

    /// <summary>
    /// Cap on the number of students that can be added in a single AddStudentsToTemplate
    /// call (REQ-EXH-035). Above this, the client should split the request.
    /// </summary>
    private const int MaxStudentsPerAddCall = 500;

    /// <summary>
    /// Cap on bulk multi-select status updates (REQ-EXH-025). Beyond this size, the
    /// transaction time grows past the 2-second NFR target.
    /// </summary>
    private const int MaxBulkUpdateItems = 1000;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly IAssignmentScopeResolver _scopeResolver;
    private readonly IExamHomeworkNotifier _examHomeworkNotifier;    // ← Phase 5
    private readonly ISubscriptionGateService _subscriptionGate;
    private readonly ITimeZoneService _timeZoneService;

    public ExamHomeworkService(
        IUnitOfWork unitOfWork,
        IStringLocalizer<Messages> localizer,
        IAssignmentScopeResolver scopeResolver,
        IExamHomeworkNotifier examHomeworkNotifier,                  // ← Phase 5
        ISubscriptionGateService subscriptionGate,
        ITimeZoneService timeZoneService)
    {
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _scopeResolver = scopeResolver;
        _examHomeworkNotifier = examHomeworkNotifier;                // ← Phase 5
        _subscriptionGate = subscriptionGate;
        _timeZoneService = timeZoneService;
    }

    // ══════════════════════════════════════════════════════════════════════
    // TEMPLATE LIFECYCLE
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<AssignmentTemplateDto>> CreateTemplateAsync(
        long teacherId, long actingUserId, CreateAssignmentTemplateDto dto)
    {
        // ── 0. Free-tier quota, keyed by assignment type (see ModuleQuota table) ──
        // An Exam-type template counts against the Exams quota (same pool as /api/exams —
        // this route used to bypass the exam cap via the all-types template count);
        // Homework counts against AssignmentTemplates.
        bool isExamTemplate = dto.AssignmentType == AssignmentType.Exam;
        if (!await _subscriptionGate.CanCreateAsync(
                teacherId,
                isExamTemplate
                    ? Domain.Constants.ModuleQuotaKeys.Exams
                    : Domain.Constants.ModuleQuotaKeys.AssignmentTemplates,
                isExamTemplate
                    ? () => _unitOfWork.ExamHomeworkRepo.CountExamTemplatesByTeacherAsync(teacherId)
                    : () => _unitOfWork.ExamHomeworkRepo.CountHomeworkTemplatesByTeacherAsync(teacherId)))
            return Result<AssignmentTemplateDto>.Failure(
                _localizer, Domain.Constants.SubscriptionConstants.Messages.SubscriptionRequired,
                System.Net.HttpStatusCode.Forbidden);

        // ── 1. Input validation (in-memory rules) ──
        var inputValidation = ValidateCreateInput(
            dto, _timeZoneService.GetTeacherLocalDate(teacherId));
        if (!inputValidation.IsSuccess) return inputValidation;

        // ── 2. Scope ownership validation (DB-bound) ──
        var ownership = await ValidateScopeOwnershipAsync(teacherId, dto.Scopes);
        if (!ownership.IsSuccess)
            return Result<AssignmentTemplateDto>.Failure(
                _localizer, ownership.MessageKey!, ownership.StatusCode);

        // ── 3. Resolve & dedupe target students BEFORE the transaction ──
        // Read-only; minimizes the write-lock window. Reuses PaymentsRepo helpers
        // (single source of truth for "who's in a session").
        var resolvedStudentIds = await ResolveAndDedupeStudentsAsync(teacherId, dto.Scopes);
        if (resolvedStudentIds.Count == 0)
            return Result<AssignmentTemplateDto>.Failure(
                _localizer, "ScopeResolvedToZeroStudents", HttpStatusCode.BadRequest);

        if (resolvedStudentIds.Count > MaxStudentsPerOccurrence)
            return Result<AssignmentTemplateDto>.Failure(
                _localizer, "ScopeResolutionTooLarge", HttpStatusCode.BadRequest);

        // ── 4. Build the entity graph: template → scopes → first occurrence → obligations ──
        DateTime utcNow = DateTime.UtcNow;
        DateTime assignmentDate = dto.AssignmentDate.Date;

        var template = BuildTemplateFromDto(dto, teacherId, actingUserId, utcNow);
        var scopeEntities = BuildScopesFromDto(dto.Scopes, template, teacherId, utcNow);
        var firstOccurrence = BuildFirstOccurrence(template, teacherId, assignmentDate, utcNow);
        var obligations = BuildObligations(firstOccurrence, teacherId, resolvedStudentIds, utcNow);

        // ── 5. Persist within a transaction ──
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            await _unitOfWork.ExamHomeworkRepo.AddTemplateAsync(template);
            await _unitOfWork.ExamHomeworkRepo.AddScopesRangeAsync(scopeEntities);
            await _unitOfWork.ExamHomeworkRepo.AddOccurrenceAsync(firstOccurrence);
            await _unitOfWork.ExamHomeworkRepo.AddObligationsRangeAsync(obligations);

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
        }
        catch (DbUpdateException)
        {
            await _unitOfWork.RollbackAsync();
            // The unique index UX_StudentAssignmentObligations_Occurrence_Student is the
            // only realistic violator; our own dedupe should prevent it.
            return Result<AssignmentTemplateDto>.Failure(
                _localizer, "DatabaseConflict", HttpStatusCode.Conflict);
        }

        // ── 6. Build response ──
        var hydrated = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateWithScopesAsync(template.Id, teacherId);

        var responseDto = MapTemplateToDto(hydrated!);
        responseDto.FirstOccurrenceId = firstOccurrence.Id;
        responseDto.StudentsAssigned = obligations.Count;

        return Result<AssignmentTemplateDto>.Success(
            responseDto, _localizer, "AssignmentCreated", HttpStatusCode.Created);
    }

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<AssignmentOverviewItemDto>>>> GetOverviewAsync(
        long teacherId, AssignmentOverviewRequest request)
    {
        // ── 1. Page query — all filters and pagination handled by the repo. ──
        // The repo method follows the same (Items, TotalCount) pattern as every other
        // paged method in IExamHomeworkRepo (GetTrackingViewPagedAsync etc.). The service
        // does not compose any LINQ — it consumes a materialized result.
        var (pageEntities, totalCount) = await _unitOfWork.ExamHomeworkRepo
            .GetAssignmentOverviewPagedAsync(
                teacherId,
                request.Search,
                request.AssignmentType,
                request.RecurrencePattern,
                request.IsRecurring,
                request.Page,
                request.PageSize);

        // ── 2. Aggregate side-loads (O(N+constant) — no per-row queries). ──
        var templateIds = pageEntities.Select(t => t.Id).ToList();

        var latestOccurrenceIds = await _unitOfWork.ExamHomeworkRepo
            .GetLatestOccurrenceIdsByTemplateAsync(templateIds);

        var occurrenceSummaries = await _unitOfWork.ExamHomeworkRepo
            .GetCompletionSummariesByOccurrenceIdsAsync(latestOccurrenceIds.Values);

        var nextOrLastDates = await _unitOfWork.ExamHomeworkRepo
            .GetNextOrLastOccurrenceDatesAsync(templateIds, DateTime.UtcNow);

        var scopeCounts = await _unitOfWork.ExamHomeworkRepo
            .GetScopeCountsByTemplateIdsAsync(templateIds);

        // ── 3. Map ──
        var items = pageEntities.Select(t => new AssignmentOverviewItemDto
        {
            Id = t.Id,
            AssignmentType = t.AssignmentType,
            Name = t.Name,
            NameAr = t.NameAr,
            IsRecurring = t.IsRecurring,
            RecurrencePattern = t.RecurrencePattern,
            IsRecurrenceStopped = t.IsRecurrenceStopped,
            NextOrLastOccurrenceDate = nextOrLastDates.GetValueOrDefault(t.Id),
            ScopeSummary = BuildScopeSummary(scopeCounts.GetValueOrDefault(t.Id)),
            CompletionSummary = MapToCompletionSummaryDto(
                latestOccurrenceIds.TryGetValue(t.Id, out var occId)
                    ? occurrenceSummaries.GetValueOrDefault(occId)
                    : null),
            CreatedAt = t.CreateAt,
        }).ToList();

        var response = new PaginatedResponse<List<AssignmentOverviewItemDto>>
        {
            data = items,
            page = request.Page,
            pageSize = request.PageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
        };
        return Result<PaginatedResponse<List<AssignmentOverviewItemDto>>>.Success(
            response, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<AssignmentTemplateDto>> GetTemplateByIdAsync(
        long teacherId, long templateId)
    {
        var template = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateWithScopesAsync(templateId, teacherId);

        if (template is null)
            return Result<AssignmentTemplateDto>.Failure(
                _localizer, "TemplateNotFound", HttpStatusCode.NotFound);

        return Result<AssignmentTemplateDto>.Success(
            MapTemplateToDto(template), _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<AssignmentTemplateDto>> UpdateTemplateAsync(
        long teacherId, long actingUserId, long templateId, UpdateAssignmentTemplateDto dto)
    {
        var template = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateByIdAndTeacherAsync(templateId, teacherId);

        if (template is null)
            return Result<AssignmentTemplateDto>.Failure(
                _localizer, "TemplateNotFound", HttpStatusCode.NotFound);

        var inputValidation = ValidateUpdateInput(
            template, dto, _timeZoneService.GetTeacherLocalDate(teacherId));
        if (!inputValidation.IsSuccess) return inputValidation;

        // REQ-EXH-013 lock — only when pattern is actually changing.
        bool patternChanging = dto.RecurrencePattern.HasValue
                            && dto.RecurrencePattern.Value != template.RecurrencePattern;
        if (patternChanging)
        {
            bool canEdit = await _unitOfWork.ExamHomeworkRepo
                .CanEditRecurrencePatternAsync(templateId);
            if (!canEdit)
                return Result<AssignmentTemplateDto>.Failure(
                    _localizer, "RecurrencePatternLocked", HttpStatusCode.UnprocessableEntity);
        }

        ApplyTemplateEdits(template, dto);
        template.UpdatedAt = DateTime.UtcNow;

        // Edit the only existing occurrence's DueDate when a non-recurring template's date changes.
        if (dto.AssignmentDate.HasValue && !template.IsRecurring)
        {
            var firstOccurrence = await _unitOfWork.ExamHomeworkRepo
                .GetFirstOccurrenceAsync(template.Id, teacherId);
            if (firstOccurrence is not null)
            {
                firstOccurrence.DueDate = dto.AssignmentDate.Value.Date;
                await _unitOfWork.ExamHomeworkRepo.UpdateOccurrenceAsync(firstOccurrence);
            }
        }

        await _unitOfWork.ExamHomeworkRepo.UpdateTemplateAsync(template);
        _unitOfWork.ExamHomeworkRepo.SetTemplateOriginalRowVersion(template, dto.RowVersion);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<AssignmentTemplateDto>.Failure(
                _localizer, "TemplateConcurrencyConflict", HttpStatusCode.Conflict);
        }

        var hydrated = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateWithScopesAsync(template.Id, teacherId);
        return Result<AssignmentTemplateDto>.Success(
            MapTemplateToDto(hydrated!), _localizer, "AssignmentUpdated");
    }

    /// <inheritdoc />
    public async Task<Result<bool>> DeleteTemplateAsync(
        long teacherId, long actingUserId, long templateId, bool confirm)
    {
        if (!confirm)
            return Result<bool>.Failure(
                _localizer, "DeletionConfirmationRequired", HttpStatusCode.BadRequest);

        var template = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateWithScopesAsync(templateId, teacherId);

        if (template is null)
            return Result<bool>.Failure(
                _localizer, "TemplateNotFound", HttpStatusCode.NotFound);

        // ── Compute audit context BEFORE the delete fires ──
        int studentsAffected = await _unitOfWork.ExamHomeworkRepo
            .CountStudentsWithRecordedDataAsync(templateId);
        int occurrenceCount = await _unitOfWork.ExamHomeworkRepo
            .CountOccurrencesByTemplateAsync(templateId);

        // Pull audit-log history into the snapshot before we delete it.
        var auditLogs = await _unitOfWork.ExamHomeworkRepo
            .GetAuditLogsForTemplateAsync(templateId);

        string snapshotJson = BuildDeletionSnapshotJson(
            template, studentsAffected, occurrenceCount, auditLogs);

        var deletionLog = new AssignmentDeletionLog
        {
            TemplateId = template.Id,
            TeacherId = teacherId,
            DeletionType = AssignmentDeletionType.HardDelete,
            StudentsAffected = studentsAffected,
            TemplateSnapshotJson = snapshotJson,
            DeletedByUserId = actingUserId,
            DeletedAt = DateTime.UtcNow,
            LastOccurrenceId = null, // hard delete — no surviving anchor
            CreateAt = DateTime.UtcNow,
        };

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            // 1. Persist the JSON snapshot first — it's the only forensic record.
            await _unitOfWork.ExamHomeworkRepo.AddDeletionLogAsync(deletionLog);

            // 2. Delete audit logs explicitly. Their FK to the obligation is Restrict,
            //    so a NoAction from template → occurrence → obligation would fail with
            //    a referential-integrity error if any audit row exists. We archive into
            //    the JSON snapshot above, then delete in bulk here.
            await _unitOfWork.ExamHomeworkRepo.DeleteAuditLogsForTemplateAsync(templateId);

            // 3. Leaf-first set-based hard delete: obligations → occurrences → scopes →
            //    template (all FKs are NoAction, so nothing cascades on its own).
            await _unitOfWork.ExamHomeworkRepo.DeleteTemplateAsync(template);

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            throw;
        }

        return Result<bool>.Success(true, _localizer, "AssignmentDeleted", HttpStatusCode.NoContent);
    }

    /// <inheritdoc />
    public async Task<Result<StopRecurrenceResultDto>> StopRecurrenceAsync(
        long teacherId, long actingUserId, long templateId, StopRecurrenceDto dto)
    {
        var template = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateByIdAndTeacherAsync(templateId, teacherId);

        if (template is null)
            return Result<StopRecurrenceResultDto>.Failure(
                _localizer, "TemplateNotFound", HttpStatusCode.NotFound);

        if (!template.IsRecurring)
            return Result<StopRecurrenceResultDto>.Failure(
                _localizer, "TemplateIsNotRecurring", HttpStatusCode.BadRequest);

        if (template.IsRecurrenceStopped)
            return Result<StopRecurrenceResultDto>.Failure(
                _localizer, "RecurrenceAlreadyStopped", HttpStatusCode.BadRequest);

        var lastOccurrence = await _unitOfWork.ExamHomeworkRepo
            .GetLatestOccurrenceAsync(templateId, teacherId);

        DateTime utcNow = DateTime.UtcNow;
        template.IsRecurrenceStopped = true;
        template.UpdatedAt = utcNow;

        var deletionLog = new AssignmentDeletionLog
        {
            TemplateId = template.Id,
            TeacherId = teacherId,
            DeletionType = AssignmentDeletionType.StopRecurrence,
            StudentsAffected = 0, // stop-recurrence destroys no data
            TemplateSnapshotJson = BuildStopRecurrenceSnapshotJson(template, lastOccurrence),
            DeletedByUserId = actingUserId,
            DeletedAt = utcNow,
            LastOccurrenceId = lastOccurrence?.Id,
            CreateAt = utcNow,
        };

        await _unitOfWork.ExamHomeworkRepo.UpdateTemplateAsync(template);
        await _unitOfWork.ExamHomeworkRepo.AddDeletionLogAsync(deletionLog);
        _unitOfWork.ExamHomeworkRepo.SetTemplateOriginalRowVersion(template, dto.RowVersion);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<StopRecurrenceResultDto>.Failure(
                _localizer, "TemplateConcurrencyConflict", HttpStatusCode.Conflict);
        }

        return Result<StopRecurrenceResultDto>.Success(new StopRecurrenceResultDto
        {
            TemplateId = template.Id,
            StoppedAt = utcNow,
            LastOccurrenceId = lastOccurrence?.Id,
            LastOccurrenceNumber = lastOccurrence?.OccurrenceNumber,
            LastOccurrenceDueDate = lastOccurrence?.DueDate,
        }, _localizer, "RecurrenceStopped");
    }

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<OccurrenceSummaryItemDto>>>> GetOccurrencesAsync(
        long teacherId, long templateId, int page, int pageSize)
    {
        // Tenant-guarded existence check — never leak existence of other tutors' templates.
        var templateExists = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateByIdAndTeacherAsync(templateId, teacherId) is not null;
        if (!templateExists)
            return Result<PaginatedResponse<List<OccurrenceSummaryItemDto>>>.Failure(
                _localizer, "TemplateNotFound", HttpStatusCode.NotFound);

        var (occurrences, totalCount) = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrencesByTemplatePagedAsync(teacherId, templateId, page, pageSize);

        // O(2) aggregate: occurrence-level counts in one grouped query.
        var occurrenceIds = occurrences.Select(o => o.Id).ToList();
        var summaries = await _unitOfWork.ExamHomeworkRepo
            .GetCompletionSummariesByOccurrenceIdsAsync(occurrenceIds);

        var items = occurrences.Select(o => new OccurrenceSummaryItemDto
        {
            Id = o.Id,
            OccurrenceNumber = o.OccurrenceNumber,
            DueDate = o.DueDate,
            Status = o.Status.ToString(),
            MaxGradeSnapshot = o.MaxGradeSnapshot,
            PassingThresholdSnapshot = o.PassingThresholdSnapshot,
            TrackingModeSnapshot = o.TrackingModeSnapshot,
            Totals = MapToCompletionSummaryDto(summaries.GetValueOrDefault(o.Id)),
        }).ToList();

        var response = new PaginatedResponse<List<OccurrenceSummaryItemDto>>
        {
            data = items,
            page = page,
            pageSize = pageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
        };
        return Result<PaginatedResponse<List<OccurrenceSummaryItemDto>>>.Success(
            response, _localizer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PHASE 3 — SCOPE MANAGEMENT
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<AddScopesResultDto>> AddScopesAsync(
        long teacherId, long actingUserId, long templateId, AddScopesDto dto)
    {
        if (dto.Scopes is null || dto.Scopes.Count == 0)
            return Result<AddScopesResultDto>.Failure(
                _localizer, "ScopeListEmpty", HttpStatusCode.BadRequest);

        var template = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateByIdAndTeacherAsync(templateId, teacherId);
        if (template is null)
            return Result<AddScopesResultDto>.Failure(
                _localizer, "TemplateNotFound", HttpStatusCode.NotFound);

        foreach (var s in dto.Scopes)
        {
            if (!IsScopeShapeValid(s))
                return Result<AddScopesResultDto>.Failure(
                    _localizer, "InvalidScopeShape", HttpStatusCode.BadRequest);
        }

        var ownership = await ValidateScopeOwnershipAsync(teacherId, dto.Scopes);
        if (!ownership.IsSuccess)
            return Result<AddScopesResultDto>.Failure(
                _localizer, ownership.MessageKey!, ownership.StatusCode);

        // Resolve students from the new scopes only — existing scopes already covered.
        var newStudentIds = await ResolveAndDedupeStudentsAsync(teacherId, dto.Scopes);

        // Anchor: the next future occurrence (or the only existing one for non-recurring).
        // BR-EXH-002: existing past occurrences are NOT modified.
        var nextOccurrence = await GetNextFutureOccurrenceAsync(teacherId, templateId);

        DateTime utcNow = DateTime.UtcNow;
        var scopeEntities = BuildScopesFromDto(dto.Scopes, template, teacherId, utcNow);

        int obligationsCreated = 0;
        long? nextOccurrenceId = nextOccurrence?.Id;
        DateTime? nextOccurrenceDueDate = nextOccurrence?.DueDate;

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            await _unitOfWork.ExamHomeworkRepo.AddScopesRangeAsync(scopeEntities);

            // If a future occurrence exists, add obligations for newly resolved students
            // who don't already have one.
            if (nextOccurrence is not null && newStudentIds.Count > 0)
            {
                var existingIds = await _unitOfWork.ExamHomeworkRepo
                    .GetExistingObligationStudentIdsAsync(nextOccurrence.Id, newStudentIds);

                var idsToCreate = newStudentIds.Except(existingIds).ToList();
                if (idsToCreate.Count > 0)
                {
                    var obligations = BuildObligations(nextOccurrence, teacherId, idsToCreate, utcNow);
                    await _unitOfWork.ExamHomeworkRepo.AddObligationsRangeAsync(obligations);
                    obligationsCreated = idsToCreate.Count;
                }
            }

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            throw;
        }

        return Result<AddScopesResultDto>.Success(new AddScopesResultDto
        {
            ScopesAdded = scopeEntities.Count,
            ObligationsCreated = obligationsCreated,
            NextOccurrenceId = nextOccurrenceId,
            NextOccurrenceDueDate = nextOccurrenceDueDate,
        }, _localizer, "ScopesAdded");
    }

    /// <inheritdoc />
    public async Task<Result<bool>> RemoveScopeAsync(
        long teacherId, long actingUserId, long templateId, long scopeId)
    {
        var scope = await _unitOfWork.ExamHomeworkRepo
            .GetScopeByIdAndTeacherAsync(scopeId, teacherId);

        if (scope is null || scope.TemplateId != templateId)
            return Result<bool>.Failure(
                _localizer, "ScopeNotFound", HttpStatusCode.NotFound);

        await _unitOfWork.ExamHomeworkRepo.DeleteScopeAsync(scope);
        await _unitOfWork.SaveChangesAsync();

        return Result<bool>.Success(true, _localizer, "ScopeRemoved", HttpStatusCode.NoContent);
    }

    /// <inheritdoc />
    public async Task<Result<AddStudentsResultDto>> AddStudentsToTemplateAsync(
        long teacherId, long actingUserId, long templateId, AddStudentsToTemplateDto dto)
    {
        if (dto.TeacherStudentIds is null || dto.TeacherStudentIds.Count == 0)
            return Result<AddStudentsResultDto>.Failure(
                _localizer, "StudentListEmpty", HttpStatusCode.BadRequest);

        if (dto.TeacherStudentIds.Count > MaxStudentsPerAddCall)
            return Result<AddStudentsResultDto>.Failure(
                _localizer, "TooManyStudentsInOneCall", HttpStatusCode.BadRequest);

        // Convert to scope inputs and delegate to AddScopesAsync logic — keeps the
        // dedupe / ownership / next-occurrence wiring in one place.
        var scopeInputs = dto.TeacherStudentIds
            .Distinct()
            .Select(id => new ScopeInputDto
            {
                ScopeType = AssignmentScopeType.IndividualStudent,
                TeacherStudentId = id,
            })
            .ToList();

        var addScopesResult = await AddScopesAsync(
            teacherId, actingUserId, templateId, new AddScopesDto { Scopes = scopeInputs });

        if (!addScopesResult.IsSuccess)
        {
            // Propagate the already-localized message directly. Result<T>.Message holds
            // the localized text; we cannot re-key through the localizer here.
            return Result<AddStudentsResultDto>.Failure(
                addScopesResult.Message ?? string.Empty, addScopesResult.StatusCode);
        }

        var inner = addScopesResult.Data!;
        var skipped = scopeInputs.Count - inner.ObligationsCreated;

        return Result<AddStudentsResultDto>.Success(new AddStudentsResultDto
        {
            ScopesAdded = inner.ScopesAdded,
            ObligationsCreated = inner.ObligationsCreated,
            Skipped = skipped > 0 ? new List<long>() : new List<long>(), // detailed list available if needed
            NextOccurrenceId = inner.NextOccurrenceId,
        }, _localizer, "StudentsAddedToTemplate");
    }

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<EligibleStudentDto>>>> GetEligibleStudentsAsync(
        long teacherId, long templateId, EligibleStudentsRequest request)
    {
        var templateExists = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateByIdAndTeacherAsync(templateId, teacherId) is not null;
        if (!templateExists)
            return Result<PaginatedResponse<List<EligibleStudentDto>>>.Failure(
                _localizer, "TemplateNotFound", HttpStatusCode.NotFound);

        var (rows, totalCount) = await _unitOfWork.ExamHomeworkRepo
            .GetEligibleStudentsForTemplatePagedAsync(
                teacherId, templateId, request.SessionId, request.Search,
                request.Page, request.PageSize);

        var items = rows.Select(r => new EligibleStudentDto
        {
            TeacherStudentId = r.TeacherStudentId,
            StudentName = r.StudentName,
            StudentCode = r.StudentCode,
            SessionName = r.SessionName,
        }).ToList();

        var response = new PaginatedResponse<List<EligibleStudentDto>>
        {
            data = items,
            page = request.Page,
            pageSize = request.PageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
        };
        return Result<PaginatedResponse<List<EligibleStudentDto>>>.Success(response, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<RemoveStudentResultDto>> RemoveStudentFromTemplateAsync(
        long teacherId, long actingUserId, long templateId, long teacherStudentId, bool force)
    {
        var template = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateByIdAndTeacherAsync(templateId, teacherId);
        if (template is null)
            return Result<RemoveStudentResultDto>.Failure(
                _localizer, "TemplateNotFound", HttpStatusCode.NotFound);

        // Force-flag check: any obligation with recorded data?
        int recordedCount = await _unitOfWork.ExamHomeworkRepo
            .CountStudentObligationsWithDataAsync(teacherId, templateId, teacherStudentId);

        if (recordedCount > 0 && !force)
            return Result<RemoveStudentResultDto>.Failure(
                _localizer, "ForceFlagRequiredForRecordedData", HttpStatusCode.Conflict);

        var futureObligations = await _unitOfWork.ExamHomeworkRepo
            .GetFutureObligationsForStudentAsync(
                teacherId, templateId, teacherStudentId, DateTime.UtcNow);

        // Find any IndividualStudent scope rows pointing at this student and delete them
        // so the student isn't re-added on the next recurrence iteration.
        var scopes = await _unitOfWork.ExamHomeworkRepo.GetScopesByTemplateAsync(templateId);
        var individualScopes = scopes
            .Where(s => s.ScopeType == AssignmentScopeType.IndividualStudent
                     && s.TeacherStudentId == teacherStudentId)
            .ToList();

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            foreach (var s in individualScopes)
                await _unitOfWork.ExamHomeworkRepo.DeleteScopeAsync(s);

            var occurrenceIds = new HashSet<long>();
            foreach (var o in futureObligations)
            {
                occurrenceIds.Add(o.OccurrenceId);
                await _unitOfWork.ExamHomeworkRepo.DeleteObligationAsync(o);
            }

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();

            return Result<RemoveStudentResultDto>.Success(new RemoveStudentResultDto
            {
                ScopeRowsRemoved = individualScopes.Count,
                ObligationsDeleted = futureObligations.Count,
                OccurrencesAffected = occurrenceIds.Count,
                RecordsWithDataDeleted = recordedCount,
            }, _localizer, "StudentRemovedFromTemplate");
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> RemoveObligationAsync(
        long teacherId, long actingUserId, long obligationId, bool force)
    {
        var obligation = await _unitOfWork.ExamHomeworkRepo
            .GetObligationByIdAndTeacherAsync(obligationId, teacherId);

        if (obligation is null)
            return Result<bool>.Failure(
                _localizer, "ObligationNotFound", HttpStatusCode.NotFound);

        bool hasData = obligation.Status != ObligationStatus.Pending || obligation.IsGradeEntered;
        if (hasData && !force)
            return Result<bool>.Failure(
                _localizer, "ForceFlagRequiredForRecordedData", HttpStatusCode.Conflict);

        await _unitOfWork.ExamHomeworkRepo.DeleteObligationAsync(obligation);
        await _unitOfWork.SaveChangesAsync();

        return Result<bool>.Success(true, _localizer, "ObligationRemoved", HttpStatusCode.NoContent);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PHASE 3 — OCCURRENCE & TRACKING VIEWS
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<OccurrenceDetailDto>> GetOccurrenceAsync(
        long teacherId, long occurrenceId)
    {
        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceWithTemplateAsync(occurrenceId, teacherId);

        if (occurrence is null)
            return Result<OccurrenceDetailDto>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        return Result<OccurrenceDetailDto>.Success(new OccurrenceDetailDto
        {
            Id = occurrence.Id,
            TemplateId = occurrence.TemplateId,
            TemplateName = occurrence.Template.Name,
            TemplateNameAr = occurrence.Template.NameAr,
            AssignmentType = occurrence.Template.AssignmentType,
            OccurrenceNumber = occurrence.OccurrenceNumber,
            DueDate = occurrence.DueDate,
            Status = occurrence.Status.ToString(),
            MaxGradeSnapshot = occurrence.MaxGradeSnapshot,
            PassingThresholdSnapshot = occurrence.PassingThresholdSnapshot,
            TrackingModeSnapshot = occurrence.TrackingModeSnapshot,
            RowVersion = occurrence.RowVersion,
        }, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<OccurrenceSummaryDto>> GetOccurrenceSummaryAsync(
        long teacherId, long occurrenceId)
    {
        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceByIdAndTeacherAsync(occurrenceId, teacherId);

        if (occurrence is null)
            return Result<OccurrenceSummaryDto>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        var summaries = await _unitOfWork.ExamHomeworkRepo
            .GetCompletionSummariesByOccurrenceIdsAsync(new[] { occurrenceId });

        var raw = summaries.GetValueOrDefault(occurrenceId);

        // Grade-pending count (separate filtered aggregate via the grade-entry pipeline).
        var (gradePendingItems, _) = await _unitOfWork.ExamHomeworkRepo
            .GetGradeEntryViewPagedAsync(teacherId, occurrenceId, search: null, page: 1, pageSize: 1);
        // We only need the count, but GetGradeEntryViewPagedAsync returns it as part of the tuple
        // when using `_, total`. Re-call with a count-only signature would be cleaner; for now
        // we run a one-row page to leverage the existing index.
        int gradePendingCount = await CountGradePendingAsync(teacherId, occurrenceId);

        // Below-passing count for exams only.
        int? belowPassingCount = null;
        if (occurrence.PassingThresholdSnapshot.HasValue)
            belowPassingCount = await CountBelowPassingAsync(
                teacherId, occurrenceId, occurrence.PassingThresholdSnapshot.Value);

        return Result<OccurrenceSummaryDto>.Success(new OccurrenceSummaryDto
        {
            OccurrenceId = occurrenceId,
            TotalStudents = raw?.TotalStudents ?? 0,
            DoneOrAttended = raw?.DoneOrAttended ?? 0,
            NotDoneOrAbsent = raw?.NotDoneOrAbsent ?? 0,
            Pending = raw?.Pending ?? 0,
            GradePending = gradePendingCount,
            BelowPassingCount = belowPassingCount,
        }, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<TrackingViewRowDto>>>> GetTrackingViewAsync(
        long teacherId, long occurrenceId, TrackingViewRequest request)
    {
        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceByIdAndTeacherAsync(occurrenceId, teacherId);
        if (occurrence is null)
            return Result<PaginatedResponse<List<TrackingViewRowDto>>>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        var (rows, totalCount) = await _unitOfWork.ExamHomeworkRepo
            .GetTrackingViewPagedAsync(
                teacherId, occurrenceId,
                request.Search,
                request.Status,
                request.MissingEntries,
                request.GradeAbove,
                request.GradeBelow,
                request.BelowPassing,
                gradeEntered: null,
                request.Page, request.PageSize);

        var response = new PaginatedResponse<List<TrackingViewRowDto>>
        {
            data = rows.Select(MapTrackingViewRowToDto).ToList(),
            page = request.Page,
            pageSize = request.PageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
        };
        return Result<PaginatedResponse<List<TrackingViewRowDto>>>.Success(response, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<List<StudentPickerRowDto>>> SearchStudentsInOccurrenceAsync(
        long teacherId, long occurrenceId, string query, int limit)
    {
        // Clamp limit to a sane range — typeahead pickers usually show 5-20 rows.
        int safeLimit = Math.Clamp(limit, 1, 50);

        var occurrenceExists = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceByIdAndTeacherAsync(occurrenceId, teacherId) is not null;
        if (!occurrenceExists)
            return Result<List<StudentPickerRowDto>>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        var rows = await _unitOfWork.ExamHomeworkRepo
            .SearchStudentsInOccurrenceAsync(teacherId, occurrenceId, query ?? string.Empty, safeLimit);

        var dtos = rows.Select(r => new StudentPickerRowDto
        {
            ObligationId = r.ObligationId,
            TeacherStudentId = r.TeacherStudentId,
            StudentName = r.StudentName,
            StudentCode = r.StudentCode,
            CurrentStatus = r.CurrentStatus,
        }).ToList();

        return Result<List<StudentPickerRowDto>>.Success(dtos, _localizer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PHASE 3 — STATUS ENTRY (Option B split)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<TrackingViewRowDto>> UpdateExamStatusAsync(
        long teacherId, long actingUserId, long occurrenceId, long teacherStudentId,
        UpdateExamStatusDto dto)
    {
        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceWithTemplateAsync(occurrenceId, teacherId);
        if (occurrence is null)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        if (occurrence.Template.AssignmentType != AssignmentType.Exam)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "InvalidStatusForExam", HttpStatusCode.BadRequest);

        if (!IsValidExamStatus(dto.Status))
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "InvalidStatusForExam", HttpStatusCode.BadRequest);

        var gradeValidation = ValidateExamGrade(
            dto.Status, dto.GradeValue, occurrence.MaxGradeSnapshot);
        if (gradeValidation is not null)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, gradeValidation, HttpStatusCode.BadRequest);

        var obligation = await _unitOfWork.ExamHomeworkRepo
            .GetObligationByOccurrenceAndStudentAsync(occurrenceId, teacherStudentId);
        if (obligation is null || obligation.TeacherId != teacherId)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "ObligationNotFound", HttpStatusCode.NotFound);

        return await ApplyStatusUpdateAsync(
            obligation, occurrence, dto.Status, dto.GradeValue,
            dto.RowVersion, actingUserId, teacherId, occurrenceId);
    }

    /// <inheritdoc />
    public async Task<Result<TrackingViewRowDto>> UpdateHomeworkStatusAsync(
        long teacherId, long actingUserId, long occurrenceId, long teacherStudentId,
        UpdateHomeworkStatusDto dto)
    {
        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceWithTemplateAsync(occurrenceId, teacherId);
        if (occurrence is null)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        if (occurrence.Template.AssignmentType != AssignmentType.Homework)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "InvalidStatusForHomework", HttpStatusCode.BadRequest);

        if (!IsValidHomeworkStatus(dto.Status, occurrence.TrackingModeSnapshot))
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "InvalidStatusForHomework", HttpStatusCode.BadRequest);

        var gradeValidation = ValidateHomeworkGrade(
            dto.Status, dto.GradeValue, occurrence.TrackingModeSnapshot);
        if (gradeValidation is not null)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, gradeValidation, HttpStatusCode.BadRequest);

        var obligation = await _unitOfWork.ExamHomeworkRepo
            .GetObligationByOccurrenceAndStudentAsync(occurrenceId, teacherStudentId);
        if (obligation is null || obligation.TeacherId != teacherId)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "ObligationNotFound", HttpStatusCode.NotFound);

        return await ApplyStatusUpdateAsync(
            obligation, occurrence, dto.Status, dto.GradeValue,
            dto.RowVersion, actingUserId, teacherId, occurrenceId);
    }

    /// <inheritdoc />
    public async Task<Result<TrackingViewRowDto>> UpdateExamStatusByCodeAsync(
        long teacherId, long actingUserId, long occurrenceId, UpdateStatusByCodeDto dto)
    {
        var student = await _unitOfWork.Students
            .GetActiveByCodeAndTeacherAsync(dto.StudentCode, teacherId);
        if (student is null)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "StudentCodeNotFound", HttpStatusCode.NotFound);

        return await UpdateExamStatusAsync(teacherId, actingUserId, occurrenceId, student.Id,
            new UpdateExamStatusDto
            {
                Status = dto.Status,
                GradeValue = dto.GradeValue,
                RowVersion = dto.RowVersion,
            });
    }

    /// <inheritdoc />
    public async Task<Result<TrackingViewRowDto>> UpdateHomeworkStatusByCodeAsync(
        long teacherId, long actingUserId, long occurrenceId, UpdateStatusByCodeDto dto)
    {
        var student = await _unitOfWork.Students
            .GetActiveByCodeAndTeacherAsync(dto.StudentCode, teacherId);
        if (student is null)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "StudentCodeNotFound", HttpStatusCode.NotFound);

        return await UpdateHomeworkStatusAsync(teacherId, actingUserId, occurrenceId, student.Id,
            new UpdateHomeworkStatusDto
            {
                Status = dto.Status,
                GradeValue = dto.GradeValue,
                RowVersion = dto.RowVersion,
            });
    }

    /// <inheritdoc />
    public async Task<Result<BulkStatusResultDto>> BulkUpdateStatusAsync(
        long teacherId, long actingUserId, long occurrenceId, BulkUpdateStatusDto dto)
    {
        if (dto.Items is null || dto.Items.Count == 0)
            return Result<BulkStatusResultDto>.Failure(
                _localizer, "BulkItemsEmpty", HttpStatusCode.BadRequest);

        if (dto.Items.Count > MaxBulkUpdateItems)
            return Result<BulkStatusResultDto>.Failure(
                _localizer, "BulkItemsTooMany", HttpStatusCode.BadRequest);

        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceWithTemplateAsync(occurrenceId, teacherId);
        if (occurrence is null)
            return Result<BulkStatusResultDto>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        // Validate the shared status against the occurrence's assignment type.
        bool statusValid = occurrence.Template.AssignmentType == AssignmentType.Exam
            ? IsValidExamStatus(dto.Status)
            : IsValidHomeworkStatus(dto.Status, occurrence.TrackingModeSnapshot);
        if (!statusValid)
            return Result<BulkStatusResultDto>.Failure(
                _localizer, "InvalidStatusForOccurrence", HttpStatusCode.BadRequest);

        // Validate the shared grade against the shared status.
        string? gradeError = occurrence.Template.AssignmentType == AssignmentType.Exam
            ? ValidateExamGrade(dto.Status, dto.GradeValue, occurrence.MaxGradeSnapshot)
            : ValidateHomeworkGrade(dto.Status, dto.GradeValue, occurrence.TrackingModeSnapshot);
        if (gradeError is not null)
            return Result<BulkStatusResultDto>.Failure(
                _localizer, gradeError, HttpStatusCode.BadRequest);

        var obligationIds = dto.Items.Select(i => i.ObligationId).ToList();
        var obligations = await _unitOfWork.ExamHomeworkRepo
            .GetObligationsByIdsAsync(teacherId, occurrenceId, obligationIds);

        if (obligations.Count != obligationIds.Count)
            return Result<BulkStatusResultDto>.Failure(
                _localizer, "BulkObligationMismatch", HttpStatusCode.BadRequest);

        DateTime utcNow = DateTime.UtcNow;
        var auditLogs = new List<StudentObligationAuditLog>();

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            // Match each obligation with its supplied RowVersion to validate concurrency.
            var rowVersionByObligationId = dto.Items.ToDictionary(i => i.ObligationId, i => i.RowVersion);

            foreach (var obligation in obligations)
            {
                var auditEntry = BuildAuditLogEntry(
                    obligation, dto.Status, dto.GradeValue, occurrence, actingUserId, utcNow);
                auditLogs.Add(auditEntry);

                ApplyStatusToObligation(obligation, dto.Status, dto.GradeValue, actingUserId, utcNow);

                await _unitOfWork.ExamHomeworkRepo.UpdateObligationAsync(obligation);
                _unitOfWork.ExamHomeworkRepo.SetObligationOriginalRowVersion(
                    obligation, rowVersionByObligationId[obligation.Id]);
            }

            foreach (var audit in auditLogs)
                await _unitOfWork.ExamHomeworkRepo.AddAuditLogAsync(audit);

            try
            {
                await _unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await _unitOfWork.RollbackAsync();
                return Result<BulkStatusResultDto>.Failure(
                    _localizer, "ObligationConcurrencyConflict", HttpStatusCode.Conflict);
            }

            await _unitOfWork.CommitAsync();
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            throw;
        }

        return Result<BulkStatusResultDto>.Success(new BulkStatusResultDto
        {
            OccurrenceId = occurrenceId,
            RowsUpdated = obligations.Count,
            NewStatus = dto.Status,
        }, _localizer, "BulkStatusUpdated");
    }

    // ══════════════════════════════════════════════════════════════════════
    // PHASE 3 — BARCODE SCAN, GRADE ENTRY, AUDIT
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<ScanResultDto>> ScanBarcodeAsync(
        long teacherId, long actingUserId, long occurrenceId, ScanBarcodeDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Barcode))
            return Result<ScanResultDto>.Failure(
                _localizer, "BarcodeRequired", HttpStatusCode.BadRequest);

        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceWithTemplateAsync(occurrenceId, teacherId);
        if (occurrence is null)
            return Result<ScanResultDto>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        // Resolve the barcode to a student. Barcodes are unique per teacher (REQ-STU-047).
        // For Phase 3 we treat the barcode as the student's StudentCode for lookup; in
        // production this should use a dedicated `GetByBarcodeAsync`. The catalog notes
        // that auto-generated barcodes contain the student code as a prefix.
        var student = await _unitOfWork.Students
            .GetActiveByCodeAndTeacherAsync(dto.Barcode.Trim(), teacherId);
        if (student is null)
            return Result<ScanResultDto>.Failure(
                _localizer, "BarcodeNotFound", HttpStatusCode.NotFound);

        // Determine the auto-mark status: Attended for exam, Done for homework.
        ObligationStatus newStatus = occurrence.Template.AssignmentType == AssignmentType.Exam
            ? ObligationStatus.Attended
            : ObligationStatus.Done;

        DateTime scannedAt = DateTime.UtcNow;

        // Atomic conditional UPDATE — no SELECT-then-UPDATE; idempotent on re-scan.
        int rowsUpdated = await _unitOfWork.ExamHomeworkRepo.TryMarkScannedAsync(
            teacherId, occurrenceId, student.Id, newStatus, scannedAt, actingUserId);

        // Re-read the obligation to return the canonical state to the client. If rowsUpdated
        // is 0, another process won the race — we still return a 200 with AlreadyProcessed=true
        // and the current status (REQ-EXH-NFR-002 idempotent success).
        var obligation = await _unitOfWork.ExamHomeworkRepo
            .GetObligationByOccurrenceAndStudentAsync(occurrenceId, student.Id);

        if (obligation is null)
            return Result<ScanResultDto>.Failure(
                _localizer, "ObligationNotFound", HttpStatusCode.NotFound);

        // Persist audit only when we actually transitioned the row (rowsUpdated > 0).
        // The idempotent re-scan path does NOT add a duplicate audit row.
        if (rowsUpdated > 0)
        {
            var audit = new StudentObligationAuditLog
            {
                StudentObligationId = obligation.Id,
                TeacherId = teacherId,
                OldStatus = ObligationStatus.Pending,
                NewStatus = newStatus,
                OldGradeValue = null,
                NewGradeValue = null,
                MaxGradeSnapshot = occurrence.MaxGradeSnapshot,
                PassingThresholdSnapshot = occurrence.PassingThresholdSnapshot,
                ChangeReason = "Barcode scan",
                ChangedByUserId = actingUserId,
                ChangedAt = scannedAt,
                CreateAt = scannedAt,
            };
            await _unitOfWork.ExamHomeworkRepo.AddAuditLogAsync(audit);
            await _unitOfWork.SaveChangesAsync();
        }

        return Result<ScanResultDto>.Success(new ScanResultDto
        {
            ObligationId = obligation.Id,
            TeacherStudentId = student.Id,
            StudentName = student.StudentName,
            StudentCode = student.StudentCode,
            Status = obligation.Status,
            AlreadyProcessed = rowsUpdated == 0,
            ScannedAt = obligation.ScannedAt ?? scannedAt,
        }, _localizer, rowsUpdated > 0 ? "ScanRecorded" : "ScanAlreadyProcessed");
    }

    /// <inheritdoc />
    public async Task<Result<GradeEntryViewDto>> GetPendingGradesAsync(
        long teacherId, long occurrenceId, GradeEntryRequest request)
    {
        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceByIdAndTeacherAsync(occurrenceId, teacherId);
        if (occurrence is null)
            return Result<GradeEntryViewDto>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        var (rows, totalCount) = await _unitOfWork.ExamHomeworkRepo
            .GetGradeEntryViewPagedAsync(
                teacherId, occurrenceId, request.Search, request.Page, request.PageSize);

        var paged = new PaginatedResponse<List<TrackingViewRowDto>>
        {
            data = rows.Select(MapTrackingViewRowToDto).ToList(),
            page = request.Page,
            pageSize = request.PageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
        };

        return Result<GradeEntryViewDto>.Success(new GradeEntryViewDto
        {
            OccurrenceId = occurrenceId,
            MaxGrade = occurrence.MaxGradeSnapshot,
            PassingThreshold = occurrence.PassingThresholdSnapshot,
            Students = paged,
        }, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<TrackingViewRowDto>> EnterGradeAsync(
        long teacherId, long actingUserId, long obligationId, EnterGradeDto dto)
    {
        var obligation = await _unitOfWork.ExamHomeworkRepo
            .GetObligationByIdAndTeacherAsync(obligationId, teacherId);
        if (obligation is null)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "ObligationNotFound", HttpStatusCode.NotFound);

        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceWithTemplateAsync(obligation.OccurrenceId, teacherId);
        if (occurrence is null)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);

        // REQ-EXH-026-D / 026-E / 027: Status must be in the grade-pending state.
        if (obligation.Status != ObligationStatus.Attended
         && obligation.Status != ObligationStatus.DoneWithoutGrade
         && obligation.Status != ObligationStatus.AttendedWithGrade
         && obligation.Status != ObligationStatus.DoneWithGrade)
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "ObligationNotInGradePendingState", HttpStatusCode.UnprocessableEntity);

        // Range-validate the grade against the snapshotted max.
        if (occurrence.MaxGradeSnapshot.HasValue
         && (dto.GradeValue < 0m || dto.GradeValue > occurrence.MaxGradeSnapshot.Value))
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "GradeOutOfRange", HttpStatusCode.BadRequest);

        // Compute the post-entry status based on the current one + assignment type.
        ObligationStatus newStatus = obligation.Status switch
        {
            ObligationStatus.Attended => ObligationStatus.AttendedWithGrade,
            ObligationStatus.DoneWithoutGrade => ObligationStatus.DoneWithGrade,
            ObligationStatus.AttendedWithGrade => ObligationStatus.AttendedWithGrade, // amend
            ObligationStatus.DoneWithGrade => ObligationStatus.DoneWithGrade, // amend
            _ => obligation.Status,
        };

        DateTime utcNow = DateTime.UtcNow;
        var audit = BuildAuditLogEntry(obligation, newStatus, dto.GradeValue, occurrence, actingUserId, utcNow);

        ApplyStatusToObligation(obligation, newStatus, dto.GradeValue, actingUserId, utcNow);
        obligation.IsGradeEntered = true;

        await _unitOfWork.ExamHomeworkRepo.UpdateObligationAsync(obligation);
        await _unitOfWork.ExamHomeworkRepo.AddAuditLogAsync(audit);
        _unitOfWork.ExamHomeworkRepo.SetObligationOriginalRowVersion(obligation, dto.RowVersion);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "ObligationConcurrencyConflict", HttpStatusCode.Conflict);
        }

        // Phase 5: Notify messaging engine after the save succeeds.
        await NotifyOnGradeEntryAsync(obligation, occurrence, dto.GradeValue);

        return Result<TrackingViewRowDto>.Success(
            BuildTrackingViewRowDtoFromObligation(obligation, occurrence),
            _localizer, "GradeEntered");
    }

     
    

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<ObligationAuditEntryDto>>>> GetObligationAuditLogAsync(
        long teacherId, long obligationId, int page, int pageSize)
    {
        var obligation = await _unitOfWork.ExamHomeworkRepo
            .GetObligationByIdAndTeacherAsync(obligationId, teacherId);
        if (obligation is null)
            return Result<PaginatedResponse<List<ObligationAuditEntryDto>>>.Failure(
                _localizer, "ObligationNotFound", HttpStatusCode.NotFound);

        // Audit histories per obligation are bounded (typically < 50 entries) — fetching
        // the full list and paginating in-memory is acceptable here. If a single obligation
        // ever grows above ~500 audit entries, add a paged repo method.
        var allEntries = await _unitOfWork.ExamHomeworkRepo
            .GetAuditHistoryByObligationAsync(obligationId);

        int totalCount = allEntries.Count;
        var pagedEntries = allEntries
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new ObligationAuditEntryDto
            {
                Id = a.Id,
                OldStatus = a.OldStatus,
                NewStatus = a.NewStatus,
                OldGradeValue = a.OldGradeValue,
                NewGradeValue = a.NewGradeValue,
                MaxGradeSnapshot = a.MaxGradeSnapshot,
                PassingThresholdSnapshot = a.PassingThresholdSnapshot,
                ChangeReason = a.ChangeReason,
                ChangedByUserId = (long)a.ChangedByUserId,
                ChangedByUserName = a.ChangedByUser?.FullName,
                ChangedAt = a.ChangedAt,
            }).ToList();

        var response = new PaginatedResponse<List<ObligationAuditEntryDto>>
        {
            data = pagedEntries,
            page = page,
            pageSize = pageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
        };
        return Result<PaginatedResponse<List<ObligationAuditEntryDto>>>.Success(response, _localizer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS — INPUT VALIDATION
    // ══════════════════════════════════════════════════════════════════════

    private Result<AssignmentTemplateDto> ValidateCreateInput(
        CreateAssignmentTemplateDto dto, DateTime teacherLocalToday)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Length > 200
         || string.IsNullOrWhiteSpace(dto.NameAr) || dto.NameAr.Length > 200)
            return Failure("AssignmentNameRequired");

        if (dto.Notes is not null && dto.Notes.Length > 2000)
            return Failure("AssignmentNotesTooLong");

        // The tutor's today, not UTC's: between midnight and 2-3 AM Cairo the UTC date is
        // still yesterday, so this let a genuinely past date through for those hours.
        if (dto.AssignmentDate.Date < teacherLocalToday)
            return Failure("AssignmentDateInPast");

        if (dto.Scopes is null || dto.Scopes.Count == 0)
            return Failure("ScopeListEmpty");

        foreach (var scope in dto.Scopes)
        {
            if (!IsScopeShapeValid(scope))
                return Failure("InvalidScopeShape");
        }

        if (!dto.IsRecurring && dto.RecurrencePattern != RecurrencePattern.OneTime)
            return Failure("RecurrenceFlagMismatch");

        if (dto.AssignmentType == AssignmentType.Homework)
        {
            if (dto.TrackingMode is null)
                return Failure("HomeworkRequiresTrackingMode");

            if (dto.RecurrencePattern is not (RecurrencePattern.OneTime
                                           or RecurrencePattern.EverySession
                                           or RecurrencePattern.EveryTwoSessions))
                return Failure("InvalidRecurrenceForHomework");
        }
        else // Exam
        {
            if (dto.MaxGrade is null || dto.MaxGrade.Value <= 0m)
                return Failure("ExamRequiresMaxGrade");

            if (dto.PassingThreshold is null
             || dto.PassingThreshold.Value < 0m
             || dto.PassingThreshold.Value > dto.MaxGrade.Value)
                return Failure("PassingThresholdOutOfRange");

            if (dto.RecurrencePattern is not (RecurrencePattern.OneTime
                                           or RecurrencePattern.Monthly))
                return Failure("InvalidRecurrenceForExam");
        }

        return Result<AssignmentTemplateDto>.Success(default!, _localizer);
    }

    private Result<AssignmentTemplateDto> ValidateUpdateInput(
        AssignmentTemplate template, UpdateAssignmentTemplateDto dto, DateTime teacherLocalToday)
    {
        if (dto.RowVersion is null || dto.RowVersion.Length == 0)
            return Failure("BadRequest");

        if (dto.Name is not null
            && (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Length > 200))
            return Failure("AssignmentNameRequired");

        if (dto.NameAr is not null
            && (string.IsNullOrWhiteSpace(dto.NameAr) || dto.NameAr.Length > 200))
            return Failure("AssignmentNameRequired");

        if (dto.Notes is not null && dto.Notes.Length > 2000)
            return Failure("AssignmentNotesTooLong");

        if (dto.AssignmentDate.HasValue)
        {
            // assignmentDate is not editable for recurring templates per §3.1 endpoint 4.
            if (template.IsRecurring)
                return Failure("BadRequest");

            if (dto.AssignmentDate.Value.Date < teacherLocalToday)
                return Failure("AssignmentDateInPast");
        }

        if (template.AssignmentType == AssignmentType.Homework
            && dto.RecurrencePattern.HasValue
            && dto.RecurrencePattern.Value is not (RecurrencePattern.OneTime
                                                or RecurrencePattern.EverySession
                                                or RecurrencePattern.EveryTwoSessions))
            return Failure("InvalidRecurrenceForHomework");

        if (template.AssignmentType == AssignmentType.Exam
            && dto.RecurrencePattern.HasValue
            && dto.RecurrencePattern.Value is not (RecurrencePattern.OneTime
                                                or RecurrencePattern.Monthly))
            return Failure("InvalidRecurrenceForExam");

        if (template.AssignmentType == AssignmentType.Exam
            && (dto.MaxGrade.HasValue || dto.PassingThreshold.HasValue))
        {
            decimal effectiveMax = dto.MaxGrade ?? template.MaxGrade ?? 0m;
            decimal effectiveThreshold = dto.PassingThreshold ?? template.PassingThreshold ?? 0m;

            if (effectiveMax <= 0m)
                return Failure("ExamRequiresMaxGrade");

            if (effectiveThreshold < 0m || effectiveThreshold > effectiveMax)
                return Failure("PassingThresholdOutOfRange");
        }

        return Result<AssignmentTemplateDto>.Success(default!, _localizer);
    }

    private static bool IsScopeShapeValid(ScopeInputDto s) =>
        s.ScopeType switch
        {
            AssignmentScopeType.IndividualStudent =>
                s.TeacherStudentId.HasValue && !s.SessionId.HasValue && !s.SessionGroupId.HasValue,
            AssignmentScopeType.Session =>
                s.SessionId.HasValue && !s.TeacherStudentId.HasValue && !s.SessionGroupId.HasValue,
            AssignmentScopeType.SessionGroup =>
                s.SessionGroupId.HasValue && !s.TeacherStudentId.HasValue && !s.SessionId.HasValue,
            _ => false
        };

    /// <summary>
    /// Validates that every referenced scope target belongs to the JWT teacher.
    /// Returns a tuple instead of <see cref="Result{T}"/> so the caller can decide
    /// which T to wrap the failure in (it differs per endpoint).
    /// </summary>
    private async Task<(bool IsSuccess, string? MessageKey, HttpStatusCode StatusCode)>
        ValidateScopeOwnershipAsync(long teacherId, IEnumerable<ScopeInputDto> scopes)
    {
        foreach (var s in scopes)
        {
            switch (s.ScopeType)
            {
                case AssignmentScopeType.IndividualStudent:
                    var student = await _unitOfWork.Students
                        .GetActiveByIdAndTeacherAsync(s.TeacherStudentId!.Value, teacherId);
                    if (student is null)
                        return (false, "ScopeTargetNotFoundOrForeign", HttpStatusCode.BadRequest);
                    break;

                case AssignmentScopeType.Session:
                    var session = await _unitOfWork.SessionsRepo
                        .GetByIdAndTeacherAsync(s.SessionId!.Value, teacherId);
                    if (session is null)
                        return (false, "ScopeTargetNotFoundOrForeign", HttpStatusCode.BadRequest);
                    break;

                case AssignmentScopeType.SessionGroup:
                    var group = await _unitOfWork.SessionsRepo
                        .GetGroupByIdAndTeacherAsync(s.SessionGroupId!.Value, teacherId);
                    if (group is null)
                        return (false, "ScopeTargetNotFoundOrForeign", HttpStatusCode.BadRequest);
                    break;
            }
        }
        return (true, null, HttpStatusCode.OK);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS — STUDENT RESOLUTION (REUSE existing PaymentsRepo methods)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves all scope rows to a deduplicated set of TeacherStudentIds.
    /// Delegates to <see cref="IAssignmentScopeResolver"/> — the same resolver is
    /// shared with the Hangfire materializer so dedupe semantics never drift.
    /// REQ-EXH-003: HashSet handles deduplication for free.
    /// </summary>
    private Task<HashSet<long>> ResolveAndDedupeStudentsAsync(
        long teacherId, IEnumerable<ScopeInputDto> scopes)
        => _scopeResolver.ResolveFromInputsAsync(teacherId, scopes);

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS — ENTITY BUILDERS
    // ══════════════════════════════════════════════════════════════════════

    private static AssignmentTemplate BuildTemplateFromDto(
        CreateAssignmentTemplateDto dto, long teacherId, long actingUserId, DateTime utcNow)
        => new()
        {
            TeacherId = teacherId,
            AssignmentType = dto.AssignmentType,
            Name = dto.Name.Trim(),
            NameAr = dto.NameAr.Trim(),
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            IsRecurring = dto.IsRecurring,
            RecurrencePattern = dto.RecurrencePattern,
            RecurrenceEndDate = dto.RecurrenceEndDate?.Date,
            IsRecurrenceStopped = false,
            TrackingMode = dto.AssignmentType == AssignmentType.Homework ? dto.TrackingMode : null,
            MaxGrade = dto.AssignmentType == AssignmentType.Exam ? dto.MaxGrade : null,
            PassingThreshold = dto.AssignmentType == AssignmentType.Exam ? dto.PassingThreshold : null,
            CreatedByUserId = actingUserId,
            UpdatedAt = utcNow,
            CreateAt = utcNow,
        };

    private static List<AssignmentScope> BuildScopesFromDto(
        IEnumerable<ScopeInputDto> dtos, AssignmentTemplate template, long teacherId, DateTime utcNow)
        => dtos.Select(s => new AssignmentScope
        {
            TeacherId = teacherId,
            Template = template,
            ScopeType = s.ScopeType,
            TeacherStudentId = s.ScopeType == AssignmentScopeType.IndividualStudent ? s.TeacherStudentId : null,
            SessionId = s.ScopeType == AssignmentScopeType.Session ? s.SessionId : null,
            SessionGroupId = s.ScopeType == AssignmentScopeType.SessionGroup ? s.SessionGroupId : null,
            CreateAt = utcNow,
        }).ToList();

    private static AssignmentOccurrence BuildFirstOccurrence(
        AssignmentTemplate template, long teacherId, DateTime dueDate, DateTime utcNow)
        => new()
        {
            Template = template,
            TeacherId = teacherId,
            OccurrenceNumber = 1,
            DueDate = dueDate,
            Status = AssignmentOccurrenceStatus.Pending,
            MaxGradeSnapshot = template.MaxGrade,
            PassingThresholdSnapshot = template.PassingThreshold,
            TrackingModeSnapshot = template.TrackingMode,
            CreateAt = utcNow,
        };

    private static List<StudentAssignmentObligation> BuildObligations(
        AssignmentOccurrence occurrence, long teacherId, IEnumerable<long> studentIds, DateTime utcNow)
        => studentIds.Select(studentId => new StudentAssignmentObligation
        {
            Occurrence = occurrence,
            TeacherId = teacherId,
            TeacherStudentId = studentId,
            Status = ObligationStatus.Pending,
            IsGradeEntered = false,
            MarkedByScan = false,
            UpdatedAt = utcNow,
            CreateAt = utcNow,
        }).ToList();

    private static void ApplyTemplateEdits(AssignmentTemplate template, UpdateAssignmentTemplateDto dto)
    {
        if (dto.Name is not null) template.Name = dto.Name.Trim();
        if (dto.NameAr is not null) template.NameAr = dto.NameAr.Trim();
        if (dto.Notes is not null) template.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
        if (dto.RecurrencePattern.HasValue) template.RecurrencePattern = dto.RecurrencePattern.Value;
        if (dto.RecurrenceEndDate.HasValue) template.RecurrenceEndDate = dto.RecurrenceEndDate.Value.Date;
        if (dto.TrackingMode.HasValue && template.AssignmentType == AssignmentType.Homework)
            template.TrackingMode = dto.TrackingMode.Value;
        if (dto.MaxGrade.HasValue && template.AssignmentType == AssignmentType.Exam)
            template.MaxGrade = dto.MaxGrade.Value;
        if (dto.PassingThreshold.HasValue && template.AssignmentType == AssignmentType.Exam)
            template.PassingThreshold = dto.PassingThreshold.Value;
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS — MAPPERS & FORMATTING
    // ══════════════════════════════════════════════════════════════════════

    private static AssignmentTemplateDto MapTemplateToDto(AssignmentTemplate t) => new()
    {
        Id = t.Id,
        AssignmentType = t.AssignmentType,
        Name = t.Name,
        NameAr = t.NameAr,
        Notes = t.Notes,
        IsRecurring = t.IsRecurring,
        RecurrencePattern = t.RecurrencePattern,
        RecurrenceEndDate = t.RecurrenceEndDate,
        IsRecurrenceStopped = t.IsRecurrenceStopped,
        TrackingMode = t.TrackingMode,
        MaxGrade = t.MaxGrade,
        PassingThreshold = t.PassingThreshold,
        CreatedAt = t.CreateAt,
        UpdatedAt = t.UpdatedAt,
        RowVersion = t.RowVersion,
        Scopes = t.Scopes.Select(MapScopeToDto).ToList(),
    };

    private static ScopeOutputDto MapScopeToDto(AssignmentScope s) => new()
    {
        Id = s.Id,
        ScopeType = s.ScopeType,
        TeacherStudentId = s.TeacherStudentId,
        StudentName = s.TeacherStudent?.StudentName,
        StudentCode = s.TeacherStudent?.StudentCode,
        SessionId = s.SessionId,
        SessionName = s.Session?.SessionName,
        SessionGroupId = s.SessionGroupId,
        SessionGroupName = s.SessionGroup?.GroupName,
    };

    private static CompletionSummaryDto MapToCompletionSummaryDto(OccurrenceCompletionSummary? src)
    {
        if (src is null) return new CompletionSummaryDto();
        return new CompletionSummaryDto
        {
            TotalStudents = src.TotalStudents,
            DoneOrAttended = src.DoneOrAttended,
            NotDoneOrAbsent = src.NotDoneOrAbsent,
            Pending = src.Pending,
        };
    }

    /// <summary>
    /// Builds the human-readable scope summary string (e.g., "3 students · 2 sessions · 1 group")
    /// from the per-template aggregate returned by the repo.
    /// </summary>
    private static string BuildScopeSummary(ScopeCountAggregate? c)
    {
        if (c is null) return "—";
        var parts = new List<string>(3);
        if (c.IndividualCount > 0)
            parts.Add($"{c.IndividualCount} {(c.IndividualCount == 1 ? "student" : "students")}");
        if (c.SessionCount > 0)
            parts.Add($"{c.SessionCount} {(c.SessionCount == 1 ? "session" : "sessions")}");
        if (c.GroupCount > 0)
            parts.Add($"{c.GroupCount} {(c.GroupCount == 1 ? "group" : "groups")}");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    private static string BuildDeletionSnapshotJson(
        AssignmentTemplate template,
        int studentsAffected,
        int occurrenceCount,
        IReadOnlyList<StudentObligationAuditLog> auditLogs)
    {
        bool perStudentDetailIncluded = studentsAffected <= DeletionSnapshotPerStudentCap;

        // Audit logs are archived into the snapshot before being bulk-deleted in the same
        // transaction (their FK to obligations is Restrict). This way the audit trail
        // survives the hard delete inside this single JSON column.
        var auditSummary = auditLogs.Select(a => new
        {
            a.Id,
            a.StudentObligationId,
            a.OldStatus,
            a.NewStatus,
            a.OldGradeValue,
            a.NewGradeValue,
            a.MaxGradeSnapshot,
            a.PassingThresholdSnapshot,
            a.ChangeReason,
            a.ChangedByUserId,
            a.ChangedAt,
        }).ToList();

        var snapshot = new
        {
            template.Id,
            template.AssignmentType,
            template.Name,
            template.NameAr,
            template.Notes,
            template.IsRecurring,
            template.RecurrencePattern,
            template.RecurrenceEndDate,
            template.MaxGrade,
            template.PassingThreshold,
            template.TrackingMode,
            template.CreatedByUserId,
            template.CreateAt,
            Scopes = template.Scopes.Select(MapScopeToDto).ToList(),
            OccurrenceCount = occurrenceCount,
            StudentsAffected = studentsAffected,
            PerStudentDetailIncluded = perStudentDetailIncluded,
            AuditLogCount = auditLogs.Count,
            AuditLogs = auditSummary,
            CapturedAt = DateTime.UtcNow,
        };
        return JsonSerializer.Serialize(snapshot);
    }

    private static string BuildStopRecurrenceSnapshotJson(
        AssignmentTemplate template, AssignmentOccurrence? lastOccurrence)
    {
        var snapshot = new
        {
            template.Id,
            template.Name,
            template.NameAr,
            template.RecurrencePattern,
            LastOccurrenceId = lastOccurrence?.Id,
            LastOccurrenceNumber = lastOccurrence?.OccurrenceNumber,
            LastOccurrenceDueDate = lastOccurrence?.DueDate,
            CapturedAt = DateTime.UtcNow,
        };
        return JsonSerializer.Serialize(snapshot);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS — PHASE 3 (status validation, audit, mappers, counts)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the next future occurrence for a template (DueDate &gt;= today).
    /// For non-recurring templates, this is either the only existing occurrence (if it
    /// hasn't passed) or null. Used by AddScopesAsync to pick the obligation insertion target.
    /// </summary>
    private async Task<AssignmentOccurrence?> GetNextFutureOccurrenceAsync(
        long teacherId, long templateId)
    {
        // Reuse GetLatestOccurrenceAsync only when the template is non-recurring with one
        // occurrence. For recurring templates we want the earliest future one. Currently
        // there's no dedicated repo method for this — we use the latest occurrence as a
        // reasonable proxy (newest-numbered = most-recent due date for monotonic schedulers).
        // A precise "earliest future occurrence" repo method should be added if the
        // monotonicity assumption ever breaks.
        var latest = await _unitOfWork.ExamHomeworkRepo
            .GetLatestOccurrenceAsync(templateId, teacherId);

        if (latest is null) return null;

        // DueDate is a calendar day, so "still in the future" is judged against the TEACHER's
        // today — UtcNow.Date is still yesterday between midnight and 2-3 AM Cairo, which made
        // an occurrence that was already due look like a valid insertion target.
        return latest.DueDate >= _timeZoneService.GetTeacherLocalDate(teacherId) ? latest : null;
    }

    /// <summary>Counts grade-pending obligations for an occurrence.</summary>
    private async Task<int> CountGradePendingAsync(long teacherId, long occurrenceId)
    {
        // Reuses the filtered index IX_StudentAssignmentObligations_PendingGrades via the
        // same repo method that the Grade Entry View uses. Page size of 1 means we pay
        // for the count, not for materializing rows. (A dedicated CountGradePendingAsync
        // repo method would be a future optimization if profiling shows this is a hotspot.)
        var (_, totalCount) = await _unitOfWork.ExamHomeworkRepo
            .GetGradeEntryViewPagedAsync(teacherId, occurrenceId, search: null, page: 1, pageSize: 1);
        return totalCount;
    }

    /// <summary>Counts obligations whose grade is below the snapshotted passing threshold.</summary>
    private async Task<int> CountBelowPassingAsync(long teacherId, long occurrenceId, decimal passingThreshold)
    {
        // The Tracking View paged method supports a "belowPassingGrade" boolean filter.
        // We invoke it page-1/size-1 to obtain the totalCount cheaply.
        var (_, totalCount) = await _unitOfWork.ExamHomeworkRepo
            .GetTrackingViewPagedAsync(
                teacherId, occurrenceId,
                search: null,
                statusFilter: null,
                missingEntries: null,
                gradeAboveThreshold: null,
                gradeBelowThreshold: null,
                belowPassingGrade: true,
                gradeEntered: null,
                page: 1, pageSize: 1);
        return totalCount;
    }

    /// <summary>
    /// Common path for both UpdateExamStatusAsync and UpdateHomeworkStatusAsync.
    /// Applies the status, persists the audit log, and returns the canonical row.
    /// </summary>
    private async Task<Result<TrackingViewRowDto>> ApplyStatusUpdateAsync(
        StudentAssignmentObligation obligation,
        AssignmentOccurrence occurrence,
        ObligationStatus newStatus,
        decimal? gradeValue,
        byte[] rowVersion,
        long actingUserId,
        long teacherId,
        long occurrenceId)
    {
        DateTime utcNow = DateTime.UtcNow;
        var audit = BuildAuditLogEntry(obligation, newStatus, gradeValue, occurrence, actingUserId, utcNow);

        ApplyStatusToObligation(obligation, newStatus, gradeValue, actingUserId, utcNow);

        await _unitOfWork.ExamHomeworkRepo.UpdateObligationAsync(obligation);
        await _unitOfWork.ExamHomeworkRepo.AddAuditLogAsync(audit);
        _unitOfWork.ExamHomeworkRepo.SetObligationOriginalRowVersion(obligation, rowVersion);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<TrackingViewRowDto>.Failure(
                _localizer, "ObligationConcurrencyConflict", HttpStatusCode.Conflict);
        }

        // Phase 5: Notify messaging engine after the save succeeds.
        // ExamHomeworkNotifier.SafeDispatchAsync swallows its own errors — a
        // messaging failure never fails an already-committed status update.
        // Scope: null for sessionId/sessionGroupId — assignment scopes are multi-valued;
        // the dispatcher matches TriggerScope.All triggers only. Documented limitation.
        await NotifyOnStatusUpdateAsync(obligation, occurrence, newStatus, gradeValue);
        return Result<TrackingViewRowDto>.Success(
                    BuildTrackingViewRowDtoFromObligation(obligation, occurrence),
                    _localizer, "StatusUpdated");
    }

    /// <summary>Mutates the obligation in-place with the new status/grade/scan flags.</summary>
    private static void ApplyStatusToObligation(
        StudentAssignmentObligation obligation,
        ObligationStatus newStatus,
        decimal? gradeValue,
        long actingUserId,
        DateTime utcNow)
    {
        obligation.Status = newStatus;
        obligation.GradeValue = gradeValue;
        obligation.IsGradeEntered = gradeValue.HasValue;
        obligation.LastUpdatedByUserId = actingUserId;
        obligation.UpdatedAt = utcNow;
        // MarkedByScan / ScannedAt are NOT touched on manual updates — they remain whatever
        // the scan path set them to (or false/null).
    }

    /// <summary>
    /// Constructs an audit-log entry capturing the full delta of a single obligation update.
    /// Snapshots the occurrence's grading config so historical reports remain reproducible
    /// even if the template's MaxGrade is later edited.
    /// </summary>
    private static StudentObligationAuditLog BuildAuditLogEntry(
        StudentAssignmentObligation obligation,
        ObligationStatus newStatus,
        decimal? newGradeValue,
        AssignmentOccurrence occurrence,
        long actingUserId,
        DateTime utcNow,
        string? changeReason = null)
        => new()
        {
            StudentObligationId = obligation.Id,
            TeacherId = obligation.TeacherId,
            OldStatus = obligation.Status,
            NewStatus = newStatus,
            OldGradeValue = obligation.GradeValue,
            NewGradeValue = newGradeValue,
            MaxGradeSnapshot = occurrence.MaxGradeSnapshot,
            PassingThresholdSnapshot = occurrence.PassingThresholdSnapshot,
            ChangeReason = changeReason,
            ChangedByUserId = actingUserId,
            ChangedAt = utcNow,
            CreateAt = utcNow,
        };

    /// <summary>
    /// Validates that a status value is allowed for an exam occurrence.
    /// Allowed: Pending, Attended, AttendedWithGrade, DidNotAttend.
    /// </summary>
    private static bool IsValidExamStatus(ObligationStatus status) =>
        status is ObligationStatus.Pending
              or ObligationStatus.Attended
              or ObligationStatus.AttendedWithGrade
              or ObligationStatus.DidNotAttend;

    /// <summary>
    /// Validates that a status value is allowed for a homework occurrence given its
    /// snapshotted TrackingMode.
    ///   - CompletionOnly: Pending, Done, NotDone
    ///   - Graded: Pending, DoneWithoutGrade, DoneWithGrade, NotDone
    /// </summary>
    private static bool IsValidHomeworkStatus(ObligationStatus status, HomeworkTrackingMode? mode)
    {
        if (mode == HomeworkTrackingMode.CompletionOnly)
        {
            return status is ObligationStatus.Pending
                          or ObligationStatus.Done
                          or ObligationStatus.NotDone;
        }
        if (mode == HomeworkTrackingMode.Graded)
        {
            return status is ObligationStatus.Pending
                          or ObligationStatus.DoneWithoutGrade
                          or ObligationStatus.DoneWithGrade
                          or ObligationStatus.NotDone;
        }
        return false;
    }

    /// <summary>
    /// Validates the grade value for an exam status transition.
    /// Returns null on success or a message key on failure.
    /// </summary>
    private static string? ValidateExamGrade(
        ObligationStatus status, decimal? gradeValue, decimal? maxGrade)
    {
        if (status == ObligationStatus.AttendedWithGrade)
        {
            if (!gradeValue.HasValue) return "GradeRequiredForAttendedWithGrade";
            if (!maxGrade.HasValue) return "OccurrenceMissingMaxGrade";
            if (gradeValue.Value < 0m || gradeValue.Value > maxGrade.Value)
                return "GradeOutOfRange";
        }
        else if (gradeValue.HasValue)
        {
            // Grade supplied for a non-grade status — reject.
            return "GradeNotAllowedForStatus";
        }
        return null;
    }

    /// <summary>
    /// Validates the grade value for a homework status transition.
    /// Returns null on success or a message key on failure.
    /// </summary>
    private static string? ValidateHomeworkGrade(
        ObligationStatus status, decimal? gradeValue, HomeworkTrackingMode? mode)
    {
        if (status == ObligationStatus.DoneWithGrade)
        {
            if (mode != HomeworkTrackingMode.Graded) return "GradeNotAllowedForCompletionOnly";
            if (!gradeValue.HasValue) return "GradeRequiredForDoneWithGrade";
            if (gradeValue.Value < 0m) return "GradeOutOfRange";
        }
        else if (gradeValue.HasValue)
        {
            return "GradeNotAllowedForStatus";
        }
        return null;
    }

    /// <summary>
    /// Maps the Domain projection <c>TrackingViewRow</c> to the Application DTO.
    /// </summary>
    private static TrackingViewRowDto MapTrackingViewRowToDto(TrackingViewRow r) => new()
    {
        ObligationId = r.ObligationId,
        TeacherStudentId = r.TeacherStudentId,
        StudentName = r.StudentName,
        StudentCode = r.StudentCode,
        Status = r.Status,
        GradeValue = r.GradeValue,
        IsGradeEntered = r.IsGradeEntered,
        IsBelowPassing = r.IsBelowPassing,
        MarkedByScan = r.MarkedByScan,
        ScannedAt = r.ScannedAt,
        UpdatedAt = r.UpdatedAt,
        RowVersion =Convert.ToBase64String( r.ObligationRowVersion),
    };

    /// <summary>
    /// Builds a <see cref="TrackingViewRowDto"/> from a tracked obligation entity (post-update).
    /// Used by the status / grade endpoints to return the canonical row to the client.
    /// </summary>
    private static TrackingViewRowDto BuildTrackingViewRowDtoFromObligation(
        StudentAssignmentObligation obligation,
        AssignmentOccurrence occurrence)
    {
        bool isBelowPassing = occurrence.PassingThresholdSnapshot.HasValue
                           && obligation.GradeValue.HasValue
                           && obligation.GradeValue.Value < occurrence.PassingThresholdSnapshot.Value;

        return new TrackingViewRowDto
        {
            ObligationId = obligation.Id,
            TeacherStudentId = obligation.TeacherStudentId,
            // Student name/code are not loaded on the tracked entity by default.
            // Caller (front-end) typically already has these from the picker; if not,
            // a small re-read could be added. For now we leave them empty rather than
            // forcing an extra round-trip on the hot path.
            StudentName = obligation.TeacherStudent?.StudentName ?? string.Empty,
            StudentCode = obligation.TeacherStudent?.StudentCode ?? string.Empty,
            Status = obligation.Status,
            GradeValue = obligation.GradeValue,
            IsGradeEntered = obligation.IsGradeEntered,
            IsBelowPassing = isBelowPassing,
            MarkedByScan = obligation.MarkedByScan,
            ScannedAt = obligation.ScannedAt,
            UpdatedAt = obligation.UpdatedAt,
RowVersion = Convert.ToBase64String(obligation.RowVersion),
        };
    }
    /// <summary>
    /// Phase 5: dispatches the appropriate notifier after ApplyStatusUpdateAsync commits.
    /// Scope: null for both — templates span multiple scopes; dispatcher matches TriggerScope.All only.
    /// </summary>
    private async Task NotifyOnStatusUpdateAsync(
        StudentAssignmentObligation obligation,
        AssignmentOccurrence occurrence,
        ObligationStatus newStatus,
        decimal? gradeValue)
    {
        bool isExam = occurrence.Template.AssignmentType == AssignmentType.Exam;
        string assignmentName = occurrence.Template.Name;

        if (isExam)
        {
            switch (newStatus)
            {
                case ObligationStatus.AttendedWithGrade when gradeValue.HasValue:
                    await _examHomeworkNotifier.OnExamGradeRecordedAsync(
                        obligation.TeacherId, obligation.TeacherStudentId,
                        sessionId: null, sessionGroupId: null,
                        assignmentName, gradeValue.Value, occurrence.MaxGradeSnapshot);
                    break;
                case ObligationStatus.DidNotAttend:
                    await _examHomeworkNotifier.OnExamNotAttendedAsync(
                        obligation.TeacherId, obligation.TeacherStudentId,
                        sessionId: null, sessionGroupId: null,
                        assignmentName);
                    break;
            }
        }
        else
        {
            switch (newStatus)
            {
                case ObligationStatus.Done:
                case ObligationStatus.DoneWithoutGrade:
                    await _examHomeworkNotifier.OnHomeworkStatusRecordedAsync(
                        obligation.TeacherId, obligation.TeacherStudentId,
                        sessionId: null, sessionGroupId: null,
                        assignmentName, isDone: true);
                    break;
                case ObligationStatus.DoneWithGrade when gradeValue.HasValue:
                    await _examHomeworkNotifier.OnHomeworkStatusRecordedAsync(
                        obligation.TeacherId, obligation.TeacherStudentId,
                        sessionId: null, sessionGroupId: null,
                        assignmentName, isDone: true);
                    await _examHomeworkNotifier.OnHomeworkGradeRecordedAsync(
                        obligation.TeacherId, obligation.TeacherStudentId,
                        sessionId: null, sessionGroupId: null,
                        assignmentName, gradeValue.Value, occurrence.MaxGradeSnapshot);
                    break;
                case ObligationStatus.NotDone:
                    await _examHomeworkNotifier.OnHomeworkStatusRecordedAsync(
                        obligation.TeacherId, obligation.TeacherStudentId,
                        sessionId: null, sessionGroupId: null,
                        assignmentName, isDone: false);
                    break;
            }
        }
    }
    /// <summary>
    /// Phase 5: dispatches the grade-recorded notifier after EnterGradeAsync commits.
    /// Two-step path: student was already marked Attended/DoneWithoutGrade; grade entered separately.
    /// </summary>
    private async Task NotifyOnGradeEntryAsync(
        StudentAssignmentObligation obligation,
        AssignmentOccurrence occurrence,
        decimal gradeValue)
    {
        bool isExam = occurrence.Template.AssignmentType == AssignmentType.Exam;
        string assignmentName = occurrence.Template.Name;

        if (isExam)
            await _examHomeworkNotifier.OnExamGradeRecordedAsync(
                obligation.TeacherId, obligation.TeacherStudentId,
                sessionId: null, sessionGroupId: null,
                assignmentName, gradeValue, occurrence.MaxGradeSnapshot);
        else
            await _examHomeworkNotifier.OnHomeworkGradeRecordedAsync(
                obligation.TeacherId, obligation.TeacherStudentId,
                sessionId: null, sessionGroupId: null,
                assignmentName, gradeValue, occurrence.MaxGradeSnapshot);
    }
    private Result<AssignmentTemplateDto> Failure(string key) =>
        Result<AssignmentTemplateDto>.Failure(_localizer, key, HttpStatusCode.BadRequest);
    /// <summary>
    /// Maps one offline-exam row to its student DTO. <paramref name="subject"/> is the publishing
    /// teacher's subject (F3 — same for every row in a list). <paramref name="rank"/> is the student's
    /// leaderboard position in this exam's cohort (F1), or null when the student's exam isn't graded.
    /// F2 max grade is the occurrence's snapshotted max — the same denominator as <c>ScorePercentage</c>.
    /// </summary>
    private static StudentOfflineExamListItemDto MapToStudentOfflineExamListItemDto(
        StudentOfflineExamRow row, string? subject, StudentExamRankRow? rank) => new()
    {
        ExamId = row.OccurrenceId,
        ExamName = row.ExamName,
        Description = row.Notes,
        Date = DateOnly.FromDateTime(row.DueDate),
        Score = row.GradeValue,
        ScorePercentage = ComputeScorePercentage(row.GradeValue, row.MaxGradeSnapshot),
        MaxGrade = row.MaxGradeSnapshot,
        Subject = subject,
        Rank = rank?.Rank,
        GroupSize = rank?.GroupSize,
        Status = row.Status,
    };

    /// <summary>Null when not gradeable (no grade entered yet, or MaxGradeSnapshot missing/zero).</summary>
    private static decimal? ComputeScorePercentage(decimal? score, decimal? maxScore)
    {
        if (score is null || maxScore is null || maxScore.Value == 0m) return null;
        return Math.Round(score.Value / maxScore.Value * 100m, 2);
    }

    // ══════════════════════════════════════════════════════════════════════
    // STUDENT-FACING (mobile app)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<StudentOfflineExamListItemDto>>>> GetMyOfflineExamsAsync(
        long teacherId, long teacherStudentId, string? studentLanguage, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var (rows, totalCount) = await _unitOfWork.ExamHomeworkRepo
            .GetOfflineExamsForStudentPagedAsync(teacherId, teacherStudentId, page, pageSize);

        // F3 — resolve the publishing teacher's subject ONCE (per-teacher, not per-row). Canonical
        // pattern (StudentUserService.BuildDashboardTeacherDtoFromBatch): prefer the ministry subject
        // name (language-aware), fall back to the teacher's free-text CustomSubject.
        string? subject = await ResolveTeacherSubjectAsync(teacherId, studentLanguage);

        // F1 — batch the leaderboard ranks for this page's occurrences in ONE query (no N+1). Only
        // graded occurrences come back; ungraded ones map to null rank/group-size below.
        var rankRows = await _unitOfWork.ExamHomeworkRepo
            .GetStudentExamRanksAsync(teacherId, teacherStudentId, rows.Select(r => r.OccurrenceId));
        var rankByOccurrence = rankRows.ToDictionary(r => r.OccurrenceId);

        var dtos = rows
            .Select(r => MapToStudentOfflineExamListItemDto(
                r, subject, rankByOccurrence.GetValueOrDefault(r.OccurrenceId)))
            .ToList();

        var response = new PaginatedResponse<List<StudentOfflineExamListItemDto>>
        {
            data = dtos,
            page = page,
            pageSize = pageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
        };

        return Result<PaginatedResponse<List<StudentOfflineExamListItemDto>>>.Success(
            response, _localizer, "OfflineExamsRetrieved");
    }

    /// <summary>
    /// F3 — resolves the teacher's subject for student-facing display. Replicates the canonical
    /// language-aware pattern in <c>StudentUserService.BuildDashboardTeacherDtoFromBatch</c>: the
    /// ministry <see cref="Domain.Entities.Subject"/> name (by the student's language) wins, falling
    /// back to the teacher's free-text <see cref="Domain.Entities.Teacher.CustomSubject"/>; null when
    /// neither is on file. One query per request (the whole list is one teacher).
    /// </summary>
    private async Task<string?> ResolveTeacherSubjectAsync(long teacherId, string? studentLanguage)
    {
        var row = await _unitOfWork.ExamHomeworkRepo.GetTeacherSubjectDisplayAsync(teacherId);
        if (row is null) return null;

        string? subject = row.CustomSubject;
        bool isArabic = string.Equals(studentLanguage?.Trim(), "ar", StringComparison.OrdinalIgnoreCase);
        string? ministryName = isArabic ? row.SubjectNameAr : row.SubjectNameEn;
        if (!string.IsNullOrWhiteSpace(ministryName))
            subject = ministryName;

        return subject;
    }
}
