# Security

## Reporting a vulnerability

Don't open a public issue. Report it privately:

- through GitHub: the repository's **Security** tab, **Report a vulnerability**, or
- by e-mail to hello@jsolve.nl with "VFS365 security" in the subject.

Include the version, what an attacker can do, and the steps to reproduce. We confirm receipt within five working days, keep you informed, and credit you in the release notes unless you prefer otherwise.

## Supported versions

Fixes go into the latest release. Update to it before reporting, if you can.

## Verifying a release

Releases from 0.4.2 on are Authenticode signed with JSolve B.V.'s certificate through Azure Artifact Signing: the setups, the MSIs, and VFS365's programs, DLLs and `vfs365np.dll` inside them. Check a file with `Get-AuthenticodeSignature <file>`: the status must be `Valid` and the signer JSolve B.V. The only unsigned file inside is `winfsp-msil.dll`, which WinFsp's NuGet package ships unsigned and VFS365 ships unmodified.

VFS365's release files also carry a build provenance attestation and are listed in `SHA256SUMS.txt`. Check that a file was built by the release workflow from `main`:

```
gh attestation verify <file> --repo jflieben/VFS365 --signer-workflow jflieben/VFS365/.github/workflows/release.yml --source-ref refs/heads/main
```

The bundled WinFsp MSI is upstream's: it carries its author's signature and its SHA-256 is pinned in `build.ps1`.

## Known limits

- While a user's drive isn't running (before it starts, after a crash or sign-out, after an uninstall), `\\VFS365\<user>` is an ordinary server name to Windows: SMB and WebDAV look for a server called VFS365 on the network, and whoever answers gets the user's NTLM response and files that apps save to remembered paths. VFS365's network provider never asks the network, and the agent warns at the start when the name resolves, but the fix is on the network side: turn off LLMNR and NetBIOS name resolution, create the DNS record `VFS365` yourself (pointing to `127.0.0.1`), and block NTLM for SMB where you can. See [The name VFS365 on the network](docs/DEPLOYMENT.md#the-name-vfs365-on-the-network).
- On a multi-session host, WinFsp lets any user mount a volume under any UNC prefix. VFS365 refuses to start, warns and reports when another user's volume holds `\\VFS365\<user>`, and its network provider resolves typed paths only to a volume the caller owns. A user who deliberately creates a volume that claims someone else as its owner (WinFsp's sample file system allows that) is not detected.
- Monitoring (optional): every user on a PC can read the devices' SAS from the policy. The agent refuses a SAS that can do more than add table rows, but rows are not signed, so anyone with it can add rows that look like a device's. Anyone with the dashboard's SAS can read every row: keep it short-lived, and open a `#connect=` link only on a computer you control or in a private window, as it stays in the browser history.

## WinFsp

VFS365 bundles [WinFsp](https://github.com/winfsp/winfsp) unmodified. Vulnerabilities in WinFsp itself go to the WinFsp project; we update the bundled version when it publishes a fix.
