# Manual updater — 1.2.0-preview.4

This document describes the manual updater and records its validation separately
from preview 3's protected-UAC and game-switching results. Release build,
focused automated tests, rendered UI inspection, a genuine GitHub download and
four isolated real-process update scenarios passed on October 18, 2026.
An update of the user's running MWB installation and physical two-PC acceptance
of preview 4 remain untested.

## User flow and scope

About displays the full product version, with any preview suffix, and the current
fork maintainer, **El3ctro99**. The credits retain **aeae1** as the upstream
portable maintainer and **Truong Do (Đỗ Đức Trường)**, Microsoft Garage,
PowerToys and the original contributors.

**About → Check for updates** opens the update window and checks the fixed
`El3ctro99/MouseWithoutBorders-Portable` GitHub releases endpoint. The window
displays the installed version and source. When a newer eligible release is
available, **Download and install** asks permission to download, replace the
normal application and restart it. Opening About alone, starting Windows or
starting MWB does not initiate an update check. Checks do not install updates
without the separate download/restart confirmation.

Stable users remain on stable releases. A current prerelease can advance to a
newer preview, release candidate or stable release. Selection uses bounded
SemVer 2.0 precedence, including numeric prerelease identifiers: `preview.10`
follows `preview.9`; build metadata does not change precedence. Drafts, invalid
tags and releases whose prerelease flag disagrees with their version are ignored.
Only anchored `mwb-v` tags from this fork are considered. If the newest eligible
release lacks valid required downloads, the check reports a problem instead of
silently choosing an older release or claiming that the app is up to date.

The replacement is limited to an ordinary `MouseWithoutBorders.exe` beneath the
current user's Windows profile on a local fixed drive. The default
`%LOCALAPPDATA%\Programs\Mouse Without Borders` installation qualifies when its
path and permissions pass validation. Network/removable drives, protected or
shared locations, paths containing reparse points such as junctions, and the
protected service installation are refused. A refused in-app update can still be
downloaded through **Open GitHub releases** and installed manually after exiting
MWB. The updater does not request an elevation to bypass its target restrictions.

The optional protected-UAC service has a separate administrator installation and
upgrade path in the full release package. The update window shows its installed
version when readable and explains that this action updates the normal
application only. The updater does not replace the protected service payload,
edit its private profile, change its startup settings or alter firewall rules.

## Download and replacement checks

The required release assets are `MouseWithoutBorders.exe` and
`MouseWithoutBorders.exe.sha256`, with exact names and fixed HTTPS download URLs
under this fork and selected tag. They must have uploaded state and bounded
integer sizes: the application is at most 256 MiB and the checksum at most
4096 bytes. Metadata is bounded to 1 MiB, 100 releases and a limited nesting
depth; duplicate JSON fields and ambiguous asset names are rejected.

The downloader restricts redirects to supported GitHub HTTPS hosts and bounds
redirect count, duration and streamed byte counts. It reads the checksum before
the larger application, requires one valid SHA-256 entry for the exact EXE name,
and compares any supplied GitHub SHA-256 digest. It checks the complete download
length and calculates the EXE's SHA-256 before accepting the payload. The PE
architecture must match the running process, and the embedded product version
must match the selected release version, including the prerelease suffix. Build
metadata after `+` is ignored for the version comparison. These checks rely on
the release and checksum hosted by this GitHub account; the builds remain
unsigned, and the checksum is not an independent publisher signature.

Downloads and apply requests use private staging beneath
`%USERPROFILE%\.MouseWithoutBorders\downloads` and `Updates`. This avoids
assuming that every user's AppData tree has restrictive permissions. Completed,
inactive apply stages have only their known large files and request markers
removed when another update is prepared; a small `result.json` remains for
diagnostics. Cleanup is bounded and leaves active, malformed or unfamiliar
stages untouched. The helper is a mode
of the same EXE, rather than an installed service or a new distributed helper
program. Its request binds the running parent process, start time, session,
executable path, user and old/new hashes. The helper rechecks files and waits for
the verified parent and the same copy's clipboard helper to exit, with a bounded
timeout. It does not kill an uncooperative process or accept an arbitrary target
path from command-line arguments. Cancellation before replacement leaves the
current application in place.

Replacement copies the verified payload to a new sibling file and atomically
replaces the EXE with a complete old EXE retained for recovery. Preferences,
pairing, machine layout, transfer recovery files and startup-registration
settings are not rewritten by the update helper. Normal application exit still
uses its existing preferences-save and transfer pause/journal behavior.

If the new process fails to launch or exits during its first **three seconds**,
the helper attempts to restore and restart the old EXE. A failed recovery retains
the backup and records its location. A process that remains alive for three
seconds can still fail later, fail to establish a peer connection or have a
functional regression. The liveness check does not establish that input,
clipboard, file transfer, UAC control or sleep/wake works, and does not trigger a
later automatic rollback. If an update cannot be applied safely, use the release
downloads for a manual replacement while retaining the adjacent preferences and
recovery files.

## Validation record

| Check | Current evidence |
| --- | --- |
| Release x64 build and repository checks | Passed, including self-contained single-file x64 publish and three-project repository validation |
| Focused release-selection, download and apply-helper tests | 102 passed, zero failed/skipped; includes About and updater UI cases |
| Existing UAC, connection and settings regressions | 65 passed, zero failed/skipped |
| About and updater normal/large-text screenshots | Four PNGs rendered and visually inspected; full version, maintainer, credits and footer fit |
| Genuine GitHub release metadata and asset compatibility | Passed against published preview 3; see the live-download evidence below |
| Full inherited test suite | Not claimed green; earlier inherited failures remain documented in the [service validation](SECURE_DESKTOP_VALIDATION.md) |
| Real EXE replacement/restart | Passed with isolated Windows fixture EXEs invoking the exact compiled updater implementation; user's running MWB not replaced |
| Real rollback, cancellation and timeout | Four actual parent/helper process scenarios passed; opaque adjacent test preferences were byte-identical in every case |
| Physical two-PC behavior after an update | Pending; previous preview 3 acceptance is not proof for this update |

The passing tests cover stable/prerelease ordering, malformed metadata and
versions, URL/size/hash validation, download cancellation and partial failures,
parent/target validation, cancellation/timeout, EXE-only replacement and recovery.
UI cases render the actual About window and update-window constructor content
without invoking a network check. They inspect full version text, maintainer and
upstream credits, initial install-button state, source/service notes, text bounds
and footer intersections at normal and larger text sizes. Reports are
`artifacts/preview4-tests/preview4-functional-validated.trx` and
`preview4-regressions-validated.trx`; rendered PNGs are under
`artifacts/preview4-ui-qa/mwb-ui-previews`.

### Published-release download smoke

On October 18, 2026, the GitHub connector retrieved this fork's real releases
JSON. An isolated .NET 10 host invoked `ManualUpdates.SelectCandidate` from a
snapshot of the freshly compiled App DLL: installed `1.2.0-preview.2` selected
`mwb-v1.2.0-preview.3`; installed stable `1.1.1` did not offer that prerelease, and
installed `1.2.0-preview.4` did not offer a downgrade.

The same host invoked the exact compiled `UpdateDownload` implementation with
its production HTTP redirect/cookie/credential settings and a workspace-only
private staging location. It fetched both actual release assets, following one
HTTPS redirect from `github.com` to `release-assets.githubusercontent.com` for
each. The verified EXE was **88,135,371 bytes**, embedded product version
`1.2.0-preview.3`, with SHA-256
`A096A2DDF61E5F0079B968B24162BE0F6C4B68DF1E2F794FF30381F4DE98955C`.
The checksum file, GitHub digests, declared lengths and PE/version checks all
passed. Selection and the genuine download completed without executing or
installing the downloaded payload, modifying a user installation, or touching
preferences or the optional service.

The report is `artifacts/updater-live-smoke/live-smoke-result.json`; its recorded
App DLL snapshot hash is
`0F76905C3FAE9C14E6278979B76DF26D8826661D099EB8C646B2E72924C78061`.
This establishes compatibility with an actual published release and its CDN
redirects. It does not establish a completed in-app update or a real rollback,
and later source edits or rebuilt assembly hashes require separate validation.

### Real parent/helper process fixtures

The same tested App DLL snapshot was loaded by an isolated self-contained Windows
fixture, without entering MWB's normal startup, input hooks or networking. The
fixture used real Windows parent PID/start/session/user/image binding, READY and
commit markers, process exit waits, `File.Replace` and three-second startup
checks. All four cases passed:

- successful replacement launched the new fixture and retained its new hash;
- an immediately exiting new fixture caused restoration and restart of the old
  EXE, whose final hash matched the original;
- cancellation followed by later parent exit caused no replacement or restart;
- a parent that remained alive beyond the 30-second timeout was left untouched.

Every case retained an adjacent deliberately invalid-JSON preferences sentinel
byte for byte. This proves the helper did not parse or rewrite that file; it
does not establish MWB's live peer/input behavior after a user update. The report
is `artifacts/updater-apply-smoke/final-userprofile/summary.json`.

The actual host's AppData ancestry grants modify access to an additional
capability identity. Strict validation correctly rejected that location; no ACL
was changed and no identity was whitelisted. User-profile staging passed, as
did the workspace's portable fixture targets. The current AppData installation
therefore requires manual EXE replacement on this host. Its failure mode and the
Open GitHub releases fallback are intentional.

The produced local single-file build is version `1.2.0-preview.4`, 88,162,860
bytes, SHA-256
`BF21CECE180E7B08FB2F478BD105766C0FAC6325B0079F6E5218BB8AD556A09A`.
It has not been installed into the user's running copy or published as a release
at the time of this validation record. Single-file publishing rebuilt the App
assembly with the publish/debug settings; the EXE and tested DLL have different
hashes and package forms. The tested source is the same, but the published EXE
has not completed a live user-installation update. A separate run of that exact
single-file EXE with an incomplete updater invocation exited with code 1 and
created no preferences file; report: `artifacts/preview4-entry-smoke/result.json`.
Existing build warnings concern the
inherited transfer UI/nullable tests and unavailable offline NuGet audit data.

Before calling an update accepted on a real pair, record the source and target
versions, verify the adjacent preferences and recovery files were retained,
confirm a restarted tray process and peer connection, and exercise mouse,
keyboard, clipboard and file transfer. Check the optional service separately if
its package was upgraded. Never copy actual pairing keys, private service
profiles or user transfer journals into published validation artifacts.
