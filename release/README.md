# Releases

Published releases are on the repository's GitHub Releases page. GitHub Actions builds one when `VERSION` on `main` names a version without a release (`.github/workflows/release.yml`); the release notes are that version's `CHANGELOG.md` entry.

To release: `./build.ps1 bump` (or edit `VERSION`), add a `## <version>` entry to `CHANGELOG.md`, and push both to `main`.

`./build.ps1 release` builds the same locally into `release/<version>/`: per-architecture setup programs (WinFsp plus VFS365), the MSIs, the unmodified WinFsp MSI (pinned version and SHA256 in `build.ps1`), the policy templates (also as a zip), `LICENSE`, `THIRD-PARTY-NOTICES.md`, the deployment and app registration guides and `SHA256SUMS.txt`.

Release folders are not in git; only this file is. VFS365 is GPLv3: whoever receives a release must be able to get the source, which is this repository.
