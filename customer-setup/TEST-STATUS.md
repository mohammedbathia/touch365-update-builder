# Verification status - 30 October 2026

Current source candidate: Touch365 Remote Support Setup 1.0.2.
Source update only: no release publication, deployment or installation performed.

## Candidate results

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

## Required customer-PC acceptance

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
