# Contributing

Thanks for helping. VFS365 is maintained by JSolve B.V. Bug reports, fixes and well-scoped improvements are welcome.

## Before you start

- Bugs and ideas: open an [issue](../../issues) with the templates. For anything larger than a fix, open an issue first so we can agree on the approach before you spend time on it.
- Questions about your own deployment are support, not issues: see [SUPPORT.md](SUPPORT.md).
- Security problems: never in a public issue; see [SECURITY.md](SECURITY.md).

## Building and testing

- Windows 10 or 11, the .NET 10 SDK, and PowerShell 7. Running the drive also needs [WinFsp](https://winfsp.dev); building and the tests don't.
- `./build.ps1 test` builds and runs the tests. `./build.ps1 np` builds the C network provider (it fetches a pinned Zig).
- `./build.ps1 run <command>` runs the console tool against your own test tenant. Put tenant and app IDs in `vfs365.local.json` (git-ignored), never in code.
- Test against a test tenant and a test folder, never production data.

## Pull requests

- One change per pull request, with tests for engine changes (`tests/`, using the in-memory `FakeDriveApi`).
- Add a line to `CHANGELOG.md` under the next version for anything users notice.
- Keep the rules in [CLAUDE.md](CLAUDE.md): delegated permissions only, public APIs only, the data safety rules in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), and no tenant-specific values.
- Dependencies must be under an OSI-approved licence compatible with GPLv3 and get a line in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). No proprietary components, ever: WinFsp is bundled under its FLOSS exception, which depends on that.
- Style: match the code around your change; concise comments that say what and how, and why only where it isn't obvious.

CI builds and tests every pull request. A maintainer reviews it; changes that don't fit the product's direction may be declined, even when they are good code.

## Licence of contributions

VFS365 is licensed under the GNU GPL version 3. By submitting a pull request you confirm that you wrote the change or have the right to submit it, and that it is licensed under the GPLv3 like the rest of the project.

## Releases

Maintainers release by raising `VERSION` on `main` (`./build.ps1 bump`) together with its `CHANGELOG.md` entry. GitHub Actions then builds the setup programs and MSIs and publishes the release.
