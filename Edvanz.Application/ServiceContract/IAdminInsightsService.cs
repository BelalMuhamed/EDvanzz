using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.AdminInsights;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// The SuperAdmin insights surface: who is actually using Edvanz, how often, how deeply, and who on
/// the account is doing the work — plus the sales attribution and internal notes that turn those
/// numbers into a conversation someone can have.
///
/// Reads come from the pre-computed <c>TeacherUsageSnapshots</c> table, rebuilt nightly by
/// <see cref="ITeacherUsageRollupService"/>. Nothing here aggregates raw domain tables at request
/// time.
/// </summary>
public interface IAdminInsightsService
{
    /// <summary>
    /// The admin landing page in one call: headline counts with a real previous-period delta, the
    /// three axis distributions, module adoption, and the named insight cards.
    /// </summary>
    Task<Result<AdminOverviewDto>> GetOverviewAsync();

    /// <summary>One page of the usage grid, filtered and sorted in SQL.</summary>
    Task<Result<PaginatedResponse<List<TeacherUsageListItemDto>>>> GetUsageGridAsync(
        TeacherUsageQueryRequest request);

    /// <summary>Everything the Teacher 360 usage tab draws for one teacher.</summary>
    Task<Result<TeacherUsageDetailDto>> GetTeacherUsageAsync(long teacherId);

    /// <summary>
    /// The full named list behind one insight card, paged — what "see all 34" opens.
    /// </summary>
    Task<Result<PaginatedResponse<List<TeacherUsageListItemDto>>>> GetInsightTeachersAsync(
        string insightKey, int page, int pageSize);

    /// <summary>
    /// Recomputes one teacher's usage on demand, instead of waiting for tonight's run. Used after a
    /// support action, or when an admin distrusts a number in front of them.
    /// </summary>
    Task<Result<TeacherUsageDetailDto>> RecomputeTeacherUsageAsync(long teacherId, int days);

    /// <summary>
    /// Exports the CURRENTLY FILTERED teachers as a CSV, with everything needed to identify and
    /// contact each one — including their admin notes.
    ///
    /// Deliberately exports the whole filtered set, not the page on screen: the point of an export
    /// is to hand a rep their call list, and a list truncated at 25 rows is worse than none. Bounded
    /// by <c>AdminInsightsConstants.CsvExportMaxRows</c>.
    /// </summary>
    Task<Result<byte[]>> ExportTeachersCsvAsync(TeacherUsageQueryRequest request);

    // ── Sales attribution ──────────────────────────────────────────────────────

    /// <summary>Every sales rep with their book of accounts rolled up by outcome.</summary>
    Task<Result<List<SalesRepDto>>> GetSalesRepsAsync(bool includeInactive);

    /// <summary>Adds a sales rep.</summary>
    Task<Result<SalesRepDto>> CreateSalesRepAsync(SaveSalesRepRequest request);

    /// <summary>Updates a sales rep. Deactivating keeps their name on the teachers they brought in.</summary>
    Task<Result<SalesRepDto>> UpdateSalesRepAsync(long salesRepId, SaveSalesRepRequest request);

    /// <summary>Assigns or clears a teacher's sales attribution and acquisition source.</summary>
    Task<Result<TeacherUsageListItemDto>> AssignSalesRepAsync(long teacherId, AssignSalesRepRequest request);

    // ── Internal notes ─────────────────────────────────────────────────────────

    /// <summary>A teacher's notes, pinned first then newest.</summary>
    Task<Result<List<AdminNoteDto>>> GetNotesAsync(long teacherId);

    /// <summary>Adds a note, stamped with the acting admin's id and name.</summary>
    Task<Result<AdminNoteDto>> CreateNoteAsync(long teacherId, CreateAdminNoteRequest request, long authorUserId);

    /// <summary>Soft-deletes a note. The row survives for audit.</summary>
    Task<Result<bool>> DeleteNoteAsync(long teacherId, long noteId);
}
