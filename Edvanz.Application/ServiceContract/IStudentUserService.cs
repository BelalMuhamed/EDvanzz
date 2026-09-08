using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.StudentUser;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Defines the contract for Student User module operations.
/// Called by the User module after routing based on UserType = Student.
/// All methods are async per system architecture requirements.
/// Lives in the Application layer because it depends on Application DTOs.
/// 
/// IMPORTANT: User-level operations (registration, login, password update,
/// account deactivation, deletion) are handled by the User module.
/// This service handles Student-SPECIFIC operations only.
/// </summary>
public interface IStudentUserService
{
    /// <summary>
    /// Initializes the StudentUser record and generates the StudentAccountCode.
    /// Called after the User record is created during registration.
    /// AAM-FR-05.3: Auto-generates unique StudentAccountCode.
    /// AAM-FR-05.4: Sets IsFirstLogin = true for empty dashboard prompt.
    /// </summary>
    /// <param name="dto">Registration data including userId and language preference.</param>
    /// <returns>Result containing the created student user's profile including the generated code.</returns>
    Task<Result<StudentUserProfileDto>> InitializeStudentUserAsync(CreateStudentUserDto dto);

    /// <summary>
    /// Retrieves the full student user profile including linked teacher count.
    /// </summary>
    /// <param name="studentUserId">The StudentUser's Id (not the UserId).</param>
    /// <returns>Result containing the student user profile DTO.</returns>
    Task<Result<StudentUserProfileDto>> GetStudentUserProfileAsync(long studentUserId);

    /// <summary>
    /// Updates the student user's own profile settings.
    /// AAM-FR-02.3: Language preference changeable from settings.
    /// Note: FullName, PhoneNumber, Password updates go through the User module.
    /// </summary>
    /// <param name="studentUserId">The StudentUser's Id.</param>
    /// <param name="dto">Profile fields to update.</param>
    /// <returns>Result containing the updated student user profile.</returns>
    Task<Result<StudentUserProfileDto>> UpdateStudentUserProfileAsync(long studentUserId, UpdateStudentUserProfileDto dto);

    /// <summary>
    /// Retrieves the student's dashboard including linked teachers and first-login state.
    /// AAM-FR-05.4: Empty dashboard with prompt on first login.
    /// AAM-FR-05.6/07: Lists all linked teachers with name and subject.
    /// AAM-FR-05.8: Includes visibility configuration per teacher.
    /// </summary>
    /// <param name="studentUserId">The StudentUser's Id.</param>
    /// <returns>Result containing the dashboard DTO.</returns>
    Task<Result<StudentDashboardDto>> GetDashboardAsync(long studentUserId);

    /// <summary>
    /// Sends a link REQUEST to a teacher (request/approval flow — supersedes the
    /// 3-credential instant link). Creates a Pending StudentTeacherLink carrying
    /// the student-typed name and optional teacher-assigned student code; the
    /// teacher later accepts (binding a roster record) or rejects it.
    /// Notifies the teacher (inbox + push) post-commit, best-effort.
    /// </summary>
    /// <param name="studentUserId">The StudentUser's Id (resolved from JWT by the controller).</param>
    /// <param name="dto">Teacher code + student-typed name + optional student code.</param>
    /// <returns>Result containing the new Pending dashboard entry for the teacher.</returns>
    Task<Result<StudentDashboardTeacherDto>> CreateLinkRequestAsync(long studentUserId, CreateLinkRequestDto dto);

    /// <summary>
    /// Removes a Teacher from the Student's side:
    ///   - a Pending request is cancelled (LinkStatus = CancelledByStudent);
    ///   - an Active link is unlinked (LinkStatus = Unlinked).
    /// Terminal rows are preserved for audit; the student may send a new request later.
    /// </summary>
    /// <param name="studentUserId">The StudentUser's Id (resolved from JWT by the controller).</param>
    /// <param name="teacherId">The Teacher's Id to remove.</param>
    /// <param name="actingUserId">User.Id of the caller — persisted to RemovedByUserId for audit.</param>
    /// <returns>Result indicating success or failure.</returns>
    Task<Result<bool>> UnlinkTeacherAsync(long studentUserId, long teacherId, long actingUserId);

    /// <summary>
    /// "Deletes" a Teacher from the Student's side — a stronger action than Unlink.
    /// Ends any live link (Active → Unlinked, Pending → CancelledByStudent) AND marks
    /// the card dismissed so it disappears from the student's home entirely (Unlink,
    /// by contrast, keeps the card visible as "Unlinked" and re-requestable). Also
    /// notifies the teacher that a linked student left (awareness only — nothing on the
    /// teacher's roster/attendance/payment data changes). Idempotent, and works on the
    /// latest row of ANY status so a dead card can be cleared too.
    /// </summary>
    /// <param name="studentUserId">The StudentUser's Id (resolved from JWT by the controller).</param>
    /// <param name="teacherId">The Teacher's Id to delete.</param>
    /// <param name="actingUserId">User.Id of the caller — persisted to RemovedByUserId for audit.</param>
    Task<Result<bool>> DeleteTeacherAsync(long studentUserId, long teacherId, long actingUserId);

    /// <summary>
    /// Retrieves the student's teachers INCLUDING request states — one entry per
    /// teacher (the latest link row), so the student can see Pending requests and
    /// whether a request was accepted (Active) or rejected (Rejected).
    /// AAM-FR-05.7: Each Teacher displayed distinctly.
    /// AAM-FR-05.8: Visibility flags from the teacher's configuration are included
    /// so the app knows which module tiles to show once the link is Active.
    /// </summary>
    /// <param name="studentUserId">The StudentUser's Id (resolved from JWT by the controller).</param>
    /// <returns>Result containing the list of teacher entries with status.</returns>
    Task<Result<List<StudentDashboardTeacherDto>>> GetMyTeachersAsync(long studentUserId);

    /// <summary>
    /// Retrieves a student user by their unique StudentAccountCode.
    /// Used by the Parent module when linking to a child's account (AAM-FR-06.3 Method A).
    /// Returns minimal info: account code, full name.
    /// </summary>
    /// <param name="accountCode">The unique student account code.</param>
    /// <returns>Result containing basic student info if found.</returns>
    Task<Result<StudentUserProfileDto>> GetStudentUserByAccountCodeAsync(string accountCode);

    /// <summary>
    /// Returns the authenticated student's per-teacher attendance QR for the "Show QR Code"
    /// screen. The student displays the QR and the teacher scans it (from "Scan QR Code from
    /// Student account") to record attendance / exam presence / payment. The QR encodes the
    /// per-teacher student code (<c>TeacherStudent.StudentCode</c>) — the SAME value the scan
    /// paths resolve via <c>GetActiveByCodeAndTeacherAsync</c> — so the loop closes.
    ///
    /// GATES: the caller must have an ACTIVE <c>StudentTeacherLink</c> to
    /// <paramref name="teacherId"/> (else <c>StudentNotLinkedToTeacher</c>, 403) that is bound
    /// to a roster record (else <c>StudentNotBoundToRoster</c>, 403 — no per-teacher code
    /// exists yet). If the teacher issues codes as printed cards only
    /// (<c>BarcodeDisplayMode.HardCopyOnly</c>) the QR is withheld
    /// (<c>BarcodeNotAvailableInApp</c>, 403) so the frontend can hide the button. AAM-FR-04.7.
    /// </summary>
    /// <param name="studentUserId">The StudentUser's Id (resolved from JWT by the controller).</param>
    /// <param name="teacherId">The linked teacher whose per-teacher code the QR encodes (route segment, NOT the student id).</param>
    /// <param name="deviceId">The caller's device id (X-Device-Id header); enforced only when the teacher's device
    /// lock is on (unregistered → <c>DeviceRegistrationRequired</c> 409, non-matching → <c>DeviceMismatch</c> 403).</param>
    /// <returns>Result containing the teacher name, the code, and the rendered QR SVG.</returns>
    Task<Result<StudentTeacherBarcodeDto>> GetTeacherBarcodeForStudentAsync(
        long studentUserId, long teacherId, string? deviceId, string? previousDeviceId = null);

    /// <summary>
    /// Registers the caller's current device as the one allowed to open <paramref name="teacherId"/>,
    /// after the student has consented to the device lock. Requires an ACTIVE, bound link. Idempotent:
    /// re-registering the same device (or calling when the lock is off) succeeds; a device is bound only
    /// when none is set yet (atomic — the first device wins a race). Returns <c>DeviceMismatch</c> (403)
    /// when another device is already bound (the teacher/assistant must reset it first), or
    /// <c>DeviceIdMissing</c> (400) when the header is absent.
    /// </summary>
    /// <param name="studentUserId">The StudentUser's Id (resolved from JWT by the controller).</param>
    /// <param name="teacherId">The linked teacher to bind this device under (route segment).</param>
    /// <param name="deviceId">The caller's device id (X-Device-Id header).</param>
    Task<Result<bool>> RegisterDeviceForTeacherAsync(
        long studentUserId, long teacherId, string? deviceId, string? previousDeviceId = null);

    /// <summary>
    /// SuperAdmin-only: pages ALL student accounts on the platform (not scoped to
    /// any teacher), each enriched with the list of teachers it currently holds
    /// an ACTIVE link to. Supports searching by account full name or by the
    /// per-teacher student code, and filtering to a single teacher's accounts.
    /// </summary>
    /// <param name="request">Page/pageSize plus the optional search term and teacherId filter.</param>
    /// <returns>Result containing the paginated, enriched student-accounts page.</returns>
    Task<Result<PaginatedResponse<List<StudentAccountListItemDto>>>> GetStudentAccountsAsync(
        StudentAccountListRequest request);
}