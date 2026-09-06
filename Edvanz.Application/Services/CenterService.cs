using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Center;
using Edvanz.Application.Dtos.Teacher;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Edvanz.Domain.ServiceContract;
using Microsoft.Extensions.Localization;
using System.Net;

namespace Edvanz.Application.Services;

/// <summary>
/// Center self-service. Teacher PROFILES are created with NO usable login (their identity
/// <see cref="User"/> row is IsActive=false with a random unguessable password), then initialized
/// through the SHARED <see cref="ITeacherService.InitializeTeacherAsync"/> so a center-owned teacher
/// is identical to a standalone one everywhere the app scopes on Teacher.Id. Slot/pool quotas are
/// enforced against the center's current subscription when one exists.
/// </summary>
public class CenterService : ICenterService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly IPasswordService _passwordService;
    private readonly ITeacherService _teacherService;
    private readonly IservicesContract.ISubscriptionCacheService _subscriptionCache;
    private readonly IservicesContract.IUserAuthInvalidationService _authInvalidation;
    private readonly ITimeZoneService _timeZone;

    public CenterService(
        IUnitOfWork unitOfWork,
        IStringLocalizer<Messages> localizer,
        IPasswordService passwordService,
        ITeacherService teacherService,
        IservicesContract.ISubscriptionCacheService subscriptionCache,
        IservicesContract.IUserAuthInvalidationService authInvalidation,
        ITimeZoneService timeZone)
    {
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _passwordService = passwordService;
        _teacherService = teacherService;
        _subscriptionCache = subscriptionCache;
        _authInvalidation = authInvalidation;
        _timeZone = timeZone;
    }

    /// <summary>
    /// The teacher-slot entitlement for one plan: the current subscription's slot count, else the
    /// free-tier trial constants. THE three-way plan switch for slot quotas — create and plan-flip
    /// both go through it so a new plan type can never be enforced in one path and missed in the other.
    /// </summary>
    private static int SlotsForPlan(CenterSubscription? sub, SubscriptionPlanType plan) => plan switch
    {
        SubscriptionPlanType.Managerial =>
            sub?.ManagerialTeacherSlots ?? CenterConstants.FreeTierManagerialTeacherSlots,
        SubscriptionPlanType.ManagerialPlus =>
            sub?.ManagerialPlusTeacherSlots ?? CenterConstants.FreeTierManagerialPlusTeacherSlots,
        _ => sub?.FullTeacherSlots ?? CenterConstants.FreeTierFullTeacherSlots
    };

    /// <inheritdoc />
    public async Task<Result<CenterOverviewDto>> GetOverviewAsync(long centerId)
    {
        var center = await _unitOfWork.Centers.GetCenterByIdAsync(centerId);
        if (center == null)
            return Result<CenterOverviewDto>.Failure(_localizer, "CenterNotFound", HttpStatusCode.NotFound);

        var full = await _unitOfWork.Centers.CountActiveTeachersByPlanAsync(centerId, SubscriptionPlanType.Full);
        var managerial = await _unitOfWork.Centers.CountActiveTeachersByPlanAsync(centerId, SubscriptionPlanType.Managerial);
        var managerialPlus = await _unitOfWork.Centers.CountActiveTeachersByPlanAsync(centerId, SubscriptionPlanType.ManagerialPlus);
        var students = await _unitOfWork.Centers.CountCenterStudentsTotalAsync(centerId);
        var sub = await _unitOfWork.Centers.GetCurrentCenterSubscriptionAsync(centerId);

        var dto = new CenterOverviewDto
        {
            CenterId = center.Id,
            Name = center.Name,
            CenterCode = center.CenterCode,
            DefaultRevenueSharePercent = center.DefaultRevenueSharePercent,
            TeacherCount = full + managerial + managerialPlus,
            FullTeacherCount = full,
            ManagerialTeacherCount = managerial,
            ManagerialPlusTeacherCount = managerialPlus,
            StudentCount = students,
            HasActiveSubscription = sub != null && sub.EndDate > DateTime.UtcNow,
            FullTeacherSlots = sub?.FullTeacherSlots,
            ManagerialTeacherSlots = sub?.ManagerialTeacherSlots,
            ManagerialPlusTeacherSlots = sub?.ManagerialPlusTeacherSlots,
            StudentCapacityTotal = sub?.StudentCapacityTotal,
            StudentCapacityUnderFull = sub?.StudentCapacityUnderFull,
            StudentCapacityUnderManagerial = sub?.StudentCapacityUnderManagerial,
            StudentCapacityUnderManagerialPlus = sub?.StudentCapacityUnderManagerialPlus,
            SubscriptionEndDate = sub?.EndDate
        };
        return Result<CenterOverviewDto>.Success(dto, _localizer, "Success");
    }

    /// <inheritdoc />
    public async Task<Result<CenterSettingsDto>> GetSettingsAsync(long centerId)
    {
        var center = await _unitOfWork.Centers.GetCenterByIdAsync(centerId);
        if (center == null)
            return Result<CenterSettingsDto>.Failure(_localizer, "CenterNotFound", HttpStatusCode.NotFound);

        var dto = await BuildSettingsDtoAsync(center);
        return Result<CenterSettingsDto>.Success(dto, _localizer, "Success");
    }

    /// <inheritdoc />
    public async Task<Result<CenterSettingsDto>> UpdateSettingsAsync(long centerId, UpdateCenterSettingsDto dto)
    {
        var center = await _unitOfWork.Centers.GetCenterByIdAsync(centerId);
        if (center == null)
            return Result<CenterSettingsDto>.Failure(_localizer, "CenterNotFound", HttpStatusCode.NotFound);
        if (dto.DefaultRevenueSharePercent is < 0 or > 100)
            return Result<CenterSettingsDto>.Failure(_localizer, "InvalidRevenueSharePercent", HttpStatusCode.BadRequest);

        // Validate the config block up front (same rules as the teacher settings save). It is a DEFAULT
        // template — no students hang off it — so a center-config save never re-prices anyone.
        if (dto.Configuration is not null)
        {
            var validationKey = ValidateConfigTiers(dto.Configuration);
            if (validationKey is not null)
                return Result<CenterSettingsDto>.Failure(_localizer, validationKey, HttpStatusCode.BadRequest);
        }

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(dto.Name)) center.Name = dto.Name.Trim();
            if (dto.DefaultRevenueSharePercent.HasValue) center.DefaultRevenueSharePercent = dto.DefaultRevenueSharePercent.Value;
            if (dto.StudentCodeGenerationMode.HasValue) center.StudentCodeGenerationMode = dto.StudentCodeGenerationMode.Value;

            if (dto.Configuration is not null)
                await SaveCenterConfigInTransactionAsync(center, dto.Configuration);

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();

            var resultDto = await BuildSettingsDtoAsync(center);
            return Result<CenterSettingsDto>.Success(resultDto, _localizer, "CenterSettingsUpdated");
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            return Result<CenterSettingsDto>.Failure(_localizer, "ServerError");
        }
    }

    /// <inheritdoc />
    public async Task<Result<ApplyCenterConfigResultDto>> ApplyConfigToAllTeachersAsync(long centerId)
    {
        var center = await _unitOfWork.Centers.GetCenterByIdAsync(centerId);
        if (center == null)
            return Result<ApplyCenterConfigResultDto>.Failure(_localizer, "CenterNotFound", HttpStatusCode.NotFound);

        // Snapshot the center's template into a teacher update DTO (never carries a capacity package).
        var config = await EnsureCenterConfigAsync(center);
        var tiers = await _unitOfWork.Centers.GetProratedTiersByConfigIdAsync(config.Id);
        var templateDto = MapConfigToUpdateDto(config, tiers, center.StudentCodeGenerationMode);

        // ALL non-deleted teachers (active + inactive) owned by the center.
        var teacherIds = await _unitOfWork.Centers.GetTeacherIdsByCenterAsync(centerId);

        int updated = 0;
        foreach (var teacherId in teacherIds)
        {
            // Reuse the EXACT single-teacher settings save so behavior is IDENTICAL — including the
            // proration reconcile that re-prices existing students when the proration config changed.
            // Each call owns its own transaction, so this is literally N independent single-teacher
            // saves (idempotent: re-running writes the same values). A teacher whose save can't apply
            // (e.g. missing config row) is skipped and not counted; infra failures still throw.
            var result = await _teacherService.SaveConfigurationAsync(teacherId, templateDto);
            if (result.IsSuccess) updated++;
        }

        return Result<ApplyCenterConfigResultDto>.Success(
            new ApplyCenterConfigResultDto { UpdatedTeacherCount = updated },
            _localizer, "CenterConfigAppliedToAllTeachers");
    }

    /// <summary>Builds the full center settings DTO (business fields + the config block), lazy-creating
    /// the config if needed. The config block's StudentCodeGenerationMode is projected from the
    /// authoritative <see cref="Center.StudentCodeGenerationMode"/> so the two never disagree.</summary>
    private async Task<CenterSettingsDto> BuildSettingsDtoAsync(Center center)
    {
        var config = await EnsureCenterConfigAsync(center);
        var tiers = await _unitOfWork.Centers.GetProratedTiersByConfigIdAsync(config.Id);
        return new CenterSettingsDto
        {
            CenterId = center.Id,
            Name = center.Name,
            CenterCode = center.CenterCode,
            DefaultRevenueSharePercent = center.DefaultRevenueSharePercent,
            StudentCodeGenerationMode = center.StudentCodeGenerationMode,
            Configuration = MapConfigToDto(config, tiers, center.StudentCodeGenerationMode)
        };
    }

    /// <summary>Persists the center config block INSIDE the caller's transaction (full replace incl.
    /// prorated tiers). Keeps the stored code-mode mirror in sync with the authoritative
    /// <see cref="Center.StudentCodeGenerationMode"/> (edited via the top-level card).</summary>
    private async Task SaveCenterConfigInTransactionAsync(Center center, UpdateTeacherConfigurationDto dto)
    {
        var config = await EnsureCenterConfigAsync(center);

        config.StudentCodeGenerationMode = center.StudentCodeGenerationMode; // mirror stays synced
        config.StudentCodeLanguage = dto.StudentCodeLanguage;
        config.SessionNameMode = dto.SessionNameMode;
        config.SessionNameLanguage = dto.SessionNameLanguage;
        config.IsProratedPaymentEnabled = dto.IsProratedPaymentEnabled;
        config.ConsecutiveAbsenceThreshold = dto.ConsecutiveAbsenceThreshold;
        config.ConsecutiveUnpaidThreshold = dto.ConsecutiveUnpaidThreshold;
        config.BarcodeDisplayMode = dto.BarcodeDisplayMode;
        config.StudentVisibilityAttendance = dto.StudentVisibilityAttendance;
        config.StudentVisibilityPayment = dto.StudentVisibilityPayment;
        config.StudentVisibilityHomework = dto.StudentVisibilityHomework;
        config.StudentVisibilityExamDefault = dto.StudentVisibilityExamDefault;
        config.StudentVisibilityVideo = dto.StudentVisibilityVideo;
        config.ParentVisibilityAttendance = dto.ParentVisibilityAttendance;
        config.ParentVisibilityPayment = dto.ParentVisibilityPayment;
        config.ParentVisibilityHomework = dto.ParentVisibilityHomework;
        config.ParentVisibilityExamDefault = dto.ParentVisibilityExamDefault;
        config.IsDeviceLockEnabled = dto.IsDeviceLockEnabled;
        config.ShowPaymentInfoOnAttendanceScreen = dto.ShowPaymentInfoOnAttendanceScreen;
        config.ShowAttendanceHistoryOnAttendanceScreen = dto.ShowAttendanceHistoryOnAttendanceScreen;
        config.UpdatedAt = DateTime.UtcNow;
        await _unitOfWork.Centers.UpdateConfigurationAsync(config);

        // Replace tiers (delete existing, add new) — same pattern as the teacher save.
        var existing = await _unitOfWork.Centers.GetProratedTiersByConfigIdAsync(config.Id);
        if (existing.Any())
            await _unitOfWork.Centers.DeleteProratedTiersAsync(existing);

        var newTiers = dto.ProratedTiers.Select(t => new CenterProratedTier
        {
            CenterConfigurationId = config.Id,
            TierNumber = t.TierNumber,
            ThresholdDayStart = t.ThresholdDayStart,
            ThresholdDayEnd = t.ThresholdDayEnd,
            FractionRate = t.FractionRate,
            CreateAt = DateTime.UtcNow
        }).ToList();
        if (newTiers.Count > 0)
            await _unitOfWork.Centers.AddProratedTiersAsync(newTiers);
    }

    /// <summary>Lazy-loads (creating with system defaults + the 3 default prorated tiers if missing) the
    /// center's configuration — a safety net for centers created before the backfill migration or via a
    /// path that didn't seed one. Persists via SaveChanges (joins the caller's transaction when one is
    /// active); the caller still owns the commit. The initial code-mode mirrors the authoritative
    /// <see cref="Center.StudentCodeGenerationMode"/>.</summary>
    private async Task<CenterConfiguration> EnsureCenterConfigAsync(Center center)
    {
        var config = await _unitOfWork.Centers.GetConfigurationByCenterIdAsync(center.Id);
        if (config is not null) return config;

        config = new CenterConfiguration
        {
            CenterId = center.Id,
            StudentCodeGenerationMode = center.StudentCodeGenerationMode,
            StudentCodeLanguage = GenerationLanguage.English,
            SessionNameMode = GenerationMode.Auto,
            SessionNameLanguage = GenerationLanguage.English,
            IsProratedPaymentEnabled = false,
            ConsecutiveAbsenceThreshold = 3,
            ConsecutiveUnpaidThreshold = 3,
            BarcodeDisplayMode = BarcodeDisplayMode.InApp,
            StudentVisibilityAttendance = true,
            StudentVisibilityPayment = true,
            StudentVisibilityHomework = true,
            StudentVisibilityExamDefault = true,
            StudentVisibilityOnlineExamDefault = true,
            StudentVisibilityVideo = true,
            ParentVisibilityAttendance = true,
            ParentVisibilityPayment = true,
            ParentVisibilityHomework = true,
            ParentVisibilityExamDefault = false,
            ParentVisibilityOnlineExamDefault = false,
            ParentVisibilityVideo = true,
            IsDeviceLockEnabled = false,
            ShowPaymentInfoOnAttendanceScreen = true,
            ShowAttendanceHistoryOnAttendanceScreen = true,
            CreateAt = DateTime.UtcNow
        };
        await _unitOfWork.Centers.AddConfigurationAsync(config);
        await _unitOfWork.SaveChangesAsync();

        var defaultTiers = new List<CenterProratedTier>
        {
            new() { CenterConfigurationId = config.Id, TierNumber = 1, ThresholdDayStart = 1, ThresholdDayEnd = 10, FractionRate = 1.0000m, CreateAt = DateTime.UtcNow },
            new() { CenterConfigurationId = config.Id, TierNumber = 2, ThresholdDayStart = 11, ThresholdDayEnd = 20, FractionRate = 0.6667m, CreateAt = DateTime.UtcNow },
            new() { CenterConfigurationId = config.Id, TierNumber = 3, ThresholdDayStart = 21, ThresholdDayEnd = 31, FractionRate = 0.3333m, CreateAt = DateTime.UtcNow }
        };
        await _unitOfWork.Centers.AddProratedTiersAsync(defaultTiers);
        await _unitOfWork.SaveChangesAsync();

        return config;
    }

    /// <summary>Validates the config's prorated tiers exactly like TeacherService.SaveConfigurationAsync
    /// (fraction in (0,1], day range 1..31 with start ≤ end, no overlap, ≤3 tiers). Returns the resx key
    /// of the first failure, or null when valid.</summary>
    private static string? ValidateConfigTiers(UpdateTeacherConfigurationDto dto)
    {
        if (dto.IsProratedPaymentEnabled && dto.ProratedTiers.Count == 0)
            return "ProratedTiersRequired";
        if (dto.ProratedTiers.Count > 3)
            return "MaxThreeProratedTiers";
        if (dto.IsProratedPaymentEnabled && dto.ProratedTiers.Count > 0)
        {
            if (dto.ProratedTiers.Any(t => t.FractionRate <= 0m || t.FractionRate > 1m))
                return "ProratedTierFractionInvalid";
            if (dto.ProratedTiers.Any(t => t.ThresholdDayStart < 1 || t.ThresholdDayEnd > 31 || t.ThresholdDayStart > t.ThresholdDayEnd))
                return "ProratedTierDayRangeInvalid";
            var ordered = dto.ProratedTiers.OrderBy(t => t.ThresholdDayStart).ToList();
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].ThresholdDayStart <= ordered[i - 1].ThresholdDayEnd)
                    return "ProratedTiersOverlap";
        }
        return null;
    }

    /// <summary>Maps a center config (+tiers) to the teacher OUTPUT DTO (reused for FE parity). The
    /// code-mode is projected from the authoritative Center value, not the stored mirror.</summary>
    private static TeacherConfigurationDto MapConfigToDto(
        CenterConfiguration config, IReadOnlyList<CenterProratedTier> tiers, GenerationMode codeMode) => new()
    {
        Id = config.Id,
        TeacherId = 0, // reused teacher DTO; the center id lives on the parent CenterSettingsDto
        StudentCodeGenerationMode = codeMode,
        StudentCodeLanguage = config.StudentCodeLanguage,
        SessionNameMode = config.SessionNameMode,
        SessionNameLanguage = config.SessionNameLanguage,
        IsProratedPaymentEnabled = config.IsProratedPaymentEnabled,
        ConsecutiveAbsenceThreshold = config.ConsecutiveAbsenceThreshold,
        ConsecutiveUnpaidThreshold = config.ConsecutiveUnpaidThreshold,
        BarcodeDisplayMode = config.BarcodeDisplayMode,
        StudentVisibilityAttendance = config.StudentVisibilityAttendance,
        StudentVisibilityPayment = config.StudentVisibilityPayment,
        StudentVisibilityHomework = config.StudentVisibilityHomework,
        StudentVisibilityExamDefault = config.StudentVisibilityExamDefault,
        StudentVisibilityVideo = config.StudentVisibilityVideo,
        ParentVisibilityAttendance = config.ParentVisibilityAttendance,
        ParentVisibilityPayment = config.ParentVisibilityPayment,
        ParentVisibilityHomework = config.ParentVisibilityHomework,
        ParentVisibilityExamDefault = config.ParentVisibilityExamDefault,
        IsDeviceLockEnabled = config.IsDeviceLockEnabled,
        ShowPaymentInfoOnAttendanceScreen = config.ShowPaymentInfoOnAttendanceScreen,
        ShowAttendanceHistoryOnAttendanceScreen = config.ShowAttendanceHistoryOnAttendanceScreen,
        UpdatedAt = config.UpdatedAt,
        ProratedTiers = tiers.OrderBy(t => t.TierNumber).Select(t => new ProratedTierDto
        {
            TierNumber = t.TierNumber,
            ThresholdDayStart = t.ThresholdDayStart,
            ThresholdDayEnd = t.ThresholdDayEnd,
            FractionRate = t.FractionRate
        }).ToList()
    };

    /// <summary>Maps a center config (+tiers) to the teacher UPDATE DTO used by "apply to all". Never
    /// carries a StudentCapacityPackageId — a teacher's capacity/subscription is untouched.</summary>
    private static UpdateTeacherConfigurationDto MapConfigToUpdateDto(
        CenterConfiguration config, IReadOnlyList<CenterProratedTier> tiers, GenerationMode codeMode) => new()
    {
        StudentCapacityPackageId = null,
        StudentCodeGenerationMode = codeMode,
        StudentCodeLanguage = config.StudentCodeLanguage,
        SessionNameMode = config.SessionNameMode,
        SessionNameLanguage = config.SessionNameLanguage,
        IsProratedPaymentEnabled = config.IsProratedPaymentEnabled,
        ConsecutiveAbsenceThreshold = config.ConsecutiveAbsenceThreshold,
        ConsecutiveUnpaidThreshold = config.ConsecutiveUnpaidThreshold,
        BarcodeDisplayMode = config.BarcodeDisplayMode,
        StudentVisibilityAttendance = config.StudentVisibilityAttendance,
        StudentVisibilityPayment = config.StudentVisibilityPayment,
        StudentVisibilityHomework = config.StudentVisibilityHomework,
        StudentVisibilityExamDefault = config.StudentVisibilityExamDefault,
        StudentVisibilityVideo = config.StudentVisibilityVideo,
        ParentVisibilityAttendance = config.ParentVisibilityAttendance,
        ParentVisibilityPayment = config.ParentVisibilityPayment,
        ParentVisibilityHomework = config.ParentVisibilityHomework,
        ParentVisibilityExamDefault = config.ParentVisibilityExamDefault,
        IsDeviceLockEnabled = config.IsDeviceLockEnabled,
        ShowPaymentInfoOnAttendanceScreen = config.ShowPaymentInfoOnAttendanceScreen,
        ShowAttendanceHistoryOnAttendanceScreen = config.ShowAttendanceHistoryOnAttendanceScreen,
        ProratedTiers = tiers.OrderBy(t => t.TierNumber).Select(t => new ProratedTierDto
        {
            TierNumber = t.TierNumber,
            ThresholdDayStart = t.ThresholdDayStart,
            ThresholdDayEnd = t.ThresholdDayEnd,
            FractionRate = t.FractionRate
        }).ToList()
    };

    /// <inheritdoc />
    public async Task<Result<List<CenterTeacherListItemDto>>> GetTeachersAsync(long centerId)
    {
        var center = await _unitOfWork.Centers.GetCenterByIdAsync(centerId);
        if (center == null)
            return Result<List<CenterTeacherListItemDto>>.Failure(_localizer, "CenterNotFound", HttpStatusCode.NotFound);

        var teachers = await _unitOfWork.Centers.GetTeachersByCenterAsync(centerId);
        var counts = await _unitOfWork.Centers.GetStudentCountsByCenterTeachersAsync(centerId);

        var list = teachers.Select(t => ToTeacherItem(t, center.DefaultRevenueSharePercent,
            center.StudentCodeGenerationMode, counts.TryGetValue(t.Id, out var c) ? c : 0)).ToList();

        return Result<List<CenterTeacherListItemDto>>.Success(list, _localizer, "Success");
    }

    /// <inheritdoc />
    public async Task<Result<CenterTeacherListItemDto>> CreateTeacherAsync(long centerId, long actingUserId, CreateCenterTeacherDto dto)
    {
        var center = await _unitOfWork.Centers.GetCenterByIdAsync(centerId);
        if (center == null)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "CenterNotFound", HttpStatusCode.NotFound);

        if (string.IsNullOrWhiteSpace(dto.FullName))
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "TeacherNameRequired");
        if ((dto.SubjectIds == null || dto.SubjectIds.Count == 0) && string.IsNullOrWhiteSpace(dto.CustomSubject))
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "SubjectRequired");
        if (dto.RevenueSharePercentOverride is < 0 or > 100)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "InvalidRevenueSharePercent");

        // Teacher-slot quota — ALWAYS enforced. With an active subscription the cap is the entitlement;
        // WITHOUT one the center gets a small free-tier trial (mirrors the teacher free-tier), so it is
        // never unlimited. Each created teacher's students then fall under the normal per-teacher
        // free-tier limit automatically (they have no active subscription).
        var sub = await _unitOfWork.Centers.GetCurrentCenterSubscriptionAsync(centerId);
        var used = await _unitOfWork.Centers.CountActiveTeachersByPlanAsync(centerId, dto.PlanType);
        var slots = SlotsForPlan(sub, dto.PlanType);
        if (used >= slots)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "CenterTeacherSlotExhausted", HttpStatusCode.Conflict);

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            // Identity row for a center-owned teacher: NO usable login (IsActive=false + random hash),
            // a system-generated unique username (never used to sign in).
            var user = new User
            {
                UserType = UserType.Teacher,
                FullName = dto.FullName.Trim(),
                Username = $"ct_{center.CenterCode}_{Guid.NewGuid():N}".Substring(0, 24),
                PasswordHashed = _passwordService.HashPassword(Guid.NewGuid().ToString("N")),
                IsActive = false,
                CreateAt = DateTime.UtcNow,
                CreateByUserId = actingUserId
            };
            await _unitOfWork.Users.AddAsync(user);
            await _unitOfWork.SaveChangesAsync();

            var initResult = await _teacherService.InitializeTeacherAsync(new CreateTeacherDto
            {
                UserId = user.Id,
                SubjectIds = dto.SubjectIds ?? new List<long>(),
                CustomSubject = dto.CustomSubject,
                LanguagePreference = dto.LanguagePreference,
                CreatedByUserId = actingUserId,
                StudentCapacity = dto.StudentCapacity
            });
            if (!initResult.IsSuccess)
            {
                await _unitOfWork.RollbackAsync();
                return Result<CenterTeacherListItemDto>.Failure(_localizer, initResult.Message);
            }

            var teacher = await _unitOfWork.Users.GetTeacherByUserIdAsync(user.Id);
            if (teacher == null)
            {
                await _unitOfWork.RollbackAsync();
                return Result<CenterTeacherListItemDto>.Failure(_localizer, "ServerError");
            }

            teacher.CenterId = centerId;
            teacher.CenterPlanType = dto.PlanType;
            teacher.RevenueSharePercentOverride = dto.RevenueSharePercentOverride;
            teacher.StudentCodeModeOverride = dto.StudentCodeModeOverride;
            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();

            return Result<CenterTeacherListItemDto>.Success(
                ToTeacherItem(teacher, center.DefaultRevenueSharePercent, center.StudentCodeGenerationMode, 0, user.FullName),
                _localizer, "CenterTeacherCreated");
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "ServerError");
        }
    }

    /// <inheritdoc />
    public async Task<Result<CenterTeacherListItemDto>> UpdateTeacherAsync(long centerId, long teacherId, UpdateCenterTeacherDto dto)
    {
        if (!await _unitOfWork.Centers.IsTeacherInCenterAsync(centerId, teacherId))
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);
        if (dto.RevenueSharePercentOverride is < 0 or > 100)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "InvalidRevenueSharePercent");

        var center = await _unitOfWork.Centers.GetCenterByIdAsync(centerId);
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher == null || center == null)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        // A plan-type FLIP consumes a slot of the target plan — enforce the same quota as
        // CreateTeacherAsync (free-tier fallback included), otherwise a center with 0 Full slots
        // could create a Managerial teacher and edit it to Full, bypassing the package entirely.
        if (dto.PlanType.HasValue && dto.PlanType.Value != teacher.CenterPlanType)
        {
            var sub = await _unitOfWork.Centers.GetCurrentCenterSubscriptionAsync(centerId);
            var used = await _unitOfWork.Centers.CountActiveTeachersByPlanAsync(centerId, dto.PlanType.Value);
            var slots = SlotsForPlan(sub, dto.PlanType.Value);
            if (used >= slots)
                return Result<CenterTeacherListItemDto>.Failure(_localizer, "CenterTeacherSlotExhausted", HttpStatusCode.Conflict);
        }

        var user = await _unitOfWork.Users.GetUserByIdAsync(teacher.UserId);

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(dto.FullName) && user != null)
                user.FullName = dto.FullName.Trim();
            if (dto.PlanType.HasValue) teacher.CenterPlanType = dto.PlanType.Value;
            if (dto.StudentCapacity.HasValue)
            {
                // A center manages ONE capacity number per teacher, and center-owned teachers are
                // covered by the center's own subscription (their per-teacher limits are quota,
                // not price). So the edited number governs BOTH limits — otherwise a teacher the
                // center raised to 2,000 would stay blocked at their old student-app-account limit
                // with no knob anywhere in the center UI to fix it.
                teacher.StudentCapacity = dto.StudentCapacity.Value;
                teacher.LinkedStudentCapacity = dto.StudentCapacity.Value;
                // A no-op while both are assigned the same number, but every write path that
                // touches LinkedStudentCapacity runs the shared rule so the invariant survives
                // any future edit to the two lines above.
                TeacherCapacityRules.EnforceInvariant(teacher);
            }
            // Overrides are set UNCONDITIONALLY (including to null) so the edit form can clear an
            // override back to "inherit center default" — the form always sends the intended value.
            teacher.RevenueSharePercentOverride = dto.RevenueSharePercentOverride;
            teacher.StudentCodeModeOverride = dto.StudentCodeModeOverride;

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();

            // A plan-type change alters this teacher's effective (center-redirected) subscription
            // projection — cached by teacherId — so the managerial/active gate re-reads it.
            if (dto.PlanType.HasValue)
            {
                try { await _subscriptionCache.InvalidateAsync(teacher.Id); } catch { /* cache best-effort */ }
            }

            var counts = await _unitOfWork.Centers.GetStudentCountsByCenterTeachersAsync(centerId);
            return Result<CenterTeacherListItemDto>.Success(
                ToTeacherItem(teacher, center.DefaultRevenueSharePercent, center.StudentCodeGenerationMode,
                    counts.TryGetValue(teacher.Id, out var c) ? c : 0, user?.FullName),
                _localizer, "CenterTeacherUpdated");
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "ServerError");
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> DeactivateTeacherAsync(long centerId, long teacherId)
    {
        if (!await _unitOfWork.Centers.IsTeacherInCenterAsync(centerId, teacherId))
            return Result<string>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher == null)
            return Result<string>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        var user = await _unitOfWork.Users.GetUserByIdAsync(teacher.UserId);

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            teacher.AccountStatus = AccountStatus.Inactive;
            teacher.DeactivatedAt = DateTime.UtcNow;
            // Suspending the teacher also blocks their login (if enabled) — a deactivated teacher
            // must not be able to sign in. Reactivation does NOT auto-re-enable login; the center
            // re-enables it explicitly (fail-closed).
            if (user != null && user.IsActive == true)
                await DisableLoginInTransactionAsync(user);
            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
            return Result<string>.Success("ok", _localizer, "CenterTeacherDeactivated");
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            return Result<string>.Failure(_localizer, "ServerError");
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> ReactivateTeacherAsync(long centerId, long teacherId)
    {
        if (!await _unitOfWork.Centers.IsTeacherInCenterAsync(centerId, teacherId))
            return Result<string>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher == null)
            return Result<string>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            teacher.AccountStatus = AccountStatus.Active;
            teacher.DeactivatedAt = null;
            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
            return Result<string>.Success("ok", _localizer, "Success");
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            return Result<string>.Failure(_localizer, "ServerError");
        }
    }

    /// <inheritdoc />
    public async Task<Result<CenterTeacherListItemDto>> EnableTeacherLoginAsync(long centerId, long teacherId, EnableCenterTeacherLoginDto dto)
    {
        var username = dto.Username?.Trim() ?? string.Empty;
        if (username.Length < 4)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "CenterTeacherUsernameInvalid");
        if (string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 8)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "PasswordTooShort");

        if (!await _unitOfWork.Centers.IsTeacherInCenterAsync(centerId, teacherId))
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        var center = await _unitOfWork.Centers.GetCenterByIdAsync(centerId);
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher == null || center == null)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        var user = await _unitOfWork.Users.GetUserByIdAsync(teacher.UserId);
        if (user == null)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "ServerError");

        // Username must be globally unique (login identity). Allow re-pointing the SAME teacher's
        // username; reject a value already taken by another account.
        var existing = await _unitOfWork.Users.GetByUserName(username);
        if (existing != null && existing.Id != user.Id)
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "CenterTeacherUsernameTaken", HttpStatusCode.Conflict);

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            user.Username = username;
            user.PasswordHashed = _passwordService.HashPassword(dto.Password);
            user.IsActive = true;
            user.SecurityStamp = Guid.NewGuid().ToString();
            // Snapshot invalidation joins this transaction (BEFORE SaveChanges) — the IsActive flip
            // must be visible to SecurityStampValidationMiddleware on the teacher's first request (§5.1).
            await _authInvalidation.InvalidateUserAsync(user.Id);
            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();

            var counts = await _unitOfWork.Centers.GetStudentCountsByCenterTeachersAsync(centerId);
            return Result<CenterTeacherListItemDto>.Success(
                ToTeacherItem(teacher, center.DefaultRevenueSharePercent, center.StudentCodeGenerationMode,
                    counts.TryGetValue(teacher.Id, out var c) ? c : 0, user.FullName),
                _localizer, "CenterTeacherLoginEnabled");
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            return Result<CenterTeacherListItemDto>.Failure(_localizer, "ServerError");
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> ResetTeacherPasswordAsync(long centerId, long teacherId, ResetCenterTeacherPasswordDto dto)
    {
        if (!string.Equals(dto.NewPassword, dto.ConfirmPassword, StringComparison.Ordinal))
            return Result<string>.Failure(_localizer, "PasswordConfirmationMismatch");
        if (string.IsNullOrWhiteSpace(dto.NewPassword) || dto.NewPassword.Length < 8)
            return Result<string>.Failure(_localizer, "PasswordTooShort");

        if (!await _unitOfWork.Centers.IsTeacherInCenterAsync(centerId, teacherId))
            return Result<string>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher == null)
            return Result<string>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        var user = await _unitOfWork.Users.GetUserByIdAsync(teacher.UserId);
        if (user == null)
            return Result<string>.Failure(_localizer, "ServerError");
        if (user.IsActive != true)
            return Result<string>.Failure(_localizer, "CenterTeacherLoginNotEnabled", HttpStatusCode.Conflict);

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            user.PasswordHashed = _passwordService.HashPassword(dto.NewPassword);
            user.SecurityStamp = Guid.NewGuid().ToString();
            // A center-driven reset always revokes the teacher's live sessions (mirrors the admin
            // force-reset), so a shared/old device is signed out.
            var tokens = _unitOfWork.RefreshTokenRepo.GetByUserId(user.Id);
            await _unitOfWork.GetRepository<RefreshToken, long>().DeleteRangeAsync(tokens);
            await _authInvalidation.InvalidateUserAsync(user.Id);
            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
            return Result<string>.Success("ok", _localizer, "CenterTeacherPasswordReset");
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            return Result<string>.Failure(_localizer, "ServerError");
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> DisableTeacherLoginAsync(long centerId, long teacherId)
    {
        if (!await _unitOfWork.Centers.IsTeacherInCenterAsync(centerId, teacherId))
            return Result<string>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher == null)
            return Result<string>.Failure(_localizer, "CenterTeacherNotFound", HttpStatusCode.NotFound);

        var user = await _unitOfWork.Users.GetUserByIdAsync(teacher.UserId);
        if (user == null)
            return Result<string>.Failure(_localizer, "ServerError");

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            await DisableLoginInTransactionAsync(user);
            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
            return Result<string>.Success("ok", _localizer, "CenterTeacherLoginDisabled");
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            return Result<string>.Failure(_localizer, "ServerError");
        }
    }

    /// <summary>Blocks login on a User (IsActive=false), bumps the security stamp, revokes live
    /// sessions, and joins the Redis snapshot invalidation to the caller's transaction. The caller
    /// owns SaveChanges/Commit (§5.2).</summary>
    private async Task DisableLoginInTransactionAsync(User user)
    {
        user.IsActive = false;
        user.SecurityStamp = Guid.NewGuid().ToString();
        var tokens = _unitOfWork.RefreshTokenRepo.GetByUserId(user.Id);
        await _unitOfWork.GetRepository<RefreshToken, long>().DeleteRangeAsync(tokens);
        await _authInvalidation.InvalidateUserAsync(user.Id);
    }

    /// <inheritdoc />
    public async Task<Result<List<CenterStudentResolveCandidateDto>>> ResolveStudentByCodeAsync(long centerId, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result<List<CenterStudentResolveCandidateDto>>.Success(
                new List<CenterStudentResolveCandidateDto>(), _localizer, "Success");

        var matches = await _unitOfWork.Centers.ResolveStudentsByCodeAcrossCenterAsync(centerId, code.Trim());
        var list = new List<CenterStudentResolveCandidateDto>();
        foreach (var m in matches)
        {
            // Resolve TODAY's occurrence (teacher-local) for the student's session so a scan can jump
            // straight into that session's take-attendance form; null when there's no class today.
            long? todayOccurrenceId = null;
            if (m.SessionId is long sessionId)
            {
                var localToday = _timeZone.GetTeacherLocalDate(m.TeacherId);
                var occurrence = await _unitOfWork.AttendanceRepo.GetOccurrenceBySessionAndDateAsync(sessionId, localToday);
                todayOccurrenceId = occurrence?.Id;
            }

            list.Add(new CenterStudentResolveCandidateDto
            {
                TeacherId = m.TeacherId,
                TeacherName = m.TeacherName,
                TeacherCode = m.TeacherCode,
                TeacherStudentId = m.TeacherStudentId,
                StudentName = m.StudentName,
                StudentCode = m.StudentCode,
                StudentPhoneNumber = m.StudentPhoneNumber,
                SessionId = m.SessionId,
                SessionName = m.SessionName,
                TodaySessionOccurrenceId = todayOccurrenceId
            });
        }

        // Messaging hint for the client: many → show a picker, one → auto-select, none → not found.
        var messageKey = list.Count > 1 ? "StudentCodeCandidates" : "Success";
        return Result<List<CenterStudentResolveCandidateDto>>.Success(list, _localizer, messageKey);
    }

    /// <inheritdoc />
    public async Task<Result<List<CenterTodaySessionDto>>> GetTodaySessionsAsync(long centerId)
    {
        var teachers = await _unitOfWork.Centers.GetTeachersByCenterAsync(centerId);
        var list = new List<CenterTodaySessionDto>();
        foreach (var teacher in teachers.Where(t => t.AccountStatus != AccountStatus.Inactive))
        {
            var teacherName = teacher.User?.FullName ?? string.Empty;
            var localToday = _timeZone.GetTeacherLocalDate(teacher.Id);
            var occurrences = await _unitOfWork.AttendanceRepo
                .GetOccurrencesByTeacherAndDateAsync(teacher.Id, localToday);
            foreach (var occurrence in occurrences.OrderBy(o => o.Session.StartTime))
            {
                list.Add(new CenterTodaySessionDto
                {
                    TeacherId = teacher.Id,
                    TeacherName = teacherName,
                    TeacherCode = teacher.TeacherCode,
                    SessionId = occurrence.SessionId,
                    SessionName = occurrence.Session.SessionName,
                    SessionOccurrenceId = occurrence.Id,
                    OccurrenceDate = occurrence.OccurrenceDate,
                    StartTime = occurrence.Session.StartTime,
                    EndTime = occurrence.Session.StartTime
                              + TimeSpan.FromMinutes(occurrence.Session.DurationMinutes),
                    Status = occurrence.Status
                });
            }
        }
        return Result<List<CenterTodaySessionDto>>.Success(list, _localizer, "Success");
    }

    /// <inheritdoc />
    public async Task<Result<List<CenterTeacherScheduleDto>>> GetTeacherScheduleSummariesAsync(long centerId)
    {
        var teachers = await _unitOfWork.Centers.GetTeachersByCenterAsync(centerId);

        // Active teachers only (mirrors GetTodaySessionsAsync's AccountStatus filter).
        var activeTeachers = teachers
            .Where(t => t.AccountStatus != AccountStatus.Inactive)
            .ToList();
        if (activeTeachers.Count == 0)
            return Result<List<CenterTeacherScheduleDto>>.Success(
                new List<CenterTeacherScheduleDto>(), _localizer, "Success");

        var teacherById = activeTeachers.ToDictionary(t => t.Id);

        // Two BATCHED reads → fixed query cost regardless of teacher count (avoids the per-teacher
        // occurrence loop GetTodaySessionsAsync does). Active-session filter mirrors the teacher home.
        // One local day for the whole card: EndDate is a calendar day, and UtcNow.Date is still
        // yesterday between midnight and 2-3 AM Cairo — which dropped a session from the schedule,
        // and flipped its Expired chip, for the first hours of the day it actually ended. The
        // batched read spans many teachers, so this anchors on the product zone rather than on
        // any one of them (all teachers share Africa/Cairo today).
        var today = _timeZone.ConvertUtcToLocal(DateTime.UtcNow).Date;

        var sessions = await _unitOfWork.SessionsRepo
            .GetActiveSessionsByTeacherIdsAsync(teacherById.Keys.ToList(), today);
        var studentCounts = await _unitOfWork.SessionsRepo
            .GetStudentCountsBySessionIdsAsync(sessions.Select(s => s.Id).ToList());
        var list = sessions
            .Select(s =>
            {
                var teacher = teacherById[s.TeacherId];
                return new CenterTeacherScheduleDto
                {
                    TeacherId = teacher.Id,
                    TeacherName = teacher.User?.FullName ?? string.Empty,
                    TeacherCode = teacher.TeacherCode,
                    SessionId = s.Id,
                    SessionName = s.SessionName,
                    OccurrenceType = s.OccurrenceType,
                    SelectedDays = ParseSelectedDays(s.SelectedDays),
                    MonthlyDayOfMonth = s.MonthlyDayOfMonth,
                    StartDate = s.StartDate,
                    EndDate = s.EndDate,
                    StartTime = s.StartTime,
                    DurationMinutes = s.DurationMinutes,
                    StudentCount = studentCounts.TryGetValue(s.Id, out var c) ? c : 0,
                    IsExpired = s.EndDate.Date < today
                };
            })
            .OrderBy(x => x.TeacherName)
            .ThenBy(x => x.StartTime)
            .ToList();

        return Result<List<CenterTeacherScheduleDto>>.Success(list, _localizer, "Success");
    }

    /// <summary>Parses the comma-separated selected-days string into an app day-index list (or null),
    /// matching SessionService.ParseSelectedDays so the client mapper sees identical data.</summary>
    private static List<int>? ParseSelectedDays(string? daysString)
    {
        if (string.IsNullOrWhiteSpace(daysString))
            return null;

        return daysString.Split(',')
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .ToList();
    }

    // ── mappers ──
    private static CenterTeacherListItemDto ToTeacherItem(Teacher t, decimal centerDefaultPercent,
        GenerationMode centerDefaultCodeMode, int studentCount, string? fullNameOverride = null) => new()
    {
        TeacherId = t.Id,
        FullName = fullNameOverride ?? t.User?.FullName ?? string.Empty,
        TeacherCode = t.TeacherCode,
        PlanType = t.CenterPlanType,
        StudentCapacity = t.StudentCapacity,
        RevenueSharePercentOverride = t.RevenueSharePercentOverride,
        EffectiveRevenueSharePercent = t.RevenueSharePercentOverride ?? centerDefaultPercent,
        StudentCodeModeOverride = t.StudentCodeModeOverride,
        EffectiveStudentCodeMode = t.StudentCodeModeOverride ?? centerDefaultCodeMode,
        AccountStatus = t.AccountStatus,
        StudentCount = studentCount,
        // Login gate = identity User.IsActive. The placeholder username is hidden until login is on.
        LoginEnabled = t.User?.IsActive == true,
        LoginUsername = t.User?.IsActive == true ? t.User?.Username : null
    };
}
