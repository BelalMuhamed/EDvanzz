using Edvanz.Application.Dtos.Notifications;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements <see cref="IStudentLinkNotifier"/> — inbox row + FCM push for the
/// link request/approval flow, following the exact pattern of the subscription
/// notification jobs (RenewalNotificationJob): resolve recipient, render the
/// localized strings under the RECIPIENT's culture, persist the UserNotification,
/// then fan the push out to active device tokens (deactivating stale ones).
///
/// Runs as a post-commit side-effect unit: it owns its own SaveChanges for the
/// inbox row. Callers invoke it after their transaction commits, inside try/catch.
/// </summary>
public class StudentLinkNotifier : IStudentLinkNotifier
{
    private const string TeacherDeepLink = "/teacher/link-requests";
    private const string StudentDeepLink = "/student/teachers";
    private const string TeacherLinkedStudentsDeepLink = "/teacher/linked-students";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPushNotificationSender _pushSender;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;

    public StudentLinkNotifier(
        IUnitOfWork unitOfWork,
        IPushNotificationSender pushSender,
        IStringLocalizer<Domain.Resources.Messages> localizer)
    {
        _unitOfWork = unitOfWork;
        _pushSender = pushSender;
        _localizer = localizer;
    }

    /// <inheritdoc />
    public async Task NotifyRequestReceivedAsync(long teacherId, string requestedStudentName)
    {
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(teacherId);
        if (teacher is null) return;

        var (title, body) = RenderInCulture(teacher.LanguagePreference,
            "LinkRequestReceivedNotifTitle", "LinkRequestReceivedNotifBody", requestedStudentName);
        await PersistAndPushAsync(teacher.UserId, title, body, new PushPayload
        {
            Category = NotificationCategory.notifiction,
            Screen = TeacherDeepLink
        }); 
    }

    /// <inheritdoc />
    public async Task NotifyRequestResolvedAsync(long studentUserId, long teacherId, bool accepted)
    {
        var (studentUser, teacherName) = await ResolveStudentAndTeacherNameAsync(studentUserId, teacherId);
        if (studentUser is null) return;

        string titleKey = accepted ? "LinkRequestAcceptedNotifTitle" : "LinkRequestRejectedNotifTitle";
        string bodyKey = accepted ? "LinkRequestAcceptedNotifBody" : "LinkRequestRejectedNotifBody";

        var (title, body) = RenderInCulture(studentUser.LanguagePreference, titleKey, bodyKey, teacherName);

        await PersistAndPushAsync(studentUser.UserId, title, body, new PushPayload
        {
            Category = NotificationCategory.notifiction,
            Screen = StudentDeepLink
        });
    }

    /// <inheritdoc />
    public async Task NotifyRemovedByTeacherAsync(long studentUserId, long teacherId)
    {
        var (studentUser, teacherName) = await ResolveStudentAndTeacherNameAsync(studentUserId, teacherId);
        if (studentUser is null) return;

        var (title, body) = RenderInCulture(studentUser.LanguagePreference,
            "LinkRemovedByTeacherNotifTitle", "LinkRemovedByTeacherNotifBody", teacherName);

        await PersistAndPushAsync(studentUser.UserId, title, body, new PushPayload { Category=NotificationCategory.notifiction,Screen=StudentDeepLink});
    }

    /// <inheritdoc />
    public async Task NotifyLinkDeletedByStudentAsync(long teacherId, string studentName)
    {
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(teacherId);
        if (teacher is null) return;

        var (title, body) = RenderInCulture(teacher.LanguagePreference,
            "LinkDeletedByStudentNotifTitle", "LinkDeletedByStudentNotifBody", studentName);

        await PersistAndPushAsync(teacher.UserId, title, body, new PushPayload
        {
            Category = NotificationCategory.notifiction,
            Screen = TeacherDeepLink
        });
    }

    /// <inheritdoc />
    public async Task NotifyLinkBindingChangedAsync(long studentUserId, long teacherId, bool linked)
    {
        var (studentUser, teacherName) = await ResolveStudentAndTeacherNameAsync(studentUserId, teacherId);
        if (studentUser is null) return;

        string titleKey = linked ? "LinkBoundNotifTitle" : "LinkUnboundNotifTitle";
        string bodyKey = linked ? "LinkBoundNotifBody" : "LinkUnboundNotifBody";

        var (title, body) = RenderInCulture(studentUser.LanguagePreference, titleKey, bodyKey, teacherName);

        await PersistAndPushAsync(studentUser.UserId, title, body, new PushPayload { Category = NotificationCategory.notifiction, Screen = StudentDeepLink });
    }

    /// <inheritdoc />
    public async Task NotifyDeviceBlockedAsync(long teacherId, string studentName)
    {
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(teacherId);
        if (teacher is null) return;

        var (title, body) = RenderInCulture(teacher.LanguagePreference,
            "DeviceBlockedNotifTitle", "DeviceBlockedNotifBody", studentName);

        // Deep-links to the linked-students screen, where the per-student "reset device" action
        // already lives — the teacher can act straight from the notification.
        await PersistAndPushAsync(teacher.UserId, title, body, new PushPayload
        {
            Category = NotificationCategory.notifiction,
            Screen = TeacherLinkedStudentsDeepLink
        });
    }

    // ══════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════

    private async Task<(StudentUser? studentUser, string teacherName)> ResolveStudentAndTeacherNameAsync(
        long studentUserId, long teacherId)
    {
        var studentUser = await _unitOfWork.Users.GetStudentUserByIdAsync(studentUserId);
        if (studentUser is null) return (null, string.Empty);

        string teacherName = string.Empty;
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(teacherId);
        if (teacher is not null)
        {
            var teacherUser = await _unitOfWork.Users.GetUserByIdAsync(teacher.UserId);
            teacherName = teacherUser?.FullName ?? string.Empty;
        }

        return (studentUser, teacherName);
    }

    /// <summary>
    /// Renders a title/body pair under the RECIPIENT's culture, then restores the
    /// ambient culture. The HTTP request culture belongs to the CALLER (e.g. the
    /// teacher accepting), which is the wrong language for the student's push.
    /// </summary>
    private (string Title, string Body) RenderInCulture(
        string? languagePreference, string titleKey, string bodyKey, string arg)
    {
        var original = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var culture = new CultureInfo(languagePreference?.Trim().ToLowerInvariant() == "ar" ? "ar" : "en");
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            return (_localizer[titleKey], _localizer[bodyKey, arg]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = original;
        }
    }
    private async Task PersistAndPushAsync(long recipientUserId, string title, string body, PushPayload payload)
    {
        await _unitOfWork.UserNotificationsRepo.InsertNotificationAsync(new UserNotification
        {
            UserId = recipientUserId,
            Title = title,
            Body = body,
            DeepLinkPayload = payload.Screen,
            SentAt = DateTime.UtcNow,
            IsRead = false,
            Category = payload.Category,
            CreateAt = DateTime.UtcNow
        });

        await _unitOfWork.SaveChangesAsync();

        var tokens = await _unitOfWork.UserDeviceTokensRepo.GetActiveTokensForUserAsync(recipientUserId);
        foreach (var token in tokens)
        {
            var result = await _pushSender.SendAsync(token.FcmToken, title, body, payload);

            if (!result.Success && result.ShouldDeactivateToken)
            {
                // EC-11: stale FCM token — deactivate so future sends skip it.
                await _unitOfWork.UserDeviceTokensRepo.DeactivateTokenAsync(token.Id);
            }
        }
    }
}