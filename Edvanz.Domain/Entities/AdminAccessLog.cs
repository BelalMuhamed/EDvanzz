using Edvanz.Domain.Entities.ShareProp;
using System.ComponentModel.DataAnnotations;

namespace Edvanz.Domain.Entities;

/// <summary>
/// Records every request a SuperAdmin makes while VIEWING A TEACHER'S DATA through the read-only
/// "view as teacher" path (the <c>X-Acting-Teacher-Id</c> header).
///
/// Two reasons this exists:
///  1. Looking at a tenant's real data is a privileged act and must leave a trail naming who looked,
///     at whom, and when.
///  2. It closes a standing gap — <c>AuditTrail</c> only ever recorded ASSISTANT actions (and in
///     practice only admin module grants), so nothing a SuperAdmin did was audited anywhere.
///
/// Write-only from the application's perspective: rows are appended by the read-only impersonation
/// filter and never updated. Both id columns are plain audit columns with NO FK, so purging a teacher
/// or an admin can never cascade into or block deletion of the trail they appear in.
/// </summary>
public class AdminAccessLog : BaseEntity
{
    /// <summary>The SuperAdmin who made the request.</summary>
    public long AdminUserId { get; set; }

    /// <summary>Denormalised admin name captured at write time, so the trail stays readable forever.</summary>
    [MaxLength(128)]
    public string AdminName { get; set; } = null!;

    /// <summary>The teacher whose data was being read.</summary>
    public long TeacherId { get; set; }

    /// <summary>HTTP method + path that was served, e.g. <c>GET /api/teacherstudent/students</c>.</summary>
    [MaxLength(512)]
    public string Route { get; set; } = null!;
}
