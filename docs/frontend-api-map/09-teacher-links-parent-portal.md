# 09 — Teacher: Student Links & Parent Portal

Source: `lib/feature/teacher_module/student_links/**` (~23 files) and
`lib/feature/teacher_module/parent_portal/**` (~17 files).
Endpoint constants: `lib/core/network_services/web_constant.dart`, lines ~428–480
(`webPathTeacherStudentLinks*`, `webPathTeacherParentPortal*`).
Data flow: `TeacherStudentLinkRemoteDataSource` / `TeacherParentPortalRemoteDataSource` (Dio) →
`*RepositoryImpl` → cubits → views. Responses are read through the standard envelope
`{success, code, message, data}`; list endpoints return the standard paginated shape
(`data`/`items`, `page`, `pageSize`, `totalCount`, `totalPages`) unless noted.

**Two separate axes, don't conflate them:** a *student link* (`api/teacher/student-links/*`) connects
a **student's own app account** to this teacher; *binding* additionally points that connected account
at one **roster record** (`TeacherStudent`). A *parent-portal follower* (`api/teacher/parent-portal/*`)
is a completely different grant — a parent's **phone number** gets read-only web access to one
roster student via `parent.edvanz.io`, no app account involved. The two inboxes (Link Requests /
Parent requests) are deliberately shaped identically (same card, same accept/reject buttons, same
select-all header) so a teacher who has learned one recognises the other instantly, but they hit
different endpoint families and different failure models — see the graceful-degrade note below.

**`studentCode` ambiguity (documented past bug, repo `CLAUDE.md` §7.2b):** every `studentCode` field
on the student-link accept/bind bodies is the **teacher's own roster code**
(`TeacherStudent.StudentCode`, e.g. `"A12"`) — **never** the student's globally-unique
`StudentUser.StudentAccountCode` (the 10-char code shown on the request card). Passing the account
code there is rejected with `StudentAccountCodeNotRosterCode`.

**Parent portal graceful degradation:** an older backend has no `api/teacher/parent-portal/*` routes
at all. Every call on that family maps a **bare** 404/501/403 (no business `code` in the body) to
`ParentPortalUnavailableFailure`, which every surface treats as "feature does not exist" — the side
menu entry, the settings group, the requests screen, and the profile follow-chip all render as if the
feature were simply absent, never as an error. A 404 that **does** carry a business `code` (e.g.
`ParentPortalRequestNotFound`) is a different thing — the route exists, the record is just gone
(already handled by someone else) — and is surfaced as `ParentPortalRecordGoneFailure`: the row is
dropped from the list and the server's localized message is toasted.

---

## My Teacher Code
_Dart file: `lib/feature/teacher_module/student_links/view/teacher_my_code_screen.dart`_
_Cubit: `TeacherMyCodeCubit`_
**Reached from:** side menu → Students section → "My Teacher Code".

The 8-digit code shown is the code students type into their own app to send this teacher a link
request. The QR box is decorative only (a static icon) — it does not encode the code. "Copy" copies
the plain code string; "Share" opens the OS share sheet with the code as plain text.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load | `GET api/teacher/student-links/my-code` | — | `data.teacherCode` |
| Screen load (silent, best-effort, right after the code loads) | `GET api/teacher/student-links` with `page=1&pageSize=1` | — | `data.linkedStudentCapacity`, `data.linkedStudentsRemaining` — drives the "You can still link N more student app accounts" / "All N in use" line under the subtitle. Both `null` on a server that predates the limit → line hidden entirely |
| "Copy" | *(no call)* | — | copies `teacherCode` to clipboard |
| "Share" | *(no call — OS share sheet)* | — | shares `teacherCode` as plain text |

## Link Requests (student link inbox)
_Dart file: `lib/feature/teacher_module/student_links/view/teacher_link_requests_screen.dart`_
_Cubit: `TeacherLinkRequestsCubit`_
**Reached from:** side menu → Students section → "Link Requests" (row carries a red pending-count
badge fed by a lightweight `GET .../requests?page=1&pageSize=1` probe run by
`TeacherLinkRequestsPendingCubit` on app resume and re-run after the screen is popped).

Each card is one student's app account asking to connect. If the student typed a roster code when
requesting (`requestedStudentCode`) or the server found an unambiguous name match
(`suggestedMatch`), the banner shows it and "Accept" pre-fills that code in the picker sheet below —
otherwise it shows "No code given — accept now, link by code after." Accepting and binding share the
exact same picker sheet and the exact same wire shape.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / infinite scroll | `GET api/teacher/student-links/requests` | Query: `page`, `pageSize` (20) | Per item: `linkId`, `requestedStudentName`, `requestedStudentCode`, `requestedAt` (UTC), `studentAccountCode`, `studentFullName`, `studentPhoneNumber`, `suggestedMatch{teacherStudentId, studentName, studentCode, isAlreadyLinked}`; envelope `totalCount` (badge count) |
| "Accept" → opens "Link this student" sheet → confirm | `POST api/teacher/student-links/requests/{linkId}/accept` | Body `{teacherStudentId?, studentCode?}` — **both omitted** accepts the account **without binding** it to any roster record (connected, but the student sees nothing until later bound); a search-picked student sends `teacherStudentId`; a typed code sends `studentCode` (trimmed, the teacher's roster code, ≤10 chars) | `data.linkId`, `data.isLinked`, `data.studentAccountCode`, `data.studentFullName`, `data.linkedAt`, `data.teacherStudentId`, `data.studentRecordName` (falls back to `rosterStudentName`), `data.studentRecordCode` (falls back to `rosterStudentCode`) — row is removed from the list on success; toast reads "Linked" if `isLinked` else "Accepted" |
| "Reject" → confirm dialog | `POST api/teacher/student-links/requests/{linkId}/reject` | — (no body) | void envelope only — row removed from the list, "Request rejected" toast |
| Sheet: search box | `GET api/teacherstudent/students` *(Students module — documented in that chapter, not this one)* | Query: `search`, `page=1`, `pageSize=10` | roster student rows to pick from |
| Sheet: type a code directly, no search | *(no call until Confirm)* | — | validated client-side as an alphanumeric code, ≤10 chars |

Non-trivial request body — accepting a request and binding it to roster record 512 in one step
("Accept & link"):
```json
{
  "teacherStudentId": 512
}
```
Accepting a request typed with a code the teacher recognizes, without a picked roster row:
```json
{
  "studentCode": "A12"
}
```

**Error codes handled specially, on both `accept` and `bind`:**
- `403` with body `code` (or raw `message`) equal to `LinkedStudentCapacityReached` **or**
  `LinkedStudentCapacityReachedCenterManaged` → the request/row **stays** (nothing was accepted); the
  server's own message is shown in a dedicated "you've used all your student app accounts" dialog with
  two ways out (unlink someone, or raise the plan's limit) instead of a toast. The center-managed
  variant carries different wording server-side but maps to the same client dialog.
- Any other failure (including `TeacherStudentCodeNotFound` — wrong roster code —,
  `RosterStudentNotFound` — wrong `teacherStudentId` —, and `StudentAccountCodeNotRosterCode` — the
  teacher pasted the student's account code instead of the roster code) is shown as a **plain error
  toast** using the server's localized `message` verbatim; the client does not branch on these codes
  individually, it just surfaces whatever text the server sent. The request row is left in place so
  the teacher can retry with a different code.
- A session-expiry failure is swallowed here (handled globally elsewhere in the app), never toasted
  on this screen.

## Linked Students
_Dart file: `lib/feature/teacher_module/student_links/view/teacher_linked_students_screen.dart`_
_Cubit: `TeacherLinkedStudentsCubit`_
**Reached from:** side menu → Students section → "My Students".

Every row here is an already-**connected** student account (accepted, via the screen above); this
screen manages whether each is additionally **bound** to a roster record, and — when the teacher's
device lock is on — whether its registered device needs resetting.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / search / filter chip / infinite scroll | `GET api/teacher/student-links` | Query: `page`, `pageSize` (10), `search` (trimmed, omitted if empty), `filter` (`"Linked"` \| `"NotLinked"`, **omitted** for the "All" chip) | Per item: `linkId`, `linkedAt`, `studentAccountCode`, `studentFullName`, `studentPhoneNumber`, `teacherStudentId`, `studentRecordName`/`rosterStudentName`, `studentRecordCode`/`rosterStudentCode`, `isLinked`, `isDeviceRegistered`, `deviceBoundAt`; envelope `totalCount` (filtered-slice count for paging), `allCount`, `linkedCount`, `unlinkedCount` (also accepts the misspelled `unlinckedCount`), `deviceLockEnabled`, `linkedStudentCapacity`, `linkedStudentsRemaining` — chip counts and the seat-usage pill all come from this one call |
| Tap a **not-linked** row → "Link this student" sheet → confirm | `POST api/teacher/student-links/{linkId}/bind` | Body `{teacherStudentId?, studentCode?}` (identical shape/rules to `accept` above) | Same shape as the accept result; row updates in place (or drops out of the current chip if it no longer matches the active filter) |
| Tap a **linked** row → confirm "Unlink student record?" | `POST api/teacher/student-links/{linkId}/unbind` | — (no body) | void envelope — row flips to Not-linked (optimistic; rolled back on failure) |
| "Select" → checkbox rows → "Remove N students" → confirm | `POST api/teacher/student-links/remove` | Body `{linkIds: [int]}` | `data.removedCount`, `data.skippedLinkIds[]` — removed rows drop out of the list entirely (optimistic) |
| Linked row with device lock on → "Reset device" → confirm | `POST api/teacher/student-links/{linkId}/reset-device` | — (no body) | void envelope — row's device chip flips to "No device" (optimistic; rolled back on failure) |

Non-trivial request body — bulk-removing three connected accounts:
```json
{
  "linkIds": [101, 102, 108]
}
```

Error handling mirrors the Link Requests screen: `LinkedStudentCapacityReached` /
`LinkedStudentCapacityReachedCenterManaged` on `bind` opens the same capacity dialog; every other
failure is a plain toast of the server's message.

---

## Parent follow-up (Settings section)
_Dart file: `lib/feature/teacher_module/parent_portal/view/widgets/teacher_parent_portal_settings_section_widget.dart`_
_Cubit (summary only): `TeacherParentPortalSummaryCubit`_
**Reached from:** embedded as one group inside the teacher Settings screen (Settings chapter, not
detailed here) — dropped from that screen's children entirely (not just hidden) when the summary
probe below comes back "unavailable".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Section renders (screen open) | `GET api/teacher/parent-portal/summary` | — | `data.pendingCount` (pending-row badge + side-menu badge), `data.followedStudentsCount`, `data.studentsMissingParentPhone` (nudge row + count), `data.portalEnabled` (parsed but **not** used to drive the switch — see below), `data.canManage` (default `true` on an older server; `false` hides the pending row and the side-menu entry for a teacher-owned assistant without `Student/Edit`), `data.portalAllowed` (default `true`; explicit `false` renders the whole group as a locked upsell card instead) |
| "Parent follow-up" master switch | *(handled by the general teacher-settings save endpoint — Settings chapter, not this one)* | — | `TeacherConfiguration.parentPortalEnabled` is the single source of truth for the switch's ON/OFF state, not `summary.portalEnabled` |
| Attendance / Payments / Grades visibility toggles (under "What parents will see") | *(same general settings save endpoint — Settings chapter)* | — | `TeacherConfiguration.parentSeesAttendance` / `parentSeesPayment` / `parentSeesGrades` |
| "Copy" (page link + teacher code) | *(no call)* | — | copies `"{portalUrl}\n{teacherCode}"` (teacher code read from local storage, not this call) |
| "Send on WhatsApp" | *(no call — opens WhatsApp with a prefilled message)* | — | — |
| Pending-requests row (only when `canManage && pendingCount > 0`) | *(navigation only)* | — | opens **Parent requests** below; on return, re-runs `GET .../summary` (`force: true`) so the badge settles |
| Missing-parent-phone nudge row | *(navigation only)* | — | opens the Add/Edit Student screen (Students chapter) |

## Parent requests
_Dart file: `lib/feature/teacher_module/parent_portal/view/teacher_parent_portal_requests_screen.dart`_
_Cubit: `TeacherParentPortalRequestsCubit`_
**Reached from:** side menu → Students section → "Parent requests" row (badge-count from the same
summary probe above); also the settings pending-row above. If the teacher's plan does not include the
follow-up page (`summary.portalAllowed == false`), the route renders a locked upsell card in place of
the list instead of pushing through to endpoints that would refuse.

A request only ever appears here for a phone number the server does **not** already recognise as the
student's saved parent number — a matching number is admitted automatically server-side with no
inbox row at all.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / infinite scroll | `GET api/teacher/parent-portal/requests` | Query: `page`, `pageSize` (20) | Per item: `id`, `teacherStudentId`, `studentName`, `studentCode` (teacher's roster code, display-only chip), `claimedPhone` (also reads legacy `claimedPhoneMasked`/`maskedPhone`), `parentName` (self-declared, may be empty), `requestedAt` (UTC), `studentHasParentPhone`, `phoneMatchesRoster`, `studentParentPhone` (the differing number already on file, when one exists); envelope `totalCount` (header badge) |
| "Also save this number to the student" checkbox (per card, defaults ON when the student has no number on file, OFF when one already matches) | *(no call — folded into the Approve tap below)* | — | — |
| Conflict banner → "Replace the saved number" → confirm (only shown when `studentHasParentPhone && !phoneMatchesRoster`) | *(no call — arms `overwriteStudentPhone` for the next Approve tap)* | — | — |
| "Approve" | `POST api/teacher/parent-portal/requests/{id}/approve` | Body **omitted entirely** if `claimedPhone` is empty (nothing to save); otherwise `{savePhoneToStudent: bool, overwriteStudentPhone: true}` — `overwriteStudentPhone` is included **only when `true`** (armed via the confirm above), never sent as `false` | `data.follower{id, claimedPhone, parentName, autoApproved, origin, grantedAt, lastSeenAt}`, `data.phoneSavedToStudent`, `data.phoneSaveSkippedReason` (`"noPhoneOnRequest"` \| `"alreadySaved"` \| `"studentHasDifferentPhone"` \| unknown) — row removed from the list; success toast varies: "Approved, and the number was saved" / "Approved. …left as it is…" (on `studentHasDifferentPhone`) / plain "Request approved" |
| "Reject" → confirm dialog | `POST api/teacher/parent-portal/requests/{id}/reject` | — (no body) | void envelope — row removed, "Request rejected" toast |
| Header checkbox → "Select all" (pages in every remaining row first) | `GET api/teacher/parent-portal/requests` (repeated, paging through `hasMore`) | Query: `page` (incrementing), `pageSize` | same item shape as the initial load — every id gets added to the selection set |
| Bulk bar: "Save parents' numbers…" checkbox (default ON) | *(no call — folded into the next bulk action)* | — | — |
| Bulk bar → "Approve selected" → confirm | `POST api/teacher/parent-portal/requests/bulk` | Body `{ids: [int], action: "approve", savePhoneToStudent: true}` (`savePhoneToStudent` key only sent when checked **and** action is approve) | `data.processedIds[]`, `data.skippedIds[]`, `data.phonesSaved` — processed rows removed, selection cleared; toast reads "{count} approved · {saved} numbers saved…" when `phonesSaved > 0`, else "{count} requests approved" |
| Bulk bar → "Reject selected" → confirm | `POST api/teacher/parent-portal/requests/bulk` | Body `{ids: [int], action: "reject"}` | `data.processedIds[]`, `data.skippedIds[]` — processed rows removed; "{count} requests rejected" |

Non-trivial request body — approving a request and also replacing a conflicting saved number:
```json
{
  "savePhoneToStudent": true,
  "overwriteStudentPhone": true
}
```
Bulk-approving a start-of-term batch, capturing every empty student record's number:
```json
{
  "ids": [301, 302, 305, 311],
  "action": "approve",
  "savePhoneToStudent": true
}
```

**Error handling:**
- A **404/501 with no business `code`** on any call in this screen (older server) sets
  `isUnavailable`; the empty state renders calmly with no retry affordance rather than an error banner
  (an older backend is not something the teacher can fix).
- A **404 with a business `code`** (e.g. `ParentPortalRequestNotFound` — someone else already handled
  it) drops the row from the list and toasts the server's localized message, instead of leaving a
  dead Approve button.
- Every other failure is a plain error toast of the server's `message`.

## Following parents (per-student chip + sheet)
_Dart files: `lib/feature/teacher_module/parent_portal/view/widgets/teacher_parent_portal_follow_chip.dart`,
`teacher_parent_portal_followers_sheet.dart`_
_Cubit: `TeacherParentPortalFollowersCubit`_
**Reached from:** Student Profile screen (`lib/feature/teacher_module/student_profile/view/teacher_student_profile_view.dart`,
Students chapter) — a green "Parent is following · last opened…" chip appears under the student's
details; tapping it opens the followers sheet.

The chip itself is silent/ambient: it renders nothing at all (not even a placeholder) until the probe
below confirms at least one follower, so a teacher on an older backend or offline sees the profile
exactly as it always looked.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Chip probe (fires as soon as the profile mounts) | `GET api/teacher/parent-portal/students/{teacherStudentId}/followers` | — | list of `{id, claimedPhone, parentName, autoApproved, origin, grantedAt, lastSeenAt}` — chip stays hidden on an empty list or any failure; otherwise shows the most recent `lastSeenAt` ("Last opened 2h ago" / "Not opened yet") |
| Tap the chip → opens "Following parents" sheet | *(reuses the chip's already-loaded data — no extra call)* | — | same list rendered per-follower: name (or just the phone if none given), phone (selectable), an origin chip — "Number was on the student's record" (`rosterPhone`) / "Approved number, new device" (`trustedPhone`) / "You approved this number" (`teacherApproved`/legacy `autoApproved:false`) — "Since {date}", "Last opened…" |
| Per-follower "Revoke" → confirm ("will lose access on every device") | `POST api/teacher/parent-portal/followers/{followerId}/revoke` | — (no body) | `data.revokedCount` (defaults to `1` on an older server that answers `data: true`), `data.revokedPhone`, `data.teacherStudentId` — row removed from the sheet; toast "Access removed" or "Access removed on {count} devices"; sheet auto-closes once the last follower is gone |

**Error codes handled specially:**
- `409` with body `code == "ParentPortalRevokeBlockedRosterPhone"` (the number being revoked is the
  student's own saved parent phone — revoking it is futile, the next visit re-admits the parent
  automatically) → **not** shown as an error. A dialog explains this and offers "Open student record"
  (the only real fix — clear the number on the roster record), wired through
  `onOpenStudentRecord` supplied by the profile screen.
- `404` with a business `code` (already revoked elsewhere) drops the row silently and shows the
  server's message; a bare/unavailable 404 is swallowed with no message at all (internal sentinel,
  never toasted).

---

### Endpoint coverage

**Used:**
- `GET api/teacher/student-links/my-code`
- `GET api/teacher/student-links` (list + the `page=1&pageSize=1` capacity-probe variant)
- `GET api/teacher/student-links/requests` (list + the `page=1&pageSize=1` side-menu badge probe)
- `POST api/teacher/student-links/requests/{linkId}/accept`
- `POST api/teacher/student-links/requests/{linkId}/reject`
- `POST api/teacher/student-links/remove`
- `POST api/teacher/student-links/{linkId}/bind`
- `POST api/teacher/student-links/{linkId}/unbind`
- `POST api/teacher/student-links/{linkId}/reset-device`
- `GET api/teacher/parent-portal/summary`
- `GET api/teacher/parent-portal/requests`
- `POST api/teacher/parent-portal/requests/{requestId}/approve`
- `POST api/teacher/parent-portal/requests/{requestId}/reject`
- `POST api/teacher/parent-portal/requests/bulk`
- `GET api/teacher/parent-portal/students/{teacherStudentId}/followers`
- `POST api/teacher/parent-portal/followers/{followerId}/revoke`

Every `webPathTeacherStudentLinks*` / `webPathTeacherParentPortal*` constant in `web_constant.dart` is
called from somewhere in these two folders — none defined-but-unused in this family.

**Called from these screens but outside this chapter's endpoint family** (documented in their own
chapters): `GET api/teacherstudent/students` (roster search inside the "Link this student" picker
sheet, shared by Link Requests' Accept and Linked Students' Bind) and the general teacher-settings
save endpoint (the parent-portal master switch + the three `parentSees*` visibility toggles, and the
teacher code shown in the Share card is read from local storage, not fetched here).
