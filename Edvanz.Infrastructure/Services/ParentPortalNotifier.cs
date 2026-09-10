using System.Globalization;
using Edvanz.Application.Dtos.Notifications;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;

namespace Edvanz.Infrastructure.Services;

/// <inheritdoc cref="IParentPortalNotifier"/>
/// <remarks>
/// Follows <c>StudentLinkNotifier</c> exactly: resolve the recipient, render the strings under the
/// RECIPIENT's culture, persist the inbox row, then fan the push out to active device tokens
/// (deactivating stale ones). Lives in Infrastructure per CLAUDE.md §6.2 so notification fan-out
/// never pulls transport concerns into the Application layer.
///
/// Runs as a post-commit side-effect unit: it owns its own SaveChanges for the inbox row, and the
/// caller invokes it after committing, inside try/catch.
/// </remarks>
public class ParentPortalNotifier : IParentPortalNotifier
{
    /// <summary>Deep link the teacher app opens when the notification is tapped.</summary>
    private const string TeacherDeepLink = "/teacher/parent-portal/requests";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPushNotificationSender _pushSender;
    private readonly IStringLocalizer<Edvanz.Domain.Resources.Messages> _localizer;

    public ParentPortalNotifier(
        IUnitOfWork unitOfWork,
        IPushNotificationSender pushSender,
        IStringLocalizer<Edvanz.Domain.Resources.Messages> localizer)
    {
        _unitOfWork = unitOfWork;
        _pushSender = pushSender;
        _localizer = localizer;
    }

    /// <inheritdoc />
    public async Task NotifyPendingRequestsAsync(
        long teacherId, string studentName, int pendingCount, string? parentName = null,
        bool suppressPush = false)
    {
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(teacherId);
        if (teacher is null) return;

        // Naming the PARENT is the point: "someone wants to follow Ahmed" tells the teacher
        // nothing they can act on. So a named request is ALWAYS announced by name — one inbox row
        // describes one parent, and rolling several into "3 parents are waiting" would throw away
        // the only detail the teacher can decide on.
        //
        // The anonymous wording survives only for requests carrying no name (grants written before
        // the portal collected one), and only there does the count still earn its place.
        bool named = !string.IsNullOrWhiteSpace(parentName);
        bool batched = !named && pendingCount > 1;

        string messageKey = batched
            ? "ParentPortalNewRequestsNotification"
            : named
                ? "ParentPortalNewRequestNamedNotification"
                : "ParentPortalNewRequestNotification";

        object[] args = batched
            ? new object[] { pendingCount }
            : named
                ? new object[] { parentName!.Trim(), studentName }
                : new object[] { studentName };

        string body = RenderInCulture(teacher.LanguagePreference, messageKey, args);

        // The portal has no separate title string in the resx; the body doubles as the title, the
        // same shape the FCM payload uses elsewhere when only one string is authored.
        await PersistAndPushAsync(teacher.UserId, body, body, new PushPayload
        {
            Category = NotificationCategory.notifiction,
            Screen = TeacherDeepLink
        }, suppressPush);
    }

    /// <summary>
    /// Renders one localized string under the RECIPIENT's culture, then restores the ambient
    /// culture. The HTTP request culture belongs to the PORTAL (a parent's browser), which is the
    /// wrong language for the teacher's push.
    /// </summary>
    private string RenderInCulture(string? languagePreference, string messageKey, params object[] args)
    {
        var originalUi = CultureInfo.CurrentUICulture;
        var original = CultureInfo.CurrentCulture;
        try
        {
            var culture = new CultureInfo(
                languagePreference?.Trim().ToLowerInvariant() == "ar" ? "ar" : "en");
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            return _localizer[messageKey, args];
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    private async Task PersistAndPushAsync(
        long recipientUserId, string title, string body, PushPayload payload, bool suppressPush)
    {
        // The inbox row is written unconditionally — it is the durable record the teacher can go
        // back and find, and the only signal at all when push is unavailable. Only the buzz is
        // ever withheld.
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

        if (suppressPush) return;

        var tokens = await _unitOfWork.UserDeviceTokensRepo.GetActiveTokensForUserAsync(recipientUserId);
        foreach (var token in tokens)
        {
            var result = await _pushSender.SendAsync(token.FcmToken, title, body, payload);

            // Stale FCM token — deactivate so future sends skip it (same handling as StudentLinkNotifier).
            if (!result.Success && result.ShouldDeactivateToken)
                await _unitOfWork.UserDeviceTokensRepo.DeactivateTokenAsync(token.Id);
        }
    }
}
