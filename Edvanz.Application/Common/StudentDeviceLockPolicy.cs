using System;
using Edvanz.Domain.Entities;

namespace Edvanz.Application.Common;

/// <summary>
/// Outcome of evaluating a student's current device against a teacher's device lock.
/// </summary>
public enum DeviceLockDecision
{
    /// <summary>Access allowed — the lock is off, or the presented device matches the bound one.</summary>
    Allowed,

    /// <summary>
    /// Access allowed because the PREVIOUS-id header matched the bound device: the client has
    /// upgraded from the old per-install random id to the stable per-device id and the binding
    /// must be re-pointed to the new one. The caller is responsible for performing that
    /// (idempotent, conditional) write — see
    /// <c>IUserRepo.TryMigrateStudentTeacherLinkDeviceAsync</c>.
    /// </summary>
    AllowedAfterMigration,

    /// <summary>Lock is on but no device is bound yet — the student must confirm and register this device.</summary>
    RegistrationRequired,

    /// <summary>Lock is on and a DIFFERENT device is bound — access denied until a teacher/assistant resets it.</summary>
    Mismatch,

    /// <summary>
    /// Lock is on, a device is bound, and the caller presented NO usable device id at all
    /// (missing/blank header — an out-of-date or tampered client). Still a DENY (fail closed),
    /// but reported with its own code so support can tell "old app" apart from "second phone".
    /// </summary>
    DeviceIdMissing
}

/// <summary>
/// Central, side-effect-free policy for the "lock student to one device (per teacher)" feature.
///
/// The teacher toggles <see cref="TeacherConfiguration.IsDeviceLockEnabled"/>; the per-student
/// binding lives on <see cref="StudentTeacherLink.LockedDeviceId"/> (so it is naturally isolated
/// per teacher — a student can be on a different device for each teacher).
///
/// DEVICE IDENTITY (2026-09-08 — read before changing anything here).
/// The client originally sent a random uuid v4 minted on first run and stored in app-private
/// storage. That is an INSTALL id, not a DEVICE id: deleting the app (or Android "Clear storage",
/// or a backup-restore that leaves the Keystore key behind) destroys it and the app mints a new
/// one, so the SAME phone presented a new identity and the student was locked out for a device
/// change that never happened. The client now derives a STABLE per-device id instead (Android
/// SSAID-derived hash; iOS Keychain entry that survives app deletion), and sends the old value it
/// still has in <see cref="PreviousHeaderName"/> for exactly one migration hop.
///
/// The lock stays STRICT by product decision: a genuinely different device is ALWAYS denied until
/// the teacher resets the binding or turns the setting off. There is no automatic re-bind, no
/// quota and no idle-release — do not add one. A factory reset or a cloned/dual-app space
/// legitimately reads as a new device.
///
/// This must be evaluated at EVERY teacher-scoped student entry point (the aggregated home read
/// plus all per-module content reads) so the lock cannot be bypassed by hitting a different screen.
/// </summary>
public static class StudentDeviceLockPolicy
{
    /// <summary>Request header carrying the client's stable device id.</summary>
    public const string HeaderName = "X-Device-Id";

    /// <summary>
    /// Request header carrying the id this device was previously known by (the pre-2026-09-08
    /// per-install uuid), sent only until the binding has been migrated. Absent on the live
    /// 4.0.0+17 build and on any client that never had an old value — both behave exactly as before.
    /// </summary>
    public const string PreviousHeaderName = "X-Device-Id-Previous";

    /// <summary>
    /// Stable failure code: lock on, no device registered yet (returned with 409 Conflict).
    /// The app reacts by showing the "register this device" consent sheet.
    /// </summary>
    public const string RegistrationRequiredCode = "DeviceRegistrationRequired";

    /// <summary>
    /// Stable failure code: lock on, bound to a different device (returned with 403 Forbidden).
    /// The app reacts by showing the "you're on a different phone — ask your teacher to reset" dialog.
    /// </summary>
    public const string MismatchCode = "DeviceMismatch";

    /// <summary>
    /// Stable failure code: lock on and bound, but the caller sent no device id (403 Forbidden).
    /// Distinct from <see cref="MismatchCode"/> purely so support can diagnose it; the caller is
    /// denied either way.
    /// </summary>
    public const string DeviceIdMissingCode = "DeviceIdMissing";

    /// <summary>
    /// Normalizes a device id for storage and comparison: trims, drops wrapping quotes some HTTP
    /// clients add, and strips NUL/control characters. Returns null for anything that normalizes
    /// to empty, so "absent" and "blank" are one case everywhere.
    /// </summary>
    public static string? Normalize(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        var span = deviceId.Trim();

        // Some HTTP clients/proxies wrap header values in quotes; a quoted id must not read as a
        // different device from the same id unquoted.
        if (span.Length >= 2 &&
            ((span[0] == '"' && span[^1] == '"') || (span[0] == '\'' && span[^1] == '\'')))
        {
            span = span[1..^1].Trim();
        }

        var builder = new System.Text.StringBuilder(span.Length);
        foreach (var c in span)
        {
            if (!char.IsControl(c))
                builder.Append(c);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// True when the two ids denote the same device. Comparison is case-INSENSITIVE: the stable
    /// id is a hex digest whose casing is not guaranteed stable across client platforms, and a
    /// casing difference must never read as a different phone.
    /// </summary>
    public static bool SameDevice(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        return left is not null && right is not null &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Evaluates whether the caller may open the teacher behind <paramref name="link"/>. Pure (no
    /// I/O). <paramref name="config"/> may be null — the lock then fails open to "allowed" (a
    /// missing config row means the teacher never configured a lock).
    ///
    /// <paramref name="enforcementEnabled"/> is the platform kill switch (DeviceLock:Enabled);
    /// false disables the whole feature without a deploy.
    ///
    /// <paramref name="previousDeviceId"/> is matched ONLY when <paramref name="deviceId"/> did not
    /// match and a device is already bound. It can therefore never create a binding, never admit a
    /// third unrelated id, and never widen access — it only recognises the device that was already
    /// bound under its former id.
    /// </summary>
    public static DeviceLockDecision Evaluate(
        StudentTeacherLink link,
        TeacherConfiguration? config,
        string? deviceId,
        string? previousDeviceId = null,
        bool enforcementEnabled = true)
    {
        if (!enforcementEnabled)
            return DeviceLockDecision.Allowed;

        if (config is null || !config.IsDeviceLockEnabled)
            return DeviceLockDecision.Allowed;

        var bound = Normalize(link.LockedDeviceId);
        if (bound is null)
            return DeviceLockDecision.RegistrationRequired;

        var presented = Normalize(deviceId);
        if (presented is not null && SameDevice(bound, presented))
            return DeviceLockDecision.Allowed;

        // One-time upgrade hop: this phone used to be known by another id, and THAT id is the one
        // currently bound. Requires a usable new id to migrate TO — otherwise there is nothing to
        // re-point the binding at and we fall through to the normal denial.
        if (presented is not null && SameDevice(bound, previousDeviceId))
            return DeviceLockDecision.AllowedAfterMigration;

        return presented is null
            ? DeviceLockDecision.DeviceIdMissing
            : DeviceLockDecision.Mismatch;
    }
}
