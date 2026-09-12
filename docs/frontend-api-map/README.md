# Edvanz Mobile App — Frontend → API Map

A screen-by-screen reference of **what the Flutter mobile app actually calls on the backend**:
for every screen and every button/action — the HTTP method + endpoint, the exact request keys it
sends, and the exact response keys it reads. Written for a backend developer who does **not** have
the frontend repository. Wire key names were extracted from the app's real `toJson()` /
`fromJson()` code, not guessed from backend DTOs — so where the app misspells a key or sends
something surprising, the doc records what the app really does.

Snapshot date: **2026-09-12**, taken from the app's `NewApp` branch working tree.
Scope: the **mobile app only**. The Angular admin UI (`EdvanzAdminUI`) and the PHP parent portal
(`EdvanzParentPortal`) are separate frontends and are not covered here.

## How to use this

1. Start with **chapter 01** — it defines the conventions every other chapter leans on: base URL,
   the `{success, code, message, data}` response envelope, the paginated shape, standard headers
   (`Authorization`, `Accept-Language`, `X-Device-Id`, `X-Acting-Teacher-Id`), and the token-refresh
   flow.
2. Then jump to the chapter for the screen you care about. Each screen section has a table:
   *UI element / action → endpoint → what it sends → which response keys the app reads*, followed
   by JSON examples for non-trivial bodies and the error `code` values the UI special-cases.
3. Each chapter ends with an **"Endpoint coverage"** list — a quick index of every
   `METHOD path` that chapter documents.

## Chapters

| # | File | Covers |
|---|---|---|
| 01 | [01-conventions-auth-shell.md](01-conventions-auth-shell.md) | Global wire conventions, login/register/OTP/change-password/delete-account, bottom-nav shells, teacher home dashboard, side menu, notifications, app-version force-update gate, help manifest, uploads & gated files |
| 02 | [02-teacher-students.md](02-teacher-students.md) | Student list/filters, add/edit student, bulk import (NDJSON streaming), student profile, barcode & Excel exports, recycle bin |
| 03 | [03-teacher-sessions.md](03-teacher-sessions.md) | Session create/edit (schedule wire formats: day index 0=Sat…6=Fri), session hub, groups, session links, assign/transfer students, violations lists |
| 04 | [04-teacher-attendance.md](04-teacher-attendance.md) | The register, mark/hold flows, absence & unpaid pop-ups, QR scan (resolution is client-side), monthly grids, timelines, **offline queue + `POST api/Attendance/sync` replay contract** |
| 05 | [05-teacher-payments-subscription.md](05-teacher-payments-subscription.md) | Tracking dashboard, per-status lists, collect flow (lookup/submit/QR queue), collections ledgers (exact-instant `from`/`to` convention), assistant wallets & withdrawals, edit/delete/revert transactions, forgive, departures, **offline `POST api/Payment/sync`**, subscription screens |
| 06 | [06-teacher-videos.md](06-teacher-videos.md) | Units & videos CRUD, upload→`fileId`→attach handshake, publish flow, watch analytics + per-class breakdown |
| 07 | [07-teacher-exams-homework.md](07-teacher-exams-homework.md) | Offline (paper) exams incl. DuringSession per-session occurrences, grades entry (+ per-item error codes), exam attendance, online exams + question editor + results/analysis; homework (mock-only) |
| 08 | [08-teacher-assistants-messages-settings.md](08-teacher-assistants-messages-settings.md) | Assistants CRUD + permissions matrix, teacher configuration (full PUT body incl. proration/billing-start readbacks), audit trail, independence requests; messages (mock-only) |
| 09 | [09-teacher-links-parent-portal.md](09-teacher-links-parent-portal.md) | Teacher code, link request inbox, accept/bind/unbind semantics (roster code vs account code!), linked students, device reset; parent-portal approval inbox, followers, revoke |
| 10 | [10-student-learning.md](10-student-learning.md) | Student videos (watch start/stop tracking contract), video quizzes, online exams (autosave/submit/violation flow), offline-exam results list |
| 11 | [11-student-account.md](11-student-account.md) | Student home & teacher cards, add-teacher linking flow, per-teacher home aggregate (visibility flags, device lock), barcode, attendance month calendar, payment tracking |
| 12 | [12-parent-center.md](12-parent-center.md) | Parent app (largely mockup — see below), center account: overview, teachers/assistants management, front-desk scan, revenue, subscription, settings, `X-Acting-Teacher-Id` acting mode |

## Read this before assuming a module is "broken"

Several things in the app **look** wired but are not — a backend dev should not wait for traffic
from them:

- **Messages module is 100% mock** — the repository is DI-bound to an in-memory mock; zero real
  endpoints exist or are called. The chapter documents its UI contract as a build-toward spec.
- **Homework (teacher & student) is mock/unwired** and the menu entry is hidden by the
  `isMvp` flag. One side effect is real: its question-image picker performs genuine
  `POST api/upload` calls whose files are never attached.
- **Most of the parent app renders hardcoded values.** Only two real parent calls exist:
  `GET api/payment/parent/children/{childId}/teachers/{teacherId}/tracking` and
  `PUT api/ParentUser/{parentUserId}/profile` (language only). There is **no parent attendance
  call anywhere in the client** yet.
- **Notifications UI never fires in the shipped build** — `AppConfig.isMvp = true` turns off the
  bell + unread polling app-wide, though the wiring is complete.
- **Audit trail screen works but is unreachable** — no menu links to it in the current build.

## Endpoints the backend serves that no client screen calls today

Consolidated from the per-chapter findings (each chapter has the detail). Useful when you wonder
"who calls this?" — today, nobody in the mobile app:

| Area | Dead / uncalled endpoint |
|---|---|
| Auth | `POST api/auth/forgot-password`, `POST api/auth/reset-password` (UI dead-ends before calling), `GET api/auth/me` |
| Students | `GET api/teacherstudent/students/overview`, `POST api/teacherstudent/bulk-import` (only the `/stream` variant is used), `POST api/teacherstudent/bulk-restore` |
| Attendance | `POST api/Attendance/release-hold`, `DELETE api/Attendance/delete`, `GET …/sessions/{id}/unmarked-count`, `GET …/records/{id}/history`, `GET api/Attendance/reports`, `GET api/Attendance/reports/export`, `GET …/timeline/students` (bare list), `GET …/timeline/students/{id}/export` |
| Payments | `POST api/v1/payments/collect/mark-paid`, `GET/POST api/Payment/wallets/{id}` + `/reset` (legacy pair; v1 wallet endpoints are the live ones), `GET api/v1/payments/collections/yearly`, `POST api/v1/payments/forgive/{id}/reverse` (wired, no UI caller) |
| Subscription | `GET api/Subscription/current`, `GET api/subscription/requests/{id}` |
| Videos | `PATCH api/videos/{id}/status/toggle`, `PUT api/videos/{id}/units`, `GET api/videos/{id}/attachments/{aid}/download` (app uses the gated `readUrl` directly), `api/videos/teacher` |
| Online exams | `POST api/online-exams/{id}/questions/bulk`, `GET api/online-exams/{id}/questions/overview`, student `POST …/block` (block state is inferred from the `violation` response) |
| Teacher misc | `GET api/Teacher/capacity-packages` |
| Student | `GET api/studentuser/me` (bare; `me/profile` is PUT-only in practice) |
| Center | `GET api/center/sessions/today` (the picker uses `…/sessions/schedules`) |
| Legacy | The whole non-`api/` block in `web_constant.dart` (departments/companies/employees/salary/kpis/…) is dead scaffolding from a template |

## Cross-cutting behaviors worth knowing

- **Offline-first writes**: attendance marks and payment collections are queued locally when
  offline and replayed in batches — attendance via `POST api/Attendance/sync`, payments via
  `POST api/Payment/sync`, both carrying per-entry `clientEntryId` idempotency keys. Chapters 04
  and 05 document the exact entry/result shapes, including the conflict / needs-confirmation
  semantics.
- **Center acting mode**: once a center picks a teacher, the app reuses the ordinary teacher
  endpoints unchanged with the `X-Acting-Teacher-Id` header attached (chapter 12); the
  `api/center/*` family itself is called without that header.
- **Gated files**: no file bytes are ever fetched from blob storage directly — the app uploads via
  `POST api/upload` (multipart, `category` decides visibility) and displays via the returned
  `api/files/{fileId}` URL with auth headers (chapters 01 and 06).
- **Wire quirks preserved on purpose**: some deployed key spellings are wrong but load-bearing —
  e.g. the assistants list query param `isAcitve`, audit fields `assisstantName`/`acction`, query
  casing differing between exam families. The chapters flag each one; do not "fix" them without a
  coordinated app release.

## Keeping this current

This map is a snapshot. When a screen or contract changes, update the matching chapter section —
the endpoint constants all live in one frontend file (`lib/core/network_services/web_constant.dart`),
so a diff of that file between releases is a cheap way to spot new/removed endpoints that need
re-documenting.
