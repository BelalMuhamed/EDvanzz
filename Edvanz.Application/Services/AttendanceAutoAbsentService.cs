using Edvanz.Application.IservicesContract;
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
/// Nightly auto-absent sweep — the writer half of the "unmarked/held ⇒ Absent" feature.
///
/// WHAT IT DOES (per teacher, in the teacher's local Africa/Cairo day):
/// For every active-roster student who was OBLIGATED for a past class occurrence but was never marked,
/// it writes one <see cref="AttendanceStatus.Absent"/> record (flagged <c>IsAutoAbsent</c>), and it
/// rolls any unresolved <see cref="AttendanceStatus.Held"/> record forward to Absent. This is the
/// deliberate reversal of the historical "unmarked = unknown, never inferred" policy — requested so a
/// student the teacher simply skipped counts as absent.
///
/// EQUIVALENCE-SAFE (the whole reason this is a job, not a per-occurrence write):
/// A student assigned to session A may fulfil a class by attending a membership-LINKED session B whose
/// equivalent occurrence (same <c>WeekStartDate</c> + <c>DayPositionIndex</c> slot) can fall on a LATER
/// weekday (Sun≡Mon). So the sweep marks a student absent for a slot ONLY once the ENTIRE slot window —
/// the max occurrence date across the home session AND its linked sessions — has passed. And it skips
/// any student who already has a record on the occurrence OR on any equivalent occurrence in the group
/// (they attended a linked class; their cross-session record landed there). If the sweep runs before a
/// very-late equivalent scan, the take-attendance path still FLIPS the auto-Absent to
/// CrossSessionPresent on that scan (see AttendanceService) — this job never creates a phantom the class
/// can't undo.
///
/// NON-RETROACTIVE: never touches occurrences before <see cref="AutoAbsentOptions.EffectiveFrom"/>, and
/// only within a rolling <see cref="AutoAbsentOptions.LookbackDays"/> catch-up window — so first deploy
/// does not back-write months of history, yet a multi-day outage still fills the gap.
///
/// IDEMPOTENT: re-running is safe — a slot with an existing record (of any kind) for a student is never
/// re-marked. SCOPE NOTE: only students with a CURRENTLY-ACTIVE assignment to the session are swept
/// (matches how occurrence status counts its roster); a student unassigned since the class day is not
/// retro-marked.
///
/// LAYERING: pure business logic, Hangfire-agnostic (mirrors RecurringAssignmentMaterializerService).
/// The Infrastructure dispatcher/worker (AutoAbsentJob) fans out one call per teacher onto its queue.
/// </summary>
public interface IAttendanceAutoAbsentService
{
    /// <summary>
    /// Coarse fan-out selector for the dispatcher: teacher ids that held at least one class within the
    /// padded catch-up window. Each returned teacher gets one <see cref="SweepTeacherAsync"/> worker;
    /// the worker re-gates precisely against that teacher's own local date. Empty when the feature is
    /// disabled or no teacher qualifies.
    /// </summary>
    Task<IReadOnlyList<long>> GetTeacherIdsToSweepAsync();

    /// <summary>
    /// Runs the sweep for ONE teacher. Idempotent — safe to retry. Never throws for "nothing to do".
    /// </summary>
    Task<AutoAbsentOutcome> SweepTeacherAsync(long teacherId);
}

/// <summary>Outcome of one teacher's sweep — surfaced to the Hangfire dashboard and logs.</summary>
public sealed class AutoAbsentOutcome
{
    public long TeacherId { get; init; }
    public int AbsencesWritten { get; init; }
    public int HeldRolledForward { get; init; }
    public int OccurrencesProcessed { get; init; }
    public int SlotsSkippedNotFullyPassed { get; init; }
    public string Reason { get; init; } = "Swept";

    public static AutoAbsentOutcome Skipped(long teacherId, string reason)
        => new() { TeacherId = teacherId, Reason = reason };
}

/// <summary>
/// Default implementation. Scoped lifetime — fresh DbContext per Hangfire execution.
/// </summary>
public class AttendanceAutoAbsentService : IAttendanceAutoAbsentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITimeZoneService _timeZoneService;
    private readonly IAttendanceNotifier _attendanceNotifier;
    private readonly IExamAttendanceSyncService _examAttendanceSync;
    private readonly AutoAbsentOptions _options;
    private readonly ILogger<AttendanceAutoAbsentService> _logger;

    public AttendanceAutoAbsentService(
        IUnitOfWork unitOfWork,
        ITimeZoneService timeZoneService,
        IAttendanceNotifier attendanceNotifier,
        IExamAttendanceSyncService examAttendanceSync,
        IOptions<AutoAbsentOptions> options,
        ILogger<AttendanceAutoAbsentService> logger)
    {
        _unitOfWork = unitOfWork;
        _timeZoneService = timeZoneService;
        _attendanceNotifier = attendanceNotifier;
        _examAttendanceSync = examAttendanceSync;
        _options = options.Value;
        _logger = logger;
    }

    private int LookbackDays => Math.Max(1, _options.LookbackDays);

    /// <summary>Grace days after a class day closes before an absence may be inferred (see options).</summary>
    private int GraceDays => Math.Max(0, _options.GraceDays);

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetTeacherIdsToSweepAsync()
    {
        if (!_options.Enabled)
            return Array.Empty<long>();

        // Coarse UTC window, padded a day either side of the precise per-teacher window so no teacher in
        // any Cairo-adjacent offset is missed. The worker re-gates against the teacher's local date, so
        // over-selecting here only costs a fast no-op worker.
        DateTime utcToday = DateTime.UtcNow.Date;
        DateTime from = MaxDate(_options.EffectiveFrom.Date, utcToday.AddDays(-(LookbackDays + 1)));
        DateTime to = utcToday.AddDays(1); // exclusive

        if (from >= to)
            return Array.Empty<long>();

        return await _unitOfWork.AttendanceRepo
            .GetTeacherIdsWithOccurrencesBetweenAsync(from, to);
    }

    /// <inheritdoc />
    public async Task<AutoAbsentOutcome> SweepTeacherAsync(long teacherId)
    {
        if (!_options.Enabled)
            return AutoAbsentOutcome.Skipped(teacherId, "Disabled");

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
            return AutoAbsentOutcome.Skipped(teacherId, "TeacherNotFound");

        // Precise per-teacher window in the teacher's local calendar. `sweepCutoff` is EXCLUSIVE —
        // only days that have fully closed for this teacher AND cleared the offline-sync grace
        // window are swept (GraceDays; an offline class marked in the evening must reach the server
        // before the sweep is allowed to infer anything about it).
        DateTime localToday = _timeZoneService.GetTeacherLocalDate(teacherId).Date;
        DateTime sweepCutoff = localToday.AddDays(-GraceDays);
        DateTime windowStart = MaxDate(_options.EffectiveFrom.Date, localToday.AddDays(-LookbackDays));
        if (windowStart >= sweepCutoff)
            return AutoAbsentOutcome.Skipped(teacherId, "EmptyWindow");

        // Bounded by the cutoff, not by today: occurrences inside the grace window would be skipped
        // by the per-slot gate below anyway, so not loading them is strictly cheaper.
        var candidateOccurrences = await _unitOfWork.AttendanceRepo
            .GetOccurrencesByTeacherAndDateRangeAsync(teacherId, windowStart, sweepCutoff);
        if (candidateOccurrences.Count == 0)
            return AutoAbsentOutcome.Skipped(teacherId, "NoCandidateOccurrences");

        int absencesWritten = 0, heldRolled = 0, processed = 0, slotsSkipped = 0;

        // Process one home session at a time — each in its own transaction so a failure isolates to that
        // session and Hangfire's retry re-sweeps only the unfinished work (already-written slots skip).
        foreach (var sessionGroup in candidateOccurrences
                     .Where(o => o.Session is not null)
                     .GroupBy(o => o.SessionId))
        {
            long sessionId = sessionGroup.Key;
            var session = sessionGroup.First().Session!;

            var linked = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(sessionId);
            var linkedSessionIds = linked.Select(l => l.Id).ToList();
            var groupSessionIds = linkedSessionIds.Append(sessionId).ToList();

            // Active roster of THIS home session (skip soft-deleted students — their nav is filtered out).
            var assignments = (await _unitOfWork.AttendanceRepo
                    .GetActiveAssignmentsBySessionAsync(sessionId))
                .Where(a => a.TeacherStudentId.HasValue && a.TeacherStudent is not null)
                .ToList();
            if (assignments.Count == 0)
                continue;

            var assignmentByStudent = assignments.ToDictionary(a => a.TeacherStudentId!.Value);

            var (sessionAbsences, sessionHeld, sessionProcessed, sessionSkipped) =
                await SweepSessionAsync(teacherId, session, groupSessionIds,
                    sessionGroup.OrderBy(o => o.OccurrenceDate).ToList(), assignmentByStudent, sweepCutoff);

            absencesWritten += sessionAbsences;
            heldRolled += sessionHeld;
            processed += sessionProcessed;
            slotsSkipped += sessionSkipped;
        }

        _logger.LogInformation(
            "Auto-absent: teacher {TeacherId} swept — {Absences} absences written, {Held} held rolled forward, " +
            "{Processed} occurrences processed, {Skipped} slots deferred (not fully passed).",
            teacherId, absencesWritten, heldRolled, processed, slotsSkipped);

        return new AutoAbsentOutcome
        {
            TeacherId = teacherId,
            AbsencesWritten = absencesWritten,
            HeldRolledForward = heldRolled,
            OccurrencesProcessed = processed,
            SlotsSkippedNotFullyPassed = slotsSkipped,
        };
    }

    /// <summary>
    /// Sweeps one home session's candidate occurrences within a single transaction, then notifies +
    /// reconciles exams post-commit. Returns per-session counters.
    /// </summary>
    private async Task<(int absences, int held, int processed, int skipped)> SweepSessionAsync(
        long teacherId, Session session, List<long> groupSessionIds,
        List<SessionOccurrence> occurrences,
        Dictionary<long, StudentSessionAssignment> assignmentByStudent, DateTime sweepCutoff)
    {
        var newRecords = new List<AttendanceRecord>();
        // Students who became absent in this run (new record OR held→absent), the occurrences that
        // changed, and each student's latest new-absence context for the counter's Last* fields.
        var affectedStudentIds = new HashSet<long>();
        var occurrencesToRefresh = new List<SessionOccurrence>();
        var latestAbsence = new Dictionary<long, (DateTime date, string sessionName, long sessionId)>();
        // Newly-absent students grouped by occurrence, for the post-commit notification.
        var absentByOccurrence = new Dictionary<long, (SessionOccurrence occ, List<long> studentIds)>();

        int absences = 0, held = 0, processed = 0, skipped = 0;

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            foreach (var occ in occurrences)
            {
                // Gate 1 — the WHOLE equivalence slot must have passed (linked sessions may meet later)
                // AND cleared the grace window, so an offline-marked class is never inferred upon
                // before its queued marks have had a realistic chance to reach the server.
                DateTime? groupMaxDate = await _unitOfWork.AttendanceRepo
                    .GetMaxOccurrenceDateForSlotAsync(groupSessionIds, occ.WeekStartDate, occ.DayPositionIndex);
                DateTime effectiveMax = groupMaxDate ?? occ.OccurrenceDate;
                if (effectiveMax >= sweepCutoff)
                {
                    skipped++;
                    continue;
                }

                // Roster obligated for THIS date: assigned on/before the class day (BR-ATT-001).
                var eligible = assignmentByStudent.Values
                    .Where(a => a.AssignedAt.Date <= occ.OccurrenceDate.Date)
                    .Select(a => a.TeacherStudentId!.Value)
                    .ToList();
                if (eligible.Count == 0)
                {
                    processed++;
                    continue;
                }

                var existingOnOcc = (await _unitOfWork.AttendanceRepo
                        .GetRecordsByOccurrenceForStudentsAsync(occ.Id, eligible))
                    .Where(r => r.TeacherStudentId.HasValue)
                    .ToDictionary(r => r.TeacherStudentId!.Value);

                var newlyAbsentHere = new List<long>();

                // Students with NO record on this occurrence — candidates for a fresh Absent, pending the
                // equivalence-group safety net (they may have attended a linked class whose record landed
                // on the attended occurrence in the partial-week fallback).
                var noRecordHere = eligible.Where(id => !existingOnOcc.ContainsKey(id)).ToList();
                var attendedEquivalent = new HashSet<long>();
                if (noRecordHere.Count > 0)
                {
                    var equivalentOccIds = await _unitOfWork.AttendanceRepo
                        .GetEquivalentOccurrenceIdsAsync(session.Id, occ.OccurrenceDate, groupSessionIds);
                    if (equivalentOccIds.Count > 0)
                    {
                        var inGroup = await _unitOfWork.AttendanceRepo
                            .GetExistingAttendanceOnEquivalentOccurrenceBatchAsync(noRecordHere, equivalentOccIds);
                        attendedEquivalent = inGroup.Keys.ToHashSet();
                    }
                }

                foreach (var studentId in eligible)
                {
                    var assignment = assignmentByStudent[studentId];

                    if (existingOnOcc.TryGetValue(studentId, out var record))
                    {
                        // Roll an unresolved Held forward to Absent; leave any other status alone.
                        if (record.Status == AttendanceStatus.Held)
                        {
                            await ConvertHeldToAbsentAsync(record);
                            held++;
                            newlyAbsentHere.Add(studentId);
                            affectedStudentIds.Add(studentId);
                            TrackLatestAbsence(latestAbsence, studentId, occ.OccurrenceDate, session);
                        }
                        continue;
                    }

                    if (attendedEquivalent.Contains(studentId))
                        continue; // attended a linked/equivalent class — not absent

                    // Fresh auto-absent on the student's home occurrence.
                    newRecords.Add(BuildAutoAbsentRecord(teacherId, session, occ, assignment));
                    absences++;
                    newlyAbsentHere.Add(studentId);
                    affectedStudentIds.Add(studentId);
                    TrackLatestAbsence(latestAbsence, studentId, occ.OccurrenceDate, session);
                }

                processed++;
                occurrencesToRefresh.Add(occ);
                if (newlyAbsentHere.Count > 0)
                    absentByOccurrence[occ.Id] = (occ, newlyAbsentHere);
            }

            if (affectedStudentIds.Count == 0)
            {
                // Nothing changed for this session — close the transaction and move on.
                if (ownsTransaction)
                    await _unitOfWork.CommitAsync();
                return (0, 0, processed, skipped);
            }

            if (newRecords.Count > 0)
                await _unitOfWork.AttendanceRepo.AddAttendanceRecordsRangeAsync(newRecords);

            // Flush records + held conversions BEFORE recomputing counters / occurrence status — both
            // read back from the DB (AsNoTracking queries can't see un-flushed changes) — same ATT-9
            // ordering the mark/edit paths use.
            await _unitOfWork.SaveChangesAsync();

            var consecutiveByStudent = new Dictionary<long, int>();
            foreach (var studentId in affectedStudentIds)
            {
                int consecutive = await RecalculateCounterFromRecordsAsync(
                    teacherId, studentId,
                    latestAbsence.TryGetValue(studentId, out var la) ? la : null);
                consecutiveByStudent[studentId] = consecutive;
            }

            foreach (var occ in occurrencesToRefresh)
                await RefreshOccurrenceStatusAsync(occ, assignmentByStudent.Count);

            await _unitOfWork.SaveChangesAsync();
            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // ── Post-commit, best-effort side effects ──
            if (ownsTransaction)
            {
                // Notify absent students per occurrence (same shape as a bulk mark) — user opted in to
                // normal absence notifications for auto-absences.
                foreach (var (_, (occ, studentIds)) in absentByOccurrence)
                {
                    var recipients = studentIds
                        .Select(id => (id, consecutiveByStudent.TryGetValue(id, out var c) ? c : 1))
                        .ToList();
                    await SafeNotifyAsync(teacherId, session.Id, session.SessionName, occ.OccurrenceDate, recipients);
                }

                // During-session exams mirror the class — reconcile each changed occurrence.
                foreach (var occ in occurrencesToRefresh)
                    await SafeReconcileExamsAsync(teacherId, occ.Id);
            }

            return (absences, held, processed, skipped);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    // ══════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════

    private AttendanceRecord BuildAutoAbsentRecord(
        long teacherId, Session session, SessionOccurrence occ, StudentSessionAssignment assignment)
    {
        var now = DateTime.UtcNow;
        return new AttendanceRecord
        {
            TeacherId = teacherId,
            SessionOccurrenceId = occ.Id,
            TeacherStudentId = assignment.TeacherStudentId,
            StudentSessionAssignmentId = assignment.Id,
            StudentName = assignment.TeacherStudent!.StudentName,
            StudentCode = assignment.TeacherStudent!.StudentCode,
            SessionId = session.Id,
            SessionName = session.SessionName,
            SessionGroupId = session.SessionGroupId,
            OccurrenceDate = occ.OccurrenceDate,
            Status = AttendanceStatus.Absent,
            IsAutoAbsent = true,
            // No dedicated "system" method in the enum; MultiSelect matches the add-record path.
            // IsAutoAbsent is the authoritative "system-generated" flag.
            AttendanceMethod = AttendanceMethod.MultiSelect,
            IsCrossSession = false,
            RecordedAt = now,
            RecordedByUserId = null, // system-generated — nullable by design (see entity doc)
            IsEdited = false,
            CreateAt = now,
        };
    }

    private async Task ConvertHeldToAbsentAsync(AttendanceRecord record)
    {
        var now = DateTime.UtcNow;
        await _unitOfWork.AttendanceRepo.AddEditLogAsync(new AttendanceEditLog
        {
            AttendanceRecordId = record.Id,
            PreviousStatus = AttendanceStatus.Held,
            NewStatus = AttendanceStatus.Absent,
            PreviousAttendanceMethod = record.AttendanceMethod,
            NewAttendanceMethod = record.AttendanceMethod,
            EditedAt = now,
            EditedByUserId = null, // system
            EditReason = AttendanceConstants.Messages.AutoAbsentHeldRolledToAbsent,
            CreateAt = now,
        });

        record.Status = AttendanceStatus.Absent;
        record.IsAutoAbsent = true;
        record.IsEdited = true;
        record.LastEditedAt = now;
        record.LastEditedByUserId = null;
        await _unitOfWork.AttendanceRepo.UpdateAttendanceRecordAsync(record);
    }

    /// <summary>
    /// Full recompute of a student's counter totals + streak from their (now-flushed) records — the
    /// single source of truth, identical to the edit/delete path — and advances the Last* absence
    /// context to this run's newest absence when it is newer. Returns the post-recompute consecutive
    /// count for the notifier. Creates the counter row if absent.
    /// </summary>
    private async Task<int> RecalculateCounterFromRecordsAsync(
        long teacherId, long teacherStudentId,
        (DateTime date, string sessionName, long sessionId)? latestAbsence)
    {
        var counter = await _unitOfWork.AttendanceRepo
            .GetAbsenceCounterFreshAsync(teacherId, teacherStudentId);
        if (counter is null)
        {
            counter = new StudentAbsenceCounter
            {
                TeacherId = teacherId,
                TeacherStudentId = teacherStudentId,
                CreateAt = DateTime.UtcNow,
            };
            await _unitOfWork.AttendanceRepo.AddAbsenceCounterAsync(counter);
        }

        var (totalOccurrences, totalPresent, totalAbsences) = await _unitOfWork.AttendanceRepo
            .GetCounterAggregatesAsync(teacherStudentId);
        counter.TotalOccurrences = totalOccurrences;
        counter.TotalPresent = totalPresent;
        counter.TotalAbsences = totalAbsences;
        counter.ConsecutiveAbsences = await _unitOfWork.AttendanceRepo
            .RecalculateConsecutiveAbsencesAsync(teacherStudentId);

        if (latestAbsence is { } la
            && (!counter.LastAbsenceDate.HasValue || la.date >= counter.LastAbsenceDate.Value))
        {
            counter.LastAbsenceDate = la.date;
            counter.LastAbsenceSessionName = la.sessionName;
            counter.LastAbsenceSessionId = la.sessionId;
        }

        await _unitOfWork.AttendanceRepo.UpdateAbsenceCounterAsync(counter);
        return counter.ConsecutiveAbsences;
    }
    /// <summary>
    /// C1 perf fix: <paramref name="totalStudents"/> is passed in instead of re-querying
    /// GetActiveAssignmentsBySessionAsync — the caller (SweepSessionAsync) already holds the
    /// session's active roster in assignmentByStudent, loaded once per session, not once per occurrence.
    /// </summary>
    private async Task RefreshOccurrenceStatusAsync(SessionOccurrence occurrence, int totalStudents)
    {
        var records = await _unitOfWork.AttendanceRepo.GetRecordsByOccurrenceAsync(occurrence.Id);

        int markedCount = records.Count(r =>
            r.Status != AttendanceStatus.Held && r.SessionId == occurrence.SessionId);

        if (markedCount == 0)
            occurrence.Status = OccurrenceStatus.Pending;
        else if (markedCount >= totalStudents && totalStudents > 0)
            occurrence.Status = OccurrenceStatus.Completed;
        else
            occurrence.Status = OccurrenceStatus.InProgress;

        await _unitOfWork.AttendanceRepo.UpdateOccurrenceAsync(occurrence);
    }

    private static void TrackLatestAbsence(
        Dictionary<long, (DateTime date, string sessionName, long sessionId)> map,
        long studentId, DateTime date, Session session)
    {
        if (!map.TryGetValue(studentId, out var current) || date >= current.date)
            map[studentId] = (date.Date, session.SessionName, session.Id);
    }

    private async Task SafeNotifyAsync(
        long teacherId, long sessionId, string sessionName, DateTime occurrenceDate,
        IReadOnlyCollection<(long teacherStudentId, int consecutiveAbsences)> recipients)
    {
        if (recipients.Count == 0) return;
        try
        {
            await _attendanceNotifier.OnStudentsAbsentAsync(
                teacherId, sessionId, sessionName, occurrenceDate, recipients);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Auto-absent: absence notification failed for teacher {TeacherId} session {SessionId} on {Date} " +
                "(non-fatal — records already committed).", teacherId, sessionId, occurrenceDate.ToString("yyyy-MM-dd"));
        }
    }

    private async Task SafeReconcileExamsAsync(long teacherId, long occurrenceId)
    {
        try
        {
            await _examAttendanceSync.ReconcileExamsForSessionOccurrenceAsync(
                teacherId, occurrenceId, teacherId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Auto-absent: exam reconcile failed for teacher {TeacherId} occurrence {OccurrenceId} " +
                "(non-fatal).", teacherId, occurrenceId);
        }
    }

    private static DateTime MaxDate(DateTime a, DateTime b) => a >= b ? a : b;
}
