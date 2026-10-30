# Touch365 Update Builder

Windows tool for creating Touch365 update EXEs with a Project Plan PDF and one or two destination folders.

## Remote support for technicians

**[Download Touch365 Remote Support 1.0.1 for Windows](https://github.com/mohammedbathia/touch365-update-builder/releases/download/remote-support-v1.0.1/Touch365-Remote-Support-Setup-1.0.1.exe)**

Customers open this setup, approve the Windows administrator prompt, then share their RustDesk ID and accept the technician's connection. Server details and customer-approved support settings are configured automatically. Future sessions use **Touch365 Remote Support** in the Start menu.

[Release notes, technician guide, checksums and source](https://github.com/mohammedbathia/touch365-update-builder/releases/tag/remote-support-v1.0.1).

The [Remote Support source](customer-setup/README.md) includes the 1.1.1
candidate's authenticated, approval-gated application/configuration updater
and the preserved certificate-chain retry. Service-free app-only enrollment
preserves the Windows user's identity, servers and permissions; it never applies
configuration or installs a background service. The updater is disabled until an
approved public signing trust/feed is provisioned. See the
[updater design and activation prerequisites](customer-setup/UPDATER.md).
This is a source update; the published 1.0.1 installer has not been replaced.

## Update Builder

Download the installer from the [latest release](https://github.com/mohammedbathia/touch365-update-builder/releases/latest). Install it once on each team PC. The builder checks for newer releases automatically when it opens; the header also includes **Check for updates**. When a newer version appears, choose **Install** to download and verify its installer. The builder saves your draft and closes before setup replaces its files.

The release version you enter for a customer update is separate from the builder's own version, which appears in the header.

Requires Windows with .NET Framework 4.5 or later. The installer includes the NSIS compiler and PDF generator. Project Plan PDFs open with the customer's normal PDF viewer.

This repository distributes Touch365 tool installers and their checksums. Team drafts and customer update ZIPs are not stored here.
