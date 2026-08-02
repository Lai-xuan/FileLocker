#include <windows.h>
#include <objbase.h>
#include <shlobj.h>
#include <shellapi.h>
#include <strsafe.h>
#include <aclapi.h>
#include <new>
#include <vector>
#include <string>
#include "shell_extension_common.h"

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")

// FileLocker Shell Extension（右鍵選單）的 CLSID，這個專案專屬的識別碼，跟其他程式不會撞。
// {A1B2C3D4-E5F6-4789-9ABC-DEF012345678}
static const CLSID CLSID_FileLockerShellExtension =
{ 0xA1B2C3D4, 0xE5F6, 0x4789, { 0x9A, 0xBC, 0xDE, 0xF0, 0x12, 0x34, 0x56, 0x78 } };

// 不能用 static——shell_extension_common.h 的 inline 函式（例如 LaunchFileLockerApp）也需要
// 讀寫這個全域變數，static 會限制成只有這個編譯單元看得到，跨檔連結會找不到符號。
LONG g_cDllRef = 0;
HMODULE g_hModule = nullptr;

static const wchar_t* GetContextMenuLabel()
{
    return IsSystemUiChinese() ? L"使用 FileLocker 加密" : L"Encrypt with FileLocker";
}

/// <summary>
/// 對應「資料夾防護」規劃文件第 5 節：純 ACL 存取限制，不加密，跟上面的加密選單是完全不同的
/// 命令 id（見 QueryContextMenu／InvokeCommand），只在選取的項目全部是資料夾時才會被插入。
/// </summary>
static const wchar_t* GetLockFolderMenuLabel()
{
    return IsSystemUiChinese() ? L"將所選資料夾上鎖" : L"Lock Selected Folders";
}

static const wchar_t* GetUnlockFolderMenuLabel()
{
    return IsSystemUiChinese() ? L"將所選資料夾解鎖" : L"Unlock Selected Folders";
}

/// <summary>
/// 執行期讀取 ShellExtensionRegistrar.cs 寫入的拒絕權限遮罩——FolderGuardAcl.cs 的
/// DeniedRightsMask 才是唯一定義處，這裡不再手動維護第二份數值（這正是曾經修過的一個 bug
/// 的成因：兩份各自手寫的數值曾經對不上，選單永遠判斷成「未鎖定」，解鎖選項永遠不會出現）。
/// 讀不到登錄值時才退回這個寫死的備援值，只在 App 從未啟動過一次、Shell Extension 卻已經
/// 被外部工具註冊這種極端情況才用得到——正常情況下 App 每次啟動都會透過
/// ShellExtensionRegistrar.EnsureRegistered() 寫入這個值，這裡幾乎不會真的走到備援分支。
/// </summary>
static DWORD GetFolderGuardDeniedRightsMask()
{
    // ReadAndExecute(0x200A9) | Write(0x116) | Delete(0x10000) = 0x301BF
    // （這個組合值剛好等於 .NET FileSystemRights.Modify）。
    constexpr DWORD kFallbackDeniedRights = 0x000301BFUL;

    DWORD value = 0;
    DWORD size = sizeof(value);
    LSTATUS status = RegGetValueW(
        HKEY_CURRENT_USER,
        L"Software\\Classes\\CLSID\\{A1B2C3D4-E5F6-4789-9ABC-DEF012345678}\\InprocServer32",
        L"FolderGuardDeniedRightsMask",
        RRF_RT_REG_DWORD,
        nullptr, &value, &size);

    return (status == ERROR_SUCCESS) ? value : kFallbackDeniedRights;
}

/// <summary>
/// 查目前使用者的 SID 在這個資料夾上是不是有一條符合的 Deny ACE，邏輯對應
/// FolderGuardAcl.cs 的 IsDenyRuleActive。任何 API 失敗都當作「沒有鎖定」，跟 C# 那邊 catch
/// 起來回傳 false 的保守做法一致：選單顯示錯了頂多是使用者點了發現不對，不影響資料正確性
/// （真正的解鎖動作還是會重新驗證身份）。
/// </summary>
static bool IsFolderGuardLocked(const std::wstring& path)
{
    HANDLE hToken = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken))
    {
        return false;
    }

    DWORD tokenUserSize = 0;
    GetTokenInformation(hToken, TokenUser, nullptr, 0, &tokenUserSize);
    std::vector<BYTE> tokenUserBuffer(tokenUserSize);
    bool gotTokenUser = tokenUserSize > 0
        && GetTokenInformation(hToken, TokenUser, tokenUserBuffer.data(), tokenUserSize, &tokenUserSize);
    CloseHandle(hToken);

    if (!gotTokenUser)
    {
        return false;
    }

    PSID currentUserSid = reinterpret_cast<TOKEN_USER*>(tokenUserBuffer.data())->User.Sid;

    PACL pDacl = nullptr;
    PSECURITY_DESCRIPTOR pSecurityDescriptor = nullptr;
    DWORD result = GetNamedSecurityInfoW(
        path.c_str(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
        nullptr, nullptr, &pDacl, nullptr, &pSecurityDescriptor);

    if (result != ERROR_SUCCESS || pDacl == nullptr)
    {
        if (pSecurityDescriptor != nullptr)
        {
            LocalFree(pSecurityDescriptor);
        }
        return false;
    }

    const DWORD deniedRightsMask = GetFolderGuardDeniedRightsMask();

    bool isLocked = false;
    for (WORD i = 0; i < pDacl->AceCount; i++)
    {
        LPVOID pAce = nullptr;
        if (!GetAce(pDacl, i, &pAce))
        {
            continue;
        }

        auto* pHeader = static_cast<ACE_HEADER*>(pAce);
        if (pHeader->AceType != ACCESS_DENIED_ACE_TYPE)
        {
            continue;
        }

        auto* pDeniedAce = static_cast<ACCESS_DENIED_ACE*>(pAce);
        PSID aceSid = reinterpret_cast<PSID>(&pDeniedAce->SidStart);

        if (EqualSid(aceSid, currentUserSid) && (pDeniedAce->Mask & deniedRightsMask) == deniedRightsMask)
        {
            isLocked = true;
            break;
        }
    }

    LocalFree(pSecurityDescriptor);
    return isLocked;
}

// 選取範圍相對於資料夾防護目前鎖定狀態的三種結果：全部都是資料夾但沒鎖的顯示「上鎖」、
// 全部都是資料夾且都鎖的顯示「解鎖」，混合鎖定狀態（或選到檔案）兩個都不顯示——避免使用者
// 搞不清楚這次點下去是要鎖還是解鎖。用一個 enum 存，比兩個獨立布林旗標更不會互相矛盾。
enum class FolderGuardSelectionState { NotApplicable, AllLocked, AllUnlocked, Mixed };

// ---- 這一版加上 IShellExtInit（接收使用者選了哪些檔案）跟 IContextMenu（顯示選單、處理點擊）----
class FileLockerShellExtClass : public IShellExtInit, public IContextMenu
{
public:
    FileLockerShellExtClass() : m_cRef(1) { InterlockedIncrement(&g_cDllRef); }

    // ---- IUnknown（IShellExtInit／IContextMenu 都繼承自 IUnknown，共用同一份實作）----
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IUnknown || riid == IID_IShellExtInit)
        {
            *ppv = static_cast<IShellExtInit*>(this);
        }
        else if (riid == IID_IContextMenu)
        {
            *ppv = static_cast<IContextMenu*>(this);
        }
        else
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    STDMETHODIMP_(ULONG) AddRef() override
    {
        return InterlockedIncrement(&m_cRef);
    }

    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG cRef = InterlockedDecrement(&m_cRef);
        if (cRef == 0)
        {
            delete this;
        }
        return cRef;
    }

    // ---- IShellExtInit：Explorer 把使用者選取的內容包成 IDataObject 傳進來，這裡解析出完整路徑清單 ----
    STDMETHODIMP Initialize(LPCITEMIDLIST /*pidlFolder*/, LPDATAOBJECT pDataObj, HKEY /*hKeyProgID*/) override
    {
        if (pDataObj == nullptr)
        {
            return E_INVALIDARG;
        }

        FORMATETC fmt = { CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL };
        STGMEDIUM stg;
        HRESULT hr = pDataObj->GetData(&fmt, &stg);
        if (FAILED(hr))
        {
            return hr;
        }

        HDROP hDrop = static_cast<HDROP>(GlobalLock(stg.hGlobal));
        if (hDrop == nullptr)
        {
            ReleaseStgMedium(&stg);
            return E_FAIL;
        }

        m_selectedFiles.clear();
        UINT fileCount = DragQueryFileW(hDrop, 0xFFFFFFFF, nullptr, 0);
        for (UINT i = 0; i < fileCount; i++)
        {
            wchar_t path[MAX_PATH];
            if (DragQueryFileW(hDrop, i, path, ARRAYSIZE(path)))
            {
                m_selectedFiles.push_back(path);
            }
        }

        GlobalUnlock(stg.hGlobal);
        ReleaseStgMedium(&stg);

        // 「將所選資料夾上鎖」選單項目只在選取的項目全部是資料夾時才出現（見 QueryContextMenu）——
        // 資料夾防護 v1 不支援單一檔案，混到任何一個檔案就不顯示這個選項，不做「自動忽略檔案」
        // 這種容易讓使用者誤以為全部項目都被處理到的隱性行為（見規劃文件第 5 節）。
        m_allSelectedAreFolders = !m_selectedFiles.empty();
        for (const auto& path : m_selectedFiles)
        {
            DWORD attributes = GetFileAttributesW(path.c_str());
            if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY))
            {
                m_allSelectedAreFolders = false;
                break;
            }
        }

        // 全部都是資料夾時才需要查鎖定狀態——查 ACL 有實際磁碟 I/O 成本，選到檔案時完全不需要。
        m_folderGuardState = FolderGuardSelectionState::NotApplicable;
        if (m_allSelectedAreFolders)
        {
            bool anyLocked = false;
            bool anyUnlocked = false;
            for (const auto& path : m_selectedFiles)
            {
                if (IsFolderGuardLocked(path))
                {
                    anyLocked = true;
                }
                else
                {
                    anyUnlocked = true;
                }
            }

            if (anyLocked && anyUnlocked)
            {
                m_folderGuardState = FolderGuardSelectionState::Mixed;
            }
            else if (anyLocked)
            {
                m_folderGuardState = FolderGuardSelectionState::AllLocked;
            }
            else
            {
                m_folderGuardState = FolderGuardSelectionState::AllUnlocked;
            }
        }

        return S_OK;
    }

    // ---- IContextMenu：顯示選單項目、處理點擊 ----
    STDMETHODIMP QueryContextMenu(HMENU hMenu, UINT indexMenu, UINT idCmdFirst, UINT /*idCmdLast*/, UINT uFlags) override
    {
        if (uFlags & CMF_DEFAULTONLY)
        {
            return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
        }

        InsertMenuW(hMenu, indexMenu, MF_BYPOSITION | MF_STRING, idCmdFirst + 0, GetContextMenuLabel());

        UINT commandCount = 1;
        if (m_folderGuardState == FolderGuardSelectionState::AllLocked)
        {
            InsertMenuW(hMenu, indexMenu + 1, MF_BYPOSITION | MF_STRING, idCmdFirst + 1, GetUnlockFolderMenuLabel());
            commandCount = 2;
        }
        else if (m_folderGuardState == FolderGuardSelectionState::AllUnlocked)
        {
            InsertMenuW(hMenu, indexMenu + 1, MF_BYPOSITION | MF_STRING, idCmdFirst + 1, GetLockFolderMenuLabel());
            commandCount = 2;
        }
        // Mixed（有些鎖有些沒鎖）：兩個都不顯示，只留「加密」，避免使用者搞不清楚這次點下去的動作。

        // 回傳值代表我們加了幾個命令 id，Explorer 靠這個數字知道下一個外掛可以從哪個 id 開始用。
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, commandCount);
    }

    STDMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO pici) override
    {
        // lpVerb 高位元組不是 0 代表呼叫端是用「字串動詞」而不是數字 id 呼叫，我們沒有註冊字串動詞，不支援。
        if (HIWORD(pici->lpVerb) != 0)
        {
            return E_INVALIDARG;
        }

        // command 0 = 加密（既有行為，不帶任何旗標）；command 1 = 資料夾防護上鎖或解鎖，帶
        // --folder-guard-lock／--folder-guard-unlock 旗標讓 App 端（App.xaml.cs HandleLaunchArgs）
        // 分辨這次啟動要做什麼——命令 id 1 實際代表哪個動作，看 QueryContextMenu 當時算出的
        // m_folderGuardState，這是同一個 COM 物件實例、同一次選單顯示週期，狀態不會變動。
        UINT commandId = LOWORD(pici->lpVerb);
        if (commandId > 1)
        {
            return E_INVALIDARG;
        }
        const wchar_t* extraArgPrefix = L"";
        if (commandId == 1)
        {
            extraArgPrefix = (m_folderGuardState == FolderGuardSelectionState::AllLocked)
                ? L" --folder-guard-unlock"
                : L" --folder-guard-lock";
        }

        if (m_selectedFiles.empty())
        {
            return S_OK;
        }

        std::wstring appPath = GetFileLockerAppPath();
        if (appPath.empty())
        {
            MessageBoxW(pici->hwnd, L"找不到 FileLocker.App.exe，請確認主程式已經編譯過。", L"FileLocker", MB_OK | MB_ICONERROR);
            return S_OK;
        }

        // 對應規格文件第 5.2 節決定的門檻：預估命令列長度超過 8000 字元，或選取項目數量超過 50 個，
        // 改成把完整路徑清單寫進一個暫存 txt 檔，只把這個檔案路徑（前面加 @ 當標記）當成單一命令列參數傳遞。
        size_t estimatedLength = appPath.length();
        for (const auto& path : m_selectedFiles)
        {
            estimatedLength += path.length() + 3; // 引號 + 空白
        }

        std::wstring commandLine;

        if (estimatedLength > 8000 || m_selectedFiles.size() > 50)
        {
            wchar_t tempDir[MAX_PATH];
            GetTempPathW(ARRAYSIZE(tempDir), tempDir);
            wchar_t tempFileName[MAX_PATH];
            GetTempFileNameW(tempDir, L"FLK", 0, tempFileName);

            HANDLE hFile = CreateFileW(tempFileName, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (hFile == INVALID_HANDLE_VALUE)
            {
                MessageBoxW(pici->hwnd, L"建立暫存清單檔失敗。", L"FileLocker", MB_OK | MB_ICONERROR);
                return S_OK;
            }

            wchar_t bom = 0xFEFF; // UTF-16 LE BOM，讓 C# 那邊讀檔時能正確判斷編碼。
            DWORD written;
            WriteFile(hFile, &bom, sizeof(bom), &written, nullptr);

            for (const auto& path : m_selectedFiles)
            {
                std::wstring line = path + L"\r\n";
                WriteFile(hFile, line.c_str(), (DWORD)(line.size() * sizeof(wchar_t)), &written, nullptr);
            }
            CloseHandle(hFile);

            commandLine = QuoteArgument(appPath) + extraArgPrefix + L" " + QuoteArgument(L"@" + std::wstring(tempFileName));
        }
        else
        {
            commandLine = QuoteArgument(appPath) + extraArgPrefix;
            for (const auto& path : m_selectedFiles)
            {
                commandLine += L" " + QuoteArgument(path);
            }
        }

        STARTUPINFOW si = { sizeof(si) };
        PROCESS_INFORMATION pi = {};

        std::vector<wchar_t> mutableCmd(commandLine.begin(), commandLine.end());
        mutableCmd.push_back(L'\0');

        if (CreateProcessW(nullptr, mutableCmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
        {
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
        else
        {
            MessageBoxW(pici->hwnd, L"啟動 FileLocker 失敗。", L"FileLocker", MB_OK | MB_ICONERROR);
        }

        return S_OK;
    }

    STDMETHODIMP GetCommandString(UINT_PTR idCmd, UINT uFlags, UINT* /*pReserved*/, LPSTR pszName, UINT cchMax) override
    {
        if (uFlags == GCS_HELPTEXTW)
        {
            const wchar_t* helpText;
            if (idCmd == 1 && m_folderGuardState == FolderGuardSelectionState::AllLocked)
            {
                helpText = IsSystemUiChinese()
                    ? L"解除此資料夾的存取限制"
                    : L"Remove the access restriction on this folder";
            }
            else if (idCmd == 1)
            {
                helpText = IsSystemUiChinese()
                    ? L"限制存取此資料夾，不加密內容"
                    : L"Restrict access to this folder without encrypting it";
            }
            else
            {
                helpText = IsSystemUiChinese()
                    ? L"用 FileLocker 加密選取的項目"
                    : L"Encrypt the selected items with FileLocker";
            }
            StringCchCopyW(reinterpret_cast<LPWSTR>(pszName), cchMax, helpText);
            return S_OK;
        }
        return E_NOTIMPL;
    }

private:
    ~FileLockerShellExtClass() { InterlockedDecrement(&g_cDllRef); }
    long m_cRef;
    std::vector<std::wstring> m_selectedFiles;
    bool m_allSelectedAreFolders = false;
    FolderGuardSelectionState m_folderGuardState = FolderGuardSelectionState::NotApplicable;
};

// ---- Class Factory：COM 標準機制，負責「生出」上面那個類別的實體 ----
class FileLockerClassFactory : public IClassFactory
{
public:
    FileLockerClassFactory() : m_cRef(1) { InterlockedIncrement(&g_cDllRef); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_cRef); }

    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG cRef = InterlockedDecrement(&m_cRef);
        if (cRef == 0)
        {
            delete this;
        }
        return cRef;
    }

    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        *ppv = nullptr;
        if (pUnkOuter != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }

        auto* pExt = new (std::nothrow) FileLockerShellExtClass();
        if (pExt == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        HRESULT hr = pExt->QueryInterface(riid, ppv);
        pExt->Release();
        return hr;
    }

    STDMETHODIMP LockServer(BOOL fLock) override
    {
        if (fLock)
        {
            InterlockedIncrement(&g_cDllRef);
        }
        else
        {
            InterlockedDecrement(&g_cDllRef);
        }
        return S_OK;
    }

private:
    ~FileLockerClassFactory() { InterlockedDecrement(&g_cDllRef); }
    long m_cRef;
};

// ---- DLL 標準匯出函式 ----

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hModule = hModule;
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    *ppv = nullptr;

    if (IsEqualCLSID(rclsid, CLSID_FileLockerShellExtension))
    {
        auto* pFactory = new (std::nothrow) FileLockerClassFactory();
        if (pFactory == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        HRESULT hr = pFactory->QueryInterface(riid, ppv);
        pFactory->Release();
        return hr;
    }

    return CLASS_E_CLASSNOTAVAILABLE;
}

STDAPI DllCanUnloadNow()
{
    return (g_cDllRef == 0) ? S_OK : S_FALSE;
}

// ---- 註冊／解除註冊：CLSID 本身 + *\shellex\ContextMenuHandlers\FileLocker 這個掛勾兩個都要寫 ----
//
// 特意寫進 HKEY_CURRENT_USER\Software\Classes，不是 HKEY_CLASSES_ROOT——這是 Windows 官方支援
// 的每個使用者各自登錄的機制，Explorer 會自動把它併進當前使用者看到的 HKEY_CLASSES_ROOT
// 合併視圖裡，效果完全一樣，但不需要系統管理員權限。這樣一來，正式版可以讓 FileLocker.App
// 自己在啟動時檢查、需要的話就自己註冊（見 FileLocker.App 裡的 ShellExtensionRegistrar），
// 不需要安裝程式知道任何 COM 相關的事；開發階段用 regsvr32 手動測試也不用再開系統管理員權限。

STDAPI DllRegisterServer()
{
    wchar_t modulePath[MAX_PATH];
    if (GetModuleFileNameW(g_hModule, modulePath, ARRAYSIZE(modulePath)) == 0)
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    wchar_t clsidStr[64];
    StringFromGUID2(CLSID_FileLockerShellExtension, clsidStr, ARRAYSIZE(clsidStr));

    // 1. Software\Classes\CLSID\{...}\InprocServer32 = 這個 DLL 的路徑
    wchar_t clsidKeyPath[160];
    StringCchPrintfW(clsidKeyPath, ARRAYSIZE(clsidKeyPath), L"Software\\Classes\\CLSID\\%s\\InprocServer32", clsidStr);

    HKEY hKeyClsid;
    LSTATUS status = RegCreateKeyExW(HKEY_CURRENT_USER, clsidKeyPath, 0, nullptr, 0, KEY_WRITE, nullptr, &hKeyClsid, nullptr);
    if (status != ERROR_SUCCESS)
    {
        return HRESULT_FROM_WIN32(status);
    }
    RegSetValueExW(hKeyClsid, nullptr, 0, REG_SZ, (const BYTE*)modulePath, (DWORD)((wcslen(modulePath) + 1) * sizeof(wchar_t)));
    RegSetValueExW(hKeyClsid, L"ThreadingModel", 0, REG_SZ, (const BYTE*)L"Apartment", (DWORD)((wcslen(L"Apartment") + 1) * sizeof(wchar_t)));
    RegCloseKey(hKeyClsid);

    // 2. Software\Classes\*\shellex\ContextMenuHandlers\FileLocker = CLSID 字串——這行才是真正「掛」到右鍵選單上的關鍵。
    HKEY hKeyHandler;
    status = RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\Classes\\*\\shellex\\ContextMenuHandlers\\FileLocker", 0, nullptr, 0, KEY_WRITE, nullptr, &hKeyHandler, nullptr);
    if (status != ERROR_SUCCESS)
    {
        return HRESULT_FROM_WIN32(status);
    }
    RegSetValueExW(hKeyHandler, nullptr, 0, REG_SZ, (const BYTE*)clsidStr, (DWORD)((wcslen(clsidStr) + 1) * sizeof(wchar_t)));
    RegCloseKey(hKeyHandler);

    return S_OK;
}

STDAPI DllUnregisterServer()
{
    wchar_t clsidStr[64];
    StringFromGUID2(CLSID_FileLockerShellExtension, clsidStr, ARRAYSIZE(clsidStr));

    wchar_t clsidKeyPath[160];
    StringCchPrintfW(clsidKeyPath, ARRAYSIZE(clsidKeyPath), L"Software\\Classes\\CLSID\\%s", clsidStr);
    RegDeleteTreeW(HKEY_CURRENT_USER, clsidKeyPath);

    RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\Classes\\*\\shellex\\ContextMenuHandlers\\FileLocker");

    return S_OK;
}