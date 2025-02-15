using System;
using System.IO;
using System.Windows;

namespace FileLockerApp
{
    public partial class QuickUnlockWindow : Window
    {
        private string lockedFilePath;

        public QuickUnlockWindow(string filePath)
        {
            InitializeComponent();
            lockedFilePath = filePath;
        }

        private void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            string password = PasswordBox.Password;

            // 這裡要加解密檢查
            if (IsCorrectPassword(password))
            {
                // 解鎖檔案
                UnlockFile(lockedFilePath, password);
                MessageBox.Show("解鎖成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            else
            {
                MessageBox.Show("密碼錯誤！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool IsCorrectPassword(string password)
        {
            // 這裡是假的密碼檢查，之後要換成真實的解密邏輯
            return password == "1234"; // 測試密碼
        }

        private void UnlockFile(string filePath, string password)
        {
            // 解鎖邏輯（這部分之後要和加密/解密系統整合）
            string originalFilePath = filePath.Replace(".locked", ""); // 假設解鎖後去掉 .locked
            File.Move(filePath, originalFilePath);
        }
    }
}
