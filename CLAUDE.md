# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# CLAUDE.md — Edvanz Project Brain

> Drop this file in the **repo root** (`/`). Claude Code reads it automatically on every
> invocation. It is the authoritative source of architectural decisions, enforced patterns,
> known bugs, and active work items. Keep it updated as decisions evolve.

---

## 0. Commands

**Build / run (from repo root, solution file is `Edvanz.slnx`):**

```bash
dotnet restore Edvanz.slnx
dotnet build Edvanz.slnx
dotnet run --project Edvanz.API      # Swagger UI at /swagger (Development/Staging)
```

**EF Core migrations** (tool pinned in `Edvanz.API/dotnet-tools.json`, run once per clone):

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project Edvanz.Infrastructure --startup-project Edvanz.API
dotnet ef database update --project Edvanz.Infrastructure --startup-project Edvanz.API
```

`migrate.sql` at repo root is a generated migration script (EF `script` output), not
hand-written seed SQL — don't edit it directly. `.github/workflows/deploy.yml` is the
authoritative CI/CD sequence (restore → build → idempotent EF script applied to Azure SQL →
self-contained publish → `az webapp deploy --type zip` via OIDC) if you need to see how
changes reach production.

**Deploy behavior (2026-07-16, rev 3):** push to `master_integration` → CI now runs two
migration gates before touching Azure: (1) `dotnet ef migrations has-pending-model-changes`
rejects commits whose model isn't covered by a migration; (2) the generated `migrate.sql`
is rehearsed twice (fresh apply + idempotent re-run) against a throwaway SQL Server 2022
container with `sqlcmd -b`. The prod `azure/sql-action` step also runs with `-b`, so any
SQL error aborts the job BEFORE `az webapp deploy` — code and schema can no longer diverge
(closes the BUG-10 delivery hole). Gates add ~1.5–2 min. Rev 2 behavior (unchanged below):
push → ~2–2.5 min CI →
**async** zip deploy + explicit verification: the workflow polls the deployment record to
`deployed+active` (cap 2 min) and then `/health/live` (anonymous, cap 5 min). Healthy path
≈ 3.5–4.5 min total. An App Service quirk can start the first replacement container with a
**stale app-settings snapshot** (e.g., missing/pre-rotation `ConnectionStrings__con` → SQL
18456); the app fails fast by design and Azure auto-replaces the container over ~8–10 min.
When that happens the run ends GREEN with a `::warning::` ("site not healthy within 5 min")
instead of blocking 10 minutes — the swap still completes on Azure's side. Since rev 2 a
RED run means a REAL failure (record status 3, or record never active). The old synchronous
step used to sit blind for 10 min and report false "site failed to start" failures. `appsettings.json` holds a design-time placeholder connection string only;
runtime configuration comes from App Service settings (`ConnectionStrings__con`), and
Program.cs refuses to boot on the placeholder. `WEBSITE_RUN_FROM_PACKAGE` is inert on this
Linux plan and the workflow removes it if it reappears.

**Tests:** there is no automated test project in this solution. Validate changes with
`dotnet build` plus manual exercise via Swagger UI or the root `EDvanz.postman_collection.json`
(Postman collection) — that collection is the de facto test suite until one is added.

**WhatsApp microservice** (`whatsapp-service/`): a standalone Node/Express process
(`whatsapp-web.js` + Puppeteer) the API calls over HTTP via `IWhatsAppSender`
(`AddHttpClient<IWhatsAppSender, WhatsAppSender>()` in `Program.cs`). It is **not** part of
the .NET solution/build — run it separately:

```bash
cd whatsapp-service
npm install
node index.js   # first run prints a QR code to link WhatsApp Web
```

---

## 1. Solution Structure

```
Edvanz.sln
├── Edvanz.Domain/          # Entities, Interfaces, Enums, Constants, Value Objects
├── Edvanz.Application/     # Services, DTOs, Service-contract interfaces, Result pattern
├── Edvanz.Infrastructure/  # EF Core DbContext, Repos, Background Jobs, External integrations
└── Edvanz.API/             # Controllers, Middleware, Program.cs, appsettings.json
```

**Dependency rule (strictly enforced):**
`Domain ← Application ← Infrastructure ← API`
Never reference Infrastructure from Domain or Application. Never reference API from any
other project.

---

## 2. Technology Stack

| Concern | Technology |
|---|---|
| Runtime | .NET 10 / ASP.NET Core |
| ORM | EF Core 10 (Code-First, SQL Server) |
| Database | SQL Server (MSSQL 2025 on MonsterASP.NET) |
| Auth | JWT Bearer + custom SecurityStamp validation middleware |
| Cache | Redis via Upstash (production) / Memurai port 6379 (local dev) |
| Background jobs | Hangfire with SQL Server storage |
| PDF export | QuestPDF (Community license, Noto Sans Arabic for RTL) |
| Localization | `IStringLocalizer`, `Messages_en.resx` + `Messages_ar.resx` (Egyptian Arabic) |
| Hosting | MonsterASP.NET free tier (IIS/Windows Server, Let's Encrypt HTTPS) |

### 2.1 Program.cs Wiring Points

Cross-cutting registrations live in `Edvanz.API/Program.cs`, not auto-discovered — check here
before assuming something is missing:

- Recurring Hangfire jobs are registered inline via `RecurringJob.AddOrUpdate<T>(...)`:
  subscription reminder dispatcher (09:00 Africa/Cairo), pending-payment expiry sweep
  (hourly), assistant cleanup (01:00 Africa/Cairo), recurring-assignment materializer
  (06:00 Africa/Cairo), file-registry GC `file-object-gc` (hourly — see §5.5). A new
  recurring job needs its own registration here.
- Swagger example providers (see `IEndpointExampleProvider`, §3.7-adjacent Swagger tooling)
  are added one `AddSingleton<IEndpointExampleProvider, ...>()` call at a time.
- `/health/live` (process-up only) and `/health/ready` (checks SQL Server + Hangfire) are
  the health-check endpoints; `/hangfire` is the dashboard, gated to the `SuperAdmin` role.

---

## 3. Core Patterns — Follow These Without Exception

### 3.1 Repository + Unit of Work

- All data access goes through `IUnitOfWork` → named repo properties or
  `GetRepository<T, TKey>()` for generic CRUD.
- **Named repo methods encapsulate all query logic.** No raw LINQ expression predicates
  in the service layer. No `GetQueryable()` calls from services.
- Extended repos (e.g., `IUserRepo`, `IAttendanceRepo`) inherit `IGenericRepo<T, long>`
  and add named methods for every domain-specific query.

```csharp
// CORRECT
var teacher = await _unitOfWork.Users.GetTeacherByUserIdAsync(userId);

// WRONG — raw predicate in service layer
var teacher = await _unitOfWork.GetRepository<Teacher, long>()
    .FindAsync(t => t.UserId == userId);
```

### 3.2 Result Pattern

All service methods return `Result<T>`. Controllers call `ToResponse(result)` from
`ApiBaseController`. Never throw exceptions for business-logic failures.

```csharp
// Service
return Result<MyDto>.Failure(_localizer, "SomeMessageKey");
return Result<MyDto>.Success(dto, _localizer, "Success");

// Controller
return ToResponse(await _service.DoSomethingAsync(...));
```

### 3.3 `teacherId` Must Come from JWT — Never from Route/Body

Sourcing tenant identity from request parameters is an **IDOR vulnerability**.
Use `ResolveTeacherIdAsync()` from `ModuleSixApiBaseController` (or the equivalent
pattern for other base controllers).

```csharp
// CORRECT
var teacherId = await ResolveTeacherIdAsync();
if (teacherId is null) return TeacherNotResolved();

// WRONG — IDOR
long teacherId = request.TeacherId;
```

### 3.4 Localization

All user-facing strings use `IStringLocalizer`. Keys are defined in
`Messages_en.resx` and `Messages_ar.resx`. Egyptian Arabic dialect in the AR file.

### 3.5 Async/Await Everywhere

All I/O operations are `async Task`. No `.Result` or `.Wait()` anywhere.

### 3.6 Constructor Injection Only

No service-locator pattern, no `IServiceProvider` lookups in business code.

### 3.7 XML Documentation

All public interfaces, methods, and non-obvious logic blocks carry `/// <summary>` XML
doc comments. Reference requirement IDs where applicable, e.g. `/// REQ-ATT-007`.

### 3.8 Pagination

List endpoints return `PaginatedResponse<T>`. Page size and page number come from
query parameters. Total count is calculated separately before fetching the page.

---

## 4. EF Core Rules — Read Before Touching OnModelCreating

### 4.1 Fluent API is the SOLE source of truth for FK behavior

**Never mix `[ForeignKey]` data annotations with Fluent API `OnDelete` configuration
on the same relationship.** EF Core 10 silently drops the explicit `OnDelete` behavior
when both exist on the same FK. This caused the NoAction-everywhere bug in an early
migration.

```csharp
// CORRECT — Fluent API only
entity.HasOne(v => v.Teacher)
    .WithMany()
    .HasForeignKey(v => v.TeacherId)
    .OnDelete(DeleteBehavior.NoAction);

// WRONG — annotation coexists with Fluent; OnDelete gets silently dropped
[ForeignKey(nameof(Teacher))]
public long TeacherId { get; set; }
// + HasForeignKey(...).OnDelete(DeleteBehavior.NoAction) in OnModelCreating
```

Use `[ForeignKey]` annotations only when there is NO Fluent API `OnDelete` configuration
for that FK. When in doubt, configure entirely in Fluent API.

### 4.2 Delete Behaviors

| Default policy | Exceptions |
|---|---|
| `NoAction` (app-layer cascade) | Explicitly noted per entity |
| Soft-delete via `IsDeleted`/`DeletedAt` for most entities | Hard-delete for `Session` and `VideoAsset` (see below) |

### 4.3 Hard-Delete vs Soft-Delete

- **Soft-delete** (`IsDeleted` + `DeletedAt`): default for all entities.
- **Hard-delete exceptions**: `Session`, `SessionGroup` (documented), `VideoAsset`
  (audit snapshot captured in `VideoAssetAudit` atomically before deletion).

### 4.4 Composite FKs for Tenant Integrity

When an entity stores `TeacherId` as a denormalized tenant column AND references a
parent that also has `TeacherId`, use a composite FK `(EntityId, TeacherId)` →
`(Parent.Id, Parent.TeacherId)` to enforce tenant integrity at the DB level.
See `VideoScope` → `VideoAsset` for the reference implementation.

---

## 5. Authentication & Security

### 5.1 SecurityStamp Invalidation

The stamp bump **must** run inside the same transaction as the triggering write
(password change, deactivation, permission revoke) via `_authInvalidation.InvalidateUserAsync(userId)`.
It **must be called before** `SaveChangesAsync` so the bump joins the transaction.

Post-commit side effects (activity logging, audit) run **after** `CommitAsync` as
best-effort, wrapped in their own `try/catch`.

```csharp
// CORRECT ordering
await _authInvalidation.InvalidateUserAsync(userId); // stamp bump in tx
await _unitOfWork.SaveChangesAsync();
await _unitOfWork.CommitAsync();
// activity log here, outside tx
```

### 5.2 Service-Layer Commit Ownership

Internal helpers (e.g., `RecordLoginActivityAsync`) must **not** call their own
`SaveChangesAsync`. The caller owns the commit boundary.

### 5.3 FallbackPolicy vs DefaultPolicy

`[Authorize]` on a controller class suppresses the global `FallbackPolicy`
(which carries `ActiveSubscriptionRequirement`). To enforce subscription checks
universally, the requirement must live in `DefaultPolicy`, not only in
`FallbackPolicy`.

### 5.4 JWT Claims

JWT carries only: `NameIdentifier` (user id), `Name` (username), `Role`, `SecurityStamp`.
Permission and module claims were removed (v1→v2 architectural fix — they were stale).
Live permissions are resolved from `UserAuthSnapshot` on every request by
`SecurityStampValidationMiddleware`.

---

### 5.5 File Handling — Central FileObject Registry (shipped 2026-07-16, merge `07aba66`)

ALL uploaded files (video thumbnails, video PDF attachments, exam-question images, the
sign-up national-ID image) live in ONE **private** blob container (`uploads`;
`AzureBlobStorageOptions.UploadsContainerName`) and are tracked one-row-per-file in the
**`FileObjects` registry** (opaque `PublicId` GUID; `OwnerUserId`; denormalized `TeacherId`
tenant set server-side from the JWT; `Category`; `Status`; `BlobPath` — never exposed).
The storage account has `allow-blob-public-access=false`: an anonymous blob URL returning
401/403/409 is the INTENDED state, not a bug — never re-enable public access.

- **Reads**: only via `GET /api/files/{fileId}` (`FilesController`, `[Authorize]`), which
  re-authorizes on EVERY fetch — owner → SuperAdmin → category policy (`FileAccessService`,
  fail-closed) — then 302s to a short-lived SAS (`UploadsSasLifetimeMinutes`, default 240).
  Category policies: `VideoPhoto` (the video's cover image — renamed app-wide from
  "thumbnail" 2026-07-16) / `VideoAttachment` / `VideoExamQuestionImage` = teacher-tenant OR
  a student the OWNING VIDEO is scoped to (`IsScopedStudentOfVideoAsync` →
  `GetOwningVideoAssetIdForFileAsync` + the module's canonical `IsStudentInVideoScopeAsync`,
  which also enforces the Published + PublishDate gate — Draft/scheduled videos never expose
  files to students); `OnlineExamQuestionImage` = tenant OR a student in the exam's LIVE
  assigned set (tenant-scoped EXISTS, `IsQuestionImageAssignedToStudentAsync`);
  `NationalIdImage` = owner+admin only (no resource policy; excluded from
  `FileConstants.UploadableCategories` — created server-side during sign-up). The student
  video list (`GetVisibleVideosForStudentAsync`) returns `videoPhotoUrl` + `attachment` per
  row, batch-resolved (no N+1).
- **Writes**: frontend uploads via `POST /api/upload` (multipart `files` + required
  `category`) → 201 `{fileId, url, ...}` with `Status=Pending`; resource create/update then
  passes `fileId` and the service attaches it via `IFileAccessService.ResolveForAttachAsync`
  (ownership/tenant + category match + 409 claim-stealing guard) INSIDE the resource's
  transaction. Replace/delete on `/api/upload` are fileId-based and ownership-guarded.
- **Lifecycle / cleanup**: `Pending → Attached → Detached`. Deleting/replacing a resource
  DETACHES its files in the same transaction (`DetachAsync`; e.g.
  `VideoService.DetachVideoFilesAsync`, exam replace/delete). Blob deletion happens ONLY in
  the hourly `FileObjectGcJob` (`file-object-gc`): reaps every `Detached` row + `Pending`
  rows older than `UploadsPendingGraceHours` (24), blob-first then row, retried next sweep.
  **Never hard-delete a FileObject row or its blob inline** — that orphans the blob forever
  (only registry-backed blobs are GC-visible). `VideoAssetRepo.DeleteVideoAsync` carries a
  defensive detach `ExecuteUpdate` so the NoAction FK can never block a video delete.
- **Consequences shipped with this change**: videos create/update/replace-video-photo are
  **JSON** (`videoPhotoFileId` / `attachmentFileId` / `ReplaceVideoPhotoRequest` on
  `PUT /api/videos/{id}/video-photo`) — no more multipart or `IFormFile` on those routes; the
  attachment-download endpoint was removed (the gated URL is used directly); `User.IdImage`
  varbinary → `IdImageFileId` FK; `VideoAttachments` table dropped (folded into the registry,
  category `VideoAttachment`, back-ref `FileObject.VideoAssetId`);
  `OnlineExamQuestion.ImageFileId` / `VideoExamQuestion.ImageFileId` added (requests carry
  `imageFileId`, responses carry the gated `imageUrl`). Migrations
  `20260716140711_FileObjectRegistry` + `20260716151304_RenameThumbnailToVideoPhoto`
  (column `VideoAssets.ThumbnailFileId` → `VideoPhotoFileId`; "thumbnail" is "video photo"
  everywhere — enum `FileCategory.VideoPhoto`, DTOs, route, resx keys `VideoPhoto*`).
- **Adding a new file-bearing feature** = add a `FileCategory` value + a policy branch in
  `FileAccessService.IsReadAuthorizedAsync` + a named EXISTS repo method — nothing else.
  Unhandled categories are DENIED by default.

## 6. Hangfire & Background Jobs

### 6.1 Queue Names

| Queue | Purpose |
|---|---|
| `"notifications"` | Subscription reminders, renewal confirmations, payment rejections |
| `"assignment-materialization"` | Recurring assignment occurrence generation |

Queue name is declared on the **interface method** via `[Queue("...")]`, not on the
implementation class.

### 6.2 Architecture Constraint

The Application layer **must not** reference `IBackgroundJobClient` directly.
Background job enqueuing belongs in Infrastructure dispatcher jobs
(e.g., `SubscriptionReminderDispatcherJob`, `RecurringAssignmentDispatcherJob`).
Any existing `IBackgroundJobClient` usage in Application is a **known architectural
violation** flagged for remediation.

### 6.3 Intent-Based Interfaces

Prefer named job interfaces (`IRenewalNotificationJob`, `ISubscriptionReminderJob`)
over generic scheduler wrappers. The interface is the contract; Hangfire's job
activator instantiates through DI.

### 6.4 Idempotency

Every job implementation must be idempotent. Hangfire retries 3 times with
exponential backoff. Throw on failure so Hangfire records and retries.

---

## 7. Module Status & Active Work Items

### 7.1 Module 8 — Messaging (P0 defects outstanding)

| ID | Defect | Status |
|---|---|---|
| P0-A | Auth endpoints commented out on `MessagingController` | **Fixed 2026-07-16** (commit `000f009`) — class-level `[Authorize]` and the `[ModulePermission("Messaging","SendManual")]` / `"ViewHistory"` gates on `send` / `history` / `history/{id}/resend` uncommented; both are registered permissions and the tenant is still JWT-forced by `TenantScopeFilter`. See §8 BUG-13. Do not re-comment. |
| P0-B | `EncryptedCredentials` field never populated (latent bug) | Open |
| P0-C | Wrong phone field guard in send path | Open |
| P0-D | `MessageSenderJob` duplicate log rows on retry | Fix designed — `CreatePendingAsync`/`MarkResultAsync` on `IMessageLogService`, `MessageLogId` threaded through `MessageSendPayload` |

**P1 backlog**: log row correctness, resend restriction, preview/confirm split (REQ-MSG-028).

### 7.2 Attendance Module

- Full auth/IDOR remediation complete (Phases 1–3).
- `ParentAttendanceController` implemented.
- **Open**: `AttendanceRepo.UpdateAbsenceCounterAsync` — same guard fix needed as was
  applied to `PaymentRepo.UpdatePaymentCounterAsync` (forcing `EntityState.Modified`
  on a freshly-`Added` entity is a bug).

**Student/parent-facing month view — occurrence-overlay contract (shipped 2026-07-13,
commit `5bae703`).** `GET /api/attendance/student/teachers/{teacherId}/month?year=&month=`
(mirror: `ParentAttendanceController`; the teacher `AttendanceController` month route shares
the same service method `GetStudentTimelineMonthAsync`). The calendar is driven by the
session's **scheduled `SessionOccurrence`s**, NOT merely by `AttendanceRecord` rows — so the
student sees every class day, including upcoming and not-yet-marked ones. Rules:

- **Two gates, unchanged**: JWT → `StudentUser` → **Active** `StudentTeacherLink` → bound
  `TeacherStudentId` (403 if unlinked/unbound; `ResolveStudentForTeacherAsync`, replicated in
  `StudentAttendanceController`/`StudentVideosController`), THEN
  `IsAttendanceVisibleTo(config, viewer)` on `StudentVisibilityAttendance` /
  `ParentVisibilityAttendance` (fail-closed on null config).
- **`year`/`month` optional** on `StudentTimelineMonthRequest` (now `int?`) → default to the
  teacher's local (Africa/Cairo) current month via `ITimeZoneService`, matching the payment
  module's month scoping.
- **Occurrences clipped to the enrollment window**: for each `StudentSessionAssignment`
  overlapping the month, pull `GetOccurrencesBySessionAndDateRangeAsync` bounded by
  `AssignedAt`/`UnassignedAt` (BR-ATT-001 — no obligation before/after enrollment). Records are
  overlaid by `SessionOccurrenceId`; a record with no matching cell (cross-session present, or
  outside the window) is surfaced as its own date-driven cell so nothing marked is lost.
- **`MonthlyAttendanceSummaryDto`** carries top-level `SessionId`/`SessionName` (screen header,
  from the active/most-recent assignment, else latest record), `TotalOccurrences` (= scheduled
  days = `Days.Count`), `MarkedOccurrences`, `TotalPresent` (Present + CrossSessionPresent),
  `TotalAbsences`, `AttendancePercentage`, a `Days[]` calendar (`StudentAttendanceDayDto`:
  `Date`, `SessionOccurrenceId`, `SessionId`, `SessionName`, **nullable `Status`** where
  `null` = scheduled/unmarked, `IsPast`), and the original `Records[]` (kept for back-compat;
  the export path builds this DTO inline and is untouched).
- **Percentage denominator = Present + Absent only** — `Held` and unmarked/upcoming days are
  excluded. `Status` serializes as a **string** (global `JsonStringEnumConverter`).
- Additive, **no migration**. Apply this same occurrence-overlay shape to the not-yet-built
  student payment/exam/homework calendar views (see §7.2b).

**Auto-absent nightly sweep — unmarked/held ⇒ Absent (shipped 2026-07-24).** DELIBERATE reversal of
the old "unmarked = unknown, never inferred" policy (requested: a student the teacher skipped, or left
on hold, should count absent). This is NOT the historical "harmful silent Absent" per-mark default that
`ValidateCallerStatus` still guards against — that was an ungated default on an explicit mark; this is a
gated, equivalence-safe, auditable background job. Do NOT "fix" it as a regression.
- **Job**: Hangfire recurring `attendance-auto-absent` (`AttendanceConstants.AutoAbsentJobId`, default
  02:30 Africa/Cairo) → `AutoAbsentDispatcherJob` fans out one `IAutoAbsentJob.SweepTeacherAsync(teacherId)`
  per teacher on the `auto-absent` queue → `AttendanceAutoAbsentService` (Application; the job is a thin
  wrapper, §6.2). Config `AutoAbsentOptions` ("AutoAbsent" section): `Enabled` kill switch,
  `EffectiveFrom` (**non-retroactive** — never marks class days before this; default the go-live date
  2026-07-24), `LookbackDays` (rolling catch-up window, default 14), `CronExpression`.
- **What it writes**: for each active-roster student obligated on a PAST occurrence (assigned on/before
  the class day, BR-ATT-001) with no record, an `Absent` row flagged **`AttendanceRecord.IsAutoAbsent`**
  (migration `20260724184931_AddIsAutoAbsentToAttendanceRecords`, `bit NOT NULL default 0`); and it rolls
  an unresolved `Held` record forward to `Absent` (same flag). Then it recomputes the student's
  `StudentAbsenceCounter` from records, refreshes `OccurrenceStatus`, and (post-commit, best-effort)
  fires the normal absence notification + `ReconcileExamsForSessionOccurrenceAsync`.
- **Equivalence-safe (the reason it's a job, not a per-occurrence write)**: a linked session's equivalent
  occurrence (same `(WeekStartDate, DayPositionIndex)` slot) can fall on a LATER weekday (Sun≡Mon), so it
  marks a student absent for a slot ONLY once the **max occurrence date across the home + linked sessions
  has passed** (`GetMaxOccurrenceDateForSlotAsync`), and skips any student with a record on the occurrence
  OR any equivalent occurrence (`GetEquivalentOccurrenceIdsAsync` + the batch equiv check — they attended
  a linked class). Idempotent: a slot with an existing record for a student is never re-marked.
- **Flip on late scan (mark path)**: `MarkAttendanceAsync`/`BulkMarkAttendanceAsync` no longer 409 an
  auto-absent as a duplicate. A same-occurrence `IsAutoAbsent` row is **overwritten** by a later mark
  (teacher marking the morning after isn't forced into Edit). An `Absent` on an **equivalent occurrence**
  (auto OR manual — physical attendance is ground truth) **flips to `CrossSessionPresent`** with the
  attended session/date when the student is scanned Present there — logged as an `AttendanceEditLog`,
  `IsAutoAbsent` cleared, counter recomputed so the "was absent last time" alert (driven by
  `ConsecutiveAbsences`) does NOT surface for the same logical class instance. Shared helper
  `ApplyReconciliationAsync`; edit-reason resx keys `AutoAbsent*` / `AbsentFlippedToCrossSessionPresent`.
- **Scope note**: only CURRENTLY-active assignments are swept (matches the occurrence-status roster); a
  student unassigned since the class day is not retro-marked.
- **`GraceDays` (added 2026-09-10, default 1).** The sweep may only infer an absence on a class day
  older than `localToday - GraceDays` (gate `effectiveMax >= sweepCutoff`; the candidate query is
  bounded by the same cutoff, so it also loads less). Attendance is routinely taken OFFLINE and the
  queued marks only reach the server when the app is next open AND online — at 0 an evening class was
  swept ~6 hours later, overnight, with the app closed, so the sweep wrote absences for a class the
  tutor had already marked. Config-only (`AutoAbsent__GraceDays`); 0 restores the old timing.

### 7.2c Offline attendance + payment sync — the write path must never lose a mark (2026-09-10)

`POST api/Attendance/sync` is the path EVERY offline class takes (a single queued mark uses
`POST mark`; two or more go through the batch). Two short-circuits in it were silently discarding
marks in production — a class showed Present on the tutor's phone and Absent to the parents:

- **A differing server row is not automatically a conflict.** `SyncOfflineRecordsAsync` returned
  `IsConflict` for ANY existing record with another status, never reaching `MarkAttendanceAsync` —
  which has overwritten a system `IsAutoAbsent` row and resolved a `Held` row since the sweep
  shipped. Combined with the 6-hour sweep race above, a whole offline class came back as
  unresolvable conflicts. Now: same status → success; auto-absent-Absent OR Held → fall through to
  the shared mark logic; anything else → conflict (a real tutor decision). Keep the fall-through —
  do not re-add a pre-check that bypasses `MarkAttendanceAsync`.
- **A replay is never withheld for a prompt.** The path now forces `AbsenceAlertConfirmed = true`
  and reports the alert as INFORMATION on the success entry (`SyncEntryResultDto.AbsenceAlertRaised`
  + `AbsenceAlertInfo`). Previously a Present mark for a student with `ConsecutiveAbsences > 0`
  returned `Record = null` and recorded NOTHING; a background drain has no human to answer, the app
  pre-confirms from a CACHED list row that predates the sweep, and a tutor with the absence pop-up
  off never pre-confirms at all. The loss hit exactly the students who were absent last time (~8 of
  100) and was self-reinforcing — the same faces every week. REQ-ATT-057/058 keep their teeth on the
  INTERACTIVE path (`POST mark`), which is untouched. **Do not "restore" the withholding.**
- Payments: `PaymentSyncEntryResultDto.ErrorCode` (the `Result.Code` message key, plus
  `StudentNotAssignedToSession` / `StudentNotFound` for the pre-collect checks) so the app can tell a
  collector holding cash what to DO. `CollectPaymentDto.CollectionNote` always worked on this
  endpoint — the app was sending the key `note`, which bound to nothing; that is why partial collects
  were online-only.

App side (`edvanz-mobile-app`): an op in `conflict` / `needsConfirmation` means NOTHING was recorded,
so it must never be counted as "saved offline" nor painted on the student list (both the scan-result
count and the pending overlay used to do exactly that). A dated student-list snapshot carries THAT
day's statuses — serving it for another day made already-marked students unmarkable and silently
dropped them from bulk-mark, so a mismatched snapshot day now yields membership with every status
nulled. Entry points: home banner, side-menu row with a badge, and a post-drain dialog — the screen
is "Unsent records", never "sync center", and **no user-facing copy anywhere says "roster"**.

### 7.2b Student User Module — Request/Approval Linking (redesigned 2026-07-12; Connection↔Link split 2026-07-13)

The original AAM-FR-05.5 3-credential instant link (TeacherCode + StudentCode +
HashedToken) was **replaced** by a request/approval flow. Do not reintroduce the
credential flow on the student side (the PARENT Method B flow still uses it —
`ParentUserService.LinkTeacherToChildAsync` — until that module is migrated).

- **Lifecycle** (`LinkStatus`): `Pending`(3) → `Active`(1) accept / `Rejected`(4)
  reject / `CancelledByStudent`(6); `Active` → `Unlinked`(2) student removes /
  `RemovedByTeacher`(5). Terminal rows are kept for audit; a **filtered unique
  index** (`[LinkStatus] IN (1,3)`) allows one live row per (StudentUserId,
  TeacherId) and unlimited history — keep the filter literals in sync with the enum.
- **Student side** (`StudentUserController`, `api/studentuser/me/*`): identity is
  ALWAYS resolved JWT → `GetActiveStudentUserByUserIdAsync` (the old route-id
  endpoints were IDOR-prone and were removed). `POST me/link-requests`
  {teacherCode, studentName, studentCode?} creates the Pending row;
  `GET me/teachers` returns the latest row per teacher with `status` so the
  student sees accepted/rejected outcomes; `DELETE me/teachers/{teacherId}`
  cancels a Pending or unlinks an Active row.
- **Teacher side** (`TeacherStudentLinksController`, `api/teacher/student-links`):
  `GET my-code` (shareable 8-digit code), `GET requests` (inbox + suggested
  roster match from the typed student code), `POST requests/{id}/accept`,
  `POST requests/{id}/reject`, `GET` (linked students), `POST remove` (bulk),
  `POST {linkId}/bind` + `POST {linkId}/unbind` (link management — see next bullet).
  One roster record ↔ one student account, enforced BOTH app-level (accept + bind
  re-run `IsTeacherStudentActivelyLinkedAsync`) AND by the filtered unique index
  `UX_StudentTeacherLinks_TeacherStudentId_Active`
  (`[LinkStatus]=1 AND [TeacherStudentId] IS NOT NULL`); the redesign migration
  self-heals legacy duplicate claims (keeps the newest Active row, demotes older
  ones to Unlinked) before creating it.
- **Connection vs Link — SEPARATE axes (shipped 2026-07-13, commit `5d00efd`).**
  Accepting only CONNECTS the account (`LinkStatus.Active`); binding it to a
  `TeacherStudent` (roster) record is a distinct, re-pointable step. `accept` takes
  {`teacherStudentId?` | `studentCode?`} exactly like `bind` (2026-07-16 — both go
  through the shared `ResolveRosterTargetAsync`): a supplied target links atomically
  ("Accept & link") and one that does NOT resolve **fails the accept** (it must never
  silently downgrade to a plain accept — that was the "accept succeeded with a garbage
  code" prod bug); with BOTH omitted it accepts UNBOUND (`TeacherStudentId = null` —
  Active but **Not linked**: connected, sees NOTHING, since every module joins through
  that FK). `POST {linkId}/bind` {`teacherStudentId?` | `studentCode?`} links or
  re-points ("Change linked student"); `POST {linkId}/unbind` clears the binding yet
  stays Active. Both are `Student/Edit`, return the updated `LinkedStudentListItemDto`,
  and re-run the one-account-per-record guard. **Two DIFFERENT codes, never conflate:**
  `studentCode` on these bodies is the TEACHER's roster code
  (`TeacherStudent.StudentCode`, per-teacher unique, e.g. `A12`) — NOT the student's
  globally-unique account code (`StudentUser.StudentAccountCode`, the 10-char
  `studentAccountCode` on link rows). Passing the account code returns 400
  `StudentAccountCodeNotRosterCode` (dedicated message; the generic
  `RosterStudentNotFound` confused the frontend into reporting it as a bug). A wrong
  CODE returns 404 `TeacherStudentCodeNotFound` ("Wrong student code was provided…");
  `RosterStudentNotFound` is now only for a wrong `teacherStudentId`. Message TEXTS
  avoid the word "roster" (user-unfriendly); the `Roster*` resx keys and
  `rosterStudent*` DTO fields keep their names — shipped wire contract. Both DTOs
  reject unknown JSON fields (`JsonUnmappedMemberHandling.Disallow`) so a typo'd field
  400s instead of silently succeeding. `IsLinked` (= `Active && TeacherStudentId !=
  null`) is exposed on the teacher `LinkedStudentListItemDto` AND the student
  `StudentDashboardTeacherDto` — distinct from `Status`; do NOT add a `LinkStatus`
  member for it (the filtered-index literals `[LinkStatus] IN (1,3)` are hand-synced).
  No migration (`TeacherStudentId` already nullable).
  `IStudentLinkNotifier.NotifyLinkBindingChangedAsync(linked)` fires on bind/unbind.
  Accept never auto-matches by the request's typed code — the client passes
  `suggestedMatch.teacherStudentId` for one-tap "Accept & link".
- **End-of-link audit**: `RespondedByUserId` records who accepted/rejected;
  `RemovedByUserId` records who ENDED the link (student on Unlinked/
  CancelledByStudent, teacher/assistant on RemovedByTeacher). Plain columns, no FK.
- **Visibility concept unchanged**: `TeacherConfiguration.StudentVisibility*`
  flags are still returned per dashboard entry and still gate the per-module
  student endpoints (attendance/videos today; payments/exams/homework when built).
- **THIRD axis — session assignment (added 2026-09-05).** Connected → Linked →
  **Assigned** are three SEPARATE gates and only the first two were ever surfaced.
  Videos and online exams are targeted by session ONLY (`VideoScopeType` /
  `OnlineExamScopeType` are Session|SessionGroup — there is NO per-student scope), so a
  linked, bound student with `TeacherStudent.SessionId = null` gets an empty list with a
  **successful 200** on both, and the app could not tell that apart from "the teacher
  published nothing". `GET me/teachers/{teacherId}/home` now returns `sessionId` +
  `sessionName` (null = unassigned), resolved through the named repo method
  `ITeacherStudentRepo.GetAssignedSessionAsync(teacherStudentId, teacherId)` — tenant-scoped
  by matching `TeacherId` on BOTH the student row and the session row. Additive, no
  migration. The student app renders it as a "Session" row plus, when null, a note telling
  the student to ask their teacher; the note is suppressed when the teacher has hidden BOTH
  Videos and Online exams (it names those two modules, so promising them would be false).
  DELIBERATELY NOT guarded against a missing field on the client (product decision) — a
  rollback past this change would show the note to every student. Only the home aggregate
  carries it; `me/teachers` / `me/dashboard` were left alone on purpose (no consumer, and it
  would add a batch join to a hot list endpoint). Student-facing copy says **"Session" /
  «المجموعة»** — never "roster", which users do not understand.
- **Notifications**: `IStudentLinkNotifier` (inbox `UserNotification` +
  FCM push, localized to the RECIPIENT's language) fires post-commit,
  best-effort, on request-received / accepted / rejected / removed-by-teacher.
- Login `teacherIds` now come from Active links joined through
  `StudentUsers.UserId` (`StudentTeacherLinkRepo` — was comparing User.Id to
  StudentUser.Id and ignoring status).

**OPEN WORK ITEM — Parent Method B migration (deferred; still POSTPONED as of
2026-07-13):** `ParentUserService.LinkTeacherToChildAsync` still uses the OLD
3-credential flow (TeacherCode + StudentCode + HashedToken via `LinkTeacherToChildDto`
and `GetTeacherStudentByLinkingCredentialsAsync`). The PARENT side must EVENTUALLY get
the **same treatment as the student side above — request/approval AND the
Connection-vs-Link split**: parent sends a request for a child → teacher accepts,
CONNECTING it → teacher separately binds/unbinds a roster record; a
`ParentChildTeacherLink` lifecycle mirrors `LinkStatus` plus an `IsLinked` flag and
`bind`/`unbind` endpoints. **Deliberately postponed — not scheduled.** Until then: do
NOT remove `TeacherStudent.HashedToken`, the credentials repo method, or the
`StudentCodeRequired`/`HashedTokenRequired`/`InvalidLinkCredentials` resx keys — they
are all load-bearing for parents. Reference spec: this section +
`docs/student-linking-openapi.json`.

### 7.3 DbInitializer Refactor

Proposed split into five partial files. **Scope decision pending** — confirm before
proceeding.

### 7.4 Payment Module — Business Logic (authoritative)

The payment screens (`PaymentScreenService` over the `api/v1/*` routes, plus
`PaymentController`/`PaymentService` for `api/Payment/*`) follow these rules. The
API response shapes are fixed (frontend contract) — change logic, never payloads.

**Periods & the "selected month".** On assignment, one `PaymentPeriod` per month is
generated from the join month to the session end (monthly sessions), so **future
months exist as `Unpaid` rows up front**. Therefore every paid/unpaid/prorated/
outstanding computation must be judged **only through the month the screen is looking
at** — filter `PeriodStart <= selectedMonthEnd`. Never derive buckets/arrears from the
all-time `StudentPaymentCounter` (it counts future months). Repo helpers:
`GetUnpaidPeriodsThroughAsync`, `GetOverdueTotalThroughAsync`. Screens with no explicit
month use the teacher's **current local (Africa/Cairo) month** via `ITimeZoneService`.

**Status buckets (through the selected month).** `Paid` = caught up through that month;
`Unpaid` = any unpaid month ≤ that month; `Partial` = that month partly paid;
`Prorated` = the prorated first month **only when the teacher has proration enabled**.

**Proration rev 2 — ENROLLMENT-ANCHORED (2026-09-03; supersedes the 2026-09-02
first-attendance-anchored model).** The joining (first) month of a genuinely NEW enrollment is
priced ONCE, at assignment, from the teacher-LOCAL assignment day — **attendance never prices the
joining month** (an absence is a missed obligation, not a discount; consistent with the auto-absent
sweep treating assignment as the start of the attendance obligation). Methods
(`TeacherConfiguration.ProrationMethod`): `ByPercentage` = join-day tier; `ByClasses` = scheduled
classes from join day → month end ÷ total that month (0 remaining ⇒ 0-due anchor, born `Paid`);
`Manual` = full until a human sets it. All joining amounts snap to the nearest 5
(`SnapToNearest5`), clamped [0, full]. `ReapplyFirstAttendanceProrationAsync` and its
AttendanceService hooks were **deleted — do not reintroduce an attendance-driven re-price.**
`ComputeProrationSuggestionAsync` always recomputes from `GetEarliestAssignmentDateAsync` (min
AssignedAt across ALL sessions — survives moves) + current settings, never from the stored
fraction (the rev-1 echo made "reset to suggested" circular after a manual override — the
confirmed x1 bug). A human-set amount (`PaymentPeriod.IsProrationManual`, sole writer
`SetStudentProrationAmountAsync`) stays STICKY through every automatic path, is now ALWAYS
audited (period-linked `PaymentEditLog`, actor + suggested + set), surfaced on the lookup
(`prorationSetByName`/`prorationSetAt`) and lists (`isProrationManual`), and is cleared only by
PUT proration with `amount: null`. A settings save whose proration signature changed runs
`ReconcileProrationForExistingStudentsAsync` (idempotent, skips manual + any-cash anchors) and
returns a `ProrationReconcileSummary` on the config DTO so the app reports "N re-priced · M kept"
instead of recalculating silently. Carried never-paid anchors keep their stored fraction on a
move (re-applied to the new base) — no attendance lookup. **The pricing story is FROZEN on the
anchor row** (`PaymentPeriod.ProrationClassesTotal`/`ProrationClassesBilled`, migration
`20260903121426_AddProrationClassBasisToPaymentPeriods`, nullable — ByClasses only): every list
surface (by-status tabs incl. PAID rows, session hub rows, collect list) explains the price as
"Joined {date} · {billed} of {total} classes left · {amount} of {full}" (percent form for By%,
"Set by hand" for manual) via `GetAnchorPeriodInfoByStudentIdsAsync` + the FE
`buildProrationStoryLine` helper — collection never erases the story, and schedule edits/method
switches never rewrite history (display reads the frozen counts, not the live schedule). The
COLLECTOR-scoped ledgers tell the same story (increment 3): `CollectionRow` carries
`ProrationJoinedAt`/`ProrationClasses*`/`ProratedFirstMonthAmount`/`ProrationFullMonthAmount`
(full base reconstructed as `round(AmountDue ÷ fraction)`) filled by
`EnrichProrationTransparencyAsync`, rendered NAME-FREE (the ledger is already scoped to one
person — per-row names are noise; the who-set-it audit stays in the collect editor). Related
UX merge (2026-09-03): tapping an assistant on the collectors card opens ONE merged
wallet+ledger screen (`TeacherPaymentSessionCollectionsView` wallet mode) — balance card +
withdraw + hand-over history above the full ledger, opening on the "in hand now" scope (exact
window since the last hand-over, so rows sum to the balance) with an "all months" chip; the
collections endpoint's `from`/`to` accept EXACT instants bounding [from, to) when the RAW query
value carries a time component (the controller passes `exactRange` into
`GetCollectionsByMonthAsync`; date-only callers unchanged — inclusive whole days), and the wallet
response's `collections.sinceAt` anchors it (strict `heldSinceAt` for scoping). Fixed 2026-09-04:
the old `TimeOfDay != 0` inference misread the wallet day filter's midnight-to-midnight window as
date-only and served the whole NEXT day inclusively — never reintroduce value-based inference,
the raw text is the only carrier of that intent. The mobile cubit sends drawer/day bounds as UTC
instants ('Z' suffix): `CollectedAt` is UTC, day boundaries must cut on the backend's raw-UTC
`dayKey` that the day headers/nets/insights bucket by, and the earlier local-wall-clock echo
anchored "in hand now" ~3h late for Egypt devices (dropping post-hand-over rows from the sum).

**Buckets are assigned-only and reconcile to `TotalStudents`.** The status headcounts
(`statusBreakdown.paid/prorated/unpaid` on `/api/v1/payments/tracking`) and the per-status
lists (`/api/v1/payments/students?status=…`) classify **only students currently assigned to
a session** (`TeacherStudents.SessionId != null` — the same population as
`CountAssignedStudentsAsync`/`summary.totalStudents`). So `paid + prorated + unpaid ==
totalStudents` by construction: an assigned student with no outstanding period through the
selected month (caught up, or no obligation generated yet) counts as `Paid`. **Do not** count
students off their historical `PaymentPeriods` alone — formerly-assigned students keep old
periods and would inflate the buckets past the assigned headcount (this was the paid+unpaid >
total bug). Both `GetStudentPaymentStatusCountsAsync` and `GetStudentsByPaymentStatusPagedAsync`
gate on the assigned-student id set. Trade-off: unassigned students with lingering arrears are
excluded from these headcounts/lists (their money still shows in cash/expected aggregates). Edge
case: the `paid` count may exceed the `status=paid` list by 1 per assigned student who has no
period row at all (rare — assignment normally generates one).

**Collection engine (`CollectPaymentAsync`, monthly sessions).** A payment fills the
**oldest unpaid month first and cascades forward** across months; each cleared month is
attributed to its own period, while **one** `PaymentTransaction` records the whole cash
event (dated now). Advance is capped at **current month + 1** (end of next month); cash
beyond that is rejected with `PaymentAmountExceedsAdvanceLimit` (422). Partial payments
allowed (a short month stays `PartiallyPaid`). The counter advances
`TotalPaidPeriods`/`TotalUnpaidPeriods` by the number of months a single collection fully
cleared. **Per-session (per-class) billing keeps its original single-period behavior** —
the monthly rules apply to `PaymentType.Monthly` only. `mark-paid` and the collect
`lookup.AmountDue` use the **server-computed total arrears through the current month**,
not a single month or a client-supplied amount.

**Dashboards = actual cash.** Dashboard/wallet "collected this month" is **actual cash
physically collected this calendar month** (by transaction date, net of refunds via the
`!IsDeleted` filter), so it can exceed "expected" when students pay arrears or one month
ahead. "Expected" stays period-based (assigned students × their monthly amount). Repo:
`GetCashCollectedInRangeAsync`. The shared `GetDashboardAggregatesAsync` is period-based
and reused by reports — do not switch it to cash; add a dedicated method instead. All
per-session dashboard figures are month-scoped (`GetSessionMonthCollectionAsync`,
`GetAssignedStudentCountsPerSessionAsync`); `TotalStudents` = students assigned to a
session (`CountAssignedStudentsAsync`).

**Assistant wallet & refunds.** Wallet rises on collect, falls on refund; refunds are
negative-amount entries in the same month log as collections
(`GetCollectorTransactionsInRangeAsync` + `GetCollectorRefundsInRangeAsync`, merged).
**Who a refund is charged to depends on its kind (changed 2026-08-24):** a **delete/edit**
(`Deleted` `PaymentEditLog`) is a CORRECTION — charged to the **original collector**
(the transaction's `CollectedByUserId`) whose figure it corrects, no fresh cash moves; a
**departure refund** (`Reversed` log, sole writer `ReverseDeparturePeriodAsync`) is a
physical PAYOUT — charged to the **performer who confirmed the departure**
(`EditedByUserId` on the log; `StudentDeparture.CollectedByUserId` also holds the
performer despite its name — their drawer handed the cash back, the original collector's
held cash is untouched). Migration `20260824190558` flipped historical departure rows.
`TotalCashCollected` = money in − money out for the month. **Withdraw** = tutor taking
cash (a `WalletResetLog`), reduces `CurrentBalance` only, distinct from a refund.

**Transfer between sessions.** No proration on monthly→monthly; the carried balance is
the source session's **arrears through the current month** (`GetOverdueTotalThroughAsync`,
not the all-time counter), written as one `IsCarriedForward` period in the destination.

**Purge.** Permanently deleting a student **deletes** its `PaymentPeriods`
(`OnStudentPermanentlyDeletedAsync`) — do not orphan them (nulled periods leak their
`AmountDue` into aggregates). The `PaymentTransaction→PaymentPeriod` FK is
`ON DELETE SET NULL`, so audit transactions survive with denormalized data. Dashboard
aggregates also defensively exclude `TeacherStudentId == null` periods.

**Aggregates exclude orphans.** Any period-summing query that feeds a total must ignore
`TeacherStudentId == null` rows.

### 7.4a Removed collectors + the assistant's own payment screen (2026-09-05)

**A removed collector is HISTORY, not roster.** The tracking collectors card
(`GetTrackingAsync` → `CollectedByAssistant.Assistants`) is built from the WALLET roster
(`GetAllAssistantWalletsAsync`), and an assistant's soft-delete is terminal (`AssistantCleanupJob`
is a no-op) so their wallet row lives forever — a removed person used to surface on EVERY future
month as a silent "0 EGP / 0 payments" card. A wallet row is now included for month *M* only when
the collector is not removed, **or** the teacher-local `Assistant.DeletedAt` /
`CenterAssistant.DeletedAt` falls on/after `monthStart` — removed in July ⇒ visible in July and
every earlier month, gone from August on. This is DELIBERATE; do not "restore" the missing row.
Two invariants hold it together: (a) delete is blocked while the wallet holds cash and kills the
login, so a removed collector can neither collect nor confirm a departure afterwards — their
post-removal months are always empty and the hidden rows carry no money; (b) an **activity escape
hatch** keeps any row with a non-zero month total regardless of removal, so the visible cards always
reconcile with `CollectedByAssistant.TotalCollected` (which stays activity-driven over ALL
collectors). Surviving rows carry `isRemoved: true` and the app renders a muted "Removed" chip. The
activity-driven `byCollector` on `/collections/summary` was already correct and is untouched.

**The assistant's payments tab is the teacher's ledger, own-scoped.** `AssistantPaymentView` keeps
its cash-bag card + Collect + Student-leaving actions (no withdraw — teacher-only) but its list is
now the SAME ledger the teacher's assistant drill-in shows: `TeacherPaymentSessionCollectedCubit`
(scope chips "collections in wallet"/"all collections", month nav, day filter + day insights,
"collections only", amount tiers, day totals + nets, proration story rows, skeleton loading), driven
by the shared `PaymentWalletLedgerCoordinator` / `PaymentCollectionsLedgerList` /
`PaymentCollectionsFiltersSection` widgets that BOTH screens use — extend those, never fork them
back per screen. Two backend gaps closed with it:
- **Forced own-scope (was a tenant-internal IDOR).** `GET /api/v1/payments/collections` and
  `/collections/summary` never applied `AssistantScopeUserId()`, so an assistant with
  `Payment.ViewHistory` could omit `collectedByUserId` and read the tutor's WHOLE ledger, or forge a
  peer's id. Both now do `collectedByUserId = AssistantScopeUserId() ?? collectedByUserId`, mirroring
  the wallet / wallets / collector-summary endpoints. Teacher/SuperAdmin behaviour is unchanged.
- **`ModulePermission(..., alsoAllowPermission:)`** — an optional SECOND permission that also
  satisfies a gate (checked only after the primary fails, and never past a module-not-assigned
  failure). Widen-only by construction: no existing caller can lose access. Used on
  `/api/v1/payments/collections` so `ViewCollectorSummary` — the grant that already opens the
  assistant's wallet over the very same rows — passes too, otherwise the rebuilt tab would 403 for
  assistants whose tutor granted only that. Argument binding for the extra ctor string is positional
  through `ActivatorUtilities`; it was verified against all four attribute shapes (module+permission,
  +alsoAllow, roleOnly, module-only) before shipping.

### 7.4b Billing start date — tenant-wide billing floor (added 2026-09-03)

`TeacherConfiguration.BillingStartDate` (nullable, teacher-local, always first-of-month) is an
ONBOARDING billing floor: a teacher entering the roster in August while classes/billing begin in
September sets September and NO pre-September obligation exists. Null = off = historical behavior
byte-for-byte. Rules:

- **Generation clamp** (`BuildSessionPeriodsAsync`, both branches): the ladder starts at
  max(assignment date, floor) — the floor-clamped date is ALSO the proration join day (Aug 15 entry
  + Sept floor ⇒ Sept-1 join ⇒ day-1 tier / all classes ⇒ full first month). PerSession skips
  occurrences before the floor. The optional `billingStartOverride` parameter exists so the
  reconcile can dry-run a floor that is not yet saved — do not remove it.
- **Carried debt is NEVER the floor's business**: `IsCarriedForward`/`MovedFromSessionId` rows are
  agreed money, not generated months — the reconcile skips them and the DB2a pending-debt fold-in
  in `OnStudentAssignedToSessionAsync` clamps its delete cutoff to the floor so a pending month in
  [assignment, floor) re-attaches as arrears instead of being deleted-but-never-rebilled.
- **`ReconcileBillingStartAsync(teacherId, floor, dryRun)`** (runs on the caller's transaction):
  TRIM deletes pre-floor rows with no cash, not manual, not carried (kept rows reported as
  keptPaid/keptManual — cash and human decisions are ground truth); BACKFILL regenerates months an
  EARLIER floor newly allows for currently-assigned students via the shared generation pipeline
  (front-gap bounded — trailing gaps belong to the end-date backfill); RE-ANCHOR moves
  `IsProrationAnchorMonth` to the surviving first month (cash-free, non-manual anchors only),
  re-priced per the current method; counters fully recomputed per affected student. Summary
  (`BillingStartReconcileSummary`) is surfaced on the config-save response
  (`billingStartReconcile`, sibling of `prorationReconcile`) — never reconcile silently.
- **One-time self-service**: the teacher's first set is free; after that
  (`BillingStartDateSetAt != null`) changes 403 `BillingStartDateLocked` until support re-grants
  ONE change via `BillingStartDateChangeAllowed` (consumed on use). On the wire the update DTO
  field is NULLABLE ON PURPOSE (omitted/null = unchanged — teachers can never clear the floor;
  old app builds must not disturb it); re-sending the identical value is an idempotent no-op that
  neither trips nor consumes the lock. Config GET exposes `billingStartDate` +
  `billingStartLocked`.
- **Admin surface** (`AdminPaymentController`, SuperAdmin roleOnly):
  `POST /api/admin/payments/billing-start?teacherId=&date=&dryRun=` sets + reconciles in one
  transaction (dryRun default TRUE writes nothing; an admin set re-locks the teacher);
  `POST /api/admin/payments/billing-start/allow-change?teacherId=` re-grants the one-time change.
- Migration `20260903162852_AddBillingStartDateToTeacherConfigurations` (additive columns only).

### 7.5 Offline Exams (`/api/exams`) — during-session dates are PER SESSION (restored 2026-07-17)

Create/update contract (v1.3 — `docs/exams-api-guide.md`, `docs/exams-openapi.{json,yaml}`):
recipient stays `sessionIds` XOR `groupIds`, but a **DuringSession** exam anchors EVERY resolved
session — including each member session of a selected group — to its own picked class occurrence
via `sessionOccurrences: [{sessionId, sessionOccurrenceId}]` (ids from
`GET /api/exams/session-dates`, called once per session; the entries must cover the resolved
session set exactly; a pick may be in the past — the class's attendance back-fills the exam).
The single `examDate` belongs to **SeparateTime only**. Sending the wrong date field for the
delivery type is a 400 (`ExamDateOnlyForSeparateTime` / `SessionOccurrencesOnlyForDuringSession`).
Create and update validate through ONE shared pipeline (`ExamService.BuildSessionPlansAsync`) —
extend it, never fork it back into the two methods. **History (do not reintroduce):** the
original per-session design (`479a2e2`) was replaced with a single global `examDate` in
`2bc840b`; that was a regression (its commit message says "frontend-requested" — it wasn't the
planned design). Restored 2026-07-17 while keeping `2bc840b`'s keepers (groups recipient, home
pagination/scope, `teacherStudentId` write keys). PUT `/api/exams/{examId}` mirrors create;
structural edits still 409 once results exist (`ExamHasResultsCannotRestructure`).

### 7.6 Subscription plans — ManagerialPlus ("Managerial + Parents", added 2026-09-03)

Three plans: `Full=1`, `Managerial=2`, `ManagerialPlus=3` (display "Managerial + Parents" /
«إداري + أولياء الأمور» — the enum identifier is the stable wire value, clients localize labels).
ManagerialPlus = Managerial rules (no student app accounts, no in-app parent accounts) PLUS the
public parent follow-up page. The plan → feature map lives in ONE place —
`SubscriptionPlanCapabilities` (Domain.Helpers) — consumed via
`ISubscriptionGateService.GetPlanEntitlementsAsync(teacherId)` → `{PlanType,
StudentAccountsAllowed, ParentFollowUpAllowed}`. **Never compare plan values inline at a gate
site**; a new plan is a change to the capabilities class + pricing only. Restrictions apply only
while the plan is Active/ExpiringSoon; expired/none = unrestricted (free-tier quotas gate
creation instead). `IsManagerialAsync` is kept as the legacy alias for
`!StudentAccountsAllowed` (both managerial plans return true). Related invariants:
- Settings gate: enabling `ParentPortalEnabled` false→true without the capability → 403
  `ParentPortalRequiresSubscription`; an already-true stored value never fails unrelated saves
  (the read-time gate `ParentPortalService.IsPortalEligibleAsync` keeps the portal closed, and
  re-opens it automatically on upgrade — the stored toggle is never rewritten by plan changes).
- Wire entitlements: `GET /api/subscription/status` carries `features
  {studentAccountsAllowed, parentFollowUpAllowed}`; the parent-portal summary carries
  `portalAllowed`. Clients gate screens ONLY off these (fail-open on absence) — the app never
  hardcodes plan semantics. Blocked messages: `ManagerialSubscriptionNoStudents` (Managerial)
  vs `PlanNoStudentAccounts` (ManagerialPlus) via
  `SubscriptionConstants.StudentAccountsBlockedMessageKey`.
- Pricing: flat `ManagerialPlusMonthlyPriceEGP` (seed 650) on `SubscriptionPricingSettings`;
  admin PUT pricing accepts the flat prices as NULLABLE (omitted = unchanged — older admin
  clients must not zero them). Renewal amount is plan-aware (flat for managerial plans).
- Admin: `POST /api/admin/subscriptions/activate-managerial-plus` (same body as
  activate-managerial; `RemoveExistingLinks` severs student/parent-ACCOUNT links only, NEVER
  parent-portal follow-up grants).
- Centers: symmetric third slot type — `ManagerialPlusTeacherSlots` +
  `StudentCapacityUnderManagerialPlus` on CenterSubscriptions/-Requests,
  `ManagerialPlusTeacherSlotPriceEGP` (seed 65) on center pricing,
  `FreeTierManagerialPlusTeacherSlots = 0`; slot checks go through
  `CenterService.SlotsForPlan` (the single three-way switch). `Teacher.CenterPlanType` may be
  ManagerialPlus; the gate reads it through the same current-subscription projection.
- Migration `20260903154829_AddManagerialPlusPlan` (additive columns + seeded-row updates).

### 7.7 Parent portal sign-in — an unknown student code is REJECTED, not swallowed (reversed 2026-09-11)

**DELIBERATE REVERSAL of the original enumeration design. Do NOT restore the silent behaviour.**
The portal (`~/Desktop/Edvanz-Application/EdvanzParentPortal`, own git, manual FTP) asks a parent
for TWO codes; the teacher's own WhatsApp share message asked for ONE (the teacher code), so
parents arrived not knowing the second, guessed, and `RequestAccessAsync` answered a nonexistent
`StudentCode` with the byte-identical "pending" payload a real request gets — **writing nothing**.
The portal's `/pending` then treated every unrecognised state (including `none`, i.e. *no row
exists*) as "still waiting" and refreshed every 15s **forever**. The parent saw «طلبك اتبعت»; no
teacher ever saw a request. Reproduced on prod 2026-09-11: fake code → `pendingCount: 0`, real code
→ inbox row, **identical screen for both**.

- **Unknown student code → 404 `ParentPortalStudentCodeNotFound`**, an inline error under that
  field exactly like a wrong teacher code. Enumeration is now a **budget, not a blanket**:
  `UnknownStudentCodeRepliesPer{Device,Teacher}PerHour` (8 / 40, `IDistributedCache`) meter honest
  answers and revert to the old neutral payload once spent — fail **open** on a cache error. Student
  name/code/id are still only returned on an `active` result: *that* a code exists is answerable,
  *whose* it is is not.
- **Trust is evaluated BEFORE the post-rejection cooldown** (`ParentPortalService` step 5b, then
  5c). It used to be the other way round, which meant the one remedy the product advertises — the
  teacher adding the parent's number to the student record — silently did nothing for 24h. The
  cooldown now answers 409 `ParentPortalRequestPreviouslyRejected` and names that remedy.
- **A live PENDING row is RE-EVALUATED for trust and promoted in place.** The `existing` branch
  returned "still waiting" without re-checking anything, so a parent who first submitted with no
  phone and then supplied one — or whose TEACHER added the number to the student record — stayed
  queued forever and only a manual approval could release them. That is the same remedy the cooldown
  message and the waiting screen both advertise, silently doing nothing. `GetLiveByStudentAndDeviceAsync`
  loads the row TRACKED with the comment "the request path may promote this row to Active"; nothing
  ever did. Promotion sets Active/RespondedAt/AutoApproved/Origin, stores the number that earned it
  (so a later device change is re-admitted), leaves `RespondedByUserId` NULL, and on a write failure
  returns pending rather than lying. Trust lives in ONE place — `EvaluateTrustAsync`, shared by the
  new-request and promote paths; never re-inline it, or the two answers drift.
- **Phone REQUIRED** (`ParentPortal__RequirePhone`, default false for deploy ordering exactly like
  `RequireParentName`; flip BOTH after the portal upload). It is the only thing that admits a parent
  with zero teacher involvement, the only detail a teacher can recognise, and the only way back in
  after a lost device cookie — which iOS in-app browsers drop routinely.
- **The inbox row is now written for EVERY request**; only the push is batched to one per hour
  (`suppressPush`). The old early-return wrote nothing at all for the 2nd..Nth parent in an hour —
  and `Firebase__CredentialsPath` on prod is a 1-char placeholder with no vault secret, so FCM
  no-ops platform-wide and that row is the ONLY signal a teacher gets.
- Portal side: `/pending` renders **only** a real `pending` (anything else → "no request waiting" +
  a way back to the form); a `?retry=1` route that clears the remembered state; API `code` mapped to
  the FIELD that caused it (`signin_error_field`); student code upper-cased with inner spaces
  stripped; phone validated locally against a PHP mirror of `EgyptianPhoneNumber.Normalize` (keep
  the two in step); the per-hour throttle keyed **per IP+device** (Egyptian carrier CGNAT made a
  per-IP bucket punish unrelated parents) and 10 distinct codes per 30 min.
- **`includes/mock.php` returned 404 for state `none` while the real API returns 200 + `state:
  "none"`** — that fixture lie is *why* the waiting-screen bug survived review. Fixtures mirror the
  API, never the convenient behaviour.

**Same-day follow-up audit — five more defects on this path, all fixed 2026-09-11:**

- **SIBLINGS: a device may hold a live grant per child.** Reads resolved a device to its NEWEST
  active grant and required the route id to equal it, so a parent who signed in for a second child
  could never reach the first again — re-entering the older code still landed on the newer one, and
  the only escape was to end following altogether. `GetActiveByDeviceAndStudentAsync` authorizes the
  route id against the device's OWN grants (identical strictness, finally correct for two children),
  `GET /access?rosterId=` selects which child the payload describes, `students[]` lists them all,
  and the portal renders switcher chips in the app bar — whose doc comment had promised exactly this
  since day one. Selection lives in the PHP session, is re-synced from what the API actually
  returned (a revoked child degrades to the other rather than erroring), and the access cache key
  carries the child id — keying it on "access" alone serves the previous child for a minute after
  every switch.
- **The waiting screen polled twice over.** `<meta refresh 15s>` AND a 5s JS fetch, each a full
  render plus an uncached API call ≈ 960 requests/hour per waiting parent, indefinitely. portal.js
  now REMOVES the meta tag when it takes over and backs off 5s → 60s (resetting on tab focus); the
  no-JS fallback is 30s.
- **`ResolveTeacherHeaderAsync` cost FIVE round-trips** — one name and one label via the bulk
  dashboard loader, on every request and every poll. Now one query, `GetPortalTeacherHeaderAsync`.
  Deliberately uncached: it carries the config that gates eligibility and visibility.
- **Phone capture from the inbox.** `BulkResolveAsync` captured NOTHING, the worst possible place
  for that gap — the select-all lane exists precisely because a whole class arrives at once, so the
  teacher's highest-volume action guaranteed every one of those parents would need approving again.
  Bulk now fills EMPTY records only (`savePhoneToStudent`, ON by default in the app, NEVER
  overwrites — nobody judges forty "mother or father?" questions in one tap) and reports
  `phonesSaved`. Single approve may REPLACE a differing number via a separate
  `overwriteStudentPhone` flag behind its own confirmation naming both numbers; the number on file
  may be a second parent rather than a stale one, and only the teacher knows which.
  `studentParentPhone` is exposed on the list row (only when it differs) so that choice is informed.
- The approval sheet rendered a phone icon beside an empty line for requests carrying no phone, and
  the missing-parent-phone nudge lived only in Settings — it is now on the inbox itself, where the
  teacher is actively approving people who would not have needed approving at all.

---

## 8. Known Bugs (Fixed — Do Not Reintroduce)

| Bug | Location | Fix |
|---|---|---|
| BUG-1 | `UnitOfWork.CommitAsync` | `_transaction` was not nulled after commit; `HasActiveTransaction` stayed `true`. Fixed. |
| BUG-2 | `GenericRepo.UpdateAsync` / `DeleteAsync` | EF Core `Entry().State` and `Remove()` are synchronous; async signature kept for convention but `await Task.CompletedTask` added. |
| BUG-3 | `PaymentRepo.UpdatePaymentCounterAsync` | Was forcing `EntityState.Modified` on a freshly-`Added` entity. Fixed with state guard. |
| BUG-4 | EF FK/annotation coexistence | `[ForeignKey]` + Fluent `OnDelete` silently drops `OnDelete`. Fluent API is sole authority. |
| BUG-5 | Payment buckets counted future months | Paid/unpaid/outstanding derived from all periods (incl. pre-generated future months) → everyone read as unpaid. Now judged through the selected month. See §7.4. |
| BUG-6 | Purge orphaned payment periods | Permanent student delete nulled `PaymentPeriods.TeacherStudentId`, leaving orphans that leaked `AmountDue` into dashboards. Now periods are deleted on purge; aggregates also exclude null-student rows. See §7.4. |
| BUG-7 | `AttendanceRepo.GetPagedAttendanceStudentListAsync` | `occurrenceId.HasValue ? subquery : null` in a projection put an **untyped NULL constant** in the SQL tree; EF throws `Expression 'NULL' in the SQL tree does not have a type mapping assigned` at query-compile time for any date with no `SessionOccurrences` row → data-independent 500 (the 2026-07-12 attendance student-list outage; PR #8 was innocent). The occurrence guard now lives INSIDE the subquery `Where`, so the member is always a typed subquery. Never project a bare `null` branch against a subquery. |
| BUG-8 | Same method — purge ghosts | Purging a student SET-NULLs `StudentSessionAssignments.TeacherStudentId` but leaves `IsActive=1`; the row surfaced as an "Unknown" student and crashed the `TeacherStudentId!.Value` mapping. Assignment queries that materialize students must filter `Where(a => a.TeacherStudent != null)` (also hides soft-deleted students via the global filter). |
| BUG-9 | Duplicate migration lineages | The `video phase 01` merge re-added the old init migration chain alongside the 07-09 baseline chain; two baseline-chain migrations duplicated index creations (`Add_TeacherStudent_Phone_UniqueIndexes`, `Add_PP_Status_PeriodStart_Index_Catchup`), so `dotnet ef database update` on a FRESH database failed (error 1913). Duplicates deleted 2026-07-12; prod unaffected (their ids remain in `__EFMigrationsHistory`; EF ignores orphan history rows). Keep a single lineage and verify a fresh-DB build after any merge touching `Migrations/`. |
| BUG-11 | `20260708193718`/`20260708220307` phone-index migrations never applied on prod | The first created a GLOBAL unique `ParentPhoneNumber` index that included soft-deleted rows → failed on an existing duplicate; the second then failed dropping the index the first never created. Both re-ran and re-failed silently on every deploy (no `-b`, see CI note below), so prod had NO phone uniqueness (duplicate-phone protection relies on the DB index via `ResolveUniqueViolationKey`) and was missing `IX_PP_TeacherId_Status_PeriodStart`. Fixed 2026-07-16: both files deleted (BUG-9 precedent) and `20260715231605_RepairTeacherStudentPhoneIndexes` defensively converges all environments — parent phone index recreated NON-unique (siblings share it — see the uniqueness rule below), student phone unique filtered, active student-phone duplicates cleaned (earliest row keeps the phone), PP index caught up. |
| BUG-10 | `20260715202558_AddSessionOccurrenceSlotKeys` backfill referenced same-batch new columns | The migration did `AddColumn DayPositionIndex/WeekStartDate` then a bare `migrationBuilder.Sql` **UPDATE** setting those columns. EF emits a migration's ops as ONE `GO`-less batch, and the idempotent deploy script keeps them in one batch, so SQL Server bound the UPDATE at batch-compile time when the columns didn't exist yet → **error 207 "Invalid column name"** (under the idempotent IF-wrapper it surfaced as a downstream **1505** duplicate-key: the un-backfilled default `(2000-01-01,1)` rows collide on the new unique index). The migration silently failed to apply (see the CI note below), yet the code that queries the columns shipped → prod 500'd on every attendance call (the 2026-07-15/16 outage). **Fix:** wrap the backfill UPDATE in `EXEC(N'...')` so name resolution is deferred to run time, after the columns exist. Applied to prod out-of-band and recorded in `__EFMigrationsHistory`. Never backfill a just-added column with a bare `Sql()` UPDATE in the same migration — use `EXEC()` (or a separate migration). |
| BUG-12 | `ParentUserController` (all 10 endpoints) | Mass horizontal IDOR: the controller had NO `[Authorize]`/`[ModulePermission]` and injected no identity service, so `parentUserId`/`childId` were trusted straight from the route — any authenticated user (any role) could read/modify/delete ANY parent's profile, dashboard, and children. Fixed 2026-07-16 (commit `ece9fab`): identity is resolved ONLY from the JWT via `ResolveParentUserIdAsync()` (`User.Id` → active `ParentUser`); the `{parentUserId}` route segment is kept for wire-compat but IGNORED (0/null/wrong/mismatched all behave identically); every `childId` is scoped to the resolved parent inside the service (`GetActiveChildAsync(parentUserId, childId)`); class `[Authorize]` + per-endpoint `[ModulePermission(roles:["Parent"], roleOnly:true)]` added; `InitializeParentUser` forces `dto.UserId` from the JWT (registration still initializes parents server-side via `UserService`, unaffected). Mirrors `ParentAttendanceController`/`ParentPaymentController`. Generalizes §3.3 — never trust a route/body identity id (teacherId, parentUserId, childId); resolve from the token. |
| BUG-14 | Parent portal: a wrong student code was answered "request sent" and waited forever | `RequestAccessAsync` returned the neutral pending payload for a nonexistent `StudentCode` **without writing a row**, and the portal's `/pending` treated the resulting `state: "none"` as "still waiting", refreshing every 15s indefinitely. The parent believed they had asked; no teacher could ever see or approve anything; support could not tell the case apart from a slow teacher. Fixed 2026-09-11 — honest 404 + a metered enumeration budget, and `/pending` renders only a genuine `pending`. Full contract in §7.7. **Never make a discarded request answer `pending` again**, and never let a screen assert a state the API did not return. |
| BUG-13 | `MessagingController` auth commented + `TeacherController.GetTeachers` (`GET /api/teacher/list`) ungated | Two authorization holes closed 2026-07-16 (commit `000f009`). (a) MessagingController's class `[Authorize]` and the `send`/`history`/`resend` `[ModulePermission]` gates were commented out (this was §7.1 P0-A) → any authenticated caller could send manual messages, read history, and resend; restored verbatim — `"SendManual"`/`"ViewHistory"` are registered Messaging permissions (`DbInitializer`), `roleOnly:false` runs the `PermissionRequirement(module,permission)` check, and the tenant is still JWT-forced by `TenantScopeFilter`. (b) `GetTeachers` is documented Super-Admin-only but carried no role gate → any authenticated user could enumerate every teacher (name, code, phone, capacity, subscription); added `[ModulePermission(roles:["SuperAdmin"], roleOnly:true)]` (`roleOnly:true` → role-membership gate). Do not re-comment controller auth attributes or ship an admin-only endpoint without a role gate. |

**CI migration delivery — root cause of the 2026-07-15/16 attendance outage (deploy.yml `Apply EF migrations`) — RESOLVED 2026-07-16.** `azure/sql-action@v2` used to run the multi-batch idempotent `migrate.sql` (one `BEGIN TRAN…COMMIT` per migration) via go-sqlcmd **without `-b`**, so when a migration's batch errored, its own transaction rolled back (migration NOT recorded) but the runner **continued to the next migration and still exited 0** — a broken migration was silently skipped while the code that needed it deployed anyway. This is why BUG-10 shipped, and it also silently skipped the `20260708193718`/`20260708220307` phone-index migrations on every deploy since 2026-07-08 (see BUG-11). Fixed by: (a) BUG-11's repair migration clearing the failing backlog, (b) `arguments: '-b'` on the sql-action step (any SQL error → non-zero exit → job fails BEFORE `az webapp deploy`), and (c) the two pre-Azure migration gates described in §0 (model-coverage check + fresh-DB rehearsal of `migrate.sql`). Do not remove `-b` or the gates.

**Phone-number uniqueness rule (decided 2026-07-16):** `ParentPhoneNumber` is **NOT unique** — one parent legitimately has several children on the same roster, so it carries only a non-unique lookup index `IX_TeacherStudents_TeacherId_ParentPhoneNumber`. `StudentPhoneNumber` IS unique per teacher among active rows (filtered `IS NOT NULL AND IsDeleted = 0`). Bulk import dedupes student phones (not parent phones) within a batch; `ResolveUniqueViolationKey` maps only StudentPhoneNumber/StudentCode violations.

---

## 9. Seeding Rules

- **Service-driven seeding only** (all data flows through Application layer services).
- Never fabricate transactional rows (attendance records, payment counters, session
  occurrences) directly via repo in seeders — produces referentially inconsistent state.
- `DbInitializer` is the entry point. Follow Option A (service-driven) established pattern.

---

## 10. Naming & Code Conventions

- **PascalCase** for all C# identifiers (classes, methods, properties).
- **camelCase** for `PaginatedResponse` fields (existing convention).
- **`ResolveTeacherIdAsync()`** — canonical method name for JWT→teacher resolution in
  base controllers.
- **`ToResponse(result)`** — canonical method name for Result→IActionResult in
  `ApiBaseController`.
- Module-scoped base controllers (e.g., `ModuleSixApiBaseController`) for shared JWT
  resolution logic. Do not copy-paste the resolution logic into individual controllers.
- Required-on-the-wire DTO fields use `[Required]` so ASP.NET model binding surfaces
  400s with field names automatically.
- Enum-valued query parameters use `JsonStringEnumConverter` so Swagger and clients
  see string values, not integers.
- `[Timestamp]` on `RowVersion` properties for optimistic concurrency.

---

## 11. Requirement IDs

The authoritative spec is `Edvanz_Requirements.pdf` (attached to the Claude.ai project).
Reference IDs follow this pattern:

- `REQ-XXX-NNN` — Functional requirements
- `BR-XXX-NNN` — Business rules
- `AAM-FR-XX-NN` — Assistant Access Management requirements

Always cross-reference code comments to the relevant `REQ-*` / `BR-*` IDs.

---

## 11b. Time, dates & timezones (hardened 2026-09-06)

Authoritative detail: **`TIMEZONE_STANDARD.md`** (backend) and the app's own
`TIMEZONE_STANDARD.md` (Flutter). The short version:

- **Three kinds, three types.** A moment-in-time is a `DateTime` in UTC; a calendar day is a
  `DateOnly` (wire `"2026-09-06"`); a wall-clock time-of-day is a `TimeOnly` (wire
  `"22:53:00"`). Pick the type by what the value MEANS — the wire cannot tell them apart on
  its own, and that ambiguity is what produced the 2-3h bugs.
- **The wire now states its zone.** `UtcDateTimeJsonConverter` /
  `NullableUtcDateTimeJsonConverter` (`Edvanz.Application/Json/`, registered in `Program.cs`
  AND repeated in `TeacherStudentController.StreamJsonOptions`) serialize every `DateTime` as
  `...Z`. `Unspecified` is STAMPED, never shifted — everything persisted is already UTC.
  Reading is left at the framework default, so what callers may send and what gets persisted
  are unchanged. Two fields opt out per-property with
  `LocalWallClockDateTimeJsonConverter` because they are deliberately the teacher's LOCAL
  wall-clock: `PaymentTransaction.LocalCollectedAt` and the student tracking `PaidOnDate`.
- **Never retype a REQUEST DTO field to `DateOnly`** — deployed clients send full ISO strings
  and `DateOnly` cannot read them, which 400s them. Responses only.
- **A business "today" is the TENANT's**, via `ITimeZoneService.GetTeacherLocalDate` — never
  `DateTime.UtcNow.Date`, which is still YESTERDAY between midnight and 2-3 AM Cairo. A repo
  that needs "today" takes it as a REQUIRED parameter from its calling service
  (`ISessionRepo.BuildSessionListQuery`, `IPaymentRepo.GetActiveSessionsCollectionSummaryAsync`)
  rather than reading a clock, so the compiler enforces the rule.
- **Two deliberate exceptions, do not "fix" them**: `AttendanceAutoAbsentService` uses a coarse
  UTC window on purpose (the per-teacher worker re-gates locally); the subscription module
  compares `EndDate.Date` to `UtcNow.Date` throughout and is internally consistent, with its
  dispatcher at 09:00 Cairo where both dates agree — migrate it whole or not at all.
- **Enforcement.** `scripts/check-datetime-usage.sh` runs as a CI gate before the migration
  gates and fails on `DateTime.Now`, `DateTime.Today`, `.ToLocalTime()` and
  `DateTime.UtcNow.Date` outside the allowlist. On the app side
  `test/core/api_date_time_guard_test.dart` fails on a bare `DateTime.parse`/`tryParse` outside
  `lib/core/utils/api_date_time.dart`; models parse through `parseApiUtcDateTime` (instant) /
  `parseApiCalendarDate` (day) / `parseApiLocalDateTime` (already-local) /
  `parseApiDurationMinutes` (a .NET `TimeSpan` such as `"01:01:00"`, which `int.tryParse`
  cannot read — it silently became 0 on the student exam list).
- **Fixed with this change (do not reintroduce):** the student online-exam list re-read the
  already-Cairo `examDate` + `examTime` pair as UTC, so a 22:53 exam showed at 01:53 the NEXT
  day and its entry gate ran 2-3h late while the take screen (real `startDateTime` instant)
  showed the right time; `duration` (a `TimeSpan`) parsed to 0 so every card read "0 min";
  `prorationSetAt` (an audit instant) was read as a calendar day; scheduled messages built
  `UtcNow.Date + 09:00` and so went out at noon Cairo; a session transfer between midnight and
  3 AM dated its carried-forward period into the PREVIOUS billing month.

## 12. What NOT to Do

| Anti-pattern | Reason |
|---|---|
| Raw LINQ predicates in service layer | Bypasses named-method contract; leaks query logic |
| `GetQueryable()` calls from services | Same violation as above |
| `[ForeignKey]` annotation + Fluent `OnDelete` on same FK | Silently drops `OnDelete` in EF Core 10 |
| `teacherId` from route or body | IDOR vulnerability |
| `SaveChangesAsync` inside internal helper methods | Caller owns commit boundary |
| `IBackgroundJobClient` in Application layer services | Hangfire type belongs in Infrastructure |
| Security stamp bump after `SaveChangesAsync` | Stamp must join the transaction |
| Generic `IJobScheduler` wrappers that re-export Hangfire | Use intent-based interfaces instead |
| Fabricating transactional rows directly in seeders | Referentially inconsistent state |
| Throwing exceptions for business-logic failures | Use `Result<T>.Failure(...)` |
| Hard-deleting `FileObject` rows or blobs inline | Only `file-object-gc` reaps (Detach instead); inline deletes orphan blobs (§5.5) |
| `DateTime.UtcNow.Date` as a business "today" | Still YESTERDAY between midnight and 2-3 AM Cairo; use `ITimeZoneService.GetTeacherLocalDate` (§11b) |
| A calendar day typed `DateTime` on a response DTO | Use `DateOnly` so the wire says it is a day; a `DateTime` now carries a meaningless `Z` (§11b) |
| Retyping a REQUEST DTO field to `DateOnly` | Deployed clients send full ISO strings; `DateOnly` cannot read them and 400s them (§11b) |
| Bare `DateTime.parse`/`tryParse` in a Flutter model | Silently assumes device-local and shifts an instant by 2-3h; use the `api_date_time.dart` helpers (§11b) |
| Re-enabling anonymous blob access / exposing `BlobPath` | Files are JWT-gated via `/api/files/{fileId}`; anonymous blob 401/403/409 is intended (§5.5) |

<!-- ci: markdown-only edits do not trigger the deploy workflow (paths-ignore). -->
