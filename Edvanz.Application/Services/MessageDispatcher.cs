using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.DispatcherDtos;
using Edvanz.Application.Dtos.MessageResolver;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Entities.Messaging;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Hangfire;
using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;
using System.Text;

namespace Edvanz.Application.Services
{
    public class MessageDispatcher : IMessageDispatcher
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAutomatedTriggerService _triggerService;
        private readonly IBlockResolver _resolver;
        private readonly IStringLocalizer<Messages> _localizer;
        private readonly IMessageLogService _logService;
        private readonly ITimeZoneService _timeZoneService;

        public MessageDispatcher(
            IUnitOfWork unitOfWork,
            IAutomatedTriggerService triggerService,
            IBlockResolver resolver,
            IStringLocalizer<Messages> localizer,
            IMessageLogService logService,
            ITimeZoneService timeZoneService)
        {
            _unitOfWork = unitOfWork;
            _triggerService = triggerService;
            _resolver = resolver;
            _localizer = localizer;
            _logService = logService;
            _timeZoneService = timeZoneService;
        }

        // ── AUTOMATED DISPATCH ────────────────────────────────────────────────

        public async Task DispatchAsync(DispatchRequest request)
        {
            if (!request.StudentIds.Any()) return;

            // 1. Find the matching active trigger
            var trigger = await _triggerService.GetMatchingTriggerAsync(
                request.TeacherId, request.EventType,
                request.SessionId, request.SessionGroupId);

            if (trigger is null) return;

            // 2. Load template + blocks
            var template = await _unitOfWork.messageTemplateRepo
                .GetByIdWithBlocksAsync(trigger.MessageTemplateId);

            if (template is null || !template.IsActive) return;

            // 3. Verify channel is connected
            var channel = await _unitOfWork.GetRepository<MessagingChannel, long>()
                .FindAsync(c =>
                    c.TeacherId == request.TeacherId &&
                    c.ChannelType == template.Channel &&
                    c.IsActive);

            if (channel is null) return;

            // ── PHASE 2: THRESHOLD GATE ───────────────────────────────────────
            //
            // For threshold-based events (ConsecutiveAbsenceAlert, GradeBelowThreshold),
            // filter StudentIds to only those who meet the tutor's configured value
            // BEFORE loading student records from the database.
            //
            // Evaluated using PerStudentContext first (bulk absence — each student
            // has their own consecutive count), then SharedContext (single-student
            // events like grade entry where context is the same for all).
            //
            // ThresholdValue null → no gate configured → all students pass through.
            //
            // ConsecutiveNonPayment is intentionally excluded here — it is
            // time-based and will be handled by a scheduled scanning job.
            // ─────────────────────────────────────────────────────────────────
            if (IsThresholdEvent(trigger.EventType) && trigger.ThresholdValue is int threshold)
            {
                request.StudentIds = request.StudentIds
                    .Where(id =>
                    {
                        var ctx = request.PerStudentContext?.GetValueOrDefault(id)
                               ?? request.SharedContext;
                        return MeetsThreshold(trigger.EventType, threshold, ctx);
                    })
                    .ToList();

                // All students filtered out → no message to send.
                if (request.StudentIds.Count == 0) return;
            }

            // 4. Load students in bulk (post-gate — only qualifying students loaded)
            var students = await _unitOfWork.Students
                .GetActiveByIdsAndTeacherAsync(request.TeacherId, request.StudentIds);

            var orderedBlocks = template.Blocks.OrderBy(b => b.SortOrder).ToList();

            // 5. One Hangfire job per student per recipient target (BR-MSG-003)
            foreach (var student in students)
            {
                var ctx = request.PerStudentContext?.GetValueOrDefault(student.Id)
                       ?? request.SharedContext
                       ?? new MessageResolveContext();

                // Dispatcher always overwrites identity fields from the loaded entity
                // so the notifier never needs to supply them.
                ctx.StudentId = student.Id;
                ctx.StudentName = student.StudentName ?? string.Empty;
                ctx.StudentCode = student.StudentCode ?? string.Empty;

                var resolved = _resolver.Resolve(orderedBlocks, ctx);

                // ── Student phone ──────────────────────────────────────────────
                if (template.RecipientTarget is RecipientTarget.Student or RecipientTarget.Both)
                {
                    if (!string.IsNullOrEmpty(student.StudentPhoneNumber))
                    {
                        EnqueueOrSchedule(new MessageSendPayload
                        {
                            TeacherId = request.TeacherId,
                            StudentId = student.Id,
                            StudentName = ctx.StudentName,
                            StudentCode = ctx.StudentCode,
                            RecipientPhone = student.StudentPhoneNumber,
                            RecipientType = RecipientTarget.Student,
                            ResolvedContent = resolved,
                            Channel = template.Channel,
                            MessageTemplateId = template.Id,
                            TriggerType = trigger.EventType,
                            IsManual = false,
                            ScheduledSendAt = BuildScheduledTime(trigger.SendTiming, trigger.ScheduledTime),
                        });
                    }
                    else
                    {
                        await LogMissingPhoneAsync(
                            request.TeacherId, student.Id,
                            RecipientTarget.Student, template.Id, trigger.EventType);
                    }
                }

                // ── Parent phone ───────────────────────────────────────────────
                if (template.RecipientTarget is RecipientTarget.Parent or RecipientTarget.Both)
                {
                    if (!string.IsNullOrEmpty(student.ParentPhoneNumber))
                    {
                        EnqueueOrSchedule(new MessageSendPayload
                        {
                            TeacherId = request.TeacherId,
                            StudentId = student.Id,
                            StudentName = ctx.StudentName,
                            StudentCode = ctx.StudentCode,
                            RecipientPhone = student.ParentPhoneNumber,
                            RecipientType = RecipientTarget.Parent,
                            ResolvedContent = resolved,
                            Channel = template.Channel,
                            MessageTemplateId = template.Id,
                            TriggerType = trigger.EventType,
                            IsManual = false,
                            ScheduledSendAt = BuildScheduledTime(trigger.SendTiming, trigger.ScheduledTime),
                        });
                    }
                    else
                    {
                        await LogMissingPhoneAsync(
                            request.TeacherId, student.Id,
                            RecipientTarget.Parent, template.Id, trigger.EventType);
                    }
                }
            }
        }

        // ── MANUAL DISPATCH ───────────────────────────────────────────────────

        public async Task<Result<ManualSendSummaryDto>> DispatchManualAsync(ManualMessageRequests request)
        {
            var template = await _unitOfWork.messageTemplateRepo
                .GetByIdWithBlocksAsync(request.MessageTemplateId);

            if (template is null || template.TeacherId != request.TeacherId)
                return Result<ManualSendSummaryDto>.Failure(_localizer, "TemplateNotFound");

            var channel = request.ChannelOverride ?? template.Channel;
            var recipientTarget = request.RecipientTargetOverride ?? template.RecipientTarget;

            // Verify channel is active
            var channelEntity = await _unitOfWork.GetRepository<MessagingChannel, long>()
                .FindAsync(c => c.TeacherId == request.TeacherId
                             && c.ChannelType == channel
                             && c.IsActive);

            if (channelEntity is null)
                return Result<ManualSendSummaryDto>.Failure(_localizer, "ChannelNotActiveOrConfigured");

            var students = await _unitOfWork.Students
                .GetActiveByIdsAndTeacherAsync(request.TeacherId, request.StudentIds);

            var orderedBlocks = template.Blocks.OrderBy(b => b.SortOrder).ToList();
            var summary = new ManualSendSummaryDto
            {
                Channels = new List<string> { channel.ToString() }
            };

            // Build preview from first student (REQ-MSG-028)
            var firstStudent = students.FirstOrDefault();
            if (firstStudent is not null)
            {
                var previewCtx = new MessageResolveContext
                {
                    StudentId = firstStudent.Id,
                    StudentName = firstStudent.StudentName ?? string.Empty,
                    StudentCode = firstStudent.StudentCode ?? string.Empty,
                };
                summary.PreviewContent = _resolver.Resolve(orderedBlocks, previewCtx);
            }

            foreach (var student in students)
            {
                var ctx = new MessageResolveContext
                {
                    StudentId = student.Id,
                    StudentName = student.StudentName ?? string.Empty,
                    StudentCode = student.StudentCode ?? string.Empty,
                    Date = DateTime.UtcNow,
                };

                var resolved = _resolver.Resolve(orderedBlocks, ctx);

                // ── Student phone ──────────────────────────────────────────────
                if (recipientTarget is RecipientTarget.Student or RecipientTarget.Both)
                {
                    if (string.IsNullOrWhiteSpace(student.StudentPhoneNumber))
                    {
                        summary.SkippedNoPhone++;
                        await _logService.LogMissingPhoneAsync(
                            request.TeacherId,
                            student.Id,
                            student.StudentName,
                            student.StudentCode,
                            RecipientTarget.Student,
                            template.Id,
                            channel);
                    }
                    else
                    {
                        EnqueueOrSchedule(new MessageSendPayload
                        {
                            TeacherId = request.TeacherId,
                            StudentId = student.Id,
                            StudentName = ctx.StudentName,
                            StudentCode = ctx.StudentCode,
                            RecipientPhone = student.StudentPhoneNumber,
                            RecipientType = RecipientTarget.Student,
                            ResolvedContent = resolved,
                            Channel = channel,
                            MessageTemplateId = template.Id,
                            IsManual = true,
                            ScheduledSendAt = request.ScheduledSendAt
                        });

                        summary.StudentCount++;
                    }
                }

                // ── Parent phone ───────────────────────────────────────────────
                if (recipientTarget is RecipientTarget.Parent or RecipientTarget.Both)
                {
                    if (string.IsNullOrWhiteSpace(student.ParentPhoneNumber))
                    {
                        summary.SkippedNoPhone++;
                        await _logService.LogMissingPhoneAsync(
                            request.TeacherId,
                            student.Id,
                            student.StudentName,
                            student.StudentCode,
                            RecipientTarget.Parent,   // ← was RecipientTarget.Student in original (pre-existing bug, fixed here)
                            template.Id,
                            channel);
                    }
                    else
                    {
                        EnqueueOrSchedule(new MessageSendPayload
                        {
                            TeacherId = request.TeacherId,
                            StudentId = student.Id,
                            StudentName = ctx.StudentName,
                            StudentCode = ctx.StudentCode,
                            RecipientPhone = student.ParentPhoneNumber,
                            RecipientType = RecipientTarget.Parent,
                            ResolvedContent = resolved,
                            Channel = channel,
                            MessageTemplateId = template.Id,
                            IsManual = true,
                            ScheduledSendAt = request.ScheduledSendAt
                        });

                        summary.ParentCount++;
                    }
                }
            }

            summary.TotalRecipients = summary.StudentCount + summary.ParentCount;

            return Result<ManualSendSummaryDto>.Success(summary, _localizer);
        }

        // ── HELPERS ───────────────────────────────────────────────────────────

        /// <summary>
        /// Immediate → BackgroundJob.Enqueue (fires right away — REQ-MSG-034)
        /// Scheduled → BackgroundJob.Schedule (fires at the configured time — REQ-MSG-024)
        /// </summary>
        public static void EnqueueOrSchedule(MessageSendPayload payload)
        {
            if (payload.ScheduledSendAt is null || payload.ScheduledSendAt <= DateTime.UtcNow)
            {
                BackgroundJob.Enqueue<IMessageSenderJob>(job => job.SendAsync(payload));
            }
            else
            {
                var delay = payload.ScheduledSendAt.Value - DateTime.UtcNow;
                BackgroundJob.Schedule<IMessageSenderJob>(job => job.SendAsync(payload), delay);
            }
        }

        /// <summary>
        /// Builds the absolute UTC DateTime to send at, from the trigger's timing config.
        /// Returns null for Immediate triggers (Hangfire enqueues immediately).
        /// </summary>
        private DateTime? BuildScheduledTime(SendTimingType timing, TimeSpan? scheduledTime)
        {
            if (timing == SendTimingType.Immediate || scheduledTime is null)
                return null;

            // The tutor picked this clock time in THEIR zone, so the send hour has to be built
            // in local terms and only then converted to the UTC instant the queue runs on.
            // Adding it to `UtcNow.Date` treated "09:00" as 09:00 UTC — noon in Cairo — so every
            // scheduled message went out 2-3h after the hour the tutor chose.
            var localNow = _timeZoneService.ConvertUtcToLocal(DateTime.UtcNow);
            var localTodayAt = localNow.Date.Add(scheduledTime.Value);

            // Fire at the configured clock time today; push to tomorrow if already past.
            if (localTodayAt <= localNow) localTodayAt = localTodayAt.AddDays(1);
            return _timeZoneService.ConvertLocalToUtc(localTodayAt);
        }

        // ── PHASE 2: THRESHOLD HELPERS ────────────────────────────────────────

        /// <summary>
        /// Returns true when the event type carries a configurable threshold
        /// that must be evaluated per-student before enqueuing.
        /// </summary>
        private static bool IsThresholdEvent(TriggerEventType eventType) =>
            eventType is TriggerEventType.ConsecutiveAbsenceAlert
                      or TriggerEventType.GradeBelowThreshold;
        // ConsecutiveNonPayment is time-based → handled by a scheduled scanning job, not here.

        /// <summary>
        /// Evaluates whether a student's context value meets the tutor-configured threshold.
        /// Returns false when context is null (safe default — do not send).
        /// </summary>
        /// <param name="eventType">The threshold-based event type.</param>
        /// <param name="threshold">The tutor's configured AutomatedTrigger.ThresholdValue.</param>
        /// <param name="ctx">
        /// The student's resolved context. May come from PerStudentContext (bulk absence)
        /// or SharedContext (single-student grade events). Null → false.
        /// </param>
        private static bool MeetsThreshold(
            TriggerEventType eventType, int threshold, MessageResolveContext? ctx)
        {
            if (ctx is null) return false;

            return eventType switch
            {
                // Student's consecutive absence count must be AT OR ABOVE the configured value.
                // AbsenceCount null (context populated without it) → false (safe default).
                TriggerEventType.ConsecutiveAbsenceAlert =>
                    ctx.AbsenceCount.HasValue
                    && ctx.AbsenceCount.Value >= threshold,

                // Student's grade must be STRICTLY BELOW the configured value.
                // Cast threshold (int) to decimal for comparison with ExamGrade (decimal?).
                // ExamGrade null (grade not yet entered) → false (safe default).
                TriggerEventType.GradeBelowThreshold =>
                    ctx.ExamGrade.HasValue
                    && ctx.ExamGrade.Value < (decimal)threshold,

                // Unknown threshold event — pass through rather than silently drop.
                _ => true
            };
        }

        // ── MISSING PHONE LOG ────────────────────────────────────────────────

        private async Task LogMissingPhoneAsync(
            long teacherId, long studentId, RecipientTarget target,
            long templateId, TriggerEventType eventType)
        {
            var log = new MessageLog
            {
                TeacherId = teacherId,
                StudentId = studentId,
                StudentName = string.Empty,
                StudentCode = string.Empty,
                RecipientPhone = string.Empty,
                RecipientType = target,
                MessageTemplateId = templateId,
                ResolvedContent = string.Empty,
                Channel = ChannelType.SMS,
                Status = MessageStatus.Failed,
                FailureReason = "MissingPhoneNumber",   // REQ-MSG-009
                TriggerType = eventType,
                IsManual = false,
                SentAt = DateTime.UtcNow,
                CreateAt = DateTime.UtcNow,
            };

            await _unitOfWork.GetRepository<MessageLog, long>().AddAsync(log);
            await _unitOfWork.SaveChangesAsync();
        }
    }
}
