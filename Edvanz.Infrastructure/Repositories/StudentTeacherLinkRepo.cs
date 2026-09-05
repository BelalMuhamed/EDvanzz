using Edvanz.Domain.Entities;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace Edvanz.Infrastructure.Repositories
{
    public class StudentTeacherLinkRepo : GenericRepo<StudentTeacherLink, (long, long)>, IStudentTeacherLinkRepo
    {
        public StudentTeacherLinkRepo(EdvanzDbContext context) : base(context)
        {
        }

        /// <summary>
        /// Resolves the ACTIVE linked teacher ids for a student account from the
        /// USER id (JWT identity). Joins Users → StudentUsers because
        /// StudentTeacherLink.StudentUserId is the StudentUser.Id, not the User.Id —
        /// the original implementation compared the two id spaces directly and also
        /// ignored LinkStatus, so login could report wrong/stale teacher ids.
        /// Pending/Rejected/removed links are excluded by design.
        /// </summary>
        public async Task<(long studentAccountId, List<long> teacherIds)> GetSudentAccountLinkedTeacherIdsByUserId(long userId)
        {
            var studentUser = await _context.StudentUsers
                .AsNoTracking()
                .FirstOrDefaultAsync(su => su.UserId == userId && su.DeletedAt == null);

            if (studentUser is null)
                return (0, new List<long>());

            var teacherIds = await _context.StudentTeacherLinks
                .AsNoTracking()
                .Where(link => link.StudentUserId == studentUser.Id &&
                               link.LinkStatus == Domain.Enums.LinkStatus.Active)
                .Select(link => link.TeacherId)
                .Distinct()
                .ToListAsync();

            return (studentUser.Id, teacherIds);
        }

        /// <inheritdoc />
        public async Task<Dictionary<long, int>> GetActiveLinkedCountsAsync(
            IReadOnlyCollection<long> teacherIds)
        {
            if (teacherIds.Count == 0) return new Dictionary<long, int>();

            return await _context.StudentTeacherLinks
                .AsNoTracking()
                .Where(l => teacherIds.Contains(l.TeacherId) &&
                            l.LinkStatus == Domain.Enums.LinkStatus.Active)
                .GroupBy(l => l.TeacherId)
                .Select(g => new { TeacherId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TeacherId, x => x.Count);
        }

        /// <inheritdoc />
        public async Task<int> CountBoundLinksAsync(long teacherId)
        {
            // BOUND links only — an Active-but-unbound connection ("accepted, Not linked")
            // sees no data and therefore consumes no paid seat.
            return await _context.StudentTeacherLinks
                .AsNoTracking()
                .CountAsync(l => l.TeacherId == teacherId &&
                                 l.LinkStatus == Domain.Enums.LinkStatus.Active &&
                                 l.TeacherStudentId != null);
        }

        /// <inheritdoc />
        public async Task<Dictionary<long, int>> GetBoundLinkedCountsAsync(
            IReadOnlyCollection<long> teacherIds)
        {
            if (teacherIds.Count == 0) return new Dictionary<long, int>();

            // ONE GROUP BY for the whole page — same shape as GetActiveLinkedCountsAsync, with the
            // extra TeacherStudentId filter that makes it a SEAT count rather than a connection count.
            return await _context.StudentTeacherLinks
                .AsNoTracking()
                .Where(l => teacherIds.Contains(l.TeacherId) &&
                            l.LinkStatus == Domain.Enums.LinkStatus.Active &&
                            l.TeacherStudentId != null)
                .GroupBy(l => l.TeacherId)
                .Select(g => new { TeacherId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TeacherId, x => x.Count);
        }


    }
}
