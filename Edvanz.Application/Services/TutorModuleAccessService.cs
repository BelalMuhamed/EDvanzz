using Edvanz.Application.Dtos;
using Edvanz.Application.ServiceContract;
using Edvanz.Application.Dtos.AuditTrial;
using Edvanz.Application.Dtos.Subscription;
using Edvanz.Application.IservicesContract;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Edvanz.Application.Services;

/// <summary>
/// Default implementation of <see cref="ITutorModuleAccessService"/>
/// (REQ-ADM-034 through REQ-ADM-038 / REQ-ADM-055 / BR-ADM-010).
///
/// Transaction discipline:
///   - Every write goes through one BeginTransactionAsync / CommitAsync block.
///   - Inside the block, the order is:
///       (a) repo write (grant / revoke)
///       (b) audit-trail insert (one row per actual change)
///       (c) IUserAuthInvalidationService call (stamp bumps queued, cache dropped)
///       (d) SaveChangesAsync
///       (e) CommitAsync
///   - A rollback rolls back (a), (b), and the stamp bumps in (c). The cache DEL
///     in (c) is fire-and-forget at the Redis level — at worst the next request
///     re-builds from the DB, which is safe.
/// </summary>
public class TutorModuleAccessService : ITutorModuleAccessService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly IUserAuthInvalidationService _authInvalidation;
    private readonly IAudittrialService _auditTrail;
    private readonly ITeacherUsageRollupService _usageRollup;
    private readonly ILogger<TutorModuleAccessService> _logger;

    public TutorModuleAccessService(
        IUnitOfWork unitOfWork,
        IStringLocalizer<Messages> localizer,
        IUserAuthInvalidationService authInvalidation,
        IAudittrialService auditTrail,
        ITeacherUsageRollupService usageRollup,
        ILogger<TutorModuleAccessService> logger)
    {
        _usageRollup = usageRollup;
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _authInvalidation = authInvalidation;
        _auditTrail = auditTrail;
        _logger = logger;
    }

    /// <summary>
    /// Best-effort refresh of the admin usage model's entitlement cache. Swallows and logs, because
    /// the grant itself has already committed and must stand regardless.
    /// </summary>
    private async Task RefreshUsageEntitlementAsync(long teacherId)
    {
        try
        {
            await _usageRollup.RefreshEntitlementAsync(teacherId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Module grant committed for teacher {TeacherId} but the admin entitlement cache could "
                + "not be refreshed; tonight's rollup will correct it.", teacherId);
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> GrantAsync(ModuleGrantRequest request, long adminUserId)
    {
        // ── 1. Validate inputs ──
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(request.TeacherId);
        if (teacher is null)
            return Result<string>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        var module = await _unitOfWork.GetRepository<Module, long>()
            .GetByIdAsync(request.ModuleId);
        if (module is null)
            return Result<string>.Failure(_localizer, "ModuleNotFound", HttpStatusCode.NotFound);

        // ── 2. Transaction ──
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            bool changed = await _unitOfWork.ModuleTeacherRepo!
                .GrantModuleAsync(request.TeacherId, request.ModuleId);

            if (!changed)
            {
                // Idempotent no-op. Commit anyway (no-op commit is cheap) so the
                // surrounding transaction semantics are consistent across paths.
                await _unitOfWork.CommitAsync();
                return Result<string>.Success(null, _localizer, "ModuleAlreadyGranted");
            }

            // ── 3. Audit trail (REQ-ADM-055) ──
            await _auditTrail.RecordAuditTrailAsync(new CreateAuditTrailDto
            {
                teacherId = request.TeacherId,
                assistantId = null,
                actionType = ActionType.Add,
                moduleId = request.ModuleId,
                desc = $"SuperAdmin (UserId={adminUserId}) granted module '{module.Name}' to tutor."
            });

            // ── 4. Fan-out invalidation (BR-ADM-010 / REQ-ADM-035) ──
            // Bumps SecurityStamp + drops Redis snapshot for the tutor AND
            // every assistant under that tutor. Must run BEFORE SaveChangesAsync
            // so the stamp bumps join this transaction.
            await _authInvalidation.InvalidateTutorAndAssistantsAsync(request.TeacherId);

            await _unitOfWork.SaveChangesAsync();

            // The admin usage model caches what each teacher is ENTITLED to. Refresh it now rather
            // than leaving a module granted this morning to read as an unused entitlement until
            // tonight's rollup. Best-effort: a derived cache must never fail an admin's grant.
            await RefreshUsageEntitlementAsync(request.TeacherId);
            await _unitOfWork.CommitAsync();

            return Result<string>.Success(null, _localizer, "ModuleGranted");
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackAsync();
            _logger.LogError(ex,
                "GrantAsync failed for teacher {TeacherId} module {ModuleId}",
                request.TeacherId, request.ModuleId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> RevokeAsync(ModuleRevokeRequest request, long adminUserId)
    {
        // ── 1. Validate inputs ──
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(request.TeacherId);
        if (teacher is null)
            return Result<string>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        var module = await _unitOfWork.GetRepository<Module, long>()
            .GetByIdAsync(request.ModuleId);
        if (module is null)
            return Result<string>.Failure(_localizer, "ModuleNotFound", HttpStatusCode.NotFound);

        // ── 2. Transaction ──
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            bool changed = await _unitOfWork.ModuleTeacherRepo!
                .RevokeModuleAsync(request.TeacherId, request.ModuleId);

            if (!changed)
            {
                await _unitOfWork.CommitAsync();
                return Result<string>.Success(null, _localizer, "ModuleAlreadyRevoked");
            }

            // ── 3. Audit trail (REQ-ADM-055) ──
            await _auditTrail.RecordAuditTrailAsync(new CreateAuditTrailDto
            {
                teacherId = request.TeacherId,
                assistantId = null,
                actionType = ActionType.Delete,
                moduleId = request.ModuleId,
                desc = $"SuperAdmin (UserId={adminUserId}) revoked module '{module.Name}' from tutor."
            });

            // ── 4. Fan-out invalidation ──
            await _authInvalidation.InvalidateTutorAndAssistantsAsync(request.TeacherId);

            await _unitOfWork.SaveChangesAsync();

            // The admin usage model caches what each teacher is ENTITLED to. Refresh it now rather
            // than leaving a module granted this morning to read as an unused entitlement until
            // tonight's rollup. Best-effort: a derived cache must never fail an admin's grant.
            await RefreshUsageEntitlementAsync(request.TeacherId);
            await _unitOfWork.CommitAsync();

            return Result<string>.Success(null, _localizer, "ModuleRevoked");
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackAsync();
            _logger.LogError(ex,
                "RevokeAsync failed for teacher {TeacherId} module {ModuleId}",
                request.TeacherId, request.ModuleId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> BulkReplaceAsync(
        TutorModulesReplaceRequest request, long adminUserId)
    {
        // ── 1. Validate inputs ──
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(request.TeacherId);
        if (teacher is null)
            return Result<string>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        var desiredIds = (request.ModuleIds ?? new List<long>())
            .Distinct()
            .ToHashSet();

        // Validate every desired id resolves to an existing Module.
        // Cheap COUNT query; on mismatch we fail fast with a clear message.
        if (desiredIds.Count > 0)
        {
            int validCount = await _unitOfWork.GetRepository<Module, long>()
                .CountAsync(m => desiredIds.Contains(m.Id));

            if (validCount != desiredIds.Count)
                return Result<string>.Failure(
                    _localizer, "OneOrMoreModulesInvalid", HttpStatusCode.BadRequest);
        }

        // ── 2. Diff current vs desired ──
        var currentIds = (await _unitOfWork.ModuleTeacherRepo!
            .GetTutorModuleIdsAsync(request.TeacherId))
            .ToHashSet();

        var toGrant = desiredIds.Except(currentIds).ToList();
        var toRevoke = currentIds.Except(desiredIds).ToList();

        // Nothing changed → idempotent no-op.
        if (toGrant.Count == 0 && toRevoke.Count == 0)
            return Result<string>.Success(null, _localizer, "NoChange");

        // ── 3. Apply diff inside one transaction ──
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            // Load the affected Module names once for audit-trail descriptions.
            // Single round-trip for all changed module ids.
            var affectedModuleIds = toGrant.Concat(toRevoke).ToList();
            var moduleNames = await _unitOfWork.GetRepository<Module, long>()
                .GetAsync(m => affectedModuleIds.Contains(m.Id));
            var moduleNameById = moduleNames.ToDictionary(m => m.Id, m => m.Name);

            // Grants
            foreach (long moduleId in toGrant)
            {
                await _unitOfWork.ModuleTeacherRepo!
                    .GrantModuleAsync(request.TeacherId, moduleId);

                await _auditTrail.RecordAuditTrailAsync(new CreateAuditTrailDto
                {
                    teacherId = request.TeacherId,
                    assistantId = null,
                    actionType = ActionType.Add,
                    moduleId = moduleId,
                    desc = $"SuperAdmin (UserId={adminUserId}) granted module " +
                           $"'{ResolveModuleName(moduleNameById, moduleId)}' to tutor (bulk-replace)."
                });
            }

            // Assistant user-ids under this tutor — computed once (not per module),
            // reused by the BR-ADM-010 cascade below for every revoked module.
            // Includes assistants of any AccountStatus (Active/Inactive/Suspended):
            // a deactivated assistant's stale grants must be cleaned up too, or
            // reactivating them later would silently restore access to a module
            // the tutor no longer has.
            var assistantUserIds = toRevoke.Count > 0
                ? (await _unitOfWork.AssistantRepo
                    .GetUserIdsByTeacherAccountIdAsync(request.TeacherId)).ToList()
                : new List<long>();

            // Revokes
            foreach (long moduleId in toRevoke)
            {
                await _unitOfWork.ModuleTeacherRepo!
                    .RevokeModuleAsync(request.TeacherId, moduleId);

                // ── BR-ADM-010 cascade ──────────────────────────────────────────
                // A revoked module must not leave any assistant of this tutor still
                // holding permissions that belong to it — otherwise an assistant
                // could retain functional access the tutor itself no longer has.
                int removedPermissionCount = 0;
                if (assistantUserIds.Count > 0)
                {
                    var modulePermissionIds = await _unitOfWork.permissionRepo
                        .GetPermissionIdsByModuleIdsAsync(new[] { moduleId });

                    if (modulePermissionIds.Count > 0)
                    {
                        var rowsToRemove = await _unitOfWork.UsersPermissions
                            .GetExistingUserPermissionsAsync(
                                assistantUserIds, modulePermissionIds.ToList());

                        if (rowsToRemove.Count > 0)
                        {
                            await _unitOfWork.UsersPermissions.DeleteRangeAsync(rowsToRemove);
                            removedPermissionCount = rowsToRemove.Count;
                        }
                    }
                }

                var cascadeNote = removedPermissionCount > 0
                    ? $" {removedPermissionCount} assistant permission grant(s) for this " +
                      "module were also removed."
                    : string.Empty;

                await _auditTrail.RecordAuditTrailAsync(new CreateAuditTrailDto
                {
                    teacherId = request.TeacherId,
                    assistantId = null,
                    actionType = ActionType.Delete,
                    moduleId = moduleId,
                    desc = $"SuperAdmin (UserId={adminUserId}) revoked module " +
                           $"'{ResolveModuleName(moduleNameById, moduleId)}' from tutor " +
                           $"(bulk-replace).{cascadeNote}"
                });
            }

            // ── 4. Single fan-out invalidation for the whole diff ──
            // We call this ONCE regardless of how many modules changed — the
            // affected user-id set (tutor + assistants) is the same for every
            // module change on this tutor.
            await _authInvalidation.InvalidateTutorAndAssistantsAsync(request.TeacherId);

            await _unitOfWork.SaveChangesAsync();

            // The admin usage model caches what each teacher is ENTITLED to. Refresh it now rather
            // than leaving a module granted this morning to read as an unused entitlement until
            // tonight's rollup. Best-effort: a derived cache must never fail an admin's grant.
            await RefreshUsageEntitlementAsync(request.TeacherId);
            await _unitOfWork.CommitAsync();

            return Result<string>.Success(null, _localizer, "ModulesReplaced");
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackAsync();
            _logger.LogError(ex,
                "BulkReplaceAsync failed for teacher {TeacherId}", request.TeacherId);
            throw;
        }
    }

    // ════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ════════════════════════════════════════════════

    private static string ResolveModuleName(Dictionary<long, string> map, long moduleId) =>
        map.TryGetValue(moduleId, out string? name) ? name : $"#{moduleId}";
    /// <inheritdoc />
    public async Task<Result<List<ModuleInfoDto>>> GetCatalogueAsync()
    {
        var modules = await _unitOfWork.GetRepository<Module, long>().GetAllAsync();

        var dtos = modules
            .OrderBy(m => m.Id)
            .Select(m => new ModuleInfoDto { Id = m.Id, Name = m.Name })
            .ToList();

        return Result<List<ModuleInfoDto>>.Success(dtos, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<List<ModuleInfoDto>>> GetTutorModulesAsync(long teacherId)
    {
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<List<ModuleInfoDto>>.Failure(
                _localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        var modules = await _unitOfWork.ModuleTeacherRepo!.GetModulesPerTeacher(teacherId);

        var dtos = modules
            .OrderBy(m => m.Id)
            .Select(m => new ModuleInfoDto { Id = m.Id, Name = m.Name })
            .ToList();

        return Result<List<ModuleInfoDto>>.Success(dtos, _localizer);
    }
}