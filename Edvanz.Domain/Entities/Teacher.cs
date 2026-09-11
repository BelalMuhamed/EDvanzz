using Edvanz.Domain.Entities.ShareProp;
using Edvanz.Domain.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace Edvanz.Domain.Entities;

/// <summary>
/// Represents a Teacher account in the system. Links to a User record via UserId.
/// This is the multi-tenant anchor: students, sessions, payments, and all module data
/// are scoped to a Teacher's Id.
/// </summary>
public class Teacher : BaseEntity
{
    /// <summary>
    /// Foreign key to the User table. Establishes 1:1 identity link.
    /// </summary>
    public long UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Unique, immutable, 8-digit code auto-generated upon registration.
    /// AAM-FR-03.3 / AAM-BR-05: Permanent after creation, cannot be changed or regenerated.
    /// AAM-NFR-03: Cryptographically unique and collision-resistant.
    /// </summary>
    public string TeacherCode { get; set; } = null!;

    /// <summary>
    /// Selected student capacity package tier. FK to StudentCapacityPackages lookup table.
    /// AAM-FR-04.1: Determines subscription tier. Nullable until configuration is completed.
    /// </summary>
    [ForeignKey(nameof(StudentCapacityPackage))]
    public long? StudentCapacityPackageId { get; set; }
    public StudentCapacityPackage? StudentCapacityPackage { get; set; }

    /// <summary>
    /// Effective maximum student count for this account.
    /// REQ-ADM-006/009: Set by super admin during creation, adjustable at any time.
    /// Defaults to 500 per REQ-STU-002.
    /// </summary>
    public int StudentCapacity { get; set; } = 500;

    /// <summary>
    /// Maximum number of student APP ACCOUNTS that may be LINKED (bound) to this teacher —
    /// a seat is consumed only by a StudentTeacherLink that is Active AND bound to a
    /// TeacherStudent record. Distinct from <see cref="StudentCapacity"/> (how many student
    /// records may exist in the account, a free operational quota).
    ///
    /// This is the PRICED limit: the renewal fee is LinkedStudentCapacity × the per-student
    /// rate. INVARIANT: LinkedStudentCapacity &lt;= StudentCapacity — a linked account always
    /// needs a student record behind it, so a higher linked limit would be unreachable; every
    /// write path that raises this value raises <see cref="StudentCapacity"/> to match rather
    /// than failing. Defaults to 500, and existing rows were backfilled from
    /// <see cref="StudentCapacity"/> so nobody's bill changed on the deploy that added it.
    /// </summary>
    public int LinkedStudentCapacity { get; set; } = 500;

    /// <summary>
    /// Teacher's preferred UI language. "en" or "ar".
    /// AAM-FR-02.1/02.3: Selected during registration, changeable from settings.
    /// </summary>
    public string? LanguagePreference { get; set; }

    /// <summary>
    /// Free-text subject name for subjects not in the ministry-defined list.
    /// AAM-FR-03.5: Used only when the teacher's subject is not in the Subjects lookup.
    /// </summary>
    public string? CustomSubject { get; set; }

    /// <summary>
    /// Current account status: Active, Inactive, or Suspended.
    /// </summary>
    public AccountStatus AccountStatus { get; set; } = AccountStatus.Active;

    /// <summary>
    /// Indicates whether the teacher has completed the post-registration configuration flow.
    /// AAM-NFR-05: Skippable; system applies defaults. UI uses this to prompt configuration.
    /// </summary>
    public bool IsConfigurationCompleted { get; set; } = false;

    /// <summary>
    /// Timestamp of account deactivation. Null if account is active.
    /// REQ-ADM-017/018: Set when super admin deactivates the account.
    /// </summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>
    /// Soft-delete timestamp. Null if account is not deleted.
    /// REQ-ADM-020 through 024: Data preservation period before permanent removal.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// The user who created this teacher account (typically the super admin).
    /// REQ-ADM-006: Super admin creates tutor accounts.
    /// </summary>
    [ForeignKey(nameof(CreatedByUser))]
    public long? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    // ── Center tenancy tier (multi-teacher account) ─────────────────
    // A null CenterId is a STANDALONE teacher (unchanged behavior); a set CenterId means the
    // teacher is owned/operated by a Center. Center-owned teachers have NO usable login (their
    // User row is created with IsActive = false). FK configured in Fluent API (BUG-4 rule).

    /// <summary>
    /// The <see cref="Center"/> that owns this teacher, or null for a standalone teacher.
    /// </summary>
    public long? CenterId { get; set; }
    public Center? Center { get; set; }

    /// <summary>
    /// Managerial vs Full plan for a CENTER-OWNED teacher. Center-owned teachers have no
    /// <see cref="TeacherSubscription"/> to carry PlanType, so the managerial gate
    /// (SubscriptionGateService.IsManagerialAsync) reads this instead. Null / meaningless for a
    /// standalone teacher (whose plan lives on the current TeacherSubscription).
    /// </summary>
    public SubscriptionPlanType? CenterPlanType { get; set; }

    /// <summary>
    /// Optional per-teacher override of the center's revenue-share percentage. Effective share =
    /// <c>RevenueSharePercentOverride ?? Center.DefaultRevenueSharePercent</c>. Null = use the
    /// center default. Stored as decimal(5,2) via Fluent.
    /// </summary>
    public decimal? RevenueSharePercentOverride { get; set; }

    /// <summary>
    /// Optional per-teacher override of the center's student-code mode (<see cref="Center.StudentCodeGenerationMode"/>).
    /// Null = inherit the center default; set = this teacher diverges (the app surfaces a clear
    /// "overriding center setting" message). Only meaningful for a center-owned teacher.
    /// </summary>
    public GenerationMode? StudentCodeModeOverride { get; set; }

    // ── Sales attribution (admin-only, added 2026-09-11) ────────────
    // Who brought this account in. Before this, attribution lived solely in a disconnected Google
    // Sheet (sales-crm/) with no shared identifier, so no screen could tell which rep's accounts
    // actually went live. Both columns are admin-managed and never exposed to the teacher.

    /// <summary>
    /// The <see cref="SalesRep"/> credited with this account, or null if unattributed (every teacher
    /// registered before this shipped). FK configured in Fluent API (CLAUDE.md §4.1).
    /// </summary>
    public long? SalesRepId { get; set; }
    public SalesRep? SalesRep { get; set; }

    /// <summary>
    /// How the account was acquired — field visit, referral, inbound, and so on. Free text on purpose:
    /// the categories are still settling, and an enum here would need a migration every time sales
    /// coined a new one. Promote it to a lookup once the values stop changing.
    /// </summary>
    public string? AcquisitionSource { get; set; }

    // Navigation properties
    public TeacherConfiguration? Configuration { get; set; }
    public ICollection<TeacherSubject> TeacherSubjects { get; set; } = new List<TeacherSubject>();
    public ICollection<TeacherSubscription> Subscriptions { get; set; } = new List<TeacherSubscription>();

    public virtual ICollection<TutorModule> OpenModules { get; set; } = new List<TutorModule>();


    // ── Subscription Management Module navigation properties ────────

    /// <summary>
    /// All pending subscription-renewal payments initiated by this teacher.
    /// Includes historical (Confirmed/Rejected/Expired) and in-flight (Initiated/AwaitingSuperAdminApproval).
    /// </summary>
    public ICollection<PendingSubscriptionPayment> PendingSubscriptionPayments { get; set; }
        = new List<PendingSubscriptionPayment>();

    /// <summary>
    /// All subscription alert records (idempotency log) for this teacher.
    /// </summary>
    public ICollection<SubscriptionAlert> SubscriptionAlerts { get; set; }
        = new List<SubscriptionAlert>();


}