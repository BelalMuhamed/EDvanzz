using Edvanz.API.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Edvanz.API.Attributes
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public class ModulePermissionAttribute : TypeFilterAttribute
    {
        /// <param name="alsoAllowPermission">
        /// Optional SECOND permission that also satisfies this gate (checked only if
        /// <paramref name="permission"/> fails). Purely additive — it can widen access, never
        /// revoke it — for an endpoint that legitimately serves two grants. Used by
        /// <c>GET /api/v1/payments/collections</c>, where the assistant's own (force-scoped)
        /// ledger is the same data their <c>ViewCollectorSummary</c> wallet screen already shows,
        /// so requiring <c>ViewHistory</c> on top would 403 assistants granted only the former.
        /// Ignored when <paramref name="roleOnly"/> is set.
        /// </param>
        public ModulePermissionAttribute(
            string module = "",
            string? permission = null,
            string[]? roles = null,
            bool roleOnly = false,
            string? alsoAllowPermission = null)
            : base(typeof(ModulePermissionFilter))
        {
            Arguments = new object[]
            {
            module ?? "",
            permission ?? "",
            roles ?? Array.Empty<string>(),
            roleOnly,
            alsoAllowPermission ?? ""
            };
        }
    }
}
