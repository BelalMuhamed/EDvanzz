using Azure.Monitor.OpenTelemetry.AspNetCore;
using Edvanz.API.Authorization;
using Edvanz.API.Controllers;
using Edvanz.API.Filters;
using Edvanz.API.Middleware;
using Edvanz.Application.Extensions;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.Options;
using Edvanz.Application.Security;
using Edvanz.Application.ServiceContract;
using Edvanz.Application.Services;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure;
using Edvanz.Infrastructure.BackGroundJobs;
using Edvanz.Infrastructure.Extensions;
using Edvanz.Infrastructure.Persistence;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Globalization;
using System.IO.Compression;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Edvanz.Application.Json;
using HangfireDashboardAuthFilter = Edvanz.API.Filters.HangfireDashboardAuthFilter;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());

        // Every DateTime this API returns is a UTC instant, but EF materializes SQL Server
        // datetime2 as Kind=Unspecified, which serializes with NO suffix — while the same
        // logical field computed in memory (DateTime.UtcNow echoed back in a create response)
        // is Kind=Utc and DOES carry a Z. Clients therefore saw one field in two shapes, and a
        // suffix-less instant reads as LOCAL in Dart/JS, landing 2-3h off for Egypt. These make
        // the payload state its zone. Reading is unchanged, so nothing about what callers may
        // send, or what gets persisted, moves. Fields that are deliberately a LOCAL wall-clock
        // opt out per-property with LocalWallClockDateTimeJsonConverter; a calendar day belongs
        // in a DateOnly, which these never see. See TIMEZONE_STANDARD.md.
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new NullableUtcDateTimeJsonConverter());
    });
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle

builder.Services.AddEndpointsApiExplorer();
var x = builder.Configuration.GetConnectionString("con");

// ── Database context ─────────────────────────────────────────────────────────────────────
// PERF Tier-1 (2026-08-31, docs/perf-tier1-2026-08-31.md): DbContext POOLING reuses context
// instances across requests, removing per-request context allocation + internal-service-provider
// setup — a real saving on the single-core B1. The context is safe to pool: options-only primary
// ctor, no per-request injected state, no SaveChanges override, and a static OnConfiguring.
// KILL SWITCH (reversible WITHOUT a redeploy): set App Service setting
//   Performance__DbContextPoolingEnabled = false  then Restart.
// It is read BEFORE the registration, so it also recovers a boot that ever failed under pooling —
// flip it, restart, and the app falls back to the classic per-request AddDbContext.
bool enableDbContextPooling =
    builder.Configuration.GetValue("Performance:DbContextPoolingEnabled", true);

// NOTE: this configures the options for BOTH the pooled and the non-pooled path. It also holds
// the settings that USED to live in EdvanzDbContext.OnConfiguring — pooling forbids OnConfiguring
// from modifying options ("'OnConfiguring' cannot be used to modify DbContextOptions when DbContext
// pooling is enabled"), so they were consolidated here. CommandTimeout stays 300 to preserve the
// previous EFFECTIVE behaviour (the old OnConfiguring's 300 overrode the DI block's 30).
void ConfigureDbContext(DbContextOptionsBuilder options) =>
    options
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .UseSqlServer(
            builder.Configuration.GetConnectionString("con"),
            sqlOpts => sqlOpts
                //.EnableRetryOnFailure(          // Tier-2, deliberately still OFF — enabling it requires
                //    maxRetryCount: 5,           // wrapping the UnitOfWork transaction boundary in a
                //    maxRetryDelay: TimeSpan.FromSeconds(30),  // DB execution strategy, or explicit
                //    errorNumbersToAdd: null)    // transactions throw. See the perf doc, Tier-2.
                .CommandTimeout(300));

if (enableDbContextPooling)
    builder.Services.AddDbContextPool<EdvanzDbContext>(ConfigureDbContext);
else
    builder.Services.AddDbContext<EdvanzDbContext>(ConfigureDbContext);

// ── Response compression ─────────────────────────────────────────────────────────────────
// PERF Tier-1 (2026-08-31, docs/perf-tier1-2026-08-31.md): gzip/brotli-compress JSON responses.
// Mobile payloads shrink ~70-85% → faster screen loads on cellular. The Flutter client already
// sends "Accept-Encoding: gzip" and auto-decompresses, so this is transparent — NO app change.
// Providers run at CompressionLevel.Fastest to stay cheap on the 1-core B1 (Brotli "Optimal"
// would peg the CPU). EnableForHttps is safe here: this is a Bearer-token JSON API with no
// cookie/CSRF secrets reflected into responses, so the BREACH concern that disables HTTPS
// compression by default does not apply.
// KILL SWITCH (reversible WITHOUT a redeploy): set App Service setting
//   Performance__ResponseCompressionEnabled = false  then Restart — the UseResponseCompression
// middleware below is only added when this flag is true.
bool enableResponseCompression =
    builder.Configuration.GetValue("Performance:ResponseCompressionEnabled", true);
if (enableResponseCompression)
{
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        // application/json is in the defaults, but the middleware matches the FULL content-type,
        // so pin the "; charset=utf-8" variant System.Text.Json emits or JSON won't be compressed.
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes
            .Concat(new[] { "application/json; charset=utf-8" });
    });
    builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
    builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
}
builder.Services.AddScoped(typeof(IUnitOfWork), typeof(UnitOfWork));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddLocalization();

builder.Services.Configure<RequestLocalizationOptions>(options =>
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Edvanz", policy =>
    {
        // Origins are configured per-environment in appsettings.Production.json
        // (Cors:Origins array). Flutter mobile clients are not browser-bound
        // so they don't need CORS — this list is for any web admin surface only.
        var origins = builder.Configuration
            .GetSection("Cors:Origins")
            .Get<string[]>() ?? Array.Empty<string>();

        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});


builder.Services.AddSwaggerGen(c =>
{
    // JWT Bearer Authentication
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Paste ONLY your JWT access token below — the 'Bearer ' prefix is added automatically.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    // Apply the Bearer scheme to every operation so the token entered in the
    // Authorize dialog is actually attached to requests. Without this, the
    // Authorize button stores the token but never sends it → every call 401s.
    // Microsoft.OpenApi v2 syntax: reference the scheme by id via OpenApiSecuritySchemeReference.
    c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("Bearer", doc), new List<string>() }
    });
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Edvanz", Version = "v1" });
    c.OperationFilter<AcceptLanguageHeaderFilter>();
    // Fully automatic examples — no per-endpoint/per-module providers.
    //   • AutoExampleSchemaFilter  → request-body example for every DTO (from its type)
    //     + "Allowed values: …" appended to every enum field. Registered AFTER
    //     IncludeXmlComments (below) so it appends to, rather than is overwritten by,
    //     the XML-comment description.
    //   • ResponseEnvelopeExampleFilter → { success, message, data } example for every response.
    // Covers all endpoints, old and new, with zero maintenance.
    c.OperationFilter<ResponseEnvelopeExampleFilter>();
    // Parameter-level examples so imported GET requests get filled query strings
    // (Postman ignores schema-level examples on parameters) — see ParameterExampleFilter.
    c.OperationFilter<ParameterExampleFilter>();
    c.UseInlineDefinitionsForEnums();
    // Collision-proof schemaIds: two DTOs sharing a class name across namespaces used
    // to abort the whole spec generation ("Can't use schemaId ... already used") — see
    // SwaggerSchemaIds. First type keeps the short name; duplicates get namespace-prefixed.
    c.CustomSchemaIds(SwaggerSchemaIds.For);

    // --- 👇 Add these lines ---
    var basePath = AppContext.BaseDirectory;

    // 1) XML from the API project (controllers)
    var apiXml = $"{typeof(AttendanceController).Assembly.GetName().Name}.xml";
    var apiPath = Path.Combine(basePath, apiXml);
    if (File.Exists(apiPath))
        c.IncludeXmlComments(apiPath);

    // 2) XML from the Application project (DTOs) – adjust assembly name if needed
    var appXml = "Edvanz.Application.xml";
    var appPath = Path.Combine(basePath, appXml);
    if (File.Exists(appPath))
        c.IncludeXmlComments(appPath);

    // Registered LAST so its enum "Allowed values: …" text appends to the XML-comment
    // description instead of being overwritten by it.
    c.SchemaFilter<AutoExampleSchemaFilter>();
});
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
// ── Hangfire — production-tuned for Azure SQL Basic (5 DTU) ──────────────
// QueuePollInterval at 15s: the default is already 15s in Hangfire 1.8;
// we pin it explicitly so a library upgrade never silently lowers it.
// DisableGlobalLocks: reduces lock contention on Azure SQL — recommended
// for cloud-hosted SQL where distributed locks incur extra round-trips.
// WorkerCount = 4: on a 1-vCPU B1 the default (ProcessorCount × 5 ≈ 5-10)
// competes with the API for the same core and the same 5 DTU.
builder.Services.AddHangfire(config =>
    config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(
            builder.Configuration.GetConnectionString("con"),
            new SqlServerStorageOptions
            {
                QueuePollInterval = TimeSpan.FromSeconds(15),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                JobExpirationCheckInterval = TimeSpan.FromHours(1),
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true,
                SchemaName = "HangFire"
            }));

builder.Services.AddHangfireServer(options =>
{
    options.Queues = new[]
    {
        "default",
        SubscriptionConstants.NotificationsQueue,
        "assignment-materialization",
        AttendanceConstants.AutoAbsentQueue
    };
    options.WorkerCount = 4;
    options.ServerName = $"edvanz-{Environment.MachineName}";
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"])),
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role,
        };
    });
builder.Services.AddAuthorization(
options =>
{
    // ── Subscription model (free-tier) ──────────────────────────────────────
    // The app is NO LONGER hard-gated on an active subscription. Unsubscribed teachers
    // may use every module for free; a subscription only lifts per-feature CREATE quotas
    // (students, sessions, assistants, groups) enforced in the service layer via
    // ISubscriptionGateService. Attendance and payments are free for everyone.
    //
    // The named "ActiveSubscription" policy + ActiveSubscriptionHandler are retained (dormant)
    // so hard gating can be re-enabled later by adding the requirement back to a policy.
    options.AddPolicy("ActiveSubscription", policy =>
        policy.Requirements.Add(new ActiveSubscriptionRequirement()));

    // Fallback only requires authentication — no subscription requirement.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
// ????????????????????????????????????????????????
// SUBSCRIPTION MANAGEMENT MODULE — Phase 07 (Authorization)
// ????????????????????????????????????????????????
//
// The ActiveSubscriptionHandler enforces the §8 policy: every authenticated
// request requires an Active or ExpiringSoon subscription unless the endpoint
// carries [AllowExpiredSubscription].
//
// Singleton is the right scope here — the handler is stateless; per-request
// dependencies (ICurrentUserService, IUnitOfWork) are injected fresh on each
// HandleRequirementAsync call via the request-scoped scope.
// (Note: ASP.NET Core resolves IAuthorizationHandler instances per request
// when registered as Scoped. Use Scoped to match IUnitOfWork's lifetime.)
// Free-tier per-module quotas — bound from config so limits change without a code edit
// (edit appsettings.json locally, or App Service settings "FreeTierQuotas__Students" in prod).
builder.Services.Configure<Edvanz.Application.Options.FreeTierQuotaOptions>(
    builder.Configuration.GetSection(Edvanz.Application.Options.FreeTierQuotaOptions.Section));
// Nightly auto-absent sweep — kill switch, effective-from (non-retroactive), lookback, cron all
// tunable via App Service settings "AutoAbsent__Enabled" / "AutoAbsent__EffectiveFrom" etc.
builder.Services.Configure<Edvanz.Application.Options.AutoAbsentOptions>(
    builder.Configuration.GetSection(Edvanz.Application.Options.AutoAbsentOptions.Section));

builder.Services.Configure<Edvanz.Application.Options.SupportOptions>(
    builder.Configuration.GetSection(Edvanz.Application.Options.SupportOptions.Section));
// Mobile-app version gate — force/optional-update thresholds tunable via App Service settings
// "AppVersion__Android__MinSupportedBuild" / "AppVersion__iOS__LatestBuild" etc. (no redeploy).
builder.Services.Configure<Edvanz.Application.Options.AppVersionOptions>(
    builder.Configuration.GetSection(Edvanz.Application.Options.AppVersionOptions.Section));
// Public parent portal (parent.edvanz.io). The shared secret is NEVER committed — appsettings.json
// carries an empty placeholder and the real value is the App Service setting
// "ParentPortal__PortalKey"; an empty key makes ParentPortalKeyFilter reject every request
// (fail-closed). "ParentPortal__Enabled" = false takes the whole public surface down without a
// redeploy.
builder.Services.Configure<Edvanz.Application.Options.ParentPortalOptions>(
    builder.Configuration.GetSection(Edvanz.Application.Options.ParentPortalOptions.Section));

// Device lock: kill switch + teacher-alert cooldown. Changing these on App Service
// (DeviceLock__Enabled / DeviceLock__BlockNotificationCooldownHours) takes effect on restart,
// with no redeploy — the recovery path for a wrongly-blocked student is otherwise thin.
builder.Services.Configure<Edvanz.Application.Options.DeviceLockOptions>(
    builder.Configuration.GetSection(Edvanz.Application.Options.DeviceLockOptions.Section));
builder.Services.AddScoped<IAuthorizationHandler, ActiveSubscriptionHandler>();
// Turns a subscription-only Forbidden into a clear localized "please subscribe" envelope
// (instead of the framework's bare, body-less 403) on every gated action.
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, SubscriptionAuthorizationResultHandler>();
// Tenant-isolation guard for controllers that carry teacherId in the route (Session, Teacher).
builder.Services.AddScoped<Edvanz.API.Filters.TenantScopeFilter>();
builder.Services.AddSingleton<IVideoUrlParser, VideoUrlParser>();
builder.Services.AddScoped<IVideoScopeResolver, VideoScopeResolver>();
builder.Services.AddScoped<IVideoService, VideoService>();
builder.Services.AddScoped<IVideoUnitService, VideoUnitService>();
builder.Services.AddScoped<IOnlineExamScopeResolver, OnlineExamScopeResolver>();
builder.Services.AddScoped<IOnlineExamService, OnlineExamService>();
builder.Services.AddScoped<IStudentOnlineExamService, StudentOnlineExamService>();
builder.Services.AddScoped<IStudentVideoExamService, StudentVideoExamService>();
builder.Services.AddScoped<IOnlineExamGradingService, OnlineExamGradingService>();
builder.Services.AddHttpContextAccessor();
builder.Configuration.AddEnvironmentVariables();

// Production secrets (currently: AzureBlobStorage:ConnectionString) come from Azure Key
// Vault via Managed Identity — same auth model already used for prod SQL access. Locally
// (Development), config keeps coming from appsettings.Development.json / user-secrets;
// Key Vault is never contacted. Vault URI is non-secret, so it's just an app setting
// ("KeyVault__Uri" in App Service). Key Vault secret names use "--" in place of ":"
// (e.g. "AzureBlobStorage--ConnectionString" binds to AzureBlobStorage:ConnectionString).
// A vault outage/misconfig must not crash-loop the container (the 18456 lesson): nothing
// boot-critical lives in the vault — SQL/JWT come from App Service settings — so a load
// failure degrades to "blob features unavailable" and is logged CRITICAL after Build.
Exception? keyVaultLoadError = null;
if (!builder.Environment.IsDevelopment())
{
    var keyVaultUri = builder.Configuration["KeyVault:Uri"];
    if (!string.IsNullOrWhiteSpace(keyVaultUri))
    {
        try
        {
            builder.Configuration.AddAzureKeyVault(
                new Uri(keyVaultUri),
                new Azure.Identity.DefaultAzureCredential());
        }
        catch (Exception ex)
        {
            keyVaultLoadError = ex;
        }
    }
}
builder.Services.AddHttpClient<IWhatsAppSender, WhatsAppSender>();
// ── Application Insights / Azure Monitor (production observability) ──────
// Azure Monitor OpenTelemetry Distro — one call auto-instruments ASP.NET Core
// requests, HttpClient calls, and SqlClient calls, and (critically for this
// codebase) registers an ILogger provider so every _logger.LogError/LogWarning
// call already present throughout FirebasePushNotificationSender, the
// notification jobs, and Hangfire job bodies ships to Application Insights —
// not just to whatever log sink happens to be capturing App Service's stdout.
// Guarded the same way as Key Vault/Firebase above: an optional dependency,
// never allowed to block boot if the connection string isn't set (e.g. local
// Development never has one and shouldn't need one).
string? appInsightsConnectionString =
    builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? builder.Configuration["ApplicationInsights:ConnectionString"];

if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    // Cost control (2026-09-05): trace spans (AppRequests + AppDependencies) were
    // ~71% of all ingested telemetry, dominated by one SqlClient span per EF query.
    // The Log Analytics workspace has a 5 GB/month free grant and unsampled
    // ingestion was trending to ~7.5 GB/month, which bills per-GB overage; the
    // workspace also now carries a 0.16 GB/day hard cap that blackholes ingestion
    // for the rest of the UTC day once tripped. Sampling spans keeps ingestion
    // inside the grant (~2.6 GB/month) and comfortably under that cap.
    // ILogger output (AppTraces) and exceptions are log records, NOT spans, so
    // they are unaffected by SamplingRatio and still arrive in full — error
    // diagnostics keep working at 100%.
    // Override with the "ApplicationInsights:SamplingRatio" App Service app
    // setting (e.g. 1.0 to capture everything while debugging); it applies on
    // restart, no redeploy needed.
    float samplingRatio = builder.Configuration.GetValue<float?>("ApplicationInsights:SamplingRatio") ?? 0.15f;
    samplingRatio = Math.Clamp(samplingRatio, 0.01f, 1.0f);

    builder.Services.AddOpenTelemetry().UseAzureMonitor(options =>
    {
        options.ConnectionString = appInsightsConnectionString;
        options.SamplingRatio = samplingRatio;
    });
}
// ── Rate limiting (built-in, no Redis needed for single instance) ─────────
// "auth" policy: 10 login/register attempts per IP per minute.
// Applied via [EnableRateLimiting("auth")] on AuthController.
builder.Services.AddRateLimiter(o =>
{
    // General auth surface: login, sign-up, OTP, Google sign-up, change-password.
    o.AddFixedWindowLimiter("auth", opts =>
    {
        opts.Window = TimeSpan.FromMinutes(1);
        opts.PermitLimit = 10;
        opts.QueueLimit = 0;
        opts.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
    });

    // Admin login: stricter — admin credentials are the highest-value target.
    o.AddFixedWindowLimiter("admin-auth", opts =>
    {
        opts.Window = TimeSpan.FromMinutes(1);
        opts.PermitLimit = 5;
        opts.QueueLimit = 0;
        opts.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
    });

    // Chat message send: PARTITIONED per caller (JWT NameIdentifier), unlike the
    // two global policies above. One user flooding POST …/messages must only
    // throttle that user — not every other participant on the platform.
    // Falls back to remote IP for the (should-never-happen, since the endpoint
    // requires [Authorize]) case where the partitioner runs against an
    // unauthenticated request.
    o.AddPolicy("chat-send", httpContext =>
    {
        string partitionKey = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? "anonymous"
            : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(30),
                PermitLimit = 20,
                QueueLimit = 0,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
            });
    });

    // FCM device-token registration: partitioned per caller, mirrors "chat-send".
    // A device registers rarely — 5/60s covers legitimate use while blocking a
    // malicious authenticated user from spraying arbitrary tokens onto their own
    // account (push-review 🟡 finding).
    o.AddPolicy("fcm-register", httpContext =>
    {
        string partitionKey = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? "anonymous"
            : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(60),
                PermitLimit = 5,
                QueueLimit = 0,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
            });
    });

    // Teacher profile writes (PUT /api/teacher/profile): partitioned per caller, mirrors
    // "fcm-register". The teacher's display name is what students, parents and receipts show, so
    // a rename is socially visible; 5/10min is far above the legitimate ceiling (this endpoint is
    // also fired by the UI language toggle) while stopping a rename loop.
    o.AddPolicy(Edvanz.Domain.Constants.TeacherConstants.ProfileUpdateRateLimitPolicy, httpContext =>
    {
        string partitionKey = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? "anonymous"
            : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(10),
                PermitLimit = 5,
                QueueLimit = 0,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
            });
    });

    // Public parent portal: the ONLY anonymous, internet-facing write surface, so it is
    // partitioned per BROWSER (the X-Portal-Device header the PHP page mints) rather than per
    // caller identity — there is no JWT here. Falls back to the forwarded client IP, then to the
    // socket peer (which is the portal itself, i.e. a shared bucket — acceptable as a last
    // resort because the service-layer caps in ParentPortalOptions still apply per device and
    // per teacher).
    o.AddPolicy(ParentPortalConstants.RateLimitPolicy, httpContext =>
    {
        string partitionKey = httpContext.Request.Headers[ParentPortalConstants.DeviceHeader].ToString();
        if (string.IsNullOrWhiteSpace(partitionKey))
            partitionKey = httpContext.Request.Headers[ParentPortalConstants.ClientIpHeader].ToString();
        if (string.IsNullOrWhiteSpace(partitionKey))
            partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(60),
                PermitLimit = 60,
                QueueLimit = 0,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
            });
    });

    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── Health checks ─────────────────────────────────────────────────────────
// /health/live  — always 200 if the process is up (no dependencies checked)
// /health/ready — checks SQL Server connectivity; tagged "ready"
builder.Services.AddHealthChecks()
    .AddAsyncCheck("sqldb", async ct =>
    {
        try
        {
            var cs = builder.Configuration.GetConnectionString("con")!;
            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server unreachable", ex);
        }
    }, tags: new[] { "ready", "db" })
    .AddCheck("hangfire", () =>
        JobStorage.Current?.GetMonitoringApi().Servers().Count > 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Hangfire server not running"),
        tags: new[] { "ready" });

const string SpaCors = "SpaCors";

builder.Services.AddCors(options =>
{
options.AddPolicy(SpaCors, policy =>
    policy.WithOrigins(
              "http://localhost:4200",              // Angular dev
              "https://app-edvanz-admin.azurewebsites.net",
              "https://muhammadkhha-png.github.io", // GitHub Pages admin (EdvanzAdminUI)
              "https://parent.edvanz.io",           // public parent portal (PHP page on Hostinger)
              "https://belalmuhamed.github.io") // prod admin origin
          .AllowAnyHeader()
          .AllowAnyMethod()
          .AllowCredentials());                     // omit if you don't use cookies
});

// Idle-session sliding: per-user throttle marker cache (in-process, short TTL) used by
// SessionActivitySlidingMiddleware to keep the refresh-token deadline write off the hot path.
builder.Services.AddMemoryCache();

var app = builder.Build();


// Refuse to run against the design-time placeholder connection string — FIRST thing after
// Build, before seeding, Hangfire storage init, or anything else can dial it. The App
// Service first-boot quirk can start a container BEFORE app settings are injected;
// configuration then falls back to the committed appsettings.json placeholder. Nothing can
// work in that state, so die immediately with a clear message — Azure replaces the
// container and the replacement gets the real settings. (EF design-time tooling never
// reaches this code: it stops at builder.Build().)
{
    var runtimeCon = app.Configuration.GetConnectionString("con");
    if (string.IsNullOrWhiteSpace(runtimeCon) || runtimeCon.Contains("EDVANZ_DESIGN_TIME_ONLY"))
    {
        app.Logger.LogCritical(
            "ConnectionStrings__con is missing or is the design-time placeholder — this container " +
            "booted without App Service settings (first-boot sync quirk). Exiting so the platform " +
            "starts a replacement container with real settings.");
        throw new InvalidOperationException(
            "No runtime database connection string configured (ConnectionStrings__con).");
    }
}
if (keyVaultLoadError is not null)
{
    app.Logger.LogCritical(keyVaultLoadError,
        "Azure Key Vault configuration failed to load — booting WITHOUT vault-sourced secrets " +
        "(AzureBlobStorage:ConnectionString). Blob-dependent features will fail until the vault " +
        "is reachable (check the app's 'Key Vault Secrets User' role and KeyVault__Uri), then restart.");
}

// ── Firebase credential visibility check (log-only — NEVER blocks boot, D5) ──
// The adapter (FirebasePushNotificationSender) now fails soft per-call when
// credentials are missing/broken, so this can't crash anything — it just surfaces
// the misconfiguration once, loudly, at boot instead of silently inside push logs.
{
    var firebaseOpts = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<Edvanz.Application.Options.FirebaseOptions>>().Value;
    if (string.IsNullOrWhiteSpace(firebaseOpts.CredentialsPath))
    {
        app.Logger.LogWarning(
            "Firebase:CredentialsPath is not configured — push notifications will no-op until it " +
            "is set. Get a service-account JSON from Firebase Console → Project Settings → Service " +
            "accounts → Generate new private key, then set Firebase__CredentialsPath (a file path " +
            "locally, or the inline JSON via Key Vault in Azure) and Firebase__ProjectId.");
    }
}

// Demo/baseline seeding runs only outside Production (prod already holds its real
// reference data), and is wrapped so a seed failure can NEVER abort app startup.
// Regression guard: the 6868b41 seeder threw a duplicate-key (soft-deleted student
// code collides with the unfiltered unique index) which crashed boot -> 503.
if (!app.Environment.IsProduction())
{
    try
    {
        await app.SeedDatabaseAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database seeding failed; continuing startup.");
    }
}

// Hangfire's static RecurringJob.* API (used below to register recurring jobs) reads
// JobStorage.Current, which AddHangfire only initializes lazily when a Hangfire service
// is first resolved from DI. Previously the startup seeder happened to trigger that
// resolution; now that seeding is skipped in Production, resolve JobStorage explicitly
// so recurring-job registration works independently of the seeder.
_ = app.Services.GetRequiredService<JobStorage>();


// PERF Tier-1 (2026-08-31): compress responses. Placed first so it wraps the whole pipeline.
// Only added when Performance:ResponseCompressionEnabled (default true) — see the service
// registration above for the rationale and the kill switch.
if (enableResponseCompression)
    app.UseResponseCompression();

// Use localization middleware
var locOptions = app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>();
app.UseRequestLocalization(locOptions.Value);
app.UseMiddleware<ExceptionMiddleware>();
if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // Production: only serve the raw JSON, or add authentication
    app.UseSwagger();   // still serves /swagger/v1/swagger.json
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Edvanz v1");
        // Protect the UI with a custom middleware if needed
    });
}
// ?? Subscription Module — Phase 08 recurring registrations ??

// Boot-time DB config diagnostics. ConnectionStrings:con can be defined TWICE — as an App
// Service *connection string* ('con', injected as SQLAZURECONNSTR_con) AND as an app setting
// ('ConnectionStrings__con'). Both collapse onto the same config key with a NON-deterministic
// winner; a stale duplicate holding an old password shadowed the good one and caused the
// 2026-07-15 SQL 18456 crash-loop outage. Surface the collision and the effective target
// loudly so the next occurrence is a 5-second log read, not a multi-hour hunt. No password is
// logged. Fix = keep ONLY the deploy-managed app setting; delete the connection-string entry.
{
    bool hasConnStringEntry = Environment.GetEnvironmentVariable("SQLAZURECONNSTR_con") is not null
                           || Environment.GetEnvironmentVariable("SQLCONNSTR_con") is not null;
    bool hasAppSetting = Environment.GetEnvironmentVariable("ConnectionStrings__con") is not null;
    if (hasConnStringEntry && hasAppSetting)
        app.Logger.LogWarning(
            "ConnectionStrings:con is defined BOTH as an App Service connection string ('con') AND as an app " +
            "setting ('ConnectionStrings__con'). They collide on one config key with a non-deterministic winner — " +
            "remove one (keep the deploy-managed app setting). This caused the 2026-07-15 SQL 18456 outage.");
    try
    {
        var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(app.Configuration.GetConnectionString("con"));
        app.Logger.LogInformation("Effective SQL target at boot: server={Server}; database={Database}; user={User}.",
            csb.DataSource, csb.InitialCatalog, csb.UserID);
    }
    catch { /* diagnostics only — never block boot */ }
}

// These registrations are the boot's FIRST SQL round-trip (Hangfire storage connects lazily
// here). Retry transient faults with backoff so a container-swap blip cannot kill the boot.
// On a PERSISTENT failure (SQL 18456 = login failed, or retries exhausted) we no longer abort
// the process: a crash here produced an endless container crash-loop and an opaque Azure "503
// Application Error" for EVERY request — even anonymous /api/auth/login — that never self-heals
// when the cause is a standing misconfiguration (the 2026-07-15 outage). Instead, log CRITICAL
// and boot in a degraded mode (jobs unscheduled) so the app stays reachable and diagnosable via
// its own error envelopes and /health/ready. NOTE: with this change the deploy's /health/live
// poll goes green even on a bad DB credential — the CI pipeline gates DB health on /health/db.
for (int attempt = 1; ; attempt++)
{
    try
    {
        // Daily reminder dispatcher (§7.1). 09:00 in Africa/Cairo by default.
        var reminderOpts = app.Services
            .GetRequiredService<IOptions<ReminderSchedulerOptions>>().Value;

        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(reminderOpts.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
        }

        RecurringJob.AddOrUpdate<SubscriptionReminderDispatcherJob>(
            SubscriptionConstants.ReminderDispatcherJobId,
            job => job.RunAsync(),
            reminderOpts.CronExpression,
            new RecurringJobOptions { TimeZone = timeZone });

        // Hourly pending-payment expiry sweep (EC-18). Runs at minute 0 of every hour.
        RecurringJob.AddOrUpdate<PendingPaymentExpiryJob>(
            SubscriptionConstants.PendingPaymentExpiryJobId,
            job => job.RunAsync(),
            Cron.Hourly);

        // Central file-registry GC — reaps abandoned (Pending) and released (Detached)
        // FileObjects (blob + row). Hourly; idempotent, so a transient Azure failure just
        // retries next sweep. This is the single reliable reaper of blob storage.
        RecurringJob.AddOrUpdate<FileObjectGcJob>(
            "file-object-gc",
            job => job.RunAsync(),
            Cron.Hourly);

        // ── Student recycle-bin purge (REQ-STU-027/028) ──
        // Permanently deletes roster records soft-deleted more than 10 days ago. The service
        // method existed but had NO caller and NO registration, so the retention window the
        // recycle-bin UI advertises never actually expired anything. Daily at 03:00
        // Africa/Cairo — off-peak, and before the 06:00 assignment materializer so a purged
        // student is gone before the day's obligations are generated. Idempotent (§6.4): each
        // student is purged in its own transaction, so a partial run just resumes next day.
        RecurringJob.AddOrUpdate<RecycleBinPurgeJob>(
            recurringJobId: "recycle-bin-purge",
            methodCall: job => job.RunAsync(),
            cronExpression: "0 3 * * *",
            options: new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo"),
            });

        // assistant-cleanup runs every 10 minutes so a teacher-deleted (soft-deleted)
        // assistant is purged promptly — freeing its username/phone for reuse shortly
        // after deletion. Each purge is a small, isolated transaction (see the job), so
        // the frequent cadence is cheap. (Was daily 01:00, which left deleted rows —
        // hidden from the list but still holding their unique username — for up to a day.)
        RecurringJob.AddOrUpdate<AssistantCleanupJob>(
            "assistant-cleanup-job",
            job => job.ExecuteAsync(),
            "*/10 * * * *",
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo")
            });

        // ?? Recurring Assignment Materializer (Module 6) ??
        // Runs once daily at 06:00 Africa/Cairo. Earlier than the reminder dispatcher
        // (09:00) so tomorrow's occurrences are visible by morning.
        RecurringJob.AddOrUpdate<RecurringAssignmentDispatcherJob>(
            recurringJobId: "recurring-assignment-materializer",
            methodCall: job => job.RunAsync(),
            cronExpression: "0 6 * * *",
            options: new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo"),
            }
          );

        // ── Nightly auto-absent sweep ──
        // Marks unmarked/held roster students Absent once a class occurrence's whole equivalence-slot
        // window has passed. Default 02:30 Africa/Cairo — after the day has closed, off-peak. The
        // dispatcher enqueues per-teacher workers; the service is the kill switch (AutoAbsent__Enabled).
        var autoAbsentOpts = app.Services
            .GetRequiredService<IOptions<Edvanz.Application.Options.AutoAbsentOptions>>().Value;
        RecurringJob.AddOrUpdate<AutoAbsentDispatcherJob>(
            recurringJobId: AttendanceConstants.AutoAbsentJobId,
            methodCall: job => job.RunAsync(),
            cronExpression: autoAbsentOpts.CronExpression,
            options: new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo"),
            }
          );

        break;
    }
    catch (Exception ex) when (attempt < 4 && !ContainsSqlLoginFailure(ex))
    {
        app.Logger.LogWarning(ex,
            "Hangfire recurring-job registration attempt {Attempt}/4 failed; retrying in {DelaySeconds}s",
            attempt, 5 * attempt);
        Thread.Sleep(TimeSpan.FromSeconds(5 * attempt));
    }
    catch (Exception ex)
    {
        // Persistent failure: a SQL login error (18456) is routed here immediately (retrying a
        // bad credential is pointless), and transient faults land here once retries are spent.
        // Do NOT rethrow — booting degraded beats an endless crash-loop + opaque platform 503.
        // Background jobs stay unscheduled until the next healthy boot; fix ConnectionStrings:con
        // (see the collision warning above) and restart.
        app.Logger.LogCritical(ex,
            "Hangfire recurring-job registration FAILED after {Attempt} attempt(s) — BOOTING IN DEGRADED MODE " +
            "(background jobs NOT scheduled). SQL 18456 = login failed → check ConnectionStrings:con for a " +
            "wrong or duplicated password, then see /health/ready.", attempt);
        break;
    }
}

// SQL error 18456 (login failed) is a credentials/config problem, never a transient blip, so
// it skips the retry/backoff and goes straight to the degraded-boot path above — retrying a
// bad password only burns the container-start budget. Typical causes: a wrong password in
// ConnectionStrings:con, or two colliding definitions of it (see the boot warning above).
static bool ContainsSqlLoginFailure(Exception? ex)
{
    for (; ex is not null; ex = ex.InnerException)
        if (ex is Microsoft.Data.SqlClient.SqlException { Number: 18456 }) return true;
    return false;
}

app.UseHttpsRedirection();

// HSTS — tells browsers to only use HTTPS for the next year.
// Excluded in Development so localhost still works over HTTP.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Security headers — defence-in-depth for any browser-based surface.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("X-Frame-Options", "DENY");
    ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    ctx.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    await next();
});

app.UseCors(SpaCors);



app.UseAuthentication();

// Rate limiting — applied before auth so unauthenticated callers are
// throttled on the login/register surface. Policy "auth" is registered
// in AddRateLimiter above and applied via [EnableRateLimiting("auth")]
// on AuthController endpoints.
app.UseRateLimiter();

// Live permission / module-revocation enforcement (REQ-USR-013 / REQ-USR-027 /
// REQ-USR-008 / BR-ADM-010). Runs after UseAuthentication so HttpContext.User
// is populated, and before UseAuthorization so PermissionHandler and
// ActiveSubscriptionHandler see the resolved snapshot on HttpContext.Items.
app.UseMiddleware<SecurityStampValidationMiddleware>();

// Idle-session enforcement: slide the caller's refresh-token idle deadline forward on every
// authenticated request (throttled). Runs AFTER the security-stamp check so an invalidated
// token never extends a session, and BEFORE UseAuthorization (any authenticated call counts
// as activity, even one that is ultimately 403'd). See SessionActivitySlidingMiddleware.
app.UseMiddleware<SessionActivitySlidingMiddleware>();
app.UseAuthorization();

// ── Hangfire dashboard — MUST be after UseAuthentication + UseAuthorization ──
// Placing it here ensures HttpContext.User is fully populated by the time
// HangfireDashboardAuthFilter.Authorize() runs. A SuperAdmin-role JWT is
// required; an IP restriction on /hangfire in App Service Access Restrictions
// adds a belt-and-braces defence at the network layer.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireDashboardAuthFilter() },
    IsReadOnlyFunc = _ => false
});

app.MapControllers();
// AllowAnonymous: liveness must be reachable by unauthenticated probes (Azure
// health pings, the CI deploy poll). Without it the global FallbackPolicy made
// /health/live return 401, so nothing could actually use it. It exposes only
// "Healthy"/"Unhealthy" text — no data. /health/ready stays behind auth because
// its response writer includes dependency exception messages.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false   // process-up only — no dependency checks
}).AllowAnonymous();

// /health/db — anonymous, DB-aware gate for the CI deploy verification. Runs ONLY the SQL
// check (tag "db") and uses the DEFAULT response writer, so the body is a terse
// "Healthy"/"Unhealthy" with a 200/503 status — no exception details leaked (unlike the authed
// /health/ready). Since a persistent SQL-login failure now boots the app DEGRADED (see the
// Hangfire registration block), /health/live alone cannot catch a bad-DB deploy; the pipeline
// polls THIS to fail a release that can't reach SQL. Hangfire is intentionally excluded so a
// background-worker blip doesn't fail deploys.
app.MapHealthChecks("/health/db", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("db")   // SQL connectivity only
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                error = e.Value.Exception?.Message
            })
        });
        await ctx.Response.WriteAsync(result);
    }
});

app.Run();

