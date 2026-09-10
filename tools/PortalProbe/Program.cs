// ============================================================================
// PortalProbe — READ-ONLY diagnostic for the parent-portal follow-up funnel.
//
// Every statement below is a SELECT. Nothing is written, updated or deleted.
// The connection string is read from the EDVANZ_CS environment variable so it
// never has to be typed, pasted or echoed anywhere.
//
//   run-portal-probe.sh is the intended entry point — it opens a temporary SQL
//   firewall rule for the current machine, sets EDVANZ_CS from the App Service
//   setting, runs this, and closes the rule again.
//
// Why this exists: a portal request whose STUDENT CODE does not resolve writes
// NOTHING (deliberately — it is the roster-enumeration guard), so the silent
// drops cannot be counted directly. What CAN be measured is the recorded side
// of the funnel: how many requests exist at all, how many are still waiting for
// a teacher who never noticed, and how much of the roster carries the parent
// phone that would have let the parent in with no teacher involvement.
// ============================================================================

using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

Console.OutputEncoding = Encoding.UTF8;

string? cs = Environment.GetEnvironmentVariable("EDVANZ_CS");
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("EDVANZ_CS is not set. Run tools/PortalProbe/run-portal-probe.sh instead.");
    return 1;
}

// TrustServerCertificate: the App Service value omits it; adding it here keeps a
// local run from failing on certificate chain validation.
if (!cs.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
    cs = cs.TrimEnd(';') + ";TrustServerCertificate=True";

// The reported case, from the support screenshot. Override without editing code:
//   PROBE_NAME='<fragment>' PROBE_PHONE='<digits>' ./run-portal-probe.sh
string nameFragment = Environment.GetEnvironmentVariable("PROBE_NAME") ?? "أكرم";
string phoneFragment = Environment.GetEnvironmentVariable("PROBE_PHONE") ?? "1272666";

var queries = new (string Title, string Sql)[]
{
    ("1. Every portal grant ever created, by status",
     """
     SELECT
         StatusName = CASE Status WHEN 1 THEN 'Active' WHEN 3 THEN 'Pending'
                                  WHEN 4 THEN 'Rejected' WHEN 5 THEN 'Revoked'
                                  ELSE CONCAT('Unknown(', Status, ')') END,
         [Rows]     = COUNT(*),
         Teachers   = COUNT(DISTINCT TeacherId),
         Students   = COUNT(DISTINCT TeacherStudentId),
         Devices    = COUNT(DISTINCT DeviceHash),
         Earliest   = CONVERT(varchar(16), MIN(RequestedAt), 120),
         Latest     = CONVERT(varchar(16), MAX(RequestedAt), 120)
     FROM ParentPortalAccesses
     GROUP BY Status
     ORDER BY Status;
     """),

    ("2. Requests per day since launch",
     """
     SELECT
         [Day]        = CONVERT(varchar(10), RequestedAt, 120),
         Requests     = COUNT(*),
         AutoApproved = SUM(CASE WHEN AutoApproved = 1 THEN 1 ELSE 0 END),
         StillPending = SUM(CASE WHEN Status = 3 THEN 1 ELSE 0 END),
         Approved     = SUM(CASE WHEN Status = 1 AND AutoApproved = 0 THEN 1 ELSE 0 END),
         Rejected     = SUM(CASE WHEN Status = 4 THEN 1 ELSE 0 END)
     FROM ParentPortalAccesses
     GROUP BY CONVERT(varchar(10), RequestedAt, 120)
     ORDER BY [Day] DESC;
     """),

    ("3. STRANDED BUT RECORDED — pending requests no teacher ever answered",
     """
     SELECT
         a.Id,
         a.TeacherId,
         Teacher     = u.FullName,
         Student     = ts.StudentName,
         StudentCode = ts.StudentCode,
         a.ParentName,
         a.ClaimedPhone,
         RosterPhone = ts.ParentPhoneNumber,
         Requested   = CONVERT(varchar(16), a.RequestedAt, 120),
         AgeHours    = DATEDIFF(hour, a.RequestedAt, GETUTCDATE())
     FROM ParentPortalAccesses a
     LEFT JOIN TeacherStudents ts ON ts.Id = a.TeacherStudentId
     LEFT JOIN Teachers t         ON t.Id  = a.TeacherId
     LEFT JOIN Users u            ON u.Id  = t.UserId
     WHERE a.Status = 3
     ORDER BY a.RequestedAt ASC;
     """),

    ("4. Reach — teachers with the portal on vs teachers who ever got a request",
     """
     SELECT
         PortalEnabledTeachers      = (SELECT COUNT(*) FROM TeacherConfigurations WHERE ParentPortalEnabled = 1),
         TeachersWithAnyRequest     = (SELECT COUNT(DISTINCT TeacherId) FROM ParentPortalAccesses),
         TeachersWithActiveFollower = (SELECT COUNT(DISTINCT TeacherId) FROM ParentPortalAccesses WHERE Status = 1);
     """),

    ("5. The instant-access path — how much of the roster carries a parent phone",
     """
     SELECT
         ActiveStudents     = COUNT(*),
         WithParentPhone    = SUM(CASE WHEN ParentPhoneNumber IS NOT NULL AND LTRIM(RTRIM(ParentPhoneNumber)) <> '' THEN 1 ELSE 0 END),
         WithoutParentPhone = SUM(CASE WHEN ParentPhoneNumber IS NULL OR LTRIM(RTRIM(ParentPhoneNumber)) = '' THEN 1 ELSE 0 END)
     FROM TeacherStudents
     WHERE IsDeleted = 0;
     """),

    ("6. Portal-enabled teachers most exposed (roster missing parent phones)",
     """
     SELECT TOP 15
         ts.TeacherId,
         Teacher      = u.FullName,
         TeacherCode  = t.TeacherCode,
         Students     = COUNT(*),
         MissingPhone = SUM(CASE WHEN ts.ParentPhoneNumber IS NULL OR LTRIM(RTRIM(ts.ParentPhoneNumber)) = '' THEN 1 ELSE 0 END)
     FROM TeacherStudents ts
     JOIN Teachers t                    ON t.Id  = ts.TeacherId
     JOIN Users u                       ON u.Id  = t.UserId
     LEFT JOIN TeacherConfigurations tc ON tc.TeacherId = ts.TeacherId
     WHERE ts.IsDeleted = 0 AND tc.ParentPortalEnabled = 1
     GROUP BY ts.TeacherId, u.FullName, t.TeacherCode
     ORDER BY MissingPhone DESC;
     """),

    ("7. THE REPORTED CASE — locate the student by name fragment",
     """
     SELECT
         ts.Id,
         ts.TeacherId,
         Teacher       = u.FullName,
         TeacherCode   = t.TeacherCode,
         ts.StudentName,
         ts.StudentCode,
         ts.ParentPhoneNumber,
         ts.IsDeleted,
         PortalEnabled = tc.ParentPortalEnabled
     FROM TeacherStudents ts
     JOIN Teachers t                    ON t.Id = ts.TeacherId
     JOIN Users u                       ON u.Id = t.UserId
     LEFT JOIN TeacherConfigurations tc ON tc.TeacherId = ts.TeacherId
     WHERE ts.StudentName LIKE @name;
     """),

    ("8. THE REPORTED CASE — did that phone ever reach us?",
     """
     SELECT Source = 'portal request', a.Id, a.TeacherId, Who = a.ParentName,
            Phone = a.ClaimedPhone, [Status] = CAST(a.Status AS varchar(3)),
            [When] = CONVERT(varchar(16), a.RequestedAt, 120)
     FROM ParentPortalAccesses a
     WHERE a.ClaimedPhone LIKE @phone
     UNION ALL
     SELECT Source = 'roster parent phone', ts.Id, ts.TeacherId, Who = ts.StudentName,
            Phone = ts.ParentPhoneNumber, [Status] = 'roster', [When] = NULL
     FROM TeacherStudents ts
     WHERE ts.ParentPhoneNumber LIKE @phone AND ts.IsDeleted = 0;
     """),

    ("9. Rejection-cooldown victims — rejected in the last 7 days",
     """
     SELECT
         a.Id, a.TeacherId, a.ParentName, a.ClaimedPhone,
         Rejected = CONVERT(varchar(16), ISNULL(a.RespondedAt, a.RequestedAt), 120),
         HoursSince = DATEDIFF(hour, ISNULL(a.RespondedAt, a.RequestedAt), GETUTCDATE())
     FROM ParentPortalAccesses a
     WHERE a.Status = 4 AND ISNULL(a.RespondedAt, a.RequestedAt) > DATEADD(day, -7, GETUTCDATE())
     ORDER BY a.RespondedAt DESC;
     """),

    ("10a. THE REPORTED CASE — the FULL grant history for that student, in order",
     """
     -- Everything ever written for the matched student(s), so a "it still says waiting"
     -- report can be settled exactly: when each attempt arrived, whether it was auto
     -- approved and why, and WHICH BROWSER it belonged to. A grant that is Active on one
     -- DeviceHash while the parent is looking at another is the lost-cookie case — the
     -- portal identifies a browser by that hash and nothing else.
     SELECT
         a.Id,
         a.TeacherStudentId,
         Student     = ts.StudentName,
         StudentCode = ts.StudentCode,
         a.ParentName,
         a.ClaimedPhone,
         [Status]    = CASE a.Status WHEN 1 THEN 'Active' WHEN 3 THEN 'Pending'
                                    WHEN 4 THEN 'Rejected' WHEN 5 THEN 'Revoked'
                                    ELSE CONCAT('?', a.Status) END,
         a.AutoApproved,
         [Origin]    = CASE a.Origin WHEN 1 THEN 'RosterPhone' WHEN 2 THEN 'TeacherApproved'
                                     WHEN 3 THEN 'TrustedPhone' ELSE '—' END,
         Requested   = CONVERT(varchar(16), a.RequestedAt, 120),
         Responded   = CONVERT(varchar(16), a.RespondedAt, 120),
         LastSeen    = CONVERT(varchar(16), a.LastSeenAt, 120),
         Browser     = LEFT(a.DeviceHash, 8)
     FROM ParentPortalAccesses a
     JOIN TeacherStudents ts ON ts.Id = a.TeacherStudentId
     WHERE ts.StudentName LIKE @name OR a.ClaimedPhone LIKE @phone
     ORDER BY a.TeacherStudentId, a.RequestedAt;
     """),

    ("10b. SIBLINGS — one browser holding a live grant for more than one child",
     """
     -- The portal resolves a device to the NEWEST active grant and renders exactly one
     -- rosterId everywhere, with no child switcher. So a parent of two children at the
     -- same teacher who signs in for the second can no longer reach the first: every
     -- screen follows the newest grant, and re-entering the older child's code still
     -- lands on the newer one. Each row here is a parent living with that today.
     SELECT
         Browser  = LEFT(a.DeviceHash, 8),
         Children = COUNT(DISTINCT a.TeacherStudentId),
         Students = STRING_AGG(ts.StudentName, ' | '),
         Teachers = COUNT(DISTINCT a.TeacherId),
         Newest   = CONVERT(varchar(16), MAX(a.RequestedAt), 120)
     FROM ParentPortalAccesses a
     JOIN TeacherStudents ts ON ts.Id = a.TeacherStudentId
     WHERE a.Status = 1
     GROUP BY a.DeviceHash
     HAVING COUNT(DISTINCT a.TeacherStudentId) > 1
     ORDER BY Children DESC;
     """),

    ("10. Lost-cookie signal — same student reached from more than one device",
     """
     SELECT
         a.TeacherStudentId,
         Student  = ts.StudentName,
         Devices  = COUNT(DISTINCT a.DeviceHash),
         [Rows]   = COUNT(*),
         Statuses = STRING_AGG(CAST(a.Status AS varchar(3)), ',')
     FROM ParentPortalAccesses a
     LEFT JOIN TeacherStudents ts ON ts.Id = a.TeacherStudentId
     GROUP BY a.TeacherStudentId, ts.StudentName
     HAVING COUNT(DISTINCT a.DeviceHash) > 1
     ORDER BY Devices DESC;
     """),
};

await using var connection = new SqlConnection(cs);
await connection.OpenAsync();

var builder = new SqlConnectionStringBuilder(cs);
Console.WriteLine($"Connected to {builder.DataSource} / {builder.InitialCatalog}  (read-only probe)");
Console.WriteLine($"UTC now: {DateTime.UtcNow:yyyy-MM-dd HH:mm}");

foreach (var (title, sql) in queries)
{
    Console.WriteLine();
    Console.WriteLine("══ " + title);

    try
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = $"%{nameFragment}%";
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 50).Value = $"%{phoneFragment}%";

        await using var reader = await command.ExecuteReaderAsync();
        WriteTable(reader);
    }
    catch (Exception ex)
    {
        Console.WriteLine("   ! query failed: " + ex.Message);
    }
}

Console.WriteLine();
Console.WriteLine("Done. Nothing was written.");
return 0;

// ── Renders a result set as a plain aligned table, capped so one noisy query
//    cannot bury the rest of the report. ──
static void WriteTable(SqlDataReader reader, int maxRows = 60)
{
    var columns = new string[reader.FieldCount];
    for (int i = 0; i < reader.FieldCount; i++)
        columns[i] = reader.GetName(i);

    var rows = new List<string[]>();
    int total = 0;
    while (reader.Read())
    {
        total++;
        if (rows.Count >= maxRows) continue;

        var row = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
            row[i] = reader.IsDBNull(i) ? "—" : (reader.GetValue(i)?.ToString() ?? "—");
        rows.Add(row);
    }

    if (total == 0)
    {
        Console.WriteLine("   (no rows)");
        return;
    }

    var widths = new int[columns.Length];
    for (int i = 0; i < columns.Length; i++)
    {
        widths[i] = columns[i].Length;
        foreach (var row in rows)
            widths[i] = Math.Max(widths[i], row[i].Length);
        widths[i] = Math.Min(widths[i], 40);
    }

    Console.WriteLine("   " + string.Join("  ", columns.Select((c, i) => Pad(c, widths[i]))));
    Console.WriteLine("   " + string.Join("  ", widths.Select(w => new string('-', w))));
    foreach (var row in rows)
        Console.WriteLine("   " + string.Join("  ", row.Select((v, i) => Pad(v, widths[i]))));

    if (total > rows.Count)
        Console.WriteLine($"   … {total - rows.Count} more row(s) not shown (total {total})");
}

static string Pad(string value, int width) =>
    value.Length > width ? value[..(width - 1)] + "…" : value.PadRight(width);
