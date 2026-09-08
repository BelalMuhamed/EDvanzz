namespace Edvanz.Domain.Constants;

/// <summary>
/// Single-sourced constants for the Teacher account surface.
///
/// The display-name bounds live here because <c>Users.FullName</c> is <c>nvarchar(max)</c> with no
/// index and no DB constraint (deliberate — two teachers may legitimately share a name, and
/// identity is the random, name-independent <c>TeacherCode</c>). The database therefore enforces
/// nothing, so the app layer is the ONLY guard and both the mobile client and the Angular admin
/// must agree on the same numbers.
/// </summary>
public static class TeacherConstants
{
    /// <summary>Shortest accepted display name after trimming. Below this it identifies nobody.</summary>
    public const int FullNameMinLength = 2;

    /// <summary>
    /// Longest accepted display name. Matches the Angular admin's <c>Validators.maxLength(120)</c>
    /// so a name saved in one surface can never be rejected by the other.
    /// </summary>
    public const int FullNameMaxLength = 120;

    /// <summary>Name of the rate-limiter policy applied to teacher profile writes.</summary>
    public const string ProfileUpdateRateLimitPolicy = "profile-update";
}
