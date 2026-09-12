# 10 — Student App: Videos, Exams & Homework

Covers the student side of the four content modules under
`lib/feature/student_module/`: **videos** (VCM), **exam** (teacher-authored online
exams), **offline_exams** (assignment-obligation exam results), and **homework**
(UI shell only — see the note at the end).

All endpoints in this chapter are scoped by `{teacherId}` in the path — the numeric
`Teacher.Id` of the linked teacher whose content the student is viewing, resolved
once when the student opens that teacher's card (never the student's own id). Every
call uses `ApiService.client(requireAuth: true)` (JWT bearer) and responses are
unwrapped through the standard `{success, code, message, data}` envelope
(`ensureApiSuccess` throws `ApiBusinessException` on `success:false`). List endpoints
that return a page use the standard `PaginatedApiResponse` shape
(`items`/`totalCount`/`page`/`pageSize`/`totalPages`). A 404 on a GET is mapped to a
typed `NotFoundFailure` (`statusCode: 404`) — several screens branch on that type
specifically (noted per-screen below), distinct from any other 4xx/5xx which just
surfaces `message` as a toast.

Two dead ends worth flagging up front (details in "Endpoint coverage"): the
`ExamCubit` / `webLessonExamQuestions` legacy lesson-quiz path is defined in code but
never reachable from any live navigation, and `POST …/exam/block` /
`POST …/online-exams/…/block` is wired in the online-exam data source but never
called — blocking is inferred client-side from the violation endpoint's response
instead.

---

## Video categories (units list)
_Dart file: `lib/feature/student_module/videos/view/video_units_list_screen.dart`_
**Reached from:** the linked-teacher home/dashboard screen (`teacher_details_view_body.dart`) — "Videos" tile in the resource-stats row (`AppRoute.goToVideoUnitsList`).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/videos/student/teachers/{teacherId}/units` | — (no query params) | array of `{id, title, description, videoCount, quizCount, subject, watchedCount}` — card shows title, description, subject, a `videoCount`/`quizCount` meta row, and a progress bar (`watchedCount/videoCount`, "All watched" once equal) |
| Tap a unit card | *(no call — navigation only)* | — | opens **Unit videos** with `unitId` + `unitTitle` |

```json
// GET api/videos/student/teachers/14/units → data[]
{
  "id": 66,
  "title": "Algebra Basics",
  "description": "Foundational algebra concepts",
  "videoCount": 12,
  "quizCount": 5,
  "subject": "Mathematics",
  "watchedCount": 4
}
```

---

## Unit videos (lessons list)
_Dart file: `lib/feature/student_module/videos/view/video_unit_lessons_list_screen.dart`_
**Reached from:** tapping a unit card on **Video categories**.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/videos/student/teachers/{teacherId}/units/{unitId}/videos` | query `Page` (int, default 1), `PageSize` (default 10) | paginated `items[]` of `{id, title, description, sourceType, sourceUrl, durationSeconds, assignedAt, hasOpened, lastOpenedAt, hasQuiz, questionsCount, watchStatus, subject, videoPhotoUrl, attachments[]}` |
| Infinite scroll (within 200px of the bottom) | same endpoint, `Page` + 1 | same query shape | items appended |
| Search box (400ms debounce) | same endpoint | adds `Search` (trimmed title text; omitted when blank — unfiltered call is byte-for-byte the same request) | server filters before paging so the count and rows agree |
| Watch-state chips: Not watched / In progress / Watched (mutually exclusive; tapping the active one clears it) | same endpoint | adds `WatchStatus` = `NotStarted`\|`InProgress`\|`Completed`; when `NotStarted` is selected, ALSO sends legacy `UnwatchedOnly: true` for compatibility with a server that predates the tri-state filter (ignored by current servers) | — |
| Tap a lesson card | *(no call)* | — | opens **Video lesson**; on return the list re-fetches with `force:true` so the row's watch badge reflects what was just watched (badge is server-computed, not derived locally) |

Wire keys straight from `StudentVideoApiDto.fromJson` / `StudentVideoAttachmentApiDto.fromJson`:

```json
// one item of GET .../units/66/videos → data.items[]
{
  "id": 119,
  "title": "Solving Linear Equations",
  "description": "Step-by-step walkthrough",
  "sourceType": "YouTube",
  "sourceUrl": "https://youtu.be/abc123",
  "durationSeconds": 645,
  "assignedAt": "2026-08-01T09:00:00Z",
  "hasOpened": true,
  "lastOpenedAt": "2026-09-10T18:22:00Z",
  "hasQuiz": true,
  "questionsCount": 5,
  "watchStatus": "InProgress",
  "subject": "Mathematics",
  "videoPhotoUrl": "https://.../api/files/{fileId}...(gated redirect target)",
  "attachments": [
    {
      "id": "3fa1...",
      "fileName": "worksheet.pdf",
      "contentType": "application/pdf",
      "fileSizeBytes": 245678,
      "readUrl": "https://.../api/files/{fileId}",
      "createdAt": "2026-08-01T09:00:00Z"
    }
  ]
}
```
`watchStatus` is read case-insensitively (`Completed`/`watched` → watched, `InProgress`/`in_progress` → in progress, anything else → not watched — no badge shown for "not watched"). `videoPhotoUrl`/`attachment.readUrl` are already-resolved gated file URLs (see §5.5 of the backend's own CLAUDE.md) — the app opens them directly (`CustomNetworkImage` / `AppHelper.openUrl`), it never calls `/api/files/{fileId}` itself.

---

## Video lesson (Overview / Files)
_Dart file: `lib/feature/student_module/videos/view/video_lesson_screen.dart`_
**Reached from:** tapping a lesson card on **Unit videos**. Carries the already-fetched `StudentVideo` object — this screen makes **no GET of its own**.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Thumbnail / "play" tile → **Open player** | *(navigation only)* | — | opens the fullscreen player (below), passing the same `StudentVideoDetailBloc` instance so watch tracking survives the screen change |
| Overview tab | *(no call)* | — | renders `title`, `subject`, `description`, duration (`durationSeconds` → `Xh Ym`/`Y min`), `questionsCount`, `assignedAt` (localized date), `watchStatus` label |
| Files tab | *(no call)* | — | lists `attachments[]`; tapping a row opens `attachment.readUrl` via the OS (`AppHelper.openUrl`) — no in-app download endpoint |
| Quiz CTA bar (shown only when `hasQuiz \|\| questionsCount > 0`) → **Start Quiz** | *(navigation only)* | — | opens **Video quiz** |

Screen-recording protection (`ScreenCaptureProtector` wrapping the body) is **local-only** — Android `FLAG_SECURE` / iOS capture-state detection via platform channel. It makes no API call.

---

## Video lesson — fullscreen player & watch tracking
_Dart file: `lib/feature/student_module/videos/view/video_lesson_fullscreen_screen.dart`_ (telemetry lives in `manager/student_video_detail_bloc/student_video_detail_bloc.dart`)
**Reached from:** tapping the thumbnail on **Video lesson**.

This is the only screen that calls start/stop. Player is YouTube-only (`youtube_player_flutter`); Google Drive sources show an "unsupported source" notice instead of a dead player.

| Trigger | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Player transitions paused→playing (also fires again after a resume once a prior stop settles) | `POST api/videos/student/teachers/{teacherId}/{videoAssetId}/start` | `{deviceId, clientEventId, videoDurationSeconds?}` | `{resumeFromSeconds, durationSeconds}` — the player seeks to `resumeFromSeconds` once per screen visit (skipped if ≤5s, so a few seconds watched still restarts from 0) |
| Player transitions playing→paused, OR a YouTube playback error interrupts an active segment | `POST api/videos/student/teachers/{teacherId}/{videoAssetId}/stop` | `{deviceId, clientEventId, positionSeconds?, deltaSeconds?, videoDurationSeconds?}` | success only (no response body parsed) |
| App backgrounded/inactive/detached while playing | same **stop** call | same shape | — |
| Leaving the fullscreen screen (back button or dispose, including an unexpected pop) | same **stop** call, best-effort; if it fails the pending stop is persisted locally (`PendingStudentVideoWatchStop`) and retried the next time this bloc is constructed, via an internal `StudentVideoDetailFlushPendingStop` event fired in the constructor | same shape | — |

Key semantics (exact wire keys from `StartStudentVideoWatchParams`/`StopStudentVideoWatchParams` in `model/student_video_params.dart`):
- `deviceId` — a UUID persisted once per app install (`VideoWatchIds.resolveDeviceId`), stable across sessions.
- `clientEventId` — a fresh UUID per logical play→pause segment; the SAME id is reused only when retrying that same start or that same stop (idempotency key), never reused across segments.
- `videoDurationSeconds` — **duration reporting is deliberately dual-sourced.** `start` fires the instant playback begins, often before the YouTube player's metadata has loaded, so it's frequently 0/omitted; the bloc tracks the **largest** duration seen all session (`_bestKnownDurationSeconds`, seeded from the player's own report AND the server's echoed `video.durationSeconds`) and sends that on every subsequent `stop` — this is how the backend first learns a length it has no other way to hear. Sent only when `> 0`.
- `positionSeconds`/`deltaSeconds` on stop — current playback position and elapsed wall-clock seconds for that segment, both clamped to `[0, durationSeconds]` when a duration is known.
- Only ONE start/stop pair is ever in flight per segment; a pause that arrives while `start` is still in-flight is queued and replayed once `start` resolves (so idle time isn't billed as watching), and a resume that arrives while `stop` is in-flight is queued and replayed once `stop` settles.

```json
// POST .../14/119/start
{ "deviceId": "b6b1-...", "clientEventId": "8f21-...", "videoDurationSeconds": 645 }

// POST .../14/119/stop
{
  "deviceId": "b6b1-...",
  "clientEventId": "9a02-...",
  "positionSeconds": 212,
  "deltaSeconds": 48,
  "videoDurationSeconds": 645
}
```

---

## Video quiz (take)
_Dart file: `lib/feature/student_module/videos/view/video_quiz_screen.dart`_ (state in `manager/student_video_exam_bloc/`)
**Reached from:** the "Start Quiz" CTA on **Video lesson**.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open | `GET api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam` | — | `{videoAssetId, videoExamId, title, description, questionsCount, totalDegree, status?, canRetake, lastPercentage?, lastScore?, questions[]}`; each question is `{questionId, questionText, questionType, degree, imageUrl?, options:[{optionId, optionText}]}` |
| Selecting an option | *(local state only — no save-as-you-go call for video quizzes, unlike online exams)* | — | — |
| Previous / Next | *(no call)* | — | — |
| Last question → **Review answers** | *(no call)* | — | opens the pre-submit summary (answered/unanswered per question, tap to jump back and edit) |
| **Submit** (first attempt, i.e. no prior graded attempt or already retried this session) | `POST api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam/submit` | `{answers:[{questionId, selectedOptionIds:[...]}]}` — one entry per question, `selectedOptionIds: []` for a skipped question | `{percentage, stars, notAnswered, correct, wrong, score, totalDegree, status?}` → opens **Video quiz result** |
| **Submit / Retry** (a graded previous attempt exists, `canRetake:true`, not yet retried this session) | `POST .../exam/retry` immediately followed by `POST .../exam/submit` with the same body shape | retry: no body; submit: `{answers:[...]}` (the answers already selected before tapping Submit) | retry's response reloads the question set (fresh attempt unlocked); submit's response opens the result screen |
| **Retry** button on the result screen | `POST api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam/retry` | — | reloads `{questions:[...]}` with a cleared answer set and returns to the questions (does not auto-submit) |

```json
// POST .../videos/119/exam/submit
{
  "answers": [
    { "questionId": 501, "selectedOptionIds": [2003] },
    { "questionId": 502, "selectedOptionIds": [2010, 2011] },
    { "questionId": 503, "selectedOptionIds": [] }
  ]
}
```

Blanks are graded as wrong; the confirm dialog before submitting says so when `unansweredCount > 0`.

---

## Video quiz result
_Dart file: `lib/feature/student_module/videos/view/video_quiz_result_screen.dart`_
**Reached from:** a successful submit on **Video quiz**.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open (only if not already carrying a result from submit) | `GET api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam/result` | — | `{percentage, stars, notAnswered, correct, wrong, score, totalDegree, status?}` |
| **Review** button | `GET api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam/review` | — | `{videoAssetId, videoExamId, title, finalized, status?, score, percentage, stars, questions:[{questionId, questionText, questionType, degree, awardedDegree?, imageUrl?, options:[{optionId, optionText, isSelected?, isCorrect?}]}]}` → opens **Video quiz review** |
| **Retry** (shown only when `exam.canRetake`) | `POST .../exam/retry` | — | pops back to the quiz questions with a fresh attempt |
| **Done** | *(no call)* | — | returns to the lesson screen |

---

## Video quiz review
_Dart file: `lib/feature/student_module/videos/view/video_quiz_review_screen.dart`_
**Reached from:** the "Review" button on **Video quiz result** (or reached directly if the exam window/attempt was already terminal on load, via the same review fetch).

Purely a render of the `StudentVideoExamReviewApiDto` already fetched — no further calls. Each option row shows `isSelected`/`isCorrect` to mark the student's pick right/wrong and highlight the correct one(s).

---

## Online exams list ("Exams")
_Dart file: `lib/feature/student_module/exam/view/teacher_exams_list_screen.dart`_ (list state in `manager/student_online_exam_list_cubit/`)
**Reached from:** the "Exams" tile in the resource-stats row on the linked-teacher home screen (`AppRoute.goToTeacherExamsList`).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/online-exams/student/teachers/{teacherId}` | — | either a bare array, or `{upcoming:[...], past:[...]}`, or a nested paginated page — the parser (`parseStudentOnlineExamListPayload`) accepts all three shapes. Each item: `{examId, examName, subject, examDate, examTime, duration, questionsCount, examDegree, studentDegree?, studentStatus, startDateTime?}` |
| Tap **Take Exam** / **Show Questions** on a card | *(navigation only)* | — | opens **Online exam session** with `onlineExamId`; on return, if the session mutated anything, the list reloads |

Card badge/action derivation (client-side, from `studentStatus` + date/time + `duration`):
- `duration` is a **.NET `TimeSpan` string** (e.g. `"01:00:00"`), parsed via `parseApiDurationMinutes` — NOT a plain int; a bare `int.tryParse` silently reads it as 0.
- `studentStatus` containing "block" → **Blocked** badge. Containing "completed"/"submitted"/"finalized"/"passed"/"failed" → **Completed** badge (score shown, action = Show Questions). Containing "miss"/"absent", or the exam window has ended (`examDate+examTime+duration` versus now, or backend `isPast`) with no finished attempt → **Missed** badge (action = Show Questions, no score). Otherwise → **Upcoming** badge with a live countdown (action = Take Exam).
- `startDateTime` is the instant form of `examDate`+`examTime` when the server sends it; the list endpoint does not send it today, so the countdown falls back to combining the Cairo wall-clock `examDate`/`examTime` pair, which the client resolves against its own clock via a timezone-aware helper.

---

## Online exam session (instructions → questions → submit)
_Dart file: `lib/feature/student_module/exam/view/student_online_exam_session_screen.dart`_ (state in `manager/student_online_exam_bloc/`)
**Reached from:** tapping a card on the **Online exams list**.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open | `GET api/online-exams/student/teachers/{teacherId}/{onlineExamId}/questions` | — | `{examId, examName, description, instructions, startDateTime, endDateTime, examDegree, studentStatus, blockOnViolation, maxViolations, violationCount, questions:[{id, text, questionType, degree, sortOrder, imageUrl?, options:[{id, optionText}]}]}` (questions sorted by `sortOrder`) |
| — a **404** on this GET | *(same endpoint, error path)* | — | mapped to `NotFoundFailure` → renders "Exam already taken" (attempt unavailable), not a generic error toast |
| Instructions gate → **Start Exam** | *(no call)* | — | reveals question 1; starts the local countdown if `endDateTime` is present |
| Selecting an option | *(local only)* | — | — |
| **Previous** / **Next** | `POST api/online-exams/student/teachers/{teacherId}/{onlineExamId}/answers` (fires for the question being LEFT, i.e. incremental autosave, not on every tap) | `{questionId, selectedOptionIds:[...]}` | success only — a save failure while the attempt is still open shows an error toast; a save failure after the attempt is already blocked/completed is swallowed |
| **Review answers** (last question) | same **answers** POST for the question being left, then opens the pre-submit summary | `{questionId, selectedOptionIds:[...]}` | — |
| Pre-submit summary → tap a question to edit | *(no call)* | — | jumps back into the questions at that index |
| **Submit** | `POST api/online-exams/student/teachers/{teacherId}/{onlineExamId}/submit` | `{answers:[{questionId, selectedOptionIds:[...]}]}` (full answer set, not just the last-edited one) | `{percentage, stars, notAnswered, correct, wrong, status?}` → opens **Online exam result** |
| Countdown timer expires | same **submit** call, fired automatically (no confirm dialog) | same shape | — |
| App backgrounded/inactive/detached OR the student explicitly confirms "Leave" on a proctored exam mid-attempt | `POST api/online-exams/student/teachers/{teacherId}/{onlineExamId}/violation` — **only when `exam.blockOnViolation` is true**; a non-proctored exam's backgrounding is a no-op | — (no body) | `{violationCount, maxViolations, isBlocked}`. Under tolerance: a toast warns "left the exam (N of max)" on resume. At/over tolerance: `isBlocked:true` → attempt is locked, and the client immediately re-fetches the result (see below) and opens it in the blocked state |
| Back button before the attempt starts, or once the attempt is read-only (terminal/window-ended) | *(no call — free navigation)* | — | — |

Session gating that is entirely **client-derived** from the questions-GET fields (no extra endpoint calls):
- Before `startDateTime` → "Exam hasn't started yet" notice with a Go back button (server also rejects early answers with a "window not open" business error as a second line of defense).
- After `endDateTime`, or `studentStatus` already terminal → read-only; the questions render but Previous/Next/answer controls are hidden, and the screen immediately tries to load a result (see next row).
- On load, if the attempt is already terminal/window-ended: `GET .../result`; if that 404s/empty, falls back to `GET .../answers` (same endpoint the incremental-save POST uses, called as GET here) to reconstruct a result from the graded review — for legacy/window-ended attempts that have graded answers but no result row. If both come back empty → "No answers to review" notice.

```json
// POST .../14/91/answers  (autosave on Next/Previous/Review)
{ "questionId": 701, "selectedOptionIds": [3001] }

// POST .../14/91/submit
{
  "answers": [
    { "questionId": 701, "selectedOptionIds": [3001] },
    { "questionId": 702, "selectedOptionIds": [] }
  ]
}

// POST .../14/91/violation → 200 response.data
{ "violationCount": 1, "maxViolations": 2, "isBlocked": false }
```

`POST api/online-exams/student/teachers/{teacherId}/{onlineExamId}/block` is defined end-to-end in the data layer (`StudentOnlineExamRemoteDataSource.block`, `StudentOnlineExamRepository.block`) but **no bloc/cubit ever calls it** — the actual block transition is inferred entirely from `isBlocked` on the `violation` response.

---

## Online exam result
_Dart file: `lib/feature/student_module/exam/view/student_online_exam_result_screen.dart`_
**Reached from:** a successful submit, an auto-submit on timer expiry, or an anti-cheat block, on **Online exam session**.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open (only if no result already carried over) | `GET api/online-exams/student/teachers/{teacherId}/{onlineExamId}/result` | — | `{percentage, stars, notAnswered, correct, wrong, status?}` |
| **Review** button (hidden when the status reads as "missed") | `GET api/online-exams/student/teachers/{teacherId}/{onlineExamId}/answers` | — | `{examId, examName, finalized, score, percentage, reportStatus?, questions:[{questionId, questionText, questionType, degree, awardedDegree?, imageUrl?, options:[{id, optionText, isSelected?, isCorrect?}]}]}` → opens **Online exam review** |
| **Done** | *(no call)* | — | leaves the module, signalling back whether anything mutated this session |

A blocked attempt (`state.isBlocked`) shows a red "Exam blocked — you were removed because you left the screen or exited" banner above the score; a missed one (`studentStatus` containing "miss"/"absent") shows an amber "You missed this exam" banner and hides Review.

---

## Online exam review
_Dart file: `lib/feature/student_module/exam/view/student_online_exam_review_screen.dart`_
**Reached from:** the "Review" button on **Online exam result** (same `GET .../answers` payload the result screen already fetched, re-used if present).

Purely a render of `StudentOnlineExamReviewApiDto` — no further calls. Per-question outcome (correct/wrong/not answered) is computed client-side by comparing `isSelected`/`isCorrect` across `options[]`.

---

## Offline exams
_Dart file: `lib/feature/student_module/offline_exams/view/offline_exams_view.dart`_
**Reached from:** the "Offline Exams" section on the linked-teacher home screen ("View all" next to the month chips), or directly from the linking/link-tab module list.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/assignmentobligations/student/teachers/{teacherId}/exams` | query `page` (default 1), `pageSize` (default 10, clamped to `[1,10]`) | paginated `items[]`: `{examId, examName, description, date, status, score?, scorePercentage?, maxGrade?, subject?, rank?, groupSize?}` |
| Infinite scroll (within 120px of the bottom) | same endpoint, `page` + 1 | same query shape | items appended |

This list is **read-only** — cards are not tappable, there is no detail/drill-in screen and no further endpoint calls.

Wire status values and how the app derives the "Missed" chip: `status` ∈ `pending`/`done`/`notDone`/`attended`/`attendedWithGrade`/`didNotAttend`/`doneWithoutGrade`/`doneWithGrade` (case-insensitive; anything else parses to `unknown`). The card's **Missed** state is `status == didNotAttend || status == notDone` — everything else with a `scorePercentage` shows a green percentage chip and progress bar; everything else without one shows a neutral "—" chip. `date` is a calendar day (`parseApiCalendarDate`), never localized to an instant.

```json
// one item of GET .../exams → data.items[]
{
  "examId": 344,
  "examName": "Mid-term Algebra Test",
  "description": "Chapters 1–4",
  "date": "2026-09-01",
  "status": "attendedWithGrade",
  "score": 27,
  "scorePercentage": 90,
  "maxGrade": 30,
  "subject": "Mathematics",
  "rank": 3,
  "groupSize": 28
}
```

---

## Homework
_Dart files: `lib/feature/student_module/homework/view/{homework_view,homework_question_view,homework_review_view}.dart`_
**Reached from:** a "Homework" tile that exists on two older module-list widgets (`link_tab_view_body.dart`, `student_linked_teacher_modules_section.dart`, gated behind `teacher.visibilityHomework`) but is **commented out** on the current teacher-home screen (`teacher_details_view_body.dart`, `homeworkVisible: false`).

This entire module is a **UI-only placeholder** — it calls no API at all. `HomeworkView` renders two sections ("Upcoming Homework"/"Past Homework") built from hardcoded `HomeworkItemCard` data in the widget tree itself, and `HomeworkQuestionCubit` (the "Solve" flow) ships two hardcoded questions ("What is the capital of France?" among them) with answers kept only in local cubit state — nothing is sent or fetched. Do not treat any of it as a live contract; there is nothing here for a backend developer to reconcile against.

---

### Endpoint coverage

```
GET  api/videos/student/teachers/{teacherId}/units
GET  api/videos/student/teachers/{teacherId}/units/{unitId}/videos
POST api/videos/student/teachers/{teacherId}/{videoAssetId}/start
POST api/videos/student/teachers/{teacherId}/{videoAssetId}/stop
GET  api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam
POST api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam/submit
POST api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam/retry
GET  api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam/result
GET  api/videos/student/teachers/{teacherId}/videos/{videoAssetId}/exam/review

GET  api/online-exams/student/teachers/{teacherId}
GET  api/online-exams/student/teachers/{teacherId}/{onlineExamId}/questions
POST api/online-exams/student/teachers/{teacherId}/{onlineExamId}/answers
GET  api/online-exams/student/teachers/{teacherId}/{onlineExamId}/answers
POST api/online-exams/student/teachers/{teacherId}/{onlineExamId}/submit
GET  api/online-exams/student/teachers/{teacherId}/{onlineExamId}/result
POST api/online-exams/student/teachers/{teacherId}/{onlineExamId}/violation

GET  api/assignmentobligations/student/teachers/{teacherId}/exams
```

**Defined in `web_constant.dart` / wired in a data source, but never called from any bloc/cubit:**
- `POST api/online-exams/student/teachers/{teacherId}/{onlineExamId}/block` (`webPathStudentOnlineExamBlock`) — `StudentOnlineExamRemoteDataSource.block` / `StudentOnlineExamRepository.block` exist but have no call site; the app infers blocking from the `violation` response's `isBlocked` instead.
- `String webLessonExamQuestions(String lessonId)` → `student/videos/lessons/{lessonId}/quiz` — used only by `ExamRepositoryImpl` (legacy lesson-quiz path: `ExamCubit`, `LessonExamInstructionsScreen`, `ExamSessionScreen`, `ExamReviewScreen`). DI registers `ExamRepositoryMock` (hardcoded questions), not `ExamRepositoryImpl`, and none of those four screens/routes has a live navigation call site anywhere in the app (`goToLessonExamInstructions`/`goToExamSession`/`goToExamReview` are defined in `app_route.dart` but never invoked). This endpoint is effectively dead code on the client — flag it as unused rather than assume the app depends on it.

No endpoints were found being called that are absent from `web_constant.dart`.
