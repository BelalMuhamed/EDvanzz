using Edvanz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Edvanz.Domain.Interfaces
{
    public interface IStudentTeacherLinkRepo:IGenericRepo<StudentTeacherLink,(long,long)>
    {
        public Task<(long studentAccountId, List<long> teacherIds)> GetSudentAccountLinkedTeacherIdsByUserId(long userId);

        /// <summary>
        /// Batch count of ACTIVE student-account links per teacher (students who connected their
        /// account) for a set of teachers — one GROUP BY for the admin teacher list. Teachers with
        /// no active links are absent from the result.
        /// </summary>
        Task<Dictionary<long, int>> GetActiveLinkedCountsAsync(IReadOnlyCollection<long> teacherIds);

        /// <summary>
        /// Live count of the teacher's CONSUMED student-app-account seats: links that are
        /// <see cref="Domain.Enums.LinkStatus.Active"/> AND bound to a student record
        /// (<c>TeacherStudentId != null</c>). An accepted-but-unbound connection consumes
        /// nothing. This is the number checked against <c>Teacher.LinkedStudentCapacity</c>
        /// on every bind, and the one reported as usage on the linked-students page.
        /// </summary>
        Task<int> CountBoundLinksAsync(long teacherId);

        /// <summary>
        /// BATCHED sibling of <see cref="CountBoundLinksAsync"/>: consumed student-app-account
        /// seats per teacher (links that are Active AND bound to a student record) for a set of
        /// teachers, in ONE GROUP BY — the admin teacher list is paged and hot, so it must never
        /// count seats per row. Teachers with no bound links are absent from the result.
        ///
        /// Deliberately distinct from <see cref="GetActiveLinkedCountsAsync"/>, which counts every
        /// ACTIVE (connected) link including accepted-but-unbound ones. Only the BOUND number is
        /// the one measured against Teacher.LinkedStudentCapacity.
        /// </summary>
        Task<Dictionary<long, int>> GetBoundLinkedCountsAsync(IReadOnlyCollection<long> teacherIds);
    }
}
