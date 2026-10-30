# Touch365 Remote Support Setup

Single-file Windows x64 bootstrapper wrapping the official, unmodified RustDesk
1.5.0 portable installer. Built with the Windows .NET Framework compiler; no
additional .NET download or PowerShell execution is required on customer PCs.

Current source candidate: **1.1.1**. Build output:
`dist/Touch365-Remote-Support-Setup-1.1.1.exe`. It includes the 1.0.2 certificate
retry, service-managed application/configuration updates and service-free
app-only updates. App-only mode preserves the enrolled Windows user's profile,
refuses all configuration updates and does not install a RustDesk service. The default
build has no production signing trust/feed and updating is disabled. See
[`UPDATER.md`](UPDATER.md) for controls, supported packages and activation steps.
This candidate has not been published as a release or accepted on a customer PC.

Version 1.0.1 added normal Windows certificate retrieval and exact trust errors.
The later customer screenshot reports `0x800B010A` (certificate-chain building
failed). Version 1.0.2 retries that specific error once, after one second, with
the same Windows Authenticode policy. A retry must succeed before the publisher
check or setup can continue. Persistent failures stay blocked and explain how to
retry after checking the PC's internet connection and date/time.

This does not supply new trusted roots, import certificates, change Windows
security settings or accept an untrusted signature. A machine with unavailable
certificate retrieval or missing trust still needs its administrator's attention.
The affected customer PC's exact cause remains unconfirmed; see `TEST-STATUS.md`.

## Customer flow

Open `dist/Touch365-Remote-Support-Setup.exe`, approve Windows UAC, wait for
RustDesk to show Ready, share the ID and accept the technician's request.
Later use the per-user **Touch365 Remote Support** Start menu shortcut.

This installer changes an existing compatible RustDesk installation to
Touch365's server. It does not provide an isolated second RustDesk identity.
Existing user clients must be quit first; an active session is never killed.
It supports official EXE/legacy/MSI installations in Program Files, version
1.4.3 or newer, by applying configuration without replacing their binaries.
Older, custom, corrupt or conflicting installations stop with an actionable
message rather than being replaced. Fresh PCs receive bundled version 1.5.0.

## Configuration

- ID and relay: `remote.touch365.co.za`; API blank; public server key embedded.
- Accept sessions via click; main RustDesk window must be open.
- Custom permissions: keyboard/mouse, clipboard and file transfer enabled.
- Audio, camera, terminal, TCP tunnels, remote printing, remote restart,
  recording, user-input blocking and remote configuration changes disabled.
- Interactive user's remote approval-window modification disabled.
- No passwords, private keys or existing device identity are distributed.

The customer's existing password values are not exported or copied. Click-only
approval prevents password-based session admission while this policy remains
selected. These are configured settings, not a tamper-proof custom client:
the local customer/administrator may subsequently change their settings.
One-way clipboard/file redirection and lock-screen-password exceptions are
built-in settings in RustDesk, not normal service options. The unmodified
official client is used; this wrapper does not pretend that writing arbitrary
CLI options overrides built-in settings.

## Implementation and recovery

`Program.cs` runs as the original user and launches an elevated worker. The
worker installs/configures the service using supported `--option` commands.
Every required option is read back through the CLI and independently from
the service's persisted configuration. This detects RustDesk's silent IPC
fallback to the invoking user's profile.

The original user's local setting is patched with an atomic, scoped edit
before the new tray/connection manager can cache it. A UAC refusal restores
that edit. After success, the original user process opens RustDesk, even if
another administrator approved UAC.

Previous service option values are backed up under:
`%ProgramData%\Touch365\RemoteSupportSetup\<run>\backup\previous-options.xml`.
Existing local profile backup is under:
`%LOCALAPPDATA%\Touch365\RemoteSupport\Backups\<run>\RustDesk_local.toml`.
Backups have restricted ACLs. Do not distribute backups or collect entire
RustDesk profiles; they can contain user-specific sensitive data.

Configuration failure attempts to restore original values, with approval
restored last. If rollback cannot be confirmed, the service is disabled and
stopped. A failed fresh installation/configuration similarly pauses its newly
started service. Retrying this setup deliberately re-enables that service.
Setup does not uninstall a newly installed application after failure.

Each run retains a small status/result record and, for fresh installs, the
verified payload under its ProgramData folder. No automatic cleanup of user
data, customer sessions, existing backups or other installations is performed.

## Build and verification

Run `./Build.ps1`, then `./tests/Test.ps1` from PowerShell. Build.ps1 checks the
official payload's pinned SHA-256 and publisher signature before embedding it.
The source uses C# 5 and the existing .NET Framework 4.x runtime.
Run `./tests/Test-Updater.ps1` for the isolated updater suite and complete source
compilation. The original engine suite also checks the actual installed client;
a revoked legacy client must remain rejected rather than turning that test green.

When using the standalone source ZIP, supply the official payload explicitly:
`./Build.ps1 -PayloadPath C:\path\rustdesk-1.5.0-x86_64.exe`.
Download it from the exact official release linked in the third-party notices;
the build rejects a different digest. The source ZIP excludes payload binaries,
test output, customer profiles and administrative keys.

`--preview` displays the setup window without any provisioning or application
launch. It is for layout inspection only, not proof of installation success.

`dist/build-receipt.json` identifies the exact output hash and payload.
The Touch365 wrapper is currently unsigned. No code-signing certificate was
provided. The inner RustDesk payload retains its verified official signature.
Windows UAC/SmartScreen prompts are not bypassed.

Automated tests exercise configuration/rollback against a fake service and
atomic local edits against isolated fixtures. Signature checks use the actual
payload and local installed client. They do not prove that a real fresh install
or remote session works. See `TEST-STATUS.md` for the current acceptance status.

## Source and redistribution

The custom bootstrapper source is in `src/` and may be maintained by Touch365.
RustDesk remains unmodified and separately licensed under AGPL-3.0.
See `THIRD-PARTY-NOTICES.txt`, `RustDesk-LICENSE.txt`, and `SOURCE-NOTES.md`.
Preserve these notices and access to the corresponding RustDesk source when
redistributing the customer package.
