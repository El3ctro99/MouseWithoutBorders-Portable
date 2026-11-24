# Clipboard stream recovery — 1.2.0-preview.11 candidate

The user reported that text copied from the other PC sometimes appeared as an
unusable image entry in Windows clipboard history on the laptop over Wi-Fi.
Waiting five seconds did not help; manually reconnecting both peers restored
copying temporarily. A fresh plain-text Notepad test did work over Wi-Fi, and
format-only inspection saw the normal main process publishing Unicode text.
The exact failing transfer has not yet been captured, so the observed image
entry is not proof that MWB converted text into a picture.

Code review found three defects in the existing message-channel clipboard path:

- A receiver abandoned a copy after 100 intervening packets, including valid
  mouse, keyboard and heartbeat traffic. Remaining chunks could then be read as
  a new copy beginning in the middle of the compressed payload.
- Each outgoing copy ran in an independent task. Packet-level locking allowed
  chunks of simultaneous text/image copies to interleave before their end markers.
- An incomplete extended message changed the read buffer's error type without
  updating the already decoded packet object.

The candidate keeps outgoing clipboard batches in enqueue order. Each batch
uses an immutable payload and captures its original socket owner, peers and
backing sockets. Input continues between individual packets, while an old batch
cannot continue onto replacement connections. Receiving no longer counts valid
input as a clipboard failure. Mixed type/source, disabled sharing and oversized
batches are discarded through their terminator; broken frames reach the normal
connection-close/recovery path instead of continuing a partial assembly.

The packet format, encryption, pairing, large-transfer/file channel and protected
input service are retained. The sender-side correction takes effect on a peer
only after that peer is updated. Existing debounce and duplicate-detection
behavior is outside this focused change.

## Validation

Release x64 builds and all 472 focused tests passed with no failures or skips.
This includes 34 new tests for ordered/concurrent copies, failure continuation,
more than 100 intervening input packets, mixed batches, sharing/size limits,
fragmented reads and EOF at every position of an extended frame. Independent
semantic review found no blocking defect. The original-connection filtering was
reviewed in the integration code; its replacement-owner unit test uses a fake
transport callback rather than live sockets.

The reviewed preview 11 was installed on the laptop after an authenticated,
graceful shutdown of the previous main process and clipboard helper. Pairing,
startup and administrator-launch choices were preserved. The source and protected
copies matched the reviewed candidate, and the existing approved-launch task was
updated and returned a success result. The native launcher and protected-input
service were unchanged.

A normal, non-elevated launch exited after starting the elevated protected main.
Its clipboard helper was verified as the same user's non-elevated process,
launched through Explorer. The laptop's Wi-Fi profile was Private, with both peer
connections established. These installation and launch checks do not yet confirm
resolution of the reported recurrence.

The user confirmed basic two-PC acceptance after installation: remote mouse
clicks worked in Task Manager, and fresh plain text copied from Notepad on the
main PC pasted correctly into Notepad on the laptop with Ctrl+V. The main peer's
version was not checked during acceptance; it has not been confirmed updated to
preview 11. These results do not establish that the intermittent failure is cured.

Images, rich text, both peers on the new version, repeated copies, copying during
mouse movement, extended Wi-Fi use, reconnect, lock/unlock and sleep/wake remain
physical acceptance checks. No public release or merge has been made. No clipboard
contents, security keys or raw logs were collected for the format-only diagnosis.

The user subsequently confirmed both PCs were on preview 11 and reported a
recurrence: main-to-laptop browser copying retained the laptop's last local text,
while reverse copying worked. Source-side local pasting sometimes made it
transferable. Preview 11 is therefore not a confirmed resolution. The focused
follow-up is documented in [capture recovery](CLIPBOARD_CAPTURE_RECOVERY.md).
