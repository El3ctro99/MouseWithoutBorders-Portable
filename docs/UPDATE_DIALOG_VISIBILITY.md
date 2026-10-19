# Update dialog visibility — 1.2.0-preview.8

Opening About → Check for updates could show the dialog briefly and then leave
it covered by About. The user reported this on both preview 7 PCs.

The laptop's native window snapshot confirmed that the update dialog was still
visible, enabled, opaque, restored and uncloaked, with the correct About owner.
About had the topmost extended style and a higher z-order position; its bounds
fully covered the non-topmost child. The asynchronous update check contains no
automatic hide or close operation.

The About launch path now explicitly gives the modal update dialog the same
`TopMost` state as its owner before `ShowDialog(owner)`. An ordinary non-topmost
owner retains an ordinary dialog. The existing close/cancellation behavior,
GitHub endpoint, update selection, download verification and install approval
are unchanged.

The update dialog has an internal checker seam for deterministic modal tests;
its normal constructor still uses the fixed production GitHub checker. Tests
must exercise the actual modal window and native ownership/topmost state,
including visibility before and after asynchronous completion. Earlier layout
tests moved its content into a separate host and did not cover this lifecycle.

## Validation status

The Release x64 build and all 357 targeted tests passed, including eight
real-modal visibility cases for topmost and ordinary owners while the check is
pending and after success, an available update, failure or cancellation. The
three release-metadata tests and three-project repository check also passed.
The full inherited test suite is not claimed as passing.

An isolated Windows fixture clicked the actual About check button and used the
production GitHub checker. The result changed from `Checking GitHub…` to
`You're up to date.`. At completion and in the final snapshot 18.5 seconds into
the run, the modal updater remained visible, enabled, opaque, restored and
uncloaked, with `TopMost` enabled and the correct About owner. Its native z-order
index was 14 and About's was 15, so the owner did not cover the child. The
fixture then explicitly closed the updater and returned to About. No update
installation was attempted by this fixture.

The native failure snapshot is retained privately under
`artifacts/updater-window-preview8/before-fix-window-state.json`, and the real
modal fixture results under
`artifacts/updater-window-preview8/modal-results/modal-result.json`. These
records contain app window metadata and status text, without preferences or
pairing keys.

Installing preview 8 and the user's acceptance check on the installed copy are
still pending.
