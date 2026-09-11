using Edvanz.Application.Options;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Edvanz.Application.Services;

/// <summary>
/// Builds the admin usage model. See <see cref="ITeacherUsageRollupService"/> for the contract.
///
/// THE SHAPE OF THE WORK, per teacher:
///   1. Ask the repo for per-module activity counts bucketed to the UTC hour.
///   2. Fold each hour into the TEACHER'S LOCAL calendar day (CLAUDE.md §11b) and attribute it to the
///      teacher, an assistant, or nobody.
///   3. Delete-and-rewrite that window of day rows — the idempotency guarantee.
///   4. Read the stored days back and derive the three bands plus the setup-health figures.
///
/// Step 2 is the whole reason this lives in the Application layer: <c>ITimeZoneService</c> handles
/// DST (Egypt is UTC+2, UTC+3 in summer) and will handle per-teacher timezones when they arrive.
/// A hardcoded offset here would silently misfile every evening's work, which is precisely the bug
/// class the timezone standard exists to prevent.
/// </summary>
public class TeacherUsageRollupService : ITeacherUsageRollupService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITimeZoneService _timeZone;
    private readonly AdminInsightsOptions _options;
    private readonly ILogger<TeacherUsageRollupService> _logger;

    public TeacherUsageRollupService(
        IUnitOfWork unitOfWork,
        ITimeZoneService timeZone,
        IOptions<AdminInsightsOptions> options,
        ILogger<TeacherUsageRollupService> logger)
    {
        _unitOfWork = unitOfWork;
        _timeZone = timeZone;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(long TeacherId, int Days)>> GetRollupPlanAsync(CancellationToken ct = default)
    {
        // THE KILL SWITCH. Checked here, in the selector, so a disabled rollup enqueues NOTHING
        // rather than enqueuing workers that each no-op — the same shape as the auto-absent sweep.
        // This is the one lever that stops the heaviest job on the platform without a redeploy.
        if (!_options.Enabled)
        {
            _logger.LogInformation("Usage rollup is disabled (AdminInsights__Enabled=false); nothing enqueued.");
            return Array.Empty<(long, int)>();
        }

        var repo = _unitOfWork.TeacherUsageRepo;
        var allIds = await repo.GetTeacherIdsForRollupAsync(ct);
        var needBackfill = await repo.GetTeacherIdsWithoutSnapshotAsync(ct);

        int backfillDays = Math.Clamp(_options.BackfillDays, 1, 730);
        int tailDays = Math.Clamp(_options.RecomputeTailDays, 1, 90);

        // A teacher with no snapshot has never been rolled up — give them the full history window.
        // Everyone else only needs the short tail, because anything older is already stored and only
        // changes when a late offline sync lands (which the tail covers).
        return allIds
            .Select(id => (
                TeacherId: id,
                Days: needBackfill.Contains(id) ? backfillDays : tailDays))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<UsageRollupOutcome> RollupTeacherAsync(
        long teacherId, int days, CancellationToken ct = default)
    {
        var repo = _unitOfWork.TeacherUsageRepo;

        var identities = await repo.GetActorIdentitiesAsync(teacherId, ct);
        if (identities is null)
        {
            // Teacher purged between the dispatcher listing them and this worker running. Not an
            // error — just nothing to do.
            _logger.LogDebug("Usage rollup: teacher {TeacherId} no longer exists; skipped.", teacherId);
            return new UsageRollupOutcome(teacherId, 0, 0, Skipped: true);
        }

        // ── Window, in the teacher's local calendar ─────────────────────────────
        // Today is included: the job runs after midnight, so "today" is a fresh empty day, but an
        // on-demand refresh from the admin UI at midday must be able to see this morning's work.
        DateOnly localToday = DateOnly.FromDateTime(_timeZone.GetTeacherLocalDate(teacherId));
        DateOnly fromDate = localToday.AddDays(-(days - 1));

        // Query in UTC with a generous margin on both ends: a local day starts up to 3 hours before
        // the same UTC date. The margin only ever pulls in extra hours, which are then discarded by
        // the local-day filter below — never the other way round.
        DateTime fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-1);
        DateTime toUtc = localToday.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1);

        // ── Gather every source ────────────────────────────────────────────────
        var attendance = await repo.GetAttendanceBucketsAsync(teacherId, fromUtc, toUtc, ct);
        var payments = await repo.GetPaymentBucketsAsync(teacherId, fromUtc, toUtc, ct);
        var videos = await repo.GetVideoBucketsAsync(teacherId, fromUtc, toUtc, ct);
        var onlineExams = await repo.GetOnlineExamBucketsAsync(teacherId, fromUtc, toUtc, ct);
        var examHomework = await repo.GetExamHomeworkBucketsAsync(teacherId, fromUtc, toUtc, ct);
        var parentPortal = await repo.GetParentPortalBucketsAsync(teacherId, fromUtc, toUtc, ct);
        var students = await repo.GetStudentBucketsAsync(teacherId, fromUtc, toUtc, ct);
        var sessions = await repo.GetSessionBucketsAsync(teacherId, fromUtc, toUtc, ct);
        var messaging = await repo.GetMessagingBucketsAsync(teacherId, fromUtc, toUtc, ct);

        // ── Fold into local days ───────────────────────────────────────────────
        var byDay = new Dictionary<DateOnly, DayAccumulator>();

        DayAccumulator DayFor(DateTime utcHour)
        {
            // ONE conversion point for the whole rollup. Everything downstream reads local days.
            DateOnly local = DateOnly.FromDateTime(_timeZone.ConvertUtcToLocal(utcHour));
            if (!byDay.TryGetValue(local, out var acc))
                byDay[local] = acc = new DayAccumulator();
            return acc;
        }

        void AddAttributed(
            IReadOnlyList<UsageActorHourBucket> buckets,
            UsageModules module,
            Action<DayAccumulator, int> apply)
        {
            foreach (var b in buckets)
            {
                var acc = DayFor(b.UtcHour);
                apply(acc, b.Count);
                acc.ModulesMask |= (int)module;
                acc.TotalWrites += b.Count;

                // Attribution is three-way ON PURPOSE. An unknown actor is never assumed to be the
                // teacher — that guess would erase AssistantsOnly, the model's most valuable output.
                if (b.ActorUserId is null)
                    acc.UnattributedWrites += b.Count;
                else if (b.ActorUserId == identities.TeacherUserId)
                    acc.TeacherWrites += b.Count;
                else if (identities.AssistantUserIds.Contains(b.ActorUserId.Value))
                    acc.AssistantWrites += b.Count;
                else
                    // An actor who is neither the teacher nor one of their assistants — a SuperAdmin
                    // acting in support, most likely. Real work, but not the tenant's own.
                    acc.UnattributedWrites += b.Count;
            }
        }

        void AddUnattributed(
            IReadOnlyList<UsageHourBucket> buckets,
            UsageModules module,
            Action<DayAccumulator, int> apply)
        {
            foreach (var b in buckets)
            {
                var acc = DayFor(b.UtcHour);
                apply(acc, b.Count);
                acc.ModulesMask |= (int)module;
                acc.TotalWrites += b.Count;
                acc.UnattributedWrites += b.Count;
            }
        }

        AddAttributed(attendance, UsageModules.Attendance, (a, n) => a.AttendanceWrites += n);
        AddAttributed(payments, UsageModules.Payments, (a, n) => a.PaymentWrites += n);
        AddAttributed(videos, UsageModules.Videos, (a, n) => a.VideoWrites += n);
        AddAttributed(onlineExams, UsageModules.OnlineExams, (a, n) => a.OnlineExamWrites += n);
        AddAttributed(examHomework, UsageModules.ExamsHomework, (a, n) => a.ExamHomeworkWrites += n);
        AddAttributed(parentPortal, UsageModules.ParentPortal, (a, n) => a.ParentPortalWrites += n);
        AddUnattributed(students, UsageModules.Students, (a, n) => a.StudentWrites += n);
        AddUnattributed(sessions, UsageModules.Sessions, (a, n) => a.SessionWrites += n);
        AddUnattributed(messaging, UsageModules.Messaging, (a, n) => a.MessagingWrites += n);

        // Drop days outside the requested window. The UTC margin above deliberately over-fetches;
        // this is where the excess is discarded.
        var rows = byDay
            .Where(kv => kv.Key >= fromDate && kv.Key <= localToday && kv.Value.TotalWrites > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value.ToEntity(teacherId, kv.Key))
            .ToList();

        await repo.ReplaceDaysAsync(teacherId, fromDate, localToday, rows, ct);

        // ── Derive the snapshot from what is now stored ─────────────────────────
        await RebuildSnapshotAsync(teacherId, localToday, identities, ct);

        return new UsageRollupOutcome(
            teacherId, rows.Count, rows.Sum(r => r.TotalWrites), Skipped: false);
    }

    /// <summary>
    /// Recomputes the teacher's single snapshot row from the STORED day rows plus a handful of live
    /// counts. Reading back from storage rather than reusing the in-memory window is deliberate: the
    /// 30- and 90-day figures must span days this run never touched.
    /// </summary>
    private async Task RebuildSnapshotAsync(
        long teacherId, DateOnly localToday, TeacherActorIdentities identities, CancellationToken ct)
    {
        var repo = _unitOfWork.TeacherUsageRepo;

        var days90 = await repo.GetDayTotalsAsync(teacherId, localToday.AddDays(-89), localToday, ct);
        var setup = await repo.GetSetupHealthAsync(teacherId, ct);
        var allTime = await repo.GetAllTimeFactsAsync(teacherId, ct);

        DateOnly since7 = localToday.AddDays(-6);
        DateOnly since30 = localToday.AddDays(-29);

        var days7 = days90.Where(d => d.ActivityDate >= since7).ToList();
        var days30 = days90.Where(d => d.ActivityDate >= since30).ToList();

        int modulesMask30 = days30.Aggregate(0, (mask, d) => mask | d.ModulesMask);

        // All-time values come from allTime (the FULL stored history), never from the 90-day
        // read-back — a teacher last active 100 days ago must still carry a LastActivityAt, or they
        // silently drop off the "went quiet" card, which is exactly the list they belong on.
        int modulesMaskAllTime = allTime.ModulesAllTimeMask;
        DateTime? lastActivity = allTime.LastActivityAt;

        var snapshot = await repo.GetSnapshotAsync(teacherId, ct) ?? new TeacherUsageSnapshot
        {
            TeacherId = teacherId,
            CreateAt = DateTime.UtcNow
        };

        snapshot.ActiveDays7 = days7.Count;
        snapshot.ActiveDays30 = days30.Count;
        snapshot.ActiveDays90 = days90.Count;
        snapshot.TotalWrites30 = days30.Sum(d => d.TotalWrites);
        snapshot.Cadence = ResolveCadence(days30.Count, hasHistory: days90.Count > 0 || allTime.FirstActivityAt is not null);

        snapshot.ModulesUsedMask = modulesMask30;
        // All-time is the union of what is stored and what the snapshot already knew, so history
        // beyond the 90-day read-back is never forgotten once it has been observed.
        snapshot.ModulesUsedAllTimeMask |= modulesMaskAllTime;
        snapshot.Depth = ResolveDepth(CountModules(modulesMask30));

        snapshot.Operators = ResolveOperators(
            days30.Sum(d => d.TeacherWrites),
            days30.Sum(d => d.AssistantWrites),
            identities);
        snapshot.LastTeacherActivityAt = identities.LastTeacherSeenAt;
        snapshot.LastAssistantActivityAt = identities.LastAssistantSeenAt;
        snapshot.ActiveAssistantCount = setup.ActiveAssistantCount;

        // FirstActivityAt only ever moves EARLIER. A later backfill can uncover older history; a
        // shorter window must never make an account look younger than it is.
        if (allTime.FirstActivityAt is not null &&
            (snapshot.FirstActivityAt is null || allTime.FirstActivityAt < snapshot.FirstActivityAt))
            snapshot.FirstActivityAt = allTime.FirstActivityAt;

        if (lastActivity is not null &&
            (snapshot.LastActivityAt is null || lastActivity > snapshot.LastActivityAt))
            snapshot.LastActivityAt = lastActivity;

        snapshot.StudentCount = setup.StudentCount;
        snapshot.StudentsAssignedToSession = setup.StudentsAssignedToSession;
        snapshot.SessionCount = setup.SessionCount;
        snapshot.SessionsWithOccurrences = setup.SessionsWithOccurrences;
        snapshot.LinkedAccountCount = setup.LinkedAccountCount;
        snapshot.BoundAccountCount = setup.BoundAccountCount;
        snapshot.HasEverMarkedAttendance = allTime.HasEverMarkedAttendance;
        snapshot.HasEverCollectedPayment = allTime.HasEverCollectedPayment;

        // THE HONEST "has data" TEST. Not "has rows" — a roster nobody assigned to a session, or a
        // session that never generated an occurrence, is an account that shows its students nothing.
        snapshot.HasRealData =
            setup.StudentsAssignedToSession > 0 && setup.SessionsWithOccurrences > 0;

        snapshot.ComputedAt = DateTime.UtcNow;

        await repo.UpsertSnapshotAsync(snapshot, ct);
    }

    // ════════════════════════════════════════════════════════════════════════
    // BAND RESOLUTION — thresholds all live in AdminInsightsConstants
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Maps active-day count to a cadence band.</summary>
    private static UsageCadence ResolveCadence(int activeDays30, bool hasHistory) => activeDays30 switch
    {
        >= AdminInsightsConstants.CadenceDailyMinDays => UsageCadence.Daily,
        >= AdminInsightsConstants.CadenceMostDaysMinDays => UsageCadence.MostDays,
        >= AdminInsightsConstants.CadenceWeeklyMinDays => UsageCadence.Weekly,
        >= AdminInsightsConstants.CadenceRarelyMinDays => UsageCadence.Rarely,
        // Zero active days splits two ways, and the distinction is the whole point: someone who used
        // to work the account and stopped needs a phone call; someone who never started needs
        // onboarding.
        _ => hasHistory ? UsageCadence.Dormant : UsageCadence.Never
    };

    /// <summary>Maps distinct-module count to a depth band.</summary>
    private static UsageDepth ResolveDepth(int moduleCount) => moduleCount switch
    {
        >= AdminInsightsConstants.DepthFullMinModules => UsageDepth.Full,
        >= AdminInsightsConstants.DepthBroadMinModules => UsageDepth.Broad,
        >= AdminInsightsConstants.DepthCoreMinModules => UsageDepth.Core,
        1 => UsageDepth.Single,
        _ => UsageDepth.None
    };

    /// <summary>
    /// Decides who is working the account, preferring ATTRIBUTED WRITES and falling back to
    /// last-seen presence.
    ///
    /// The fallback matters: three of the nine modules carry no actor column, so a teacher whose only
    /// activity is creating sessions would otherwise register as "nobody". Presence answers that
    /// honestly without pretending to know which module the person touched.
    /// </summary>
    private static OperatorMix ResolveOperators(
        int teacherWrites, int assistantWrites, TeacherActorIdentities identities)
    {
        bool teacherActive = teacherWrites > 0;
        bool assistantActive = assistantWrites > 0;

        if (!teacherActive && !assistantActive)
        {
            // No attributed writes at all. Fall back to whoever was actually seen, comparing the two
            // sides against each other rather than against a clock.
            teacherActive = identities.LastTeacherSeenAt is not null;
            assistantActive = identities.LastAssistantSeenAt is not null;
        }

        return (teacherActive, assistantActive) switch
        {
            (true, true) => OperatorMix.TeacherAndAssistants,
            (true, false) => OperatorMix.TeacherOnly,
            (false, true) => OperatorMix.AssistantsOnly,
            _ => OperatorMix.Nobody
        };
    }

    /// <summary>Popcount over a <see cref="UsageModules"/> mask.</summary>
    private static int CountModules(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);

    /// <summary>
    /// Mutable per-day tally used while folding hour buckets. Exists only inside one rollup call;
    /// <see cref="ToEntity"/> turns it into the row that gets stored.
    /// </summary>
    private sealed class DayAccumulator
    {
        public int StudentWrites;
        public int SessionWrites;
        public int AttendanceWrites;
        public int PaymentWrites;
        public int VideoWrites;
        public int OnlineExamWrites;
        public int ExamHomeworkWrites;
        public int MessagingWrites;
        public int ParentPortalWrites;
        public int ModulesMask;
        public int TotalWrites;
        public int TeacherWrites;
        public int AssistantWrites;
        public int UnattributedWrites;

        public TeacherUsageDay ToEntity(long teacherId, DateOnly date) => new()
        {
            TeacherId = teacherId,
            ActivityDate = date,
            StudentWrites = StudentWrites,
            SessionWrites = SessionWrites,
            AttendanceWrites = AttendanceWrites,
            PaymentWrites = PaymentWrites,
            VideoWrites = VideoWrites,
            OnlineExamWrites = OnlineExamWrites,
            ExamHomeworkWrites = ExamHomeworkWrites,
            MessagingWrites = MessagingWrites,
            ParentPortalWrites = ParentPortalWrites,
            ModulesMask = ModulesMask,
            TotalWrites = TotalWrites,
            TeacherWrites = TeacherWrites,
            AssistantWrites = AssistantWrites,
            UnattributedWrites = UnattributedWrites,
            CreateAt = DateTime.UtcNow
        };
    }
}
