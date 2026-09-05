using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Auth;
using Edvanz.Application.Dtos.ParentUser;
using Edvanz.Application.Dtos.StudentUser;
using Edvanz.Application.Dtos.Teacher;
using Edvanz.Application.Dtos.UserDto;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Edvanz.Domain.ServiceContract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Edvanz.Application.Services
{
    /// <summary>
    /// Implements user registration operations.
    /// Creates the shared User record and delegates type-specific initialization
    /// to ITeacherService, IStudentUserService, or IParentUserService.
    /// 
    /// All database access goes through IUnitOfWork.Users (IUserRepo) — no direct
    /// GetRepository calls with raw expression predicates.
    /// 
    /// TRANSACTION SAFETY:
    /// AddUser starts a transaction that wraps both the User row creation and the
    /// type-specific initialization. The type-specific services detect HasActiveTransaction
    /// and participate in this outer transaction rather than starting their own.
    /// </summary>
    // FIX I3: Renamed from IuserService to IUserService (PascalCase per .NET conventions)
    public class UserService : IUserService
    {
        private readonly IStringLocalizer<Messages> _localizer;
        private readonly IOtpService _otpService;
        // FIX I3: Renamed from passServicr to _passwordService (correct spelling, underscore prefix)
        private readonly IPasswordService _passwordService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ICurrentUserService currentUserService;
        private readonly ITeacherService teacherService;
        private readonly IStudentUserService studentService;
        private readonly IParentUserService parentUserService;
        private readonly IFileStorageService _fileStorage;

        /// <summary>
        /// Initializes a new instance of UserService with required dependencies.
        /// </summary>
        /// <param name="localizer">String localizer for multilingual error/success messages.</param>
        /// <param name="otpService">OTP generation and verification service.</param>
        /// <param name="passwordService">Password hashing and verification service.</param>
        /// <param name="unitOfWork">Unit of work for database operations.</param>
        public UserService(
            IStringLocalizer<Messages> localizer,
            IOtpService otpService,
            IPasswordService passwordService,
            IUnitOfWork unitOfWork,ICurrentUserService _currentUserService,ITeacherService teacherService,IStudentUserService studentService ,IParentUserService parentUserService,
            IFileStorageService fileStorage)
        {
            _localizer = localizer;
            _otpService = otpService;
            _passwordService = passwordService;
            _unitOfWork = unitOfWork;
            currentUserService = _currentUserService;
            this.teacherService = teacherService;
            this.studentService = studentService;
            this.parentUserService = parentUserService;
            _fileStorage = fileStorage;
        }

        /// <inheritdoc />
       
        public async Task<Result<string?>> AddUser(SigupDto user)
        {
            if (user == null)
                return Result<string?>.Failure(_localizer, "cann't add empty user");
            var allowedSelfRegistration = new[] { UserType.Teacher, UserType.Student, UserType.Parent };
            if (!allowedSelfRegistration.Contains(user.userType))
                return Result<string?>.Failure(_localizer, "InvalidUserType");
            if (user.password != user.confirmedPassword)
                return Result<string?>.Failure(_localizer, "password must be equail confirmed password");

            // Normalize credentials up front so both the duplicate checks below and the stored row
            // are trimmed/consistent (defense-in-depth; the client already trims).
            user.username = user.username?.Trim() ?? string.Empty;
            user.phoneNumber = user.phoneNumber?.Trim();
            user.email = string.IsNullOrWhiteSpace(user.email) ? null : user.email.Trim();

            if (string.IsNullOrWhiteSpace(user.phoneNumber))
                return Result<string?>.Failure(_localizer, "PhoneNumberRequired");

            if (!PhoneNumberValidator.IsValidEgyptianMobile(user.phoneNumber))
                return Result<string?>.Failure(_localizer, "PhoneNumberInvalidFormat");

            // Duplicate pre-check — query EACH credential SEPARATELY so the DB's own case- and
            // trailing-space-insensitive collation decides the match, exactly like the unique
            // indexes (UX_Users_Username / UX_Users_PhoneNumber) do. The old single-lookup + C# "=="
            // re-check was ordinal/case-SENSITIVE, so a case- or whitespace-differing duplicate
            // (stored "Mohamed" vs typed "mohamed") passed every branch, fell through to the INSERT,
            // and surfaced as the raw "DatabaseConflict" (SQL 2601 on UX_Users_Username) instead of a
            // friendly "username already taken". (Reproduced against SQL Server 2022, CI collation.)
            if (await _unitOfWork.Users
                    .FindExistingUserByCredentialsAsync(user.phoneNumber, string.Empty, null) is not null)
                return Result<string?>.Failure(_localizer, "repeatedPhoneNumber");

            if (await _unitOfWork.Users
                    .FindExistingUserByCredentialsAsync(string.Empty, user.username, null) is not null)
                return Result<string?>.Failure(_localizer, "repeatedUserName");

            if (!string.IsNullOrEmpty(user.email) && await _unitOfWork.Users
                    .FindExistingUserByCredentialsAsync(string.Empty, string.Empty, user.email) is not null)
                return Result<string?>.Failure(_localizer, "repeatedEmail");
            if(user.userType==UserType.Teacher && (user.subjectIds == null || user.subjectIds.Count == 0) )
            {
                return Result<string>.Failure(_localizer, "SubjectRequired");
            }
               

            // =============================
            // Hash password
            // =============================

            var hashedPass = _passwordService.HashPassword(user.password);

            var addedUser = new User
            {
                CreateAt = DateTime.UtcNow,
                CreateByUserId = currentUserService.UserId,
                Email = user.email,
                FullName = user.fullName,
                Username = user.username,
                PhoneNumber = user.phoneNumber,
                IsActive = true,
                PasswordHashed = hashedPass,
                UserType = user.userType
            };

            await _unitOfWork.BeginTransactionAsync();

            try
            {
                await _unitOfWork.Users.AddAsync(addedUser);

                await _unitOfWork.SaveChangesAsync();

                // National-ID image (optional) — stored server-side in the private uploads container
                // and recorded in the central registry, since sign-up has no pre-account JWT to
                // authorize a separate /api/upload. Access = owner + SuperAdmin via the gated endpoint.
                if (user.idImage is not null)
                {
                    string ext = Path.GetExtension(user.idImage.FileName);
                    string blobPath = $"files/{addedUser.Id}/{Guid.NewGuid():N}{ext}";
                    await using (var stream = user.idImage.OpenReadStream())
                    {
                        await _fileStorage.UploadAsync(blobPath, stream, user.idImage.ContentType);
                    }

                    var idFile = new FileObject
                    {
                        PublicId = Guid.NewGuid(),
                        OwnerUserId = addedUser.Id,
                        TeacherId = null,
                        Category = FileCategory.NationalIdImage,
                        Status = FileStatus.Attached,
                        BlobPath = blobPath,
                        ContentType = user.idImage.ContentType,
                        SizeBytes = user.idImage.Length,
                        OriginalName = Path.GetFileName(user.idImage.FileName) ?? string.Empty,
                        CreateAt = DateTime.UtcNow,
                    };
                    await _unitOfWork.FileObjectsRepo.AddAsync(idFile);
                    await _unitOfWork.SaveChangesAsync();

                    addedUser.IdImageFileId = idFile.Id;
                    await _unitOfWork.SaveChangesAsync();
                }

                switch (user.userType)
                {
                    case UserType.Teacher:

                        var teacherDto = new CreateTeacherDto
                        {
                            UserId = addedUser.Id,
                            CreatedByUserId = currentUserService.UserId,
                            LanguagePreference = user.languagePreference,
                            CustomSubject = user.customSubject,
                            // 500 aligns the entity default, CreateTeacherDto default, and
                            // REQ-STU-002 (this path used to say 50 — the lone outlier).
                            StudentCapacity = user.studentCapacity ?? 500,
                            // Null flows through as null — InitializeTeacherAsync then mirrors
                            // StudentCapacity, preserving pre-existing behaviour for old clients.
                            LinkedStudentCapacity = user.linkedStudentCapacity,
                            SubjectIds = user.subjectIds ?? new List<long>()
                        };

                        var teacherResult = await teacherService.InitializeTeacherAsync(teacherDto);

                        if (!teacherResult.IsSuccess)
                        {
                            await _unitOfWork.RollbackAsync();
                            return Result<string?>.Failure(_localizer, teacherResult.Message);
                        }

                        break;


                    case UserType.Student:

                        var studentDto = new CreateStudentUserDto
                        {
                            UserId = addedUser.Id,
                            LanguagePreference = user.languagePreference
                        };

                        var studentResult = await studentService.InitializeStudentUserAsync(studentDto);

                        if (!studentResult.IsSuccess)
                        {
                            await _unitOfWork.RollbackAsync();
                            return Result<string?>.Failure(_localizer, studentResult.Message);
                        }

                        break;


                    case UserType.Parent:

                        var parentDto = new CreateParentUserDto
                        {
                            UserId = addedUser.Id,
                            LanguagePreference = user.languagePreference
                        };

                        var parentResult = await parentUserService.InitializeParentUserAsync(parentDto);

                        if (!parentResult.IsSuccess)
                        {
                            await _unitOfWork.RollbackAsync();
                            return Result<string?>.Failure(_localizer, parentResult.Message);
                        }

                        break;
                }

                await _unitOfWork.CommitAsync();

                return Result<string?>.Success(null, _localizer, "SuccessSaving");
            }
            catch (DbUpdateException ex) when (ResolveUserUniqueViolationKey(ex) is { } messageKey)
            {
                // Safety net: a concurrent sign-up or a legacy unnormalized row can still trip a Users
                // unique index between the pre-check and the INSERT. Map it to the same friendly
                // message instead of surfacing the raw "DatabaseConflict".
                await _unitOfWork.RollbackAsync();
                return Result<string?>.Failure(_localizer, messageKey, HttpStatusCode.Conflict);
            }
            catch
            {
                await _unitOfWork.RollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Maps a SQL Server unique-key violation (2601/2627) on the Users table to the matching
        /// localized message key by inspecting the column embedded in the index name. Returns null
        /// when the exception is not a Users unique violation, so it rethrows unchanged. Mirrors
        /// <c>AssistantService.ResolveUserUniqueViolationKey</c>.
        /// </summary>
        private static string? ResolveUserUniqueViolationKey(DbUpdateException ex)
        {
            var sql = ex.InnerException as Microsoft.Data.SqlClient.SqlException
                      ?? ex.GetBaseException() as Microsoft.Data.SqlClient.SqlException;

            if (sql is not { Number: 2601 or 2627 })
                return null;

            string message = sql.Message;

            if (message.Contains("PhoneNumber", StringComparison.OrdinalIgnoreCase))
                return "repeatedPhoneNumber";
            if (message.Contains("Username", StringComparison.OrdinalIgnoreCase))
                return "repeatedUserName";
            if (message.Contains("Email", StringComparison.OrdinalIgnoreCase))
                return "repeatedEmail";

            return null;
        }

        /// <summary>
        /// Egyptian-mobile rule (11 digits, 010/011/012/015), shared by the Teacher / Student /
        /// Parent / Google paths.
        ///
        /// The implementation was EXTRACTED to <see cref="Common.EgyptianPhoneNumber"/> so the
        /// parent portal — which also needs to NORMALIZE user-typed numbers — can reuse the exact
        /// same rule instead of copying the regex. This shim stays because several services import
        /// it via <c>using static Edvanz.Application.Services.UserService;</c>; it forwards, so
        /// behaviour is byte-for-byte unchanged. New code should call
        /// <see cref="Common.EgyptianPhoneNumber"/> directly.
        /// </summary>
        public static class PhoneNumberValidator
        {
            /// <inheritdoc cref="Common.EgyptianPhoneNumber.IsValidEgyptianMobile"/>
            public static bool IsValidEgyptianMobile(string? phone) =>
                Common.EgyptianPhoneNumber.IsValidEgyptianMobile(phone);
        }

        //public async Task<Result<string>> DeactiveUser(long userId)
        //{
        //    var user =await _unitOfWork.Users.GetByIdAsync(userId);
        //    if (user == null)
        //        return Result<string>.Failure(_localizer, "UserNotFound");
        //    if(user.IsActive == false)
        //        return Result<string>.Failure(_localizer, "UserAlreadyDeactive");

        //}
    }
}