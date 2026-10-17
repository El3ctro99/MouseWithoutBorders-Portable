# Version 1.1.1 — stable

Version 1.1.1 promotes **1.1.1-rc.3** to the stable release and `main`. Application code, dependencies, preferences and recovery formats are unchanged from RC3; this promotion updates version metadata and documentation.

## What's improved since 1.1.0

- **Smoother computer matrix:** buffered monitor/status painting, immediate drag swaps, stable one/two-row arrangement, keyboard movement, and cancellation with Escape, right-click, lost capture or release outside the matrix. Native name editors and checkboxes remain.
- **Better scaling:** tile dimensions follow available matrix width, keeping ordinary status labels readable at enlarged display sizes.
- **Recovery-storage failures:** transfers pause visibly if their recovery information cannot be saved. Metadata saving can recover automatically; copying waits for explicit Resume/Retry.
- **Safer recovery:** missing/damaged journals recover from backup, completed records compact into bounded receipts, and a lost final acknowledgement does not authorize a duplicate copy.
- **File-transfer edge cases:** temporarily unreadable regular files can be rescanned on Retry; replaced receiving folders are refused; paused backlogs no longer reserve disk space for all pending files.
- **Less allocation and checkpoint overhead:** receive buffers are reused and redundant saves are removed, retaining encryption, checksums and durable completion checkpoints.

## Upgrading

Use **1.1.1 on every connected PC**.

1. Finish or cancel active transfers before updating.
2. For portable use, exit MWB and replace the EXE in its existing folder. Keep adjacent preferences, transfer-recovery metadata and backups.
3. For an installed copy, run the new EXE and choose **Install for me**, using the existing installation folder. Setup handles the running copy and relaunches it.
4. Check About shows 1.1.1 on both PCs, then try normal sharing and a small file/folder transfer.

RC3 settings and recovery formats are retained. Recovered unfinished work starts paused; review it and choose Resume or Cancel. Compared with 1.1.0, the recovery journal includes compact receipts and file-rescan metadata. Avoid downgrading with unfinished transfers. Updating never redirects an existing transfer to a different destination.

## Review and validation

The release review covered the changes since 1.1.0, with particular attention to destination identity, non-overwriting commits, cancellation and cleanup, lost acknowledgements, recovery-write failures, queue handling, matrix capture cancellation and scaling. No release-blocking issue was identified in that review. This is not an exhaustive security audit or proof against every race or device failure.

RC3 passed **155 automated Windows tests with one existing skip**, including encrypted loopback interruption/recovery and matrix interaction/rendering checks. The owner reports RC3 is working well on real PCs. The stable release repeats Windows Release x64 compilation, the automated suite, executable-version validation and single-file packaging checks before publishing the EXE and SHA-256 checksum. Physical sleep/wake, lock/unlock and all display/network configurations have not been freshly exhaustively retested for this promotion.

## Remaining limitations

- Unsigned Windows x64 EXE; no service, protected-UAC or sign-in-screen control.
- Full-speed transfers can still compete with mouse sharing on busy Wi-Fi.
- Folder copies can partially complete. Existing destination files and completed children are preserved.
- Unsupported links and unreadable directory trees remain skipped and require a fresh drag after the problem is resolved.
- Remote cancellation and cleanup require the peer and original destination to become available again.
- Transfer-window recovery controls apply to drag-and-drop; clipboard file copy/paste uses its separate transport.

See the [file-transfer guide](FILE_TRANSFERS.md), historical [transfer hardening notes](RC_1.1.1.md) and [matrix notes](RC_1.1.1_RC2.md).
