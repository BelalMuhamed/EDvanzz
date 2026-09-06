# Timezone & Date-Time Standard (Backend — Edvanz.API)

One rule set for every point-in-time and calendar value in the .NET solution. Follow it
without exception; it exists because subtle UTC/local mistakes shipped real bugs (online-exam
times shown 2–3h early, "X ago" off by the Egypt offset).

Default timezone for the product is **`Africa/Cairo`** (UTC+2 winter / UTC+3 summer).

---

## 1. Persistence — always UTC, always `DateTime.UtcNow`

- Every stored moment-in-time is UTC. Write `DateTime.UtcNow` (never `DateTime.Now`).
  Examples: `CreateAt`, `CreatedAt`, `UpdatedAt`, `SubmittedAt`, `CollectedAt`,
  `RequestedAt`, `ResolvedAt`, `DepartedAt`, `RefundedAt`, `EditedAt`, `LastCollectionAt`,
  `StartDateTime`/`EndDateTime`, `PublishDate`.
- A **calendar-only** value (a day with no meaningful time-of-day) is a `DateOnly`, or a
  `DateTime` whose date component is the payload and whose time is an ignored midnight.
  Examples: `PeriodStart` (billing month), exam `ExamDate`/`DueDate`, `AssignmentDate`,
  attendance occurrence date, `SessionOccurrence.OccurrenceDate`, subscription `StartDate`/
  `EndDate`, `JoinedAt` (first-attendance/assignment day), `OverdueOn`. Never stamp these
  with `UtcNow`'s time-of-day.
- **`LocalCollectedAt` is the deliberate exception**: it is stored as the teacher's LOCAL
  wall-clock (Kind `Unspecified`) so a receipt shows the time the tutor actually collected
  cash. It must never be treated as UTC. Its sibling `CollectedAt` is the UTC instant.

## 2. Any UTC↔local conversion goes through `ITimeZoneService`

`ITimeZoneService` (impl `Edvanz.Infrastructure/Services/TimeZoneService.cs`) is the single
place that knows the timezone and handles the DST gap:

- `GetTeacherLocalNow(teacherId)` / `GetTeacherLocalDate(teacherId)` — "now"/"today" in the
  teacher's zone. Use for defaulting the selected month, "today's sessions", auto-absent, etc.
- `ConvertUtcToLocal(utc, "Africa/Cairo")` — render a stored UTC instant in local time, or
  bucket it to a local day. **Use this before truncating a UTC instant to a `DateOnly`/
  `TimeOnly`/day.**
- `ConvertLocalToUtc(local, "Africa/Cairo")` — turn a caller-supplied local filter bound into
  UTC before querying UTC columns.

**Never** truncate a UTC instant to a local date/time without converting first:

```csharp
// WRONG — truncates the UTC instant; shows 2–3h early in Cairo
ExamDate = DateOnly.FromDateTime(exam.StartDateTime),
ExamTime = TimeOnly.FromDateTime(exam.StartDateTime),

// CORRECT — convert UTC → Cairo first
var localStart = _timeZoneService.ConvertUtcToLocal(exam.StartDateTime, "Africa/Cairo");
ExamDate = DateOnly.FromDateTime(localStart),
ExamTime = TimeOnly.FromDateTime(localStart),
```

Server-generated static artifacts (PDF/Excel exports, "generated at" stamps) have no client to
localize them, so they must convert through `ITimeZoneService` at build time (see
`AuditTrialService`), or label the value `UTC` explicitly.

## 3. The wire states its own zone (changed 2026-09-06)

Every `DateTime` a controller returns is serialized as an explicit UTC instant —
`2026-08-31T13:15:45.1405802Z` — by `UtcDateTimeJsonConverter` /
`NullableUtcDateTimeJsonConverter` (`Edvanz.Application/Json/`), registered in `Program.cs`
and repeated in `TeacherStudentController.StreamJsonOptions` (the NDJSON bulk-import stream
builds its own options; keep the two in sync).

**Why it changed.** EF materializes SQL Server `datetime2` as `Kind=Unspecified`, which
serializes with NO suffix, while the same logical field computed in memory (`DateTime.UtcNow`
echoed back in a create response) is `Kind=Utc` and DOES carry a `Z`. One field travelled in
two shapes depending on whether it had been round-tripped through the database — and a
suffix-less instant is read as LOCAL by every standards-compliant client parser, landing 2–3h
off for Egypt. The payload now says what it means instead of each client keeping a private
list of which fields are UTC.

**Unspecified is stamped, never shifted.** Everything persisted is already UTC, so the
converter applies `SpecifyKind(Utc)`. Converting would subtract the server offset a second
time.

**Reading is deliberately unchanged** (`reader.GetDateTime()`, byte-identical to the framework
default), so nothing about what callers may send, or about what gets persisted, moves.

**The two opt-outs.** A value the backend deliberately expresses in the teacher's LOCAL
wall-clock — `PaymentTransaction.LocalCollectedAt` and the student tracking screen's
`PaidOnDate` — carries `[JsonConverter(typeof(LocalWallClockDateTimeJsonConverter))]` (or the
`Nullable…` variant) and is written with no suffix. A property-level attribute wins over the
globally registered converters. Stamping those with a `Z` would be a lie.

**A calendar day belongs in a `DateOnly`** (wire `"2026-09-06"`) and a wall-clock time in a
`TimeOnly` (wire `"22:53:00"`); the converter never sees either. Some legacy calendar-day
fields are still typed `DateTime` and therefore now carry a meaningless `Z` — harmless for
Egypt, where the local offset is always ahead of UTC so a UTC midnight can only move the clock
*forward* within the same day, but retype them to `DateOnly` when you touch them.

**Requests are a separate contract.** Do NOT retype a REQUEST DTO field to `DateOnly` —
deployed clients send full ISO strings and `DateOnly` cannot read them, which would 400 them.

## 4. Checklist when adding a field

1. Is it a moment-in-time or a calendar day? Moment → `DateTime` in UTC (`UtcNow`), which the
   converter stamps with `Z`; day → `DateOnly`; wall-clock time-of-day → `TimeOnly`.
2. Displaying or day-bucketing a UTC instant? Convert via `ITimeZoneService` first.
3. Never `DateTime.Now` / `DateTime.Today` / `.ToLocalTime()` in business code.
4. Deriving "today"/"this month"? Use `GetTeacherLocalDate` / `GetTeacherLocalNow` — never
   `DateTime.UtcNow.Date`, which is still YESTERDAY between midnight and 2–3 AM Cairo. A repo
   that needs "today" takes it as a required parameter from its calling service rather than
   reading a clock (`ISessionRepo.BuildSessionListQuery`,
   `IPaymentRepo.GetActiveSessionsCollectionSummaryAsync`).
5. Is it a duration? `TimeSpan` serializes as `"01:01:00"`, which `int.tryParse` cannot read on
   the client — prefer an explicit `int …Minutes`, or make sure the client uses a TimeSpan-aware
   parser (Flutter: `parseApiDurationMinutes`).

### Deliberate exceptions to rule 4 — do not "fix" these

- `AttendanceAutoAbsentService` uses a coarse **UTC** window padded a day either side on
  purpose; the per-teacher worker re-gates on the teacher's local date, so over-selecting only
  costs a fast no-op.
- The subscription module compares `EndDate.Date` with `UtcNow.Date` throughout
  (`SubscriptionStatusCalculator.DeriveDaysRemaining`, `SubscriptionReminderService`). It is
  internally consistent and its dispatcher runs at 09:00 Africa/Cairo, where the UTC and Cairo
  dates always agree. Changing one site alone would make `daysRemaining` and the reminder
  disagree by a day — migrate the whole module together or not at all.
