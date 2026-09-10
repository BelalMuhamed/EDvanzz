/* ============================================================================
   EDVANZ — PARENT PORTAL: why do some follow-up requests never reach a teacher?
   READ-ONLY. Every statement is a SELECT. Nothing is written or changed.
   Safe to run against PRODUCTION during business hours.
   ============================================================================

   WHAT WE ALREADY KNOW (reproduced live on prod, 2026-09-10)
     A request whose STUDENT CODE does not resolve for that teacher is answered
     with the byte-identical "request sent" payload a real one gets, and writes
     NOTHING. The portal's /pending screen then treats "no row exists" as "still
     waiting" and refreshes forever. So the parent waits, and the teacher never
     sees anything.

   WHAT THIS SCRIPT CAN AND CANNOT MEASURE
     It CANNOT count the silent drops directly — by design nothing is written,
     so they leave no row. What it CAN do is size the recorded side of the
     funnel (Q1-Q5), find requests that WERE recorded but left unanswered (Q3),
     and locate the specific reported case (Q6-Q7).

   STATUS VALUES (ParentPortalAccessStatus)
     1 = Active   3 = Pending   4 = Rejected   5 = Revoked
   ========================================================================== */


/* ----------------------------------------------------------------------------
   Q1 — HEADLINE. Every portal grant ever created, by status.
   Read with: how small is this number compared to how many parents were sent
   the link? A tiny total is itself evidence of the silent drop.
   -------------------------------------------------------------------------- */
SELECT
    Status,
    StatusName = CASE Status WHEN 1 THEN 'Active'
                             WHEN 3 THEN 'Pending'
                             WHEN 4 THEN 'Rejected'
                             WHEN 5 THEN 'Revoked'
                             ELSE 'Unknown' END,
    Rows_       = COUNT(*),
    Teachers    = COUNT(DISTINCT TeacherId),
    Students    = COUNT(DISTINCT TeacherStudentId),
    Devices     = COUNT(DISTINCT DeviceHash),
    Earliest    = MIN(RequestedAt),
    Latest      = MAX(RequestedAt)
FROM ParentPortalAccesses
GROUP BY Status
ORDER BY Status;


/* ----------------------------------------------------------------------------
   Q2 — VOLUME PER DAY since the portal went live.
   Shows whether requests are trickling in at all, and whether there are days
   where the teacher clearly never responded.
   -------------------------------------------------------------------------- */
SELECT
    Day            = CAST(RequestedAt AS date),
    Requests       = COUNT(*),
    AutoApproved   = SUM(CASE WHEN AutoApproved = 1 THEN 1 ELSE 0 END),
    BornPending    = SUM(CASE WHEN Status = 3 THEN 1 ELSE 0 END),
    Approved       = SUM(CASE WHEN Status = 1 AND AutoApproved = 0 THEN 1 ELSE 0 END),
    Rejected       = SUM(CASE WHEN Status = 4 THEN 1 ELSE 0 END)
FROM ParentPortalAccesses
GROUP BY CAST(RequestedAt AS date)
ORDER BY Day DESC;


/* ----------------------------------------------------------------------------
   Q3 — STRANDED BUT RECORDED. Pending requests the teacher has never answered.
   These are the parents the teacher COULD have helped and didn't — i.e. the
   "teacher never noticed the badge" half of the problem, as opposed to the
   silent-drop half. Anything older than a day or two is a real person waiting.
   -------------------------------------------------------------------------- */
SELECT
    a.Id,
    a.TeacherId,
    TeacherName   = u.FullName,
    a.TeacherStudentId,
    StudentName   = ts.StudentName,
    StudentCode   = ts.StudentCode,
    a.ParentName,
    a.ClaimedPhone,
    RosterPhone   = ts.ParentPhoneNumber,
    a.RequestedAt,
    AgeHours      = DATEDIFF(hour, a.RequestedAt, GETUTCDATE())
FROM ParentPortalAccesses a
LEFT JOIN TeacherStudents ts ON ts.Id = a.TeacherStudentId
LEFT JOIN Teachers t         ON t.Id  = a.TeacherId
LEFT JOIN Users u            ON u.Id  = t.UserId
WHERE a.Status = 3
ORDER BY a.RequestedAt ASC;


/* ----------------------------------------------------------------------------
   Q4 — REACH. How many teachers have the portal switched on, versus how many
   have ever actually received a request. A big gap means parents are trying and
   failing (or never trying).
   -------------------------------------------------------------------------- */
SELECT
    PortalEnabledTeachers = (SELECT COUNT(*) FROM TeacherConfigurations WHERE ParentPortalEnabled = 1),
    TeachersWithAnyRequest = (SELECT COUNT(DISTINCT TeacherId) FROM ParentPortalAccesses),
    TeachersWithActiveFollower = (SELECT COUNT(DISTINCT TeacherId) FROM ParentPortalAccesses WHERE Status = 1);


/* ----------------------------------------------------------------------------
   Q5 — THE INSTANT-ACCESS PATH. A parent whose number is already on the child's
   roster record skips the queue entirely. If almost no student has a parent
   phone recorded, then almost every parent MUST be approved by hand — which is
   what makes the silent drop and the unnoticed inbox so damaging.
   -------------------------------------------------------------------------- */
SELECT
    ActiveStudents      = COUNT(*),
    WithParentPhone     = SUM(CASE WHEN ParentPhoneNumber IS NOT NULL
                                    AND LTRIM(RTRIM(ParentPhoneNumber)) <> '' THEN 1 ELSE 0 END),
    WithoutParentPhone  = SUM(CASE WHEN ParentPhoneNumber IS NULL
                                    OR LTRIM(RTRIM(ParentPhoneNumber)) = '' THEN 1 ELSE 0 END)
FROM TeacherStudents
WHERE IsDeleted = 0;

/* Same thing, per teacher, worst first — who is most exposed. */
SELECT TOP 25
    ts.TeacherId,
    TeacherName    = u.FullName,
    TeacherCode    = t.TeacherCode,
    Students       = COUNT(*),
    MissingPhone   = SUM(CASE WHEN ts.ParentPhoneNumber IS NULL
                               OR LTRIM(RTRIM(ts.ParentPhoneNumber)) = '' THEN 1 ELSE 0 END),
    PortalEnabled  = MAX(CAST(tc.ParentPortalEnabled AS int))
FROM TeacherStudents ts
JOIN Teachers t              ON t.Id  = ts.TeacherId
JOIN Users u                 ON u.Id  = t.UserId
LEFT JOIN TeacherConfigurations tc ON tc.TeacherId = ts.TeacherId
WHERE ts.IsDeleted = 0
GROUP BY ts.TeacherId, u.FullName, t.TeacherCode
HAVING MAX(CAST(tc.ParentPortalEnabled AS int)) = 1
ORDER BY MissingPhone DESC;


/* ----------------------------------------------------------------------------
   Q6 — THE REPORTED CASE. Locate the student named in the support screenshot so
   we can tell the parent the RIGHT student code (and confirm whether anything
   was ever recorded for them).
   Adjust the name fragment if the spelling differs.
   -------------------------------------------------------------------------- */
SELECT
    ts.Id,
    ts.TeacherId,
    TeacherName  = u.FullName,
    TeacherCode  = t.TeacherCode,
    ts.StudentName,
    ts.StudentCode,
    ts.StudentPhoneNumber,
    ts.ParentPhoneNumber,
    ts.SessionId,
    ts.IsDeleted,
    PortalEnabled = tc.ParentPortalEnabled
FROM TeacherStudents ts
JOIN Teachers t              ON t.Id = ts.TeacherId
JOIN Users u                 ON u.Id = t.UserId
LEFT JOIN TeacherConfigurations tc ON tc.TeacherId = ts.TeacherId
WHERE ts.StudentName LIKE N'%أكرم%'
   OR ts.StudentName LIKE N'%الجزل%'
   OR ts.StudentName LIKE N'%فتح%';

/* Did that parent's number ever reach us on ANY request or roster row?
   Phone from the screenshot: +20 12 7266 6xxx -> match on the visible digits. */
SELECT
    Source = 'portal request',
    a.Id, a.TeacherId, a.TeacherStudentId, a.ParentName, a.ClaimedPhone,
    a.Status, a.RequestedAt, a.RespondedAt
FROM ParentPortalAccesses a
WHERE a.ClaimedPhone LIKE '%1272666%'
UNION ALL
SELECT
    Source = 'roster parent phone',
    ts.Id, ts.TeacherId, ts.Id, ts.StudentName, ts.ParentPhoneNumber,
    NULL, NULL, NULL
FROM TeacherStudents ts
WHERE ts.ParentPhoneNumber LIKE '%1272666%'
  AND ts.IsDeleted = 0;


/* ----------------------------------------------------------------------------
   Q7 — REJECTION COOLDOWN VICTIMS. A parent who re-submits within 24h of being
   rejected is silently discarded too (same "sent" screen, nothing written).
   Recent rejections tell us how many parents may be sitting in that state.
   -------------------------------------------------------------------------- */
SELECT
    a.Id, a.TeacherId, a.TeacherStudentId, a.ParentName, a.ClaimedPhone,
    a.RespondedAt,
    HoursSinceRejection = DATEDIFF(hour, ISNULL(a.RespondedAt, a.RequestedAt), GETUTCDATE())
FROM ParentPortalAccesses a
WHERE a.Status = 4
  AND ISNULL(a.RespondedAt, a.RequestedAt) > DATEADD(day, -7, GETUTCDATE())
ORDER BY a.RespondedAt DESC;


/* ----------------------------------------------------------------------------
   Q8 — DUPLICATE DEVICES PER PARENT. Several rows for the same student from
   different device hashes means the device cookie is not surviving (iOS in-app
   browsers), so an approval the teacher DID give never reached the parent.
   -------------------------------------------------------------------------- */
SELECT
    a.TeacherStudentId,
    StudentName = ts.StudentName,
    Devices     = COUNT(DISTINCT a.DeviceHash),
    Rows_       = COUNT(*),
    Statuses    = STRING_AGG(CAST(a.Status AS varchar(3)), ',')
FROM ParentPortalAccesses a
LEFT JOIN TeacherStudents ts ON ts.Id = a.TeacherStudentId
GROUP BY a.TeacherStudentId, ts.StudentName
HAVING COUNT(DISTINCT a.DeviceHash) > 1
ORDER BY Devices DESC;
