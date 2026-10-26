// VFS365 shell extension (vfs365shell.dll):
// - the VFS365 submenu in Windows 11's context menu (IExplorerCommand), registered by the sparse package JSolve.VFS365.Shell
//   (package\AppxManifest.xml) and shown only on items of the volume;
// - Explorer's overlay icons (IShellIconOverlayIdentifier, five classes), registered by the MSI.
// Both answer from the agent's shared section (Local\VFS365.Shell.<user SID>) and never touch the volume: Explorer asks for every
// item it shows, also on other drives. Built with Zig (zig cc) by build.ps1, without a C runtime.

#define COBJMACROS
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <sddl.h>
#include <aclapi.h>

// Exported undecorated through vfs365shell.def (32-bit WINAPI names would otherwise carry @n)
#define EXPORT

// The section the agent writes (ShellSection), little-endian. Header (64 bytes): u32 magic, version, sequence (odd while written),
// agent process ID, flags, roots offset and count, strings offset and count, slots offset and count (a power of two), entries.
// Roots: u16 prefix and target lengths, then both in UTF-16, padded to 4 bytes; strings the same with key and value. Slots, 16 bytes:
// u64 FNV-1a hash of the volume path's uppercased key (0: empty), u32 state, u32 reserved; linear probing.
#define SECTION_SIZE 2097152u
#define MAGIC 0x35363356u
#define LAYOUT_VERSION 1u
#define FLAG_MENU 0x1u
#define FLAG_OVERLAYS 0x2u
#define SLOT_SIZE 16u

// Overlay states: the section's slot states, plus 5 for a file the section doesn't list
#define STATE_NONE 0
#define STATE_CACHED 1
#define STATE_UPLOADING 2
#define STATE_DOWNLOADING 3
#define STATE_PROBLEM 4
#define STATE_ONLINE_ONLY 5
#define STATES 5

// Longer paths get no menu or overlay; longer roots are ignored
#define MAX_CHARS 4096
#define MAX_ROOT_CHARS 1024
#define MAX_ROOTS 1024
#define MAX_STRINGS 4096
#define MAX_TITLE 256
// The section is retried, and the agent's process checked, at most this often
#define RETRY_MS 2000
#define ALIVE_MS 2000
// Explorer asks each of the five overlay classes in turn: one lookup serves them for this long
#define CACHE_MS 1000

static HINSTANCE Module;
static WCHAR ModulePath[MAX_PATH];
static WCHAR AgentExe[MAX_PATH];
static volatile LONG Locks; // references to objects and LockServer calls: DllCanUnloadNow

static SRWLOCK Lock = SRWLOCK_INIT; // everything below
static HANDLE Mapping;
static const BYTE *View;
static ULONGLONG RetryAt;
static WCHAR TestName[256]; // VfsShellTestSection
static DWORD AlivePid;
static BOOL AliveAnswer;
static ULONGLONG AliveUntil;
static WCHAR CachePath[MAX_CHARS];
static int CacheLength, CacheState;
static DWORD CacheDirectory;
static LONG CacheSequence;
static ULONGLONG CacheUntil;
// Work buffers of a lookup; static rather than on the stack, which a frame over 4 KB would have to probe
static WCHAR UpperPath[MAX_CHARS], KeyBuffer[MAX_CHARS + MAX_ROOT_CHARS], RootPrefix[MAX_ROOT_CHARS], RootRaw[MAX_ROOT_CHARS];

static const GUID IidUnknown = { 0x00000000, 0x0000, 0x0000, { 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 } };
static const GUID IidClassFactory = { 0x00000001, 0x0000, 0x0000, { 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 } };
static const GUID IidExplorerCommand = { 0xA08CE4D0, 0xFA25, 0x44AB, { 0xB5, 0x7C, 0xC7, 0xB1, 0xC3, 0x23, 0xE0, 0xB9 } };
static const GUID IidEnumExplorerCommand = { 0xA88826F8, 0x186F, 0x4987, { 0xAA, 0xDE, 0xEA, 0x0C, 0xEF, 0x8F, 0xBF, 0xE8 } };
static const GUID IidOverlayIdentifier = { 0x0C6C4200, 0xC589, 0x11D0, { 0x99, 0x9A, 0x00, 0xC0, 0x4F, 0xD6, 0x55, 0xE1 } };

// Class IDs: the menu's is in package\AppxManifest.xml, the overlays' in installer\Package.wxs
static const GUID Classes[1 + STATES] = {
    { 0xA7AED2F3, 0xB922, 0x46C7, { 0xBB, 0xAB, 0x64, 0xB9, 0x86, 0x06, 0x1C, 0x74 } }, // menu
    { 0x9DB55147, 0xCB02, 0x47F4, { 0xA3, 0x86, 0xE0, 0xEE, 0x3D, 0x55, 0x60, 0x21 } }, // Cached
    { 0xB7C851F1, 0x2B15, 0x49B3, { 0xB3, 0x0E, 0x76, 0x90, 0x11, 0xCF, 0x84, 0xD2 } }, // Uploading
    { 0x372835A3, 0x0CA9, 0x4E2C, { 0x93, 0x6E, 0x42, 0x82, 0x71, 0x49, 0xC0, 0x31 } }, // Downloading
    { 0x446CAE69, 0xA613, 0x4F61, { 0xB3, 0x3B, 0x68, 0x45, 0x71, 0x5E, 0xF0, 0xE8 } }, // Problem
    { 0x04668249, 0x3350, 0x4510, { 0x96, 0x29, 0x81, 0x44, 0x08, 0xAA, 0x98, 0xD3 } }, // OnlineOnly
};

// The verbs of ExplorerVerbs.Verbs, in its order
#define FILTER_NONE 0
#define FILTER_OFFICE 1
#define FILTER_CONFLICT 2
typedef struct
{
    LPCWSTR Name, Key, English;
    BOOL Folders;
    int Filter;
    DWORD Flags;
} VERB;
static const VERB Verbs[] = {
    { L"browser", L"Verb.browser", L"Open in browser", TRUE, FILTER_NONE, 0 },
    { L"desktop", L"Verb.desktop", L"Open in desktop app", FALSE, FILTER_OFFICE, 0 },
    { L"link", L"Verb.link", L"Copy link", TRUE, FILTER_NONE, 0 },
    { L"versions", L"Verb.versions", L"Version history", FALSE, FILTER_NONE, 0 },
    { L"share", L"Verb.share", L"Share in browser", TRUE, FILTER_NONE, 0 },
    { L"keep", L"Verb.keep", L"Keep this version", FALSE, FILTER_CONFLICT, ECF_SEPARATORBEFORE },
    { L"compare", L"Verb.compare", L"Compare with the original", FALSE, FILTER_CONFLICT, 0 },
};
#define VERB_COUNT ((int)(sizeof(Verbs) / sizeof(Verbs[0])))

// ExplorerVerbs.OfficeSchemes' extensions
static const LPCWSTR OfficeExtensions[] = {
    L".doc", L".docx", L".docm", L".dot", L".dotx", L".dotm",
    L".xls", L".xlsx", L".xlsm", L".xlsb", L".xlt", L".xltx", L".xltm",
    L".ppt", L".pptx", L".pptm", L".pps", L".ppsx", L".ppsm", L".pot", L".potx", L".potm",
};

// ExplorerVerbs.ConflictCopyFiles: the copies the engine makes. "* (conflict ????-??-?? ??????*)*", ? escaped (trigraphs).
static const LPCWSTR ConflictPatterns[] = {
    L"* (conflict \?\?\?\?-\?\?-\?\? \?\?\?\?\?\?*)*",
    L"* (recovered \?\?\?\?-\?\?-\?\? \?\?\?\?\?\?*)*",
};

// The compiler may emit calls to these for struct copies and initializers; there is no C runtime to provide them
void *memset(void *target, int value, size_t count)
{
    volatile BYTE *bytes = target;
    while (count--)
        *bytes++ = (BYTE)value;
    return target;
}

void *memcpy(void *target, const void *source, size_t count)
{
    volatile BYTE *to = target;
    const BYTE *from = source;
    while (count--)
        *to++ = *from++;
    return target;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        Module = instance;
        DisableThreadLibraryCalls(instance);
        DWORD length = GetModuleFileNameW(instance, ModulePath, MAX_PATH);
        if (length == 0 || length >= MAX_PATH)
            ModulePath[0] = L'\0';
        // vfs365-agent.exe next to this DLL
        int folder = lstrlenW(ModulePath);
        while (folder > 0 && ModulePath[folder - 1] != L'\\')
            folder--;
        static const WCHAR Agent[] = L"vfs365-agent.exe";
        if (folder > 0 && folder + (int)(sizeof(Agent) / sizeof(WCHAR)) <= MAX_PATH)
        {
            lstrcpynW(AgentExe, ModulePath, folder + 1);
            lstrcpynW(AgentExe + folder, Agent, MAX_PATH - folder);
        }
    }
    return TRUE;
}

// Little-endian reads from the section, which is untrusted and may change while read: callers check bounds first
static BOOL SameGuid(REFGUID a, REFGUID b)
{
    const BYTE *x = (const BYTE *)a, *y = (const BYTE *)b;
    for (int i = 0; i < (int)sizeof(GUID); i++)
    {
        if (x[i] != y[i])
            return FALSE;
    }
    return TRUE;
}

static DWORD U16(const BYTE *p) { return (DWORD)p[0] | (DWORD)p[1] << 8; }
static DWORD U32(const BYTE *p) { return U16(p) | U16(p + 2) << 16; }
static ULONGLONG U64(const BYTE *p) { return (ULONGLONG)U32(p) | (ULONGLONG)U32(p + 4) << 32; }
static LONG Sequence(const BYTE *view) { return (LONG)U32(view + 8); }

static BOOL Equal(const WCHAR *a, const WCHAR *b, int length)
{
    for (int i = 0; i < length; i++)
    {
        if (a[i] != b[i])
            return FALSE;
    }
    return TRUE;
}

static BOOL Upper(const WCHAR *source, int length, WCHAR *target)
{
    return length == 0 || LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, source, length, target, length, NULL, NULL, 0) == length;
}

// The process token's user, copied into sid (SECURITY_MAX_SID_SIZE bytes)
static BOOL CurrentUser(PSID sid)
{
    ULONGLONG user[(sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE) / sizeof(ULONGLONG) + 1];
    HANDLE token = NULL;
    DWORD length = 0;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        return FALSE;
    BOOL ok = GetTokenInformation(token, TokenUser, user, sizeof(user), &length);
    CloseHandle(token);
    return ok && CopySid(SECURITY_MAX_SID_SIZE, sid, ((TOKEN_USER *)user)->User.Sid);
}

static void CloseSection(void)
{
    if (View)
        UnmapViewOfFile(View);
    if (Mapping)
        CloseHandle(Mapping);
    View = NULL;
    Mapping = NULL;
    AlivePid = 0;
    AliveUntil = 0;
    CacheUntil = 0;
}

// The section, opened on first use and kept open; while it doesn't exist (or isn't the user's own), tried again at most every 2 s
static const BYTE *Section(void)
{
    if (View)
        return View;
    ULONGLONG now = GetTickCount64();
    if (now < RetryAt)
        return NULL;
    RetryAt = now + RETRY_MS;

    ULONGLONG sid[SECURITY_MAX_SID_SIZE / sizeof(ULONGLONG) + 1];
    if (!CurrentUser((PSID)sid))
        return NULL;
    WCHAR name[256];
    if (TestName[0])
        lstrcpynW(name, TestName, 256);
    else
    {
        LPWSTR text = NULL;
        if (!ConvertSidToStringSidW((PSID)sid, &text))
            return NULL;
        static const WCHAR Prefix[] = L"Local\\VFS365.Shell.";
        const int prefix = (int)(sizeof(Prefix) / sizeof(WCHAR)) - 1;
        BOOL fits = prefix + lstrlenW(text) < 256;
        if (fits)
        {
            lstrcpynW(name, Prefix, prefix + 1);
            lstrcpynW(name + prefix, text, 256 - prefix);
        }
        LocalFree(text);
        if (!fits)
            return NULL;
    }

    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | READ_CONTROL, FALSE, name);
    if (!mapping)
        return NULL;
    // Only the user's own section counts: another program of the session could have made one under that name first
    PSID owner = NULL;
    PSECURITY_DESCRIPTOR descriptor = NULL;
    BOOL own = GetSecurityInfo(mapping, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, NULL, NULL, NULL, &descriptor) == ERROR_SUCCESS &&
        owner && IsValidSid(owner) && EqualSid(owner, (PSID)sid);
    if (descriptor)
        LocalFree(descriptor);
    // A smaller section fails to map
    const BYTE *view = own ? MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, SECTION_SIZE) : NULL;
    if (!view)
    {
        CloseHandle(mapping);
        return NULL;
    }
    Mapping = mapping;
    View = view;
    return View;
}

// Whether the agent that wrote the section still runs; the answer is kept for 2 s
static BOOL AgentAlive(DWORD pid)
{
    ULONGLONG now = GetTickCount64();
    if (pid == AlivePid && now < AliveUntil)
        return AliveAnswer;
    HANDLE process = pid ? OpenProcess(SYNCHRONIZE, FALSE, pid) : NULL;
    AliveAnswer = process && WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
    if (process)
        CloseHandle(process);
    AlivePid = pid;
    AliveUntil = now + ALIVE_MS;
    return AliveAnswer;
}

// A path as Explorer gives it, uppercased into upper (MAX_CHARS) without \\?\ or \\?\UNC\; its length, or -1 for a path that can't
// be on the volume (too long, or with . or .. segments)
static int Normalize(LPCWSTR path, WCHAR *upper)
{
    if (!path)
        return -1;
    int length = lstrlenW(path), start = 0, at = 0;
    if (length >= 8 && CompareStringOrdinal(path, 8, L"\\\\?\\UNC\\", 8, TRUE) == CSTR_EQUAL)
    {
        upper[0] = upper[1] = L'\\';
        at = 2;
        start = 8;
    }
    else if (length >= 4 && Equal(path, L"\\\\?\\", 4))
        start = 4;
    if (length - start + at > MAX_CHARS - 1 || !Upper(path + start, length - start, upper + at))
        return -1;
    length = length - start + at;
    upper[length] = L'\0';
    for (int segment = 0; segment <= length; )
    {
        int end = segment;
        while (end < length && upper[end] != L'\\')
            end++;
        int size = end - segment;
        if ((size == 1 && upper[segment] == L'.') || (size == 2 && upper[segment] == L'.' && upper[segment + 1] == L'.'))
            return -1;
        segment = end + 1;
    }
    return length;
}

// What one pass over the section found for a path
typedef struct
{
    BOOL Valid;   // header and tables check out
    DWORD Pid, Flags;
    BOOL OnVolume;
    int State;    // slot state; STATE_NONE when the section doesn't list the item
} ANSWER;

// The volume path's key for upper (Normalize'd) from the section's roots into key (MAX_CHARS + MAX_ROOT_CHARS); its length, 0 when
// no root matches, -1 when the roots are damaged
static int Key(const BYTE *view, const WCHAR *upper, int length, WCHAR *key)
{
    DWORD offset = U32(view + 20), count = U32(view + 24);
    if (count > MAX_ROOTS)
        return -1;
    for (DWORD i = 0; i < count; i++)
    {
        if (offset > SECTION_SIZE - 4)
            return -1;
        int prefixChars = (int)U16(view + offset), targetChars = (int)U16(view + offset + 2);
        DWORD record = (4 + 2 * (DWORD)(prefixChars + targetChars) + 3) & ~3u;
        if (record > SECTION_SIZE - offset)
            return -1;
        const BYTE *text = view + offset + 4;
        offset += record;
        if (prefixChars < 2 || prefixChars > MAX_ROOT_CHARS || targetChars < 1 || targetChars > MAX_ROOT_CHARS)
            continue;
        WCHAR *prefix = RootPrefix, *raw = RootRaw;
        for (int c = 0; c < prefixChars; c++)
            raw[c] = (WCHAR)U16(text + 2 * c);
        if (!Upper(raw, prefixChars, prefix) || prefix[prefixChars - 1] != L'\\')
            continue;
        // Below the prefix, or the prefix itself without its trailing backslash
        int rest;
        if (length >= prefixChars && Equal(upper, prefix, prefixChars))
            rest = prefixChars;
        else if (length == prefixChars - 1 && Equal(upper, prefix, prefixChars - 1))
            rest = length;
        else
            continue;

        for (int c = 0; c < targetChars; c++)
            raw[c] = (WCHAR)U16(text + 2 * (prefixChars + c));
        if (raw[0] != L'\\' || raw[targetChars - 1] != L'\\' || !Upper(raw, targetChars, key))
            return -1;
        int size = targetChars;
        for (int c = rest; c < length; c++)
        {
            if (upper[c] != L'\\' || key[size - 1] != L'\\')
                key[size++] = upper[c];
        }
        while (size > 1 && key[size - 1] == L'\\')
            size--;
        return size;
    }
    return 0;
}

// FNV-1a 64 over the key's UTF-16 code units, low byte first; 0 is the empty slot's
static ULONGLONG Hash(const WCHAR *key, int length)
{
    ULONGLONG hash = 0xcbf29ce484222325ull;
    for (int i = 0; i < length; i++)
    {
        hash = (hash ^ (key[i] & 0xFF)) * 0x100000001b3ull;
        hash = (hash ^ (key[i] >> 8)) * 0x100000001b3ull;
    }
    return hash ? hash : 1;
}

// One pass over the section; FALSE when the writer was busy or changed it meanwhile (then try again)
static BOOL Pass(const BYTE *view, const WCHAR *upper, int length, BOOL wantState, ANSWER *answer, LONG *sequence)
{
    WCHAR *key = KeyBuffer;
    LONG before = Sequence(view);
    if (before & 1)
        return FALSE;
    MemoryBarrier();
    answer->Valid = answer->OnVolume = FALSE;
    answer->State = STATE_NONE;
    answer->Pid = U32(view + 12);
    answer->Flags = U32(view + 16);
    if (U32(view) == MAGIC && U32(view + 4) == LAYOUT_VERSION)
    {
        int size = Key(view, upper, length, key);
        answer->Valid = size >= 0;
        answer->OnVolume = size > 0;
        if (size > 0 && wantState)
        {
            DWORD offset = U32(view + 36), count = U32(view + 40);
            if (count == 0 || (count & (count - 1)) != 0 || offset > SECTION_SIZE || count > (SECTION_SIZE - offset) / SLOT_SIZE)
                answer->Valid = FALSE;
            else
            {
                ULONGLONG hash = Hash(key, size);
                DWORD slot = (DWORD)hash & (count - 1);
                for (DWORD probe = 0; probe < count; probe++)
                {
                    const BYTE *entry = view + offset + slot * SLOT_SIZE;
                    ULONGLONG found = U64(entry);
                    if (found == 0)
                        break;
                    if (found == hash)
                    {
                        DWORD state = U32(entry + 8);
                        // An unknown state shows nothing rather than online-only
                        answer->State = state >= STATE_CACHED && state <= STATE_PROBLEM ? (int)state : -1;
                        break;
                    }
                    slot = (slot + 1) & (count - 1);
                }
            }
        }
    }
    MemoryBarrier();
    *sequence = before;
    return Sequence(view) == before;
}

// The section's answer for a path (under Lock): FALSE when there is no usable section, it stayed busy, or its agent stopped
static BOOL Ask(const WCHAR *upper, int length, BOOL wantState, ANSWER *answer, LONG *sequence)
{
    const BYTE *view = Section();
    if (!view)
        return FALSE;
    for (int attempt = 0; attempt < 4; attempt++)
    {
        if (Pass(view, upper, length, wantState, answer, sequence))
            return answer->Valid && AgentAlive(answer->Pid);
    }
    return FALSE;
}

// The menu shows for this path: on the volume, agent alive, menu enabled
static BOOL MenuShows(LPCWSTR path)
{
    ANSWER answer;
    LONG sequence;
    AcquireSRWLockExclusive(&Lock);
    int length = Normalize(path, UpperPath);
    BOOL shows = length >= 0 && Ask(UpperPath, length, FALSE, &answer, &sequence) && (answer.Flags & FLAG_MENU) && answer.OnVolume;
    ReleaseSRWLockExclusive(&Lock);
    return shows;
}

// The overlay a path gets (STATE_*); the last answer is kept for the other overlay classes
static int OverlayState(LPCWSTR path, DWORD attributes)
{
    DWORD directory = attributes & FILE_ATTRIBUTE_DIRECTORY;
    AcquireSRWLockExclusive(&Lock);
    int state = STATE_NONE;
    const WCHAR *upper = UpperPath;
    const BYTE *view = Section();
    int length = view ? Normalize(path, UpperPath) : -1;
    if (length >= 0)
    {
        LONG current = Sequence(view);
        if (GetTickCount64() < CacheUntil && current == CacheSequence && directory == CacheDirectory && length == CacheLength &&
            Equal(upper, CachePath, length))
        {
            state = CacheState;
        }
        else
        {
            ANSWER answer;
            LONG sequence = 0;
            if (Ask(upper, length, TRUE, &answer, &sequence) && (answer.Flags & FLAG_OVERLAYS) && answer.OnVolume)
            {
                if (answer.State > 0)
                    state = answer.State;
                else if (answer.State == STATE_NONE && !directory)
                    state = STATE_ONLINE_ONLY;
            }
            lstrcpynW(CachePath, upper, MAX_CHARS);
            CacheLength = length;
            CacheDirectory = directory;
            CacheSequence = sequence;
            CacheState = state;
            CacheUntil = GetTickCount64() + CACHE_MS;
        }
    }
    ReleaseSRWLockExclusive(&Lock);
    return state;
}

// The section's text for key into text (capacity characters); FALSE when it has none
static BOOL Text(LPCWSTR key, WCHAR *text, int capacity)
{
    int keyLength = lstrlenW(key);
    BOOL found = FALSE;
    AcquireSRWLockExclusive(&Lock);
    const BYTE *view = Section();
    for (int attempt = 0; view && attempt < 4; attempt++)
    {
        LONG before = Sequence(view);
        if (before & 1)
            continue;
        MemoryBarrier();
        found = FALSE;
        if (U32(view) == MAGIC && U32(view + 4) == LAYOUT_VERSION)
        {
            DWORD offset = U32(view + 28), count = U32(view + 32);
            for (DWORD i = 0; i < count && i < MAX_STRINGS && offset <= SECTION_SIZE - 4; i++)
            {
                int keyChars = (int)U16(view + offset), valueChars = (int)U16(view + offset + 2);
                DWORD record = (4 + 2 * (DWORD)(keyChars + valueChars) + 3) & ~3u;
                if (record > SECTION_SIZE - offset)
                    break;
                const BYTE *chars = view + offset + 4;
                offset += record;
                if (keyChars != keyLength || valueChars < 1 || valueChars >= capacity)
                    continue;
                BOOL same = TRUE;
                for (int c = 0; c < keyChars && same; c++)
                    same = (WCHAR)U16(chars + 2 * c) == key[c];
                if (!same)
                    continue;
                for (int c = 0; c < valueChars; c++)
                    text[c] = (WCHAR)U16(chars + 2 * (keyChars + c));
                text[valueChars] = L'\0';
                found = TRUE;
                break;
            }
        }
        MemoryBarrier();
        if (Sequence(view) == before)
            break;
        found = FALSE;
    }
    ReleaseSRWLockExclusive(&Lock);
    return found;
}

static HRESULT Duplicate(LPCWSTR text, LPWSTR *copy)
{
    if (!copy)
        return E_POINTER;
    int length = lstrlenW(text);
    *copy = CoTaskMemAlloc((length + 1) * sizeof(WCHAR));
    if (!*copy)
        return E_OUTOFMEMORY;
    lstrcpynW(*copy, text, length + 1);
    return S_OK;
}

// ASCII letters compared without case: the patterns' words are English
static WCHAR Fold(WCHAR c) { return c >= L'a' && c <= L'z' ? (WCHAR)(c - 32) : c; }

// * and ? wildcards, as the legacy menu's System.FileName:~ filter
static BOOL Matches(LPCWSTR name, LPCWSTR pattern)
{
    LPCWSTR star = NULL, resume = NULL;
    while (*name)
    {
        if (*pattern == L'*')
        {
            star = pattern++;
            resume = name;
        }
        else if (*pattern == L'?' || (*pattern && Fold(*pattern) == Fold(*name)))
        {
            pattern++;
            name++;
        }
        else if (star)
        {
            pattern = star + 1;
            name = ++resume;
        }
        else
            return FALSE;
    }
    while (*pattern == L'*')
        pattern++;
    return *pattern == L'\0';
}

// Whether a verb applies to an item, by its name alone
static BOOL VerbApplies(int verb, LPCWSTR path, BOOL folder)
{
    const VERB *v = &Verbs[verb];
    if (folder && !v->Folders)
        return FALSE;
    LPCWSTR name = path;
    for (LPCWSTR c = path; *c; c++)
    {
        if (*c == L'\\')
            name = c + 1;
    }
    switch (v->Filter)
    {
    case FILTER_OFFICE:
    {
        LPCWSTR extension = NULL;
        for (LPCWSTR c = name; *c; c++)
        {
            if (*c == L'.')
                extension = c;
        }
        if (!extension || !extension[1])
            return FALSE;
        int length = lstrlenW(extension);
        for (int i = 0; i < (int)(sizeof(OfficeExtensions) / sizeof(OfficeExtensions[0])); i++)
        {
            if (CompareStringOrdinal(extension, length, OfficeExtensions[i], -1, TRUE) == CSTR_EQUAL)
                return TRUE;
        }
        return FALSE;
    }
    case FILTER_CONFLICT:
        return Matches(name, ConflictPatterns[0]) || Matches(name, ConflictPatterns[1]);
    default:
        return TRUE;
    }
}

// The one selected item's path (free with CoTaskMemFree) and whether it is a folder (a .zip is a file); NULL for none or several
static LPWSTR Selected(IShellItemArray *items, BOOL *folder)
{
    DWORD count = 0;
    IShellItem *item = NULL;
    LPWSTR path = NULL;
    if (!items || FAILED(IShellItemArray_GetCount(items, &count)) || count != 1 || FAILED(IShellItemArray_GetItemAt(items, 0, &item)))
        return NULL;
    if (FAILED(IShellItem_GetDisplayName(item, SIGDN_FILESYSPATH, &path)))
        path = NULL;
    SFGAOF attributes = 0;
    if (path && folder)
        *folder = SUCCEEDED(IShellItem_GetAttributes(item, SFGAO_FOLDER | SFGAO_STREAM, &attributes)) &&
            (attributes & SFGAO_FOLDER) && !(attributes & SFGAO_STREAM);
    IShellItem_Release(item);
    return path;
}

// "<install folder>\vfs365-agent.exe" verb <name> "<path>", as the legacy menu's command (ExplorerVerbs.Command)
static HRESULT Run(int verb, LPCWSTR path)
{
    if (!AgentExe[0])
        return E_FAIL;
    int pathLength = lstrlenW(path), exeLength = lstrlenW(AgentExe), nameLength = lstrlenW(Verbs[verb].Name);
    // A trailing backslash would escape the closing quote: doubled, it stays one
    BOOL trailing = pathLength > 0 && path[pathLength - 1] == L'\\';
    int size = exeLength + nameLength + pathLength + 16;
    WCHAR *line = HeapAlloc(GetProcessHeap(), 0, size * sizeof(WCHAR));
    if (!line)
        return E_OUTOFMEMORY;
    int at = 0;
    line[at++] = L'"';
    lstrcpynW(line + at, AgentExe, exeLength + 1);
    at += exeLength;
    lstrcpynW(line + at, L"\" verb ", 8);
    at += 7;
    lstrcpynW(line + at, Verbs[verb].Name, nameLength + 1);
    at += nameLength;
    line[at++] = L' ';
    line[at++] = L'"';
    lstrcpynW(line + at, path, pathLength + 1);
    at += pathLength;
    if (trailing)
        line[at++] = L'\\';
    line[at++] = L'"';
    line[at] = L'\0';

    STARTUPINFOW startup = { 0 };
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process;
    BOOL started = CreateProcessW(AgentExe, line, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &startup, &process);
    HeapFree(GetProcessHeap(), 0, line);
    if (!started)
        return HRESULT_FROM_WIN32(GetLastError());
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
}

// Commands: index 0 is the top-level VFS365 item, 1 to VERB_COUNT the verbs. Static objects, counted in Locks.
typedef struct
{
    const IExplorerCommandVtbl *lpVtbl;
    int Index;
} COMMAND;

typedef struct
{
    const IEnumExplorerCommandVtbl *lpVtbl;
    volatile LONG References;
    ULONG Next;
} ENUMERATOR;

static COMMAND Commands[1 + VERB_COUNT];

static HRESULT STDMETHODCALLTYPE CommandQueryInterface(IExplorerCommand *self, REFIID iid, void **object)
{
    if (!object)
        return E_POINTER;
    if (SameGuid(iid, &IidUnknown) || SameGuid(iid, &IidExplorerCommand))
    {
        *object = self;
        InterlockedIncrement(&Locks);
        return S_OK;
    }
    *object = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE StaticAddRef(void *self)
{
    (void)self;
    return (ULONG)InterlockedIncrement(&Locks);
}

static ULONG STDMETHODCALLTYPE StaticRelease(void *self)
{
    (void)self;
    return (ULONG)InterlockedDecrement(&Locks);
}

static HRESULT STDMETHODCALLTYPE CommandGetTitle(IExplorerCommand *self, IShellItemArray *items, LPWSTR *name)
{
    (void)items;
    int index = ((COMMAND *)self)->Index;
    WCHAR text[MAX_TITLE];
    if (!Text(index == 0 ? L"Verb.Menu" : Verbs[index - 1].Key, text, MAX_TITLE))
        lstrcpynW(text, index == 0 ? L"VFS365" : Verbs[index - 1].English, MAX_TITLE);
    return Duplicate(text, name);
}

static HRESULT STDMETHODCALLTYPE CommandGetIcon(IExplorerCommand *self, IShellItemArray *items, LPWSTR *icon)
{
    (void)items;
    if (((COMMAND *)self)->Index != 0 || !AgentExe[0])
    {
        if (icon)
            *icon = NULL;
        return E_NOTIMPL;
    }
    WCHAR location[MAX_PATH + 4];
    int length = lstrlenW(AgentExe);
    lstrcpynW(location, AgentExe, MAX_PATH);
    lstrcpynW(location + length, L",0", 3);
    return Duplicate(location, icon);
}

static HRESULT STDMETHODCALLTYPE CommandGetToolTip(IExplorerCommand *self, IShellItemArray *items, LPWSTR *tip)
{
    (void)self;
    (void)items;
    if (tip)
        *tip = NULL;
    return E_NOTIMPL;
}

static HRESULT STDMETHODCALLTYPE CommandGetCanonicalName(IExplorerCommand *self, GUID *name)
{
    (void)self;
    if (!name)
        return E_POINTER;
    *name = Classes[0];
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE CommandGetState(IExplorerCommand *self, IShellItemArray *items, BOOL slow, EXPCMDSTATE *state)
{
    (void)slow;
    if (!state)
        return E_POINTER;
    int index = ((COMMAND *)self)->Index;
    BOOL folder = FALSE;
    LPWSTR path = Selected(items, &folder);
    BOOL shown = path && (index == 0 ? MenuShows(path) : VerbApplies(index - 1, path, folder));
    CoTaskMemFree(path);
    *state = shown ? ECS_ENABLED : ECS_HIDDEN;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE CommandInvoke(IExplorerCommand *self, IShellItemArray *items, IBindCtx *context)
{
    (void)context;
    int index = ((COMMAND *)self)->Index;
    if (index == 0)
        return E_NOTIMPL;
    LPWSTR path = Selected(items, NULL);
    if (!path)
        return E_INVALIDARG;
    HRESULT result = Run(index - 1, path);
    CoTaskMemFree(path);
    return result;
}

static HRESULT STDMETHODCALLTYPE CommandGetFlags(IExplorerCommand *self, EXPCMDFLAGS *flags)
{
    if (!flags)
        return E_POINTER;
    int index = ((COMMAND *)self)->Index;
    *flags = index == 0 ? ECF_HASSUBCOMMANDS : Verbs[index - 1].Flags;
    return S_OK;
}

static const IEnumExplorerCommandVtbl EnumeratorVtbl;

static HRESULT NewEnumerator(ULONG next, IEnumExplorerCommand **result)
{
    ENUMERATOR *enumerator = HeapAlloc(GetProcessHeap(), 0, sizeof(ENUMERATOR));
    if (!enumerator)
        return E_OUTOFMEMORY;
    enumerator->lpVtbl = &EnumeratorVtbl;
    enumerator->References = 1;
    enumerator->Next = next;
    InterlockedIncrement(&Locks);
    *result = (IEnumExplorerCommand *)enumerator;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE CommandEnumSubCommands(IExplorerCommand *self, IEnumExplorerCommand **commands)
{
    if (!commands)
        return E_POINTER;
    *commands = NULL;
    if (((COMMAND *)self)->Index != 0)
        return E_NOTIMPL;
    return NewEnumerator(0, commands);
}

static const IExplorerCommandVtbl CommandVtbl = {
    CommandQueryInterface,
    (ULONG(STDMETHODCALLTYPE *)(IExplorerCommand *))StaticAddRef,
    (ULONG(STDMETHODCALLTYPE *)(IExplorerCommand *))StaticRelease,
    CommandGetTitle,
    CommandGetIcon,
    CommandGetToolTip,
    CommandGetCanonicalName,
    CommandGetState,
    CommandInvoke,
    CommandGetFlags,
    CommandEnumSubCommands,
};

static HRESULT STDMETHODCALLTYPE EnumeratorQueryInterface(IEnumExplorerCommand *self, REFIID iid, void **object)
{
    if (!object)
        return E_POINTER;
    if (SameGuid(iid, &IidUnknown) || SameGuid(iid, &IidEnumExplorerCommand))
    {
        *object = self;
        InterlockedIncrement(&((ENUMERATOR *)self)->References);
        return S_OK;
    }
    *object = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE EnumeratorAddRef(IEnumExplorerCommand *self)
{
    return (ULONG)InterlockedIncrement(&((ENUMERATOR *)self)->References);
}

static ULONG STDMETHODCALLTYPE EnumeratorRelease(IEnumExplorerCommand *self)
{
    LONG left = InterlockedDecrement(&((ENUMERATOR *)self)->References);
    if (left == 0)
    {
        HeapFree(GetProcessHeap(), 0, self);
        InterlockedDecrement(&Locks);
    }
    return (ULONG)left;
}

static HRESULT STDMETHODCALLTYPE EnumeratorNext(IEnumExplorerCommand *self, ULONG count, IExplorerCommand **commands, ULONG *fetched)
{
    ENUMERATOR *enumerator = (ENUMERATOR *)self;
    if (!commands || (count > 1 && !fetched))
        return E_POINTER;
    ULONG given = 0;
    while (given < count && enumerator->Next < (ULONG)VERB_COUNT)
    {
        commands[given++] = (IExplorerCommand *)&Commands[1 + enumerator->Next++];
        InterlockedIncrement(&Locks);
    }
    if (fetched)
        *fetched = given;
    return given == count ? S_OK : S_FALSE;
}

static HRESULT STDMETHODCALLTYPE EnumeratorSkip(IEnumExplorerCommand *self, ULONG count)
{
    ENUMERATOR *enumerator = (ENUMERATOR *)self;
    ULONG left = (ULONG)VERB_COUNT - enumerator->Next;
    enumerator->Next += count < left ? count : left;
    return count <= left ? S_OK : S_FALSE;
}

static HRESULT STDMETHODCALLTYPE EnumeratorReset(IEnumExplorerCommand *self)
{
    ((ENUMERATOR *)self)->Next = 0;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE EnumeratorClone(IEnumExplorerCommand *self, IEnumExplorerCommand **copy)
{
    if (!copy)
        return E_POINTER;
    return NewEnumerator(((ENUMERATOR *)self)->Next, copy);
}

static const IEnumExplorerCommandVtbl EnumeratorVtbl = {
    EnumeratorQueryInterface,
    EnumeratorAddRef,
    EnumeratorRelease,
    EnumeratorNext,
    EnumeratorSkip,
    EnumeratorReset,
    EnumeratorClone,
};

// Overlay classes, one per state; static objects counted in Locks. MinGW's C declaration of IShellIconOverlayIdentifier lacks the
// IUnknown methods, so the table is declared here.
typedef struct
{
    HRESULT(STDMETHODCALLTYPE *QueryInterface)(IShellIconOverlayIdentifier *self, REFIID iid, void **object);
    ULONG(STDMETHODCALLTYPE *AddRef)(IShellIconOverlayIdentifier *self);
    ULONG(STDMETHODCALLTYPE *Release)(IShellIconOverlayIdentifier *self);
    HRESULT(STDMETHODCALLTYPE *IsMemberOf)(IShellIconOverlayIdentifier *self, LPCWSTR path, DWORD attributes);
    HRESULT(STDMETHODCALLTYPE *GetOverlayInfo)(IShellIconOverlayIdentifier *self, LPWSTR file, int capacity, int *index, DWORD *flags);
    HRESULT(STDMETHODCALLTYPE *GetPriority)(IShellIconOverlayIdentifier *self, int *priority);
} OVERLAY_VTBL;

typedef struct
{
    const OVERLAY_VTBL *lpVtbl;
    int State;
} OVERLAY;

static HRESULT STDMETHODCALLTYPE OverlayQueryInterface(IShellIconOverlayIdentifier *self, REFIID iid, void **object)
{
    if (!object)
        return E_POINTER;
    if (SameGuid(iid, &IidUnknown) || SameGuid(iid, &IidOverlayIdentifier))
    {
        *object = self;
        InterlockedIncrement(&Locks);
        return S_OK;
    }
    *object = NULL;
    return E_NOINTERFACE;
}

static HRESULT STDMETHODCALLTYPE OverlayIsMemberOf(IShellIconOverlayIdentifier *self, LPCWSTR path, DWORD attributes)
{
    return OverlayState(path, attributes) == ((OVERLAY *)self)->State ? S_OK : S_FALSE;
}

// The icon is this DLL's: icon resources 1 to 5 in state order (vfs365shell.rc), so index state - 1
static HRESULT STDMETHODCALLTYPE OverlayGetOverlayInfo(IShellIconOverlayIdentifier *self, LPWSTR file, int capacity, int *index, DWORD *flags)
{
    if (!file || !index || !flags)
        return E_POINTER;
    if (!ModulePath[0] || lstrlenW(ModulePath) >= capacity)
        return E_FAIL;
    lstrcpynW(file, ModulePath, capacity);
    *index = ((OVERLAY *)self)->State - 1;
    *flags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
    return S_OK;
}

// 0 is the highest: a problem first
static HRESULT STDMETHODCALLTYPE OverlayGetPriority(IShellIconOverlayIdentifier *self, int *priority)
{
    static const int Priorities[1 + STATES] = { 100, 30, 10, 20, 0, 40 };
    if (!priority)
        return E_POINTER;
    *priority = Priorities[((OVERLAY *)self)->State];
    return S_OK;
}

static const OVERLAY_VTBL OverlayVtbl = {
    OverlayQueryInterface,
    (ULONG(STDMETHODCALLTYPE *)(IShellIconOverlayIdentifier *))StaticAddRef,
    (ULONG(STDMETHODCALLTYPE *)(IShellIconOverlayIdentifier *))StaticRelease,
    OverlayIsMemberOf,
    OverlayGetOverlayInfo,
    OverlayGetPriority,
};

static COMMAND Commands[1 + VERB_COUNT] = {
    { &CommandVtbl, 0 }, { &CommandVtbl, 1 }, { &CommandVtbl, 2 }, { &CommandVtbl, 3 },
    { &CommandVtbl, 4 }, { &CommandVtbl, 5 }, { &CommandVtbl, 6 }, { &CommandVtbl, 7 },
};

static OVERLAY Overlays[STATES] = {
    { &OverlayVtbl, STATE_CACHED }, { &OverlayVtbl, STATE_UPLOADING }, { &OverlayVtbl, STATE_DOWNLOADING },
    { &OverlayVtbl, STATE_PROBLEM }, { &OverlayVtbl, STATE_ONLINE_ONLY },
};

// Class factories: one per class ID, index into Classes
typedef struct
{
    const IClassFactoryVtbl *lpVtbl;
    int Class;
} FACTORY;

static HRESULT STDMETHODCALLTYPE FactoryQueryInterface(IClassFactory *self, REFIID iid, void **object)
{
    if (!object)
        return E_POINTER;
    if (SameGuid(iid, &IidUnknown) || SameGuid(iid, &IidClassFactory))
    {
        *object = self;
        InterlockedIncrement(&Locks);
        return S_OK;
    }
    *object = NULL;
    return E_NOINTERFACE;
}

static HRESULT STDMETHODCALLTYPE FactoryCreateInstance(IClassFactory *self, IUnknown *outer, REFIID iid, void **object)
{
    if (!object)
        return E_POINTER;
    *object = NULL;
    if (outer)
        return CLASS_E_NOAGGREGATION;
    int index = ((FACTORY *)self)->Class;
    if (index == 0)
        return CommandQueryInterface((IExplorerCommand *)&Commands[0], iid, object);
    return OverlayQueryInterface((IShellIconOverlayIdentifier *)&Overlays[index - 1], iid, object);
}

static HRESULT STDMETHODCALLTYPE FactoryLockServer(IClassFactory *self, BOOL lock)
{
    (void)self;
    if (lock)
        InterlockedIncrement(&Locks);
    else
        InterlockedDecrement(&Locks);
    return S_OK;
}

static const IClassFactoryVtbl FactoryVtbl = {
    FactoryQueryInterface,
    (ULONG(STDMETHODCALLTYPE *)(IClassFactory *))StaticAddRef,
    (ULONG(STDMETHODCALLTYPE *)(IClassFactory *))StaticRelease,
    FactoryCreateInstance,
    FactoryLockServer,
};

static FACTORY Factories[1 + STATES] = {
    { &FactoryVtbl, 0 }, { &FactoryVtbl, 1 }, { &FactoryVtbl, 2 }, { &FactoryVtbl, 3 }, { &FactoryVtbl, 4 }, { &FactoryVtbl, 5 },
};

EXPORT HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, LPVOID *object)
{
    if (!object)
        return E_POINTER;
    *object = NULL;
    for (int i = 0; i < 1 + STATES; i++)
    {
        if (SameGuid(clsid, &Classes[i]))
            return FactoryQueryInterface((IClassFactory *)&Factories[i], iid, object);
    }
    return CLASS_E_CLASSNOTAVAILABLE;
}

EXPORT HRESULT WINAPI DllCanUnloadNow(void)
{
    return Locks == 0 ? S_OK : S_FALSE;
}

// For tests (LoadLibrary): the overlay state a path gets (STATE_*), and whether the menu shows for it
EXPORT int WINAPI VfsShellQueryState(LPCWSTR path, DWORD attributes)
{
    return OverlayState(path, attributes);
}

EXPORT int WINAPI VfsShellIsOnVolume(LPCWSTR path)
{
    return MenuShows(path) ? 1 : 0;
}

// For tests: command index (0 the top-level item, 1 to 7 the verbs) for an item: its verb name and title (capacity characters each),
// 1 when shown, 0 when hidden, -1 past the last
EXPORT int WINAPI VfsShellVerb(int index, LPCWSTR path, int folder, LPWSTR name, LPWSTR title, int capacity)
{
    if (index < 0 || index > VERB_COUNT || !path || !name || !title || capacity < 2)
        return -1;
    lstrcpynW(name, index == 0 ? L"" : Verbs[index - 1].Name, capacity);
    WCHAR text[MAX_TITLE];
    if (!Text(index == 0 ? L"Verb.Menu" : Verbs[index - 1].Key, text, MAX_TITLE))
        lstrcpynW(text, index == 0 ? L"VFS365" : Verbs[index - 1].English, MAX_TITLE);
    lstrcpynW(title, text, capacity);
    return (index == 0 ? MenuShows(path) : VerbApplies(index - 1, path, folder != 0)) ? 1 : 0;
}

// For tests: reads the section by this name instead of the user's (NULL or "" goes back to it) and forgets what was cached
EXPORT void WINAPI VfsShellTestSection(LPCWSTR name)
{
    AcquireSRWLockExclusive(&Lock);
    CloseSection();
    if (name && lstrlenW(name) < 256)
        lstrcpynW(TestName, name, 256);
    else
        TestName[0] = L'\0';
    RetryAt = 0;
    ReleaseSRWLockExclusive(&Lock);
}
