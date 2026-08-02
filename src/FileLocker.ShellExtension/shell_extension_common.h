#pragma once

#include <windows.h>
#include <string>
#include <vector>

// dllmain.cpp 裡定義（DllMain 裡指派），這個標頭裡的 GetFileLockerAppPath 也需要用來找
// FileLocker.App.exe 的路徑。
extern HMODULE g_hModule;

// dllmain.cpp 裡定義，DllCanUnloadNow 用來判斷 DLL 還有沒有物件存活。
extern LONG g_cDllRef;

/// <summary>
/// 正確處理 Windows 命令列參數的引號逃脫，比照微軟官方文件的標準演算法——
/// 單純用「路徑前後各包一個雙引號」在路徑結尾剛好是奇數個反斜線時會出錯（那個反斜線會
/// 逃脫掉我們補上去的關閉引號，導致這個參數沒有真的結束、後面的參數解析全部跟著錯亂）。
/// NTFS 檔名本身不能包含雙引號，但這裡還是做完整處理，不只賭「檔名不會有問題字元」。
/// </summary>
inline std::wstring QuoteArgument(const std::wstring& argument)
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
inline std::wstring GetFileLockerAppPath()
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

/// <summary>
/// 依系統 UI 語言決定選單文字：用 GetUserDefaultUILanguage（Explorer 本身顯示語言）而不是
/// GetSystemDefaultUILanguage（系統安裝語言，可能跟目前登入使用者顯示的語言不同）——
/// 這裡只需要跟使用者「看到的」Explorer 介面語言一致。App 目前只支援中／英兩種語言，
/// 非中文一律回退英文，不需要額外判斷其他語系。
/// </summary>
inline bool IsSystemUiChinese()
{
    return PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_CHINESE;
}
