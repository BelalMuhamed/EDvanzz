using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace Edvanz.Domain.Interfaces
{
    /// <summary>
    /// Extended repository interface for the User module ecosystem.
    /// Centralizes all domain-specific query methods used by Teacher, Student, and Parent services.
    /// 
    /// WHY THIS EXISTS:
    /// Previously, services called generic repo methods with raw expression predicates directly
    /// (e.g., FindAsync(u => u.Id == id && u.UserType == UserType.Teacher)). This violated
    /// Onion Architecture by scattering query logic across the Application layer.
    /// 
    /// Now, all query logic lives here in named methods. If a query changes, you edit ONE method
    /// in the repo — not every service that uses it.
    /// 
    /// Inherits from IGenericRepo&lt;User, long&gt; so basic User CRUD is still available.
    /// </summary>
    public interface IUserRepo : IGenericRepo<User, long>
    {
        // ══════════════════════════════════════════════
        // USER ENTITY QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Finds a user by phone number.
        /// Used by OtpService and AuthService for phone-based lookup.
        /// </summary>
        Task<User?> GetByPhoneAsync(string phone);

        /// <summary>
        /// Finds a user by email address.
        /// Used by UserService during registration duplicate checks.
        /// </summary>
        Task<User?> GetByEmail(string email);

        /// <summary>
        /// Finds a user by username.
        /// Used by UserService during registration duplicate checks.
        /// </summary>
        Task<User?> GetByUserName(string userName);

        /// <summary>
        /// Stamps <see cref="User.LastActivityAt"/> ("last seen") with a set-based UPDATE
        /// (no tracking, no SaveChanges). Called by SessionActivitySlidingMiddleware inside
        /// its per-user throttle window, alongside the refresh-token idle-deadline slide.
        /// </summary>
        Task<int> StampLastActivityAsync(long userId, DateTime nowUtc);

        /// <summary>
        /// Finds a user by Id and UserType.
        /// Used by Teacher/Student/Parent services during initialization to validate
        /// that the user exists and has the correct type before creating type-specific records.
        /// </summary>
        Task<User?> GetByIdAndTypeAsync(long userId, UserType userType);

        /// <summary>
        /// Finds a user by Id.
        /// Used across services when loading a user record for display (e.g., teacher's full name).
        /// </summary>
        Task<User?> GetUserByIdAsync(long userId);

        /// <summary>
        /// Retrieves all users (no filter). Used by TeacherService.GetTeachersAsync for bulk join.
        /// </summary>
        Task<IReadOnlyList<User>> GetAllUsersAsync();

        /// <summary>
        /// Finds an existing user that matches any of the unique credential fields:
        /// phone number, username, or email (if email is not null/empty).
        /// Used by UserService.AddUser during registration to detect duplicate accounts.
        /// 
        /// Returns the first matching user, or null if no match found.
        /// The caller checks which field matched to return the appropriate error message.
        /// </summary>
        /// <param name="phoneNumber">The phone number to check for duplicates.</param>
        /// <param name="username">The username to check for duplicates.</param>
        /// <param name="email">The email to check for duplicates (skipped if null or empty).</param>
        Task<User?> FindExistingUserByCredentialsAsync(string phoneNumber, string username, string? email);

        /// <summary>
        /// Builds a fully-populated <see cref="UserAuthSnapshot"/> for the given user
        /// in a single optimized round-trip. Backs the per-request authorization
        /// resolution path (REQ-USR-013 / REQ-USR-027 / REQ-USR-008 / BR-ADM-010).
        ///
        /// CONTENTS:
        ///   - Identity: UserId, Role, IsActive, SecurityStamp
        ///   - Scope: TeacherScopeId (self for Teacher, TeacherAccountId for Assistant,
        ///            null for SuperAdmin / Student / Parent)
        ///   - Modules: derived from TutorModuleAccess for the resolved tutor scope
        ///   - Permissions: "{ModuleName}.{PermissionName}" strings from UsersPermissions
        ///     (Assistants only — Teachers infer access from Modules)
        ///
        /// PERFORMANCE:
        ///   AsNoTracking, three sequential queries (User+role-row, modules, permissions)
        ///   bounded by the user's own data — no cross-tenant scans. Typical execution
        ///   ~5-10 ms locally. Cached afterward in Redis via
        ///   <c>IUserAuthCacheService</c> so subsequent requests skip this entirely.
        ///
        /// RETURNS:
        ///   The snapshot, or null when no user exists with the given id. A user that
        ///   exists but has been deactivated still returns a snapshot — the caller
        ///   (middleware) inspects <c>IsActive</c> to decide 401 vs proceed.
        /// </summary>
        Task<UserAuthSnapshot?> GetUserAuthSnapshotAsync(long userId);

        // ══════════════════════════════════════════════
        // TEACHER ENTITY QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Finds a teacher by Id (respects global soft-delete query filter).
        /// </summary>
        Task<Teacher?> GetTeacherByIdAsync(long teacherId);
        Task<Teacher?> GetTeacherByUserIdAsync(long teacherId);


        /// <summary>
        /// Finds an active (non-deleted) teacher by Id.
        /// Explicitly checks DeletedAt == null for use cases outside query filter scope.
        /// </summary>
        Task<Teacher?> GetActiveTeacherByIdAsync(long teacherId);

        /// <summary>
        /// Resolves the owning <see cref="User"/> id for a teacher (Teacher.UserId) without
        /// materializing the whole entity. This is the ONLY user id that represents "the teacher"
        /// as a collector; every other collector id belongs to an assistant (active, removed, or
        /// center). Used by the payment screens to label a collector Teacher vs Assistant by
        /// identity rather than an active-assistant allow-list (§7.4). Returns null if not found.
        /// </summary>
        Task<long?> GetTeacherUserIdByIdAsync(long teacherId);

        /// <summary>
        /// Checks if a teacher record already exists for the given UserId.
        /// Used during initialization to prevent duplicates.
        /// </summary>
        Task<bool> TeacherExistsByUserIdAsync(long userId);

        /// <summary>
        /// Finds an active teacher by their unique TeacherCode.
        /// Used by Student and Parent linking flows (AAM-FR-05.5) and by GetTeacherByCodeAsync.
        /// </summary>
        Task<Teacher?> GetActiveTeacherByCodeAsync(string teacherCode);

        /// <summary>
        /// Retrieves all teachers (no filter). Used by GetTeachersAsync for bulk operations.
        /// </summary>
        Task<IReadOnlyList<Teacher>> GetAllTeachersAsync();

        /// <summary>
        /// Bulk-resolves display names (User.FullName) for a set of TeacherIds in a single
        /// round-trip — mirrors ISessionRepo.GetSessionNamesByIdsAsync's contract. Used to
        /// enrich the SuperAdmin's platform-wide student list (REQ: show which teacher owns
        /// each roster row) without an N+1 per page. Ids with no matching active Teacher are
        /// simply absent from the result — callers must treat a missing key as "name unknown"
        /// rather than throwing.
        /// </summary>
        Task<IReadOnlyDictionary<long, string>> GetTeacherNamesByIdsAsync(IEnumerable<long> teacherIds);

        /// <summary>
        /// Adds a new Teacher entity.
        /// </summary>
        Task AddTeacherAsync(Teacher teacher);

        /// <summary>
        /// Updates an existing Teacher entity.
        /// </summary>
        Task UpdateTeacherAsync(Teacher teacher);

        // ══════════════════════════════════════════════
        // TEACHER SUBJECT QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Retrieves all subject associations for a specific teacher.
        /// </summary>
        Task<IReadOnlyList<TeacherSubject>> GetTeacherSubjectsByTeacherIdAsync(long teacherId);

        /// <summary>
        /// Retrieves all teacher-subject records (no filter). Used for bulk operations.
        /// </summary>
        Task<IReadOnlyList<TeacherSubject>> GetAllTeacherSubjectsAsync();

        /// <summary>
        /// Adds a new TeacherSubject association.
        /// </summary>
        Task AddTeacherSubjectAsync(TeacherSubject teacherSubject);

        /// <summary>
        /// Deletes all subject associations for a teacher. Used during profile update.
        /// </summary>
        Task DeleteTeacherSubjectsAsync(IEnumerable<TeacherSubject> subjects);

        // ══════════════════════════════════════════════
        // SUBJECT QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Finds a subject by Id.
        /// </summary>
        Task<Subject?> GetSubjectByIdAsync(long subjectId);

        /// <summary>
        /// Checks if a subject exists and is active by Id.
        /// Used during teacher initialization and profile update to validate subject selections.
        /// </summary>
        Task<bool> SubjectExistsAndActiveAsync(long subjectId);

        /// <summary>
        /// Retrieves all active subjects.
        /// </summary>
        Task<IReadOnlyList<Subject>> GetActiveSubjectsAsync();

        /// <summary>
        /// Retrieves all subjects (no filter). Used for bulk operations in GetTeachersAsync.
        /// </summary>
        Task<IReadOnlyList<Subject>> GetAllSubjectsAsync();

        // ══════════════════════════════════════════════
        // TEACHER CONFIGURATION QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Finds the configuration for a specific teacher.
        /// </summary>
        Task<TeacherConfiguration?> GetConfigurationByTeacherIdAsync(long teacherId);

        /// <summary>
        /// Adds a new TeacherConfiguration entity.
        /// </summary>
        Task AddConfigurationAsync(TeacherConfiguration configuration);

        /// <summary>
        /// Updates an existing TeacherConfiguration entity.
        /// </summary>
        Task UpdateConfigurationAsync(TeacherConfiguration configuration);

        // ══════════════════════════════════════════════
        // TEACHER PRORATED TIER QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Retrieves all prorated tiers for a specific configuration.
        /// </summary>
        Task<IReadOnlyList<TeacherProratedTier>> GetProratedTiersByConfigIdAsync(long configurationId);

        /// <summary>
        /// Adds multiple prorated tiers in bulk.
        /// </summary>
        Task AddProratedTiersAsync(IEnumerable<TeacherProratedTier> tiers);

        /// <summary>
        /// Adds a single prorated tier.
        /// </summary>
        Task AddProratedTierAsync(TeacherProratedTier tier);

        /// <summary>
        /// Deletes all prorated tiers for a configuration. Used during configuration update.
        /// </summary>
        Task DeleteProratedTiersAsync(IEnumerable<TeacherProratedTier> tiers);

        // ══════════════════════════════════════════════
        // TEACHER SUBSCRIPTION QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Retrieves active or expiring-soon subscriptions for a teacher.
        /// Used by GetActiveSubscriptionAsync.
        /// </summary>
        //Task<IReadOnlyList<TeacherSubscription>> GetActiveSubscriptionsByTeacherIdAsync(long teacherId);

        /// <summary>
        /// Retrieves all subscriptions (no filter). Used for bulk operations in GetTeachersAsync.
        /// </summary>
        //Task<IReadOnlyList<TeacherSubscription>> GetAllSubscriptionsAsync();

        // ══════════════════════════════════════════════
        // STUDENT CAPACITY PACKAGE QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Retrieves all active student capacity packages.
        /// </summary>
        Task<IReadOnlyList<StudentCapacityPackage>> GetActiveCapacityPackagesAsync();

        /// <summary>
        /// Finds an active capacity package by Id.
        /// Used during teacher profile update and configuration to validate and set capacity.
        /// </summary>
        Task<StudentCapacityPackage?> GetActiveCapacityPackageByIdAsync(long packageId);

        /// <summary>
        /// Finds a capacity package by Id (ignoring active status). Used in profile DTO builder.
        /// </summary>
        Task<StudentCapacityPackage?> GetCapacityPackageByIdAsync(long packageId);

        // ══════════════════════════════════════════════
        // STUDENT USER ENTITY QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Finds an active (non-deleted) student user by Id.
        /// </summary>
        Task<StudentUser?> GetActiveStudentUserByIdAsync(long studentUserId);

        /// <summary>
        /// Checks if a StudentUser record already exists for the given UserId.
        /// Used during initialization to prevent duplicates.
        /// </summary>
        Task<bool> StudentUserExistsByUserIdAsync(long userId);

        /// <summary>
        /// Finds a student user by their unique StudentAccountCode (non-deleted).
        /// Used by Parent module for Method A child linking (AAM-FR-06.3).
        /// </summary>
        Task<StudentUser?> GetStudentUserByAccountCodeAsync(string accountCode);

        /// <summary>
        /// Finds a student user by Id (no soft-delete check). Used in BuildChildDtoAsync.
        /// </summary>
        Task<StudentUser?> GetStudentUserByIdAsync(long studentUserId);

        /// <summary>
        /// Adds a new StudentUser entity.
        /// </summary>
        Task AddStudentUserAsync(StudentUser studentUser);

        /// <summary>
        /// Updates an existing StudentUser entity.
        /// </summary>
        Task UpdateStudentUserAsync(StudentUser studentUser);

        // ══════════════════════════════════════════════
        // STUDENT TEACHER LINK QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Counts active links for a specific student user.
        /// Used for profile summary (linked teacher count).
        /// </summary>
        Task<int> CountActiveStudentTeacherLinksAsync(long studentUserId);

        /// <summary>
        /// Retrieves all active links for a specific student user.
        /// Used by GetLinkedTeachersAsync and parent BuildChildDtoAsync (Method A).
        /// </summary>
        Task<IReadOnlyList<StudentTeacherLink>> GetActiveStudentTeacherLinksAsync(long studentUserId);

        /// <summary>
        /// Finds an active link between a specific student and teacher.
        /// Used by UnlinkTeacherAsync.
        /// </summary>
        Task<StudentTeacherLink?> GetActiveStudentTeacherLinkAsync(long studentUserId, long teacherId);

        /// <summary>
        /// Every non-terminal (Active or Pending) <see cref="StudentTeacherLink"/> owned by the
        /// given student account, tracked for update. Used when the student self-deletes their
        /// account so each link can be transitioned to its terminal state and drop out of every
        /// teacher's "My Students" (Active) and pending-requests (Pending) views.
        /// </summary>
        Task<IReadOnlyList<StudentTeacherLink>> GetLiveLinksForStudentUserAsync(long studentUserId);

        /// <summary>
        /// Checks if an active link already exists between a student and teacher.
        /// Used during LinkTeacherAsync to prevent duplicate dashboard entries (AAM-FR-05.7).
        /// </summary>
        Task<bool> StudentTeacherLinkExistsAsync(long studentUserId, long teacherId);

        /// <summary>
        /// Adds a new StudentTeacherLink entity.
        /// </summary>
        Task AddStudentTeacherLinkAsync(StudentTeacherLink link);

        /// <summary>
        /// Updates an existing StudentTeacherLink entity.
        /// </summary>
        Task UpdateStudentTeacherLinkAsync(StudentTeacherLink link);

        /// <summary>
        /// Atomically binds a device to a link ONLY when it has none yet
        /// (<c>LockedDeviceId IS NULL</c>). Returns true when this call performed the bind, false
        /// when a device was already registered (e.g. another device won a concurrent first-open
        /// race). Used by the device-lock registration flow. Writes immediately (no SaveChanges).
        /// </summary>
        Task<bool> TryBindStudentTeacherLinkDeviceAsync(long linkId, string deviceId, DateTime boundAtUtc);

        /// <summary>
        /// Terminates EVERY live (Active or Pending) student-teacher link for the teacher,
        /// setting LinkStatus = RemovedByTeacher, RemovedByUserId and UnlinkedAt. Used when a
        /// managerial subscription is activated with the "remove existing links" option so no
        /// student account remains connected. Runs as a single set-based UPDATE inside the
        /// caller's transaction (no SaveChanges needed). Returns the number of rows affected.
        /// </summary>
        Task<int> RemoveAllLiveStudentLinksForTeacherAsync(long teacherId, long removedByUserId);

        /// <summary>
        /// Terminates EVERY Active parent-child-teacher link for the teacher, setting
        /// LinkStatus = RemovedByTeacher and UnlinkedAt. Companion to
        /// <see cref="RemoveAllLiveStudentLinksForTeacherAsync"/> for the managerial
        /// activation "remove existing links" option. Set-based UPDATE inside the caller's
        /// transaction. Returns the number of rows affected.
        /// </summary>
        Task<int> RemoveAllActiveParentLinksForTeacherAsync(long teacherId);

        // ── Request/approval flow (replaces the student-side 3-credential flow) ──

        /// <summary>
        /// Finds the single live (Pending or Active) link row between a student and
        /// teacher, or null. Backed by the filtered unique index — at most one row
        /// can match. Used for duplicate checks on request creation and for the
        /// student-side cancel/unlink operation.
        /// </summary>
        Task<StudentTeacherLink?> GetLiveStudentTeacherLinkAsync(long studentUserId, long teacherId);

        /// <summary>
        /// Returns the single NEWEST link row between a student and teacher across ANY
        /// status (live or terminal), or null. Tracked, so callers may mutate + save.
        /// Used by the student-side "Delete" (dismiss) action, which must be able to hide
        /// a terminal card (Rejected/Unlinked/RemovedByTeacher) the live-row lookup misses.
        /// </summary>
        Task<StudentTeacherLink?> GetLatestStudentTeacherLinkAsync(long studentUserId, long teacherId);

        /// <summary>
        /// Returns ALL link rows for a student across every status, newest first.
        /// The service reduces this to the latest row per teacher so the dashboard
        /// can show Pending/Active/Rejected states (request-awareness, no-tracking).
        /// </summary>
        Task<IReadOnlyList<StudentTeacherLink>> GetAllStudentTeacherLinksAsync(long studentUserId);

        /// <summary>
        /// Pages the Pending link requests addressed to a teacher, newest first,
        /// joined to the requesting account's identity (StudentUser + User).
        /// </summary>
        Task<(IReadOnlyList<TeacherLinkRequestRow> Items, int TotalCount)>
            GetPendingLinkRequestsForTeacherPagedAsync(long teacherId, int page, int pageSize);

        /// <summary>
        /// Pages the Active links of a teacher (their linked students), newest first,
        /// joined to account identity and the bound TeacherStudent roster record. Optional
        /// <paramref name="search"/> filters by the account name/code and bound roster name/code
        /// (case-insensitive substring); the total/linked counts reflect the searched set.
        ///
        /// <paramref name="filter"/> narrows the returned PAGE only — TotalCount and LinkedCount are
        /// always counted over the whole searched set so the caller can render filter chips whose
        /// numbers describe the available choices rather than the slice already on screen.
        /// FilteredCount is the size of the narrowed slice, for paging.
        /// </summary>
        Task<(IReadOnlyList<TeacherLinkedStudentRow> Items, int TotalCount, int LinkedCount, int FilteredCount)>
            GetActiveLinkedStudentsForTeacherPagedAsync(
                long teacherId, int page, int pageSize, string? search = null,
                LinkedStudentFilter filter = LinkedStudentFilter.All);

        /// <summary>
        /// All of a teacher's Active links that are NOT currently bound to any roster
        /// record (TeacherStudentId is null) — the pool of "connected but not yet
        /// linked" student accounts a SuperAdmin can attach to a roster row. No
        /// pagination: this pool is small by construction (bounded by how many
        /// students requested a connection and haven't been bound yet), unlike the
        /// full linked-students list.
        /// </summary>
        Task<IReadOnlyList<TeacherLinkedStudentRow>> GetUnboundActiveLinksForTeacherAsync(long teacherId);

        /// <summary>
        /// Finds a link row by Id scoped to the teacher (tracked, for accept/reject).
        /// </summary>
        Task<StudentTeacherLink?> GetStudentTeacherLinkByIdForTeacherAsync(long linkId, long teacherId);

        /// <summary>
        /// Finds a link row by Id only — no teacher scope (tracked). SUPER-ADMIN
        /// ONLY: backs the admin bind/unbind endpoints, where the caller has no
        /// JWT-derived teacherId. The link's own TeacherId column is the source
        /// of truth for which tenant it belongs to; callers resolve it from here
        /// and delegate to the teacher-scoped bind/unbind logic.
        /// </summary>
        Task<StudentTeacherLink?> GetStudentTeacherLinkByIdAsync(long linkId);

        /// <summary>
        /// Loads the Active link rows matching the given ids under a teacher
        /// (tracked, for bulk removal). Ids not owned or not Active are ignored.
        /// </summary>
        Task<IReadOnlyList<StudentTeacherLink>> GetActiveLinksByIdsForTeacherAsync(
            long teacherId, IReadOnlyCollection<long> linkIds);

        /// <summary>
        /// True if any Active link already claims this TeacherStudent roster record.
        /// One roster record can be bound to at most one student account (accept-time guard).
        /// </summary>
        Task<bool> IsTeacherStudentActivelyLinkedAsync(long teacherStudentId);

        /// <summary>
        /// Of the given roster record ids, returns the LinkId of whichever Active
        /// StudentTeacherLink currently claims each one (at most one per record — see
        /// IsTeacherStudentActivelyLinkedAsync). Ids with no claiming link are absent
        /// from the result. Powers a per-row "Unlink" action on the Admin Portal's
        /// student list without an N+1 lookup.
        /// </summary>
        Task<IReadOnlyDictionary<long, long>> GetActiveLinkIdsByTeacherStudentIdsAsync(
            IReadOnlyCollection<long> teacherStudentIds);

        /// <summary>
        /// Of the given roster record ids, returns the subset already claimed by an
        /// Active link. Batch variant used to flag suggestions on the requests inbox.
        /// </summary>
        Task<IReadOnlyList<long>> GetActivelyLinkedTeacherStudentIdsAsync(
            IReadOnlyCollection<long> teacherStudentIds);

        /// <summary>
        /// The single Active <see cref="StudentTeacherLink"/> that currently BINDS the given
        /// roster record under the given teacher, tracked for update. At most one row can
        /// match — the filtered unique index
        /// <c>UX_StudentTeacherLinks_TeacherStudentId_Active</c> guarantees it (see §7.2b).
        ///
        /// Used by the student TEARDOWN path (roster record soft-deleted / purged): the
        /// student account link must be ENDED, otherwise the student app keeps listing the
        /// teacher forever and the live-row filtered index (<c>[LinkStatus] IN (1,3)</c>)
        /// blocks a fresh link request for the same pair.
        /// </summary>
        Task<StudentTeacherLink?> GetActiveStudentTeacherLinkByTeacherStudentIdAsync(
            long teacherId, long teacherStudentId);

        /// <summary>
        /// Active <see cref="ParentChildTeacherLink"/> rows (Method B parent links) bound to
        /// the given roster record under the given teacher, tracked for update. Same teardown
        /// reason as <see cref="GetActiveStudentTeacherLinkByTeacherStudentIdAsync"/>; there is
        /// no uniqueness guarantee here (several children profiles could point at one record),
        /// so this returns a list.
        /// </summary>
        Task<IReadOnlyList<ParentChildTeacherLink>> GetActiveParentChildTeacherLinksByTeacherStudentIdAsync(
            long teacherId, long teacherStudentId);

        /// <summary>
        /// Final safety net before a roster record is HARD-deleted: clears
        /// <c>TeacherStudentId</c> on every remaining <see cref="StudentTeacherLink"/> /
        /// <see cref="ParentChildTeacherLink"/> row that still points at it, regardless of
        /// status. Both FKs are configured <c>SetNull</c>, so the DB would do this anyway —
        /// doing it explicitly (set-based, no tracking) keeps the app's in-memory graph and
        /// the DB in agreement and means the purge never depends on cascade ordering.
        /// Idempotent: a second call updates zero rows.
        /// </summary>
        Task DetachLinksFromPurgedStudentAsync(long teacherStudentId);

        // ══════════════════════════════════════════════
        // TEACHER STUDENT (TEACHER-SCOPED RECORD) QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Finds a teacher's student record by teacher Id, student code, hashed token, and not deleted.
        /// Still used by the Parent module's Method B linking flow (AAM-FR-06.5);
        /// the student-side flow no longer uses credentials.
        /// </summary>
        Task<TeacherStudent?> GetTeacherStudentByLinkingCredentialsAsync(
            long teacherId, string studentCode);

        /// <summary>
        /// Finds a non-deleted roster record by teacher and student code
        /// (case-insensitive via the DB collation). Used to auto-match a link
        /// request to a roster record at accept time.
        /// </summary>
        Task<TeacherStudent?> GetActiveTeacherStudentByCodeAsync(long teacherId, string studentCode);

        /// <summary>
        /// Batch variant: non-deleted roster records for a teacher whose codes are
        /// in the given set. Used to compute suggested matches for the requests inbox.
        /// </summary>
        Task<IReadOnlyList<TeacherStudent>> GetActiveTeacherStudentsByCodesAsync(
            long teacherId, IReadOnlyCollection<string> studentCodes);

        /// <summary>
        /// Finds a non-deleted roster record by Id scoped to the teacher.
        /// Used to validate an explicit accept-time selection.
        /// </summary>
        Task<TeacherStudent?> GetActiveTeacherStudentByIdAsync(long teacherId, long teacherStudentId);

        /// <summary>
        /// Number of the teacher's ACTIVE roster records with no parent phone on file.
        /// Powers the parent-portal summary tile: those students can never be auto-approved,
        /// so every parent request for them lands in the teacher's manual inbox.
        /// </summary>
        Task<int> CountStudentsMissingParentPhoneAsync(long teacherId);

        /// <summary>
        /// Active roster records of one teacher whose <c>ParentPhoneNumber</c> matches ANY of the
        /// supplied variants.
        ///
        /// Roster phones are only <c>.Trim()</c>-ed on write, so the stored format varies
        /// ("01012345678", "+201012345678", "0020…"). The caller therefore passes the plausible
        /// STORED spellings of one normalized number rather than a normalized value, keeping the
        /// query a plain <c>IN</c> that still uses
        /// <c>IX_TeacherStudents_TeacherId_ParentPhoneNumber</c> (a normalizing expression on the
        /// column would make it non-sargable). Parent phones are deliberately NOT unique — one
        /// parent legitimately has several children on the same roster — so this returns a LIST.
        /// </summary>
        Task<IReadOnlyList<TeacherStudent>> GetActiveTeacherStudentsByParentPhoneAsync(
            long teacherId, IEnumerable<string> phoneVariants);

        // ══════════════════════════════════════════════
        // PARENT USER ENTITY QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Finds an active (non-deleted) parent user by Id.
        /// </summary>
        Task<ParentUser?> GetActiveParentUserByIdAsync(long parentUserId);

        /// <summary>
        /// Checks if a ParentUser record already exists for the given UserId.
        /// Used during initialization to prevent duplicates.
        /// </summary>
        Task<bool> ParentUserExistsByUserIdAsync(long userId);

        /// <summary>
        /// Adds a new ParentUser entity.
        /// </summary>
        Task AddParentUserAsync(ParentUser parentUser);

        /// <summary>
        /// Updates an existing ParentUser entity.
        /// </summary>
        Task UpdateParentUserAsync(ParentUser parentUser);

        // ══════════════════════════════════════════════
        // PARENT CHILD QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Counts active children for a specific parent user.
        /// </summary>
        Task<int> CountActiveChildrenAsync(long parentUserId);

        /// <summary>
        /// Retrieves all active children for a specific parent user.
        /// Used by GetDashboardAsync.
        /// </summary>
        Task<IReadOnlyList<ParentChild>> GetActiveChildrenAsync(long parentUserId);

        /// <summary>
        /// Finds an active child by Id and parent Id.
        /// Used for child-specific operations.
        /// </summary>
        Task<ParentChild?> GetActiveChildAsync(long parentUserId, long childId);

        /// <summary>
        /// Checks if a child is already linked to a parent by the student user Id.
        /// Used during Method A child linking to prevent duplicates.
        /// </summary>
        Task<bool> ChildAlreadyLinkedAsync(long parentUserId, long studentUserId);

        /// <summary>
        /// Adds a new ParentChild entity.
        /// </summary>
        Task AddParentChildAsync(ParentChild parentChild);

        /// <summary>
        /// Updates an existing ParentChild entity.
        /// </summary>
        Task UpdateParentChildAsync(ParentChild parentChild);

        // ══════════════════════════════════════════════
        // PARENT CHILD TEACHER LINK QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Retrieves all active teacher links for a specific parent child (Method B).
        /// </summary>
        Task<IReadOnlyList<ParentChildTeacherLink>> GetActiveParentChildTeacherLinksAsync(long parentChildId);

        /// <summary>
        /// Checks if a teacher is already linked to a parent's child (Method B).
        /// </summary>
        Task<bool> ParentChildTeacherLinkExistsAsync(long parentChildId, long teacherId);

        /// <summary>
        /// Finds an active teacher link for a parent's child by teacher Id (Method B).
        /// Used by UnlinkTeacherFromChildAsync.
        /// </summary>
        Task<ParentChildTeacherLink?> GetActiveParentChildTeacherLinkAsync(long parentChildId, long teacherId);

        /// <summary>
        /// Adds a new ParentChildTeacherLink entity.
        /// </summary>
        Task AddParentChildTeacherLinkAsync(ParentChildTeacherLink link);

        /// <summary>
        /// Updates an existing ParentChildTeacherLink entity.
        /// </summary>
        Task UpdateParentChildTeacherLinkAsync(ParentChildTeacherLink link);

        /// Used by StudentUserService.GetLinkedTeachersAsync and ParentUserService.BuildChildDtoAsync
        /// to render teacher entries on the student/parent dashboard without per-teacher loops.
        /// </summary>
        /// <param name="teacherIds">The set of Teacher IDs to load data for.</param>
        /// <returns>A container with all related data keyed by ID for O(1) lookup.</returns>
        Task<TeacherDashboardBatchData> GetTeacherDashboardDataAsync(IReadOnlyList<long> teacherIds);

        /// <summary>
        /// Ownership resolution for the code-based Parent dashboard (Parent Module requirements
        /// §3/§9): returns the owning <c>ParentChild.Id</c> when the given
        /// <paramref name="teacherStudentId"/> under <paramref name="teacherId"/> is reachable by
        /// one of <paramref name="parentUserId"/>'s own ACTIVE children — either Method A (an
        /// active <c>StudentTeacherLink</c> bound to this roster row, whose StudentUserId matches
        /// one of the parent's children) or Method B (an active <c>ParentChildTeacherLink</c> bound
        /// to this exact roster row, owned by one of the parent's children). Null when no such
        /// child exists — the caller's ownership gate.
        ///
        /// This is a pure ownership gate — resolving TeacherCode/StudentCode to
        /// (teacherId, teacherStudentId) is address resolution only; this check is what actually
        /// enforces that a Parent can never reach another Parent's child by guessing valid codes.
        /// </summary>
        Task<long?> ResolveOwnedChildIdByTeacherStudentAsync(long parentUserId, long teacherId, long teacherStudentId);


        // ══════════════════════════════════════════════
        // TEACHER SUBSCRIPTION QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Retrieves the teacher's CURRENT subscription row (IsCurrent = true) or null if none exists.
        /// Replaces the pre-migration GetActiveSubscriptionsByTeacherIdAsync method.
        ///
        /// Status (Active / ExpiringSoon / Expired) is NOT part of the row — callers derive it
        /// using Edvanz.Domain.Helpers.SubscriptionStatusCalculator.Derive(row, DateTime.UtcNow).
        ///
        /// Because the filtered unique index on (TeacherId) WHERE IsCurrent = 1 enforces
        /// the "exactly one current per teacher" invariant (BR-SUB-006), this method
        /// returns a single row — never a list.
        /// </summary>
        Task<TeacherSubscription?> GetCurrentSubscriptionByTeacherIdAsync(long teacherId);

        /// <summary>
        /// Retrieves all subscription rows for a teacher (historical + current).
        /// Used by the payment history endpoint (REQ-SUB-022).
        /// Ordered by EndDate DESC (most recent first).
        /// </summary>
        Task<IReadOnlyList<TeacherSubscription>> GetAllSubscriptionsByTeacherIdAsync(long teacherId);

        /// <summary>
        /// Retrieves all subscriptions across all teachers (no filter).
        /// Used by GetTeachersAsync for the super admin dashboard in-memory join.
        /// Retained with the same signature as before but returns rows WITHOUT the
        /// removed SubscriptionStatus column — callers must derive status via the helper.
        /// </summary>
        Task<IReadOnlyList<TeacherSubscription>> GetAllSubscriptionsAsync();




        // ══════════════════════════════════════════════
        // SUBSCRIPTION MANAGEMENT EXTENSIONS (v1.2)
        // ══════════════════════════════════════════════

        /// <summary>
        /// Returns a lean projection of the teacher's CURRENT subscription, intended
        /// for the ActiveSubscriptionHandler (§8.2) and Redis cache value.
        ///
        /// Returns null when the teacher has never had a subscription OR all rows
        /// are historical (IsCurrent = false). The handler treats null as Expired.
        ///
        /// Status (Active / ExpiringSoon / Expired) is NOT part of the projection —
        /// the handler derives it via SubscriptionStatusCalculator at request time
        /// (lazy evaluation per NFR-SUB-002 / C-6).
        ///
        /// REQ-SUB-NFR-007: This is the query backing the 30-min Redis cache.
        /// </summary>
        Task<CurrentSubscriptionStatusProjection?> GetCurrentSubscriptionStatusAsync(long teacherId);

        /// <summary>
        /// Loads the teacher's current TeacherSubscription row under a pessimistic
        /// lock (WITH (UPDLOCK, HOLDLOCK)) for the duration of the enclosing transaction.
        ///
        /// MUST be called inside an active transaction at IsolationLevel.Serializable
        /// (§6.6). The lock blocks any concurrent reader from selecting the same row
        /// until the transaction commits or rolls back, eliminating the read-modify-write
        /// race between two confirmation flows.
        ///
        /// Returns null when no current subscription exists (first-ever payment).
        /// </summary>
        Task<TeacherSubscription?> GetCurrentSubscriptionForUpdateAsync(long teacherId);

        /// <summary>
        /// Atomically flips the previous current subscription's IsCurrent flag to false
        /// (if one was passed) and inserts a new TeacherSubscription with IsCurrent = true.
        ///
        /// SaveChanges is NOT called here — the caller (SubscriptionService.ConfirmPaymentAsync)
        /// owns the transaction lifecycle and calls SaveChangesAsync explicitly so that
        /// EF Core's optimistic-concurrency check on RowVersion runs in the right place
        /// for the bounded retry to catch DbUpdateConcurrencyException.
        ///
        /// The filtered unique index IX_TeacherSubscriptions_Current is the hard guarantee:
        /// if two transactions race past EF's check, the second INSERT fails with SQL 2601
        /// and the service rolls back + retries.
        /// </summary>
        /// <param name="previousCurrent">The row whose IsCurrent flag to clear, or null if none.</param>
        /// <param name="newSubscription">The new IsCurrent = true row to insert.</param>
        Task FlipCurrentAndInsertNewAsync(
            TeacherSubscription? previousCurrent,
            TeacherSubscription newSubscription);

        /// <summary>
        /// Eligibility scan for the daily reminder dispatcher (§7.2).
        /// Returns one row per teacher whose CURRENT subscription's EndDate falls within
        /// the alert window (D-5 through D-0) relative to the supplied "today".
        ///
        /// "Today" is the dispatcher's reference date in UTC; AlertDay is computed as
        /// (EndDate.Date - today).Days and is in {0..5}.
        ///
        /// Scoped to IsCurrent = true so historical rows never produce phantom alerts.
        /// </summary>
        Task<IReadOnlyList<UpcomingExpiryProjection>> GetTeachersWithUpcomingExpiryAsync(DateTime today);

        /// <summary>
        /// Per-teacher reminder worker query (§7.3). Loads the minimum data the worker
        /// needs to render a localized push + WhatsApp template:
        /// teacher Id, owning user Id, full name, phone number, and language preference.
        ///
        /// Returns null if the teacher does not exist or is soft-deleted.
        /// </summary>
        Task<TeacherReminderProjection?> GetTeacherForReminderAsync(long teacherId);/// <summary>
                                                                                    /// Finds a teacher by Id, bypassing the global soft-delete filter, so a Suspended
                                                                                    /// (DeletedAt != null) teacher can be reloaded and reactivated. Tracked for update.
                                                                                    /// </summary>
        Task<Teacher?> GetTeacherByIdIncludingDeletedAsync(long teacherId);
        // (UpdateCapacityPackagePriceAsync was removed 2026-07-17 with the retired
        // per-package price endpoint — renewal pricing is per-student now.)

        /// <summary>
        /// Finds an active (non-deleted) student user by their underlying User.Id.
        ///
        /// Distinct from <see cref="GetActiveStudentUserByIdAsync"/>: this takes the
        /// User table's primary key (which the JWT carries as the principal id), not
        /// the StudentUser table's primary key. Used by the VCM student controller
        /// to resolve the JWT's User.Id to a StudentUser.Id, which then drives the
        /// StudentTeacherLink lookup for the targeted teacher.
        /// </summary>
        Task<StudentUser?> GetActiveStudentUserByUserIdAsync(long userId);
        /// <summary>
        /// Returns the teacher's display name (User.FullName) for the given Teacher Id,
        /// or null if no such teacher exists. Lean single-column projection used by report
        /// headers (REQ-USR-030).
        /// </summary>
        Task<string?> GetTeacherDisplayNameAsync(long teacherId);
        /// <summary>
        /// Finds an active (non-deleted) parent user by their underlying User.Id.
        /// Distinct from <see cref="GetActiveParentUserByIdAsync"/>, which takes the
        /// ParentUser table PK — this takes the User table PK carried by the JWT,
        /// mirroring <see cref="GetActiveStudentUserByUserIdAsync"/>. Used by the
        /// parent attendance controller to resolve the JWT principal to a ParentUser.
        /// </summary>
        Task<ParentUser?> GetActiveParentUserByUserIdAsync(long userId);
        // ══════════════════════════════════════════════
        // DIRECT CHAT — ELIGIBILITY GATE QUERIES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Returns the <see cref="UserType"/> for a given User.Id, or null when
        /// no user exists with that id. Used by the chat eligibility gate to determine
        /// whether a Student is involved before running link-graph checks.
        /// </summary>
        Task<UserType?> GetUserTypeByUserIdAsync(long userId);

        /// <summary>
        /// Returns true when an active <see cref="StudentTeacherLink"/> exists between
        /// the student (identified by User.Id) and the teacher (identified by User.Id).
        /// Resolves: student User.Id → StudentUser.Id → StudentTeacherLink.TeacherId,
        ///           teacher User.Id → Teacher.Id.
        /// </summary>
        Task<bool> AreStudentAndTeacherLinkedByUserIdsAsync(
            long studentUserId, long teacherUserId);

        /// <summary>
        /// Returns true when an active Method-A <see cref="ParentChild"/> record links
        /// the student (User.Id) to the parent (User.Id). Method-B children have no
        /// StudentUser account and cannot participate in chat.
        /// Resolves: student User.Id → StudentUser.Id → ParentChild.StudentUserId,
        ///           parent User.Id → ParentUser.Id → ParentChild.ParentUserId.
        /// </summary>
        Task<bool> AreStudentAndParentLinkedByUserIdsAsync(
            long studentUserId, long parentUserId);

        /// <summary>
        /// Returns true when an active <see cref="StudentTeacherLink"/> exists between
        /// the student (User.Id) and the teacher that OWNS the assistant (User.Id).
        /// Resolves: assistant User.Id → Assistant.TeacherAccountId (Teacher.Id),
        ///           student User.Id → StudentUser.Id,
        ///           then checks StudentTeacherLink(StudentUser.Id, TeacherId).
        /// </summary>
        Task<bool> AreStudentAndAssistantLinkedByUserIdsAsync(
            long studentUserId, long assistantUserId);

        // ── NAME RESOLUTION ──────────────────────────────────────────────────

        /// <summary>
        /// Returns User.FullName for a single User.Id. Null when the user does not exist.
        /// Single-column projection; no entity load.
        /// </summary>
        Task<string?> GetUserFullNameByUserIdAsync(long userId);

        /// <summary>
        /// Returns a FullName dictionary keyed by User.Id for the supplied ids.
        /// Single round-trip: WHERE Id IN (...) SELECT Id, FullName.
        /// Used by the chat thread mapping to resolve sender names without N+1 queries.
        /// </summary>
        Task<Dictionary<long, string>> GetUserFullNamesByUserIdsAsync(
            IEnumerable<long> userIds);

        /// <summary>
        /// Lightweight Id + FullName projection for every active teacher (AccountStatus = Active,
        /// DeletedAt == null), ordered by name. No pagination — backs select/dropdown UI
        /// (e.g. Super Admin filters) where the full TeacherListItemDto shape is unnecessary.
        /// </summary>
        Task<IReadOnlyList<TeacherNameLookupProjection>> GetTeacherNameLookupAsync();
        /// <summary>
        /// Returns the recipient's <c>LanguagePreference</c> ("ar"/"en") for a single
        /// User.Id. <c>LanguagePreference</c> lives on the role entity, not User, so this
        /// resolves the role via User.UserType then reads the matching role row. Null when
        /// the user does not exist, has no role-level preference, or is a role that carries
        /// none (e.g. SuperAdmin); callers treat null as the default culture.
        /// Used by <c>ChatPushJob</c> to render the push title under the recipient's culture (🔴-2).
        /// </summary>
        Task<string?> GetUserLanguagePreferenceByUserIdAsync(long userId);

        // ══════════════════════════════════════════════
        // STUDENT ACCOUNTS — SUPER-ADMIN PAGINATED LIST
        // ══════════════════════════════════════════════

        /// <summary>
        /// Pages ALL StudentUser accounts on the platform (not scoped to any teacher) for
        /// the SuperAdmin "Student Accounts" screen. Excludes soft-deleted accounts via the
        /// StudentUser/User query filters (no explicit predicate needed here).
        ///
        /// <paramref name="search"/> matches (case-insensitive, partial): the account's
        /// <c>User.FullName</c>, OR the per-teacher <c>TeacherStudent.StudentCode</c> of any of
        /// its ACTIVE teacher links. <paramref name="teacherId"/>, when supplied, restricts the
        /// result to accounts holding an ACTIVE <see cref="Entities.StudentTeacherLink"/> to that
        /// teacher. Ordered by <c>CreateAt</c> descending (newest account first), matching the
        /// Teacher admin list's default ordering.
        ///
        /// Returns identity rows ONLY — call
        /// <see cref="GetActiveLinkedTeachersForStudentUsersAsync"/> with the returned ids to
        /// batch-load each account's linked teachers without an N+1.
        /// </summary>
        Task<(IReadOnlyList<StudentAccountRow> Items, int TotalCount)> GetStudentAccountsPagedAsync(
            string? search, long? teacherId, int page, int pageSize);

        /// <summary>
        /// Batch-loads every ACTIVE <see cref="Entities.StudentTeacherLink"/> — teacher identity
        /// plus the bound per-teacher student code — for the given set of StudentUser ids, in one
        /// round trip. Used by <see cref="GetStudentAccountsPagedAsync"/>'s caller to enrich a page
        /// of student accounts with their linked-teachers list (group the result by
        /// <see cref="StudentAccountLinkedTeacherRow.StudentUserId"/>).
        /// </summary>
        Task<IReadOnlyList<StudentAccountLinkedTeacherRow>> GetActiveLinkedTeachersForStudentUsersAsync(
            IReadOnlyList<long> studentUserIds);

    }
    // ══════════════════════════════════════════════════════════════════
    // PROJECTIONS used by IUserRepo subscription methods.
    // Live in the Domain layer alongside the interface (precedent:
    // PagedAttendanceStudentRow next to IAttendanceRepo).
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lean projection of a teacher's CURRENT subscription for the policy handler
    /// and Redis cache. Excludes the encrypted blob and other heavy fields.
    /// </summary>
    public class CurrentSubscriptionStatusProjection
    {
        public long SubscriptionId { get; set; }
        public long TeacherId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public decimal AmountPaidEGP { get; set; }
        public long? StudentCapacityPackageId { get; set; }

        /// <summary>
        /// Full or Managerial. Read by SubscriptionGateService.IsManagerialAsync to
        /// block student/parent linking on a managerial subscription. Backfilled to
        /// Full for pre-existing rows, so a zero/missing value is treated as Full.
        /// </summary>
        public SubscriptionPlanType PlanType { get; set; } = SubscriptionPlanType.Full;
    }
    /// <summary>
    /// Lean Id/Name row for teacher select-dropdown lists. Deliberately excludes every
    /// other Teacher/User field — this projection exists only to avoid pulling full
    /// entities for a UI control that needs nothing else.
    /// </summary>
    public class TeacherNameLookupProjection
    {
        public long TeacherId { get; set; }
        public string FullName { get; set; } = null!;
    }
    /// <summary>
    /// One row per teacher eligible for a reminder on the dispatcher run.
    /// </summary>
    public class UpcomingExpiryProjection
    {
        public long TeacherId { get; set; }
        public DateTime SubscriptionEndDate { get; set; }
        public int DaysUntilExpiry { get; set; }
    }

    /// <summary>
    /// Minimum data the per-teacher reminder worker needs to render and dispatch
    /// a localized notification across both channels.
    /// </summary>
    public class TeacherReminderProjection
    {
        public long TeacherId { get; set; }
        public long UserId { get; set; }
        public string FullName { get; set; } = null!;
        public string? PhoneNumber { get; set; }
        public string? LanguagePreference { get; set; }
    }

}