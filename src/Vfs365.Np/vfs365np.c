// VFS365 network provider (vfs365np.dll): lets the Windows shell resolve typed \\VFS365\<share> paths, as in Explorer's
// address bar, file dialogs and Run. WinFsp's own provider doesn't implement NPGetResourceInformation, which the shell
// needs for that. It also names the drive letters the agent gives folders inside the volume (policy DriveMappings), which
// WinFsp's provider doesn't know: without that, Explorer shows them as disconnected. File I/O doesn't pass through here: it
// goes through WinFsp's redirector.
// MPR loads this DLL into every process that resolves network names, so anything not \\VFS365 is declined at once.
// Built with Zig (zig cc) by build.ps1, without a C runtime: kernel32 and advapi32 only.

#include <windows.h>
#include <winnetwk.h>

// From npapi.h, not included: its declarations lack dllexport
#define WNNC_SPEC_VERSION 0x00000001
#define WNNC_SPEC_VERSION51 0x00050001
#define WNNC_NET_TYPE 0x00000002
#define WNNC_DRIVER_VERSION 0x00000003
#define WNNC_CONNECTION 0x00000006
#define WNNC_CON_GETCONNECTIONS 0x00000004
#define WNNC_DIALOG 0x00000008
#define WNNC_START 0x0000000C
#define WNNC_DLG_GETRESOURCEPARENT 0x00000200
#define WNNC_DLG_GETRESOURCEINFORMATION 0x00000800

// Outside Microsoft's WNNC_NET_* list, which ends far below it
#define NET_TYPE 0x7F360000

// Exported undecorated through vfs365np.def (32-bit WINAPI names would otherwise carry @n)
#define EXPORT

static const WCHAR Server[] = L"\\\\VFS365";
#define SERVER_LENGTH 8

// Must match the Name value of the provider's registry key (installer/Package.wxs)
static const WCHAR Provider[] = L"VFS365";
#define PROVIDER_LENGTH 6

// WinFsp's UNC provider as MUP names it: a path below it reaches WinFsp alone
static const WCHAR WinFspMup[] = L"\\\\?\\GLOBALROOT\\Device\\WinFsp.Mup";
#define WINFSP_MUP_LENGTH ((int)(sizeof(WinFspMup) / sizeof(WCHAR)) - 1)

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
        DisableThreadLibraryCalls(instance);
    return TRUE;
}

// True for \\VFS365, \\VFS365\ and \\VFS365\<anything>
static BOOL IsOurs(LPCWSTR name, int length)
{
    return length >= SERVER_LENGTH &&
        CompareStringOrdinal(name, SERVER_LENGTH, Server, SERVER_LENGTH, TRUE) == CSTR_EQUAL &&
        (length == SERVER_LENGTH || name[SERVER_LENGTH] == L'\\');
}

// True when the user the calling thread runs as (impersonated or not) owns the volume root. UNC prefixes are machine-wide, so
// another user could mount \\VFS365\<share> before its user's agent does; that volume is not vouched for.
static BOOL OwnedByCaller(LPCWSTR root)
{
    // Self-relative descriptor with only the owner, and TOKEN_USER: each at most a header plus one SID
    ULONGLONG descriptor[(SECURITY_DESCRIPTOR_MIN_LENGTH + SECURITY_MAX_SID_SIZE) / sizeof(ULONGLONG) + 1];
    ULONGLONG user[(sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE) / sizeof(ULONGLONG) + 1];
    DWORD length = 0;
    if (!GetFileSecurityW(root, OWNER_SECURITY_INFORMATION, (PSECURITY_DESCRIPTOR)descriptor, sizeof(descriptor), &length))
        return FALSE;
    PSID owner = NULL;
    BOOL defaulted = FALSE;
    if (!GetSecurityDescriptorOwner((PSECURITY_DESCRIPTOR)descriptor, &owner, &defaulted) || !owner || !IsValidSid(owner))
        return FALSE;

    HANDLE token = NULL;
    if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &token))
    {
        if (GetLastError() != ERROR_NO_TOKEN || !OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
            return FALSE;
    }
    BOOL ok = GetTokenInformation(token, TokenUser, user, sizeof(user), &length);
    CloseHandle(token);
    return ok && EqualSid(((TOKEN_USER *)user)->User.Sid, owner);
}

// Writes WinFsp's provider path into path (capacity characters) and returns its length. Since 2.0 WinFsp installs side by side and
// the provider is WinFsp.Mup+<id>, with the id from its SxsDir (...\SxS\sxs.<id>\).
static int WinFspProvider(WCHAR *path, int capacity)
{
    int length = WINFSP_MUP_LENGTH;
    lstrcpynW(path, WinFspMup, length + 1);
    HKEY key;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WinFsp", 0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &key) != ERROR_SUCCESS)
        return length;
    WCHAR dir[MAX_PATH + 1];
    DWORD type = 0, bytes = MAX_PATH * sizeof(WCHAR);
    LONG read = RegQueryValueExW(key, L"SxsDir", NULL, &type, (LPBYTE)dir, &bytes);
    RegCloseKey(key);
    if (read != ERROR_SUCCESS || type != REG_SZ)
        return length;
    dir[bytes / sizeof(WCHAR)] = L'\0';
    int end = lstrlenW(dir);
    while (end > 0 && dir[end - 1] == L'\\')
        end--;
    int start = end;
    while (start > 0 && dir[start - 1] != L'\\')
        start--;
    int id = end - start - 4;
    if (id <= 0 || id > 32 || length + 1 + id >= capacity || CompareStringOrdinal(dir + start, 4, L"sxs.", 4, TRUE) != CSTR_EQUAL)
        return length;
    path[length] = L'+';
    lstrcpynW(path + length + 1, dir + start + 4, id + 1);
    return length + 1 + id;
}

// A NETRESOURCEW followed by its strings. remote may be null (the network itself); system is the path below a share.
static DWORD Fill(LPVOID buffer, LPDWORD size, LPCWSTR remote, int remoteLength, DWORD displayType, DWORD usage, DWORD type,
    LPCWSTR system, int systemLength, LPWSTR *systemOut)
{
    DWORD chars = PROVIDER_LENGTH + 1 + (remote ? remoteLength + 1 : 0) + (system ? systemLength + 1 : 0);
    DWORD needed = sizeof(NETRESOURCEW) + chars * sizeof(WCHAR);
    if (!buffer || *size < needed)
    {
        *size = needed;
        return WN_MORE_DATA;
    }

    NETRESOURCEW *resource = (NETRESOURCEW *)buffer;
    WCHAR *text = (WCHAR *)(resource + 1);
    resource->dwScope = RESOURCE_GLOBALNET;
    resource->dwType = type;
    resource->dwDisplayType = displayType;
    resource->dwUsage = usage;
    resource->lpLocalName = NULL;
    resource->lpComment = NULL;
    resource->lpRemoteName = NULL;
    if (remote)
    {
        resource->lpRemoteName = text;
        lstrcpynW(text, remote, remoteLength + 1);
        text += remoteLength + 1;
    }
    resource->lpProvider = text;
    lstrcpynW(text, Provider, PROVIDER_LENGTH + 1);
    text += PROVIDER_LENGTH + 1;
    if (systemOut)
    {
        *systemOut = NULL;
        if (system)
        {
            *systemOut = text;
            lstrcpynW(text, system, systemLength + 1);
        }
    }
    return WN_SUCCESS;
}

EXPORT DWORD WINAPI NPGetCaps(DWORD index)
{
    switch (index)
    {
    case WNNC_SPEC_VERSION:
        return WNNC_SPEC_VERSION51;
    case WNNC_NET_TYPE:
        return NET_TYPE;
    case WNNC_DRIVER_VERSION:
        return 1;
    case WNNC_CONNECTION:
        return WNNC_CON_GETCONNECTIONS;
    case WNNC_DIALOG:
        return WNNC_DLG_GETRESOURCEINFORMATION | WNNC_DLG_GETRESOURCEPARENT;
    case WNNC_START:
        return 1;
    default:
        return 0;
    }
}

// \\VFS365[\] is the server; \\VFS365\<share>[\path] is a share when that VFS365 volume is mounted, with \path as the system part
EXPORT DWORD WINAPI NPGetResourceInformation(LPNETRESOURCEW resource, LPVOID buffer, LPDWORD size, LPWSTR *system)
{
    if (!resource || !resource->lpRemoteName || !size)
        return WN_BAD_NETNAME;
    LPCWSTR name = resource->lpRemoteName;
    int length = lstrlenW(name);
    if (!IsOurs(name, length))
        return WN_BAD_NETNAME;

    int shareEnd = SERVER_LENGTH + 1;
    while (shareEnd < length && name[shareEnd] != L'\\')
        shareEnd++;
    if (shareEnd <= SERVER_LENGTH + 1)
        return Fill(buffer, size, Server, SERVER_LENGTH, RESOURCEDISPLAYTYPE_SERVER, RESOURCEUSAGE_CONTAINER, RESOURCETYPE_ANY,
            NULL, 0, system);

    // The volume's root, asked of WinFsp directly: it answers only while the volume is mounted and the caller may open it, and it must
    // be the caller's own. Never the UNC path itself: when the volume isn't mounted, that falls through to SMB and WebDAV, which look
    // for a server named VFS365 on the network and sign in to whoever answers.
    WCHAR root[MAX_PATH];
    int prefix = WinFspProvider(root, MAX_PATH - 2);
    int share = shareEnd - 1; // \VFS365\<share>: the name without its first backslash
    if (prefix + share + 2 > MAX_PATH)
        return WN_BAD_NETNAME;
    lstrcpynW(root + prefix, name + 1, share + 1);
    root[prefix + share] = L'\\';
    root[prefix + share + 1] = L'\0';
    DWORD attributes = GetFileAttributesW(root);
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY) || !OwnedByCaller(root))
        return WN_BAD_NETNAME;

    BOOL below = shareEnd + 1 < length;
    return Fill(buffer, size, name, shareEnd, RESOURCEDISPLAYTYPE_SHARE, RESOURCEUSAGE_CONNECTABLE, RESOURCETYPE_DISK,
        below ? name + shareEnd : NULL, below ? length - shareEnd : 0, system);
}

// The agent's drive letters for folders inside the volume are DOS devices of the user's sign-in that point to \??\UNC\VFS365\...:
// such a letter is connected to \\VFS365\... (bufferLength in characters). WinFsp's provider answers for the drive's own letter.
EXPORT DWORD WINAPI NPGetConnection(LPWSTR localName, LPWSTR remoteName, LPDWORD bufferLength)
{
    static const WCHAR Device[] = L"\\??\\UNC\\VFS365\\";
    const int deviceLength = (int)(sizeof(Device) / sizeof(WCHAR)) - 1;
    if (!localName || !bufferLength || lstrlenW(localName) != 2 || localName[1] != L':')
        return WN_NOT_CONNECTED;
    WCHAR target[MAX_PATH + 16];
    DWORD read = QueryDosDeviceW(localName, target, MAX_PATH + 16);
    if (read == 0 || CompareStringOrdinal(target, deviceLength, Device, deviceLength, TRUE) != CSTR_EQUAL)
        return WN_NOT_CONNECTED;

    // \??\UNC\VFS365\x becomes \\VFS365\x: the device without its first 6 characters (\??\UN), C replaced by a backslash
    int length = lstrlenW(target) - 6;
    if (*bufferLength < (DWORD)length + 1)
    {
        *bufferLength = (DWORD)length + 1;
        return WN_MORE_DATA;
    }
    if (!remoteName)
        return WN_BAD_VALUE;
    lstrcpynW(remoteName, target + 6, length + 1);
    remoteName[0] = L'\\';
    return WN_SUCCESS;
}

// A share's parent is the server, the server's parent the network itself
EXPORT DWORD WINAPI NPGetResourceParent(LPNETRESOURCEW resource, LPVOID buffer, LPDWORD size)
{
    if (!resource || !resource->lpRemoteName || !size)
        return WN_BAD_NETNAME;
    LPCWSTR name = resource->lpRemoteName;
    int length = lstrlenW(name);
    if (!IsOurs(name, length))
        return WN_BAD_NETNAME;
    if (length > SERVER_LENGTH + 1)
        return Fill(buffer, size, Server, SERVER_LENGTH, RESOURCEDISPLAYTYPE_SERVER, RESOURCEUSAGE_CONTAINER, RESOURCETYPE_ANY,
            NULL, 0, NULL);
    return Fill(buffer, size, NULL, 0, RESOURCEDISPLAYTYPE_NETWORK, RESOURCEUSAGE_CONTAINER, RESOURCETYPE_ANY, NULL, 0, NULL);
}
