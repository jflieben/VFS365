# Working on VFS365

Guidance for coding agents and contributors. Maintainers: when `private/` exists (it is not in git), read `private/CLAUDE.md` and `private/STATUS.md` first; they hold status, decisions, test notes and tenant details.

## Rules
- Style: concise plain Markdown and comments, no em dashes. Document what and how; add why only where it isn't obvious.
- Delegated Graph permissions only; public APIs only. Never app-only, never Sites.FullControl.All, never the OneDrive client's User-Agent.
- Items are tracked by drive ID + item ID (`ItemRef`), never by path.
- Data safety rules in docs/ARCHITECTURE.md are hard rules: never acknowledge a write that isn't durably queued, never delete server content except on an explicit user delete.
- Core and Graph stay platform neutral: no WinFsp, cfapi or MSAL types there.
- Writes in tests run against the in-memory `FakeDriveApi`. Live tests write only to a test folder in a test tenant, never to production data.
- No tenant-specific values in code or default config: tenant and SharePoint host are detected. Tenant and app IDs go in git-ignored `*.local.json` files. Only exception: `AgentSettings.DefaultClientId`, JSolve's public multi-tenant app.
- GPLv3 with WinFsp bundled under its FLOSS exception: no proprietary dependencies, ever; keep the WinFsp notice (`AgentControl.Notice`) in the UI and user docs; ship WinFsp only unmodified; new dependencies need an OSI licence and a line in THIRD-PARTY-NOTICES.md.
- Every user-visible change gets a CHANGELOG.md entry under the version in `VERSION`.

## Build
- `./build.ps1 test` (or `build`). It finds a .NET 10 SDK on PATH, in `$env:VFS365_DOTNET` or under `%LOCALAPPDATA%\dotnet-portable`.
- `./build.ps1 run <args>` builds and runs the console tool (without arguments it lists the commands). `./build.ps1 test` doesn't rebuild the console exe: run `build` before live tests. While mounted the exe is locked: only `unmount` works, rebuilds fail.
- Settings come from policy, then `vfs365.local.json` in the working directory (same names as in docs/DEPLOYMENT.md), or `--config <file>`.
- Running the drive needs WinFsp installed; building doesn't.
- `src/Vfs365.Np` is C (the network provider), built by `./build.ps1 np` and `release` with a pinned portable Zig (fetched and hash-checked on first use). No C runtime: kernel32 only.
- Versions: `VERSION` is the only place to change it; assemblies, MSIs and setups take it from there. `./build.ps1 bump` raises the patch number. `./build.ps1 release` builds `release/<version>/` locally. Pushing a new version to `main` makes GitHub Actions publish a release (`.github/workflows/release.yml`).
- Layout: src/ (Core, Graph, Drive.WinFsp, Agent library, Cli = vfs365.exe, Background = vfs365-agent.exe, Np = vfs365np.dll), installer/ (WiX 5), policy/ (ADMX), assets/ (icons from tools/), tests/, docs/.
