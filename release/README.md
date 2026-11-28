# Releases

Published releases are on the repository's GitHub Releases page. GitHub Actions builds one when `VERSION` on `main` names a version without a release (`.github/workflows/release.yml`), as a pre-release; the release notes are that version's `CHANGELOG.md` entry. It publishes the setups (with the MSIs inside), the policy zip, the dashboard and `SHA256SUMS.txt`.

To release: `./build.ps1 bump` (or edit `VERSION`), add a `## <version>` entry to `CHANGELOG.md`, and push both to `main`. Test the pre-release's downloads, then Edit release on GitHub: clear "Set as a pre-release" and tick "Set as the latest release". Until then `releases/latest` (the monitoring template's dashboard link) still points to the previous release. If the workflow failed after the version push, fix it and start it again with Run workflow (Actions, Release).

`./build.ps1 release` builds the same locally into `release/<version>/`: per-architecture setup programs (WinFsp plus VFS365), the MSIs, the unmodified WinFsp MSI (pinned version and SHA256 in `build.ps1`), the policy templates (also as a zip), `LICENSE`, `THIRD-PARTY-NOTICES.md`, the deployment and app registration guides and `SHA256SUMS.txt`.

Release folders are not in git; only this file is. VFS365 is GPLv3: whoever receives a release must be able to get the source, which is this repository.
