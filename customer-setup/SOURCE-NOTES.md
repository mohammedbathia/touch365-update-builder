# RustDesk customer provisioning source notes

Checked 2026-10-28 against official RustDesk tags 1.5.0 and 1.4.3. Research copies are under `research/client-config`; they are not customer payloads. These findings describe source behavior, not a completed runtime acceptance test.

## Windows signature verification follow-up

The October 29 screenshot is a later 1.0.1 failure with `CERT_E_CHAINING`
(`0x800B010A`), not proof that the earlier cache-only flag is still set. In a
read-only local reproduction, both this verifier and Windows PowerShell's
`Get-AuthenticodeSignature` reject the pinned payload while network retrieval
is blocked. With network access, the identical SHA-256 verifies as Valid and
its publisher chain builds successfully. That establishes a local retrieval/
trust-context dependency; it does not establish the affected customer's cause,
or prove that the official executable omits intermediate certificates.

Version 1.0.2 retries only `CERT_E_CHAINING` once after one second, keeping the
same flags, file subject and expected publisher. The final native result must
be zero. Persistent chain failures, other trust failures and unexpected
publishers remain rejected. No certificate is imported and no alternative
trust store, hash-only acceptance or certificate-only fallback is used.

Technician follow-up for a persistent error: record the exact stage/code,
check connectivity and date/time on the affected PC, then investigate Windows
certificate retrieval/proxy/trust in the account that runs setup. Any changes
to machine trust or policy require separate authorization. Customer-PC
acceptance remains pending; the source change is not a released installer.

Setup 1.0.0 passed `WTD_CACHE_ONLY_URL_RETRIEVAL` (0x1000). Microsoft documents
that this prevents network retrieval during verification. A new customer PC
can therefore lack information that the build PC already cached. The reported
generic error does not establish that this was the actual failure cause.
Version 1.0.1 uses normal provider flags (0), still requires a zero WinVerifyTrust
status and the expected publisher, and retains the exact bundled payload hash.
No trust-store changes, ignored certificate errors or verification bypasses
are made. Errors include stage, basename and the native LONG as eight hex digits.

Sources: [WINTRUST_DATA](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data),
[WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust),
[trust status codes](https://learn.microsoft.com/en-us/windows/win32/seccrypto/certificate-and-trust-return-values).

Resolved source revisions: 1.5.0 = `fada664df7a294d1d1a9ca3e7cd3637069122f17`; 1.4.3 = `6a0da9cf09b80bba6ca731d3029210c5d839a77b`. The 1.5.0 `hbb_common` submodule is `229b904508364c8997aad0fb5af57effac859f60`.

## Supported Windows management commands

- `RustDesk.exe --option NAME VALUE` writes a single service option; `RustDesk.exe --option NAME` reads it. Both require an installed client and administrator privileges. Empty VALUE removes that option. Use a process launcher that preserves an empty argument.
- `--config STRING` sets only server host, public key, API and relay. It also requires installed + administrator. Explicit per-option commands make readback easier.
- `--get-id` prints the current ID and does not require administrator in the command dispatcher. An ID is not proof of Ready/registration.
- `--install-service` exists when an installed client has no service. `--silent-install` installs the official EXE. The 1.5.0 silent-install parser supports printer=0/1 and debug, not a no-start argument.
- Command dispatch often returns normally on errors; zero exit status alone cannot prove success. Read back each expected option and verify service persistence.
- The CLI IPC get/set functions fall back to the invoking user's config when IPC cannot connect. Therefore CLI readback alone does not prove the service received values. Require the service/server process plus selected values in service-owned `RustDesk2.toml`, preferably readback again after a service restart.

Sources: [1.5.0 command dispatcher](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/core_main.rs#L415-L533), [1.4.3 dispatcher](https://github.com/rustdesk/rustdesk/blob/1.4.3/src/core_main.rs#L419-L457), [IPC option implementation](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/ipc.rs#L1869-L1918).

## Customer settings and order

Set admission policy first, then permissions, then network identity. Before opening the customer UI, verify all expected values.

| Regular service option | Value | Meaning |
|---|---|---|
| approve-mode | click | Customer must accept each new request |
| allow-only-conn-window-open | Y | Reject incoming requests while the main RustDesk process is not open |
| access-mode | custom | Use individually chosen permissions |
| enable-keyboard | Y | Keyboard/mouse support |
| enable-clipboard | Y | Text clipboard support |
| enable-file-transfer | Y | File sessions and file copy/paste support |
| allow-remote-config-modification | N | Remote party may not edit settings |
| enable-terminal | N | No separate remote-terminal permission |
| enable-tunnel | N | No TCP-tunnel permission |
| enable-camera | N | No camera permission |
| enable-remote-printer | N | No remote-printer permission |
| enable-block-input | N | Do not let technician block customer input |
| custom-rendezvous-server | remote.touch365.co.za | Touch365 ID server |
| relay-server | remote.touch365.co.za | Touch365 relay |
| key | public server key in deployment/server-public-key.txt | Server identity |
| api-server | empty argument | Clear any Pro API address |

Audio, recording and remote-restart permissions should be selected deliberately for the desired product scope; they are separate supported options. The table lists the requested screen/control/copying minimum plus explicit restrictions.

`access-mode=custom` is accepted; the UI may save empty/default for Custom. `full` forces permissions on; `view` forces them off. An empty enable-* value is normally enabled, while allow-* is enabled only by Y. For setup proof, exact explicit Y/N readback is preferable; only normalize Custom empty vs custom and deliberate removals when needed.

The only-open setting checks for a RustDesk main process. It does not verify that the window is foreground, visible or unminimized. Do not advertise that stronger behavior.

Click approval precedes cached/recent/password authorization in the ordinary login branch, including file-transfer sessions. No permanent password needs to be set, read, copied or removed. An existing password is not used in click-only mode; retain unrelated identity/password storage.

Sources: [1.5.0 permission UI](https://github.com/rustdesk/rustdesk/blob/1.5.0/flutter/lib/desktop/pages/desktop_setting_page.dart#L1138-L1228), [1.4.3 permission UI](https://github.com/rustdesk/rustdesk/blob/1.4.3/flutter/lib/desktop/pages/desktop_setting_page.dart#L952-L1035), [only-open admission](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/server/connection.rs#L1501-L1508), [click approval](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/server/connection.rs#L2990-L3032), [1.4.3 click approval](https://github.com/rustdesk/rustdesk/blob/1.4.3/src/server/connection.rs#L2171-L2207), [official advanced settings](https://rustdesk.com/docs/en/self-host/client-configuration/advanced-settings/).

## Settings that are not ordinary --option values

- `allow-remote-cm-modification` is read from **LocalConfig**, which persists in `RustDesk_local.toml` under `[options]`. It defaults off (empty is not Y). A regular --option write does not change the effective UI permission. Existing Y requires a narrow, backed-up edit in the interactive user's LocalConfig while that user's RustDesk processes are closed, or a supported local settings interface.
- `allow-logon-screen-password`, `one-way-clipboard-redirection`, and `one-way-file-transfer` are read from **BUILTIN_SETTINGS**, not ordinary Config options. The official unmodified client has them unset, which means no lock-screen password exception and no one-way restriction. Writing N with --option does not enforce these values in a custom build. Do not imply otherwise. Reject unknown custom clients rather than overriding assumptions about their embedded policy.
- Outgoing display defaults such as file-copy/paste are user/peer settings. Incoming customer capability is enabled by enable-file-transfer; a technician who has disabled outgoing copying may still need that separate preference corrected.

Sources: [CM local option lookup](https://github.com/rustdesk/rustdesk/blob/1.5.0/flutter/lib/desktop/pages/server_page.dart#L1230-L1248), [LocalConfig getter](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/common.rs#L1206-L1226), [built-in getter](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/common.rs#L2550-L2557), [clipboard/file checks](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/server/connection.rs#L2320-L2340), [lock-screen exception](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/server/connection.rs#L2990-L2995).

## Installation and persistence caveats

The official EXE installer normally starts the service and tray before returning. It imports config as a temporary service, then creates the regular auto-start service. Silent mode skips opening the main window, but still launches the tray. There is no supported no-start CLI flag in the inspected parser. A preexisting stop-service=Y is respected by installer internals, but --option stop-service is not equivalent to the UI's stop/uninstall-service action.

The service is created without an obj= argument (Windows default LocalSystem). RustDesk explicitly patches the System profile path to `ServiceProfiles\\LocalService`, so its expected service config is `%WINDIR%\\ServiceProfiles\\LocalService\\AppData\\Roaming\\RustDesk\\config\\RustDesk2.toml`. Discover and verify actual service/process state rather than relying only on an assumed service account.

`--import-config` is unsuitable for this wrapper: it expects a nonempty primary identity config, derives a companion 2.toml file and uses timestamp conditions before replacing entire configs. Sharing a technician's config would also distribute identities/credentials. Use the official CLI and scoped options instead.

EXE installation over MSI is explicitly rejected by 1.5.0 installer source. Reuse a compatible, signed existing installation through its installed executable rather than reinstalling EXE over it. Do not silently downgrade newer versions.

Sources: [installation implementation](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/platform/windows.rs#L1585-L1773), [service creation / tray](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/platform/windows.rs#L3920-L3971), [profile path patch at pinned hbb_common revision](https://github.com/rustdesk/hbb_common/blob/229b904508364c8997aad0fb5af57effac859f60/src/config.rs#L458-L470), [import implementation](https://github.com/rustdesk/rustdesk/blob/1.5.0/src/core_main.rs#L730-L754).

## Required runtime acceptance

Unit/config tests do not establish a working customer session. On a fresh Windows profile/PC, verify normal installer launch and administrator prompt, automatic configuration, main UI Ready against Touch365, reject request, accept request, input, text in both directions, one small generated file in both directions, disconnect and require fresh approval. Check a compatible existing installation separately. Keep lock/logon and UAC support explicitly unverified until exercised.
