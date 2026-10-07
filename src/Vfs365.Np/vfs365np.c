// VFS365 network provider (vfs365np.dll): lets the Windows shell resolve typed \\VFS365\<share> paths, as in Explorer's
// address bar, file dialogs and Run. WinFsp's own provider doesn't implement NPGetResourceInformation, which the shell
// needs for that. File I/O doesn't pass through here: it goes through WinFsp's redirector.
// MPR loads this DLL into every process that resolves network names, so anything not \\VFS365 is declined at once.
// Built with Zig (zig cc) by build.ps1, without a C runtime: kernel32 only.

#include <windows.h>
#include <winnetwk.h>

// From npapi.h, not included: its declarations lack dllexport
#define WNNC_SPEC_VERSION 0x00000001
#define WNNC_SPEC_VERSION51 0x00050001
#define WNNC_NET_TYPE 0x00000002
#define WNNC_DRIVER_VERSION 0x00000003
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

    // The volume's root answers through WinFsp only while it is mounted and the caller may open it
    WCHAR root[MAX_PATH];
    if (shareEnd + 2 > MAX_PATH)
        return WN_BAD_NETNAME;
    lstrcpynW(root, name, shareEnd + 1);
    root[shareEnd] = L'\\';
    root[shareEnd + 1] = L'\0';
    DWORD attributes = GetFileAttributesW(root);
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY))
        return WN_BAD_NETNAME;

    BOOL below = shareEnd + 1 < length;
    return Fill(buffer, size, name, shareEnd, RESOURCEDISPLAYTYPE_SHARE, RESOURCEUSAGE_CONNECTABLE, RESOURCETYPE_DISK,
        below ? name + shareEnd : NULL, below ? length - shareEnd : 0, system);
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
