#pragma once

#include <windows.h>
#include <objbase.h>

// 「雙擊已上鎖資料夾直接解鎖」用的命名空間擴充 CLSID，跟 dllmain.cpp 的右鍵選單 CLSID
// 是完全獨立的兩個 COM 類別——這個 CLSID 要跟 desktop.ini 裡寫的、跟
// FolderGuardNamespaceMarker.cs 裡的 NamespaceClsid 保持完全一致。
// {2A4376E0-C5FC-4126-8ACD-9FC8AA377AC1}
extern const CLSID CLSID_FolderGuardNamespaceFolder;

/// <summary>DllGetClassObject 分派到這個 CLSID 時呼叫，建立命名空間資料夾的 Class Factory。</summary>
HRESULT CreateFolderGuardNamespaceClassFactory(REFIID riid, void** ppv);
