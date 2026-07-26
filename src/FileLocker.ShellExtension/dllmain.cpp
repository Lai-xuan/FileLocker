#include <windows.h>
#include <objbase.h>
#include <shlobj.h>
#include <shellapi.h>
#include <strsafe.h>
#include <new>
#include <vector>
#include <string>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")

// FileLocker Shell Extension 的 CLSID，這個專案專屬的識別碼，跟其他程式不會撞。
// {A1B2C3D4-E5F6-4789-9ABC-DEF012345678}
static const CLSID CLSID_FileLockerShellExtension =
{ 0xA1B2C3D4, 0xE5F6, 0x4789, { 0x9A, 0xBC, 0xDE, 0xF0, 0x12, 0x34, 0x56, 0x78 } };

static LONG g_cDllRef = 0;
static HMODULE g_hModule = nullptr;

/// <summary>
/// 正確處理 Windows 命令列參數的引號逃脫，比照微軟官方文件的標準演算法——
/// 單純用「路徑前後各包一個雙引號」在路徑結尾剛好是奇數個反斜線時會出錯（那個反斜線會
/// 逃脫掉我們補上去的關閉引號，導致這個參數沒有真的結束、後面的參數解析全部跟著錯亂）。
/// NTFS 檔名本身不能包含雙引號，但這裡還是做完整處理，不只賭「檔名不會有問題字元」。
/// </summary>
static std::wstring QuoteArgument(const std::wstring& argument)
{
    std::wstring result = L"\"";
    for (auto it = argument.begin(); ; ++it)
    {
        unsigned backslashes = 0;
        while (it != argument.end() && *it == L'\\')
        {
            ++it;
            ++backslashes;
        }

        if (it == argument.end())
        {
            // 結尾的反斜線後面接的是我們要補上的關閉引號，反斜線數量要翻倍，
            // 不然會被解析成「逃脫掉關閉引號」，這個參數就不會真的結束。
            result.append(backslashes * 2, L'\\');
            break;
        }
        else if (*it == L'"')
        {
            result.append(backslashes * 2 + 1, L'\\');
            result.push_back(L'"');
        }
        else
        {
            result.append(backslashes, L'\\');
            result.push_back(*it);
        }
    }
    result.push_back(L'"');
    return result;
}

/// <summary>
/// 找 FileLocker.App.exe 在哪裡：跟這個 Shell Extension DLL 放在同一個資料夾——
/// 正式安裝後兩者會被安裝程式放在同一個「應用程式內容資料夾」裡（見規格文件第 5.2、13 節），
/// 開發階段用 regsvr32 手動註冊測試時，也是先手動把編譯出來的 DLL 複製到跟 FileLocker.App.exe
/// 同一個資料夾（見 FileLocker.App.csproj 的 CopyShellExtensionDll Target），所以這一條路徑
/// 涵蓋開發與正式兩種情境，不需要另外寫死本機開發路徑當備援（那個路徑只在特定一台機器上有效，
/// 而且會把開發機的資料夾結構打包進正式發行的 DLL 裡，沒必要）。
/// </summary>
static std::wstring GetFileLockerAppPath()
{
    wchar_t modulePath[MAX_PATH];
    GetModuleFileNameW(g_hModule, modulePath, ARRAYSIZE(modulePath));
    std::wstring dllDir = modulePath;
    size_t pos = dllDir.find_last_of(L"\\/");
    if (pos != std::wstring::npos)
    {
        dllDir = dllDir.substr(0, pos + 1);
    }

    std::wstring candidate = dllDir + L"FileLocker.App.exe";
    if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES)
    {
        return candidate;
    }

    return L"";
}

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

        return S_OK;
    }

    // ---- IContextMenu：顯示選單項目、處理點擊 ----
    STDMETHODIMP QueryContextMenu(HMENU hMenu, UINT indexMenu, UINT idCmdFirst, UINT /*idCmdLast*/, UINT uFlags) override
    {
        if (uFlags & CMF_DEFAULTONLY)
        {
            return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
        }

        InsertMenuW(hMenu, indexMenu, MF_BYPOSITION | MF_STRING, idCmdFirst, L"使用 FileLocker 加密");

        // 回傳值代表我們加了幾個命令 id（這裡只加了一個），Explorer 靠這個數字知道下一個外掛可以從哪個 id 開始用。
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 1);
    }

    STDMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO pici) override
    {
        // lpVerb 高位元組不是 0 代表呼叫端是用「字串動詞」而不是數字 id 呼叫，我們沒有註冊字串動詞，不支援。
        if (HIWORD(pici->lpVerb) != 0)
        {
            return E_INVALIDARG;
        }
        if (LOWORD(pici->lpVerb) != 0)
        {
            return E_INVALIDARG;
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

            commandLine = QuoteArgument(appPath) + L" " + QuoteArgument(L"@" + std::wstring(tempFileName));
        }
        else
        {
            commandLine = QuoteArgument(appPath);
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

    STDMETHODIMP GetCommandString(UINT_PTR /*idCmd*/, UINT uFlags, UINT* /*pReserved*/, LPSTR pszName, UINT cchMax) override
    {
        if (uFlags == GCS_HELPTEXTW)
        {
            StringCchCopyW(reinterpret_cast<LPWSTR>(pszName), cchMax, L"用 FileLocker 加密選取的項目");
            return S_OK;
        }
        return E_NOTIMPL;
    }

private:
    ~FileLockerShellExtClass() { InterlockedDecrement(&g_cDllRef); }
    long m_cRef;
    std::vector<std::wstring> m_selectedFiles;
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
    if (!IsEqualCLSID(rclsid, CLSID_FileLockerShellExtension))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* pFactory = new (std::nothrow) FileLockerClassFactory();
    if (pFactory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hr = pFactory->QueryInterface(riid, ppv);
    pFactory->Release();
    return hr;
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