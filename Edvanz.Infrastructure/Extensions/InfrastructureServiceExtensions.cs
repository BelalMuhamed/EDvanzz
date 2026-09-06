using Edvanz.Application.IservicesContract;
using Edvanz.Application.Options;
using Edvanz.Application.ServiceContract;
using Edvanz.Application.Services;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.BackGroundJobs;
using Edvanz.Infrastructure.Services;
using FluentAssertions.Common;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using static Azure.Core.HttpHeader;
using System.Net.Http;
using TimeZoneService = Edvanz.Application.Services.TimeZoneService;
namespace Edvanz.Infrastructure.Extensions;

/// <summary>
/// Registers Infrastructure layer services into the DI container.
/// Called from Program.cs alongside AddApplication().
/// </summary>
public static class InfrastructureServiceExtensions
{
    public static void AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Teacher code generator (AAM-FR-03.3 / AAM-NFR-03)
        services.AddScoped<ITeacherCodeGenerator, TeacherCodeGenerator>();

        // Center code generator (8-digit shareable center code)
        services.AddScoped<ICenterCodeGenerator, CenterCodeGenerator>();

        // Student account code generator (AAM-FR-05.3 — StudentUser account code)
        services.AddScoped<IStudentAccountCodeGenerator, StudentAccountCodeGenerator>();

        // Student code generator (REQ-STU-007 — teacher-scoped A1→Z999 sequential codes)
        services.AddScoped<IStudentCodeGenerator, StudentCodeGeneratorService>();

        // Session name generator(REQ-SES - 002 — teacher - scoped Session A1→Z999 sequential names)
        services.AddScoped<ISessionNameGenerator, SessionNameGeneratorService>();

        // Occurrence generator (REQ-ATT-001/002 — computes session occurrence dates from recurrence rules)
        services.AddScoped<IOccurrenceGeneratorService, OccurrenceGeneratorService>();

        // FIX 1.5: Timezone service — provides teacher-local date/time for Egyptian tutors.
        // Resolves the UTC midnight boundary bug where DateTime.UtcNow.Date returns the wrong
        // "today" between midnight and 2 AM Cairo time.
        services.AddScoped<ITimeZoneService, TimeZoneService>();

        // FIX 4.2: Report export service — generates PDF/Excel files for attendance reports.
        // REQ-ATT-041: Reports exportable as PDF or Excel.
        // REQ-ATT-081: Timeline exportable as PDF or Excel.
        // TODO: Uncomment when AttendanceReportExportService is implemented with ClosedXML/QuestPDF.
        services.AddScoped<IAttendanceReportExportService, AttendanceReportExportService>();

        // Payment Module export service — generates PDF/Excel files for payment reports.
        // REQ-PAY-050: Reports exportable as PDF or Excel.
        // REQ-EVT-025: Event reports exportable as PDF or Excel.
        // TODO: Replace stub with ClosedXML/QuestPDF implementation when packages are added.
        services.AddScoped<IPaymentReportExportService, PaymentReportExportService>();
        // Direct Chat — dispatcher (holds IBackgroundJobClient; Infrastructure only)
        services.AddScoped<IChatPushDispatcher, ChatPushDispatcher>();
        // Content-publish announcements — dispatcher (holds IBackgroundJobClient)
        services.AddScoped<IContentPublishNotificationDispatcher, ContentPublishNotificationDispatcher>();
        // ════════════════════════════════════════════════
        // SUBSCRIPTION MANAGEMENT MODULE — Phase 05 (v1.2)
        // ════════════════════════════════════════════════

        // ── Bind options sections from appsettings.json ──
        services.Configure<FirebaseOptions>(configuration.GetSection(FirebaseOptions.Section));
        services.Configure<SubscriptionCacheOptions>(configuration.GetSection(SubscriptionCacheOptions.Section));
        services.Configure<ReminderSchedulerOptions>(configuration.GetSection(ReminderSchedulerOptions.Section));
        services.Configure<SubscriptionDefaultsOptions>(configuration.GetSection(SubscriptionDefaultsOptions.Section));
        services.Configure<AzureBlobStorageOptions>(configuration.GetSection(AzureBlobStorageOptions.Section));

        // ── Live permission / module-revocation cache options (REQ-USR-013 / BR-ADM-010) ──
        services.Configure<UserAuthCacheOptions>(configuration.GetSection(UserAuthCacheOptions.Section));

        // (The Paymob payment gateway and its stub/selection block were removed 2026-07-17 —
        // the renewal flow is manual-only; renew/initiate always returns the manual payload.)

        // ── Firebase push sender (real — there is no stub mode for push) ──
        // The class is safe to register even if FirebaseOptions.CredentialsPath is
        // unset; initialization happens lazily on first SendAsync, and a missing
        // credential surfaces as a clear InvalidOperationException at that point.
        services.AddSingleton<IPushNotificationSender, FirebasePushNotificationSender>();

        // ── Redis subscription-status cache ──
        // Uses StackExchange.Redis when a connection string is configured;
        // falls back to in-memory IDistributedCache when "Redis:Connection" is empty.
        string? redisConnection = configuration["Redis:Connection"];
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddStackExchangeRedisCache(opts =>
            {
                opts.Configuration = redisConnection;
                opts.InstanceName = "edvanz:";
            });
        }
        else
        {
            // Dev fallback. EC-19 still works — IDistributedCache exceptions are caught
            // by RedisSubscriptionCacheService regardless of the underlying provider.
            services.AddDistributedMemoryCache();
        }

        services.AddScoped<ISubscriptionCacheService, RedisSubscriptionCacheService>();

        // Live permission / module-revocation cache — same IDistributedCache backend
        // as the subscription cache, separate key namespace (auth:user:{id} vs
        // subscription:teacher:{id}) and separate invalidation triggers.
        // (REQ-USR-013 / REQ-USR-027 / REQ-USR-008 / BR-ADM-010)
        services.AddScoped<IUserAuthCacheService, RedisUserAuthCacheService>();

        // Idempotency guard for the api/v1 money endpoints (double-submit protection). Same
        // IDistributedCache backend, separate "idem:" key namespace. Fails open on cache errors.
        services.AddScoped<Edvanz.Application.ServiceContract.IIdempotencyService, RedisIdempotencyService>();

        // Two-step invalidation orchestrator (stamp bump + cache drop). Every
        // service that mutates a user's effective access calls this — keeps the
        // two steps coupled at a single call site so neither can be forgotten.
        services.AddScoped<IUserAuthInvalidationService, UserAuthInvalidationService>();

        // Super-admin per-tutor module activation (REQ-ADM-034 through REQ-ADM-038).
        // Writes TutorModuleAccess + audit trail + invalidates tutor and assistants
        // in one transaction.
        services.AddScoped<ITutorModuleAccessService, TutorModuleAccessService>();
        services.AddScoped<SubscriptionReminderDispatcherJob>();
        services.AddScoped<PendingPaymentExpiryJob>();
        // Central file-registry garbage collector (reaps Pending/Detached FileObjects — blob + row).
        services.AddScoped<FileObjectGcJob>();
        // Daily student recycle-bin purge (REQ-STU-027/028 10-day retention).
        services.AddScoped<RecycleBinPurgeJob>();
        // Exams & Homework Module — report export (stub; replace with ClosedXML/QuestPDF)
        //  Hangfire dispatcher + worker (Phase 6) ──
        // Daily fan-out + per-template materialization workers.
        services.AddScoped<RecurringAssignmentDispatcherJob>();
        services.AddScoped<IRecurringAssignmentMaterializerJob,
                           RecurringAssignmentMaterializerJob>();
        // Nightly auto-absent sweep — dispatcher fans out one per-teacher worker.
        services.AddScoped<AutoAbsentDispatcherJob>();
        services.AddScoped<IAutoAbsentJob, AutoAbsentJob>();
        services.AddScoped<IExamHomeworkReportService, ExamHomeworkReportService>();
        // Generic PDF export engine (QuestPDF) — reused across modules. First consumer: audit (REQ-USR-030).
        services.AddScoped<IPdfExportService, PdfExportService>();

        // Student barcode rendering (REQ-STU-047/052): Code 128 SVG (ZXing) + card-grid PDF (QuestPDF).
        // Stateless renderers — the orchestrating IStudentBarcodeService lives in the Application layer.
        services.AddSingleton<IBarcodeRenderer, BarcodeRenderer>();
        // QR renderer (ZXing QR_CODE SVG) for the student in-app code — hard-copy stays Code 128 above.
        services.AddSingleton<IQrCodeRenderer, QrCodeRenderer>();
        services.AddScoped<IStudentBarcodePdfBuilder, StudentBarcodePdfBuilder>();

        // Video attachments (Track F, §5) — Azure Blob Storage. No AddHttpClient
        // needed; the Azure SDK manages its own client internally.
        services.AddScoped<IFileStorageService, AzureBlobFileStorageService>();

        // Public parent portal — the teacher notification fan-out. It lives in Infrastructure
        // (§6.2): notification transport must never become an Application-layer concern, even
        // though this one is invoked inline post-commit rather than through Hangfire.
        // ParentPortalOptions itself is bound in Program.cs alongside the other option sections.
        services.AddScoped<IParentPortalNotifier, ParentPortalNotifier>();
    }
}