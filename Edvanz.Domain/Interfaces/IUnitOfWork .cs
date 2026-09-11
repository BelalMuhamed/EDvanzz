using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Edvanz.Domain.Interfaces
{
    public interface IUnitOfWork
    {
        IGenericRepo<T, Tkey> GetRepository<T, Tkey>()
                where T : class where Tkey : IEquatable<Tkey>;
        Task<int> SaveChangesAsync();
        Task BeginTransactionAsync();
        /// <summary>
        /// Begins a database transaction at the specified isolation level.
        /// Required by SubscriptionService.ConfirmPaymentAsync (§6.6) which needs
        /// IsolationLevel.Serializable to safely combine pessimistic UPDLOCK reads
        /// with the optimistic-concurrency RowVersion check on TeacherSubscription.
        ///
        /// Use this overload only when an isolation level stronger than the default
        /// (READ COMMITTED) is required. Most flows use the parameterless overload.
        /// </summary>
        Task BeginTransactionAsync(System.Data.IsolationLevel isolationLevel);
        Task RollbackAsync();
        Task CommitAsync();
        //Task LogError(Exception ex);

        /// <summary>
        /// Extended repository for the User module ecosystem (User, Teacher, StudentUser, ParentUser, linking).
        /// </summary>
        IUserRepo Users { get; }
        IUserPermissionRepo UsersPermissions { get; }
        IRefreshTokenRepo RefreshTokenRepo { get; }
        IgoogleUserRepo googleUserRepo { get; }



        /// <summary>
        /// Extended repository for the Student Module (Module 1: teacher-scoped student records).
        /// Handles CRUD, search, filter, recycle bin, code generation, and bulk operations
        /// for TeacherStudent records.
        /// 
        /// IMPORTANT: This is separate from IUserRepo. The Student Module manages
        /// teacher-owned student DATA records, not StudentUser accounts.
        /// </summary>
        ITeacherStudentRepo Students { get; }

        /// <summary>
        /// Extended repository for the Session Module (Module 2: sessions, groups, links).
        /// Handles CRUD, search, filter, group management, and membership linking
        /// for Session, SessionGroup, and SessionLink records.
        /// </summary>
        ISessionRepo SessionsRepo { get; }
        IAssitantRepo AssistantRepo { get; }

        /// <summary>
        /// Extended repository for the Attendance Module (Module 3: attendance records,
        /// session occurrences, student assignments, absence counters, edit logs).
        /// Handles take attendance, edit attendance, absence tracking, cross-session
        /// attendance, and all attendance reporting queries.
        /// </summary>
        IAttendanceRepo AttendanceRepo { get; }

        IModuleTeacherRepo? ModuleTeacherRepo { get; }
        /// <summary>
        /// Extended repository for the Payment Module (Module 4) and Event Payment Module (Module 5).
        /// Handles payment collection, editing, deletion, unpaid overview, custom amounts,
        /// assistant wallets, dashboard, departure, transfer, offline sync, event payments,
        /// and all payment reporting queries.
        /// </summary>
        IPaymentRepo PaymentsRepo { get; }

        /// <summary>
        /// Returns true if a database transaction is currently active.
        /// 
        /// Used by type-specific services (Teacher, Student, Parent) to detect
        /// whether they are being called inside an outer transaction (e.g., from
        /// the User module's registration flow). When true, the service should
        /// NOT call BeginTransactionAsync/CommitAsync/RollbackAsync — the caller
        /// owns the transaction lifecycle.
        /// 
        /// This prevents nested transaction issues and ensures that if the
        /// type-specific initialization fails, the entire registration
        /// (including the Users row) is rolled back atomically.
        /// </summary>
        bool HasActiveTransaction { get; }



        ITemplateRepo templateRepo { get; }
        ITemplatePermissionRepo templatePermissionsRepo { get; }
        ITemplateAssistantsRepo templateAssistantsRepo { get; }
        IPermissionRepo permissionRepo { get; }

        IAuditTrialRepo auditTrialRepo { get; }
        IStudentTeacherLinkRepo studentTeacherLinkRepo { get; }

        IMessagingChannelRepo messagingChannelRepo { get; }
        IMessageTemplateRepo messageTemplateRepo { get; }
        IAutomatedTriggerRepo automatedTriggerRepo { get; }
        IMessageLogRepository messageLogRepo { get; }


        // ══════════════════════════════════════════════
        // SUBSCRIPTION MANAGEMENT MODULE (Module 11 — v1.2)
        // ══════════════════════════════════════════════

        /// <summary>
        /// Pending subscription-renewal payments. Owns the in-flight queue between
        /// initiate and Confirmed/Rejected/Expired terminal states.
        /// </summary>
        ISubscriptionPaymentRepo SubscriptionPaymentsRepo { get; }

        /// <summary>
        /// Subscription-reminder idempotency log (D-5 through D-0 alerts).
        /// </summary>
        ISubscriptionAlertRepo SubscriptionAlertsRepo { get; }

        /// <summary>
        /// Teacher-initiated capacity-increase requests (Pending → Approved/Rejected/Cancelled).
        /// Raising Teacher.StudentCapacity is admin-approved because capacity drives the
        /// per-student subscription price.
        /// </summary>
        ICapacityIncreaseRequestRepo CapacityRequestsRepo { get; }

        /// <summary>
        /// Teacher-initiated NEW-subscription requests (new tutors / renewals choosing a plan +
        /// student count; Pending → Approved/Rejected/Cancelled). Approval activates the plan.
        /// </summary>
        ISubscriptionRequestRepo SubscriptionRequestsRepo { get; }

        /// <summary>
        /// Single-row per-student pricing settings (renewal price = capacity × rate).
        /// </summary>
        ISubscriptionPricingRepo SubscriptionPricingRepo { get; }

        /// <summary>
        /// In-app push-notification inbox per user (bell-icon feed).
        /// </summary>
        IUserNotificationRepo UserNotificationsRepo { get; }

        /// <summary>
        /// Firebase FCM device-token registry per user.
        /// </summary>
        IUserDeviceTokenRepo UserDeviceTokensRepo { get; }
        /// <summary>
        /// Extended repository for the Exams &amp; Homework Module (Module 6).
        /// Handles assignment templates, scopes, occurrences, student obligations,
        /// audit logs, deletion logs, the Assignment Tracking View, the Grade Entry View,
        /// barcode-scan idempotent updates, student history, and all assignment reports.
        /// </summary>
        IExamHomeworkRepo ExamHomeworkRepo { get; }

        /// <summary>
        /// Extended repository for the Video Content Management Module (Module 14).
        /// Handles every read and write across the five VCM entities:
        /// <c>VideoAsset</c>, <c>VideoScope</c>, <c>VideoAnalytics</c>,
        /// <c>VideoWatchEvent</c>, and <c>VideoAssetAudit</c>.
        ///
        /// Endpoints served: video CRUD, scope management, student start/stop,
        /// teacher analytics report, parent view, audit snapshots, and the admin
        /// teacher-purge integration hook.
        /// </summary>
        IVideoAssetRepo VideoAssetsRepo { get; }

        /// <summary>
        /// Extended repository for <c>VideoUnit</c> (Track C / G-UNIT) — a
        /// distinct sub-aggregate from <see cref="VideoAssetsRepo"/>.
        /// </summary>
        IVideoUnitRepo VideoUnitsRepo { get; }
        /// <summary>
        /// 1:1 direct-chat repository — conversations and messages.
        /// </summary>
        IChatRepo ChatRepo { get; }

        /// <summary>
        /// Reference table of per-module free-tier creation quotas (see ModuleQuotaKeys).
        /// </summary>
        IModuleQuotaRepo ModuleQuotaRepo { get; }

        /// <summary>
        /// Admin Insights — every query behind the nightly usage rollup (cadence, module depth,
        /// operator mix, setup health) and the storage for its two derived tables.
        /// </summary>
        ITeacherUsageRepo TeacherUsageRepo { get; }

        /// <summary>
        /// Admin Insights — the read side: usage grid, overview aggregates, insight-card teacher
        /// lists, and sales-rep performance. Reads the pre-computed snapshot, never the raw tables.
        /// </summary>
        IAdminInsightsRepo AdminInsightsRepo { get; }

        /// <summary>Online Exam Module — teacher-side aggregate (exam, questions, options, scopes).</summary>
        IOnlineExamRepo OnlineExamsRepo { get; }

        /// <summary>Online Exam Module — student-report aggregate (own aggregate root, §1).</summary>
        IStudentOnlineExamReportRepo StudentOnlineExamReportsRepo { get; }

        /// <summary>Video Module — student video-quiz attempt aggregate (own aggregate root; video twin of the online-exam report repo).</summary>
        IStudentVideoExamReportRepo StudentVideoExamReportsRepo { get; }

        /// <summary>Central file registry — one row per uploaded file, gated by <c>GET /api/files/{fileId}</c>.</summary>
        IFileObjectRepo FileObjectsRepo { get; }

        /// <summary>Help / Onboarding content lookup — modules, tours, articles, FAQs (SuperAdmin-managed, global).</summary>
        IHelpContentRepo HelpContentRepo { get; }

        /// <summary>
        /// Center tenancy tier — the Center account, login/assistant resolution, acting-as
        /// membership checks, quota-enforcement counts, and current-subscription lookup.
        /// </summary>
        ICenterRepo Centers { get; }

        /// <summary>
        /// Per-platform mobile-app version gate (runtime-editable; DB-first, options-fallback).
        /// </summary>
        IAppVersionConfigRepo AppVersionConfigs { get; }

        /// <summary>
        /// Public parent-portal grants (parent.edvanz.io) — one row per (roster student, device),
        /// request/approval lifecycle, teacher inbox, follower list and abuse caps.
        /// </summary>
        IParentPortalAccessRepo ParentPortalAccesses { get; }
    }
}