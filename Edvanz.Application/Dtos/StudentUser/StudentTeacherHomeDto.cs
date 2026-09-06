namespace Edvanz.Application.Dtos.StudentUser;

/// <summary>
/// Aggregated "teacher home" screen for a student who has selected one linked
/// teacher (Figma node 232:7033). Composes the per-module student surfaces
/// (attendance, payment, videos, online/offline exams, homework) into ONE
/// response so the frontend renders the whole page from a single call.
///
/// Every module section carries its own <c>Visible</c> flag sourced from the
/// teacher's <see cref="Edvanz.Domain.Entities.TeacherConfiguration"/> visibility
/// toggles — the frontend shows a section only when it is visible, and a hidden
/// section returns its flag with empty/null data (no downstream call is made).
///
/// The whole endpoint is gated the same way as every other student read: the
/// caller must be ACTIVELY linked to, and bound under, this teacher (otherwise
/// 403), so <see cref="LinkStatus"/> is always <c>Active</c> when this DTO is
/// returned.
/// </summary>
public class StudentTeacherHomeDto
{
    /// <summary>The selected teacher's numeric id (route <c>{teacherId}</c>).</summary>
    public long TeacherId { get; set; }

    /// <summary>Teacher's full name (screen header — "Mr. Ahmed El-hady").</summary>
    public string TeacherName { get; set; } = null!;

    /// <summary>Subject taught, language-aware (student's language preference).</summary>
    public string SubjectName { get; set; } = null!;

    /// <summary>Teacher's 8-digit shareable code.</summary>
    public string TeacherCode { get; set; } = null!;

    /// <summary>Link lifecycle state — always <c>Active</c> for a returned home page.</summary>
    public string LinkStatus { get; set; } = null!;

    /// <summary>Whether the connection is bound to a roster record (Active + bound).</summary>
    public bool IsLinked { get; set; }

    /// <summary>
    /// The session (class group) this student is currently assigned to under this teacher,
    /// or <c>null</c> when the teacher has not assigned them to one yet
    /// (<c>TeacherStudent.SessionId</c> is null). REQ-STU-004 / BR-SES-002 — assignment is
    /// optional, and a student sits in at most one session at a time.
    ///
    /// WHY THE STUDENT NEEDS THIS: videos and online exams are BOTH scoped by session only
    /// (<c>VideoScope</c> and <c>OnlineExamScope</c> target Session or SessionGroup — there
    /// is no per-student scope), so an unassigned student's video and exam lists come back
    /// empty with a perfectly successful 200. Without this field the app cannot tell
    /// "your teacher published nothing yet" apart from "you are not in a session yet", and
    /// the student is left staring at an empty screen with no way to act. The client shows
    /// an explanatory note pointing them at their teacher when this is null.
    ///
    /// Distinct from <see cref="IsLinked"/>: linking binds the ACCOUNT to the teacher's
    /// student record; assignment places that record in a session. A student can be linked
    /// and still unassigned — that is exactly the case this field exposes.
    /// </summary>
    public long? SessionId { get; set; }

    /// <summary>
    /// Display name of <see cref="SessionId"/>'s session; null whenever that is null.
    /// </summary>
    public string? SessionName { get; set; }

    /// <summary>
    /// Whether the student may view their scannable QR/barcode inside the app. Mirrors the
    /// teacher's <see cref="Edvanz.Domain.Entities.TeacherConfiguration.BarcodeDisplayMode"/>:
    /// <c>true</c> for <c>InApp</c> (soft QR shown in the student app), <c>false</c> for
    /// <c>HardCopyOnly</c> (physical printed card only). The frontend hides the "Show QR" entry
    /// when this is false; the barcode endpoint itself still enforces it server-side (403
    /// <c>BarcodeNotAvailableInApp</c>) so a stale client can never fetch a suppressed code.
    /// </summary>
    public bool IsBarcodeInAppEnabled { get; set; } = true;

    /// <summary>The month the page is scoped to, ISO <c>yyyy-MM</c> (teacher-local Africa/Cairo).</summary>
    public string Month { get; set; } = null!;

    /// <summary>Human month label for the scoping month (e.g. "March").</summary>
    public string MonthLabel { get; set; } = null!;

    /// <summary>Attendance Tracking card. <see cref="HomeAttendanceDto.Visible"/> false when hidden.</summary>
    public HomeAttendanceDto Attendance { get; set; } = new();

    /// <summary>Payment Tracking card. <see cref="HomePaymentDto.Visible"/> false when hidden.</summary>
    public HomePaymentDto Payment { get; set; } = new();

    /// <summary>Videos quick-tile (count only). Gated by the StudentVisibilityVideo toggle.</summary>
    public HomeVideosDto Videos { get; set; } = new();

    /// <summary>Upcoming-Homework card + Homework quick-tile. Keys present; data wired later.</summary>
    public HomeHomeworkDto Homework { get; set; } = new();

    /// <summary>
    /// Exams: the merged Upcoming-Exam feed (online + offline, discriminated by
    /// <see cref="HomeExamItemDto.ExamType"/>), the Exams quick-tile count, and the
    /// Offline-Exam month strip. Online and offline are gated independently.
    /// </summary>
    public HomeExamsDto Exams { get; set; } = new();
}

/// <summary>Attendance Tracking card — current (or requested) month rollup.</summary>
public class HomeAttendanceDto
{
    /// <summary>Whether the teacher exposes the Attendance Track to students.</summary>
    public bool Visible { get; set; }

    /// <summary>Month label the figures belong to (e.g. "March"); null when hidden.</summary>
    public string? MonthLabel { get; set; }

    /// <summary>Present ÷ (Present + Absent) × 100, 1 dp. Held/unmarked days excluded.</summary>
    public decimal AttendancePercentage { get; set; }

    /// <summary>Days present this month (Present + CrossSessionPresent).</summary>
    public int PresentDays { get; set; }

    /// <summary>Days absent this month.</summary>
    public int AbsentDays { get; set; }

    /// <summary>Scheduled class days in the enrollment window this month (incl. upcoming/unmarked).</summary>
    public int TotalOccurrences { get; set; }

    /// <summary>Of <see cref="TotalOccurrences"/>, how many carry a marked record.</summary>
    public int MarkedOccurrences { get; set; }

    /// <summary>Header session name for the month; null when none.</summary>
    public string? SessionName { get; set; }
}

/// <summary>Payment Tracking card — current-month status + arrears.</summary>
public class HomePaymentDto
{
    /// <summary>Whether the teacher exposes the Payment Track to students.</summary>
    public bool Visible { get; set; }

    /// <summary>Current teacher-local month label (e.g. "March"); null when hidden.</summary>
    public string? MonthLabel { get; set; }

    /// <summary>Current month's period status (PaymentStatus) or "NoObligation" when none generated.</summary>
    public string? CurrentMonthStatus { get; set; }

    /// <summary>Current month's billed amount.</summary>
    public decimal CurrentMonthAmountDue { get; set; }

    /// <summary>Current month's paid amount (the "Paid (March) 500 LE" headline).</summary>
    public decimal CurrentMonthAmountPaid { get; set; }

    /// <summary>Total owed through the current month (sum of overdue outstanding); the "amount owed now".</summary>
    public decimal OverdueAmount { get; set; }

    /// <summary>Month label of the EARLIEST overdue period (e.g. "Feb"); null when nothing overdue.</summary>
    public string? OverdueFromMonthLabel { get; set; }

    /// <summary>Paid ÷ (Paid + Overdue), 0..1 — drives the ring fill; 1 when nothing owed.</summary>
    public decimal PaidProgressRatio { get; set; }

    /// <summary>Currency code for the amounts.</summary>
    public string Currency { get; set; } = "EGP";
}

/// <summary>Videos quick-tile — a count only (the full list lives at the videos endpoint).</summary>
public class HomeVideosDto
{
    /// <summary>Whether the teacher exposes the Videos module (StudentVisibilityVideo).</summary>
    public bool Visible { get; set; }

    /// <summary>Number of videos currently visible to this student under this teacher.</summary>
    public int Count { get; set; }

    /// <summary>
    /// Of <see cref="Count"/>, how many this student has never opened — the tile's "new" badge.
    /// The tile carried a bare total before, so it read the same on every visit and gave a
    /// student no reason to look again. Additive; an older build ignores it.
    /// </summary>
    public int NotStartedCount { get; set; }
}

/// <summary>Homework — Upcoming-Homework card + Homework quick-tile count.</summary>
public class HomeHomeworkDto
{
    /// <summary>Whether the teacher exposes the Homework Track to students.</summary>
    public bool Visible { get; set; }

    /// <summary>
    /// Number of upcoming homework items. NOTE: the student homework read surface
    /// is not built yet — this is 0 (and <see cref="Upcoming"/> empty) until it is.
    /// </summary>
    public int Count { get; set; }

    /// <summary>Nearest upcoming homework items, soonest-first. Empty until the read surface exists.</summary>
    public List<HomeHomeworkItemDto> Upcoming { get; set; } = new();
}

/// <summary>A single upcoming homework row (shape reserved; populated once homework reads ship).</summary>
public class HomeHomeworkItemDto
{
    public long HomeworkId { get; set; }
    public string Title { get; set; } = null!;
    public string? Subject { get; set; }
    public DateTime DueDate { get; set; }
    public int? QuestionsCount { get; set; }
    public string? Status { get; set; }
}

/// <summary>
/// Exams block covering all three exam UI elements on the page: the merged
/// Upcoming-Exam feed (<see cref="Upcoming"/>), the "Exams" quick-tile
/// (<see cref="TileCount"/>), and the "Offline Exam" month strip
/// (<see cref="OfflineMonths"/>). Online and offline exams carry independent
/// visibility flags, so an item appears only when its type is enabled.
/// </summary>
public class HomeExamsDto
{
    /// <summary>Whether online exams are visible (StudentVisibilityOnlineExamDefault).</summary>
    public bool OnlineVisible { get; set; }

    /// <summary>Whether offline exams are visible (StudentVisibilityExamDefault — default on).</summary>
    public bool OfflineVisible { get; set; }

    /// <summary>"Exams" quick-tile badge = number of upcoming exams across enabled types.</summary>
    public int TileCount { get; set; }

    /// <summary>Merged upcoming exams (online + offline), soonest-first, capped; each tagged by ExamType.</summary>
    public List<HomeExamItemDto> Upcoming { get; set; } = new();

    /// <summary>Offline-exam month strip (offline only); empty when offline is hidden.</summary>
    public List<HomeOfflineExamMonthDto> OfflineMonths { get; set; } = new();
}

/// <summary>
/// One entry in the merged Upcoming-Exam feed. <see cref="ExamType"/> tells the
/// frontend which fields apply: online items carry Time/QuestionsCount/
/// DurationMinutes/StudentStatus; offline items leave those null and carry MaxGrade.
/// </summary>
public class HomeExamItemDto
{
    /// <summary>"Online" or "Offline" — the discriminator that decides which fields to render.</summary>
    public string ExamType { get; set; } = null!;

    /// <summary>Exam id (online exam id, or the offline occurrence id).</summary>
    public long ExamId { get; set; }

    public string ExamName { get; set; } = null!;
    public string? Subject { get; set; }

    /// <summary>Exam date (online scheduled date, or offline class date).</summary>
    public DateOnly Date { get; set; }

    /// <summary>Type-appropriate status string (online student status bucket, or offline obligation status).</summary>
    public string? Status { get; set; }

    /// <summary>Scheduled start time — ONLINE only; null for offline.</summary>
    public TimeOnly? Time { get; set; }

    /// <summary>Question count — ONLINE only; null for offline.</summary>
    public int? QuestionsCount { get; set; }

    /// <summary>Exam window length in minutes — ONLINE only; null for offline.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>Max grade (online exam degree, or offline max grade).</summary>
    public decimal? MaxGrade { get; set; }

    /// <summary>Attempt status incl. Blocked — ONLINE only; null for offline / not attempted.</summary>
    public string? StudentStatus { get; set; }
}

/// <summary>One month cell in the Offline-Exam activity strip.</summary>
public class HomeOfflineExamMonthDto
{
    /// <summary>ISO <c>yyyy-MM</c> of the month.</summary>
    public string Month { get; set; } = null!;

    /// <summary>Short month label (e.g. "Jan").</summary>
    public string Label { get; set; } = null!;

    /// <summary>"Done" when the month's exams are past/graded, "Pending" when an exam is still upcoming.</summary>
    public string Status { get; set; } = null!;

    /// <summary>Number of offline exams in the month.</summary>
    public int ExamCount { get; set; }
}
