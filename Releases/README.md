# Releases

`./build.ps1 release` raises the patch number in `VERSION`, runs the tests and writes `release/<version>/`: per-architecture setup programs (WinFsp plus VFS365), the MSIs, the unmodified WinFsp MSI (pinned version and SHA256 in `build.ps1`), policy templates, `LICENSE`, `THIRD-PARTY-NOTICES.md`, the deployment and app registration guides and `SHA256SUMS.txt`. `-KeepVersion` rebuilds the current version.

Release folders are not in git; only this file is. VFS365 is GPLv3: whoever receives a release must be able to get the source (the public repository).
