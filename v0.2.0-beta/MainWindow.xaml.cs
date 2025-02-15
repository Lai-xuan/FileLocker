using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Security.Cryptography;
using System.IO.Compression;

namespace FileLockerApp
{
    public partial class MainWindow : Window
    {
        private bool isPasswordVisible = false;

        public MainWindow()
        {
            InitializeComponent();
        }
        
        // 🏠 前往主畫面
        private void GoToMain(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("前往主畫面");
        }

        // ⚙️ 前往設定
        private void GoToSettings(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("前往設定");
        }

        // 🔒 前往加密中的檔案
        private void GoToLockedFiles(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("前往加密中的檔案");
        }

        // 🔹 選擇檔案
        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            if (openFileDialog.ShowDialog() == true)
            {
                FilePathTextBox.Text = openFileDialog.FileName;
            }
        }

        // 🔹 選擇資料夾
        private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (System.Windows.Forms.FolderBrowserDialog folderDialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    FilePathTextBox.Text = folderDialog.SelectedPath;
                }
            }
        }

        // 👁 切換密碼顯示
        private void TogglePasswordVisibility(object sender, RoutedEventArgs e)
        {
            if (isPasswordVisible)
            {
                PasswordBox.Visibility = Visibility.Visible;
                PasswordTextBox.Visibility = Visibility.Collapsed;
                PasswordBox.Password = PasswordTextBox.Text;
            }
            else
            {
                PasswordBox.Visibility = Visibility.Collapsed;
                PasswordTextBox.Visibility = Visibility.Visible;
                PasswordTextBox.Text = PasswordBox.Password;
            }
            isPasswordVisible = !isPasswordVisible;
        }

        // 🔐 鎖定檔案或資料夾
        private void LockFileButton_Click(object sender, RoutedEventArgs e)
        {
            string filePath = FilePathTextBox.Text;
            string password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                MessageBox.Show("請選擇要鎖定的檔案或資料夾！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("請輸入密碼！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                string encryptedFilePath;

                if (File.Exists(filePath))
                {
                    // ✅ 這是「檔案」，直接加密
                    encryptedFilePath = filePath + ".locked";
                    EncryptFile(filePath, encryptedFilePath, password);
                    File.Delete(filePath);
                }
                else if (Directory.Exists(filePath))
                {
                    // ✅ 這是「資料夾」，先壓縮再加密
                    string zipPath = filePath + ".zip";
                    if (File.Exists(zipPath)) File.Delete(zipPath); // 如果已有同名壓縮檔，先刪除
                    ZipFile.CreateFromDirectory(filePath, zipPath);

                    encryptedFilePath = zipPath + ".locked";
                    EncryptFile(zipPath, encryptedFilePath, password);
                    File.Delete(zipPath);
                    Directory.Delete(filePath, true); // 刪除原資料夾
                }
                else
                {
                    MessageBox.Show("無效的路徑！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                MessageBox.Show("檔案/資料夾已成功鎖定！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                FilePathTextBox.Text = "";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"鎖定失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 🔓 解鎖檔案
        private void UnlockFileButton_Click(object sender, RoutedEventArgs e)
        {
            string filePath = FilePathTextBox.Text;
            string password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show("請選擇要解鎖的檔案！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("請輸入密碼！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!filePath.EndsWith(".locked"))
            {
                MessageBox.Show("這不是受鎖定的檔案！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                string decryptedFilePath = filePath.Substring(0, filePath.Length - 7); // 去掉 .locked
                DecryptFile(filePath, decryptedFilePath, password);
                File.Delete(filePath);

                // 🔄 如果是 ZIP 檔，解壓縮
                if (decryptedFilePath.EndsWith(".zip"))
                {
                    string folderPath = decryptedFilePath.Substring(0, decryptedFilePath.Length - 4); // 去掉 .zip
                    ZipFile.ExtractToDirectory(decryptedFilePath, folderPath);
                    File.Delete(decryptedFilePath); // 刪除 zip 檔
                }

                MessageBox.Show("檔案/資料夾已成功解鎖！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                FilePathTextBox.Text = "";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"解鎖失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 📌 **使用 PBKDF2 進行檔案加密**
        private void EncryptFile(string inputFile, string outputFile, string password)
        {
            byte[] salt = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt); // 產生隨機 Salt
            }

            using (Rfc2898DeriveBytes pdb = new Rfc2898DeriveBytes(password, salt, 100000))
            {
                byte[] key = pdb.GetBytes(32);
                byte[] iv = pdb.GetBytes(16);

                using (FileStream outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                {
                    outputStream.Write(salt, 0, salt.Length); // 儲存 Salt
                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = key;
                        aes.IV = iv;
                        using (CryptoStream cryptoStream = new CryptoStream(outputStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
                        using (FileStream inputStream = new FileStream(inputFile, FileMode.Open, FileAccess.Read))
                        {
                            inputStream.CopyTo(cryptoStream);
                        }
                    }
                }
            }
        }

        // 📌 **使用 PBKDF2 進行檔案解密**
        private void DecryptFile(string inputFile, string outputFile, string password)
        {
            using (FileStream inputStream = new FileStream(inputFile, FileMode.Open, FileAccess.Read))
            {
                byte[] salt = new byte[16];
                inputStream.Read(salt, 0, salt.Length); // 讀取 Salt

                using (Rfc2898DeriveBytes pdb = new Rfc2898DeriveBytes(password, salt, 100000))
                {
                    byte[] key = pdb.GetBytes(32);
                    byte[] iv = pdb.GetBytes(16);

                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = key;
                        aes.IV = iv;
                        using (CryptoStream cryptoStream = new CryptoStream(inputStream, aes.CreateDecryptor(), CryptoStreamMode.Read))
                        using (FileStream outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                        {
                            cryptoStream.CopyTo(outputStream);
                        }
                    }
                }
            }
        }
    }
}
