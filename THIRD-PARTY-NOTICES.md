# Third-party notices

VFS365 is Copyright (C) JSolve B.V. and free software under the GNU General Public License version 3 ([LICENSE](LICENSE)).

It includes or ships with:

| Component | Copyright | Licence | How |
|---|---|---|---|
| WinFsp - Windows File System Proxy, https://github.com/winfsp/winfsp | Copyright (C) Bill Zissimopoulos | GPLv3 with the FLOSS exception | The setup installs the unmodified WinFsp installer; VFS365 links to its DLL through the winfsp.net binding |
| .NET runtime and libraries, https://github.com/dotnet/runtime | Copyright (c) .NET Foundation and Contributors | MIT | Included (self-contained), with the System.Security.Cryptography.ProtectedData and Microsoft.Win32.SystemEvents packages from the same repository |
| WiX Toolset Burn engine, standard bootstrapper and Util custom actions (WixQuietExec), https://github.com/wixtoolset/wix | Copyright (c) .NET Foundation and contributors | MS-RL | In the setup programs and the MSIs |

Sign-in uses Windows' own Web Account Manager (WAM), called directly through Windows' WinRT interfaces; nothing of it ships with VFS365.
