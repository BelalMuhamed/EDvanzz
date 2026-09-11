using Edvanz.Domain.Entities;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace Edvanz.Infrastructure.Repositories
{
    public class TeacherModuleRepo : GenericRepo<TemplatePermissionsUsers, (long, long)>, IModuleTeacherRepo
    {
        public TeacherModuleRepo(EdvanzDbContext context) : base(context)
        {
        }

        public async Task<List<Module>> GetModulesPerTeacher(long teacherId)
        {
            return await _context.TutorModuleAccess.Where(t=>t.TutorId==teacherId)
                .Select(tpu => tpu.module).ToListAsync();

        }

        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<long, List<string>>> GetModuleNamesByTeacherIdsAsync(
            IReadOnlyCollection<long> teacherIds)
        {
            if (teacherIds.Count == 0)
                return new Dictionary<long, List<string>>();

            // ONE query for every teacher in the rollup. The per-teacher reads above would be a few
            // hundred round trips across a nightly pass; the composite PK (TutorId, ModuleId) serves
            // the predicate, and only the two columns needed cross the wire.
            var rows = await _context.TutorModuleAccess
                .AsNoTracking()
                .Where(t => teacherIds.Contains(t.TutorId))
                .Select(t => new { t.TutorId, t.module.Name })
                .ToListAsync();

            return rows
                .GroupBy(r => r.TutorId)
                .ToDictionary(g => g.Key, g => g.Select(r => r.Name).ToList());
        }
        // ════════════════════════════════════════════════════════════════════════════
        // EXTENSION TO EXISTING TeacherModuleRepo (Edvanz.Infrastructure.Repositories)
        // ════════════════════════════════════════════════════════════════════════════
        //
        // Splice the following method body into the EXISTING TeacherModuleRepo class
        // alongside GetModulesPerTeacher. Do NOT replace the file — just add this one
        // method.
        //
        // The corresponding interface declaration was added to IModuleTeacherRepo in
        // Phase 3.
        // ════════════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<bool> IsModuleActiveAsync(long teacherId, string moduleName)
        {
            // BR-ADM-010 instantaneous deactivation gate for student/parent endpoints
            // that have no `module` claim in their JWT. Single indexed lookup against
            // TutorModuleAccess joined to Models for the name match — sub-millisecond.
            //
            // Presence of the row IS activation. Absence (because the super-admin
            // removed it) IS deactivation. There is no IsActive flag on TutorModule
            // by design (Phase 2 review confirmed this matches the existing schema).
            return await _context.TutorModuleAccess
                   .AnyAsync(t => t.TutorId == teacherId
                               && t.module.Name == moduleName);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<long>> GetTutorModuleIdsAsync(long teacherId)
        {
            // Lightweight id-only projection — used by the service layer to diff
            // desired vs current module sets without materializing Module rows.
            return await _context.TutorModuleAccess
                .AsNoTracking()
                .Where(t => t.TutorId == teacherId)
                .Select(t => t.ModuleId)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<bool> GrantModuleAsync(long teacherId, long moduleId)
        {
            // Idempotency check: presence of the row IS the grant. We check existence
            // first (AsNoTracking, AnyAsync, sub-millisecond on the indexed composite
            // key) rather than relying on a unique-constraint violation, because the
            // latter would force the caller to handle DbUpdateException for a benign
            // case. EF Core's change tracker is bypassed for the existence check.
            bool exists = await _context.TutorModuleAccess
                .AsNoTracking()
                .AnyAsync(t => t.TutorId == teacherId && t.ModuleId == moduleId);

            if (exists)
                return false;

            await _context.TutorModuleAccess.AddAsync(new TutorModule
            {
                TutorId = teacherId,
                ModuleId = moduleId
            });

            return true;
        }

        /// <inheritdoc />
        public async Task<bool> RevokeModuleAsync(long teacherId, long moduleId)
        {
            // Load the tracked entity (not AsNoTracking — we're about to delete it).
            // FirstOrDefaultAsync over the indexed FK pair; null means nothing to do.
            var row = await _context.TutorModuleAccess
                .FirstOrDefaultAsync(t => t.TutorId == teacherId && t.ModuleId == moduleId);

            if (row is null)
                return false;

            _context.TutorModuleAccess.Remove(row);
            return true;
        }
    }
}