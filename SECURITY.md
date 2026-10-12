# Security

## Reporting a vulnerability

Don't open a public issue. Report it privately:

- through GitHub: the repository's **Security** tab, **Report a vulnerability**, or
- by e-mail to hello@jsolve.nl with "VFS365 security" in the subject.

Include the version, what an attacker can do, and the steps to reproduce. We confirm receipt within five working days, keep you informed, and credit you in the release notes unless you prefer otherwise.

## Supported versions

Fixes go into the latest release. Update to it before reporting, if you can.

## Verifying a release

Release files carry a build provenance attestation (`gh attestation verify <file> --repo jflieben/VFS365`) and are listed in `SHA256SUMS.txt`. The release workflow can Authenticode-sign them; see the header of `.github/workflows/release.yml`.

## Known limits

- On a multi-session host, WinFsp lets any user mount a volume under any UNC prefix. VFS365 refuses to start, warns and reports when another user's volume holds `\\VFS365\<user>`, and its network provider resolves typed paths only to a volume the caller owns. A user who deliberately creates a volume that claims someone else as its owner (WinFsp's sample file system allows that) is not detected.

## WinFsp

VFS365 bundles [WinFsp](https://github.com/winfsp/winfsp) unmodified. Vulnerabilities in WinFsp itself go to the WinFsp project; we update the bundled version when it publishes a fix.
