namespace Edvanz.Domain.Constants;

/// <summary>
/// Constants for the Subscription Management Module (Module 11 — v1.2).
/// Centralizes magic numbers, validation limits, and localization keys
/// so they are compile-time checked and single-sourced.
/// </summary>
public static class SubscriptionConstants
{
   
    // ══════════════════════════════════════════════
    // CONFIGURATION LIMITS
    // ══════════════════════════════════════════════

    /// <summary>
    /// Maximum number of times SubscriptionService.ConfirmPaymentAsync retries on
    /// DbUpdateConcurrencyException or SQL 2601 unique-violation (§6.6).
    /// </summary>
    public const int MaxConcurrencyRetries = 2;

    // Free-tier per-module quotas are configuration-driven — see FreeTierQuotaOptions
    // (appsettings "FreeTierQuotas" section), not hardcoded here.

    /// <summary>
    /// Upper bound for Teacher.StudentCapacity under per-student pricing. Protects the
    /// decimal(10,2) money columns: 100,000 students × even a 3-digit rate stays within
    /// range, whereas the legacy int.MaxValue "unlimited" capacity would overflow.
    /// Capacity-increase requests and the renewal price computation both enforce it.
    /// </summary>
    public const int MaxStudentCapacity = 100_000;

    /// <summary>
    /// Effective capacity granted when an onboarding teacher selects the open-ended
    /// "3000+" StudentCapacityPackage (MaxStudents = null). Replaces the legacy
    /// int.MaxValue mapping, which is incompatible with capacity × rate pricing.
    /// Teachers can raise it further via the capacity-increase request flow.
    /// </summary>
    public const int UnlimitedPackageFallbackCapacity = 3000;

    /// <summary>Upper bound accepted by the admin module-quota endpoint for FreeTierLimit.</summary>
    public const int MaxFreeTierLimit = 10_000;

    /// <summary>
    /// Window size (in days) before EndDate during which the dispatcher fires reminders.
    /// REQ-SUB-005: D-5 through D-0 — six alerts.
    /// </summary>
    public const int AlertWindowDays = 6;

    /// <summary>
    /// EC-24 guard: a pending payment cannot be approved if the teacher's current
    /// subscription was created within this many hours of the pending payment's
    /// InitiatedAt (duplicate-payment heuristic).
    /// </summary>
    public const int DuplicatePaymentGuardHours = 24;

    /// <summary>
    /// Maximum length for the rejection-reason field on PendingSubscriptionPayment.
    /// Mirrors the entity's [MaxLength(500)] mapping.
    /// </summary>
    public const int RejectionReasonMaxLength = 500;

    /// <summary>
    /// Maximum length for FCM tokens. Mirrors the entity's [MaxLength(500)] mapping.
    /// </summary>
    public const int FcmTokenMaxLength = 500;

    // ══════════════════════════════════════════════
    // PHONE MASKING (BR-SUB-011)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Number of leading digits preserved when masking a phone number for tutor display.
    /// "01012341234" → "010****1234" — three leading + four trailing.
    /// </summary>
    public const int PhoneMaskLeadingDigits = 3;

    /// <summary>
    /// Number of trailing digits preserved when masking a phone number for tutor display.
    /// </summary>
    public const int PhoneMaskTrailingDigits = 4;

    // ══════════════════════════════════════════════
    // CACHE KEY FORMAT (§8.7)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Format string for Redis subscription-status cache keys.
    /// Use string.Format(CacheKeyFormat, teacherId).
    /// </summary>
    public const string CacheKeyFormat = "subscription:teacher:{0}";

    // ══════════════════════════════════════════════
    // HANGFIRE QUEUE NAMES (§7.5)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Dedicated queue for subscription notification jobs.
    /// Isolates fan-out from time-critical work on the default queue.
    /// </summary>
    public const string NotificationsQueue = "notifications";

    /// <summary>
    /// Recurring-job id for the daily reminder dispatcher (Hangfire RecurringJob.AddOrUpdate).
    /// </summary>
    public const string ReminderDispatcherJobId = "subscription-reminder-dispatcher";

    /// <summary>
    /// Recurring-job id for the hourly pending-payment expiry job.
    /// </summary>
    public const string PendingPaymentExpiryJobId = "subscription-pending-payment-expiry";

    // ══════════════════════════════════════════════
    // LOCALIZATION KEYS — SUBSCRIPTION MODULE
    // ══════════════════════════════════════════════

    /// <summary>
    /// The localized failure key for a student/parent-ACCOUNT linking attempt blocked by the
    /// teacher's plan. Plain Managerial keeps its historical message; ManagerialPlus gets a
    /// plan-accurate one (that plan DOES include parent follow-up). Callers pass the plan from
    /// <c>SubscriptionPlanEntitlements.PlanType</c> after seeing StudentAccountsAllowed == false.
    /// </summary>
    public static string StudentAccountsBlockedMessageKey(Enums.SubscriptionPlanType? planType) =>
        planType == Enums.SubscriptionPlanType.ManagerialPlus
            ? Messages.PlanNoStudentAccounts
            : Messages.ManagerialSubscriptionNoStudents;

    /// <summary>
    /// Localization message keys. Names match entries in Messages.en.resx / Messages.ar.resx.
    /// </summary>
    public static class Messages
    {
        public const string SubscriptionActivated = "SubscriptionActivated";
        public const string SubscriptionManagerialActivated = "SubscriptionManagerialActivated";
        public const string SubscriptionManagerialPlusActivated = "SubscriptionManagerialPlusActivated";
        // Roster gate: a managerial subscription forbids linking any student/parent account.
        public const string ManagerialSubscriptionNoStudents = "ManagerialSubscriptionNoStudents";
        // Same gate under a ManagerialPlus plan — worded around the plan's contents (the plan DOES
        // include parent follow-up, so the plain managerial text would be wrong/confusing).
        public const string PlanNoStudentAccounts = "PlanNoStudentAccounts";
        // Settings gate: enabling the parent follow-up page requires a plan that includes it.
        public const string ParentPortalRequiresSubscription = "ParentPortalRequiresSubscription";
        public const string SubscriptionExtended = "SubscriptionExtended";
        public const string SubscriptionCancelled = "SubscriptionCancelled";   // ← add
        public const string SubscriptionEndDateUpdated = "SubscriptionEndDateUpdated";
        // ── Generic operation outcomes ──
        public const string Success = "Success";
        public const string TeacherNotFound = "TeacherNotFound";
        public const string UserNotFound = "UserNotFound";

        // ── GetCurrent / GetHistory ──
        public const string NoActiveSubscription = "NoActiveSubscription";

        // ── Free-tier quota block (shared across all quota-limited create paths) ──
        public const string SubscriptionRequired = "SubscriptionRequired";

        // ── Renewal initiate ──
        public const string SubscriptionRenewalInitiated = "SubscriptionRenewalInitiated";
        public const string InvalidPaymentMethod = "InvalidPaymentMethod";
        public const string InvalidPaymentChannel = "InvalidPaymentChannel";
        public const string SuperAdminMethodNotAllowed = "SuperAdminMethodNotAllowed";
        public const string PendingPaymentAlreadyInFlight = "PendingPaymentAlreadyInFlight";

        // ── Per-student pricing (capacity × rate) ──
        public const string PerStudentRateNotConfigured = "PerStudentRateNotConfigured";
        public const string StudentCapacityNotConfigured = "StudentCapacityNotConfigured";
        public const string PricePerStudentUpdated = "PricePerStudentUpdated";
        public const string PricePerStudentMustBePositive = "PricePerStudentMustBePositive";

        // ── Capacity-increase requests ──
        public const string CapacityChangeRequiresApproval = "CapacityChangeRequiresApproval";
        public const string CapacityRequestSubmitted = "CapacityRequestSubmitted";
        public const string CapacityRequestAlreadyPending = "CapacityRequestAlreadyPending";
        public const string CapacityRequestNotFound = "CapacityRequestNotFound";
        public const string CapacityRequestNotPending = "CapacityRequestNotPending";
        public const string CapacityRequestCancelled = "CapacityRequestCancelled";
        public const string CapacityRequestApproved = "CapacityRequestApproved";
        public const string CapacityRequestRejected = "CapacityRequestRejected";
        public const string RequestedCapacityMustExceedCurrent = "RequestedCapacityMustExceedCurrent";
        public const string RequestedCapacityTooLarge = "RequestedCapacityTooLarge";
        /// <summary>Increase-only guard for a LinkedStudents-kind capacity request (student app accounts).</summary>
        public const string LinkedCapacityIncreaseOnly = "LinkedCapacityIncreaseOnly";
        public const string CapacityRequestApprovedTitle = "CapacityRequestApprovedTitle";
        public const string CapacityRequestApprovedBody = "CapacityRequestApprovedBody";
        public const string CapacityRequestRejectedTitle = "CapacityRequestRejectedTitle";
        public const string CapacityRequestRejectedBody = "CapacityRequestRejectedBody";

        // ── New-subscription requests (teacher chooses plan + student count; admin activates) ──
        public const string SubscriptionRequestSubmitted = "SubscriptionRequestSubmitted";
        public const string SubscriptionRequestAlreadyPending = "SubscriptionRequestAlreadyPending";
        public const string SubscriptionRequestNotFound = "SubscriptionRequestNotFound";
        public const string SubscriptionRequestNotPending = "SubscriptionRequestNotPending";
        public const string SubscriptionRequestCancelled = "SubscriptionRequestCancelled";
        public const string SubscriptionRequestApproved = "SubscriptionRequestApproved";
        public const string SubscriptionRequestRejected = "SubscriptionRequestRejected";
        public const string RequestedStudentsRequired = "RequestedStudentsRequired";
        public const string RequestedStudentsTooLarge = "RequestedStudentsTooLarge";

        // ── Student app accounts (Teacher.LinkedStudentCapacity — the PRICED limit) ──
        /// <summary>Bind/accept-and-link refused: every paid student-app-account seat is taken. Formatted with the limit.</summary>
        public const string LinkedStudentCapacityReached = "LinkedStudentCapacityReached";
        /// <summary>
        /// Same refusal, for a CENTER-MANAGED teacher (<see cref="Entities.Teacher.CenterId"/> set).
        /// They have no subscription of their own to upgrade — the center owns the limit — and the
        /// center's screens expose a single "student capacity" field, so the generic wording would
        /// point them at a plan they cannot buy. Formatted with the limit.
        /// </summary>
        public const string LinkedStudentCapacityReachedCenterManaged = "LinkedStudentCapacityReachedCenterManaged";
        /// <summary>Admin set the teacher's student-app-account limit (up or down).</summary>
        public const string LinkedStudentCapacityUpdated = "LinkedStudentCapacityUpdated";
        public const string RequestedLinkedStudentsRequired = "RequestedLinkedStudentsRequired";
        public const string RequestedLinkedStudentsTooLarge = "RequestedLinkedStudentsTooLarge";
        /// <summary>App accounts cannot exceed the number of students in the account.</summary>
        public const string RequestedLinkedStudentsExceedsAccountStudents = "RequestedLinkedStudentsExceedsAccountStudents";
        // Teacher-facing notifications on resolution.
        public const string SubscriptionRequestApprovedTitle = "SubscriptionRequestApprovedTitle";
        public const string SubscriptionRequestApprovedBody = "SubscriptionRequestApprovedBody";
        public const string SubscriptionRequestRejectedTitle = "SubscriptionRequestRejectedTitle";
        public const string SubscriptionRequestRejectedBody = "SubscriptionRequestRejectedBody";
        // SuperAdmin-facing notification when a new request arrives.
        public const string NewSubscriptionRequestTitle = "NewSubscriptionRequestTitle";
        public const string NewSubscriptionRequestBody = "NewSubscriptionRequestBody";

        // ── Backend-driven status/indicator text (GET /api/subscription/status) ──
        public const string SubscriptionStatusActive = "SubscriptionStatusActive";
        public const string SubscriptionStatusExpiringSoon = "SubscriptionStatusExpiringSoon";
        public const string SubscriptionStatusExpired = "SubscriptionStatusExpired";
        public const string SubscriptionStatusNone = "SubscriptionStatusNone";
        public const string SubscriptionStatusRequestPending = "SubscriptionStatusRequestPending";

        // ── Module-quota admin ──
        public const string ModuleQuotaNotFound = "ModuleQuotaNotFound";
        public const string ModuleQuotaUpdated = "ModuleQuotaUpdated";
        public const string FreeTierLimitInvalid = "FreeTierLimitInvalid";

        // ── Manual submit ──
        public const string ManualSubmissionRecorded = "ManualSubmissionRecorded";
        public const string PendingPaymentNotFound = "PendingPaymentNotFound";
        public const string PendingPaymentNotInInitiated = "PendingPaymentNotInInitiated";
        public const string TransactionReferenceRequired = "TransactionReferenceRequired";
        public const string PaymentPhoneRequired = "PaymentPhoneRequired";

        // ── Confirmation flow ──
        public const string PaymentConfirmed = "PaymentConfirmed";
        public const string PaymentAlreadyConfirmed = "PaymentAlreadyConfirmed";
        public const string ConcurrentRenewalDetected = "ConcurrentRenewalDetected";

        // ── Admin pending queue ──
        public const string PendingPaymentApproved = "PendingPaymentApproved";
        public const string PendingPaymentRejected = "PendingPaymentRejected";
        public const string PendingPaymentNotAwaitingApproval = "PendingPaymentNotAwaitingApproval";
        public const string DuplicatePaymentDetected = "DuplicatePaymentDetected";
        public const string RejectionReasonRequired = "RejectionReasonRequired";

        // ── Admin manual overrides ──
       
        public const string SubscriptionNotFound = "SubscriptionNotFound";
        public const string ExtensionDaysMustBePositive = "ExtensionDaysMustBePositive";
        public const string EndDateMustBeAfterStart = "EndDateMustBeAfterStart";
        // (PackagePriceUpdated / PackageNotFound / PriceMustBeNonNegative retired
        // 2026-07-17 with the per-package price endpoint — pricing is per-student now.)

        // ── Notifications inbox ──
        public const string NotificationNotFound = "NotificationNotFound";
        public const string NotificationMarkedRead = "NotificationMarkedRead";
        public const string NotificationsAllMarkedRead = "NotificationsAllMarkedRead";
        public const string FcmTokenRegistered = "FcmTokenRegistered";
        public const string FcmTokenRequired = "FcmTokenRequired";

        // ── Reminder & confirmation templates (FR-SUB-012, §7.6) ──
        public const string SubscriptionReminderTitle = "SubscriptionReminderTitle";
        public const string SubscriptionReminderBodyDaysRemaining = "SubscriptionReminderBodyDaysRemaining";
        public const string SubscriptionReminderBodyExpiresToday = "SubscriptionReminderBodyExpiresToday";
        public const string RenewalConfirmationTitle = "RenewalConfirmationTitle";
        public const string RenewalConfirmationBody = "RenewalConfirmationBody";
        public const string PaymentRejectedTitle = "PaymentRejectedTitle";
        public const string PaymentRejectedBody = "PaymentRejectedBody";

        // ── Manual-pay instructions ──
        public const string ManualPayInstructionsVodafoneCash = "ManualPayInstructionsVodafoneCash";
        public const string ManualPayInstructionsInstaPay = "ManualPayInstructionsInstaPay";
        
        public const string FcmTokenUnregistered = "FcmTokenUnregistered";   // ← add this line

        // (Webhook keys retired 2026-07-17 with the Paymob webhook/gateway removal.)
    }
}