# Approved administrator launch — 1.2.0-preview.9

The optional **Settings → Installation → Approved administrator launch** mode
lets an already approved MWB copy start with this Windows account's administrator
token without a fresh prompt at each ordinary launch. It is off by default and
currently included in Windows x64 builds. Ordinary portable use and the existing
per-launch administrator choice remain available.

## Set up or remove

Configure the original copy and turn on **Run as administrator** first. **Set up
approved launch…** describes the persistent approval and asks before launching
the fixed setup mode. Use the same Windows account for Windows approval. Setup
creates a protected copy and an on-demand task for that account; it stores no
password. The original EXE, shortcuts and **Start with Windows** registration
remain the ordinary entry points.

Exit and reopen the original EXE to use the new route. The entire normal app,
including input received from another PC, runs with administrator permissions.
The clipboard helper still launches through ordinary Explorer at Medium
integrity. The protected-UAC service is a separate optional feature.

**Remove approved launch…** deletes only this account's matched task and clears
its local launch choice. A running process keeps its token until it exits. The
protected payload/cache may still be in use and are retained; removal never
deletes preferences or force-kills processes. Reopen the original copy afterward
to use ordinary startup. Clearing **Run as administrator** also stops ordinary
launches from using the task; remove approved launch to revoke the stored task.

This preview supports one approved source location per Windows account. Removing
the task retains its protected source association, so setup at a different EXE
location is refused even after removal. Keep the original location; moving or
reinstalling elsewhere requires separately reviewed administrator cleanup of the
retained association. There is no automatic rebinding or cleanup in this preview.

## Protected files and updates

The task is named `Approved-<user SID>` under `\MouseWithoutBorders Portable\`.
It uses that user's interactive token and Highest run level, with no logon or
timer trigger. Its single action is the fixed, argument-free native launcher in
`%ProgramFiles%\Mouse Without Borders Approved Launch\<user SID>\`.

The task and payload are administrator/SYSTEM-owned. The ordinary user can read
and execute them, without changing the action, code, ownership or permissions.
An inherited OWNER RIGHTS rule limits implicit owner rights on newly extracted
runtime files. The launcher's protected cache and temporary directories live
under that same fixed root. Symbolic links and junctions are refused.

The native launcher runs before .NET. It verifies its own location, user/token,
the protected files and ACLs; it holds checked objects against replacement until
process creation. It passes only an explicit environment allowlist, excluding
inherited .NET startup hooks, profilers and user PATH entries. It starts only the
adjacent MWB with `--mwb-approved-main` on the normal desktop. It accepts no caller
arguments and cannot elevate a caller-selected path or command.

The managed main validates the protected source record and task before reading
the original adjacent preferences and transfer journal. The actual executable
path remains the protected path; startup and local launch-choice storage use the
original path. No pairing copy is created and no pairing key enters the task,
source record or published evidence.

Updating the ordinary EXE does not silently authorize changed code. If the source
hash differs from the protected payload, task startup refuses and the ordinary
administrator route can request approval. Close the approved copy, open the new
ordinary copy with approval, then use **Update approved launch…**. A running
protected payload is not overwritten. The existing portable updater continues
to refuse Program Files destinations. Approved mode also refuses the old
install/uninstall launch paths; remove the approval and reopen the original copy
before changing its installation.

## Build and validation

`build-approved-launcher.ps1` builds the native Windows x64 helper using MSVC or
an explicitly selected Zig compiler (`MWB_ZIG_EXE`). The native artifact is
embedded in the portable EXE. The normal build and publish scripts prepare it;
direct x64 publish refuses a missing helper. No native compiler is needed on the
end user's machine. See the [native contract](../ApprovedLaunchNative/README.md).

The Release x64 build and single-file package passed. All 415 focused tests
passed, including the new task/ACL policy, fixed invocation routing, embedded
native PE and exact resource hash, plus 13 UI cases. Both normal and large-text
renders were visually reviewed. The three release-metadata tests and the
three-project repository check passed. The full inherited suite is not claimed.

A native fixture exercised the actual environment builder with poisoned .NET,
profiler, PATH and temporary-directory values: only the explicit allowlist was
passed. The real native binary rejected foreign arguments and a Medium caller.
Independent review covered the native boundary, the managed launch paths and
partial-update repair/removal. Missing or hash-mismatched payload files cannot
run, but a matched approval can be repaired or removed through approved setup.
Untrusted ownership, ACLs or reparse points still cause setup to refuse the path.

After explicit user approval, the reviewed preview 9 EXE and protected copy were
installed on the laptop. The own-account task was registered with the fixed
native action, no arguments or triggers, and a read/execute-only user grant.
Pairing, the existing startup entry and the ordinary administrator choice were
preserved. Windows UAC policy, the protected-UAC service and firewall were unchanged.

A clean ordinary launch from a Medium process invoked the task without UAC.
The verified main ran from the fixed protected path at High integrity; its
clipboard helper ran at Medium through Explorer. Source and protected EXE hashes
matched, the original preferences remained present, and no protected preferences
copy was created. An earlier existing Medium instance was closed through its
authenticated shutdown endpoint before this clean-start verification.

Post-restart two-PC input, clipboard and file acceptance is pending. Sign-in
startup, lock/unlock and sleep/wake have not been revalidated for this mode.

The Windows task security model and single-file cache requirements are described
in [Microsoft's task security documentation](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)
and [.NET single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).
