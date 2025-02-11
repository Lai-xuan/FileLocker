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

            MessageBox.Show(".locked ���ɦW�w���\���p�� FileLockerApp�I", "���\", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("�]�w���ɦW���p�ɵo�Ϳ��~�G" + ex.Message, "���~", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
