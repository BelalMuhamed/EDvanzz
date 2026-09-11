using System.Globalization;
using System.Text;

namespace Edvanz.Application.Services;

/// <summary>
/// CSV writer for the admin exports.
///
/// Two things here are not optional, and both are the kind of detail that silently ruins an export:
///
/// 1. **UTF-8 WITH A BOM.** Every teacher on this platform may have an Arabic name, and Excel on
///    Windows reads a BOM-less UTF-8 file as the local ANSI codepage — so "محمد عبد الرحمن" opens as
///    mojibake and the export is useless to the person who asked for it. The BOM is what makes Excel
///    pick UTF-8.
///
/// 2. **FORMULA-INJECTION GUARD.** A cell beginning =, +, -, @, tab or CR is executed as a formula
///    by Excel and Google Sheets. These exports carry free text written by admins (notes) and names
///    supplied at sign-up, so a value like <c>=HYPERLINK(...)</c> in a teacher name or a note would
///    run on the machine of whoever opens the file. Every such cell is prefixed with an apostrophe,
///    which Excel strips on display but never executes.
/// </summary>
public static class AdminInsightsCsv
{
    /// <summary>Characters that make Excel treat a cell as a formula.</summary>
    private static readonly char[] FormulaTriggers = { '=', '+', '-', '@', '\t', '\r' };

    /// <summary>
    /// Renders a header row plus data rows as a UTF-8 CSV byte array, BOM included.
    /// </summary>
    public static byte[] Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();

        sb.AppendLine(string.Join(',', headers.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(',', row.Select(Escape)));

        // The BOM must be written EXPLICITLY. `new UTF8Encoding(true).GetBytes(...)` does NOT emit
        // it — that flag only governs GetPreamble(), which StreamWriter would call but GetBytes
        // never does. Relying on the flag alone silently produced a BOM-less file, and Excel then
        // read every Arabic name as mojibake.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(sb.ToString());

        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>
    /// Quotes and escapes one cell, and neutralises anything Excel would run as a formula.
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        // Neutralise BEFORE quoting: the apostrophe has to end up inside the quoted value.
        if (FormulaTriggers.Contains(value[0]))
            value = "'" + value;

        // A doubled quote is the CSV escape for a literal quote. Newlines are legal inside a quoted
        // field, so multi-line notes survive intact rather than being flattened or truncated.
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    /// <summary>Formats an instant for a spreadsheet — sortable, unambiguous, no locale guessing.</summary>
    public static string Date(DateTime? value) =>
        value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Formats a calendar day.</summary>
    public static string Day(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Renders a boolean as words rather than TRUE/FALSE, which Excel coerces oddly.</summary>
    public static string YesNo(bool value) => value ? "Yes" : "No";
}
