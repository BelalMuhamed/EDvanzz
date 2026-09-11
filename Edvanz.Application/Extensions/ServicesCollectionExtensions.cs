using Edvanz.Application.Excel;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Application.Services;
using Edvanz.Application.Services.Help;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.ServiceContract;
using Edvanz.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace Edvanz.Application.Extensions;

/// <summary>
/// Registers Application layer services into the DI container.
/// All services are registered as Scoped (one instance per HTTP request).
/// </summary>
public static class ServicesCollectionExtensions
{
    /// <summary>
    /// Adds all Application layer services to the dependency injection container.
    /// Includes: authentication services, user module services, type-specific services,
    /// student module service, and localization configuration.
    /// </summary>
    /// <param name="services">The service collection to register services into.</param>
    public static void AddApplication(this IServiceCollection services)
    {

        #region Services
        services.AddScoped<ISmsService, SmsService>();
        // FIX B5: AuthService now requires IPasswordService in its constructor
        // (previously it was missing, causing the plain-text vs hash comparison bug)
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPasswordService, PasswordService>();
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IAuditContext, AuditContext>();   // ← add

        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IEncryptionService, EncryptionService>();

        // FIX I1/I3: Renamed from IuserService to IUserService
        // FIX I1: Interface moved from Edvanz.Domain.ServiceContract to Edvanz.Application.ServiceContract
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IStudentUserService, StudentUserService>();
        // Aggregated student "teacher home" screen — composes attendance/payment/videos/exams
        // behind the teacher's per-module student-visibility flags (one call for the whole page).
        services.AddScoped<IStudentTeacherHomeService, StudentTeacherHomeService>();
        services.AddScoped<IHelpContentService, HelpContentService>();
        // Student-teacher link request/approval flow: teacher-side ops + shared notifier
        services.AddScoped<ITeacherStudentLinkService, TeacherStudentLinkService>();
        services.AddScoped<IStudentLinkNotifier, StudentLinkNotifier>();
        services.AddScoped<IStudentDeviceLockService, StudentDeviceLockService>();
        services.AddScoped<IParentUserService, ParentUserService>();
        // Shared parent SECTION builders (attendance / payments / videos / homework / grades).
        // Registered before its two consumers below — the parent mobile dashboard and the public
        // parent portal both compose through this one implementation, never a fork.
        services.AddScoped<IParentSectionComposer, ParentSectionComposer>();
        services.AddScoped<IParentDashboardService, ParentDashboardService>();
        // Public parent portal (parent.edvanz.io): the anonymous, portal-key-gated read surface
        // plus the teacher-side inbox/approval half.
        services.AddScoped<IParentPortalService, ParentPortalService>();
        services.AddScoped<ITeacherParentPortalService, TeacherParentPortalService>();
        services.AddScoped<ITokenService, TokenService>();
        // Free-tier quota gate (shared by student/session/assistant/group create paths)
        services.AddScoped<ISubscriptionGateService, SubscriptionGateService>();
        // Student Module (Module 1: teacher-scoped student records CRUD)
        services.AddScoped<ITeacherStudentService, TeacherStudentService>();
        // Shared student teardown (unassign + end account/parent links on delete; blocking-FK
        // cleanup before a permanent purge). Depends only on IUnitOfWork + the link notifier so
        // it can be reused by the student, departure and recycle-bin-purge flows without a cycle.
        services.AddScoped<IStudentTeardownService, StudentTeardownService>();
        // Student barcode presentation (in-app SVG + printable PDF export, REQ-STU-052)
        services.AddScoped<IStudentBarcodeService, StudentBarcodeService>();
        services.AddScoped<ITeacherService, TeacherService>();
        // Session Module (Module 2: sessions, groups, membership links)
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<IAssistantService, AssistantService>();
        services.AddScoped<IUserPermissionService, UserPermissionService>();
        services.AddScoped<IAttendanceNotifier, AttendanceNotifier>();
        services.AddScoped<IPaymentNotifier, PaymentNotifier>();
        services.AddScoped<IExamHomeworkNotifier, ExamHomeworkNotifier>();


        // Payment Module (Module 4: payment collection, editing, wallets, dashboard, reports)
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IPaymentReportService, PaymentReportService>();
        // Payment "Screens" API (api/v1/*) — screen-oriented BFF endpoints (frontend payment.json)
        services.AddScoped<IPaymentScreenService, PaymentScreenService>();
        // Event Payment Module (Module 5: one-time event payments)
        services.AddScoped<IEventPaymentService, EventPaymentService>();
        // Profile Permission Module 
        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<IAudittrialService,AuditTrialService>();
        services.AddScoped<ExcelService>();
        //messaging
         services.AddScoped<IMessagingChannelService, MessagingChannelService>();
        services.AddScoped<IMessageTemplateService, MessageTemplateService>();
        services.AddScoped<IAutomatedTriggerService, AutomatedTriggerService>();
        services.AddScoped<IMessageDispatcher, MessageDispatcher>();
        services.AddScoped<IMessageLogService, MessageLogService>();
        services.AddScoped<IBlockResolver, BlockResolver>();
        services.AddScoped<ISmsSender,SmsSender>();             
        services.AddScoped<IEncryptionService, EncryptionService>();
        services.AddScoped<IMessageSenderJob, MessageSenderJob>();

        // Central file registry — gated reads, attach/detach lifecycle, gated-URL builder.
        services.AddScoped<IFileAccessService, FileAccessService>();
        // Generic file upload (images + PDF → private uploads container + registry row).
        services.AddScoped<IFileUploadService, FileUploadService>();

        #endregion

        // ════════════════════════════════════════════════
        // SUBSCRIPTION MANAGEMENT MODULE (Module 11 — v1.2)
        // ════════════════════════════════════════════════

        // Teacher-facing flow (§4.1 / §6.3 confirm pipeline / §6.4 initiate)
        services.AddScoped<ISubscriptionService, SubscriptionService>();

        // Super-admin operations (§4.4 / FR-SUB-060…064)
        services.AddScoped<IAdminSubscriptionService, AdminSubscriptionService>();

        // Mobile-app version gate — DB-first (SuperAdmin-editable), options-fallback.
        services.AddScoped<IAppVersionService, AppVersionService>();

        // Center tenancy tier — SuperAdmin provisioning + center self-service (teacher management).
        services.AddScoped<IAdminCenterService, AdminCenterService>();
        services.AddScoped<ICenterService, CenterService>();
        services.AddScoped<IAdminCenterSubscriptionService, AdminCenterSubscriptionService>();
        services.AddScoped<ICenterSubscriptionService, CenterSubscriptionService>();
        services.AddScoped<ICenterRevenueService, CenterRevenueService>();
        services.AddScoped<ICenterAssistantService, CenterAssistantService>();
        services.AddScoped<ITeacherIndependenceService, TeacherIndependenceService>();

        // Reminder dispatcher + per-teacher worker (§7)
        services.AddScoped<ISubscriptionReminderService, SubscriptionReminderService>();

        // Bell-icon inbox + FCM token registration (§4.2 / FR-SUB-050…054)
        services.AddScoped<INotificationHistoryService, NotificationHistoryService>();
        // ── Hangfire job implementations (Phase 08) ──
        // These are activated by Hangfire's job activator via DI. Scoped lifetime
        // so each job execution gets a fresh DbContext.
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IChatPushJob, ChatPushJob>();
        // Content-publish announcements (video / online exam). The JOB lives here;
        // its dispatcher is registered in Infrastructure, which owns Hangfire.
        services.AddScoped<IContentPublishNotificationJob, ContentPublishNotificationJob>();
        services.AddScoped<ISubscriptionReminderJob, SubscriptionReminderJob>();
        services.AddScoped<IRenewalNotificationJob, RenewalNotificationJob>();
        services.AddScoped<IPendingPaymentRejectedNotificationJob, PendingPaymentRejectedNotificationJob>();
        services.AddScoped<ICapacityRequestResolvedNotificationJob, CapacityRequestResolvedNotificationJob>();
        // ── Exams & Homework Module — recurrence materializer (Phase 6) ──
        // Pure business logic for generating the next occurrence of a recurring
        // template. The Hangfire dispatcher and worker (registered in
        // InfrastructureServiceExtensions) call into this service.
        services.AddScoped<IExamHomeworkService, ExamHomeworkService>();
        // Offline Exams module (clean /api/exams surface — create, per-session occurrences, dates)
        services.AddScoped<IExamService, ExamService>();
        // Attendance→exam sync (during-session exams mirror session attendance)
        services.AddScoped<IExamAttendanceSyncService, ExamAttendanceSyncService>();
        services.AddScoped<IAssignmentScopeResolver, AssignmentScopeResolver>();
        services.AddScoped<IRecurringAssignmentMaterializerService,
                           RecurringAssignmentMaterializerService>();
        // Nightly auto-absent sweep — writes Absent for unmarked/held rosters once the equivalence
        // slot has passed (business logic; the Infrastructure job is a thin dispatcher over this).
        services.AddScoped<IAttendanceAutoAbsentService, AttendanceAutoAbsentService>();
        // Admin Insights — nightly usage rollup (cadence / depth / operator mix / setup health).
        services.AddScoped<ITeacherUsageRollupService, TeacherUsageRollupService>();
        // Admin Insights — the read side (overview, usage grid, Teacher 360, sales, notes).
        services.AddScoped<IAdminInsightsService, AdminInsightsService>();

        services.Configure<RequestLocalizationOptions>(options =>
        {
            var supportedCultures = new[] { "en", "ar" };
            options.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("en");
            options.SupportedCultures = supportedCultures.Select(c => new CultureInfo(c)).ToList();
            options.SupportedUICultures = supportedCultures.Select(c => new CultureInfo(c)).ToList();

            options.RequestCultureProviders = new List<Microsoft.AspNetCore.Localization.IRequestCultureProvider>
        {
            new Microsoft.AspNetCore.Localization.AcceptLanguageHeaderRequestCultureProvider()
        };
        });


        #region delete assistant Assign back ground service 
        
        #endregion  
    }
}