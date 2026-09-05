using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.ServiceContract;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Persistence;

/// <summary>
/// Baseline user seed: one user of every UserType plus the full module/permission graph.
/// Partial of <see cref="DbInitializer"/>.
///
/// IDEMPOTENCY: Guards on super-admin existence. The whole block is wrapped in a
/// transaction, so it either completes fully or leaves no rows at all.
///
/// SEEDED USERS:
///   SuperAdmin   — Platform Administrator (superadmin)
///   Teacher 1    — Ahmed Mostafa (teacher1)  — ALL 8 modules
///   Teacher 2    — Mariam Hassan (teacher2)  — Student + Session + Attendance
///   Teacher 3    — Omar Farouk  (teacher3)  — ALL 8 modules
///   Assistant 1A — Sara Ibrahim  (assistant1a) — ALL permissions (full delegate)
///   Assistant 1B — Khaled Nasser (assistant1b) — Read-mostly, Student + Attendance
///   Assistant 2A — Nour Adel    (assistant2a) — Within teacher2's open modules
///   Assistant 3A — Laila Samir  (assistant3a) — ALL permissions (full delegate)
///   Student      — Youssef Tarek (student1)
///   Parent       — Hany Saleh    (parent1)
/// </summary>
public partial class DbInitializer
{
    // ════════════════════════════════════════════════
    // USERS (orchestrator)
    // ════════════════════════════════════════════════

    private static async Task SeedUsersAsync(EdvanzDbContext context, IPasswordService passwordService)
    {
        bool alreadySeeded = await context.Users.AnyAsync(u => u.UserType == UserType.SuperAdmin);
        if (alreadySeeded) return;

        string hashedPassword = passwordService.HashPassword(DefaultSeedPassword);
        DateTime now = DateTime.UtcNow;

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            try
        {
            var superAdmin = await SeedSuperAdminAsync(context, hashedPassword, now);

            var teacher1 = await SeedTeacherAsync(
                context, hashedPassword, now,
                fullName: "Ahmed Mostafa", username: "teacher1",
                email: "teacher1@edvanz.example", phoneNumber: "01000000001",
                teacherCode: Teacher1Code, studentCapacity: 300,
                createdByUserId: superAdmin.Id);

            await AssignAllModulesToTeacherAsync(context, teacher1.Id);

            var teacher2 = await SeedTeacherAsync(
                context, hashedPassword, now,
                fullName: "Mariam Hassan", username: "teacher2",
                email: "teacher2@edvanz.example", phoneNumber: "01000000002",
                teacherCode: Teacher2Code, studentCapacity: 500,
                createdByUserId: superAdmin.Id);

            await AssignSpecificModulesToTeacherAsync(
                context, teacher2.Id,
                moduleNames: new[] { "Student", "Session", "Attendance" });

            var teacher3 = await SeedTeacherAsync(
                context, hashedPassword, now,
                fullName: "Omar Farouk", username: "teacher3",
                email: "teacher3@edvanz.example", phoneNumber: "01000000003",
                teacherCode: Teacher3Code, studentCapacity: 800,
                createdByUserId: superAdmin.Id);

            await AssignAllModulesToTeacherAsync(context, teacher3.Id);

            await SeedAssistantAsync(
                context, hashedPassword, now,
                teacherId: teacher1.Id, fullName: "Sara Ibrahim", username: "assistant1a",
                email: "assistant1a@edvanz.example", phoneNumber: "01000000010",
                permissionFilter: PermissionFilter.AllPermissions);

            await SeedAssistantAsync(
                context, hashedPassword, now,
                teacherId: teacher1.Id, fullName: "Khaled Nasser", username: "assistant1b",
                email: "assistant1b@edvanz.example", phoneNumber: "01000000011",
                permissionFilter: PermissionFilter.PartialReadMostly);

            await SeedAssistantAsync(
                context, hashedPassword, now,
                teacherId: teacher2.Id, fullName: "Nour Adel", username: "assistant2a",
                email: "assistant2a@edvanz.example", phoneNumber: "01000000020",
                permissionFilter: PermissionFilter.WithinTutorModules,
                tutorModuleNames: new[] { "Student", "Session", "Attendance" });

            await SeedAssistantAsync(
                context, hashedPassword, now,
                teacherId: teacher3.Id, fullName: "Laila Samir", username: "assistant3a",
                email: "assistant3a@edvanz.example", phoneNumber: "01000000070",
                permissionFilter: PermissionFilter.AllPermissions);

            await SeedStudentUserAsync(
                context, hashedPassword, now,
                fullName: "Youssef Tarek", username: "student1",
                email: "student1@edvanz.example", phoneNumber: "01000000030",
                studentAccountCode: "STU000001");

            await SeedParentUserAsync(
                context, hashedPassword, now,
                fullName: "Hany Saleh", username: "parent1",
                email: "parent1@edvanz.example", phoneNumber: "01000000040");

            await transaction.CommitAsync();
        }
        catch
        {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    // ════════════════════════════════════════════════
    // SUPER ADMIN
    // ════════════════════════════════════════════════

    private static async Task<User> SeedSuperAdminAsync(
        EdvanzDbContext context, string hashedPassword, DateTime now)
    {
        var superAdmin = new User
        {
            UserType       = UserType.SuperAdmin,
            FullName       = "Platform Administrator",
            Username       = "superadmin",
            Email          = "admin@edvanz.example",
            PhoneNumber    = "01000000000",
            PasswordHashed = hashedPassword,
            IsActive       = true,
            IsVerified     = true,
            CreateAt       = now
        };

        context.Users.Add(superAdmin);
        await context.SaveChangesAsync();
        return superAdmin;
    }

    // ════════════════════════════════════════════════
    // TEACHER
    // ════════════════════════════════════════════════

    /// <summary>
    /// Creates User + Teacher + TeacherConfiguration rows, mirroring
    /// TeacherService.InitializeTeacherAsync so the account is immediately usable.
    /// </summary>
    private static async Task<Teacher> SeedTeacherAsync(
        EdvanzDbContext context,
        string hashedPassword,
        DateTime now,
        string fullName,
        string username,
        string email,
        string phoneNumber,
        string teacherCode,
        int studentCapacity,
        long createdByUserId)
    {
        var user = new User
        {
            UserType         = UserType.Teacher,
            FullName         = fullName,
            Username         = username,
            Email            = email,
            PhoneNumber      = phoneNumber,
            PasswordHashed   = hashedPassword,
            IsActive         = true,
            IsVerified       = true,
            CreateByUserId   = createdByUserId,
            CreateAt         = now
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var teacher = new Teacher
        {
            UserId                   = user.Id,
            TeacherCode              = teacherCode,
            StudentCapacity          = studentCapacity,
            // Seeded teachers start with both limits equal (see Teacher.LinkedStudentCapacity).
            LinkedStudentCapacity    = studentCapacity,
            LanguagePreference       = EnglishLanguage,
            AccountStatus            = AccountStatus.Active,
            IsConfigurationCompleted = true,
            CreatedByUserId          = createdByUserId,
            CreateAt                 = now
        };
        context.Teachers.Add(teacher);
        await context.SaveChangesAsync();

        context.TeacherConfigurations.Add(new TeacherConfiguration
        {
            TeacherId = teacher.Id,
            CreateAt  = now
        });
        await context.SaveChangesAsync();

        return teacher;
    }

    // ════════════════════════════════════════════════
    // TEACHER MODULE ASSIGNMENT
    // ════════════════════════════════════════════════

    private static async Task AssignAllModulesToTeacherAsync(EdvanzDbContext context, long teacherId)
    {
        var moduleIds = await context.Models.Select(m => m.Id).ToListAsync();
        await AssignModuleIdsToTeacherAsync(context, teacherId, moduleIds);
    }

    private static async Task AssignSpecificModulesToTeacherAsync(
        EdvanzDbContext context, long teacherId, IReadOnlyCollection<string> moduleNames)
    {
        var moduleIds = await context.Models
            .Where(m => moduleNames.Contains(m.Name))
            .Select(m => m.Id)
            .ToListAsync();

        await AssignModuleIdsToTeacherAsync(context, teacherId, moduleIds);
    }

    private static async Task AssignModuleIdsToTeacherAsync(
        EdvanzDbContext context, long teacherId, IReadOnlyCollection<long> moduleIds)
    {
        var rows = moduleIds
            .Select(moduleId => new TutorModule { TutorId = teacherId, ModuleId = moduleId })
            .ToList();

        context.TutorModuleAccess.AddRange(rows);
        await context.SaveChangesAsync();
    }

    // ════════════════════════════════════════════════
    // ASSISTANT + PERMISSIONS
    // ════════════════════════════════════════════════

    private enum PermissionFilter
    {
        AllPermissions,
        PartialReadMostly,
        WithinTutorModules
    }

    private static async Task SeedAssistantAsync(
        EdvanzDbContext context,
        string hashedPassword,
        DateTime now,
        long teacherId,
        string fullName,
        string username,
        string email,
        string phoneNumber,
        PermissionFilter permissionFilter,
        IReadOnlyCollection<string>? tutorModuleNames = null)
    {
        var user = new User
        {
            UserType       = UserType.Assistant,
            FullName       = fullName,
            Username       = username,
            Email          = email,
            PhoneNumber    = phoneNumber,
            PasswordHashed = hashedPassword,
            IsActive       = true,
            IsVerified     = true,
            CreateAt       = now
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        context.Assistants.Add(new Assistant
        {
            UserId            = user.Id,
            TeacherAccountId  = teacherId,
            AccountStatus     = AccountStatus.Active,
            LanguagePreference = EnglishLanguage,
            CreateAt          = now,
            UpdatedAt         = now
        });
        await context.SaveChangesAsync();

        var permissionIds = await ResolvePermissionIdsAsync(context, permissionFilter, tutorModuleNames);
        if (permissionIds.Count == 0) return;

        context.UsersPermissions.AddRange(
            permissionIds.Select(pid => new UsersPermission { UserId = user.Id, PermissionId = pid }));
        await context.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<long>> ResolvePermissionIdsAsync(
        EdvanzDbContext context,
        PermissionFilter filter,
        IReadOnlyCollection<string>? tutorModuleNames)
    {
        switch (filter)
        {
            case PermissionFilter.AllPermissions:
                return await context.Permissions.Select(p => p.Id).ToListAsync();

            case PermissionFilter.PartialReadMostly:
                var partialKeys = new (string Module, string Permission)[]
                {
                    ("Student",    "ViewList"),
                    ("Student",    "ViewProfile"),
                    ("Student",    "ViewBarcodes"),
                    ("Attendance", "Take"),
                    ("Attendance", "ViewHistory"),
                    ("Attendance", "ViewAbsenceOverview")
                };
                return await GetPermissionIdsByNameTuplesAsync(context, partialKeys);

            case PermissionFilter.WithinTutorModules:
                if (tutorModuleNames is null || tutorModuleNames.Count == 0)
                    return Array.Empty<long>();

                return await context.Permissions
                    .Where(p => tutorModuleNames.Contains(p.module.Name))
                    .Select(p => p.Id)
                    .ToListAsync();

            default:
                return Array.Empty<long>();
        }
    }

    private static async Task<IReadOnlyList<long>> GetPermissionIdsByNameTuplesAsync(
        EdvanzDbContext context,
        IReadOnlyCollection<(string Module, string Permission)> keys)
    {
        var moduleNames = keys.Select(k => k.Module).Distinct().ToList();

        var candidates = await context.Permissions
            .Include(p => p.module)
            .Where(p => moduleNames.Contains(p.module.Name))
            .ToListAsync();

        var keySet = new HashSet<(string, string)>(keys.Select(k => (k.Module, k.Permission)));

        return candidates
            .Where(p => keySet.Contains((p.module.Name, p.Name)))
            .Select(p => p.Id)
            .ToList();
    }

    // ════════════════════════════════════════════════
    // STUDENT USER
    // ════════════════════════════════════════════════

    private static async Task SeedStudentUserAsync(
        EdvanzDbContext context,
        string hashedPassword,
        DateTime now,
        string fullName,
        string username,
        string email,
        string phoneNumber,
        string studentAccountCode)
    {
        var user = new User
        {
            UserType       = UserType.Student,
            FullName       = fullName,
            Username       = username,
            Email          = email,
            PhoneNumber    = phoneNumber,
            PasswordHashed = hashedPassword,
            IsActive       = true,
            IsVerified     = true,
            CreateAt       = now
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        context.StudentUsers.Add(new StudentUser
        {
            UserId             = user.Id,
            StudentAccountCode = studentAccountCode,
            LanguagePreference = EnglishLanguage,
            AccountStatus      = AccountStatus.Active,
            IsFirstLogin       = true,
            CreateAt           = now
        });
        await context.SaveChangesAsync();
    }

    // ════════════════════════════════════════════════
    // PARENT USER
    // ════════════════════════════════════════════════

    private static async Task SeedParentUserAsync(
        EdvanzDbContext context,
        string hashedPassword,
        DateTime now,
        string fullName,
        string username,
        string email,
        string phoneNumber)
    {
        var user = new User
        {
            UserType       = UserType.Parent,
            FullName       = fullName,
            Username       = username,
            Email          = email,
            PhoneNumber    = phoneNumber,
            PasswordHashed = hashedPassword,
            IsActive       = true,
            IsVerified     = true,
            CreateAt       = now
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        context.ParentUsers.Add(new ParentUser
        {
            UserId             = user.Id,
            LanguagePreference = EnglishLanguage,
            AccountStatus      = AccountStatus.Active,
            CreateAt           = now
        });
        await context.SaveChangesAsync();
    }
}
