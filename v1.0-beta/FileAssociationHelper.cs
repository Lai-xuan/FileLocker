using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

public static class FileAssociationHelper
{
    public static void RegisterFileAssociation()
    {
        try
        {
            string appPath = Process.GetCurrentProcess().MainModule.FileName;
            string extension = ".locked";
            string progID = "FileLockerApp.lockedfile";

            Registry.SetValue(@"HKEY_CLASSES_ROOT\" + extension, "", progID);
            Registry.SetValue(@"HKEY_CLASSES_ROOT\" + extension, "PerceivedType", "document");

            string command = "\"" + appPath + "\" \"%1\"";
            Registry.SetValue(@"HKEY_CLASSES_ROOT\" + progID, "", "Locked File");
            Registry.SetValue(@"HKEY_CLASSES_ROOT\" + progID + @"\shell\open\command", "", command);

            MessageBox.Show(".locked 副檔名已成功關聯到 FileLockerApp！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("設定副檔名關聯時發生錯誤：" + ex.Message, "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
