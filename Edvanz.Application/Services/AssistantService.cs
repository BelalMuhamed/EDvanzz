using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.AssistantDtos;
using Edvanz.Application.Dtos.ModulesPermissions;
using Edvanz.Application.Dtos.PermissionsDtos;
using Edvanz.Application.Dtos.TemplatePermissionsDtos;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Exceptions;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Edvanz.Domain.ServiceContract;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Text;

namespace Edvanz.Application.Services
{
    public class AssistantService : IAssistantService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IStringLocalizer<Messages> localizer;
        private readonly IUserPermissionService userPermissionService;
        private readonly IPasswordService passwordService;
        private readonly ICurrentUserService currentUserService;
        private readonly IPaymentService _paymentService;
        private readonly IUserAuthInvalidationService _authInvalidation;
        private readonly ISubscriptionGateService _subscriptionGate;

        public AssistantService(IUnitOfWork unitOfWork, IStringLocalizer<Messages> localizer, IUserPermissionService userPermissionService, IPasswordService passwordService, ICurrentUserService _currentUserService, IPaymentService paymentService, IUserAuthInvalidationService authInvalidation, ISubscriptionGateService subscriptionGate)
        {
            _unitOfWork = unitOfWork;
            this.localizer = localizer;
            this.userPermissionService = userPermissionService;
            this.passwordService = passwordService;
            currentUserService = _currentUserService;
            this._paymentService = paymentService;
            _authInvalidation = authInvalidation;
            _subscriptionGate = subscriptionGate;
        }
        public async Task<Result<PaginatedResponse<List<AssistantListDto>>>> GetAssistantListPerTeacher(
            AssistantPerTeacherFilterDto req)
        {
            try
            {
                // Tenant isolation: a teacher/assistant may only list THEIR teacher's assistants.
                // Ignore any teacherId supplied in the request and use the caller's own scope;
                // SuperAdmin may query the requested teacherId.
                if (!CallerIsSuperAdmin())
                {
                    var callerTeacherId = await ResolveCallerTeacherIdAsync();
                    if (callerTeacherId is null)
                        return Result<PaginatedResponse<List<AssistantListDto>>>.Failure(
                            localizer, "TeacherNotFound", HttpStatusCode.NotFound);
                    req.teacherId = callerTeacherId.Value;
                }

                var (assistants, totalCount) = await _unitOfWork.AssistantRepo
                    .GetListAssistantsPerTeacher(
                        teacherId: req.teacherId,
                        isAcitve: req.isAcitve,
                        fullName: req.fullName,
                        username: req.username,
                        isAssignedToTeacher: null, 
                        sortby: req.sortBy,
                        sortDirection: req.sortDirection,
                        page: req.Page,
                        pageSize: req.PageSize
                    );

                
                var dtoList = assistants.Select(a => new AssistantListDto
                {
                    id = a.Id,
                    fullName = a.User.FullName,
                    username = a.User.Username,
                    email = a.User.Email,
                    phoneNumber = a.User.PhoneNumber ?? string.Empty,
                    isActive = (bool)a.User.IsActive,
                    teacherId = a.TeacherAccountId,
                    teacherName = a.Teacher.User.FullName,
                    accountStatus = a.AccountStatus.ToString(),
                    createdAt=a.CreateAt,
                    deletedAt=a.DeletedAt,
                    languagePreference = a.LanguagePreference,
                    updatedAt=a.UpdatedAt,
                }).ToList();

                
                var response = new PaginatedResponse<List<AssistantListDto>>
                {
                    totalCount = totalCount, 
                    page = req.Page,
                    pageSize = req.PageSize,
                    totalPages = (int)Math.Ceiling((double)dtoList.Count / req.PageSize),
                    data = dtoList
                };

                return Result<PaginatedResponse<List<AssistantListDto>>>.Success(response, localizer);
            }
            catch (Exception ex)
            {
                
                return Result<PaginatedResponse<List<AssistantListDto>>>.Failure(
                    localizer,
                    "ServerError",
                    HttpStatusCode.InternalServerError);
            }
        }
        /// <summary>
        /// Tenant guard: true iff the given assistant belongs to the caller's teacher scope
        /// (SuperAdmin always passes). Prevents one teacher from reading/mutating another teacher's
        /// assistants via the assistantId route parameter (cross-tenant IDOR). Callers should treat
        /// a false result as not-found so the assistant's existence is not leaked.
        /// </summary>
        private bool CallerIsSuperAdmin() =>
            string.Equals(currentUserService.Role, "SuperAdmin", StringComparison.Ordinal);

        /// <summary>Resolves the caller's teacher scope from the JWT (teacher's own id, or an
        /// assistant's owning teacher). Null when the caller maps to no teacher.</summary>
        private async Task<long?> ResolveCallerTeacherIdAsync()
        {
            var userId = currentUserService.UserId;
            if (userId is null) return null;

            // Center tier: resolve the acting teacher (X-Acting-Teacher-Id) validated against membership.
            if (string.Equals(currentUserService.Role, UserRoles.Center, StringComparison.Ordinal)
                || string.Equals(currentUserService.Role, UserRoles.CenterAssistant, StringComparison.Ordinal))
                return await currentUserService.ResolveActingTeacherIdAsync();

            long? callerTeacherId = (await _unitOfWork.Users.GetTeacherByUserIdAsync(userId.Value))?.Id;
            if (callerTeacherId is null)
            {
                var callerAsst = await _unitOfWork.AssistantRepo.GetAssistantWithUserIdAsync(userId.Value);
                callerTeacherId = callerAsst?.TeacherAccountId;
            }
            return callerTeacherId;
        }

        private async Task<bool> CallerOwnsAssistantAsync(Assistant assistant)
        {
            if (CallerIsSuperAdmin()) return true;
            var callerTeacherId = await ResolveCallerTeacherIdAsync();
            return callerTeacherId is not null && assistant.TeacherAccountId == callerTeacherId.Value;
        }

        public async Task<Result<AssistantDto>> GetByAssistantIdAsync(long id)
        {
            var assistant = await _unitOfWork.AssistantRepo.GetAssistantWithPermissionsAsync(id);

            if (assistant == null)
                return Result<AssistantDto>.Failure(localizer, "UserNotFound");

            if (!await CallerOwnsAssistantAsync(assistant))
                return Result<AssistantDto>.Failure(localizer, "UserNotFound", HttpStatusCode.NotFound);

            if (assistant.User == null)
                return Result<AssistantDto>.Failure(localizer, "UserDataNotFound");

            if (assistant.Teacher?.User == null)
                return Result<AssistantDto>.Failure(localizer, "TeacherDataNotFound");

            var groupedPermissions = assistant.User.Permissions
                .GroupBy(up => up.Permission.module)
                .Select(g => new ModulePermissionsDto
                {
                    id = g.Key.Id,
                    ModuleName = g.Key.Name,
                    permissions = g.Select(p => new PermissionDto
                    {
                        permissionId = p.Permission.Id,
                        permissionName = p.Permission.Name,
                        isRestricted = p.Permission.IsRestricted
                    }).ToList()
                }).ToList();
           List<ProfilePermissionDto> PermissionsProfile=new List<ProfilePermissionDto>();
             PermissionsProfile = assistant.PermissionProfiles.Select(p => new ProfilePermissionDto { profileId = p.TemplateId, profileName = p.template.Name }).ToList();
            var dto = new AssistantDto
            {
                id = assistant.Id,
                fullName = assistant.User.FullName,
                username = assistant.User.Username,
                email = assistant.User.Email,
                phoneNumber = assistant.User.PhoneNumber,
             
                teacherId = assistant.TeacherAccountId,
                teacherName = assistant.Teacher.User.FullName,
                createdAt = assistant.CreateAt,
                accountStatus = assistant.AccountStatus.ToString(),
                deletedAt = assistant.DeletedAt,
                updatedAt = assistant.UpdatedAt,
                languagePreference = assistant.LanguagePreference,
                userPermissions = groupedPermissions,
                assignedPermissionsProfiles = PermissionsProfile
            };

            return Result<AssistantDto>.Success(dto, localizer);
        }

        /// <summary>
        /// Creates a new assistant account under a teacher's space.
        ///
        /// Flow:
        ///   1.  Validate teacher exists and caller owns it
        ///   2.  Username / phone / email uniqueness check
        ///   3.  At least one permission source must be provided (permissionIds OR templates OR both)
        ///   4.  If individual permissionIds sent   -> validate they all exist in Permission table
        ///   5.  If permissionProfileIds sent       -> validate they all exist in Template table
        ///   6.  Create User row (UserType.Assistant) + first SaveChanges to get User.Id
        ///   7.  Create Assistant row               + second SaveChanges to get Assistant.Id
        ///   8.  Bulk-insert UsersPermission rows   (individual perms on the User entity)
        ///   9.  Bulk-insert TemplatePermissionsUsers rows (template links on the Assistant entity)
        ///   10. Final SaveChanges + Commit
        ///   11. Re-fetch with navigations and return string
        /// </summary>

       
        public async Task<Result<string?>> InitializeAssistantAsync(CreateAssistantDto dto)
        {
            var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(dto.teacherId);
            if (teacher is null)
                return Result<string?>.Failure(localizer, "TeacherNotFound");

            // Free-tier quota: assistants (default 0 → subscriber-only).
            if (!await _subscriptionGate.CanCreateAsync(
                    teacher.Id, ModuleQuotaKeys.Assistants,
                    () => _unitOfWork.AssistantRepo.CountByTeacherAccountIdAsync(teacher.Id)))
                return Result<string?>.Failure(
                    localizer, SubscriptionConstants.Messages.SubscriptionRequired, HttpStatusCode.Forbidden);

            var modules = await _unitOfWork.ModuleTeacherRepo.GetModulesPerTeacher(teacher.Id);

            if (modules == null || !modules.Any())
                return Result<string?>.Failure(localizer, "TeacherHaven'tAnyOpenModules");

            var moduleIds = modules.Select(m => m.Id);

            var availablePermissionIds =
                await _unitOfWork.permissionRepo.GetPermissionIdsByModuleIdsAsync(moduleIds);

            // Normalize optional fields: blank/whitespace -> null so they stay OUT of the filtered
            // unique phone index and the duplicate pre-check. Previously a blank phone was stored as
            // "" and the 2nd blank-phone user collided on the unique phone index -> "conflict with
            // existing data".
            var phone = string.IsNullOrWhiteSpace(dto.phoneNumber) ? null : dto.phoneNumber.Trim();
            var email = string.IsNullOrWhiteSpace(dto.email) ? null : dto.email.Trim();

            var existingUser = await _unitOfWork.Users.FindExistingUserByCredentialsAsync(
                phone ?? string.Empty,
                dto.username,
                email ?? string.Empty);

            if (existingUser is not null)
            {
                if (phone is not null && existingUser.PhoneNumber == phone)
                    return Result<string?>.Failure(localizer, "repeatedPhoneNumber");

                if (existingUser.Username == dto.username)
                    return Result<string?>.Failure(localizer, "repeatedUserName");

                if (email is not null && existingUser.Email == email)
                    return Result<string?>.Failure(localizer, "repeatedEmail");
            }

            if ((dto.permissionIds is not { Count: > 0 }) &&
                (dto.permissionProfileIds is not { Count: > 0 }))
                return Result<string?>.Failure(localizer, "AssistantMustHaveAtLeastOnePermission");

            HashSet<long> allPermissionIds;

            try
            {
                allPermissionIds = await ResolveAssistantPermissionsAsync(
                    dto.permissionIds,
                    dto.permissionProfileIds);
            }
            catch (Exception ex) when (ex.Message == "InvalidPermissions" || ex.Message == "InvalidTemplates")
            {
                return Result<string?>.Failure(localizer, "InvalidPermissions");
            }

            await ValidateTeacherScopeAsync(teacher.Id, allPermissionIds);

            var collectPermission = await _unitOfWork.permissionRepo
                .GetByPermissionNameAndModuleNameAsync("Collect", "Payment");

            var newUser = new User
            {
                UserType = UserType.Assistant,
                FullName = dto.fullName,
                Username = dto.username,
                Email = email,
                PhoneNumber = phone,
                PasswordHashed = passwordService.HashPassword(dto.password),
                IsActive = true,
                IsVerified = false,
                CreateAt = DateTime.UtcNow,
                CreateByUserId = currentUserService.UserId,
            };

            await _unitOfWork.BeginTransactionAsync();

            try
            {
                await _unitOfWork.Users.AddAsync(newUser);
                await _unitOfWork.SaveChangesAsync();

                var assistant = new Assistant
                {
                    UserId = newUser.Id,
                    TeacherAccountId = dto.teacherId,
                    AccountStatus = AccountStatus.Active,
                    CreateAt = DateTime.UtcNow,
                };

                await _unitOfWork.AssistantRepo.AddAsync(assistant);
                await _unitOfWork.SaveChangesAsync();

                if (allPermissionIds.Any())
                {
                    var userPermissions = allPermissionIds
                        .Select(pid => new UsersPermission
                        {
                            UserId = newUser.Id,
                            PermissionId = pid,
                        }).ToList();

                    await _unitOfWork.GetRepository<UsersPermission, long>()
                        .AddRangeAsync(userPermissions);
                }

                if (dto.permissionProfileIds is { Count: > 0 })
                {
                    var templateLinks = dto.permissionProfileIds
                        .Distinct()
                        .Select(tid => new TemplatePermissionsUsers
                        {
                            TemplateId = tid,
                            AssisstantId = assistant.Id,
                        }).ToList();

                    await _unitOfWork.GetRepository<TemplatePermissionsUsers, long>()
                        .AddRangeAsync(templateLinks);
                }

                if (collectPermission != null &&
                    allPermissionIds.Contains(collectPermission.Id))
                {
                    await _paymentService.EnsureAssistantWalletExistsAsync(
                        dto.teacherId,
                        assistant.Id,
                        newUser.Id);
                }

                await _unitOfWork.SaveChangesAsync();
                await _unitOfWork.CommitAsync();

                return Result<string?>.Success("Success", localizer);
            }
            catch (DbUpdateException ex) when (ResolveUserUniqueViolationKey(ex) is { } messageKey)
            {
                // A concurrent insert or a legacy unnormalized row can still trip a unique index
                // (phone/username/email). Map it to a friendly message instead of surfacing a raw
                // "conflict with existing data".
                await _unitOfWork.RollbackAsync();
                return Result<string?>.Failure(localizer, messageKey, HttpStatusCode.Conflict);
            }
            catch
            {
                await _unitOfWork.RollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Maps a SQL Server unique-key violation (2601/2627) on the Users table to the matching
        /// localized message key by inspecting the column embedded in the index name. Returns null
        /// when the exception is not a Users unique violation, so it rethrows unchanged.
        /// </summary>
        private static string? ResolveUserUniqueViolationKey(DbUpdateException ex)
        {
            var sql = ex.InnerException as Microsoft.Data.SqlClient.SqlException
                      ?? ex.GetBaseException() as Microsoft.Data.SqlClient.SqlException;

            if (sql is not { Number: 2601 or 2627 })
                return null;

            string message = sql.Message;

            if (message.Contains("PhoneNumber", StringComparison.OrdinalIgnoreCase))
                return "repeatedPhoneNumber";
            if (message.Contains("Username", StringComparison.OrdinalIgnoreCase))
                return "repeatedUserName";
            if (message.Contains("Email", StringComparison.OrdinalIgnoreCase))
                return "repeatedEmail";

            return null;
        }

        public async Task<Result<string?>> UpdateAssistantAsync(UpdateAssistantRequest dto)
        {
            var assistant = await _unitOfWork.AssistantRepo.GetByIdAsync(dto.assistantId);
            if (assistant is null)
                return Result<string?>.Failure(localizer, "AssistantNotFound");

            // Tenant guard: only the owning teacher (or SuperAdmin) may update this assistant.
            if (!await CallerOwnsAssistantAsync(assistant))
                return Result<string?>.Failure(localizer, "AssistantNotFound", HttpStatusCode.NotFound);

            var user = await _unitOfWork.Users.GetUserByIdAsync(assistant.UserId);
            if (user is null)
                return Result<string?>.Failure(localizer, "UserNotFound");

            if (!string.IsNullOrEmpty(dto.username) && dto.username != user.Username)
            {
                var existing = await _unitOfWork.Users
                    .FindExistingUserByCredentialsAsync(string.Empty, dto.username, null);

                if (existing is not null && existing.Id != user.Id)
                    return Result<string?>.Failure(localizer, "repeatedUserName");
            }

            if (!string.IsNullOrEmpty(dto.phoneNumber) && dto.phoneNumber != user.PhoneNumber)
            {
                var existing = await _unitOfWork.Users
                    .FindExistingUserByCredentialsAsync(dto.phoneNumber, string.Empty, null);

                if (existing is not null && existing.Id != user.Id)
                    return Result<string?>.Failure(localizer, "repeatedPhoneNumber");
            }

            if (!string.IsNullOrEmpty(dto.email) && dto.email != user.Email)
            {
                var existing = await _unitOfWork.Users
                    .FindExistingUserByCredentialsAsync(string.Empty, string.Empty, dto.email);

                if (existing is not null && existing.Id != user.Id)
                    return Result<string?>.Failure(localizer, "repeatedEmail");
            }

            HashSet<long>? newPermissionIds = null;
            bool permissionsUpdated =
                dto.permissionIds is { Count: > 0 } ||
                dto.permissionProfileIds is { Count: > 0 };

            if (permissionsUpdated)
            {
                try
                {
                    newPermissionIds = await ResolveAssistantPermissionsAsync(
                        dto.permissionIds,
                        dto.permissionProfileIds);

                }
                catch
                {
                    return Result<string?>.Failure(localizer, "InvalidPermissions");
                }

                await ValidateTeacherScopeAsync(assistant.TeacherAccountId, newPermissionIds);
            }

            user.FullName = dto.fullName ?? user.FullName;
            user.Username = dto.username ?? user.Username;
            user.PhoneNumber = dto.phoneNumber ?? user.PhoneNumber;
            user.Email = dto.email ?? user.Email;

            if (!string.IsNullOrEmpty(dto.newPassword))
                user.PasswordHashed = passwordService.HashPassword(dto.newPassword);

            await _unitOfWork.BeginTransactionAsync();

            try
            {
                await _unitOfWork.Users.UpdateAsync(user);

                if (permissionsUpdated && newPermissionIds is not null)
                {
                    var repo = _unitOfWork.GetRepository<UsersPermission, long>();

                    var existing = await repo.GetAsync(x => x.UserId == user.Id);

                    if (existing.Any())
                        await repo.DeleteRangeAsync(existing);

                    var newRows = newPermissionIds.Select(pid => new UsersPermission
                    {
                        UserId = user.Id,
                        PermissionId = pid
                    });

                    await repo.AddRangeAsync(newRows);
                }
                if (permissionsUpdated && newPermissionIds is not null)
                {
                    var collectPermission = await _unitOfWork.permissionRepo
                        .GetByPermissionNameAndModuleNameAsync("Collect", "Payment");

                    if (collectPermission != null &&
                        newPermissionIds.Contains(collectPermission.Id))
                    {
                        await _paymentService.EnsureAssistantWalletExistsAsync(
                            assistant.TeacherAccountId,
                            assistant.Id,
                            user.Id);
                    }
                }
                if (dto.permissionProfileIds != null)
                {
                    var templateRepo = _unitOfWork.GetRepository<TemplatePermissionsUsers, long>();

                    var existingTemplates = await templateRepo
                        .GetAsync(x => x.AssisstantId == assistant.Id);

                    if (existingTemplates.Any())
                        await templateRepo.DeleteRangeAsync(existingTemplates);

                    if (dto.permissionProfileIds.Any())
                    {
                        var newTemplates = dto.permissionProfileIds
                            .Distinct()
                            .Select(tid => new TemplatePermissionsUsers
                            {
                                TemplateId = tid,
                                AssisstantId = assistant.Id,
                            }).ToList();

                        await templateRepo.AddRangeAsync(newTemplates);
                    }
                }
                assistant.UpdatedAt = DateTime.UtcNow;

                // REQ-USR-013: Bump the assistant's SecurityStamp and drop their
                // Redis snapshot so the permission/template/password change
                // takes effect on their very next request. Must run BEFORE
                // SaveChangesAsync so the stamp bump joins this transaction.
                await _authInvalidation.InvalidateUserAsync(user.Id);

                await _unitOfWork.SaveChangesAsync();
                await _unitOfWork.CommitAsync();

                return Result<string?>.Success("Success", localizer);
            }
            catch
            {
                await _unitOfWork.RollbackAsync();
                throw;
            }
        }
        public async Task<Result<string>> ToggleStatus(ToggleAccountStatus req)
        {
            // -- 1. Fetch assistant ---------------------------------------------------
            var assistant = await _unitOfWork.AssistantRepo.GetByIdAsync(req.accountId);
            if (assistant is null)
                return Result<string>.Failure(localizer, "AssistantNotFound");

            // -- 2. Tenant guard: only the owning teacher (or SuperAdmin) may change status -----
            if (!await CallerOwnsAssistantAsync(assistant))
                return Result<string>.Failure(localizer, "AssistantNotFound", HttpStatusCode.NotFound);

            // -- 3. Fetch linked user -------------------------------------------------
            var user = await _unitOfWork.Users.GetUserByIdAsync(assistant.UserId);
            if (user is null)
                return Result<string>.Failure(localizer, "UserNotFound");

            // -- 4. Already in the requested status? ----------------------------------
            if (assistant.AccountStatus == req.targetStatus)
                return Result<string>.Failure(localizer, "AssistantAlreadyInThisStatus");

            // -- 4b. Cash guard (Suspend only) ----------------------------------------
            // Suspending sets DeletedAt (below), which hides the assistant from the active-collector
            // set — so, exactly like a hard delete (SoftDeleteAssistantAsync), it must not strand
            // cash she is still holding. The tutor must hand over / withdraw the wallet first.
            // It deliberately does NOT set RemovedAt: a suspension is temporary, and the payment
            // module's "removed collector" rules key on RemovedAt so a suspended assistant keeps
            // her tracking card and is never labelled "Removed".
            // Plain Deactivate (Inactive) is intentionally EXEMPT: it leaves DeletedAt null (the
            // assistant stays visible and correctly labeled) and may be needed to stop an assistant
            // from collecting while a cash dispute is resolved.
            if (req.targetStatus == AccountStatus.Suspended)
            {
                var suspendWallet = (await _unitOfWork
                        .GetRepository<AssistantWallet, long>()
                        .GetAsync(w => w.AssistantId == assistant.Id))
                    .FirstOrDefault();
                if (suspendWallet is not null && suspendWallet.CurrentBalance != 0m)
                    return Result<string>.Failure(
                        localizer, "AssistantHasWalletBalance", HttpStatusCode.Conflict);
            }

            // -- 5. Apply status ------------------------------------------------------
            assistant.AccountStatus = req.targetStatus;
                
            // Active → user can login | Inactive / Suspended → user cannot login
            user.IsActive = req.targetStatus == AccountStatus.Active;
            if (req.targetStatus == AccountStatus.Inactive)
            {
                assistant.DeactivatedAt = DateTime.UtcNow;
            }
            else if (req.targetStatus == AccountStatus.Suspended)
            {
                assistant.DeletedAt = DateTime.UtcNow.AddMinutes(2);
            }
            else if (req.targetStatus == AccountStatus.Active)
            {
                assistant.DeactivatedAt = null;
                assistant.DeletedAt = null;
                // Reactivation is the existing un-delete path (it already cleared DeletedAt), so the
                // removal marker has to go with it or the assistant would stay labelled "Removed"
                // on the payments tracking card while working again.
                assistant.RemovedAt = null;
                assistant.UpdatedAt = DateTime.UtcNow;
            }
            // -- 6. Persist -----------------------------------------------------------
            await _unitOfWork.BeginTransactionAsync();

            try
            {
                await _unitOfWork.AssistantRepo.UpdateAsync(assistant);
                await _unitOfWork.Users.UpdateAsync(user);

                // REQ-USR-008 / REQ-USR-027: Bump the assistant's SecurityStamp
                // and drop their Redis snapshot so the deactivation /
                // reactivation / suspension takes effect on their next request.
                // Without this, a deactivated assistant could continue using
                // the app for up to 60 minutes (until token expiry).
                await _authInvalidation.InvalidateUserAsync(user.Id);

                await _unitOfWork.SaveChangesAsync();
                await _unitOfWork.CommitAsync();

                var messageKey = req.targetStatus switch
                {
                    AccountStatus.Active => "AssistantActivated",
                    AccountStatus.Inactive => "AssistantDeactivated",
                    AccountStatus.Suspended => "AssistantSuspended",
                    _ => "StatusUpdated"
                };

                return Result<string>.Success(messageKey, localizer);
            }
            catch
            {
                await _unitOfWork.RollbackAsync();
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Result<string>> SoftDeleteAssistantAsync(long assistantId)
        {
            // -- 1. Fetch assistant ---------------------------------------------------
            var assistant = await _unitOfWork.AssistantRepo.GetByIdAsync(assistantId);
            if (assistant is null)
                return Result<string>.Failure(localizer, "AssistantNotFound", HttpStatusCode.NotFound);

            // -- 2. Tenant guard: only the owning teacher (or SuperAdmin) may delete ----
            if (!await CallerOwnsAssistantAsync(assistant))
                return Result<string>.Failure(localizer, "AssistantNotFound", HttpStatusCode.NotFound);

            // -- 3. Idempotent: already deleted → succeed (no "already in status" error) --
            // The row is already hidden from the list; a repeat Delete must not error.
            // Keyed on RemovedAt, not DeletedAt: a merely SUSPENDED assistant also carries
            // DeletedAt, and short-circuiting on that would let a suspend-then-delete finish
            // without ever stamping the removal.
            if (assistant.RemovedAt is not null)
                return Result<string>.Success("AssistantDeleted", localizer);

            // Guard: don't delete an assistant who is still holding collected cash.
            // The tutor must hand over / withdraw the wallet first; the wallet and its
            // history are then preserved (the deleted assistant stays visible for
            // tracking rather than being purged).
            var walletRepo = _unitOfWork.GetRepository<AssistantWallet, long>();
            var wallet = (await walletRepo.GetAsync(w => w.AssistantId == assistantId))
                .FirstOrDefault();
            if (wallet is not null && wallet.CurrentBalance != 0m)
                return Result<string>.Failure(
                    localizer, "AssistantHasWalletBalance", HttpStatusCode.Conflict);

            var user = await _unitOfWork.Users.GetUserByIdAsync(assistant.UserId);
            if (user is null)
                return Result<string>.Failure(localizer, "UserNotFound");

            // -- 4. Soft-delete: hidden from the list now (DeletedAt), login blocked, ----
            //       flagged Suspended so AssistantCleanupJob picks it up for purge.
            assistant.AccountStatus = AccountStatus.Suspended;
            assistant.DeletedAt = DateTime.UtcNow;
            // The ONLY writer of RemovedAt: this is what makes a removal distinguishable from the
            // temporary Suspend that shares DeletedAt (see Assistant.RemovedAt).
            assistant.RemovedAt = DateTime.UtcNow;
            user.IsActive = false;

            await _unitOfWork.BeginTransactionAsync();
            try
            {
                await _unitOfWork.AssistantRepo.UpdateAsync(assistant);
                await _unitOfWork.Users.UpdateAsync(user);

                // Bump SecurityStamp + drop the Redis snapshot so the deleted assistant
                // cannot keep using an already-issued token (mirrors ToggleStatus).
                await _authInvalidation.InvalidateUserAsync(user.Id);

                await _unitOfWork.SaveChangesAsync();
                await _unitOfWork.CommitAsync();

                return Result<string>.Success("AssistantDeleted", localizer);
            }
            catch
            {
                await _unitOfWork.RollbackAsync();
                throw;
            }
        }


        public async Task<Result<List<LoginActivityDto>>> GetLoginActivityAsync(long assistantId)
        {
            // -- 1. Fetch assistant ---------------------------------------------------
            var assistant = await _unitOfWork.AssistantRepo.GetByIdAsync(assistantId);
            if (assistant is null)
                return Result<List<LoginActivityDto>>.Failure(localizer, "AssistantNotFound");

            // -- 2. Tenant guard (re-enabled): only the owning teacher / SuperAdmin ----
            if (!await CallerOwnsAssistantAsync(assistant))
                return Result<List<LoginActivityDto>>.Failure(localizer, "AssistantNotFound", HttpStatusCode.NotFound);

            // -- 3. Fetch logs --------------------------------------------------------
            var logs = await _unitOfWork.GetRepository<LoginActivityAssistantLog, long>()
                .GetAsync(l => l.AssistantId == assistantId);

            // -- 4. Map ---------------------------------------------------------------
            var result = logs
                .OrderByDescending(l => l.CreateAt)
                .Select(l => new LoginActivityDto
                {
                    id = l.Id,
                    assistantName = assistant.User.FullName,
                    action = l.ActionType.ToString(),
                    occurredAt = l.CreateAt,
                    deviceOrBrowser = l.DeviceOrBrowser,
                    ipAddress = l.IpAddress,
                })
                .ToList();

            return Result<List<LoginActivityDto>>.Success(result, localizer, "Success");
        }

        public async Task RecordLoginActivityAsync(
    long assistantId,
    LoginAcitvityActionType actionType,
    HttpContext httpContext)
        {
            var log = new LoginActivityAssistantLog
            {
                AssistantId = assistantId,
                ActionType = actionType,
                CreateAt = DateTime.UtcNow,
                DeviceOrBrowser = httpContext.Request.Headers["User-Agent"].ToString(),
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
            };

            await _unitOfWork.GetRepository<LoginActivityAssistantLog, long>()
                .AddAsync(log);

            await _unitOfWork.SaveChangesAsync();
        }



        public async Task<HashSet<long>> ResolveAssistantPermissionsAsync(
    IEnumerable<long>? permissionIds,
    IEnumerable<long>? templateIds)
        {
            var result = new HashSet<long>();

            // 1. Individual permissions
            if (permissionIds != null && permissionIds.Any())
            {
                var distinct = permissionIds.Distinct().ToList();

                var validCount = await _unitOfWork
                    .GetRepository<Permission, long>()
                    .CountAsync(p => distinct.Contains(p.Id));

                if (validCount != distinct.Count)
                    throw new InvalidPermissionsException();

                result.UnionWith(distinct);
            }

            // 2. Templates → Permissions
            if (templateIds != null && templateIds.Any())
            {
                var distinctTemplates = templateIds.Distinct().ToList();

                var templatePermissions = await _unitOfWork
                    .GetRepository<TemplatePermisions, long>()
                    .GetAsync(tp => distinctTemplates.Contains(tp.TemplateId));

                var foundTemplates = templatePermissions
                    .Select(t => t.TemplateId)
                    .Distinct()
                    .ToList();

                if (foundTemplates.Count != distinctTemplates.Count)
                    throw new InvalidTemplatesException();

                result.UnionWith(templatePermissions.Select(t => t.PermisionId));
            }

            return result;
        }

        public async Task ValidateTeacherScopeAsync(long teacherId, HashSet<long> permissionIds)
            {
            var modules = await _unitOfWork.ModuleTeacherRepo.GetModulesPerTeacher(teacherId);

            var moduleIds = modules.Select(m => m.Id);

            var availablePermissionIds =
                await _unitOfWork.permissionRepo.GetPermissionIdsByModuleIdsAsync(moduleIds);

            var outOfScope = permissionIds
                .Where(p => !availablePermissionIds.Contains(p))
                .ToList();

            if (outOfScope.Any())
                throw new AssistantPermissionsOutOfScopeException();
        }

        public async Task<Result<PaginatedResponse<List<AssistantAdminListDto>>>> GetAllAssistantsAsync(
    AdminAssistantFilterDto req)
        {
            try
            {
                var (assistants, totalCount) = await _unitOfWork.AssistantRepo
                    .GetAllAssistantsAsync(
                        teacherId: req.teacherId,
                        search: req.search,
                        sortBy: req.sortBy,
                        sortDirection: req.sortDirection,
                        page: req.Page,
                        pageSize: req.PageSize
                    );

                var dtoList = assistants.Select(a => new AssistantAdminListDto
                {
                    id = a.Id,
                    userId = a.UserId,
                    fullName = a.User.FullName,
                    username = a.User.Username,
                    email = a.User.Email,
                    phoneNumber = a.User.PhoneNumber ?? string.Empty,
                    isActive = (bool)a.User.IsActive,
                    teacherId = a.TeacherAccountId,
                    teacherName = a.Teacher.User.FullName,
                    accountStatus = a.AccountStatus.ToString(),
                    createdAt = a.CreateAt,
                    deletedAt = a.DeletedAt,
                    languagePreference = a.LanguagePreference,
                    updatedAt = a.UpdatedAt,
                    lastLoginAt = a.User.LastLoginAt,
                    lastActivityAt = a.User.LastActivityAt,
                }).ToList();

                var response = new PaginatedResponse<List<AssistantAdminListDto>>
                {
                    totalCount = totalCount,
                    page = req.Page,
                    pageSize = req.PageSize,
                    totalPages = (int)Math.Ceiling((double)totalCount / req.PageSize),
                    data = dtoList
                };

                return Result<PaginatedResponse<List<AssistantAdminListDto>>>.Success(response, localizer);
            }
            catch (Exception)
            {
                return Result<PaginatedResponse<List<AssistantAdminListDto>>>.Failure(
                    localizer,
                    "ServerError",
                    HttpStatusCode.InternalServerError);
            }
        }
    }
}
