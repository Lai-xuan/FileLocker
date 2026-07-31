#include <windows.h>
#include <objbase.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <strsafe.h>
#include <new>
#include <string>
#include "shell_extension_common.h"
#include "folderguard_namespace.h"

#pragma comment(lib, "shlwapi.lib")

// {2A4376E0-C5FC-4126-8ACD-9FC8AA377AC1}
const CLSID CLSID_FolderGuardNamespaceFolder =
{ 0x2a4376e0, 0xc5fc, 0x4126, { 0x8a, 0xcd, 0x9f, 0xc8, 0xaa, 0x37, 0x7a, 0xc1 } };

// ---- 空的 IEnumIDList：這個命名空間物件不支援瀏覽子項目，EnumObjects 一律回傳「沒有項目」----
class EmptyEnumIDList : public IEnumIDList
{
public:
    EmptyEnumIDList() : m_cRef(1) { InterlockedIncrement(&g_cDllRef); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IUnknown || riid == IID_IEnumIDList)
        {
            *ppv = static_cast<IEnumIDList*>(this);
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
        if (cRef == 0) delete this;
        return cRef;
    }

    STDMETHODIMP Next(ULONG, LPITEMIDLIST*, ULONG* pceltFetched) override
    {
        if (pceltFetched) *pceltFetched = 0;
        return S_FALSE;
    }
    STDMETHODIMP Skip(ULONG) override { return S_FALSE; }
    STDMETHODIMP Reset() override { return S_OK; }
    STDMETHODIMP Clone(IEnumIDList** ppEnum) override
    {
        *ppEnum = new (std::nothrow) EmptyEnumIDList();
        return *ppEnum ? S_OK : E_OUTOFMEMORY;
    }

private:
    ~EmptyEnumIDList() { InterlockedDecrement(&g_cDllRef); }
    long m_cRef;
};

// ---- 雙擊/右鍵這個命名空間物件時用的最小 IContextMenu：只有一個命令「解鎖」，且一律是預設命令 ----
// 這樣雙擊會直接觸發 InvokeCommand（跟右鍵選單既有的解鎖流程走同一支 FileLocker.App.exe
// --folder-guard-unlock），不會讓 Explorer 嘗試呼叫 CreateViewObject 建立瀏覽檢視。
class FolderGuardNamespaceContextMenu : public IContextMenu
{
public:
    explicit FolderGuardNamespaceContextMenu(std::wstring path)
        : m_cRef(1), m_path(std::move(path))
    {
        InterlockedIncrement(&g_cDllRef);
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IUnknown || riid == IID_IContextMenu)
        {
            *ppv = static_cast<IContextMenu*>(this);
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
        if (cRef == 0) delete this;
        return cRef;
    }

    STDMETHODIMP QueryContextMenu(HMENU hMenu, UINT indexMenu, UINT idCmdFirst, UINT /*idCmdLast*/, UINT /*uFlags*/) override
    {
        // 這裡刻意不理會 CMF_DEFAULTONLY 提早跳出——我們唯一的命令本來就該是預設命令，
        // Explorer 問「只要預設」的時候也一樣要插入這個項目，不然雙擊會沒有命令可用。
        const wchar_t* label = IsSystemUiChinese() ? L"解鎖" : L"Unlock";
        InsertMenuW(hMenu, indexMenu, MF_BYPOSITION | MF_STRING, idCmdFirst + 0, label);
        SetMenuDefaultItem(hMenu, 0, MF_BYPOSITION);
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 1);
    }

    STDMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO pici) override
    {
        if (HIWORD(pici->lpVerb) != 0 || LOWORD(pici->lpVerb) != 0)
        {
            return E_INVALIDARG;
        }
        LaunchFileLockerApp(L"--folder-guard-unlock", m_path, pici->hwnd);
        return S_OK;
    }

    STDMETHODIMP GetCommandString(UINT_PTR /*idCmd*/, UINT uFlags, UINT* /*pReserved*/, LPSTR pszName, UINT cchMax) override
    {
        if (uFlags == GCS_HELPTEXTW)
        {
            const wchar_t* text = IsSystemUiChinese()
                ? L"解除此資料夾的存取限制"
                : L"Remove the access restriction on this folder";
            StringCchCopyW(reinterpret_cast<LPWSTR>(pszName), cchMax, text);
            return S_OK;
        }
        return E_NOTIMPL;
    }

private:
    ~FolderGuardNamespaceContextMenu() { InterlockedDecrement(&g_cDllRef); }
    long m_cRef;
    std::wstring m_path;
};

// ---- 命名空間資料夾本體：IPersistFolder2 接收 Explorer 綁定時給的 PIDL，IShellFolder 回答
// Explorer 問的各種問題。刻意做成「不可瀏覽的葉節點」——不支援列舉/綁定子項目，只回答
// 自己的顯示名稱、屬性，以及雙擊/右鍵要用的 IContextMenu（見上面）。----
class FolderGuardNamespaceFolder : public IShellFolder, public IPersistFolder2
{
public:
    FolderGuardNamespaceFolder() : m_cRef(1) { InterlockedIncrement(&g_cDllRef); }

    // ---- IUnknown ----
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IUnknown || riid == IID_IShellFolder)
        {
            *ppv = static_cast<IShellFolder*>(this);
        }
        else if (riid == IID_IPersist || riid == IID_IPersistFolder || riid == IID_IPersistFolder2)
        {
            *ppv = static_cast<IPersistFolder2*>(this);
        }
        else
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_cRef); }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG cRef = InterlockedDecrement(&m_cRef);
        if (cRef == 0) delete this;
        return cRef;
    }

    // ---- IPersist ----
    STDMETHODIMP GetClassID(CLSID* pClassID) override
    {
        *pClassID = CLSID_FolderGuardNamespaceFolder;
        return S_OK;
    }

    // ---- IPersistFolder：Explorer 綁定這個資料夾時呼叫，把代表這個資料夾的 PIDL 交給我們 ----
    STDMETHODIMP Initialize(LPCITEMIDLIST pidl) override
    {
        if (m_pidl != nullptr)
        {
            ILFree(m_pidl);
            m_pidl = nullptr;
        }
        m_pidl = ILClone(pidl);

        wchar_t path[MAX_PATH];
        if (SHGetPathFromIDListW(pidl, path))
        {
            m_path = path;
        }

        return S_OK;
    }

    // ---- IPersistFolder2 ----
    STDMETHODIMP GetCurFolder(LPITEMIDLIST* ppidl) override
    {
        *ppidl = ILClone(m_pidl);
        return *ppidl != nullptr ? S_OK : E_OUTOFMEMORY;
    }

    // ---- IShellFolder：不支援瀏覽/綁定子項目，只回答自己的顯示名稱、屬性、右鍵/雙擊命令 ----
    STDMETHODIMP ParseDisplayName(HWND, LPBC, LPWSTR, ULONG*, LPITEMIDLIST*, ULONG*) override
    {
        return E_NOTIMPL;
    }

    STDMETHODIMP EnumObjects(HWND, SHCONTF, IEnumIDList** ppEnumIDList) override
    {
        *ppEnumIDList = new (std::nothrow) EmptyEnumIDList();
        return *ppEnumIDList != nullptr ? S_OK : E_OUTOFMEMORY;
    }

    STDMETHODIMP BindToObject(LPCITEMIDLIST, LPBC, REFIID, void** ppv) override
    {
        *ppv = nullptr;
        return E_NOTIMPL;
    }

    STDMETHODIMP BindToStorage(LPCITEMIDLIST, LPBC, REFIID, void** ppv) override
    {
        *ppv = nullptr;
        return E_NOTIMPL;
    }

    STDMETHODIMP CompareIDs(LPARAM, LPCITEMIDLIST, LPCITEMIDLIST) override
    {
        // 沒有子項目可比較，一律回報相等——真的被呼叫到多半代表 Explorer 在做我們沒預期的
        // 事，回傳「相等」是最保守、最不會導致排序邏輯出錯的答案。
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
    }

    STDMETHODIMP CreateViewObject(HWND, REFIID, void** ppv) override
    {
        // 刻意不支援建立瀏覽檢視——雙擊要透過 GetUIObjectOf 拿到的 IContextMenu 預設命令處理，
        // 不要讓 Explorer 建立一個空蕩蕩的資料夾檢視。
        *ppv = nullptr;
        return E_NOTIMPL;
    }

    STDMETHODIMP GetAttributesOf(UINT, LPCITEMIDLIST*, SFGAOF* rgfInOut) override
    {
        // 一般資料夾一定同時回報 FOLDER | FILESYSTEM | FILESYSANCESTOR——只回 SFGAO_FOLDER
        // 少了 FILESYSTEM，Explorer 很可能把這個物件當成非檔案系統的抽象項目，不會走一般的
        // 雙擊預設動作解析流程，導致雙擊/右鍵完全沒有反應。不設 SFGAO_BROWSABLE／
        // SFGAO_HASSUBFOLDER，避免 Explorer 嘗試在左側導覽樹展開或建立瀏覽檢視。
        *rgfInOut = SFGAO_FOLDER | SFGAO_FILESYSTEM | SFGAO_FILESYSANCESTOR;
        return S_OK;
    }

    STDMETHODIMP GetUIObjectOf(HWND /*hwndOwner*/, UINT /*cidl*/, PCUITEMID_CHILD_ARRAY /*apidl*/,
        REFIID riid, UINT* /*prgfInOut*/, void** ppv) override
    {
        *ppv = nullptr;

        if (riid == IID_IContextMenu)
        {
            auto* pMenu = new (std::nothrow) FolderGuardNamespaceContextMenu(m_path);
            if (pMenu == nullptr)
            {
                return E_OUTOFMEMORY;
            }
            HRESULT hr = pMenu->QueryInterface(riid, ppv);
            pMenu->Release();
            return hr;
        }

        return E_NOTIMPL;
    }

    STDMETHODIMP GetDisplayNameOf(LPCITEMIDLIST pidl, SHGDNF /*uFlags*/, STRRET* pName) override
    {
        std::wstring name;
        wchar_t path[MAX_PATH];
        if (pidl != nullptr && SHGetPathFromIDListW(pidl, path))
        {
            name = PathFindFileNameW(path);
        }
        else if (!m_path.empty())
        {
            name = PathFindFileNameW(m_path.c_str());
        }

        pName->uType = STRRET_WSTR;
        pName->pOleStr = static_cast<LPWSTR>(CoTaskMemAlloc((name.size() + 1) * sizeof(wchar_t)));
        if (pName->pOleStr == nullptr)
        {
            return E_OUTOFMEMORY;
        }
        StringCchCopyW(pName->pOleStr, name.size() + 1, name.c_str());
        return S_OK;
    }

    STDMETHODIMP SetNameOf(HWND, LPCITEMIDLIST, LPCWSTR, SHGDNF, LPITEMIDLIST*) override
    {
        // 不支援透過命名空間物件重新命名——使用者可以先解鎖回普通資料夾再改名。
        return E_NOTIMPL;
    }

private:
    ~FolderGuardNamespaceFolder()
    {
        if (m_pidl != nullptr)
        {
            ILFree(m_pidl);
        }
        InterlockedDecrement(&g_cDllRef);
    }
    long m_cRef;
    LPITEMIDLIST m_pidl = nullptr;
    std::wstring m_path;
};

// ---- Class Factory：跟 dllmain.cpp 的 FileLockerClassFactory 同一套標準寫法 ----
class FolderGuardNamespaceClassFactory : public IClassFactory
{
public:
    FolderGuardNamespaceClassFactory() : m_cRef(1) { InterlockedIncrement(&g_cDllRef); }

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
        if (cRef == 0) delete this;
        return cRef;
    }

    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        *ppv = nullptr;
        if (pUnkOuter != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }

        auto* pFolder = new (std::nothrow) FolderGuardNamespaceFolder();
        if (pFolder == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        HRESULT hr = pFolder->QueryInterface(riid, ppv);
        pFolder->Release();
        return hr;
    }

    STDMETHODIMP LockServer(BOOL fLock) override
    {
        if (fLock) InterlockedIncrement(&g_cDllRef);
        else InterlockedDecrement(&g_cDllRef);
        return S_OK;
    }

private:
    ~FolderGuardNamespaceClassFactory() { InterlockedDecrement(&g_cDllRef); }
    long m_cRef;
};

HRESULT CreateFolderGuardNamespaceClassFactory(REFIID riid, void** ppv)
{
    auto* pFactory = new (std::nothrow) FolderGuardNamespaceClassFactory();
    if (pFactory == nullptr)
    {
        return E_OUTOFMEMORY;
    }
    HRESULT hr = pFactory->QueryInterface(riid, ppv);
    pFactory->Release();
    return hr;
}
