using Edvanz.Application.Security;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Localization;

namespace Edvanz.API.Filters
{
    /// <summary>
    /// Backing filter for <see cref="Edvanz.API.Attributes.ModulePermissionAttribute"/>.
    ///
    /// Runs in the MVC authorization-filter pipeline (NOT ASP.NET Core's
    /// <c>UseAuthorization</c> middleware), so <see cref="IAuthorizationMiddlewareResultHandler"/>
    /// never sees its results — a bare <see cref="ForbidResult"/> here would reach the client as
    /// an empty 403. This filter therefore builds the localized JSON envelope itself, reading the
    /// <see cref="AuthorizationFailureReason"/> markers <see cref="PermissionHandler"/> attaches to
    /// distinguish "module not assigned" from "permission denied" / "role not allowed".
    /// </summary>
    public class ModulePermissionFilter : IAsyncAuthorizationFilter
    {
        private readonly string _module;
        private readonly string? _permission;
        private readonly string? _alsoAllowPermission;
        private readonly IAuthorizationService _authService;
        private readonly IStringLocalizer<Messages> _localizer;
        private readonly bool _roleOnly;
        private readonly string[] _roles;

        public ModulePermissionFilter(
            string module,
            string permission,
            string[] roles,
            IAuthorizationService authService,
            IStringLocalizer<Messages> localizer,
            bool roleOnly = false,
            string alsoAllowPermission = "")
        {
            _module = module;
            _permission = string.IsNullOrWhiteSpace(permission) ? null : permission;
            _alsoAllowPermission = string.IsNullOrWhiteSpace(alsoAllowPermission) ? null : alsoAllowPermission;
            _roles = roles ?? Array.Empty<string>();
            _authService = authService;
            _localizer = localizer;
            _roleOnly = roleOnly;
        }

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;

            if (_roleOnly)
            {
                if (_roles.Any(r => user.IsInRole(r)))
                    return;

                // Center tier operates its teachers by "acting as" one per request. So any endpoint
                // gated to the Teacher role is also reachable by a Center / CenterAssistant login —
                // tenant isolation is enforced by the acting-as tenant resolvers (X-Acting-Teacher-Id
                // validated against center membership), NOT by this role gate. Endpoints gated to
                // SuperAdmin / Student / Parent only (no "Teacher" in the array) stay closed to centers.
                if (_roles.Any(r => string.Equals(r, UserRoles.Teacher, StringComparison.Ordinal))
                    && (user.IsInRole(UserRoles.Center) || user.IsInRole(UserRoles.CenterAssistant)))
                    return;

                context.Result = ForbidWithMessage(_localizer["RoleNotAllowed"].Value, "roleNotAllowed");
                return;
            }

            var requirement = new PermissionRequirement(_module, _permission);
            var result = await _authService.AuthorizeAsync(user, null, requirement);

            if (result.Succeeded)
                return;

            // Optional second grant (see ModulePermissionAttribute.alsoAllowPermission): re-check
            // against the same module. Widen-only — the primary permission still passes on its own,
            // so no existing caller can lose access. A module-not-assigned failure short-circuits:
            // no permission in a module the tutor scope doesn't have can rescue the request.
            if (_alsoAllowPermission is not null
                && result.Failure?.FailureReasons
                    .Any(r => r.Message == PermissionHandler.ModuleNotAssignedReason) != true)
            {
                var fallback = await _authService.AuthorizeAsync(
                    user, null, new PermissionRequirement(_module, _alsoAllowPermission));
                if (fallback.Succeeded)
                    return;
            }

            bool moduleNotAssigned = result.Failure?.FailureReasons
                .Any(r => r.Message == PermissionHandler.ModuleNotAssignedReason) == true;

            context.Result = moduleNotAssigned
                ? ForbidWithMessage(_localizer["ModuleNotAssigned"].Value, "moduleAccessDenied")
                : ForbidWithMessage(_localizer["PermissionDenied"].Value, "permissionDenied");
        }

        /// <summary>
        /// Builds the standard 403 envelope: <c>{ success: false, message: &lt;localized&gt;, [flagKey]: true }</c>.
        /// Replaces the framework's bare <see cref="ForbidResult"/>, which produces no response body
        /// for MVC-filter-level rejections (this filter never reaches <c>UseAuthorization</c> middleware).
        /// </summary>
        private static ObjectResult ForbidWithMessage(string message, string flagKey) =>
            new(new Dictionary<string, object>
            {
                ["success"] = false,
                ["message"] = message,
                [flagKey] = true
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
    }
}