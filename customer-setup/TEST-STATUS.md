# Verification status — 28 October 2026

Current candidate: Touch365 Remote Support Setup 1.0.1.

## Completed

- Source review against official RustDesk 1.4.3 and 1.5.0 settings/installer code.
- Bundled 1.5.0 SHA-256 and actual publisher signature verification.
- 38 automated tests passed, including configuration, rollback, quoting,
  scoped TOML, local-file edits, and bounded installer/process waits.
- Setup preview inspected; no provisioning was triggered by preview.
- Final candidate built with compiler warnings treated as errors. Its exact
  digest is recorded in `dist/build-receipt.json`.

## In progress

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
