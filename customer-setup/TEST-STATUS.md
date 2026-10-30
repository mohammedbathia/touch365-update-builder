# Verification status - 30 October 2026

Current source candidate: Touch365 Remote Support Setup **1.1.1**.
This compatibility increment implements service-free app-only maintenance in
source. It does not enroll a real PC, renew the test feed, change installed
RustDesk/settings, create signing credentials, select hourly checking, register
startup, install a service, publish a release or deploy to customers.

## 1.1.1 verification

- Final updater suite: **123 passed, 0 failed** (16 protocol, 23 package,
  29 coordinator, 34 filesystem, 14 profile and 7 handoff checks). The complete
  C# 5 application also compiles with warnings treated as errors.
- Original engine suite: **42 passed, 0 failed** on the final serial run,
  including native publisher checks of the pinned payload and installed client.
  During parallel verification, its existing one-second launcher fixture once
  exhausted its time budget before polling. The unchanged suite passed when
  run alone; no production timeout or trust rule was weakened.
- The default 1.1.1 build passed with official RustDesk 1.5.0 (`Valid` signature).
  SHA-256: `ac6527ce2e41487820e19d145f21b177672f6563ab13a527648e8079ca8861f9`.
  `UpdateTrustConfigured` is false and the executable's read-only trust probe
  returns **2 (disabled)**. Its wrapper remains unsigned.
- Independent security review found no unresolved material issue. A stale
  overlay-file finding was corrected using the static official-payload audit
  below. CI is reported against the pushed commit separately; local results
  do not assert a GitHub Actions outcome.

The verified source changes are:

- Explicit app-only enrollment with the original Windows SID and canonical
  profile binding derived from Windows `ProfileList`. Same-account UAC is
  required; a different administrator cannot substitute their profile.
- Schema 2 enrollment and transaction records bind the mode, SID/profile and
  signed package layout. The target binding cannot change on save/recovery.
  Schema 1 records retain service-managed semantics and remain readable for
  existing recovery, rather than silently switching to service-free mode.
- App-only rejects configuration-only and mixed manifests before protected
  staging or replay-floor changes. It preserves the enrolled user's profile
  through bounded fingerprint/read-back checks and never writes or restores
  identity, servers, permissions or other profile configuration. Concurrent
  changes are preserved and block unverified application/recovery.
- Closed-session checks cover ordinary/encoded RustDesk clients and the broker
  helper. App-only requires no service; service-managed maintenance requires
  its service stopped. Unknown process/service state does not prove idleness.
- New binary application in either mode requires the signed complete layout
  `rustdesk-windows-flutter-v1`. Exact reviewed runtime files, signed size/hash
  inventory, root PURSLANE module signatures, target Windows broker identity,
  Microsoft hardware-publisher catalogs and a byte-bound retained uninstall
  shortcut replace the former assumption that a root EXE alone was sufficient.
  The updater neither executes downloaded scripts nor installs drivers.
  Non-PE runtime code such as Flutter AOT `data/app.so` relies on the signed
  inventory and trusted official-payload provenance, not Authenticode coverage
  inherited from the root EXE.
- The existing service-managed configuration/consent path, shared setup lock,
  version/replay floors, directory ACL preservation, durable journals and
  known-backup recovery remain in use. Fixtures exercise missing-service
  application/recovery, binding changes, profile races, changed service state,
  incomplete packages, config rejection and existing service-path regressions.

The final suite and build run use only isolated fixtures and read-only channel
validation, not real enrollment or application. Generated binaries, test output,
profiles and backups remain excluded from Git. The default distribution has no
public verification key/feed and stays disabled; the local wrapper is unsigned.

### Complete-layout provenance and local package preparation

A read-only static audit found 104 records in the pinned official 1.5.0
executable and compared their decompressed bytes to the installed files. All
104 SHA-256 comparisons matched. Together with generated `meta.toml` and the
exact local Windows broker, they form the reviewed 106-file profile. Two stale
1.4.x overlay SVGs, `auth-azure.svg` and `keyboard.svg`, are excluded from new
packages; their presence in an installed tree is not official 1.5.0 provenance.

`Build-UpdatePackage.ps1` prepares only a local ZIP, unsigned binary metadata
fragment, extracted read-back tree and receipt. It never signs, publishes,
installs or executes the package. Unknown source extras fail validation; the
reviewed obsolete assets and local uninstall shortcut are omitted only from
the package copy, without modifying the source. The package's Windows broker
must match the target OS, so a different broker build is rejected.

The actual 1.5.0+68 source snapshot passed the producer and native extraction
round trip with the final 1.1.1 verifier: **106 files, 34,692,042 ZIP bytes**.
ZIP SHA-256: `deec23e4db29f7a89d665712da4786d70b239f319fcc1b797b2efd1145d7bef3`.
Prepared and extracted directory fingerprints matched; all root PE/catalog
publisher checks and the exact local broker check passed. The original source
directory fingerprint was unchanged. The local receipt and unsigned fragment
are retained with verification artifacts, excluded from Git. This package was
not signed, published, installed or assigned a fabricated newer version.
Same-version local preparation is not a live upgrade test.

The application adapter deliberately preserves Windows installation registry
entries and shortcuts. Its actual EXE version, not Windows Apps/Uninstall
`DisplayVersion`, governs update verification and floors. A future file-only
update may leave that display label stale; no registry transaction was added
to this source increment.

## Earlier approved laptop test (before this source increment)

The separately authorized laptop test completed the following:

- Privately backed up and hash-verified 172 application/profile files, then ran
  the official RustDesk 1.5.0 update. The resulting client is **1.5.0+68** with
  a valid PURSLANE signature and Windows registration version 1.5.0.
- All 12 profile configuration files, including the identity configuration,
  remained byte-identical. No RustDesk service was created and no support
  session was opened. The existing printer driver remained installed; a real
  printing test was not performed.
- The original engine suite passed **42/42**, including the actual installed
  publisher check that had rejected the old revoked 1.4.3 client.
- Created the specifically approved disposable, non-exportable RSA-3072
  signing-only key in the original Windows user's local CNG store. Only public
  parameters and signed public metadata left that store; no private key was
  exported or published.
- A separate 1.1.0 laptop-test build authenticated byte-identical metadata from
  the approved HTTPS test branch and its actual controller staged that
  envelope. The production verifier rejected a corrupted signature. No
  application ZIP was downloaded, configuration applied or updater enrollment
  created; automatic checking remained unselected.

That metadata-only acceptance does not demonstrate a live app-only binary
upgrade, operational health, driver compatibility or power-loss recovery.
The existing canary expires **31 October 2026 at 22:15:21 Africa/Johannesburg
(20:15:21 UTC)**. This source increment does not renew it or authorize another
publication; expired metadata must remain rejected.

## Earlier 1.1.0 source verification

Before the separately approved laptop update, the 1.1.0 updater suite passed
**80/80** isolated fixtures (16 protocol, 17 package, 22 coordinator,
22 filesystem and 3 handoff checks), and complete C# 5 compilation treated
warnings as errors. Its default build's trust probe returned **2 (disabled)**.
Default 1.1.0 output SHA-256:
`8add8eeab1e65bedab3cbb0125cbd7f98c7bab5262930c872ba86d7fd5d708f4`.
The later signed-feed laptop test used a separate provisioned local build;
these default-build results are not evidence of a production update channel.

The then-current original engine result was **41 passed, 1 failed**, because
installed 1.4.3+60 had a revoked certificate. It was correctly rejected; it is
no longer the current installed client after the separately approved update.
The historical certificate-fix evidence below retains those observations.

## Remaining live acceptance

Service-free app-only enrollment and application have not been performed on a
real PC. Their first application needs a genuinely newer approved official
version, a complete target-compatible signed package, separately reviewed
protected enrollment and controlled acceptance. Same-version reapplication or
invented version metadata is not a valid upgrade test.

Live acceptance still needs operational support/driver checks, interrupted
transaction recovery and session/offline behavior on the approved target.
Production signer/feed decisions, startup or unattended installation, releases
and customer rollout remain separate from this source task. See
[`UPDATER.md`](UPDATER.md) for the exact modes and activation requirements.

## Preserved 1.0.2 certificate-fix evidence

- The two new recovery regressions failed against 1.0.1 before the fix:
  transient `0x800B010A` was immediately rejected, and persistent failure did
  not receive a bounded retry or actionable recovery instructions.
- All four new recovery/security cases pass with 1.0.2: transient success,
  persistent rejection, final trust-error reporting and publisher rejection.
  Existing non-chain-error cases also assert that they are never retried.
- Connected full-suite run: **41 passed, 1 failed**. The failure is the real
  installed RustDesk 1.4.3+60 client: Windows returns `0x800B010C`, and
  `Get-AuthenticodeSignature` reports "A certificate was explicitly revoked
  by its issuer." Setup correctly rejects that client; it was not altered.
  Installed signer thumbprint: `A1F862FCF587F99320D14368186428E8E3489E8F`.
- The official bundled RustDesk 1.5.0 passes its pinned SHA-256 and native
  publisher trust checks with certificate retrieval available. SHA-256:
  `8555777215510d83d2d61c9dc984e4fcc838bd7e79f9d18a42585431f5e8bb47`.
- With network retrieval blocked, the same bundled file reproduces the
  screenshot's `0x800B010A` and remains rejected after the retry. This verifies
  fail-closed behavior, not an offline-installation fix. The customer PC's
  exact network/trust cause remains unconfirmed.
- 1.0.2 builds with compiler warnings treated as errors. Output SHA-256:
  `8a786c14eb4168427dfaa00c15a084ddb99d604995513a373e06bc1d0441d756`.
  The local receipt is `dist/build-receipt.json`; generated binaries and test
  output are excluded from Git. The Touch365 wrapper remains unsigned.
- Independent source/security reviews found no trust-policy regression.
  No customer provisioning, preview or remote-access session was started.

## Earlier 1.0.1 history (not acceptance of 1.0.2)

- Customer reported version 1.0.0 stopped with "The RustDesk publisher signature
  could not be verified. Contact Touch365." The old message omitted the native
  error and stage, so the exact customer-machine cause is unknown.
- Version 1.0.1 permits normal Windows certificate retrieval instead of forcing
  cached-only retrieval. A controlled cold-cache fixture failed with the old
  flag and passed with the new policy. Actual signed and unsigned-file checks
  also passed locally. This does not prove the original customer cause.
- Version 1.0.1 now reports stage, filename and exact Windows trust status on
  failure. User reported: "Setup completes and RustDesk opens" on the test PC.
  The user subsequently confirmed "1.0.1 works well" and requested publication
  for technicians. This is user-reported acceptance; the detailed per-feature
  matrix below has not been independently completed on the new test PC.

## Required setup customer-PC acceptance

These checks cover ordinary Touch365 Setup and its configured support policy,
not the app-only updater's profile-preserving mode.

1. Open the setup EXE on a Windows x64 test PC and approve UAC.
2. Confirm setup completes and RustDesk opens without entering any settings.
3. Confirm Ready and the device ID; connect from the configured technician PC.
4. Reject the first request: no viewing or control must be granted.
5. Accept a fresh request: verify display, mouse and keyboard in blank Notepad.
6. Disconnect/reconnect: another customer click must be required.
7. Copy fresh harmless text in both directions.
8. Copy `clipboard-test/TECHNICIAN-TO-CUSTOMER.txt` to the customer, rename the
   copy `CUSTOMER-TO-TECHNICIAN.txt`, copy it back, compare its SHA-256.
9. Quit RustDesk and rerun setup: preserve ID, apply same settings, reopen.
10. Reboot the test PC, open the shortcut, repeat an approved connection.

Also pending: compatible existing EXE/MSI deployments, standard-user plus
different-admin UAC, and locked/logon-screen behavior on real machines.
The prior manual-configuration server test does not prove this installer path.
