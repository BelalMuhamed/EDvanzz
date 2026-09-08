using System.Threading.Tasks;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.StudentUser;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Aggregates the student "teacher home" screen (Figma 232:7033) into ONE call:
/// after a student selects a linked teacher, this composes attendance, payment,
/// videos, online/offline exams and homework — each behind its own teacher
/// visibility toggle — so the frontend renders the whole page from a single read.
/// </summary>
public interface IStudentTeacherHomeService
{
    /// <summary>
    /// Builds the aggregated teacher-home payload for the authenticated student.
    /// The caller must be ACTIVELY linked to and bound under <paramref name="teacherId"/>
    /// (otherwise 403, mirroring every other student read). Each module section is
    /// filled only when the teacher exposes it; hidden sections carry their flag with
    /// empty data. A single module failing degrades that section, never the whole call.
    /// </summary>
    /// <param name="studentUserId">The resolved StudentUser id (from the JWT — never a route/body id).</param>
    /// <param name="teacherId">The selected teacher (route segment).</param>
    /// <param name="year">Optional scoping year (with <paramref name="month"/>); defaults to teacher-local current month.</param>
    /// <param name="month">Optional scoping month 1-12 (with <paramref name="year"/>).</param>
    /// <param name="deviceId">The caller's device id (X-Device-Id header). Used only when the teacher has enabled
    /// the device lock: an unregistered device is rejected with <c>DeviceRegistrationRequired</c> (409) and a
    /// non-matching device with <c>DeviceMismatch</c> (403). Ignored when the lock is off.</param>
    /// <param name="previousDeviceId">The id this device was PREVIOUSLY known by
    /// (X-Device-Id-Previous header), sent only by clients upgrading from the old per-install id.
    /// Matched only after <paramref name="deviceId"/> fails and only against an existing binding, so
    /// it can migrate the binding onto the stable id but can never widen access. Null/absent on the
    /// live 4.0.0+17 build.</param>
    Task<Result<StudentTeacherHomeDto>> GetTeacherHomeAsync(
        long studentUserId, long teacherId, int? year, int? month, string? deviceId,
        string? previousDeviceId = null);
}
