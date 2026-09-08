using Edvanz.API.Attributes;
using Edvanz.Domain.Constants;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Teacher;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Edvanz.API.Controllers;

/// <summary>
/// API endpoints for the Teacher module.
/// 
/// This controller handles all operations specific to the Teacher account type:
/// profile management, configuration settings, subscription status, and lookup data.
/// 
/// IMPORTANT: Registration (creating the User record), login, password update, and
/// account deletion endpoints are NOT here — they live in the User module controller.
/// The User module creates the User record first, then calls InitializeTeacher to
/// set up the Teacher-specific data (code, subjects, configuration).
/// 
/// All responses follow a unified JSON shape:
///   Success: { "success": true,  "message": "...", "data": { ... } }
///   Failure: { "success": false, "message": "..." }
/// 
/// Messages are returned in Arabic or English based on the Accept-Language header.
/// Set "Accept-Language: ar" for Arabic, "Accept-Language: en" for English.
/// </summary>
[ServiceFilter(typeof(Edvanz.API.Filters.TenantScopeFilter))]
public class TeacherController : ApiBaseController
{
    private readonly ITeacherService _teacherService;
    private readonly Edvanz.Application.IservicesContract.ICurrentUserService _currentUser;
    private readonly Edvanz.Domain.Interfaces.IUnitOfWork _unitOfWork;

    public TeacherController(
        ITeacherService teacherService,
        Edvanz.Application.IservicesContract.ICurrentUserService currentUser,
        Edvanz.Domain.Interfaces.IUnitOfWork unitOfWork)
    {
        _teacherService = teacherService;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// The acting teacher, resolved from the JWT (Teacher → own id, Assistant → owning teacher).
    /// The optional <paramref name="explicitTeacherId"/> from the route is honoured ONLY for
    /// SuperAdmin (support access); for everyone else it is ignored so the token is the sole tenant
    /// differentiator — endpoints work whether or not the URL carries the id.
    /// </summary>
    private async Task<long?> ResolveTeacherIdAsync(long? explicitTeacherId = null)
    {
        if (string.Equals(_currentUser.Role, "SuperAdmin", StringComparison.Ordinal))
            return explicitTeacherId;

        // Center tier: resolve the acting teacher (X-Acting-Teacher-Id) validated against membership.
        if (string.Equals(_currentUser.Role, UserRoles.Center, StringComparison.Ordinal)
            || string.Equals(_currentUser.Role, UserRoles.CenterAssistant, StringComparison.Ordinal))
            return await _currentUser.ResolveActingTeacherIdAsync();

        var userId = _currentUser.UserId;
        if (userId is null) return null;

        long? id = (await _unitOfWork.Users.GetTeacherByUserIdAsync(userId.Value))?.Id;
        if (id is null)
        {
            var asst = await _unitOfWork.AssistantRepo.GetAssistantWithUserIdAsync(userId.Value);
            id = asst?.TeacherAccountId;
        }
        return id;
    }

    private IActionResult TeacherNotResolvedResult() =>
        new ObjectResult(new { success = false, message = "Teacher could not be resolved from token." })
        { StatusCode = StatusCodes.Status404NotFound };

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 1: INITIALIZE TEACHER
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Called AFTER a User record is created with UserType = Teacher.
    //   This endpoint creates the Teacher-specific data that doesn't belong
    //   on the User table: generates the unique 8-digit TeacherCode, links
    //   the teacher to their subject(s), and creates a TeacherConfiguration
    //   row with all system defaults so the teacher can start using the app
    //   immediately without completing the configuration wizard.
    //
    // WHAT IT CREATES IN THE DATABASE:
    //   1. One row in Teachers table (with generated TeacherCode)
    //   2. One or more rows in TeacherSubjects table (subject associations)
    //   3. One row in TeacherConfigurations table (all defaults applied)
    //   4. Three rows in TeacherProratedTiers table (default payment tiers:
    //      days 1-10 = 100%, days 11-20 = 66.67%, days 21-31 = 33.33%)
    //
    // VALIDATIONS:
    //   - UserId must exist in Users table with UserType = Teacher (404 if not)
    //   - UserId must not already have a Teacher record (409 Conflict if duplicate)
    //   - At least one SubjectId OR a CustomSubject text must be provided (400 if neither)
    //   - Every SubjectId must exist in the Subjects table and be active (400 if invalid)
    //
    // SAMPLE REQUEST:
    //   POST /api/teacher/initialize
    //   {
    //     "userId": 1,
    //     "subjectIds": [3, 8],
    //     "customSubject": null,
    //     "languagePreference": "en",
    //     "createdByUserId": null,
    //     "studentCapacity": 500
    //   }
    //
    // SAMPLE RESPONSE (201 Created):
    //   {
    //     "success": true,
    //     "message": "Your teacher account has been created successfully. Your unique Teacher Code is ready",
    //     "data": {
    //       "id": 1,
    //       "userId": 1,
    //       "teacherCode": "48291057",
    //       "fullName": "Ahmed Mohamed",
    //       "studentCapacity": 500,
    //       "languagePreference": "en",
    //       "accountStatus": "Active",
    //       "isConfigurationCompleted": false,
    //       "subjects": [
    //         { "id": 3, "nameEn": "Mathematics", "nameAr": "الرياضيات" },
    //         { "id": 8, "nameEn": "Physics", "nameAr": "الفيزياء" }
    //       ],
    //       "activeSubscription": null
    //     }
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("initialize")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Teacher.TeacherProfileDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> InitializeTeacher([FromBody] CreateTeacherDto dto)
    {
        var result = await _teacherService.InitializeTeacherAsync(dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 2: GET TEACHER PROFILE
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns the full profile of a teacher including their personal info
    //   (from Users table), teacher-specific data (TeacherCode, capacity,
    //   language preference), their subject(s), their selected capacity
    //   package name, and their current active subscription summary.
    //
    // WHO CALLS IT:
    //   - The teacher themselves (to view their own profile/settings screen)
    //   - The super admin (to view a teacher's details in the admin dashboard)
    //
    // TABLES READ:
    //   Teachers, Users, TeacherSubjects, Subjects, StudentCapacityPackages,
    //   TeacherSubscriptions
    //
    // SAMPLE REQUEST:
    //   GET /api/teacher/1/profile
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "Done successfully",
    //     "data": {
    //       "id": 1,
    //       "userId": 1,
    //       "teacherCode": "48291057",
    //       "fullName": "Ahmed Mohamed",
    //       "email": "ahmed@example.com",
    //       "phoneNumber": "01012345678",
    //       "studentCapacity": 500,
    //       "languagePreference": "en",
    //       "customSubject": null,
    //       "accountStatus": "Active",
    //       "isConfigurationCompleted": true,
    //       "createdAt": "2026-03-28T10:00:00Z",
    //       "subjects": [
    //         { "id": 3, "nameEn": "Mathematics", "nameAr": "الرياضيات", "displayOrder": 3 }
    //       ],
    //       "capacityPackageName": "300 to 500",
    //       "activeSubscription": {
    //         "id": 1,
    //         "subscriptionStatus": "Active",
    //         "startDate": "2026-03-01T00:00:00Z",
    //         "endDate": "2026-04-01T00:00:00Z",
    //         "daysRemaining": 4
    //       }
    //     }
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("profile")]
    [HttpGet("{teacherId:long}/profile")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Teacher.TeacherProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTeacherProfile([FromRoute] long? teacherId = null)
    {
        var id = await ResolveTeacherIdAsync(teacherId);
        if (id is null) return TeacherNotResolvedResult();
        var result = await _teacherService.GetTeacherProfileAsync(id.Value);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 3: UPDATE TEACHER PROFILE
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Allows the teacher to update their own profile information:
    //   - Full name (supports Arabic and English)
    //   - Language preference (system UI language: "en" or "ar")
    //   - Subject associations (replaces all existing with the new list)
    //   - Custom subject text (for subjects not in the ministry list)
    //   - Student capacity package (selecting a tier auto-updates StudentCapacity
    //     to the package's MaxStudents value, which will determine subscription fees)
    //
    // WHAT IT CANNOT CHANGE (by design):
    //   - TeacherCode: immutable after creation (AAM-BR-05)
    //   - AccountStatus: only the super admin can activate/deactivate
    //   - Email, Username, Password, PhoneNumber: managed by the User module
    //
    // IMPORTANT — LANGUAGE PREFERENCE vs GENERATION LANGUAGE:
    //   LanguagePreference here controls the SYSTEM UI language (buttons, labels,
    //   messages in Arabic or English). This is DIFFERENT from the code/session
    //   generation language in TeacherConfiguration (StudentCodeLanguage,
    //   SessionNameLanguage) which controls whether auto-generated student codes
    //   and session names use Arabic or English characters.
    //   A teacher can have: system UI in English + student codes in Arabic.
    //
    // WHAT IT UPDATES IN THE DATABASE:
    //   1. Users table: FullName
    //   2. Teachers table: LanguagePreference, CustomSubject, StudentCapacityPackageId,
    //      StudentCapacity (auto-set from package)
    //   3. TeacherSubjects table: deletes all existing rows for this teacher,
    //      inserts new rows for each SubjectId provided
    //
    // VALIDATIONS:
    //   - TeacherId must exist (404 if not)
    //   - FullName cannot be empty (400)
    //   - LanguagePreference must be "en" or "ar" (400)
    //   - At least one SubjectId or CustomSubject required (400)
    //   - Every SubjectId must exist and be active (400)
    //   - StudentCapacityPackageId (if provided) must exist and be active (400)
    //
    // SAMPLE REQUEST:
    //   PUT /api/teacher/1/profile
    //   {
    //     "fullName": "أحمد محمد",
    //     "languagePreference": "ar",
    //     "studentCapacityPackageId": 3,
    //     "subjectIds": [3, 8],
    //     "customSubject": null
    //   }
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "تم تحديث الملف الشخصي بنجاح",
    //     "data": {
    //       "id": 1,
    //       "teacherCode": "48291057",
    //       "fullName": "أحمد محمد",
    //       "studentCapacity": 800,
    //       "languagePreference": "ar",
    //       "capacityPackageName": "500 to 800",
    //       "subjects": [
    //         { "id": 3, "nameEn": "Mathematics", "nameAr": "الرياضيات" },
    //         { "id": 8, "nameEn": "Physics", "nameAr": "الفيزياء" }
    //       ]
    //     }
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("profile")]
    [HttpPut("{teacherId:long}/profile")]
    // ROLE GATE (added with the teacher self-service rename). Without it this action carried only
    // the global authenticated-user FallbackPolicy, while ResolveTeacherIdAsync resolves an
    // ASSISTANT to their owning teacher — so any assistant could rename their tutor and silently
    // replace their subject list. Same class as BUG-13; see CLAUDE.md §8.
    // Verified before gating: no Center path reaches this action. Centers rename their teachers
    // through PUT /api/center/teachers/{id} ([Authorize(Roles = "Center")], CenterController), and
    // the app's language toggle — the only other PUT caller — returns early for assistant, center,
    // centerAssistant and superAdmin (account_language_repository_impl.dart:54-59), issuing this
    // request ONLY for AuthAccountType.teacher. The GET actions are deliberately left ungated so
    // the acting-teacher read surface keeps working for the Center tier.
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Edvanz.Domain.Constants.TeacherConstants.ProfileUpdateRateLimitPolicy)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Teacher.TeacherProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTeacherProfile(
        [FromBody] UpdateTeacherProfileDto dto,
        [FromRoute] long? teacherId = null)
    {
        var id = await ResolveTeacherIdAsync(teacherId);
        if (id is null) return TeacherNotResolvedResult();
        var result = await _teacherService.UpdateTeacherProfileAsync(id.Value, dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 4: GET TEACHER BY CODE
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Looks up a teacher by their unique 8-digit TeacherCode and returns
    //   ONLY the teacher's name and subject — no internal IDs, no configuration,
    //   no subscription data. This is a deliberately minimal response because
    //   it's used by students during the teacher-linking flow.
    //
    // WHO CALLS IT:
    //   Students, when adding a teacher to their dashboard. The student enters
    //   the 8-digit TeacherCode, and this endpoint confirms the teacher exists
    //   and shows their name + subject so the student can verify they're linking
    //   to the right person.
    //
    // SECURITY:
    //   Only returns active teachers (AccountStatus = Active).
    //   Does not expose TeacherId, email, phone, capacity, or any internal data.
    //
    // VALIDATIONS:
    //   - teacherCode must be exactly 8 characters (400 if not)
    //   - Must match an active teacher record (404 if not found or inactive)
    //
    // SAMPLE REQUEST:
    //   GET /api/teacher/by-code/48291057
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "Done successfully",
    //     "data": {
    //       "teacherCode": "48291057",
    //       "fullName": "Ahmed Mohamed",
    //       "subjectName": "Mathematics"
    //     }
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("by-code/{teacherCode}")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Teacher.TeacherPublicInfoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTeacherByCode([FromRoute] string teacherCode)
    {
        var result = await _teacherService.GetTeacherByCodeAsync(teacherCode);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 5: GET CONFIGURATION
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns ALL current configuration settings for a teacher. These are
    //   the settings from the post-registration configuration wizard
    //   (AAM-FR-04.2 through 04.9). If the teacher skipped the wizard,
    //   system defaults are returned.
    //
    // WHAT'S INCLUDED IN THE RESPONSE:
    //   - Student code generation: Auto or Manual, Arabic or English
    //   - Session name generation: Auto or Manual, Arabic or English
    //   - Prorated payment: enabled/disabled + up to 3 tier definitions
    //   - Alert thresholds: consecutive absences, consecutive unpaid sessions
    //   - Barcode display: InApp or HardCopyOnly
    //   - Student visibility: which modules students can see (attendance,
    //     payment, homework, exam default)
    //   - Parent visibility: same toggles but independent from student settings
    //
    // TABLES READ:
    //   TeacherConfigurations, TeacherProratedTiers
    //
    // SAMPLE REQUEST:
    //   GET /api/teacher/1/configuration
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "Done successfully",
    //     "data": {
    //       "id": 1,
    //       "teacherId": 1,
    //       "studentCodeGenerationMode": 1,
    //       "studentCodeLanguage": 1,
    //       "sessionNameMode": 1,
    //       "sessionNameLanguage": 1,
    //       "isProratedPaymentEnabled": false,
    //       "consecutiveAbsenceThreshold": 3,
    //       "consecutiveUnpaidThreshold": 3,
    //       "barcodeDisplayMode": 1,
    //       "studentVisibilityAttendance": true,
    //       "studentVisibilityPayment": true,
    //       "studentVisibilityHomework": true,
    //       "studentVisibilityExamDefault": false,
    //       "parentVisibilityAttendance": true,
    //       "parentVisibilityPayment": true,
    //       "parentVisibilityHomework": true,
    //       "parentVisibilityExamDefault": false,
    //       "proratedTiers": [
    //         { "tierNumber": 1, "thresholdDayStart": 1, "thresholdDayEnd": 10, "fractionRate": 1.0 },
    //         { "tierNumber": 2, "thresholdDayStart": 11, "thresholdDayEnd": 20, "fractionRate": 0.6667 },
    //         { "tierNumber": 3, "thresholdDayStart": 21, "thresholdDayEnd": 31, "fractionRate": 0.3333 }
    //       ],
    //       "updatedAt": null
    //     }
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("configuration")]
    [HttpGet("{teacherId:long}/configuration")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Teacher.TeacherConfigurationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConfiguration([FromRoute] long? teacherId = null)
    {
        var id = await ResolveTeacherIdAsync(teacherId);
        if (id is null) return TeacherNotResolvedResult();
        var result = await _teacherService.GetConfigurationAsync(id.Value);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 6: SAVE CONFIGURATION
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Saves or updates ALL teacher configuration settings in a single call.
    //   This is the endpoint for both:
    //   - The initial configuration wizard after registration
    //   - The settings screen accessible at any time
    //   On first save, it marks IsConfigurationCompleted = true on the Teacher
    //   record so the UI knows not to prompt the wizard again.
    //
    // WHAT IT UPDATES IN THE DATABASE:
    //   1. TeacherConfigurations table: all settings fields + UpdatedAt timestamp
    //   2. TeacherProratedTiers table: deletes all existing tiers, inserts new ones
    //   3. Teachers table: StudentCapacityPackageId + StudentCapacity (if package
    //      provided), IsConfigurationCompleted = true (on first save)
    //
    // VALIDATIONS:
    //   - TeacherId must exist (404)
    //   - Configuration must exist (404 — should always exist since InitializeTeacher creates it)
    //   - If IsProratedPaymentEnabled = true, at least 1 tier is required (400)
    //   - Maximum 3 prorated tiers allowed (400)
    //   - StudentCapacityPackageId (if provided) must exist and be active (400)
    //
    // SAMPLE REQUEST:
    //   PUT /api/teacher/1/configuration
    //   {
    //     "studentCapacityPackageId": 2,
    //     "studentCodeGenerationMode": 1,
    //     "studentCodeLanguage": 2,
    //     "sessionNameMode": 2,
    //     "sessionNameLanguage": 1,
    //     "isProratedPaymentEnabled": true,
    //     "proratedTiers": [
    //       { "tierNumber": 1, "thresholdDayStart": 1, "thresholdDayEnd": 10, "fractionRate": 1.0 },
    //       { "tierNumber": 2, "thresholdDayStart": 11, "thresholdDayEnd": 20, "fractionRate": 0.5 }
    //     ],
    //     "consecutiveAbsenceThreshold": 5,
    //     "consecutiveUnpaidThreshold": 3,
    //     "barcodeDisplayMode": 2,
    //     "studentVisibilityAttendance": true,
    //     "studentVisibilityPayment": false,
    //     "studentVisibilityHomework": true,
    //     "studentVisibilityExamDefault": false,
    //     "parentVisibilityAttendance": true,
    //     "parentVisibilityPayment": true,
    //     "parentVisibilityHomework": true,
    //     "parentVisibilityExamDefault": false
    //   }
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "Your settings have been saved successfully",
    //     "data": { ... full configuration object ... }
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("configuration")]
    [HttpPut("{teacherId:long}/configuration")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Teacher.TeacherConfigurationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SaveConfiguration(
        [FromBody] UpdateTeacherConfigurationDto dto,
        [FromRoute] long? teacherId = null)
    {
        var id = await ResolveTeacherIdAsync(teacherId);
        if (id is null) return TeacherNotResolvedResult();
        var result = await _teacherService.SaveConfigurationAsync(id.Value, dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 7: GET ACTIVE SUBSCRIPTION
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns the teacher's current active subscription details: status,
    //   start date, end date, and computed days remaining. If the teacher
    //   has no active subscription (e.g., expired or never subscribed),
    //   returns success = true with data = null and a friendly message.
    //
    // WHO CALLS IT:
    //   - The teacher (to see their subscription status in settings)
    //   - The system (to check if features should be accessible)
    //   - The super admin (to review a teacher's subscription)
    //
    // TABLES READ:
    //   TeacherSubscriptions (queries for Active or ExpiringSoon status,
    //   returns the one with the latest EndDate)
    //
    // SAMPLE REQUEST:
    //   GET /api/teacher/1/subscription
    //
    // SAMPLE RESPONSE — active subscription (200 OK):
    //   {
    //     "success": true,
    //     "message": "Done successfully",
    //     "data": {
    //       "id": 1,
    //       "subscriptionStatus": "Active",
    //       "startDate": "2026-03-01T00:00:00Z",
    //       "endDate": "2026-04-01T00:00:00Z",
    //       "daysRemaining": 4
    //     }
    //   }
    //
    // SAMPLE RESPONSE — no active subscription (200 OK):
    //   {
    //     "success": true,
    //     "message": "You don't have an active subscription at the moment",
    //     "data": null
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("subscription")]
    [HttpGet("{teacherId:long}/subscription")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Teacher.TeacherSubscriptionDto?>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActiveSubscription([FromRoute] long? teacherId = null)
    {
        var id = await ResolveTeacherIdAsync(teacherId);
        if (id is null) return TeacherNotResolvedResult();
        var result = await _teacherService.GetActiveSubscriptionAsync(id.Value);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 8: GET AVAILABLE SUBJECTS
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns all active subjects from the Subjects lookup table, ordered
    //   by DisplayOrder. These are the predefined subjects from the Egyptian
    //   Ministry of Education (Arabic, English, Mathematics, Science, etc.).
    //   Used to populate the subject selection dropdown during registration
    //   and in the profile edit screen.
    //
    // NOTE:
    //   This returns ALL subjects — no pagination needed (max ~20 rows).
    //   Each subject includes bilingual names (NameEn + NameAr) so the
    //   frontend can display the correct language based on the user's
    //   LanguagePreference without a second API call.
    //
    // TABLES READ:
    //   Subjects (filtered by IsActive = true)
    //
    // SAMPLE REQUEST:
    //   GET /api/teacher/subjects
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "Done successfully",
    //     "data": [
    //       { "id": 1, "nameEn": "Arabic Language", "nameAr": "اللغة العربية", "displayOrder": 1 },
    //       { "id": 2, "nameEn": "English Language", "nameAr": "اللغة الإنجليزية", "displayOrder": 2 },
    //       { "id": 3, "nameEn": "Mathematics", "nameAr": "الرياضيات", "displayOrder": 3 },
    //       ...
    //     ]
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    // AllowAnonymous: the subject dropdown is populated during teacher registration, BEFORE any
    // account/JWT exists. Without this, the global FallbackPolicy (RequireAuthenticatedUser) would
    // 401 the call. Safe — this returns only the public ministry Subjects lookup (no tenant data),
    // and TenantScopeFilter passes anonymous requests straight through.
    [HttpGet("subjects")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.IReadOnlyList<Edvanz.Application.Dtos.Teacher.SubjectDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailableSubjects()
    {
        var result = await _teacherService.GetAvailableSubjectsAsync();
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 9: GET CAPACITY PACKAGES
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns all active student capacity packages from the lookup table,
    //   ordered by DisplayOrder. These are the 7 subscription tiers that
    //   determine how many students a teacher can register and what
    //   subscription fee applies.
    //
    //   The teacher selects a package during configuration or profile update.
    //   When selected, the system auto-updates the teacher's StudentCapacity
    //   to the package's MaxStudents value.
    //
    // NOTE:
    //   No pagination needed (max 7 rows). The "3000+" tier has
    //   MaxStudents = null, meaning unlimited capacity.
    //
    // TABLES READ:
    //   StudentCapacityPackages (filtered by IsActive = true)
    //
    // SAMPLE REQUEST:
    //   GET /api/teacher/capacity-packages
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "Done successfully",
    //     "data": [
    //       { "id": 1, "name": "Up to 300", "minStudents": 0, "maxStudents": 300, "displayOrder": 1 },
    //       { "id": 2, "name": "300 to 500", "minStudents": 300, "maxStudents": 500, "displayOrder": 2 },
    //       { "id": 3, "name": "500 to 800", "minStudents": 500, "maxStudents": 800, "displayOrder": 3 },
    //       { "id": 4, "name": "800 to 1200", "minStudents": 800, "maxStudents": 1200, "displayOrder": 4 },
    //       { "id": 5, "name": "1200 to 1500", "minStudents": 1200, "maxStudents": 1500, "displayOrder": 5 },
    //       { "id": 6, "name": "1500 to 3000", "minStudents": 1500, "maxStudents": 3000, "displayOrder": 6 },
    //       { "id": 7, "name": "3000+", "minStudents": 3000, "maxStudents": null, "displayOrder": 7 }
    //     ]
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("capacity-packages")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.IReadOnlyList<Edvanz.Application.Dtos.Teacher.StudentCapacityPackageDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCapacityPackages()
    {
        var result = await _teacherService.GetCapacityPackagesAsync();
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 10: GET TEACHERS LIST (PAGINATED)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns a paginated list of teachers for the Super Admin dashboard.
    //   Supports searching by teacher name, username, or teacher code;
    //   filtering by account status and subscription status; and sorting
    //   by capacity, creation date, or teacher code.
    //
    // WHO CALLS IT:
    //   Super Admin only — this is the main teacher management list.
    //
    // QUERY PARAMETERS:
    //   - page (int, default 1): Page number (1-based)
    //   - pageSize (int, default 20, max 100): Records per page
    //   - search (string, optional): Filters by name, username, or teacher code
    //   - sortBy (string, optional): "capacity", "createdat", "code" (default: createdat desc)
    //   - sortDirection (string, optional): "asc" or "desc" (default: "asc")
    //   - accountStatus (string, optional): "Active", "Inactive", or "Suspended"
    //   - subscriptionStatus (string, optional): "Active", "ExpiringSoon", or "Expired"
    //   - subscribedWithinDays (int, optional): only teachers whose CURRENT subscription
    //     started within this many days, ordered newest-subscription-first
    //     (Activity Monitor "Newly subscribed" tab)
    //   - registeredFrom / registeredTo (date, optional): filter by REGISTRATION date
    //     (Teacher.CreateAt). Inclusive on both ends — registeredTo covers the whole of
    //     that day. Ordered newest-registration-first (Activity Monitor "Registered on"
    //     date-range filter). Either bound may be omitted (open-ended range).
    //
    // TABLES READ:
    //   Teachers, Users, TeacherSubscriptions
    //
    // SOFT-DELETE:
    //   Deleted teachers (DeletedAt != null) are automatically excluded
    //   by the EF Core global query filter — they never appear in this list.
    //
    // SAMPLE REQUEST:
    //   GET /api/teacher/list?page=1&pageSize=10&search=ahmed&sortBy=createdat&sortDirection=desc&accountStatus=Active
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "Done successfully",
    //     "data": {
    //       "totalCount": 45,
    //       "page": 1,
    //       "pageSize": 10,
    //       "totalPages": 5,
    //       "data": [
    //         {
    //           "id": 1,
    //           "fullName": "Ahmed Mohamed",
    //           "username": "ahmed.m",
    //           "teacherCode": "48291057",
    //           "phoneNumber": "01012345678",
    //           "studentCapacity": 500,
    //           "accountStatus": "Active",
    //           "isConfigurationCompleted": true,
    //           "subscriptionStatus": "Active",
    //           "subscriptionEndDate": "2026-04-01T00:00:00Z",
    //           "createdAt": "2026-03-01T10:00:00Z"
    //         },
    //         ...
    //       ]
    //     }
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("list")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.Teacher.TeacherListItemDto>>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTeachers(
    [FromQuery] PaginatedRequest request,
    [FromQuery] AccountStatus? accountStatus = null,
    [FromQuery] SubscriptionStatus? subscriptionStatus = null,
    [FromQuery] int? subscribedWithinDays = null,
    [FromQuery] DateTime? registeredFrom = null,
    [FromQuery] DateTime? registeredTo = null)
    {
        var result = await _teacherService.GetTeachersAsync(
            request,
            accountStatus?.ToString(),
            subscriptionStatus?.ToString(),
            subscribedWithinDays,
            registeredFrom,
            registeredTo);
        return ToResponse(result);
    }
    [HttpPatch("{id:long}/deactivate")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(long id)
    => ToResponse(await _teacherService.ToggleTeacherStatusAsync(
        new ToggleAccountStatus { accountId = id, targetStatus = AccountStatus.Inactive }));

    [HttpPatch("{id:long}/activate")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(long id)
        => ToResponse(await _teacherService.ToggleTeacherStatusAsync(
            new ToggleAccountStatus { accountId = id, targetStatus = AccountStatus.Active }));

    [HttpPatch("{id:long}/delete")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SoftDelete(long id)
        => ToResponse(await _teacherService.ToggleTeacherStatusAsync(
            new ToggleAccountStatus { accountId = id, targetStatus = AccountStatus.Suspended }));

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT: GET TEACHER LOOKUP (UNPAGINATED, FOR DROPDOWNS)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns Id + FullName for every active teacher, in one flat array,
    //   ordered by name. No pagination — intended for select/dropdown controls
    //   on the Super Admin dashboard.
    //
    // WHO CALLS IT:
    //   Super Admin only.
    //
    // SAMPLE REQUEST:
    //   GET /api/teacher/lookup
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "Done successfully",
    //     "data": [
    //       { "id": 1, "fullName": "Ahmed Mohamed" },
    //       { "id": 2, "fullName": "Sara Ali" }
    //     ]
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("lookup")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Application.Dtos.Teacher.TeacherLookupItemDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTeacherLookup()
    {
        var result = await _teacherService.GetTeacherLookupAsync();
        return ToResponse(result);
    }
}