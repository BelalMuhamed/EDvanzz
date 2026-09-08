using System.Net;
using Edvanz.Application.Common;
using Edvanz.Application.Options;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements <see cref="IStudentDeviceLockService"/>. See the interface for the contract and
/// <see cref="StudentDeviceLockPolicy"/> for the rule itself (and for why the lock is deliberately
/// strict — do not add an automatic re-bind here).
/// </summary>
public class StudentDeviceLockService : IStudentDeviceLockService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStudentLinkNotifier _linkNotifier;
    private readonly DeviceLockOptions _options;

    public StudentDeviceLockService(
        IUnitOfWork unitOfWork,
        IStudentLinkNotifier linkNotifier,
        IOptions<DeviceLockOptions> options)
    {
        _unitOfWork = unitOfWork;
        _linkNotifier = linkNotifier;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<DeviceLockOutcome> EvaluateAsync(
        StudentTeacherLink link, long teacherId, string? deviceId, string? previousDeviceId)
    {
        // Kill switch first: when enforcement is off platform-wide there is nothing to check, so
        // skip the configuration read entirely.
        if (!_options.Enabled)
            return DeviceLockOutcome.Allowed;

        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);
        return await EvaluateAsync(link, teacherId, config, deviceId, previousDeviceId);
    }

    /// <inheritdoc />
    public async Task<DeviceLockOutcome> EvaluateAsync(
        StudentTeacherLink link, long teacherId, TeacherConfiguration? config,
        string? deviceId, string? previousDeviceId)
    {
        var decision = StudentDeviceLockPolicy.Evaluate(
            link, config, deviceId, previousDeviceId, _options.Enabled);

        switch (decision)
        {
            case DeviceLockDecision.Allowed:
                return DeviceLockOutcome.Allowed;

            case DeviceLockDecision.AllowedAfterMigration:
                await MigrateBindingAsync(link, deviceId, previousDeviceId);
                return DeviceLockOutcome.Allowed;

            case DeviceLockDecision.RegistrationRequired:
                return new DeviceLockOutcome(
                    decision,
                    StudentDeviceLockPolicy.RegistrationRequiredCode,
                    HttpStatusCode.Conflict);

            case DeviceLockDecision.DeviceIdMissing:
                // Still a denial (fail closed) — only the reported code differs, so support can
                // tell an out-of-date client from a genuine second phone. No teacher alert: this
                // is a client fault, not a student trying another device.
                return new DeviceLockOutcome(
                    decision,
                    StudentDeviceLockPolicy.DeviceIdMissingCode,
                    HttpStatusCode.Forbidden);

            default:
                await NotifyTeacherOfBlockAsync(link, teacherId);
                return new DeviceLockOutcome(
                    DeviceLockDecision.Mismatch,
                    StudentDeviceLockPolicy.MismatchCode,
                    HttpStatusCode.Forbidden);
        }
    }

    // ══════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════

    /// <summary>
    /// Re-points the binding from the id this device used to present to its new stable one. The
    /// write is conditional on the old id (see the repo method), so it is idempotent, race-safe and
    /// incapable of touching a link bound to a different device. The in-memory entity is updated
    /// too, because callers read <c>link.LockedDeviceId</c> after the gate.
    /// </summary>
    private async Task MigrateBindingAsync(StudentTeacherLink link, string? deviceId, string? previousDeviceId)
    {
        var stable = StudentDeviceLockPolicy.Normalize(deviceId);
        var previous = StudentDeviceLockPolicy.Normalize(previousDeviceId);
        if (stable is null || previous is null)
            return;

        // Migration is a convenience, never a gate: if the write fails the student is already
        // authorized for THIS request and simply migrates on a later one.
        try
        {
            await _unitOfWork.Users.TryMigrateStudentTeacherLinkDeviceAsync(
                link.Id, previous, stable, DateTime.UtcNow);
            link.LockedDeviceId = stable;
        }
        catch { /* never fail an authorized read because the id could not be upgraded */ }
    }

    /// <summary>
    /// Tells the teacher a student was blocked on a foreign device — at most once per cooldown
    /// window per link, claimed atomically so a locked-out app hitting the gate on every screen
    /// cannot spam the teacher. Entirely best-effort: a blocked request stays blocked whatever
    /// happens here.
    /// </summary>
    private async Task NotifyTeacherOfBlockAsync(StudentTeacherLink link, long teacherId)
    {
        try
        {
            int cooldownHours = Math.Max(1, _options.BlockNotificationCooldownHours);
            var now = DateTime.UtcNow;

            bool claimed = await _unitOfWork.Users.TryStampDeviceBlockNotifiedAsync(
                link.Id, now, now.AddHours(-cooldownHours));
            if (!claimed)
                return;

            string studentName = await ResolveStudentNameAsync(link, teacherId);
            if (string.IsNullOrWhiteSpace(studentName))
                return;

            await _linkNotifier.NotifyDeviceBlockedAsync(teacherId, studentName);
        }
        catch { /* a notification failure must never change the gate's answer */ }
    }

    /// <summary>
    /// The name the TEACHER knows this student by: the bound roster record's name when there is
    /// one, falling back to the account holder's name.
    /// </summary>
    private async Task<string> ResolveStudentNameAsync(StudentTeacherLink link, long teacherId)
    {
        if (link.TeacherStudentId is not null)
        {
            var roster = await _unitOfWork.Users.GetActiveTeacherStudentByIdAsync(
                teacherId, link.TeacherStudentId.Value);
            if (!string.IsNullOrWhiteSpace(roster?.StudentName))
                return roster!.StudentName;
        }

        var studentUser = await _unitOfWork.Users.GetStudentUserByIdAsync(link.StudentUserId);
        if (studentUser is null)
            return string.Empty;

        var user = await _unitOfWork.Users.GetUserByIdAsync(studentUser.UserId);
        return user?.FullName ?? string.Empty;
    }
}
