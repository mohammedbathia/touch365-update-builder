# Remote Support updater - source candidate 1.1.1

The controller checks and stages signed RustDesk updates hourly only when its
window is open and the automatic-check option is selected. Every application
requires review of the exact signed metadata, Windows administrator approval,
explicit enrollment and a closed/idle RustDesk installation. It never ends a
session, stops or starts a service, installs a driver, registers startup, or
performs unattended installation.

Version 1.1.1 adds service-free **app-only** enrollment to the existing
service-managed path. A RustDesk service is not needed to replace application
files. App-only mode preserves the enrolled Windows user's identity, servers
and permissions and rejects configuration updates, including mixed app/config
updates. It does not create a RustDesk service to satisfy an update requirement.

**The default build has no update feed or public signing key and is disabled.**
Passing tests or building this source does not enroll a PC or activate updates.
The earlier approved laptop test used a separate local build and disposable
signing authority to fetch/stage metadata only. That does not provision a
production feed or authorize enrollment, renewal or customer rollout.

## Supported targets and controls

Both modes require an existing registered, official EXE installation at the
conventional `Program Files\RustDesk` location. MSI, custom, incomplete or
conflicting installations fail closed. The installed executable must pass the
existing Windows publisher check before a new transaction is prepared.

- **App-only:** no RustDesk service may be installed. Enrollment pins the
  original Windows user SID and its canonical profile root, resolved from
  Windows `ProfileList`, rather than accepting a caller-supplied profile path.
  Administrator approval must use that same account; alternate-admin UAC is
  rejected. The account/profile binding is checked again on later maintenance.
  The user's RustDesk configuration tree is read and fingerprinted for
  preservation, never patched or restored by this mode. Missing, redirected,
  unreadable or concurrently changed profile data blocks the operation.
  Snapshot bounds are 256 files, 256 directories, 1 MiB per file and 16 MiB total;
  profiles outside these limits require separately reviewed maintenance.
- **Service-managed:** the registered RustDesk service and its existing
  service configuration must be present. Scoped changes to service
  `RustDesk2.toml` are supported, with unrelated values/comments preserved.
  Consent must already require click approval, the main connection window open,
  custom permissions and remote policy modification disabled. Enrollment and
  configuration application verify these invariants; a partial update cannot
  silently retain weaker settings.

Before application or recovery, all ordinary and Touch365-encoded RustDesk
client processes must be closed across sessions. The broker helper is included
in the closed-process checks. Service-managed maintenance additionally requires
its service stopped; app-only maintenance requires the service still absent.
An enumeration failure is busy/unknown, never proof that maintenance is safe.
The updater does not close clients or stop services to make a machine idle.

Enrollment is separate from installing RustDesk or configuring remote access.
It creates protected replay/recovery records only after explicit administrator
confirmation. The controller offers **Enroll app-only...** and
**Enroll service-managed...**. An existing enrollment cannot silently change
mode, user or profile. No startup task, background updater service or RustDesk
service is installed by either enrollment action.

## Existing patterns and one maintenance path

The controller follows the existing Touch365 Checker/Native Builder model:
checks inside the open application, owned partial downloads, bounded HTTPS,
exact size/hash validation, revalidation before administrator handoff, verified
backups and recovery. The existing `Signature.RequireOfficial`, `SafeFiles`,
argument quoting and scoped `ConfigText` edits remain in use.

Reference implementations in the existing NSIS project include
`update-checker/CheckerSelfUpdateClient.cs`, `CheckerSelfUpdateApply.cs`,
`CheckerSelfUpdateService.cs` and `native/BuilderAutoUpdate.cs`. Their unsigned
manifests are not accepted as a Remote Support channel.

Setup and update workers share the protected machine-wide maintenance mutex.
Ordinary setup refuses pending journals or damaged enrollment state before
preparing RustDesk. The protocol/download, package verification, transaction,
Windows filesystem and profile modules keep the same verification path for
UI and elevated workers. Internal fixture adapters cannot be selected through
command arguments or downloaded metadata.

## Authentication and complete application packages

An out-of-band provisioned public RSA key (at least 2048 bits) authenticates
the exact UTF-8 payload bytes with RSA PKCS#1 v1.5/SHA-256. Public-only Windows
CNG verification does not generate private keys or create key containers.
A manifest cannot replace the pinned key or choose its signature algorithm.

The envelope format is:

```xml
<signedUpdate format="1">
  <payload>BASE64_OF_EXACT_UTF8_PAYLOAD</payload>
  <signature algorithm="rsa-sha256">BASE64_OF_SIGNATURE</signature>
</signedUpdate>
```

A service-managed payload can contain either or both of the following sections.
App-only enrollment accepts the `binary` section alone. The example contains
placeholders and is intentionally not a usable or complete inventory:

```xml
<update schema="1" product="touch365-remote-support" channel="stable"
        sequence="INCREASING_INTEGER" issuedUtc="UTC_TIME_ENDING_Z"
        expiresUtc="LATER_UTC_TIME_ENDING_Z">
  <binary version="NEWER_VERSION" url="https://APPROVED_HOST/package.zip"
          bytes="EXACT_ZIP_LENGTH" sha256="ZIP_SHA256"
          layout="rustdesk-windows-flutter-v1">
    <file path="RustDesk.exe" bytes="EXACT_LENGTH" sha256="FILE_SHA256" />
  </binary>
  <configuration version="INCREASING_INTEGER">
    <option name="enable-clipboard" value="Y" />
  </configuration>
</update>
```

Each **new binary application in either mode** requires the signed complete
layout `rustdesk-windows-flutter-v1`. It is a full application-directory ZIP,
not an upstream packed installer EXE. The profile enumerates the reviewed
RustDesk Windows Flutter runtime's exact files: core modules, Flutter assets,
metadata and known auxiliary driver payloads. Every required file must be
present once with its signed size/hash; extras are rejected. A future upstream
layout change needs a reviewed layout-profile revision, not a broader extension
allowlist. Legacy no-layout metadata remains readable for existing recovery
records, not as a new complete binary package.

The signed inventory is also the code authorization for non-PE runtime content,
including Flutter AOT `data/app.so` and the fixed asset below; those files do
not acquire an Authenticode signature from the launcher. A trusted package
producer must derive the inventory from the reviewed official payload and
correct version, not infer official provenance merely from an installed folder
or the presence of a signed root EXE.

The main `RustDesk.exe` must have the exact signed version and pass the existing
PURSLANE Windows publisher validation. Root DLL modules also pass that publisher
check. `RuntimeBroker_rustdesk.exe` must be Microsoft-signed and byte-identical
to the target Windows installation's trusted `System32\RuntimeBroker.exe`.
Consequently a package prepared with a different Windows broker build is not
portable to that target; it is rejected rather than weakening the broker check.
The known driver catalogs receive Microsoft hardware-publisher checks in
addition to their signed inventory hashes. Driver payloads are inert files in
this transaction: the updater does not install them, modify DriverStore or run
driver commands. Runtime/driver compatibility still requires live acceptance.

Only the exact upstream `wakelock_plus` `no_sleep.js` asset is admitted as part
of this reviewed runtime; the updater never executes it. Unknown scripts,
executables, paths or auxiliary payloads are rejected. The downloaded package
cannot supply an uninstall shortcut. The sole permitted local ancillary file,
`Uninstall RustDesk.lnk`, may be retained unchanged only when its hash is bound
to the protected transaction backup; it is never opened or executed.

The reviewed 1.5.0 profile contains 106 files: 104 files compared byte-for-byte
with records statically extracted from the pinned official payload, plus its
generated `meta.toml` and the exact local Windows broker. The comparison reads
and decompresses payload data without launching RustDesk. An installed overlay
also contained two obsolete 1.4.x SVGs, `auth-azure.svg` and `keyboard.svg`; those
are not part of the complete 1.5.0 package.

Limits remain 96 KiB envelope, 64 KiB signed payload, 256 MiB download, 512 MiB
expanded inventory and 256 files. Empty ZIP directory records are accepted only
for canonical parents implied by signed files. Case aliases, traversal, links,
Windows device names, alternate streams, ambiguous paths, extra directories and
compression bombs are rejected. Installed and staging paths must fit the
supported Windows path limits.

A binary version must be strictly newer than the installed version. A
configuration version and sequence must increase according to the recorded
floors; a sequence cannot be reused for different content. Times are explicit
UTC with at most seven days' validity. HTTPS uses ordinary Windows TLS
validation, explicit hosts, no credentials or redirects, bounded reads/timeouts
and no unsigned fallback.

Service-managed configuration accepts only existing policy option names,
excluding `stop-service`. Values are constrained: DNS server names, a 32-byte
RustDesk public server key and explicit permission values. Passwords, arbitrary
keys, paths, commands, scripts and unknown/duplicate fields are rejected. Every
server/key/permission change requires approval bound to its exact metadata
digest. App-only mode rejects any configuration section before protected
staging or advancing replay state. Elevated file handoffs accept only canonical
local fixed-drive ordinary files, never UNC/device paths or alternate streams.

## Recovery and replay protection

Enrollment state is administrator/SYSTEM-owned under
`%ProgramData%\Touch365-RemoteSupportUpdates`. Broad write access, an untrusted
owner or missing/null DACL is rejected. Missing or corrupt enrolled state is
never silently reset. State, markers and backup permissions are checked when
loaded; no secrets belong in a trust profile, public feed or repository.

State schema 2 records the enrollment mode and, for app-only mode, its user
SID/profile binding. Transaction backups bind the same target. These fields
cannot change after enrollment. Schema 1 state and backups retain their
service-managed meaning so existing pending work can be recovered; they do not
implicitly become app-only enrollments.

The coordinator records sequence, version floors and last-observed UTC
separately from successful application. Clock rollback fails closed. Approved
floors are retained after deferral or restoration. A private durable backup and
`Prepared` journal precede `Applying` and any application mutation.

Application directories switch by same-volume rename, retaining the original
tree and ACLs. A crash in the rename gap can recover the retained directory even
when the live application directory is temporarily absent. The complete signed
inventory and any retained local shortcut are checked again after application.
Service-managed configuration uses atomic replacement with before/after hashes.
This is directory maintenance, not an installer transaction: existing Windows
registration and shortcuts are preserved. The actual EXE version controls
verification and version floors. Windows Apps/Uninstall `DisplayVersion` may
therefore retain its earlier value after a future application-directory update.

App-only mode checks its enrolled user's profile fingerprint before and after
the application transaction and during recovery. It does not restore or write
that profile. Concurrent profile, application, service or target changes block
unverified mutation, preserve the unknown changes and retain a recovery journal
when necessary. Recovery finalizes only already-verified application bytes or
restores a known application backup; it never blindly reruns an installation.

Interrupted downloads are discarded only as owned partial files and downloaded
again. Existing staged files, unrelated data and retained backups are preserved.
No recovery decision depends on a dismissed notification.

## Build, verification and activation

Run `./tests/Test-Updater.ps1` for isolated updater/profile/filesystem fixtures,
complete C# 5 compilation with warnings as errors, and the read-only disabled
channel probe. Public crypto verification includes the known-answer vector from
[RFC 7515 Appendix A.2](https://www.rfc-editor.org/rfc/rfc7515#appendix-A.2).
The fixture suite does not create signing credentials or enroll a real PC.
See [`TEST-STATUS.md`](TEST-STATUS.md) for exact run results and limitations.

Build the default disabled candidate with the pinned official payload:

```powershell
./Build.ps1 -PayloadPath C:\approved\rustdesk-1.5.0-x86_64.exe
```

Prepare an **unsigned local package** from an approved application directory
and a freshly source-built verifier:

```powershell
./Build-UpdatePackage.ps1 -ApplicationPath C:\approved\RustDesk -VerifierPath C:\approved\Touch365-Remote-Support-Setup-1.1.1.exe -PackageUrl https://APPROVED_HOST/application.zip -DestinationPath C:\approved\new-package
```

The destination must be new and outside the source application. The script
does not contact the declared URL, sign metadata, publish files or run RustDesk.
It copies the exact reviewed inventory into `prepared-application`, creates
`application.zip` and `unsigned-binary-fragment.xml`, then re-extracts through
the production verifier into `verified-application`. Its receipt records the
hashes, actual executable version and round-trip result. It omits only the
reviewed obsolete SVGs and local uninstall shortcut from the package copy;
unknown extra files are rejected and the source directory is left untouched.

This producer is not a new signing authority. Operator approval of the source
snapshot and official non-PE provenance remains necessary before the unsigned
fragment is included in an exact-byte signed payload. The built-in fixed-layout
checks and a native publisher check cannot by themselves prove that every
unsigned runtime resource came from the official release. Package preparation
does not authorize a live same-version reinstall, driver refresh or update.

Activation still requires a separately approved target and trusted distribution
of a build containing a public trust profile. The Touch365 wrapper is unsigned.
`Build.ps1 -UpdateTrustPath ...` embeds this strict profile:

```xml
<updaterTrust><feed>https://APPROVED_FEED</feed><publicKey>BASE64_UTF8_RSA_PUBLIC_XML</publicKey><downloadHost>APPROVED_HOST</downloadHost></updaterTrust>
```

Public RSA XML contains only `Modulus` and `Exponent`. Malformed profiles fail
the build; omitted profiles disable updates. Creating/granting a signing
authority or changing production trust requires specific authorization.

On an approved target, launch the saved EXE with `--update-controller` and
choose the reviewed enrollment mode. App-only mode requires same-account UAC,
no RustDesk service and a closed client; it does not change existing consent
settings to satisfy the service-managed policy. Service-managed mode requires
its already-approved service/configuration and an explicit maintenance stop.
Opening the controller or selecting hourly checks does not enroll or apply.

Before a first live binary update, verify its genuinely newer official version,
complete application package, target-specific broker, offline/session handling,
interruption recovery and post-update runtime/driver health. Reapplying the
installed version or inventing version metadata is not a valid upgrade test.

The separately approved laptop test updated the application to 1.5.0+68 and
proved signed metadata fetch/staging with a disposable local signing key. It
did not exercise updater enrollment or binary application. Its existing canary
expires **31 October 2026 at 22:15:21 Africa/Johannesburg (20:15:21 UTC)**.
This source increment neither renews it nor authorizes a replacement feed.
Expired metadata must remain rejected.

Recovery uses the controller's **Recover** action and administrator approval.
Keep retained backups until acceptance and an approved retention policy permit
cleanup. Persistent startup, unattended installation, production publication
and customer rollout remain separate work; this candidate registers none.
