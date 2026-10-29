# Input freeze recovery — 1.2.0-preview.10

The reported laptop session repeatedly failed to inject mouse movement through
the optional WinRT input backend. Its operational log recorded
`COMException 0x8007007A` in `InputSimulation.SendInputEx`, reached from received
mouse packets. The availability flag remained enabled after that failure, so
subsequent movement packets continued to use the failing backend.

A separately started ordinary copy then blocked before creating its main form.
A managed stack capture showed its main thread inside native `SendInputEx`,
called by `MoveMouseRelative(0, 0)` from `InitAndCleanup.Init`. This startup probe
did not test WinRT: the initialization code had explicitly disabled WinRT before
the call, so it sent a real native event before the main message loop existed.

## Changes

- Initialization no longer sends synthetic mouse movement. Optional WinRT
  creation starts from a native fallback state and tolerates the supported
  missing-component/COM failure classes.
- A WinRT mouse-injection COM failure disables that backend for the rest of the
  process and sends the same event through the existing native path. It logs
  only the HRESULT once, without clipboard, key or input contents.
- Missing injectors also select native delivery. Successful WinRT calls do not
  duplicate the event. Both native layouts retain the event fields and return
  values; keyboard and mouse-button routing retain their native path.
- Existing secure-desktop checks still run before dispatch. The protected
  service, protocol, pairing and clipboard/file-transfer code are unchanged.

This removes the observed startup trigger and contains the recorded WinRT COM
fault. Native `SendInput` is still synchronous; this change does not claim to
recover every native Windows input deadlock.

## Validation and installed state

The Release x64 build and self-contained single-file package passed. All 430
focused tests passed with no failures or skips, including 15 new cases for
startup, relative/absolute movement, sticky fallback, both native layouts,
keyboard/button routing and unexpected exceptions. No real input is injected
by the new tests. Independent source review found no blocking regression.

The frozen process's authenticated shutdown endpoint was tried first. With an
empty transfer journal, a stop was requested against a handle pinned to the
diagnosed process lifetime. Windows accepted termination but the process did
not reach its signaled exited state within the bounded wait. No installed files,
preferences, task, service or firewall setting were changed by that attempt.

After the Windows restart, the reviewed preview 10 package was installed on the
laptop with same-account Windows approval. Pairing, the original startup entry
and administrator-launch choice were preserved. The protected payload and
existing approved-launch task were updated; the temporary ordinary High copy
then exited through its authenticated shutdown endpoint.

A clean Medium source launch invoked the task without a new prompt. The actual
main ran from the protected path at High integrity, with the same account's
clipboard helper at Medium through Explorer. Source and protected hashes matched
the tested package; preferences remained in their original location.

Post-install two-PC mouse, keyboard and Task Manager acceptance is pending. The
full inherited test suite, sleep/wake, lock/unlock and long-running stability are
not claimed by this validation.
