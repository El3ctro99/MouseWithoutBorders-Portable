/* Copyright (c) Mouse Without Borders Portable contributors. MIT licensed. */
#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0602
#endif
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <shlobj.h>
#include <shellapi.h>
#include <userenv.h>
#include <wchar.h>
#include <string.h>
#include <stdlib.h>

/* This optional, protected native entry point runs BEFORE any managed runtime.
   No paths, arguments, environment values or commands are accepted from a caller. */
#define PATH_CAP 4096
#define ENV_CAP 32768
#define HELD_CAP 24
enum { FAIL_ARGUMENTS = 10, FAIL_TOKEN, FAIL_PATH, FAIL_TRUST, FAIL_ENVIRONMENT,
       FAIL_CREATE_PROCESS, FAIL_DLL_POLICY };

static const GUID kProgramFiles = {0x905e63b6,0xc1bf,0x494e,{0xb2,0x9c,0x65,0xb7,0x32,0xd3,0xd2,0x1a}};
static const GUID kLocalAppData = {0xf1b32785,0x6fba,0x4fcf,{0x9d,0x55,0x7b,0x8e,0x7f,0x15,0x70,0x91}};
static const GUID kRoamingAppData = {0x3eb685db,0x65f9,0x4cf6,{0xa0,0x3a,0xe3,0xef,0x65,0x72,0x9f,0x3d}};
static PSID gAdministrators, gSystem, gTrustedInstaller, gOwnerRights;
static HANDLE gHeld[HELD_CAP];
static unsigned gHeldCount;
static WCHAR gRoot[PATH_CAP], gTarget[PATH_CAP], gRuntime[PATH_CAP];
static WCHAR gTemp[PATH_CAP], gBundle[PATH_CAP];

static BOOL CopyText(WCHAR *destination, size_t capacity, const WCHAR *source)
{
    size_t count = wcslen(source);
    if (count >= capacity) return FALSE;
    memcpy(destination, source, (count + 1) * sizeof(WCHAR));
    return TRUE;
}

static BOOL AppendText(WCHAR *destination, size_t capacity, const WCHAR *suffix)
{
    size_t count = wcslen(destination), extra = wcslen(suffix);
    if (count >= capacity || extra >= capacity - count) return FALSE;
    memcpy(destination + count, suffix, (extra + 1) * sizeof(WCHAR));
    return TRUE;
}

static BOOL ChildPath(WCHAR *destination, const WCHAR *parent, const WCHAR *name)
{
    return CopyText(destination, PATH_CAP, parent)
        && AppendText(destination, PATH_CAP, L"\\") && AppendText(destination, PATH_CAP, name);
}

static BOOL SameText(const WCHAR *left, const WCHAR *right)
{
    return CompareStringOrdinal(left, -1, right, -1, TRUE) == CSTR_EQUAL;
}

static void *TokenData(HANDLE token, TOKEN_INFORMATION_CLASS kind)
{
    DWORD size = 0;
    void *data;
    GetTokenInformation(token, kind, NULL, 0, &size);
    if (size < sizeof(DWORD) || size > 65536) return NULL;
    data = HeapAlloc(GetProcessHeap(), 0, size);
    if (data && !GetTokenInformation(token, kind, data, size, &size)) {
        HeapFree(GetProcessHeap(), 0, data); data = NULL;
    }
    return data;
}

static BOOL ReadCaller(HANDLE token, TOKEN_USER **user, LPWSTR *sidText)
{
    TOKEN_ELEVATION elevation;
    DWORD bytes, session = 0, uiAccess = 0, i;
    TOKEN_MANDATORY_LABEL *label = NULL;
    TOKEN_GROUPS *groups = NULL;
    BOOL administrator = FALSE, valid = FALSE;
    *user = NULL; *sidText = NULL;
    if (!GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &bytes) || !elevation.TokenIsElevated
        || !GetTokenInformation(token, TokenSessionId, &session, sizeof(session), &bytes) || !session || session == MAXDWORD
        || !GetTokenInformation(token, TokenUIAccess, &uiAccess, sizeof(uiAccess), &bytes) || uiAccess) return FALSE;
    *user = (TOKEN_USER *)TokenData(token, TokenUser);
    label = (TOKEN_MANDATORY_LABEL *)TokenData(token, TokenIntegrityLevel);
    groups = (TOKEN_GROUPS *)TokenData(token, TokenGroups);
    if (!*user || !label || !groups || !IsValidSid((*user)->User.Sid) || !IsValidSid(label->Label.Sid)) goto done;
    if (IsWellKnownSid((*user)->User.Sid, WinLocalSystemSid)
        || IsWellKnownSid((*user)->User.Sid, WinLocalServiceSid)
        || IsWellKnownSid((*user)->User.Sid, WinNetworkServiceSid)) goto done;
    {
        UCHAR count = *GetSidSubAuthorityCount(label->Label.Sid);
        DWORD integrity;
        if (!count) goto done;
        integrity = *GetSidSubAuthority(label->Label.Sid, count - 1);
        if (integrity < SECURITY_MANDATORY_HIGH_RID || integrity >= SECURITY_MANDATORY_SYSTEM_RID) goto done;
    }
    for (i = 0; i < groups->GroupCount; ++i)
        if (EqualSid(groups->Groups[i].Sid, gAdministrators)
            && (groups->Groups[i].Attributes & SE_GROUP_ENABLED)
            && !(groups->Groups[i].Attributes & SE_GROUP_USE_FOR_DENY_ONLY)) administrator = TRUE;
    if (!administrator || !ConvertSidToStringSidW((*user)->User.Sid, sidText)) goto done;
    valid = TRUE;
done:
    if (label) HeapFree(GetProcessHeap(), 0, label);
    if (groups) HeapFree(GetProcessHeap(), 0, groups);
    return valid;
}

static BOOL TrustedOwner(PSID owner, BOOL ancestor)
{
    return owner && IsValidSid(owner) && (EqualSid(owner, gAdministrators) || EqualSid(owner, gSystem)
        || (ancestor && EqualSid(owner, gTrustedInstaller)));
}

/* Inspect the HANDLE's DACL, not a second path lookup. Deny ACEs are harmless;
   unknown/conditional allow forms fail closed rather than being interpreted. */
static BOOL TrustedAcl(HANDLE object, BOOL ancestor)
{
    PSECURITY_DESCRIPTOR descriptor = NULL;
    PACL acl = NULL;
    PSID owner = NULL;
    DWORD i;
    BOOL valid = FALSE;
    DWORD safe = FILE_GENERIC_READ | FILE_GENERIC_EXECUTE | GENERIC_READ | GENERIC_EXECUTE;
    if (ancestor) safe |= FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES;
    if (GetSecurityInfo(object, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
        &owner, NULL, &acl, NULL, &descriptor) != ERROR_SUCCESS) goto done;
    if (!TrustedOwner(owner, ancestor) || !acl || !IsValidAcl(acl)) goto done;
    for (i = 0; i < acl->AceCount; ++i) {
        ACE_HEADER *header;
        ACCESS_ALLOWED_ACE *ace;
        PSID identity;
        if (!GetAce(acl, i, (void **)&header)) goto done;
        if (header->AceFlags & INHERIT_ONLY_ACE) continue;
        if (header->AceType == ACCESS_DENIED_ACE_TYPE || header->AceType == ACCESS_DENIED_OBJECT_ACE_TYPE
            || header->AceType == ACCESS_DENIED_CALLBACK_ACE_TYPE || header->AceType == ACCESS_DENIED_CALLBACK_OBJECT_ACE_TYPE) continue;
        if (header->AceType != ACCESS_ALLOWED_ACE_TYPE
            || header->AceSize < FIELD_OFFSET(ACCESS_ALLOWED_ACE, SidStart) + FIELD_OFFSET(SID, SubAuthority)) goto done;
        ace = (ACCESS_ALLOWED_ACE *)header;
        identity = (PSID)&ace->SidStart;
        if (!IsValidSid(identity) || GetLengthSid(identity) > header->AceSize - FIELD_OFFSET(ACCESS_ALLOWED_ACE, SidStart)) goto done;
        if (EqualSid(identity, gAdministrators) || EqualSid(identity, gSystem)
            || (ancestor && EqualSid(identity, gTrustedInstaller))) continue;
        if (EqualSid(identity, gOwnerRights)) {
            if (ace->Mask & ~READ_CONTROL) goto done;
        } else if (ace->Mask & ~safe) goto done;
    }
    valid = TRUE;
done:
    if (descriptor) LocalFree(descriptor);
    return valid;
}

static BOOL HoldProtected(const WCHAR *path, BOOL directory, BOOL ancestor)
{
    HANDLE file;
    BY_HANDLE_FILE_INFORMATION info;
    if (gHeldCount == HELD_CAP) return FALSE;
    file = CreateFileW(path, GENERIC_READ | READ_CONTROL, FILE_SHARE_READ, NULL, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | (directory ? FILE_FLAG_BACKUP_SEMANTICS : 0), NULL);
    if (file == INVALID_HANDLE_VALUE) return FALSE;
    if (!GetFileInformationByHandle(file, &info) || (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
        || ((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != (directory != FALSE)
        || GetFileType(file) != FILE_TYPE_DISK || !TrustedAcl(file, ancestor)) {
        CloseHandle(file); return FALSE;
    }
    gHeld[gHeldCount++] = file;
    return TRUE;
}

static BOOL HoldAncestors(const WCHAR *programFiles)
{
    WCHAR path[PATH_CAP];
    size_t length, i;
    if (!CopyText(path, PATH_CAP, programFiles)) return FALSE;
    length = wcslen(path);
    if (length < 3 || path[1] != L':' || path[2] != L'\\') return FALSE;
    for (i = 3; i <= length; ++i) {
        WCHAR saved;
        if (i != length && path[i] != L'\\') continue;
        saved = path[i]; path[i] = L'\0';
        if (!HoldProtected(path, TRUE, TRUE)) return FALSE;
        path[i] = saved;
    }
    path[3] = L'\0';
    return GetDriveTypeW(path) == DRIVE_FIXED && HoldProtected(path, TRUE, TRUE);
}

static BOOL PreparePaths(const WCHAR *sidText)
{
    PWSTR programFiles = NULL;
    WCHAR product[PATH_CAP], self[PATH_CAP], expected[PATH_CAP];
    DWORD count;
    BOOL valid = FALSE;
    if (FAILED(SHGetKnownFolderPath(&kProgramFiles, 0, NULL, &programFiles))) return FALSE;
    if (!ChildPath(product, programFiles, L"Mouse Without Borders Approved Launch")
        || !ChildPath(gRoot, product, sidText) || !ChildPath(expected, gRoot, L"MwbApprovedLauncher.exe")) goto done;
    count = GetModuleFileNameW(NULL, self, PATH_CAP);
    if (!count || count >= PATH_CAP || !SameText(self, expected)) goto done;
    if (!ChildPath(gTarget, gRoot, L"MouseWithoutBorders.exe") || !ChildPath(gRuntime, gRoot, L"runtime")
        || !ChildPath(gTemp, gRuntime, L"temp") || !ChildPath(gBundle, gRuntime, L"bundle")) goto done;
    if (!HoldAncestors(programFiles) || !HoldProtected(product, TRUE, FALSE) || !HoldProtected(gRoot, TRUE, FALSE)
        || !HoldProtected(expected, FALSE, FALSE) || !HoldProtected(gTarget, FALSE, FALSE)
        || !HoldProtected(gRuntime, TRUE, FALSE) || !HoldProtected(gTemp, TRUE, FALSE) || !HoldProtected(gBundle, TRUE, FALSE)) goto done;
    valid = TRUE;
done:
    if (programFiles) CoTaskMemFree(programFiles);
    return valid;
}

typedef struct ENV_ENTRY { const WCHAR *name; const WCHAR *value; } ENV_ENTRY;
static int CompareEntries(const void *left, const void *right)
{
    int result = CompareStringOrdinal(((const ENV_ENTRY *)left)->name, -1, ((const ENV_ENTRY *)right)->name, -1, TRUE);
    return result == CSTR_LESS_THAN ? -1 : result == CSTR_GREATER_THAN ? 1 : 0;
}

static BOOL BuildEnvironment(HANDLE token, PSID user, WCHAR *environment)
{
    WCHAR windows[PATH_CAP], system32[PATH_CAP], command[PATH_CAP], profile[PATH_CAP];
    WCHAR name[256], domain[256];
    DWORD capacity = PATH_CAP, nameCapacity = 256, domainCapacity = 256, count;
    PWSTR local = NULL, roaming = NULL;
    SID_NAME_USE kind;
    size_t used = 0, i;
    BOOL valid = FALSE;
    ENV_ENTRY entries[12];
    count = GetSystemWindowsDirectoryW(windows, PATH_CAP);
    if (!count || count >= PATH_CAP || !ChildPath(system32, windows, L"System32")
        || !ChildPath(command, system32, L"cmd.exe") || !GetUserProfileDirectoryW(token, profile, &capacity)
        || !LookupAccountSidW(NULL, user, name, &nameCapacity, domain, &domainCapacity, &kind)
        || FAILED(SHGetKnownFolderPath(&kLocalAppData, 0, NULL, &local))
        || FAILED(SHGetKnownFolderPath(&kRoamingAppData, 0, NULL, &roaming))) goto done;
    entries[0] = (ENV_ENTRY){L"APPDATA", roaming};
    entries[1] = (ENV_ENTRY){L"COMSPEC", command};
    entries[2] = (ENV_ENTRY){L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", gBundle};
    entries[3] = (ENV_ENTRY){L"LOCALAPPDATA", local};
    entries[4] = (ENV_ENTRY){L"PATH", system32};
    entries[5] = (ENV_ENTRY){L"SystemRoot", windows};
    entries[6] = (ENV_ENTRY){L"TEMP", gTemp};
    entries[7] = (ENV_ENTRY){L"TMP", gTemp};
    entries[8] = (ENV_ENTRY){L"USERDOMAIN", domain};
    entries[9] = (ENV_ENTRY){L"USERNAME", name};
    entries[10] = (ENV_ENTRY){L"USERPROFILE", profile};
    entries[11] = (ENV_ENTRY){L"WINDIR", windows};
    qsort(entries, 12, sizeof(entries[0]), CompareEntries);
    for (i = 0; i < 12; ++i) {
        size_t a = wcslen(entries[i].name), b = wcslen(entries[i].value);
        if (a + b + 3 > ENV_CAP - used) goto done;
        memcpy(environment + used, entries[i].name, a * sizeof(WCHAR)); used += a;
        environment[used++] = L'=';
        memcpy(environment + used, entries[i].value, b * sizeof(WCHAR)); used += b;
        environment[used++] = L'\0';
    }
    environment[used] = L'\0'; valid = TRUE;
done:
    if (local) CoTaskMemFree(local);
    if (roaming) CoTaskMemFree(roaming);
    return valid;
}

int wmain(int argc, WCHAR **argv)
{
    HANDLE token = NULL;
    TOKEN_USER *user = NULL;
    LPWSTR sidText = NULL;
    WCHAR *environment = NULL;
    WCHAR command[PATH_CAP + 64];
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    unsigned i;
    int result = FAIL_ARGUMENTS;
    (void)argv;
    if (argc != 1) goto done;
    result = FAIL_DLL_POLICY;
    if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32)
        || !SetDllDirectoryW(L"")) goto done;
    result = FAIL_TOKEN;
    if (!ConvertStringSidToSidW(L"S-1-5-32-544", &gAdministrators)
        || !ConvertStringSidToSidW(L"S-1-5-18", &gSystem)
        || !ConvertStringSidToSidW(L"S-1-3-4", &gOwnerRights)
        || !ConvertStringSidToSidW(L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", &gTrustedInstaller)
        || !OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token) || !ReadCaller(token, &user, &sidText)) goto done;
    result = FAIL_PATH;
    if (!PreparePaths(sidText)) { result = FAIL_TRUST; goto done; }
    result = FAIL_ENVIRONMENT;
    environment = (WCHAR *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, ENV_CAP * sizeof(WCHAR));
    if (!environment || !BuildEnvironment(token, user->User.Sid, environment)) goto done;
    if (!CopyText(command, PATH_CAP + 64, L"\"") || !AppendText(command, PATH_CAP + 64, gTarget)
        || !AppendText(command, PATH_CAP + 64, L"\" --mwb-approved-main")) goto done;
    ZeroMemory(&startup, sizeof(startup)); ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup); startup.lpDesktop = L"winsta0\\default";
    result = FAIL_CREATE_PROCESS;
    if (!CreateProcessW(gTarget, command, NULL, NULL, FALSE, CREATE_UNICODE_ENVIRONMENT,
        environment, gRoot, &startup, &process)) goto done;
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    result = 0; /* Process creation confirmed; application health is checked elsewhere. */
done:
    for (i = 0; i < gHeldCount; ++i) CloseHandle(gHeld[i]);
    if (environment) HeapFree(GetProcessHeap(), 0, environment);
    if (sidText) LocalFree(sidText);
    if (user) HeapFree(GetProcessHeap(), 0, user);
    if (token) CloseHandle(token);
    if (gAdministrators) LocalFree(gAdministrators);
    if (gSystem) LocalFree(gSystem);
    if (gOwnerRights) LocalFree(gOwnerRights);
    if (gTrustedInstaller) LocalFree(gTrustedInstaller);
    return result;
}

/* The GUI subsystem entry point avoids a transient console window. Both build
   modes share the same strict no-argument policy and fixed child invocation. */
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show)
{
    int argc = 0, result;
    LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    (void)instance; (void)previous; (void)commandLine; (void)show;
    if (!argv) return FAIL_ARGUMENTS;
    result = wmain(argc, argv);
    LocalFree(argv);
    return result;
}
