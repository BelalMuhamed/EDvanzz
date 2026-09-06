using System.Globalization;
using Edvanz.Application.Dtos.Notifications;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements <see cref="IContentPublishNotificationJob"/> — inbox row + FCM push
/// telling students that a video or an online exam is now available to them.
///
/// Follows <c>StudentLinkNotifier</c> exactly: resolve the recipients, render each
/// message under the RECIPIENT's culture (the publishing teacher's language is the
/// wrong one for the student's phone), persist the inbox row, then fan the push out
/// to that user's active device tokens, deactivating stale ones.
///
/// <para>WHO GETS IT — three separate gates, all required:</para>
/// <list type="number">
///   <item>The content's own resolved audience (session / group scope).</item>
///   <item>An ACTIVE StudentTeacherLink that is BOUND to a roster record — a merely
///         connected account sees nothing, so telling it about a video would send it
///         to an empty screen (§7.2b).</item>
///   <item>The teacher still exposes the module to students, and it is still active
///         for that teacher. A teacher who hides Videos should not be pushing videos.</item>
/// </list>
/// </summary>
public class ContentPublishNotificationJob : IContentPublishNotificationJob
{
    private const string StudentVideosDeepLink = "/student/videos";
    private const string StudentExamsDeepLink = "/student/exams";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPushNotificationSender _pushSender;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;
    private readonly ILogger<ContentPublishNotificationJob> _logger;

    public ContentPublishNotificationJob(
        IUnitOfWork unitOfWork,
        IPushNotificationSender pushSender,
        IStringLocalizer<Domain.Resources.Messages> localizer,
        ILogger<ContentPublishNotificationJob> logger)
    {
        _unitOfWork = unitOfWork;
        _pushSender = pushSender;
        _localizer = localizer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyVideoPublishedAsync(long teacherId, long videoAssetId)
    {
        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);

        // Re-checked at RUN time, not at enqueue time. A scheduled publish may fire
        // long after it was queued, by which point the video could be back in Draft,
        // re-dated, or deleted — announcing it then would point students at nothing.
        if (video is null
            || video.Status != VideoStatus.Published
            || (video.PublishDate is not null && video.PublishDate > DateTime.UtcNow))
        {
            _logger.LogInformation(
                "Publish notification skipped: video {VideoId} is not visible to students right now.",
                videoAssetId);
            return;
        }

        if (!await IsModuleVisibleToStudentsAsync(teacherId, forVideos: true)) return;

        var rosterIds = await _unitOfWork.VideoAssetsRepo
            .GetAudienceTeacherStudentIdsAsync(teacherId, videoAssetId);

        await NotifyAsync(
            teacherId,
            rosterIds,
            NotificationSourceType.VideoPublished,
            videoAssetId,
            "VideoPublishedNotifTitle",
            "VideoPublishedNotifBody",
            video.Title,
            StudentVideosDeepLink);
    }

    /// <inheritdoc />
    public async Task NotifyOnlineExamPublishedAsync(long teacherId, long onlineExamId)
    {
        var exam = await _unitOfWork.OnlineExamsRepo.GetByIdAndTeacherAsync(onlineExamId, teacherId);
        if (exam is null || exam.Status != OnlineExamStatus.Published)
        {
            _logger.LogInformation(
                "Publish notification skipped: online exam {ExamId} is no longer published.",
                onlineExamId);
            return;
        }

        if (!await IsModuleVisibleToStudentsAsync(teacherId, forVideos: false)) return;

        var rosterIds = await _unitOfWork.OnlineExamsRepo
            .BuildAssignedStudentIdsQuery(onlineExamId, teacherId)
            .Distinct()
            .ToListAsync();

        await NotifyAsync(
            teacherId,
            rosterIds,
            NotificationSourceType.OnlineExamPublished,
            onlineExamId,
            "OnlineExamPublishedNotifTitle",
            "OnlineExamPublishedNotifBody",
            exam.Title,
            StudentExamsDeepLink);
    }

    // ══════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════

    /// <summary>
    /// The teacher must still expose the module to students AND have it active.
    /// Both are re-read here rather than captured at enqueue time, for the same
    /// reason the content itself is: a scheduled job can fire much later.
    /// Fails CLOSED — an unreadable configuration means no push.
    /// </summary>
    private async Task<bool> IsModuleVisibleToStudentsAsync(long teacherId, bool forVideos)
    {
        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);

        // Fail OPEN on a missing config row, exactly as the student home aggregate
        // does (`config?.StudentVisibilityVideo ?? true`). Failing closed here would
        // mean a teacher whose config row never got created can see their students
        // using the module while none of them are ever told about new content.
        bool visible = forVideos
            ? config?.StudentVisibilityVideo ?? true
            : config?.StudentVisibilityOnlineExamDefault ?? true;
        if (!visible) return false;

        string moduleName = forVideos ? VideoConstants.ModuleName : OnlineExamConstants.ModuleName;
        return await _unitOfWork.ModuleTeacherRepo!.IsModuleActiveAsync(teacherId, moduleName);
    }

    /// <summary>
    /// Shared fan-out. Resolves roster ids to app accounts, drops anyone already
    /// notified for this exact (source, entity), then writes one inbox row per
    /// remaining recipient and pushes to their devices.
    /// </summary>
    private async Task NotifyAsync(
        long teacherId,
        IReadOnlyList<long> rosterIds,
        NotificationSourceType sourceType,
        long sourceEntityId,
        string titleKey,
        string bodyKey,
        string contentTitle,
        string deepLink)
    {
        if (rosterIds.Count == 0) return;

        var recipientUserIds = await _unitOfWork.Users
            .GetLinkedStudentUserIdsAsync(teacherId, rosterIds);
        if (recipientUserIds.Count == 0) return;

        // Retry-safety on the happy path: the unique index is the real guarantee,
        // but skipping known recipients avoids provoking it — and avoids the push
        // that would otherwise be sent before the insert failed.
        var alreadyNotified = (await _unitOfWork.UserNotificationsRepo
            .GetUserIdsAlreadyNotifiedAsync(sourceType, sourceEntityId, recipientUserIds))
            .ToHashSet();

        var pending = recipientUserIds.Where(id => !alreadyNotified.Contains(id)).ToList();
        if (pending.Count == 0) return;

        // Every recipient's language is read in ONE query, so rendering per student
        // costs nothing extra.
        var languageByUserId = await _unitOfWork.Users.GetStudentLanguagePreferencesAsync(pending);

        var rendered = new List<(long UserId, string Title, string Body)>(pending.Count);
        foreach (long userId in pending)
        {
            languageByUserId.TryGetValue(userId, out string? language);
            var (title, body) = RenderInCulture(language, titleKey, bodyKey, contentTitle);
            rendered.Add((userId, title, body));

            await _unitOfWork.UserNotificationsRepo.InsertNotificationAsync(new UserNotification
            {
                UserId = userId,
                Title = title,
                Body = body,
                DeepLinkPayload = deepLink,
                SentAt = DateTime.UtcNow,
                IsRead = false,
                Category = NotificationCategory.notifiction,
                SourceType = sourceType,
                SourceEntityId = sourceEntityId,
                CreateAt = DateTime.UtcNow,
            });
        }

        // One SaveChanges for the whole batch. If it throws, Hangfire retries and the
        // dedupe above plus the unique index keep the retry from duplicating anything.
        await _unitOfWork.SaveChangesAsync();

        // Pushes come AFTER the rows are durable: a delivered push whose inbox row was
        // rolled back would leave a notification the student can never open again.
        foreach (var (userId, title, body) in rendered)
        {
            try
            {
                var tokens = await _unitOfWork.UserDeviceTokensRepo.GetActiveTokensForUserAsync(userId);
                foreach (var token in tokens)
                {
                    var result = await _pushSender.SendAsync(
                        token.FcmToken, title, body,
                        new PushPayload
                        {
                            Category = NotificationCategory.notifiction,
                            Screen = deepLink,
                        });

                    if (!result.Success && result.ShouldDeactivateToken)
                        await _unitOfWork.UserDeviceTokensRepo.DeactivateTokenAsync(token.Id);
                }
            }
            catch (Exception ex)
            {
                // One student's dead token must not cost the rest of the class their
                // notification — the inbox rows are already committed either way.
                _logger.LogWarning(ex,
                    "Publish push failed for user {UserId} ({SourceType} {EntityId}).",
                    userId, sourceType, sourceEntityId);
            }
        }
    }

    /// <summary>
    /// Renders title/body under the RECIPIENT's culture, then restores the ambient one.
    /// The job runs with no HTTP request culture at all, so without this every student
    /// would get English. Mirrors <c>StudentLinkNotifier.RenderInCulture</c>.
    /// </summary>
    private (string Title, string Body) RenderInCulture(
        string? languagePreference, string titleKey, string bodyKey, string arg)
    {
        var originalUi = CultureInfo.CurrentUICulture;
        var original = CultureInfo.CurrentCulture;
        try
        {
            var culture = new CultureInfo(
                languagePreference?.Trim().ToLowerInvariant() == "ar" ? "ar" : "en");
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return (_localizer[titleKey], _localizer[bodyKey, arg]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }
}
