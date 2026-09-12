# 06 — Teacher: Videos (VCM)

Source: `lib/feature/teacher_module/videos/**` (~63 files). Endpoint constants:
`lib/core/network_services/web_constant.dart` (`webPathVideoUnits*`, `webPathVideo*`, lines ~520–551),
plus the shared `webPathUpload = 'api/upload'` (line ~505) for the video photo / attachment / exam-question-image
handshake.

Data flow: `TeacherVideoRemoteDataSource` (Dio) → `TeacherVideoRepositoryImpl` → cubits → views. All
responses are read through the standard envelope `{success, code, message, data}`. List endpoints
(`fetchUnits`, `fetchUnitVideos`) use the standard paginated shape — the unwrapped envelope `data` is
itself `{data: [...rows], page, pageSize, totalCount, totalPages}` (the row array is keyed `data`, not
`items`). **Deviation to note:** the analytics list (`GET .../analytics`) does **not** use that shape —
its row array is keyed `rows`, sibling to `page`/`pageSize`/`totalCount`/`totalPages`, and the DTO is
parsed as a plain object, not through the paginated-response parser. Don't assume `items`/`data` there.

"Video category" in the UI/locale strings = **Video Unit** on the wire (`api/video-units*`). The two
names are used interchangeably below; the JSON always says `videoUnitId` / `unitId`.

## The upload → fileId → attach handshake

Every file (video photo, PDF attachment, video-exam question image) goes through the single shared
`UploaderService` (`lib/core/services/image/uploader_service.dart`), never a per-feature endpoint:

1. **`POST api/upload`**, multipart `FormData`: field `files` (one or more `MultipartFile`) + field
   `category` (string). Response envelope `data` is a **list**: `[{fileId, url, originalName, size,
   mimeType}]` (`UploadData.fromJson`). The app always uses `data.first` for a single-file pick.
2. Category values sent (`lib/core/services/image/upload_category.dart`, `UploadCategory.apiValue`):
   `"VideoPhoto"` (cover image, picked via camera/gallery + client-side JPEG compress to 1080px/85%),
   `"VideoAttachment"` (PDF, ≤25MB else offered "Compress & upload" down to fit, refused outright over
   100MB as unsafe to compress on-device), `"VideoExamQuestionImage"` (an image on one quiz question,
   uploaded by the shared `TeacherOnlineExamQuestionCubit` from the exams module, not video-specific
   code).
3. The returned `fileId` is held client-side and only becomes durable when it rides a create/update
   video request:
   - **Video photo** → JSON key **`videoPhotoFileId`** on both `POST api/videos` and
     `PUT api/videos/{id}`.
   - **PDF attachment(s)** → JSON key **`attachmentFileIds`** (array of strings) on both create and
     update. On update, an **empty/omitted array is "leave unchanged"**, not "clear" — see the Add/Edit
     Video screen note below for the consequence.
   - **Quiz question image** → per-question JSON key **`imageFileId`** inside `exam.questions[]`.
4. `PUT api/upload` (`FormData {FileId, File}`) replaces a file in place and returns a new `fileId`;
   `DELETE api/upload?fileId=...` deletes an uploaded-but-not-yet-attached file. Neither is called
   anywhere in the videos module today (both exist only in the shared uploader).

---

## Video Categories (units list)
_Dart file: `lib/feature/teacher_module/videos/view/teacher_video_lists_view.dart`_
_Cubit: `TeacherVideoListsCubit`_
**Reached from:** Teacher home / side menu → "Videos".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / search (350ms debounce) / infinite scroll | `GET api/video-units` | Query `Page`, `PageSize=10`, `Search` (trimmed, omitted if empty) | Per row: `id`, `title`, `description`, `videoCount`, `seenStudentCount`, `unseenStudentCount`, `publishedVideoCount`, `scheduledVideoCount` (both default 0 on an older backend — used to derive the Draft/Scheduled/Published card badge client-side), `attachmentCount`, `quizCount`, `recipientLabels[]` (strings, e.g. session/group names for the card subtitle) |
| FAB "+" | *(navigation only)* | — | opens **Create Video Category** below |
| Card tap | *(navigation only)* | — | opens **Unit Videos** below |
| Card ⋮ → Edit | *(navigation only)* | — | opens **Edit Video List** below |
| Card ⋮ → Delete (unit has 0 videos) | `DELETE api/video-units/{unitId}` | — | 200 → success toast, row removed. **409 Conflict** → generic conflict message (should not happen when `videoCount==0`, defensive) |
| Card ⋮ → Delete (unit has ≥1 video, confirms "delete all videos") | `GET api/video-units/{unitId}/videos` (drains all pages) then `DELETE api/videos/{id}` **once per video, sequentially**, then `DELETE api/video-units/{unitId}` | — | On any per-video delete failure the whole sequence aborts and the unit row is restored (locally) with its `videoCount` reset to 0 only if the videos were removed but the final unit delete 409s |

## Create Video Category
_Dart file: `lib/feature/teacher_module/videos/view/teacher_video_category_create_view.dart`_
_Cubit: `TeacherVideoCategoryCreateCubit`_
**Reached from:** Video Categories list → "+" FAB.

Recipients (Groups/Sessions) are picked via the shared exam-target-scope picker screen
(`AppRoute.goToTeacherExamTargetScope`) — same picker the Online Exam module uses. At least one
session or group is required to submit (`canSubmit`).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Create" | `POST api/video-units` | `{title, description, scopes: [{scopeType, sessionId, sessionGroupId}, ...]}` — one entry per selected session/group, `scopeType` is `"Session"` or `"SessionGroup"`, the other id key is `null` | `data.videoUnitId` (int) — used to build the local `TeacherVideoList` and navigate to the success screen |

```json
{
  "title": "Algebra",
  "description": "Covers chapter 2 exercises",
  "scopes": [
    { "scopeType": "Session", "sessionId": 228, "sessionGroupId": null },
    { "scopeType": "SessionGroup", "sessionId": null, "sessionGroupId": 14 }
  ]
}
```

## Video Category Created (success)
_Dart file: `lib/feature/teacher_module/videos/view/teacher_video_category_success_view.dart`_
**Reached from:** Create Video Category → successful submit. No API of its own — "Go to video
category" replaces the route with **Unit Videos** for the just-created unit.

## Edit Video List
_Dart file: `lib/feature/teacher_module/videos/view/teacher_edit_video_list_view.dart`_
**Reached from:** Video Categories list → card ⋮ → Edit.

If the passed-in `TeacherVideoList` already carries `scopes` (cached from the list load), the screen
seeds recipients from it; otherwise it calls `GET api/video-units/{unitId}` once up front to fetch
current scopes so they're editable rather than read-only.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open (scopes not cached) | `GET api/video-units/{unitId}` | — | `data.scopes[]` (see scope shapes below) to seed the recipient picker; `data.totalStudentsInScope` not shown here |
| "Save" | `PUT api/video-units/{unitId}` | `{title, description, scopes: [...], cascadeToVideos}`. `scopes` is **omitted from the effective payload only conceptually** — the client always sends the field, but only overwrites it with the teacher's edits when the recipient picker was actually touched this visit (`_scopesEdited`); otherwise it resends the unit's existing scopes verbatim so a title-only edit can't silently wipe them. `cascadeToVideos` defaults `false` | 200 → success toast, list row updated locally. **409 Conflict** (removing a scope still used by ≥1 member video) → dialog "Remove from videos too?" naming those videos; confirming resends the same PUT with `cascadeToVideos: true` |

Non-trivial request body — removing a scope that member videos still reference, after the cascade
confirm:
```json
{
  "title": "Algebra",
  "description": "Covers chapter 2 exercises",
  "scopes": [
    { "scopeType": "SessionGroup", "sessionId": null, "sessionGroupId": 14 }
  ],
  "cascadeToVideos": true
}
```

**Scope shapes read back from the API (both handled defensively):**
- New shape: `scopes[].scopeType` + `scopes[].target.{id, name, studentCount}`.
- Legacy shape: `scopes[].scopeType` + flat `sessionId`/`sessionGroupId` + `sessionName`/`groupName` +
  `studentCount`.
- A third variant used only by the full video-detail GET's `scopes[]`: `{scopeType, ids: [...]}` (one
  entry per type, expanded client-side into one `TeacherVideoScopeInput` per id).

## Unit Videos (videos inside a category)
_Dart file: `lib/feature/teacher_module/videos/view/teacher_unit_videos_view.dart`_
_Cubit: `TeacherUnitVideosCubit`_
**Reached from:** Video Categories list → tap a card.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / search (350ms debounce) / Draft-Published filter sheet / infinite scroll | `GET api/video-units/{unitId}/videos` | Query `Page`, `PageSize`, `Search`, `Status` = `"Published"` \| `"Draft"` (omitted for "All" — filtered **server-side** so paging/count stay consistent with the visible rows) | Per row: `id`, `title`, `description`, `sourceUrl`, `durationSeconds`, `seenStudentCount`, `unseenStudentCount`, `createdAt`, `status` (nullable → defaults Draft), `publishDate`, `sourceType`, `studentsInScope`, `totalOpens`, `videoPhotoFileId`, `videoPhotoUrl`, `questionsNumber`, `attachmentsNumber` — badge (Draft/Scheduled/Published) is derived client-side from `status`+`publishDate` vs now (UTC) |
| FAB "+" | *(navigation only)* | — | opens **Add Video** below, pre-scoped to this unit |
| Card tap | *(navigation only)* | — | opens **Video Detail** below |
| Card ⋮ / long-press → "Select" mode → Edit | `GET api/videos/{id}` (via a throwaway `TeacherVideoDetailCubit.loadDetail()` before navigating, so the edit form opens with exam/scopes/attachments already loaded) | — | opens **Add/Edit Video** in edit mode |
| Card ⋮ → Delete (single) | `DELETE api/videos/{id}` | — | row removed, success toast |
| "Select" mode → checkboxes → "Select all" (drains every remaining page first) → Delete | `DELETE api/videos/{id}` **once per selected id, sequentially** | — | rows removed as each succeeds; any failure restores the still-failed rows and reports a partial success |

## Add / Edit Video
_Dart file: `lib/feature/teacher_module/videos/view/teacher_add_video_view.dart` (2-step flow: Details → Questions, the latter in `lib/.../view/widgets/video_questions_step_body.dart`)_
_Cubit: `TeacherVideoCreateCubit` (+ the shared `TeacherOnlineExamQuestionCubit` for quiz drafts)_
**Reached from:** Unit Videos → "+" FAB (unit pre-selected); Teacher home "Add video" shortcut (unit
picker shown first, `GET api/video-units` paged in full to populate it); Video Detail → Settings tab →
"Edit Video" (opens the same screen pre-filled, backed by `TeacherVideoDetailCubit.loadFullDetail()`
having already run `GET api/videos/{id}`).

Step 1 (Details) collects title/description/publish-visibility/date/link/duration/files/photo/
recipients; Step 2 (Questions) collects the optional quiz. Nothing is sent to the server until Step 2's
final Create/Save button.

**Duration** is never typed by hand (a manual-entry box was shipped and removed same day — commit
`b4087af2` — because only YouTube links are playable in-app and a typed number could only match what's
already known or be wrong). On paste, the app scrapes the YouTube watch page client-side
(`YoutubeDurationFetcher`, 500ms debounce, no API call to the backend) for `lengthSeconds` and shows
either "`{minutes} min`" or "Duration pending" — this is display-only, exact seconds are kept
internally.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Unit picker (create-from-home only) | `GET api/video-units` (all pages drained) | — | populates the category dropdown |
| Category selected / unit pre-supplied | `GET api/video-units/{unitId}` | — | `data.scopes[]` → allowed sessions/groups for the recipient picker (create: pre-selects all of the dominant type; edit: filters the video's current selection down to what the unit still allows) |
| "Create" (Step 2 submit, new video) | `POST api/videos` | See JSON below | `data.videoAssetId`, `data.examId` |
| … then, only if Published or a publish date was picked | `PATCH api/videos/{id}/status` | `{status: "Published"\|"Draft", publishDate}` (ISO instant, UTC) | 200 → done. On failure the just-created video is deleted (`DELETE api/videos/{id}`, best-effort cleanup) and the original error is surfaced |
| "Save" (Step 2 submit, editing) | `PUT api/videos/{id}` | See JSON below | Full `TeacherVideoDetailApiDto` — merged into the edit form's video; **409 Conflict** (stale `rowVersion`) → error "This lesson was changed elsewhere…", the app silently reloads `GET api/videos/{id}` for a fresh `rowVersion` and leaves the form open so the teacher can re-save |
| Thumbnail tile → pick image | `POST api/upload` (`category: "VideoPhoto"`) | multipart, compressed JPEG | `fileId` → `videoPhotoFileId` on the next save; `url` → local preview |
| "Add file" (PDF) | `POST api/upload` (`category: "VideoAttachment"`) | multipart (compressed first if >25MB, refused outright if >100MB) | `fileId` appended to `attachmentFileIds` on the next save |
| Recipients → Groups / Sessions tile | *(navigation only)* | — | opens the shared target-scope picker, pre-filtered to what the unit allows |

**Create JSON** (`POST api/videos`) — note `durationSeconds` and `isPublished`/`publishDate` are **not**
part of this body at all; publish state is a separate follow-up `PATCH .../status` call, and the
scraped duration is simply not sent on create (only on a later edit's `PUT`):
```json
{
  "title": "Lesson 3 — Quadratics",
  "description": "Covers chapter 2 exercises",
  "sourceUrl": "https://www.youtube.com/watch?v=abc123XYZ",
  "unitIds": [42],
  "scopes": [
    { "scopeType": "Session", "ids": [228, 231] },
    { "scopeType": "SessionGroup", "ids": [14] }
  ],
  "exam": {
    "title": "Quiz",
    "description": "Optional",
    "questions": [
      {
        "text": "2x + 3 = 7, x = ?",
        "questionType": "SingleChoice",
        "imageFileId": "f-9a2c...",
        "options": [
          { "text": "2", "isCorrect": true },
          { "text": "3", "isCorrect": false }
        ]
      }
    ]
  },
  "videoPhotoFileId": "f-1b77...",
  "attachmentFileIds": ["f-44de...", "f-0091..."]
}
```
`scopes`, `exam`, `videoPhotoFileId`, `attachmentFileIds` are all omitted entirely when empty (a
scope-less/quiz-less/photo-less/attachment-less video is a valid create).

**Update JSON** (`PUT api/videos/{id}`) adds status/schedule/version fields; `scopes` here use
`sessionId`/`sessionGroupId` (singular per entry), not the create shape's `ids` array:
```json
{
  "title": "Lesson 3 — Quadratics",
  "description": "Covers chapter 2 exercises",
  "sourceUrl": "https://www.youtube.com/watch?v=abc123XYZ",
  "status": "Published",
  "removeAttachment": false,
  "publishDate": "2026-09-20T06:00:00.000Z",
  "durationSeconds": 253,
  "unitIds": [42],
  "rowVersion": "AAAAAAAAB9E=",
  "scopes": [
    { "scopeType": "Session", "sessionId": 228 }
  ],
  "exam": { "title": "Quiz", "questions": [] },
  "videoPhotoFileId": "f-1b77...",
  "attachmentFileIds": ["f-44de..."]
}
```

**Publish-date semantics (important, backend-visible contract):** an **omitted** `publishDate` on
`PUT` means *keep whatever schedule is already stored* — it is not read as "clear it". The only way to
clear a stored schedule is the explicit `"clearPublishDate": true` key, which wins over `publishDate`
if both were somehow sent (the client never sends both). **The reachable Add/Edit Video screen has no
UI control that ever sends `clearPublishDate: true`** — its date picker can only set a date, never
clear one. A "Remove schedule" switch that does set it exists only in `teacher_edit_video_view.dart`,
a screen that is **fully unreachable** (no route pushes it; `AppRoute.goToTeacherEditVideo` always
pushes `TeacherAddVideoView.forEdit`, not this file). In practice, once a video has a scheduled
`publishDate`, the currently-shipping app cannot clear it — only replace it with a different date, or
flip Draft/Published from the detail screen's Settings tab (which itself always sends
`publishDate: null` via a plain status-only PATCH, see below).

**Attachment-removal caveat:** `UpdateTeacherVideoParams.removeAttachment` and its
`attachmentFileIds` are threaded end-to-end (params → JSON), but `removeAttachment` is **never set to
`true` anywhere in the app** — no UI exposes it. Deleting a file chip in the edit form only empties the
local `attachmentFileIds` list, and an empty/omitted `attachmentFileIds` on `PUT` means "leave the
existing attachment(s) unchanged" (see the upload-handshake section above) — so removing the chip
client-side currently has **no effect** on the server's stored attachment.

## Video Detail
_Dart file: `lib/feature/teacher_module/videos/view/teacher_video_detail_view.dart`_
_Cubit: `TeacherVideoDetailCubit`_
**Reached from:** Unit Videos → tap a card.

Three tabs: **Overview** (stats/description/audience/files), **Quiz** (read-only questions), **Settings**
(visibility toggle / Edit / Delete). Overview loads first (lighter payload); Quiz/Settings lazy-load
the full detail the first time either tab is opened.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open | `GET api/videos/{id}/overview` | — | `id, title, description, sourceUrl, durationSeconds, status, seenStudentCount, unseenStudentCount, completedStudentCount, publishDate, rowVersion, attachment{id,fileName,fileSizeBytes,readUrl}, sourceType, videoPhotoFileId, videoPhotoUrl, questionsNumber, attachmentsNumber, studentsInScope, totalOpens, audience[].target.name (→ audienceLabels), audienceStudentCount` |
| First open of Quiz or Settings tab | `GET api/videos/{id}` | — | Full detail: adds `attachments[]` (or singular legacy `attachment`), `scopes[]` (`{scopeType, ids[]}` shape), `exam.{title, description, questions[]}` — merged in without discarding the overview-only stats/audience already held |
| "Seen (n)" / "Unseen (n)" / "Completed (n)" stat card | *(navigation only)* | — | opens **Seen/Unseen/Completed Students** below, pre-filtered by kind |
| "Watched by class" link | *(navigation only)* | — | opens **Watched by Class** below |
| File row → download | *(no call — opens `readUrl` directly, e.g. the Azure SAS URL, in an external browser/viewer)* | — | — |
| Settings tab → Visibility switch | `PATCH api/videos/{id}/status` | `{status: "Published"\|"Draft", publishDate: null}` — **always clears any schedule** when toggled this way | 200 → then a fresh `GET api/videos/{id}/overview` re-pulled for the new `rowVersion`/stats (the toggle response itself carries neither) |
| Settings tab → "Edit Video" | `GET api/videos/{id}` (via the same `loadFullDetail`, if not already loaded) | — | opens **Add/Edit Video**; on return, `GET api/videos/{id}/overview` is re-pulled (`force: true`) since an edit can change the URL, audience, or publish state |
| Settings tab → "Delete video" | `DELETE api/videos/{id}` | — | 200 → pop back to Unit Videos |

## Watched by Class
_Dart file: `lib/feature/teacher_module/videos/view/teacher_video_session_watch_view.dart`_
_Cubit: `TeacherVideoSessionWatchCubit`_
**Reached from:** Video Detail → Overview tab → "Watched by class" link.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/videos/{id}/analytics/by-session` | — | `videoAssetId, title, totalStudentsInScope, totalStudentsWatched, unseenCount, completedCount, rows[]` where each row is `{sessionId (null = "not in a class" bucket, sorted last), sessionName, sessionGroupId, sessionGroupName, studentsInScope, watchedCount, unseenCount, completedCount, watchedPct}` — rows sum to `totalStudentsInScope` (a student reachable via both a direct session and a scoped group is counted once, keyed on their own session) |
| Row tap (has a `sessionId`) | *(navigation only)* | — | opens **Seen/Unseen/Completed Students** filtered to that `sessionId`, defaulting to the "unseen" tab if any student there hasn't watched, else "seen" |
| Row tap (the null-`sessionId` "Not in a class" bucket) | *(disabled — no tap target, nothing to filter by)* | — | — |

## Seen / Unseen / Completed Students
_Dart file: `lib/feature/teacher_module/videos/view/teacher_video_student_stats_view.dart`_
_Cubit: `TeacherVideoStudentStatsCubit`_
**Reached from:** Video Detail → Overview tab stat cards; Watched-by-Class row tap (pre-selects
`initialSessionId`).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open (session filter chips) | `GET api/videos/{id}/analytics/by-session` | — | reuses the same per-session rows as Watched by Class, purely to populate the filter chip row (best-effort — a failure just hides the chips) |
| Screen load / search (350ms debounce) / session chip / infinite scroll | `GET api/videos/{id}/analytics` | Query `Page`, `PageSize=30`, `Search`, `StatusFilter` = `"Seen"` \| `"Unseen"` \| `"Completed"` (from the `kind` the screen was opened with — this is fixed per screen, not user-togglable here), `SessionId` (from the chip row, omitted for "All classes") | **Non-standard pagination** — row array is `data.rows` (not `data.data`): each row `{teacherStudentId, studentName, studentCode, sessionName (nullable), hasOpened, openCount, totalWatchSeconds, estimatedCompletionPct}`; `page/pageSize/totalCount/totalPages` siblings of `rows` |

## Video Fullscreen Preview
_Dart file: `lib/feature/teacher_module/videos/view/teacher_video_fullscreen_screen.dart`_
**Reached from:** Video Detail's inline player → maximize. No API of its own — pure landscape
player chrome around the same `sourceUrl`; watch-time tracking (`start`/`stop`) is a **student-only**
concern (`webPathStudentVideoWatchStart/Stop`, outside this chapter) and is never called from the
teacher side, including here.

---

### Dead code found in this chapter (not reachable from any screen)

- `lib/feature/teacher_module/videos/view/teacher_edit_video_view.dart` (`TeacherEditVideoView`) — a
  single-step edit form with a working "Remove schedule" switch (→ `clearPublishDate: true`). Nothing
  constructs it; `AppRoute.goToTeacherEditVideo` always pushes `TeacherAddVideoView.forEdit` instead.
  It is the only place in the app that can clear a video's scheduled `publishDate`.
- `TeacherVideoRepository.toggleVideoStatus` → `PATCH api/videos/{id}/status/toggle` — implemented
  end-to-end (data source, repository) but no cubit/view ever calls it. The reachable visibility
  toggle uses `PATCH api/videos/{id}/status` (an explicit `status` value) instead.
- `TeacherVideoRepository.assignVideoUnits` → `PUT api/videos/{id}/units` — same story, fully wired,
  zero call sites.
- `TeacherVideoRepository.downloadAttachment` → `GET api/videos/{id}/attachments/{attachmentId}/download`
  — fully wired (including a cubit method and state fields for the downloaded bytes), but the Overview
  tab's file row downloads via the gated `readUrl` from the overview/detail payload directly instead,
  never through this endpoint.
- `const String webPathVideosTeacher = 'api/videos/teacher'` — the constant is declared and never
  referenced by any request in the app.

### Endpoint coverage

- `GET api/video-units` — units list (paged, search)
- `GET api/video-units/{unitId}` — unit detail (scopes, for edit/create-scope loading)
- `POST api/video-units` — create unit
- `PUT api/video-units/{unitId}` — update unit (title/description/scopes/cascadeToVideos)
- `DELETE api/video-units/{unitId}` — delete unit
- `GET api/video-units/{unitId}/videos` — videos in a unit (paged, search, `Status` filter)
- `POST api/videos` — create video
- `PUT api/videos/{id}` — update video
- `DELETE api/videos/{id}` — delete video
- `GET api/videos/{id}` — full video detail (exam/scopes/attachments)
- `GET api/videos/{id}/overview` — lightweight detail (stats/audience)
- `PATCH api/videos/{id}/status` — set Draft/Published (+ optional `publishDate`, or explicit `clearPublishDate`)
- `GET api/videos/{id}/analytics` — per-student seen/unseen/completed list (paged, `StatusFilter`, `SessionId`)
- `GET api/videos/{id}/analytics/by-session` — per-class watch breakdown
- `POST api/upload` / `PUT api/upload` / `DELETE api/upload?fileId=` — shared file handshake (categories `VideoPhoto`, `VideoAttachment`, `VideoExamQuestionImage`)

**Defined in `web_constant.dart`/the repository but never called by any screen** (see "Dead code"
above for detail): `PATCH api/videos/{id}/status/toggle`, `PUT api/videos/{id}/units`,
`GET api/videos/{id}/attachments/{attachmentId}/download`, and the bare constant
`webPathVideosTeacher` (`api/videos/teacher`, no method ever built against it).
