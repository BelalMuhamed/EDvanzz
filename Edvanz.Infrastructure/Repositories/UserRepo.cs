using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Edvanz.Infrastructure.Repositories
{
    /// <summary>
    /// Extended repository for the User module ecosystem.
    /// Centralizes all domain-specific query logic used by Teacher, Student, and Parent services.
    /// 
    /// ARCHITECTURAL NOTE:
    /// This repo encapsulates ALL expression-based queries so the Application layer
    /// never builds raw predicates. If a query needs to change, you edit it HERE —
    /// not in every service that uses it.
    /// 
    /// Inherits from GenericRepo&lt;User, long&gt; for basic User CRUD,
    /// and adds named methods for every entity in the User module ecosystem.
    /// 
    /// FIX BUG-2: All synchronous EF Core operations (Entry().State, Remove, RemoveRange)
    /// now use 'await Task.CompletedTask' to maintain the project's all-async convention
    /// and suppress CS1998 compiler warnings.
    /// 
    /// FIX DB-1: Added batch loading methods to eliminate N+1 query patterns in
    /// dashboard builders. These load all related data in bulk instead of per-entity loops.
    /// </summary>
    public class UserRepo : GenericRepo<User, long>, IUserRepo
    {
        public UserRepo(EdvanzDbContext context) : base(context)
        {
        }

        // ══════════════════════════════════════════════
        // USER ENTITY QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<User?> GetByPhoneAsync(string phone)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.PhoneNumber == phone);
        }
        /// <inheritdoc />
        public async Task<Teacher?> GetTeacherByIdIncludingDeletedAsync(long teacherId)
        {
            return await _context.Teachers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == teacherId);
        }
        /// <inheritdoc />
        // FIX B1: Previously queried PhoneNumber instead of Username — now correctly queries Username
        public async Task<User?> GetByUserName(string userName)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Username == userName && u.IsActive==true);
        }

        /// <inheritdoc />
        public async Task<int> StampLastActivityAsync(long userId, DateTime nowUtc)
        {
            return await _context.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActivityAt, nowUtc));
        }

        /// <inheritdoc />
        public async Task<User?> GetByEmail(string email)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Email == email);
        }

        /// <inheritdoc />
        public async Task<User?> GetByIdAndTypeAsync(long userId, UserType userType)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Id == userId && u.UserType == userType);
        }

        /// <inheritdoc />
        public async Task<User?> GetUserByIdAsync(long userId)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Id == userId);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<User>> GetAllUsersAsync()
        {
            return await _context.Users
                .AsNoTracking()
                .ToListAsync();
        }
        /// <inheritdoc />
        // FIX V1: Encapsulates the complex OR-based duplicate check that was previously
        // a raw expression in UserService.AddUser. Now the Application layer calls this
        // named method instead of building the predicate itself.
        public async Task<User?> FindExistingUserByCredentialsAsync(string phoneNumber, string username, string? email)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u =>
                   (!string.IsNullOrEmpty(phoneNumber) && u.PhoneNumber == phoneNumber) ||
                   (!string.IsNullOrEmpty(username) && u.Username == username) ||
                   (!string.IsNullOrEmpty(email) && u.Email == email));
        }

        /// <inheritdoc />
        public async Task<UserAuthSnapshot?> GetUserAuthSnapshotAsync(long userId)
        {
            // ── Step 1: Load the User row (lightweight projection — no nav properties needed yet) ──
            // We project to a temporary anonymous-shape result so EF translates this into a
            // SELECT of just the columns we need, not the whole User row.
            var userCore = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new
                {
                    u.Id,
                    u.UserType,
                    u.IsActive,
                    u.SecurityStamp
                })
                .FirstOrDefaultAsync();

            if (userCore is null)
                return null;

            string role = userCore.UserType.ToString();

            // ── Step 2: Resolve TeacherScopeId based on role ──
            // - Teacher: their own Teacher.Id
            // - Assistant: Assistant.TeacherAccountId (BR-SUB-002 / BR-USR-005)
            // - SuperAdmin / Student / Parent: null — no tutor scope applies
            // - Center / CenterAssistant: null HERE by design — a center has no SINGLE teacher scope;
            //   it "acts as" one teacher per request (X-Acting-Teacher-Id), resolved separately by
            //   ICurrentUserService.ResolveActingTeacherIdAsync. The center tier is role-sufficient in
            //   PermissionHandler, so the empty modules/permissions below are intentional (not a lockout).
            long? teacherScopeId = userCore.UserType switch
            {
                UserType.Teacher => await _context.Set<Teacher>()
                    .AsNoTracking()
                    .Where(t => t.UserId == userId)
                    .Select(t => (long?)t.Id)
                    .FirstOrDefaultAsync(),

                UserType.Assistant => await _context.Set<Assistant>()
                    .AsNoTracking()
                    .Where(a => a.UserId == userId)
                    .Select(a => (long?)a.TeacherAccountId)
                    .FirstOrDefaultAsync(),

                _ => null
            };

            // ── Step 3: Load module names for the tutor scope ──
            // Empty set for users without a tutor scope (SuperAdmin/Student/Parent).
            // Reads TutorModuleAccess joined to Models — single indexed lookup.
            HashSet<string> modules;
            if (teacherScopeId.HasValue)
            {
                var moduleNames = await _context.TutorModuleAccess
                    .AsNoTracking()
                    .Where(t => t.TutorId == teacherScopeId.Value)
                    .Select(t => t.module.Name)
                    .ToListAsync();

                modules = new HashSet<string>(moduleNames, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // ── Step 4: Load granular permissions ──
            // Only assistants have UsersPermission rows that matter for authorization —
            // a teacher's "permissions" are inferred from their open modules.
            // We still emit the set unconditionally to keep the snapshot shape simple;
            // the handler treats Teacher role as module-only regardless of permission set.
            //
            // Format matches IUserPermissionService.GetUserPermissionsToToken exactly:
            //   "{ModuleName}.{PermissionName}"  e.g. "Payment.Collect"
            HashSet<string> permissions;
            if (userCore.UserType == UserType.Assistant)
            {
                var permissionStrings = await _context.UsersPermissions
                    .AsNoTracking()
                    .Where(up => up.UserId == userId)
                    .Select(up => up.Permission.module.Name + "." + up.Permission.Name)
                    .ToListAsync();

                permissions = new HashSet<string>(permissionStrings, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // ── Step 5: Materialize the snapshot ──
            return new UserAuthSnapshot
            {
                UserId = userCore.Id,
                Role = role,
                TeacherScopeId = teacherScopeId,
                IsActive = userCore.IsActive ?? false,
                SecurityStamp = userCore.SecurityStamp ?? string.Empty,
                Modules = modules,
                Permissions = permissions
            };
        }
        // ══════════════════════════════════════════════
        // TEACHER ENTITY QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Teacher?> GetTeacherByIdAsync(long teacherId)
        {
            return await _context.Set<Teacher>()
                .FirstOrDefaultAsync(t => t.Id == teacherId);
        }

        /// <inheritdoc />
        public async Task<Teacher?> GetActiveTeacherByIdAsync(long teacherId)
        {
            return await _context.Set<Teacher>()
                .FirstOrDefaultAsync(t => t.Id == teacherId && t.DeletedAt == null   );
        }

        /// <inheritdoc />
        public async Task<long?> GetTeacherUserIdByIdAsync(long teacherId)
        {
            return await _context.Set<Teacher>()
                .AsNoTracking()
                .Where(t => t.Id == teacherId)
                .Select(t => (long?)t.UserId)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<bool> TeacherExistsByUserIdAsync(long userId)
        {
            return await _context.Set<Teacher>()
                .AnyAsync(t => t.UserId == userId);
        }

        /// <inheritdoc />
        public async Task<Teacher?> GetActiveTeacherByCodeAsync(string teacherCode)
        {
            return await _context.Set<Teacher>()
                .FirstOrDefaultAsync(t =>
                    t.TeacherCode == teacherCode &&
                    t.AccountStatus == AccountStatus.Active &&
                    t.DeletedAt == null);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Teacher>> GetAllTeachersAsync()
        {
            return await _context.Set<Teacher>()
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task AddTeacherAsync(Teacher teacher)
        {
            await _context.Set<Teacher>().AddAsync(teacher);
        }

        /// <inheritdoc />
        // FIX BUG-2: Entry().State is synchronous — await Task.CompletedTask for async contract
        public async Task UpdateTeacherAsync(Teacher teacher)
        {
            _context.Entry(teacher).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════
        // TEACHER SUBJECT QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<IReadOnlyList<TeacherSubject>> GetTeacherSubjectsByTeacherIdAsync(long teacherId)
        {
            return await _context.Set<TeacherSubject>()
                .AsNoTracking()
                .Where(ts => ts.TeacherId == teacherId)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<TeacherSubject>> GetAllTeacherSubjectsAsync()
        {
            return await _context.Set<TeacherSubject>()
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task AddTeacherSubjectAsync(TeacherSubject teacherSubject)
        {
            await _context.Set<TeacherSubject>().AddAsync(teacherSubject);
        }

        /// <inheritdoc />
        // FIX BUG-2: RemoveRange is synchronous — await Task.CompletedTask for async contract
        public async Task DeleteTeacherSubjectsAsync(IEnumerable<TeacherSubject> subjects)
        {
            _context.Set<TeacherSubject>().RemoveRange(subjects);
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════
        // SUBJECT QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Subject?> GetSubjectByIdAsync(long subjectId)
        {
            return await _context.Set<Subject>()
                .FirstOrDefaultAsync(s => s.Id == subjectId);
        }

        /// <inheritdoc />
        public async Task<bool> SubjectExistsAndActiveAsync(long subjectId)
        {
            return await _context.Set<Subject>()
                .AnyAsync(s => s.Id == subjectId && s.IsActive);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Subject>> GetActiveSubjectsAsync()
        {
            return await _context.Set<Subject>()
                .AsNoTracking()
                .Where(s => s.IsActive)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Subject>> GetAllSubjectsAsync()
        {
            return await _context.Set<Subject>()
                .AsNoTracking()
                .ToListAsync();
        }

        // ══════════════════════════════════════════════
        // TEACHER CONFIGURATION QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<TeacherConfiguration?> GetConfigurationByTeacherIdAsync(long teacherId)
        {
            return await _context.Set<TeacherConfiguration>()
                .FirstOrDefaultAsync(c => c.TeacherId == teacherId);
        }

        /// <inheritdoc />
        public async Task AddConfigurationAsync(TeacherConfiguration configuration)
        {
            await _context.Set<TeacherConfiguration>().AddAsync(configuration);
        }

        /// <inheritdoc />
        // FIX BUG-2: Entry().State is synchronous — await Task.CompletedTask for async contract
        public async Task UpdateConfigurationAsync(TeacherConfiguration configuration)
        {
            _context.Entry(configuration).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════
        // TEACHER PRORATED TIER QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<IReadOnlyList<TeacherProratedTier>> GetProratedTiersByConfigIdAsync(long configurationId)
        {
            return await _context.Set<TeacherProratedTier>()
                .AsNoTracking()
                .Where(pt => pt.TeacherConfigurationId == configurationId)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task AddProratedTiersAsync(IEnumerable<TeacherProratedTier> tiers)
        {
            await _context.Set<TeacherProratedTier>().AddRangeAsync(tiers);
        }

        /// <inheritdoc />
        public async Task AddProratedTierAsync(TeacherProratedTier tier)
        {
            await _context.Set<TeacherProratedTier>().AddAsync(tier);
        }

        /// <inheritdoc />
        // FIX BUG-2: RemoveRange is synchronous — await Task.CompletedTask for async contract
        public async Task DeleteProratedTiersAsync(IEnumerable<TeacherProratedTier> tiers)
        {
            _context.Set<TeacherProratedTier>().RemoveRange(tiers);
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════
        // TEACHER SUBSCRIPTION QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        //public async Task<IReadOnlyList<TeacherSubscription>> GetActiveSubscriptionsByTeacherIdAsync(long teacherId)
        //{
        //    return await _context.Set<TeacherSubscription>()
        //        .AsNoTracking()
        //        .Where(s => s.TeacherId == teacherId &&
        //            (s.SubscriptionStatus == SubscriptionStatus.Active ||
        //             s.SubscriptionStatus == SubscriptionStatus.ExpiringSoon))
        //        .ToListAsync();
        //}
        public async Task<TeacherSubscription?> GetCurrentSubscriptionByTeacherIdAsync(long teacherId)
        {
            // Single indexed read against the filtered unique index
            // IX_TeacherSubscriptions_Current on (TeacherId) WHERE IsCurrent = 1.
            // Returns null if the teacher has never had a subscription OR all rows are historical.
            return await _context.Set<TeacherSubscription>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TeacherId == teacherId && s.IsCurrent);
        }
        /// <inheritdoc />
        //public async Task<IReadOnlyList<TeacherSubscription>> GetAllSubscriptionsAsync()
        //{
        //    return await _context.Set<TeacherSubscription>()
        //        .AsNoTracking()
        //        .ToListAsync();
        //}
        /// <inheritdoc />
        public async Task<IReadOnlyList<TeacherSubscription>> GetAllSubscriptionsAsync()
        {
            // Used by the super admin dashboard's in-memory join (GetTeachersAsync).
            // Unchanged in behavior — but the returned rows no longer have the
            // SubscriptionStatus column. Callers derive status via
            // SubscriptionStatusCalculator.Derive(row, DateTime.UtcNow).
            return await _context.Set<TeacherSubscription>()
                .AsNoTracking()
                .ToListAsync();
        }
        /// <inheritdoc />
        public async Task<IReadOnlyList<TeacherSubscription>> GetAllSubscriptionsByTeacherIdAsync(long teacherId)
        {
            // REQ-SUB-022: full payment history, most recent first.
            return await _context.Set<TeacherSubscription>()
                .AsNoTracking()
                .Where(s => s.TeacherId == teacherId)
                .OrderByDescending(s => s.EndDate)
                .ToListAsync();
        }
        // ══════════════════════════════════════════════
        // STUDENT CAPACITY PACKAGE QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<IReadOnlyList<StudentCapacityPackage>> GetActiveCapacityPackagesAsync()
        {
            return await _context.Set<StudentCapacityPackage>()
                .AsNoTracking()
                .Where(p => p.IsActive)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<StudentCapacityPackage?> GetActiveCapacityPackageByIdAsync(long packageId)
        {
            return await _context.Set<StudentCapacityPackage>()
                .FirstOrDefaultAsync(p => p.Id == packageId && p.IsActive);
        }

        /// <inheritdoc />
        public async Task<StudentCapacityPackage?> GetCapacityPackageByIdAsync(long packageId)
        {
            return await _context.Set<StudentCapacityPackage>()
                .FirstOrDefaultAsync(p => p.Id == packageId);
        }

        // ══════════════════════════════════════════════
        // STUDENT USER ENTITY QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<StudentUser?> GetActiveStudentUserByIdAsync(long studentUserId)
        {
            return await _context.Set<StudentUser>()
                .FirstOrDefaultAsync(s => s.Id == studentUserId && s.DeletedAt == null);
        }

        /// <inheritdoc />
        public async Task<bool> StudentUserExistsByUserIdAsync(long userId)
        {
            return await _context.Set<StudentUser>()
                .AnyAsync(s => s.UserId == userId);
        }

        /// <inheritdoc />
        public async Task<StudentUser?> GetStudentUserByAccountCodeAsync(string accountCode)
        {
            string normalizedCode = accountCode.Trim().ToUpperInvariant();
            return await _context.Set<StudentUser>()
                .FirstOrDefaultAsync(s => s.StudentAccountCode == normalizedCode && s.DeletedAt == null);
        }

        /// <inheritdoc />
        public async Task<StudentUser?> GetStudentUserByIdAsync(long studentUserId)
        {
            return await _context.Set<StudentUser>()
                .FirstOrDefaultAsync(s => s.Id == studentUserId);
        }

        /// <inheritdoc />
        public async Task AddStudentUserAsync(StudentUser studentUser)
        {
            await _context.Set<StudentUser>().AddAsync(studentUser);
        }

        /// <inheritdoc />
        // FIX BUG-2: Entry().State is synchronous — await Task.CompletedTask for async contract
        public async Task UpdateStudentUserAsync(StudentUser studentUser)
        {
            _context.Entry(studentUser).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════
        // STUDENT TEACHER LINK QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<int> CountActiveStudentTeacherLinksAsync(long studentUserId)
        {
            return await _context.Set<StudentTeacherLink>()
                .CountAsync(l => l.StudentUserId == studentUserId && l.LinkStatus == LinkStatus.Active);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<StudentTeacherLink>> GetActiveStudentTeacherLinksAsync(long studentUserId)
        {
            return await _context.Set<StudentTeacherLink>()
                .AsNoTracking()
                .Where(l => l.StudentUserId == studentUserId && l.LinkStatus == LinkStatus.Active)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<StudentTeacherLink?> GetActiveStudentTeacherLinkAsync(long studentUserId, long teacherId)
        {
            return await _context.Set<StudentTeacherLink>()
                .FirstOrDefaultAsync(l =>
                    l.StudentUserId == studentUserId &&
                    l.TeacherId == teacherId &&
                    l.LinkStatus == LinkStatus.Active);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<long>> GetLinkedStudentUserIdsAsync(
            long teacherId, IReadOnlyCollection<long> teacherStudentIds)
        {
            if (teacherStudentIds is null || teacherStudentIds.Count == 0)
                return Array.Empty<long>();

            return await _context.Set<StudentTeacherLink>()
                .Where(l => l.TeacherId == teacherId
                         && l.LinkStatus == LinkStatus.Active
                         && l.TeacherStudentId != null
                         && teacherStudentIds.Contains(l.TeacherStudentId!.Value))
                .Select(l => l.StudentUser.UserId)
                .Distinct()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<StudentTeacherLink>> GetLiveLinksForStudentUserAsync(long studentUserId)
        {
            // Tracked (no AsNoTracking): the caller mutates each row's LinkStatus/UnlinkedAt
            // inside the same transaction, so SaveChanges must pick the changes up.
            return await _context.Set<StudentTeacherLink>()
                .Where(l => l.StudentUserId == studentUserId &&
                            (l.LinkStatus == LinkStatus.Active || l.LinkStatus == LinkStatus.Pending))
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<bool> StudentTeacherLinkExistsAsync(long studentUserId, long teacherId)
        {
            return await _context.Set<StudentTeacherLink>()
                .AnyAsync(l =>
                    l.StudentUserId == studentUserId &&
                    l.TeacherId == teacherId &&
                    l.LinkStatus == LinkStatus.Active);
        }

        /// <inheritdoc />
        public async Task AddStudentTeacherLinkAsync(StudentTeacherLink link)
        {
            await _context.Set<StudentTeacherLink>().AddAsync(link);
        }

        /// <inheritdoc />
        // FIX BUG-2: Entry().State is synchronous — await Task.CompletedTask for async contract
        public async Task UpdateStudentTeacherLinkAsync(StudentTeacherLink link)
        {
            _context.Entry(link).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<int> RemoveAllLiveStudentLinksForTeacherAsync(long teacherId, long removedByUserId)
        {
            DateTime now = DateTime.UtcNow;
            // Set-based: any Active/Pending row becomes a terminal RemovedByTeacher row.
            // Terminal rows are retained for audit; the filtered live-row unique index
            // ([LinkStatus] IN (1,3)) only covers Active/Pending, so this cannot collide.
            return await _context.Set<StudentTeacherLink>()
                .Where(l => l.TeacherId == teacherId
                         && (l.LinkStatus == LinkStatus.Active || l.LinkStatus == LinkStatus.Pending))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.LinkStatus, LinkStatus.RemovedByTeacher)
                    .SetProperty(l => l.RemovedByUserId, removedByUserId)
                    .SetProperty(l => l.UnlinkedAt, now));
        }

        /// <inheritdoc />
        public async Task<int> RemoveAllActiveParentLinksForTeacherAsync(long teacherId)
        {
            DateTime now = DateTime.UtcNow;
            return await _context.Set<ParentChildTeacherLink>()
                .Where(l => l.TeacherId == teacherId && l.LinkStatus == LinkStatus.Active)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.LinkStatus, LinkStatus.RemovedByTeacher)
                    .SetProperty(l => l.UnlinkedAt, now));
        }

        // ── Request/approval flow (replaces the student-side 3-credential flow) ──

        /// <inheritdoc />
        public async Task<StudentTeacherLink?> GetLiveStudentTeacherLinkAsync(long studentUserId, long teacherId)
        {
            return await _context.Set<StudentTeacherLink>()
                .FirstOrDefaultAsync(l =>
                    l.StudentUserId == studentUserId &&
                    l.TeacherId == teacherId &&
                    (l.LinkStatus == LinkStatus.Active || l.LinkStatus == LinkStatus.Pending));
        }

        /// <inheritdoc />
        public async Task<StudentTeacherLink?> GetLatestStudentTeacherLinkAsync(long studentUserId, long teacherId)
        {
            // Tracked (no AsNoTracking) — the caller mutates + saves it (the Delete action).
            return await _context.Set<StudentTeacherLink>()
                .Where(l => l.StudentUserId == studentUserId && l.TeacherId == teacherId)
                .OrderByDescending(l => l.Id)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<StudentTeacherLink>> GetAllStudentTeacherLinksAsync(long studentUserId)
        {
            return await _context.Set<StudentTeacherLink>()
                .AsNoTracking()
                .Where(l => l.StudentUserId == studentUserId)
                .OrderByDescending(l => l.Id)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<TeacherLinkRequestRow> Items, int TotalCount)>
            GetPendingLinkRequestsForTeacherPagedAsync(long teacherId, int page, int pageSize)
        {
            var query = _context.Set<StudentTeacherLink>()
                .AsNoTracking()
                .Where(l => l.TeacherId == teacherId && l.LinkStatus == LinkStatus.Pending);

            int total = await query.CountAsync();

            var items = await query
                .OrderByDescending(l => l.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Join(_context.Set<StudentUser>(),
                    l => l.StudentUserId, su => su.Id,
                    (l, su) => new { l, su })
                .Join(_context.Set<User>(),
                    x => x.su.UserId, u => u.Id,
                    (x, u) => new TeacherLinkRequestRow
                    {
                        LinkId = x.l.Id,
                        RequestedStudentName = x.l.RequestedStudentName,
                        RequestedStudentCode = x.l.RequestedStudentCode,
                        RequestedAt = x.l.RequestedAt,
                        StudentAccountCode = x.su.StudentAccountCode,
                        StudentFullName = u.FullName,
                        StudentPhoneNumber = u.PhoneNumber
                    })
                .ToListAsync();

            return (items, total);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<TeacherLinkedStudentRow>> GetUnboundActiveLinksForTeacherAsync(
            long teacherId)
        {
            return await _context.Set<StudentTeacherLink>()
                .AsNoTracking()
                .Where(l => l.TeacherId == teacherId
                         && l.LinkStatus == LinkStatus.Active
                         && l.TeacherStudentId == null)
                .OrderByDescending(l => l.LinkedAt)
                .Join(_context.Set<StudentUser>(),
                    l => l.StudentUserId, su => su.Id,
                    (l, su) => new { l, su })
                .Join(_context.Set<User>(),
                    x => x.su.UserId, u => u.Id,
                    (x, u) => new { x.l, x.su, u })
                .Select(x => new TeacherLinkedStudentRow
                {
                    LinkId = x.l.Id,
                    LinkedAt = x.l.LinkedAt,
                    StudentAccountCode = x.su.StudentAccountCode,
                    StudentFullName = x.u.FullName,
                    StudentPhoneNumber = x.u.PhoneNumber,
                    TeacherStudentId = null,
                    RosterStudentName = null,
                    RosterStudentCode = null
                })
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<TeacherLinkedStudentRow> Items, int TotalCount, int LinkedCount, int FilteredCount)>
            GetActiveLinkedStudentsForTeacherPagedAsync(
                long teacherId, int page, int pageSize, string? search = null,
                LinkedStudentFilter filter = LinkedStudentFilter.All)
        {
            // A student who deleted / deactivated their account (User.IsActive == false) must
            // never surface in the teacher's "My Students" list, even if a link row was somehow
            // left Active. Restricting the base query to accounts whose User is still active keeps
            // the total/linked counts and the returned page consistent with each other.
            var activeStudentUserIds = _context.Set<StudentUser>()
                .Join(_context.Set<User>(), su => su.UserId, u => u.Id, (su, u) => new { su, u })
                .Where(x => x.u.IsActive == true)
                .Select(x => x.su.Id);

            var baseQuery = _context.Set<StudentTeacherLink>()
                .AsNoTracking()
                .Where(l => l.TeacherId == teacherId
                         && l.LinkStatus == LinkStatus.Active
                         && activeStudentUserIds.Contains(l.StudentUserId));

            // Join account identity (+ roster record via the TeacherStudent nav) up-front so an
            // optional search can match the account name/code OR the bound roster name/code, and the
            // total/linked counts stay consistent with the filtered, returned page. The join is 1:1,
            // so paging after it yields the same rows as the pre-join paging did.
            var joined = baseQuery
                .Join(_context.Set<StudentUser>(),
                    l => l.StudentUserId, su => su.Id,
                    (l, su) => new { l, su })
                .Join(_context.Set<User>(),
                    x => x.su.UserId, u => u.Id,
                    (x, u) => new { x.l, x.su, u });

            var term = string.IsNullOrWhiteSpace(search) ? null : ArabicTextNormalizer.Normalize(search.Trim());
            if (!string.IsNullOrEmpty(term))
            {
                var like = $"%{term}%";
                joined = joined.Where(x =>
                    (x.u.FullName != null && EF.Functions.Like(DbSearch.ArabicNormalize(x.u.FullName), like))
                    || (x.su.StudentAccountCode != null && EF.Functions.Like(DbSearch.ArabicNormalize(x.su.StudentAccountCode), like))
                    || (x.l.TeacherStudent != null && x.l.TeacherStudent.StudentName != null
                        && EF.Functions.Like(DbSearch.ArabicNormalize(x.l.TeacherStudent.StudentName), like))
                    || (x.l.TeacherStudent != null && x.l.TeacherStudent.StudentCode != null
                        && EF.Functions.Like(DbSearch.ArabicNormalize(x.l.TeacherStudent.StudentCode), like)));
            }

            int total = await joined.CountAsync();
            // Linked = bound to a roster record; unlinked = accepted-but-unbound. Counted over the
            // whole (optionally filtered) Active set, not just the returned page, so headcounts match.
            int linked = await joined.CountAsync(x => x.l.TeacherStudentId != null);

            // ── The status filter narrows the PAGE ONLY, deliberately AFTER both counts above. ──
            // These counts feed the client's filter chips ("All 42 · Linked 30 · Not linked 12"), so
            // narrowing the query before counting would collapse them to (N, 0) or (0, N) — the chips
            // would then report the size of the slice you are already looking at instead of the
            // choices available. Search DOES narrow them, because a search re-defines the whole set.
            var pageQuery = filter switch
            {
                LinkedStudentFilter.Linked => joined.Where(x => x.l.TeacherStudentId != null),
                LinkedStudentFilter.NotLinked => joined.Where(x => x.l.TeacherStudentId == null),
                _ => joined
            };

            // The roster record is null when the teacher deleted the TeacherStudent after linking
            // (SetNull FK — degraded enrollment state).
            var items = await pageQuery
                .OrderByDescending(x => x.l.LinkedAt)
                // LinkedAt alone is not unique enough to page deterministically once a teacher
                // accepts several requests in the same instant; Id is the stable tiebreak.
                .ThenByDescending(x => x.l.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new TeacherLinkedStudentRow
                {
                    LinkId = x.l.Id,
                    LinkedAt = x.l.LinkedAt,
                    StudentAccountCode = x.su.StudentAccountCode,
                    StudentFullName = x.u.FullName,
                    StudentPhoneNumber = x.u.PhoneNumber,
                    TeacherStudentId = x.l.TeacherStudentId,
                    RosterStudentName = x.l.TeacherStudent != null ? x.l.TeacherStudent.StudentName : null,
                    RosterStudentCode = x.l.TeacherStudent != null ? x.l.TeacherStudent.StudentCode : null,
                    IsDeviceRegistered = x.l.LockedDeviceId != null,
                    DeviceBoundAt = x.l.DeviceBoundAt
                })
                .ToListAsync();

            // TotalCount stays the size of the UNFILTERED (but searched) set. The service derives the
            // page count from the filtered slice instead — see GetLinkedStudentsAsync.
            int filteredTotal = filter switch
            {
                LinkedStudentFilter.Linked => linked,
                LinkedStudentFilter.NotLinked => total - linked,
                _ => total
            };

            return (items, total, linked, filteredTotal);
        }

        /// <inheritdoc />
        public async Task<StudentTeacherLink?> GetStudentTeacherLinkByIdForTeacherAsync(long linkId, long teacherId)
        {
            return await _context.Set<StudentTeacherLink>()
                .FirstOrDefaultAsync(l => l.Id == linkId && l.TeacherId == teacherId);
        }

        /// <inheritdoc />
        public async Task<bool> TryBindStudentTeacherLinkDeviceAsync(long linkId, string deviceId, DateTime boundAtUtc)
        {
            // Conditional set: only binds when no device is registered yet. If two devices race to
            // register on first open, exactly one UPDATE affects a row (the first device wins) and
            // the loser gets 0 rows → the service resolves it as a mismatch.
            int rows = await _context.Set<StudentTeacherLink>()
                .Where(l => l.Id == linkId && l.LockedDeviceId == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.LockedDeviceId, deviceId)
                    .SetProperty(l => l.DeviceBoundAt, boundAtUtc));
            return rows == 1;
        }

        /// <inheritdoc />
        public async Task<bool> TryMigrateStudentTeacherLinkDeviceAsync(
            long linkId, string previousDeviceId, string newDeviceId, DateTime boundAtUtc)
        {
            // Conditional set guarded on the OLD id: the row is re-pointed only while it is still
            // bound to exactly that value. Two requests from the same upgraded phone racing here
            // both write the same new id (the loser simply affects 0 rows), and a link bound to a
            // genuinely different device is never touched.
            int rows = await _context.Set<StudentTeacherLink>()
                .Where(l => l.Id == linkId && l.LockedDeviceId == previousDeviceId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.LockedDeviceId, newDeviceId)
                    .SetProperty(l => l.DeviceBoundAt, boundAtUtc));
            return rows == 1;
        }

        /// <inheritdoc />
        public async Task<bool> TryStampDeviceBlockNotifiedAsync(long linkId, DateTime nowUtc, DateTime cutoffUtc)
        {
            // Exactly one blocked request per cooldown window wins this UPDATE and owns the push;
            // the rest see 0 rows and stay silent.
            int rows = await _context.Set<StudentTeacherLink>()
                .Where(l => l.Id == linkId &&
                            (l.DeviceBlockNotifiedAt == null || l.DeviceBlockNotifiedAt < cutoffUtc))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.DeviceBlockNotifiedAt, nowUtc));
            return rows == 1;
        }

        /// <inheritdoc />
        public async Task<StudentTeacherLink?> GetStudentTeacherLinkByIdAsync(long linkId)
        {
            // No TeacherId predicate — SUPER-ADMIN ONLY, see interface doc.
            return await _context.Set<StudentTeacherLink>()
                .FirstOrDefaultAsync(l => l.Id == linkId);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<StudentTeacherLink>> GetActiveLinksByIdsForTeacherAsync(
            long teacherId, IReadOnlyCollection<long> linkIds)
        {
            return await _context.Set<StudentTeacherLink>()
                .Where(l => l.TeacherId == teacherId &&
                            l.LinkStatus == LinkStatus.Active &&
                            linkIds.Contains(l.Id))
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<bool> IsTeacherStudentActivelyLinkedAsync(long teacherStudentId)
        {
            return await _context.Set<StudentTeacherLink>()
                .AnyAsync(l => l.TeacherStudentId == teacherStudentId &&
                               l.LinkStatus == LinkStatus.Active);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<long, long>> GetActiveLinkIdsByTeacherStudentIdsAsync(
            IReadOnlyCollection<long> teacherStudentIds)
        {
            if (teacherStudentIds.Count == 0) return new Dictionary<long, long>();

            return await _context.Set<StudentTeacherLink>()
                .AsNoTracking()
                .Where(l => l.TeacherStudentId != null &&
                            teacherStudentIds.Contains(l.TeacherStudentId.Value) &&
                            l.LinkStatus == LinkStatus.Active)
                .ToDictionaryAsync(l => l.TeacherStudentId!.Value, l => l.Id);
        }

        /// <inheritdoc />
        public async Task<StudentTeacherLink?> GetActiveStudentTeacherLinkByTeacherStudentIdAsync(
            long teacherId, long teacherStudentId)
        {
            // Tracked (no AsNoTracking) — the teardown path mutates and saves this row.
            return await _context.Set<StudentTeacherLink>()
                .FirstOrDefaultAsync(l => l.TeacherId == teacherId &&
                                          l.TeacherStudentId == teacherStudentId &&
                                          l.LinkStatus == LinkStatus.Active);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<ParentChildTeacherLink>> GetActiveParentChildTeacherLinksByTeacherStudentIdAsync(
            long teacherId, long teacherStudentId)
        {
            return await _context.Set<ParentChildTeacherLink>()
                .Where(l => l.TeacherId == teacherId &&
                            l.TeacherStudentId == teacherStudentId &&
                            l.LinkStatus == LinkStatus.Active)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task DetachLinksFromPurgedStudentAsync(long teacherStudentId)
        {
            await _context.Set<StudentTeacherLink>()
                .Where(l => l.TeacherStudentId == teacherStudentId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.TeacherStudentId, (long?)null));

            await _context.Set<ParentChildTeacherLink>()
                .Where(l => l.TeacherStudentId == teacherStudentId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.TeacherStudentId, (long?)null));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<long>> GetActivelyLinkedTeacherStudentIdsAsync(
            IReadOnlyCollection<long> teacherStudentIds)
        {
            return await _context.Set<StudentTeacherLink>()
                .AsNoTracking()
                .Where(l => l.TeacherStudentId != null &&
                            teacherStudentIds.Contains(l.TeacherStudentId.Value) &&
                            l.LinkStatus == LinkStatus.Active)
                .Select(l => l.TeacherStudentId!.Value)
                .Distinct()
                .ToListAsync();
        }

        // ══════════════════════════════════════════════
        // TEACHER STUDENT (TEACHER-SCOPED RECORD) QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<TeacherStudent?> GetTeacherStudentByLinkingCredentialsAsync(
            long teacherId, string studentCode)
        {
            return await _context.Set<TeacherStudent>()
                .FirstOrDefaultAsync(ts =>
                    ts.TeacherId == teacherId &&
                    ts.StudentCode == studentCode &&
                   
                    !ts.IsDeleted);
        }

        /// <inheritdoc />
        public async Task<TeacherStudent?> GetActiveTeacherStudentByCodeAsync(long teacherId, string studentCode)
        {
            // StudentCode is stored uppercase; the DB CI collation makes == case-insensitive.
            return await _context.Set<TeacherStudent>()
                .FirstOrDefaultAsync(ts =>
                    ts.TeacherId == teacherId &&
                    ts.StudentCode == studentCode &&
                    !ts.IsDeleted);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<TeacherStudent>> GetActiveTeacherStudentsByCodesAsync(
            long teacherId, IReadOnlyCollection<string> studentCodes)
        {
            return await _context.Set<TeacherStudent>()
                .AsNoTracking()
                .Where(ts => ts.TeacherId == teacherId &&
                             studentCodes.Contains(ts.StudentCode) &&
                             !ts.IsDeleted)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<TeacherStudent?> GetActiveTeacherStudentByIdAsync(long teacherId, long teacherStudentId)
        {
            return await _context.Set<TeacherStudent>()
                .FirstOrDefaultAsync(ts =>
                    ts.Id == teacherStudentId &&
                    ts.TeacherId == teacherId &&
                    !ts.IsDeleted);
        }

        /// <inheritdoc />
        public async Task<int> CountStudentsMissingParentPhoneAsync(long teacherId)
        {
            // Served by IX_TeacherStudents_TeacherId_IsDeleted. A blank string counts as missing:
            // legacy imports stored "" rather than NULL, and neither can ever auto-approve.
            return await _context.Set<TeacherStudent>()
                .CountAsync(ts =>
                    ts.TeacherId == teacherId &&
                    !ts.IsDeleted &&
                    (ts.ParentPhoneNumber == null || ts.ParentPhoneNumber == string.Empty));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<TeacherStudent>> GetActiveTeacherStudentsByParentPhoneAsync(
            long teacherId, IEnumerable<string> phoneVariants)
        {
            var variants = phoneVariants?
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct()
                .ToList() ?? new List<string>();

            if (variants.Count == 0)
                return Array.Empty<TeacherStudent>();

            // Plain IN over the raw column so IX_TeacherStudents_TeacherId_ParentPhoneNumber stays
            // usable — normalizing the column in SQL would make the predicate non-sargable.
            return await _context.Set<TeacherStudent>()
                .AsNoTracking()
                .Where(ts =>
                    ts.TeacherId == teacherId &&
                    !ts.IsDeleted &&
                    ts.ParentPhoneNumber != null &&
                    variants.Contains(ts.ParentPhoneNumber))
                .ToListAsync();
        }

        // ══════════════════════════════════════════════
        // PARENT USER ENTITY QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<ParentUser?> GetActiveParentUserByIdAsync(long parentUserId)
        {
            return await _context.Set<ParentUser>()
                .FirstOrDefaultAsync(p => p.Id == parentUserId && p.DeletedAt == null);
        }

        /// <inheritdoc />
        public async Task<bool> ParentUserExistsByUserIdAsync(long userId)
        {
            return await _context.Set<ParentUser>()
                .AnyAsync(p => p.UserId == userId);
        }

        /// <inheritdoc />
        public async Task AddParentUserAsync(ParentUser parentUser)
        {
            await _context.Set<ParentUser>().AddAsync(parentUser);
        }

        /// <inheritdoc />
        // FIX BUG-2: Entry().State is synchronous — await Task.CompletedTask for async contract
        public async Task UpdateParentUserAsync(ParentUser parentUser)
        {
            _context.Entry(parentUser).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════
        // PARENT CHILD QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<int> CountActiveChildrenAsync(long parentUserId)
        {
            return await _context.Set<ParentChild>()
                .CountAsync(c => c.ParentUserId == parentUserId && c.IsActive);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<ParentChild>> GetActiveChildrenAsync(long parentUserId)
        {
            return await _context.Set<ParentChild>()
                .AsNoTracking()
                .Where(c => c.ParentUserId == parentUserId && c.IsActive)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<ParentChild?> GetActiveChildAsync(long parentUserId, long childId)
        {
            return await _context.Set<ParentChild>()
                .FirstOrDefaultAsync(c =>
                    c.Id == childId &&
                    c.ParentUserId == parentUserId &&
                    c.IsActive);
        }

        /// <inheritdoc />
        public async Task<bool> ChildAlreadyLinkedAsync(long parentUserId, long studentUserId)
        {
            return await _context.Set<ParentChild>()
                .AnyAsync(c =>
                    c.ParentUserId == parentUserId &&
                    c.StudentUserId == studentUserId &&
                    c.IsActive);
        }

        /// <inheritdoc />
        public async Task AddParentChildAsync(ParentChild parentChild)
        {
            await _context.Set<ParentChild>().AddAsync(parentChild);
        }

        /// <inheritdoc />
        // FIX BUG-2: Entry().State is synchronous — await Task.CompletedTask for async contract
        public async Task UpdateParentChildAsync(ParentChild parentChild)
        {
            _context.Entry(parentChild).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════
        // PARENT CHILD TEACHER LINK QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<IReadOnlyList<ParentChildTeacherLink>> GetActiveParentChildTeacherLinksAsync(long parentChildId)
        {
            return await _context.Set<ParentChildTeacherLink>()
                .AsNoTracking()
                .Where(l => l.ParentChildId == parentChildId && l.LinkStatus == LinkStatus.Active)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<bool> ParentChildTeacherLinkExistsAsync(long parentChildId, long teacherId)
        {
            return await _context.Set<ParentChildTeacherLink>()
                .AnyAsync(l =>
                    l.ParentChildId == parentChildId &&
                    l.TeacherId == teacherId &&
                    l.LinkStatus == LinkStatus.Active);
        }

        /// <inheritdoc />
        public async Task<ParentChildTeacherLink?> GetActiveParentChildTeacherLinkAsync(long parentChildId, long teacherId)
        {
            return await _context.Set<ParentChildTeacherLink>()
                .FirstOrDefaultAsync(l =>
                    l.ParentChildId == parentChildId &&
                    l.TeacherId == teacherId &&
                    l.LinkStatus == LinkStatus.Active);
        }

        /// <inheritdoc />
        public async Task AddParentChildTeacherLinkAsync(ParentChildTeacherLink link)
        {
            await _context.Set<ParentChildTeacherLink>().AddAsync(link);
        }

        /// <inheritdoc />
        // FIX BUG-2: Entry().State is synchronous — await Task.CompletedTask for async contract
        public async Task UpdateParentChildTeacherLinkAsync(ParentChildTeacherLink link)
        {
            _context.Entry(link).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════
        // BATCH LOADING METHODS (FIX DB-1: N+1 elimination)
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        /// <summary>
        /// FIX DB-1: Loads all data needed for student dashboard teacher rendering in bulk.
        /// Previously each teacher in the loop triggered 4 separate DB calls (teacher user,
        /// teacher subjects, first subject name, configuration) — totaling 4×N round-trips.
        /// Now loads everything for a set of teacher IDs in 4 queries total regardless of N.
        /// </summary>
        public async Task<TeacherDashboardBatchData> GetTeacherDashboardDataAsync(IReadOnlyList<long> teacherIds)
        {
            if (!teacherIds.Any())
                return new TeacherDashboardBatchData();

            var distinctIds = teacherIds.Distinct().ToList();

            // 1. Bulk load all teachers
            var teachers = await _context.Set<Teacher>()
                .AsNoTracking()
                .Where(t => distinctIds.Contains(t.Id) && t.DeletedAt == null)
                .ToListAsync();

            // 2. Bulk load all user records for these teachers
            var userIds = teachers.Select(t => t.UserId).Distinct().ToList();
            var users = await _context.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToListAsync();

            // 3. Bulk load all teacher-subject associations and their subjects
            var teacherSubjects = await _context.Set<TeacherSubject>()
                .AsNoTracking()
                .Where(ts => distinctIds.Contains(ts.TeacherId))
                .ToListAsync();

            var subjectIds = teacherSubjects.Select(ts => ts.SubjectId).Distinct().ToList();
            var subjects = await _context.Set<Subject>()
                .AsNoTracking()
                .Where(s => subjectIds.Contains(s.Id))
                .ToListAsync();

            // 4. Bulk load all configurations for these teachers
            var configs = await _context.Set<TeacherConfiguration>()
                .AsNoTracking()
                .Where(c => distinctIds.Contains(c.TeacherId))
                .ToListAsync();

            return new TeacherDashboardBatchData
            {
                Teachers = teachers.ToDictionary(t => t.Id),
                Users = users.ToDictionary(u => u.Id),
                TeacherSubjects = teacherSubjects.GroupBy(ts => ts.TeacherId)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<TeacherSubject>)g.ToList()),
                Subjects = subjects.ToDictionary(s => s.Id),
                Configurations = configs.ToDictionary(c => c.TeacherId)
            };
        }

        /// <inheritdoc />
        public async Task<long?> ResolveOwnedChildIdByTeacherStudentAsync(
            long parentUserId, long teacherId, long teacherStudentId)
        {
            // Method A: an active StudentTeacherLink bound to this exact roster row (covered by
            // UX_StudentTeacherLinks_TeacherStudentId_Active), joined to one of this parent's own
            // active children by StudentUserId.
            long? methodAChildId = await _context.Set<StudentTeacherLink>()
                .AsNoTracking()
                .Where(l => l.TeacherId == teacherId
                         && l.TeacherStudentId == teacherStudentId
                         && l.LinkStatus == LinkStatus.Active)
                .Join(
                    _context.Set<ParentChild>()
                        .Where(c => c.ParentUserId == parentUserId && c.IsActive),
                    l => l.StudentUserId,
                    c => c.StudentUserId,
                    (l, c) => (long?)c.Id)
                .FirstOrDefaultAsync();

            if (methodAChildId is not null)
                return methodAChildId;

            // Method B: an active ParentChildTeacherLink bound to this exact roster row, whose
            // owning ParentChild belongs to this parent.
            return await _context.Set<ParentChildTeacherLink>()
                .AsNoTracking()
                .Where(l => l.TeacherId == teacherId
                         && l.TeacherStudentId == teacherStudentId
                         && l.LinkStatus == LinkStatus.Active)
                .Join(
                    _context.Set<ParentChild>()
                        .Where(c => c.ParentUserId == parentUserId && c.IsActive),
                    l => l.ParentChildId,
                    c => c.Id,
                    (l, c) => (long?)c.Id)
                .FirstOrDefaultAsync();
        }

        public async Task<Teacher?> GetTeacherByUserIdAsync(long userId)
        {
            return await  _context.Teachers.FirstOrDefaultAsync(t => t.UserId == userId && t.DeletedAt==null );
        }
        // ══════════════════════════════════════════════
        // SUBSCRIPTION MANAGEMENT EXTENSIONS (v1.2)
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<CurrentSubscriptionStatusProjection?> GetCurrentSubscriptionStatusAsync(long teacherId)
        {
            // ── Center redirect ──
            // A center-owned teacher has NO per-teacher TeacherSubscription — its access AND plan are
            // governed by the CENTER's current subscription. Resolving here means every consumer
            // (ActiveSubscriptionHandler, SubscriptionGateService.HasActive/IsManagerial, admin lists)
            // is correct without each having to special-case CenterId. Cache invalidation on center
            // subscription change fans out to the center's teachers (AdminCenterSubscriptionService).
            var teacherRow = await _context.Set<Teacher>()
                .AsNoTracking()
                .Where(t => t.Id == teacherId)
                .Select(t => new { t.CenterId, t.CenterPlanType, t.StudentCapacityPackageId })
                .FirstOrDefaultAsync();

            if (teacherRow?.CenterId is long centerId)
            {
                return await _context.Set<CenterSubscription>()
                    .AsNoTracking()
                    .Where(s => s.CenterId == centerId && s.IsCurrent)
                    .Select(s => new CurrentSubscriptionStatusProjection
                    {
                        SubscriptionId = s.Id,
                        TeacherId = teacherId,
                        StartDate = s.StartDate,
                        EndDate = s.EndDate,
                        AmountPaidEGP = s.AmountPaidEGP,
                        // Plan (Full/Managerial) for a center teacher lives on the Teacher, not the sub.
                        PlanType = teacherRow.CenterPlanType ?? Domain.Enums.SubscriptionPlanType.Full,
                        StudentCapacityPackageId = teacherRow.StudentCapacityPackageId
                    })
                    .FirstOrDefaultAsync();
            }

            // ── Standalone teacher — original per-teacher path ──
            // Single indexed read against IX_TeacherSubscriptions_Current.
            // Project to the lean DTO so the cache value stays small (<200 bytes JSON).
            return await _context.Set<TeacherSubscription>()
                .AsNoTracking()
                .Where(s => s.TeacherId == teacherId && s.IsCurrent)
                .Join(_context.Set<Teacher>(),
                      sub => sub.TeacherId,
                      teacher => teacher.Id,
                      (sub, teacher) => new CurrentSubscriptionStatusProjection
                      {
                          SubscriptionId = sub.Id,
                          TeacherId = sub.TeacherId,
                          StartDate = sub.StartDate,
                          EndDate = sub.EndDate,
                          AmountPaidEGP = sub.AmountPaidEGP,
                          PlanType = sub.PlanType,
                          StudentCapacityPackageId = teacher.StudentCapacityPackageId
                      })
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<TeacherSubscription?> GetCurrentSubscriptionForUpdateAsync(long teacherId)
        {
            // Pessimistic lock for the confirmation transaction (§6.6).
            // WITH (UPDLOCK, HOLDLOCK) ensures any concurrent reader/writer of the same
            // row blocks until this transaction commits or rolls back. The lock is held
            // for the duration of the enclosing transaction (Serializable isolation).
            //
            // FromSqlInterpolated parameterizes teacherId safely — never string concatenation.
            const string sql =
                "SELECT * FROM TeacherSubscriptions WITH (UPDLOCK, HOLDLOCK) " +
                "WHERE TeacherId = {0} AND IsCurrent = 1";

            return await _context.Set<TeacherSubscription>()
                .FromSqlRaw(sql, teacherId)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task FlipCurrentAndInsertNewAsync(
            TeacherSubscription? previousCurrent,
            TeacherSubscription newSubscription)
        {
            if (previousCurrent is not null)
            {
                previousCurrent.IsCurrent = false;
                _context.Entry(previousCurrent).State = EntityState.Modified;
            }

            await _context.Set<TeacherSubscription>().AddAsync(newSubscription);

            // Caller (SubscriptionService.ConfirmPaymentAsync) is responsible for
            // SaveChangesAsync — that's where DbUpdateConcurrencyException surfaces
            // for the bounded retry in §6.6.
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<UpcomingExpiryProjection>> GetTeachersWithUpcomingExpiryAsync(DateTime today)
        {
            // Day-precision boundaries for the alert window (D-5 through D-0).
            // We compare against EndDate's date component to avoid HH:MM drift.
            DateTime windowStart = today.Date;                  // D-0 → EndDate >= today
            DateTime windowEnd = today.Date.AddDays(6);         // D-5 → EndDate < today + 6 days

            return await _context.Set<TeacherSubscription>()
                .AsNoTracking()
                .Where(s => s.IsCurrent
                         && s.EndDate >= windowStart
                         && s.EndDate < windowEnd)
                .Select(s => new UpcomingExpiryProjection
                {
                    TeacherId = s.TeacherId,
                    SubscriptionEndDate = s.EndDate,
                    // SQL Server-side computation: DATEDIFF(DAY, today, s.EndDate)
                    DaysUntilExpiry = EF.Functions.DateDiffDay(windowStart, s.EndDate)
                })
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<TeacherReminderProjection?> GetTeacherForReminderAsync(long teacherId)
        {
            return await _context.Set<Teacher>()
                .AsNoTracking()
                .Where(t => t.Id == teacherId && t.DeletedAt == null)
                .Join(_context.Set<User>(),
                      teacher => teacher.UserId,
                      user => user.Id,
                      (teacher, user) => new TeacherReminderProjection
                      {
                          TeacherId = teacher.Id,
                          UserId = user.Id,
                          FullName = user.FullName,
                          PhoneNumber = user.PhoneNumber,
                          LanguagePreference = teacher.LanguagePreference
                      })
                .FirstOrDefaultAsync();
        }

        // (UpdateCapacityPackagePriceAsync was removed 2026-07-17 with the retired
        // per-package price endpoint — pricing is per-student now; see
        // SubscriptionPricingRepo. Package price columns remain in the DB, unread.)

        /// <inheritdoc />
        public async Task<StudentUser?> GetActiveStudentUserByUserIdAsync(long userId)
        {
            return await _context.StudentUsers
                .FirstOrDefaultAsync(su => su.UserId == userId && su.DeletedAt == null);
        }
        /// <inheritdoc />
        public async Task<string?> GetTeacherDisplayNameAsync(long teacherId)
        {
            return await _context.Teachers
                .Where(t => t.Id == teacherId)
                .Select(t => t.User.FullName)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<ParentUser?> GetActiveParentUserByUserIdAsync(long userId)
        {
            return await _context.Set<ParentUser>()
                .FirstOrDefaultAsync(p => p.UserId == userId && p.DeletedAt == null);
        }
        // ══════════════════════════════════════════════
        // DIRECT CHAT — ELIGIBILITY GATE QUERIES
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<UserType?> GetUserTypeByUserIdAsync(long userId)
        {
            // Single-column projection; no entity materialization.
            return await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => (UserType?)u.UserType)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<bool> AreStudentAndTeacherLinkedByUserIdsAsync(
            long studentUserId, long teacherUserId)
        {
            // student User.Id → StudentUser.Id (via StudentTeacherLink.StudentUser.UserId)
            // teacher User.Id → Teacher.Id    (via StudentTeacherLink.Teacher.UserId)
            // EF Core translates navigation property access in AnyAsync to JOINs.
            return await _context.Set<StudentTeacherLink>()
                .AnyAsync(l =>
                    l.StudentUser.UserId == studentUserId &&
                    l.Teacher.UserId == teacherUserId &&
                    l.LinkStatus == LinkStatus.Active);
        }

        /// <inheritdoc />
        public async Task<bool> AreStudentAndParentLinkedByUserIdsAsync(
            long studentUserId, long parentUserId)
        {
            // Method-A only: ParentChild.StudentUserId is set (child has a StudentUser account).
            // Method-B children have no StudentUser account — they cannot participate in chat.
            // student User.Id → StudentUser.Id (via ParentChild.StudentUser.UserId)
            // parent  User.Id → ParentUser.Id  (via ParentChild.ParentUser.UserId)
            return await _context.Set<ParentChild>()
                .AnyAsync(pc =>
                    pc.StudentUserId.HasValue &&
                    pc.StudentUser!.UserId == studentUserId &&
                    pc.ParentUser.UserId == parentUserId &&
                    pc.LinkMethod == ChildLinkMethod.StudentAccount &&
                    pc.IsActive);
        }

        /// <inheritdoc />
        public async Task<bool> AreStudentAndAssistantLinkedByUserIdsAsync(
            long studentUserId, long assistantUserId)
        {
            // assistant User.Id → Assistant.TeacherAccountId (Teacher.Id)
            // student   User.Id → StudentUser.Id → StudentTeacherLink.TeacherId
            // A student can chat with an assistant if they share the same teacher.
            return await (
                from link in _context.Set<StudentTeacherLink>()
                join su in _context.StudentUsers on link.StudentUserId equals su.Id
                join asst in _context.Set<Assistant>() on link.TeacherId equals asst.TeacherAccountId
                where su.UserId == studentUserId &&
                      asst.UserId == assistantUserId &&
                      link.LinkStatus == LinkStatus.Active
                select link
            ).AnyAsync();
        }

        // ── NAME RESOLUTION ──────────────────────────────────────────────────

        /// <inheritdoc />
        public async Task<string?> GetUserFullNameByUserIdAsync(long userId)
        {
            return await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<Dictionary<long, string>> GetUserFullNamesByUserIdsAsync(
            IEnumerable<long> userIds)
        {
            var idList = userIds.ToList();
            if (idList.Count == 0)
                return new Dictionary<long, string>();

            // Single round-trip: WHERE Id IN (...) SELECT Id, FullName.
            return await _context.Users
                .Where(u => idList.Contains(u.Id))
                .AsNoTracking()
                .ToDictionaryAsync(u => u.Id, u => u.FullName);
        }
        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<long, string>> GetTeacherNamesByIdsAsync(
            IEnumerable<long> teacherIds)
        {
            var ids = teacherIds.Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<long, string>();

            return await _context.Set<Teacher>()
                .Where(t => ids.Contains(t.Id))
                .Join(_context.Users.AsNoTracking(),
                    t => t.UserId,
                    u => u.Id,
                    (t, u) => new { t.Id, u.FullName })
                .AsNoTracking()
                .ToDictionaryAsync(x => x.Id, x => x.FullName);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<TeacherNameLookupProjection>> GetTeacherNameLookupAsync()
        {
            return await _context.Set<Teacher>()
                .AsNoTracking()
                .Where(t => t.DeletedAt == null && t.AccountStatus == AccountStatus.Active)
                .Join(_context.Users.AsNoTracking(),
                    t => t.UserId,
                    u => u.Id,
                    (t, u) => new TeacherNameLookupProjection
                    {
                        TeacherId = t.Id,
                        FullName = u.FullName
                    })
                .OrderBy(x => x.FullName)
                .ToListAsync();
        }
        /// <inheritdoc />
        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<long, string?>> GetStudentLanguagePreferencesAsync(
            IReadOnlyCollection<long> userIds)
        {
            if (userIds is null || userIds.Count == 0)
                return new Dictionary<long, string?>();

            var rows = await _context.Set<StudentUser>()
                .AsNoTracking()
                .Where(s => userIds.Contains(s.UserId))
                .Select(s => new { s.UserId, s.LanguagePreference })
                .ToListAsync();

            // A student account could in principle appear twice for one user; keep the
            // first rather than throwing on a duplicate key inside a notification job.
            var map = new Dictionary<long, string?>(rows.Count);
            foreach (var row in rows)
                map[row.UserId] = row.LanguagePreference;
            return map;
        }

        public async Task<string?> GetUserLanguagePreferenceByUserIdAsync(long userId)
        {
            // LanguagePreference is not on User — it lives on the role entity. Resolve the
            // role via UserType (single-column read), then read the matching role row's
            // preference. Mirrors the UserType switch used elsewhere in this repo.
            var userType = await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => (UserType?)u.UserType)
                .FirstOrDefaultAsync();

            return userType switch
            {
                UserType.Teacher => await _context.Set<Teacher>()
                    .AsNoTracking().Where(t => t.UserId == userId)
                    .Select(t => t.LanguagePreference).FirstOrDefaultAsync(),

                UserType.Assistant => await _context.Set<Assistant>()
                    .AsNoTracking().Where(a => a.UserId == userId)
                    .Select(a => a.LanguagePreference).FirstOrDefaultAsync(),

                UserType.Student => await _context.Set<StudentUser>()
                    .AsNoTracking().Where(s => s.UserId == userId)
                    .Select(s => s.LanguagePreference).FirstOrDefaultAsync(),

                UserType.Parent => await _context.Set<ParentUser>()
                    .AsNoTracking().Where(p => p.UserId == userId)
                    .Select(p => p.LanguagePreference).FirstOrDefaultAsync(),

                _ => null
            };
        }

        // ══════════════════════════════════════════════
        // STUDENT ACCOUNTS — SUPER-ADMIN PAGINATED LIST
        // ══════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<(IReadOnlyList<StudentAccountRow> Items, int TotalCount)> GetStudentAccountsPagedAsync(
            string? search, long? teacherId, int page, int pageSize)
        {
            // StudentUser and User both carry their own soft-delete HasQueryFilter
            // (DeletedAt == null) — no explicit predicate needed for either here.
            var query = _context.Set<StudentUser>().AsNoTracking();

            if (teacherId.HasValue)
            {
                query = query.Where(su => su.StudentTeacherLinks.Any(l =>
                    l.TeacherId == teacherId.Value && l.LinkStatus == LinkStatus.Active));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = ArabicTextNormalizer.Normalize(search.Trim());
                query = query.Where(su =>
                    DbSearch.ArabicNormalize(su.User.FullName).Contains(term) ||
                    su.StudentTeacherLinks.Any(l =>
                        l.LinkStatus == LinkStatus.Active &&
                        l.TeacherStudent != null &&
                        DbSearch.ArabicNormalize(l.TeacherStudent.StudentCode).Contains(term)));
            }

            int total = await query.CountAsync();

            var items = await query
                .OrderByDescending(su => su.CreateAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(su => new StudentAccountRow
                {
                    StudentAccountId = su.Id,
                    StudentAccountCode = su.StudentAccountCode,
                    FullName = su.User.FullName,
                    UserName = su.User.Username,
                    PhoneNumber = su.User.PhoneNumber,
                    LastLoginAt = su.User.LastLoginAt
                })
                .ToListAsync();

            return (items, total);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<StudentAccountLinkedTeacherRow>> GetActiveLinkedTeachersForStudentUsersAsync(
            IReadOnlyList<long> studentUserIds)
        {
            var idList = studentUserIds.ToList();
            if (idList.Count == 0)
                return Array.Empty<StudentAccountLinkedTeacherRow>();

            // Required-nav projection (l.Teacher.*) implicitly applies Teacher's own
            // soft-delete query filter — a link pointing at a deleted teacher is dropped,
            // same behavior as every other Teacher-joining query in this repo.
            return await _context.Set<StudentTeacherLink>()
                .AsNoTracking()
                .Where(l => l.LinkStatus == LinkStatus.Active && idList.Contains(l.StudentUserId))
                .Select(l => new StudentAccountLinkedTeacherRow
                {
                    StudentUserId = l.StudentUserId,
                    TeacherId = l.TeacherId,
                    TeacherCode = l.Teacher.TeacherCode,
                    TeacherName = l.Teacher.User.FullName,
                    StudentCode = l.TeacherStudent != null ? l.TeacherStudent.StudentCode : null
                })
                .ToListAsync();
        }
    }
}