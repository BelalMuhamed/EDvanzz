using Edvanz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Edvanz.Domain.Interfaces
{
    public interface IModuleTeacherRepo:IGenericRepo<TemplatePermissionsUsers,(long,long)>
    {
        public  Task<List<Module>> GetModulesPerTeacher(long teacherId);

        /// <param name="moduleName">The module name as seeded in <c>Models.Name</c>
        /// (e.g. <c>VideoConstants.ModuleName</c>).</param>
        Task<bool> IsModuleActiveAsync(long teacherId, string moduleName);

        /// <summary>
        /// Returns the set of <c>ModuleId</c>s currently granted to the given tutor —
        /// the same data as <see cref="GetModulesPerTeacher"/> but as a lightweight
        /// id-only projection used by the service layer to diff a desired state
        /// against the current state without materializing full <c>Module</c> rows.
        ///
        /// Used by:
        ///   - <c>TutorModuleAccessService.GrantAsync</c> / <c>RevokeAsync</c> to detect
        ///     idempotent no-ops (REQ-ADM-035 "takes effect immediately upon saving"
        ///     interpreted as "must not error when applying the same state twice").
        ///   - <c>TutorModuleAccessService.BulkReplaceAsync</c> to compute the
        ///     adds/removes diff against the request payload (REQ-ADM-051 / 053).
        /// </summary>
        Task<IReadOnlyList<long>> GetTutorModuleIdsAsync(long teacherId);

        /// <summary>
        /// Granted module NAMES for MANY teachers in one query — the entitlement half of the admin
        /// usage model. Every other read here is single-teacher, which would be a few hundred round
        /// trips across a nightly rollup. Index-served by the composite PK (TutorId, ModuleId).
        /// Teachers with no grants are absent from the dictionary rather than present-and-empty.
        /// </summary>
        Task<IReadOnlyDictionary<long, List<string>>> GetModuleNamesByTeacherIdsAsync(
            IReadOnlyCollection<long> teacherIds);

        /// <summary>
        /// Grants a module to a tutor by inserting a row into <c>TutorModuleAccess</c>.
        /// Idempotent: if the (TutorId, ModuleId) pair already exists, returns false
        /// without raising — the caller (service layer) treats false as "no state
        /// change → skip invalidation and audit write".
        ///
        /// Does NOT call <c>SaveChangesAsync</c> — the change participates in the
        /// caller's transaction.
        /// </summary>
        /// <param name="teacherId">The Teacher.Id receiving the module grant.</param>
        /// <param name="moduleId">The Module.Id to grant.</param>
        /// <returns>True when a new row was queued for insert; false when the row
        /// already existed and nothing changed.</returns>
        Task<bool> GrantModuleAsync(long teacherId, long moduleId);

        /// <summary>
        /// Revokes a module from a tutor by deleting the matching row in
        /// <c>TutorModuleAccess</c>. Idempotent: if no row exists, returns false
        /// without raising.
        ///
        /// REQ-ADM-050: data created while the module was active is preserved —
        /// only the grant row is removed. Existing <c>UsersPermissions</c> rows
        /// referencing module-scoped permissions are also preserved (they become
        /// dormant until the module is re-granted).
        ///
        /// Does NOT call <c>SaveChangesAsync</c>.
        /// </summary>
        /// <param name="teacherId">The Teacher.Id losing the module grant.</param>
        /// <param name="moduleId">The Module.Id to revoke.</param>
        /// <returns>True when a row was queued for delete; false when no row
        /// existed and nothing changed.</returns>
        Task<bool> RevokeModuleAsync(long teacherId, long moduleId);

    }
}


