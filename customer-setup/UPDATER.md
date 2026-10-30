# Remote Support updater — source candidate 1.1.0

This candidate adds an opt-in update controller to the existing setup. It can
check and stage signed RustDesk application and configuration updates hourly
while its window is open. Applying a change requires review of the exact signed
metadata, Windows administrator approval, prior enrollment and a stopped/idle
RustDesk installation. It never kills sessions, stops/starts a service, registers
a task, installs credentials, runs downloaded scripts or changes machine trust.

**The default build has no update feed or public signing key and is disabled.**
Tests passing does not activate updating. No production feed, enrollment or
customer rollout was performed for this feature.

## Existing patterns and one maintenance path

The controller follows the existing Touch365 Checker/Native Builder model:
automatic checks inside the open application, owned partial downloads, bounded
HTTPS, exact size/hash validation, revalidation before administrator handoff,
verified backups and recovery. The existing `Signature.RequireOfficial`,
`SafeFiles`, argument quoting and scoped `ConfigText` edits remain in use.

Reference implementation paths in the existing NSIS project are
`update-checker/CheckerSelfUpdateClient.cs`, `CheckerSelfUpdateApply.cs`,
`CheckerSelfUpdateService.cs` and `native/BuilderAutoUpdate.cs`. Their feeds do
not provide detached authenticated metadata, so their unsigned manifests are
not accepted as a Remote Support channel.

Setup and update workers share the protected machine-wide maintenance mutex.
Setup refuses pending updater journals or damaged enrollment state before
preparing RustDesk, so ordinary setup cannot bypass interrupted-update recovery.
The new modules are protocol/download, package verification, transaction
coordination and the Windows filesystem adapter. The UI cannot select a weaker
verifier; internal fixture adapters are not exposed as command-line switches.

## What is supported

- Existing conventional `Program Files\RustDesk` official EXE installation,
  with a registered RustDesk service and the existing service configuration.
  MSI, custom, incomplete or conflicting installations fail closed.
- Full application-directory ZIP updates: root `RustDesk.exe` plus explicitly
  inventoried resources. This is **not** an upstream packed installer EXE.
  Running an external installer would have machine-wide side effects that a
  directory backup cannot reliably roll back, so that path is not offered.
- Scoped service `RustDesk2.toml` option changes, while the service is stopped.
  Other settings, comments and unrelated customer data are preserved. Recovery
  backups are private and never included in public metadata or Git.

Before every mutation, the adapter requires the service to be stopped and no
ordinary or Touch365-encoded `rustdesk*.exe` client processes to exist across
sessions. Enumeration failure means busy/unknown, not idle. An administrator
must finish sessions and stop the service deliberately; the updater does not
do this automatically. Files/configuration are verified after application.
Runtime connectivity and operational health still require controlled acceptance
before a production release; no RustDesk process is launched by the updater.

## Authentication and package contract

An out-of-band provisioned public RSA key (at least 2048 bits) authenticates the
exact UTF-8 payload bytes with RSA PKCS#1 v1.5/SHA-256. Public-only Windows CNG
verification does not generate a private key or create a key container. A
manifest cannot replace the pinned key or choose its signature algorithm.

The strict envelope format is:

```xml
<signedUpdate format="1">
  <payload>BASE64_OF_EXACT_UTF8_PAYLOAD</payload>
  <signature algorithm="rsa-sha256">BASE64_OF_SIGNATURE</signature>
</signedUpdate>
```

The signed payload has this shape (placeholders are intentionally not usable):

```xml
<update schema="1" product="touch365-remote-support" channel="stable"
        sequence="INCREASING_INTEGER" issuedUtc="UTC_TIME_ENDING_Z"
        expiresUtc="LATER_UTC_TIME_ENDING_Z">
  <binary version="NEWER_VERSION" url="https://APPROVED_HOST/package.zip"
          bytes="EXACT_ZIP_LENGTH" sha256="ZIP_SHA256">
    <file path="RustDesk.exe" bytes="EXACT_LENGTH" sha256="FILE_SHA256" />
  </binary>
  <configuration version="INCREASING_INTEGER">
    <option name="enable-clipboard" value="Y" />
  </configuration>
</update>
```

Either `binary` or `configuration` may be omitted, but not both. A binary update
must be strictly newer than the installed version; a configuration-only update
omits `binary`. Times are UTC protocol values with a maximum seven-day validity.
Sequence numbers cannot be reused for different content. Transport is HTTPS
with ordinary Windows TLS validation, explicit host allowlists, no credentials
or redirects, bounded reads/timeouts and no unsigned fallback.

Limits: 96 KiB envelope, 64 KiB signed payload, 256 MiB download, 512 MiB expanded
inventory and 256 files. Every file needs exact size/hash; empty explicit ZIP
directory records are allowed only for canonical parents implied by those files.
Unknown files/directories, case aliases, traversal, links, Windows device names,
ADS, ambiguous paths and compression bombs are rejected. `RustDesk.exe` is the
only allowed EXE. Other allowed resources are `.dll`, `.pak`, `.dat`, `.bin`,
`.json`, `.txt`, `.png`, `.ico` and `.ttf`. All bytes are authenticated by the
signed inventory; the main EXE additionally needs the existing PURSLANE Windows
publisher validation and exact signed version. No package can add scripts or
replace the updater itself. Packages must also fit the supported Windows path
limits at their staging and installed locations.

Configuration accepts only the existing policy's known option names, excluding
`stop-service`. Values are constrained: server names are DNS hosts, the RustDesk
key is a 32-byte public key, permissions are explicit Y/N values. Consent stays
click-only, requires the main connection window open, uses custom permissions
and forbids remote policy modification. Passwords, arbitrary keys, paths,
commands, scripts and unknown/duplicate fields are rejected. **Every** update,
including server/key/permission changes, needs approval bound to its exact
metadata digest; a feed cannot authorize its own application.
Enrollment and application also verify the existing consent invariants; an
unrelated partial update cannot silently retain drift to unattended access.
Elevated metadata/package handoffs accept only canonical local fixed-drive
ordinary files, never UNC/device paths or alternate streams.

## Recovery and replay protection

Explicit enrollment records administrator/SYSTEM-owned state under
`%ProgramData%\Touch365-RemoteSupportUpdates`. Existing storage with an
untrusted owner, broad write rights or a missing/null DACL is rejected. State,
marker and backup permissions are checked when loaded. Missing/corrupt enrolled
state requires recovery; it is never silently reset. No secrets belong in a
trust profile, feed or repository.

The coordinator keeps sequence, binary/configuration floors and last-observed
UTC separately from successful application. Significant clock rollback fails
closed. Approved floors are persisted even when a busy machine defers an
update, and never decrease during restoration.

A private durable backup and `Prepared` journal precede an `Applying` journal
and any mutation. Application directories are switched by same-volume rename,
retaining the exact original directory and its ACLs. Configuration changes use
atomic replacement with before/after hashes. A crash in the directory-swap gap
can restore the retained original. Recovery confirms exact installed files and
settings before finalizing a completed update; otherwise it restores a known
backup. It never blindly reruns an installation or overwrites unrecognized
concurrent changes. Ambiguity retains `RecoveryRequired` and the version floors.

An interrupted download is discarded as an owned partial and downloaded again;
existing staged files and unrelated data are preserved. No rollback protection
depends on deleting an old binary or on a dismissed notification.

## Build, review and activation

Run fixture verification with `./tests/Test-Updater.ps1`. It compiles all source,
proves an unprovisioned build returns disabled, and exercises public crypto
verification, strict metadata, staging, package paths, replay/approval and real
filesystem recovery with isolated fixtures. The published public verification
vector is from [RFC 7515 Appendix A.2](https://www.rfc-editor.org/rfc/rfc7515#appendix-A.2);
no private key or generated signing credential is used. Parsed manifest/host
tests use internal deterministic seams; an actual owner-signed feed remains a
separate acceptance prerequisite.
The verified candidate passes 80 updater fixtures; detailed results and the
separate legacy installed-client trust failure are in `TEST-STATUS.md`.

Build the candidate with the existing verified official payload:

```powershell
./Build.ps1 -PayloadPath C:\approved\rustdesk-1.5.0-x86_64.exe
```

That build remains disabled. Future activation requires all of the following:

1. Moe selects the deployment target and an owner-controlled HTTPS feed/package
   host. Verify that route and signed canary metadata in the target environment.
2. Provision the approved signing authority out of band. Only its public key is
   embedded here. Creating or granting access to a signing key/credential needs
   specific action-time authorization; none was created in this task.
3. Supply a strict public trust profile to `Build.ps1 -UpdateTrustPath ...`:
   `<updaterTrust><feed>https://...</feed><publicKey>BASE64_UTF8_RSA_PUBLIC_XML</publicKey><downloadHost>...</downloadHost></updaterTrust>`.
   Public RSA XML contains only `Modulus` and `Exponent`. The build validates the
   embedded profile. Malformed profiles fail the build; omitted profiles disable
   updates. Approve/bootstrap this updater build through the trusted distribution
   path; the Touch365 wrapper is currently unsigned.
4. Approve a controlled test target and package producer which verifies the
   complete upstream RustDesk application layout before signing its inventory.
   Exercise clean/existing installations, signatures, offline state, active
   sessions, power-loss recovery and post-update operational health.
5. On that approved PC, run the saved setup executable with
   `--update-controller`. Enrollment prompts for administrator approval and
   explicit confirmation before creating protected state. Stop RustDesk only
   through the separately approved maintenance procedure. No enrollment or
   startup registration is silently inferred from this source change.
6. Opt into hourly checks while the controller is open, review a signed staged
   update and approve application. Starting a persistent controller or installing
   a task/service requires a separately reviewed deployment mechanism; this
   candidate registers none. Publication and customer rollout remain separate.

Recovery is exposed by the controller's **Recover** action and requires
administrator approval. Retained backups are not automatically deleted. Keep
them until acceptance and a reviewed retention policy permit cleanup.
