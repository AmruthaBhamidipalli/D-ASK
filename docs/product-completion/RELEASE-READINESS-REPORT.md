# D-ASK release readiness report (updated 2026-11-15)

**Current release candidate:** `dist/desktop/D-ASK-20261115T023936` (218 integrity-manifest files, manifest SHA-256 `09e94423…33b1`), installed at `%LOCALAPPDATA%\Programs\D-ASK` (the Chrome relay registration is kept at the same path). Sections 1–7 describe the previous round (RC `D-ASK-20261114T170903`); section 6 is updated for round 3.

Statuses: **PASS** executed and verified · **PARTIAL** implemented, only partly verified · **BLOCKED** needs you or an external dependency · **NOT IMPLEMENTED** missing.

## 0. Round 3 (2026-11-15): Core Console, your Chrome, product-vision gaps

| Item | Status | What was done | Evidence |
|---|---|---|---|
| Core Console not reachable in the installed app | **PASS** (fixed) | Root cause: the HUD started in ambient (click-through) mode, where CSS hides the command input, and the only way in was a hotkey. Now it starts interactive with the input focused. In ambient mode an OPEN CONSOLE button appears, and the native host checks that a real click happened (a page cannot open the console by itself). Launching D-ASK again brings the running console back. Typing in the input no longer triggers orb shortcuts. | `scripts/hud/console-access-live.ps1`: dev 7/7; previous installed build 7/7 (`results/console-access/installed-1`). New installed build: the 5 UI checks pass. The 2 model-answered commands FAILED only because the local model's memory guard refused (2.8 GB free; the IDE and Chrome were using about 5 GB). The console still delivered the command and showed the refusal honestly (`installed-2`, `installed-3`). Requests that don't need the model COMPLETED in the new installed console (`installed-new-tools`). |
| Your everyday Chrome profile | **PASS** (the test harness approved the cards, not you) | Read example.com, extracted its text, clicked the harmless "Learn more" link, opened/switched/closed tabs, did a harmless fill. Also checked: sensitive-action approval, Emergency Stop then re-authorization, recovery after the relay was killed, the cross-session check, 0 listening sockets, and revoke. | `results/console-access/ChromeLiveTests-everyday-profile-2.log` |
| Display resolution change | **PASS** | `system.set_display_mode` offers only modes the display reports and runs `CDS_TEST` first. After approval it applies the mode for the session and reads it back. A second "keep" approval is needed within 15 s, otherwise it rolls back automatically: decline, timeout and Emergency Stop all roll back and read back. | `results/console-access/DisplayModeLiveTests.log` (real display, 1920×1080 ↔ 1680×1050, restored) |
| Scanned PDF OCR (offline) | **PASS** | `dask-ocr --pdf` renders pages with Windows.Data.Pdf and OCRs them with Windows.Media.Ocr (on-device, no cloud). During indexing, pages without a text layer are OCR'd. Their passages keep page numbers and are marked "text recognised by OCR". | `tests/agent-runtime/knowledge-ocr.test.ts` 10/10 (image-only PDF fixture); knowledge 121/121 |
| Research mode | **PASS** (automated) / **PARTIAL** (output quality not reviewed by a person) | Offline: literature review over local documents and LaTeX report/proposal export with BibTeX (cite keys and environments checked). Datasets (CSV/TSV ≤ 50 MB inside allowed folders) get descriptive statistics, correlation, a linear fit, a Welch t-test, SVG histogram/scatter charts and a reproducible report (re-computed and compared). Optional online search (opt-in `DASK_RESEARCH_ONLINE=1`): arXiv and Crossref only, HTTPS, host allowlist, no redirects. Papers are stored with metadata and DOI. | `tests/agent-runtime/research-extras.test.ts` 30/30, including a live arXiv/Crossref search (10 papers) |
| Android / ADB | **PARTIAL**; real-device test **BLOCKED** | New daemon `AndroidService`, off unless `AgentHost:Android` is set (it is on in the product profile). It covers discovery (`adb devices -l`), device info, Wi-Fi connect (private IPv4 only), and push/pull between allowed folders and `/sdcard` only. Transfers are SHA-256 verified on both sides and never overwrite local files. Commands: six fixed ones plus opening an https link. Every call uses a fixed argument list (no shell strings), and each connect, copy and command is approved in the HUD. An "unauthorized" phone is explained, never bypassed. Reused from the legacy `tools/device/deviceService.ts`: adb discovery paths and the read commands. Not reused: PIN-unlock typing and arbitrary shell. Also added: agent grammar and tools, the host allowlist entries and HUD card labels. | `AndroidTests.cs`: 7 fake-adb tests, plus 1 real-device test that does nothing unless `DASK_LIVE_ANDROID=1`. `tests/agent-runtime/android.test.ts` 29/29. The installed build honestly reports "adb is not installed". **Not verified on a real phone**: adb is not installed here and no device is connected. |
| Windows code signing | **BLOCKED** (pipeline ready, no certificate) | `scripts/package/Sign-D-ASK.ps1` has inventory, sign and verify modes. It signs D-ASK's own 9 files (hud-native.exe, dask-ocr.exe, DAsk.Daemon.exe/.dll, DAsk.Protocol.dll, dask-chrome-host.exe, and the Install/Uninstall/register .ps1 scripts) with signtool, SHA-256 and an RFC 3161 timestamp. The certificate can come from the store (thumbprint or hardware token), a PFX (password from the environment) or Azure Trusted Signing. Self-signed, privately rooted, expired and non-code-signing certificates are refused. `build-package.mjs --sign-*` runs it before the integrity manifest is computed, and `verify-package.ps1` reports the status. | Policy test `tests/package/sign-policy.ps1` 7/7: self-signed, private-root and no-EKU certificates are refused, nothing is changed, and Verify reports NOT SIGNED. Installed app: 9 D-ASK files, **0 signed**; 345 third-party files, of which 222 are signed by their publishers and 123 are unsigned (llama.cpp/whisper.cpp builds, some NuGet libraries). See `results/code-signing/`. **D-ASK is not signed.** |

### 0a. Regression on the round-3 code (`results/session3-regression/`)

| Suite | Result |
|---|---|
| TypeScript suites (41, run alongside the package build) | 2,276 checks passed; 2 failures under that load (`local-inference` U8, `offline-integration` N5). Both suites pass when run alone: 103/103, and 15/15 + 9/9 (`ts-rerun-alone.log`). U8 is intermittent under heavy load. |
| Daemon suite | **678 passed, 0 failed, 7 skipped** (685; the 7 skips are the opt-in live tests that change real machine state). The 678 includes `RealDevice_ReadOnlyAndRoundTrip`, which does nothing without `DASK_LIVE_ANDROID=1`: it is NOT a real-phone result (`daemon-suite-full.log`) |
| Rust HUD + dask-ocr (`cargo test --release`) | 71 passed, 0 failed, 4 ignored |
| Host tool allowlist (`offer-allowlist`) | 80/80 (includes the 6 Android tools) |

**What only you can do now**
1. Android: install Android SDK Platform-Tools (this means accepting Google's licence). On the phone, enable Developer options → USB debugging, connect it, accept the "Allow USB debugging?" prompt, then restart D-ASK. To run the real-device test: `set DASK_LIVE_ANDROID=1` then `dotnet test --filter RealDevice_ReadOnlyAndRoundTrip`. It pushes one small test file to `/sdcard/Download` and pulls it back; it never deletes anything on the phone.
2. Signing: obtain a publicly trusted code-signing certificate. That is an OV or EV certificate from a CA in Microsoft's Trusted Root Program (usually on a hardware token), or an Azure Trusted Signing account. Either way you or your organisation must pass identity validation. Then run `node scripts/package/build-package.mjs --out dist\desktop\D-ASK-signed --sign-thumbprint <sha1>`, followed by `Sign-D-ASK.ps1 -PackageRoot <pkg> -Mode Verify`.
3. Human-in-the-loop HUD session (unchanged): `docs/product-completion/HITL-ACCEPTANCE.md`.
4. To see the installed console's model-answered commands pass, re-run `scripts/hud/console-access-live.ps1 -Root %LOCALAPPDATA%\Programs\D-ASK -Config Release` with at least about 3.5 GB of RAM free.

## Round 2 report (2026-11-14), RC `D-ASK-20261114T170903`

Round 2 RC: 214 integrity-manifest files, manifest SHA-256 `d0c1eab6…ff16`, 4.0 GB (almost all of it local model weights). In round 2 the packaged application installed, started, worked offline, verified itself and uninstalled cleanly. D-ASK is **not** a complete implementation of the whole product vision (section 6).

## 1. Defects found and fixed in this session

| # | Defect | Effect before the fix | Fix | Evidence |
|---|---|---|---|---|
| 1 | Every agent host shared ONE llama-server PID file per model; a second host's orphan cleanup killed the first host's **live** model server. | `Phase7B1bLiveTests` failed under load (ECONNRESET / ECONNREFUSED); two D-ASK hosts (e.g. installed + dev) would kill each other's model. | One PID file per owning host; a server is killed only when its owning host is gone (`supervisor.ts`). | Reproduced 2/2 (`results/regression-reliability/load-run-1`, `-2`); fixed runs 4–6 with two model servers alive at once; tests U0a–U0d (real processes). |
| 2 | A reset model connection failed the task at once. | One transient socket reset = failed request. | One retry with a freshly acquired endpoint (never on cancel, never twice) (`llamaServerModel.ts`). | M5b–M5e. |
| 3 | `contentWords()` stripped numbers before paths, so a digit in a folder name left stray "words". | ~18 % of verified file copies were reported "unverified" (`file-organize` C5/C7 intermittent). | Paths are removed first, as whole tokens (`grounding.ts`). | Repro 7/40 → 0/40; tests W5/W6. |
| 4 | `files.move` and `office.create_*` (added in an earlier round) were missing from the host's tool allowlist. | With the product profile (file organisation on), the **packaged agent host refused to start**: every request "could not be started". | Added; new test reads every tool name from the daemon source and checks the host accepts it. | `results/release-readiness/install/diag-stack-profile/daemon.log`; `offer-allowlist.test.ts` 73/73. |
| 5 | The planner wrote EVERY tool's argument constraints into the plan prompt. | With all 79 tools offered the prompt was ~6,600 tokens > the 4,096-token local context: every model-planned request failed (HTTP 400). | Only the offered tools' constraints go into the prompt; validation still uses all of them (`planner.ts`). | `results/release-readiness/verify-package-final-console.log` (17/32), then 32/32; `prompt-budget.test.ts` 10/10. |
| 6 | Numeric/boolean arguments could not be pinned to the request, and unrequested arguments were not forbidden. | The local model added `pid` to "Show running processes" (refused by the daemon → FAILED); a model could also change a volume, delay or scroll amount (the approval card would still show it). | Argument domains accept exact numbers/booleans; every new tool pins every argument and forbids the rest. | `verify-package-2` (31/32), tests P1–P3, E4. |
| 7 | Approval parameters are capped at 2,000 characters, but code-edit diffs (3,500) and clipboard text (4,000) were allowed. | Such requests would fail at approval time with an unclear error. | Refused up front with a clear message; clipboard text ≤ 1,900. | DevEditTests `change_too_large`. |
| 8 | An authorized Chrome session was usable by tasks of ANY connected HUD session. | Another client's task could reuse your Chrome authorization. | The session belongs to the approving HUD session; another session must authorize again. | ChromeLiveTests (cross-session step). |
| 9 | The MediaPipe hand model was downloaded from Google at runtime. | Gestures silently did not work offline. | Model shipped in `public/mediapipe/` (Apache-2.0, SHA-256 recorded). | `public/mediapipe/README.txt`. |

## 2. Build results (final code)

| Component | Result |
|---|---|
| .NET daemon (Debug + self-contained Release publish) | PASS (0 errors) |
| Chrome relay `dask-chrome-host.exe` (self-contained, single file, trimmed, 12 MB) | PASS |
| Agent host (`tsc -p tsconfig.agent-host.json`) | PASS |
| Frontend (`next build`) | PASS |
| Rust HUD + `dask-ocr` (`cargo build --release`) | PASS |
| Package build (`scripts/package/build-package.mjs`) | PASS — `results/packaging/20261114T170903/build.log` |

## 3. Automated tests

| Suite | Result | Evidence |
|---|---|---|
| Daemon suite (676 tests) | **670 passed, 0 failed, 6 skipped** in each of 3 consecutive full-load runs (daemon suite + 36 TS suites + frontend build at once) after fixes 1–2; final run on the final code (`load-run-final`): 670 passed, 0 failed, 6 skipped | `results/regression-reliability/load-run-4..6`, `load-run-final` |
| The 6 skips | Opt-in live tests that change real machine state, gated by env vars (`DASK_LIVE_INPUT`, `DASK_LIVE_SYSTEM`, `DASK_LIVE_CHROME`, Office/app-launch live). Each was run explicitly (section 4). | — |
| TypeScript suites | 38 suites, 2,192 checks, 0 failed, under load on the final code (new this session: `prompt-budget`, `offer-allowlist`; extended: `local-inference` 103, `grounding` 82, `approval-lifecycle` 97, `system-admin` 43, `chrome` 23) | `load-run-final/ts-suites.log` |
| HUD approval cards | 97/97, incl. 15 new checks: every new action type has an exact summary; cards with inconsistent parameters are never offered | `tests/hud-agent/approval-lifecycle.test.ts` |
| Approval security (existing, in the daemon suite) | PASS: owner-session binding, foreign sessions cannot decide, one-time consumption/replay rejected, expiry, cancellation, Emergency Stop invalidates pending and granted approvals, reconnect inherits nothing | `ApprovalSessionBindingTests`, `…WireTests` (H39_*) |

## 4. Real-machine acceptance (the test plays the HUD unless stated)

| Area | Status | Evidence |
|---|---|---|
| Installed package: launcher, packaged Node + llama-server, verified + answered questions, memory incl. restart, knowledge + research, new Windows-admin / Chrome-status / broker-pipe / no-TCP checks, 5 tamper refusals + recoveries | **PASS 32/32** | `results/release-readiness/verify-package-rc-console.log` |
| Wi-Fi OFF: installed package 32/32 + offline cross-feature 8/8; 0 non-loopback connections (208 processes); radio restored | **PASS** | `results/offline-final/20261114T172051/SUMMARY.txt` |
| Install → verify → uninstall (relay registered for the user only; uninstall removes it, keeps your data) | **PASS** | `results/release-readiness/install/*.log` |
| Real Chrome 157 with the **installed** relay + extension (throwaway profile): navigate, read, link click, tabs, harmless fill, password/secret refusal, sensitive-click approval (declined → nothing; approved → done), rogue pipe client rejected, 0 listening sockets, offline + web-search refusal, Emergency Stop revocation in daemon AND extension, re-authorization, second-HUD-session authorization, relay killed → reconnect → new authorization, cleanup | **PASS** | `results/release-readiness/install/ChromeLiveTests-rc.log` |
| Desktop input (right/double click, scroll, drag-drop, selection, clipboard, Emergency Stop mid-scroll) | **PASS** (earlier today, unchanged code) | `results/universal-control-2/` |
| Windows administration (processes, services, Wi-Fi/Bluetooth, volume, brightness, startup, power scheduled + cancelled, E-Stop abort) | **PASS** (earlier today) | `results/universal-control-2/SystemAdminLiveTests.log` |
| Code edit + real tests | **PASS** | `results/release-readiness/dev-live/DevEditTests.log` |
| Voice (Whisper) | **PARTIAL** — earlier offline voice acceptance 7/7 preserved (`results/offline-voice*`); not re-run (needs a person speaking) |
| Your everyday Chrome profile | **BLOCKED** — Chrome only lets you load an unpacked extension yourself (section 7) |
| Human-in-the-loop HUD acceptance | **BLOCKED** — needs you; prepared (`docs/product-completion/HITL-ACCEPTANCE.md`, `scripts/hud/hitl-prepare.ps1`) |

## 5. Security review of the new subsystems

PASS (code + tests): approvals are bound to the exact canonical action and owner session and consumed once; target window/control identity re-checked before every input event; consequential actions each approved separately (sensitive Chrome clicks, power, process end, services, radios, volume, brightness, startup, clipboard, code edits); Emergency Stop: input, power (aborts `shutdown.exe`), Chrome (daemon + extension) — tested; clipboard/secret-looking text withheld and never logged; Chrome access only via the exact relay exe started by Chrome for the D-ASK extension origin over a user-only pipe that rejects remote clients — no TCP port anywhere (tested); UAC respected (no elevation; admin-only services reported as refused); Windows/security/D-ASK processes protected; file and code edits confined to configured roots; audit chain verified in every live test; failures report their real outcome.
Residual risks: no code signing yet (round 3: the pipeline is ready; a certificate is needed) (an attacker who can rewrite the install folder can replace the relay); same-user malware can read what the user can; the extension is loaded unpacked (Developer mode).

## 6. Product vision — honest status

| Area | Status | Notes |
|---|---|---|
| AI brain: intent, grounded planning, tool selection, verification, follow-up rounds | PASS | Local 0.5B model; quality bounded by model size. Multi-agent "orchestrator" (`agent/orchestrator.ts`) is the legacy web path, not the native runtime: PARTIAL. |
| Voice: local Whisper, wake, TTS, voice → tools | PARTIAL | Earlier acceptance 7/7; not re-run with a person today. |
| Vision: desktop observation, UI Automation, OCR, grounding | PASS | Gestures: offline model now shipped; not tested with a camera: PARTIAL. |
| HUD (Three.js orb, states, approval cards, E-Stop, panels) | PASS (automated) / BLOCKED (human session) | |
| Windows control: apps, input, clipboard, windows, settings, processes, services, radios, audio, display, startup, power | PASS | Display resolution change with automatic rollback: PASS (round 3). |
| Files: search, read, summarize, create, copy, move, rename, organize, undo, verification | PASS | |
| Browsers: private D-ASK browser; your Chrome | PASS (everyday profile verified in round 3; the harness approved the cards) | Web search only when enabled and online. |
| Memory + RAG (PDF, DOCX, TXT, MD, code; hybrid search; citations; incremental) | PASS | Scanned PDFs: offline OCR with page references, PASS (round 3). |
| Research mode (local literature review, cited, versioned; optional named web pages) | PASS (automated) / PARTIAL | Round 3 added optional arXiv/Crossref discovery, LaTeX + BibTeX export, dataset statistics/tests/charts and reproducible reports. Experiment tracking across runs (beyond one dataset report): NOT IMPLEMENTED. |
| Automation: workflows, schedules, history | PASS | Conditional workflows: PARTIAL. |
| Plugins (sandboxed, permissioned) | PASS | |
| Android / ADB | PARTIAL / BLOCKED | Round 3 added a native daemon service and agent tools with approvals and SHA-256 verified transfers. The fake-adb tests pass; no real phone has been tested (no adb or device here). |
| Developer tools: build/test, approved diff edits, post-edit tests | PASS | |
| Packaging: native, installer, uninstaller, integrity manifest, offline | PASS | Code-signing pipeline ready but NOT SIGNED (no trusted certificate): BLOCKED. `app/api/stt` (cloud transcription) exists only in the legacy web server, not the native app. |

## 7. Setup and launch (for you)

1. Install: `powershell -ExecutionPolicy Bypass -File D:\D-ASK\D-ASK\dist\desktop\D-ASK-20261114T170903\Install-D-ASK.ps1` (installs to `%LOCALAPPDATA%\Programs\D-ASK`, adds a Start Menu shortcut, registers the Chrome relay for your user only).
2. Chrome (once, in your everyday profile): `chrome://extensions` → **Developer mode** on → **Load unpacked** → `%LOCALAPPDATA%\Programs\D-ASK\extensions\dask-chrome`.
3. Start D-ASK from the Start Menu. `Start-D-ASK-Safe.cmd` starts with every optional feature off.
4. Optional features are in `features.default.cmd` (copy to `%LOCALAPPDATA%\D-ASK\features.cmd` to change them): code edits (`DevEditRoots`), services D-ASK may start/stop (`SystemAdminServices`), web search (`WebSearch`).
5. Human-in-the-loop session (~15 min): `docs/product-completion/HITL-ACCEPTANCE.md`.
6. Uninstall: `Uninstall-D-ASK.ps1` in the install folder (keeps `%LOCALAPPDATA%\D-ASK` unless `-RemoveData`).
