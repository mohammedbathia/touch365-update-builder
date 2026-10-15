# Touch365 Update Builder

Windows tool for creating Touch365 update EXEs with a Project Plan PDF and one or two destination folders.

Download the installer from the [latest release](https://github.com/mohammedbathia/touch365-update-builder/releases/latest). Install it once on each team PC. The builder checks for newer releases automatically when it opens; the header also includes **Check for updates**. When a newer version appears, choose **Install** to download and verify its installer. The builder saves your draft and closes before setup replaces its files.

The release version you enter for a customer update is separate from the builder's own version, which appears in the header.

Requires Windows with .NET Framework 4.5 or later. The installer includes the NSIS compiler and PDF generator. Project Plan PDFs open with the customer's normal PDF viewer.

This repository distributes builder installers and their checksums. Team drafts and customer update ZIPs are not stored here.
