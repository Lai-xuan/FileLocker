using System;
using System.IO;
using System.Windows;
using System.Security.Cryptography;
using System.IO.Compression;
using Microsoft.Win32;
using System.Linq;
using System.Threading;

namespace FileLockerApp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            if (openFileDialog.ShowDialog() == true)
            {
                FilePathTextBox.Text = openFileDialog.FileName;
            }
        }

        private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    FilePathTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void LockFileButton_Click(object sender, RoutedEventArgs e)
        {
            string filePath = FilePathTextBox.Text;
            if (!string.IsNullOrEmpty(filePath))
            {
                if (filePath.EndsWith(".locked"))
                {
                    MessageBox.Show("檔案已經被鎖定！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                EncryptFileOrFolder(filePath);
            }
        }

        private void UnlockFileButton_Click(object sender, RoutedEventArgs e)
        {
            string filePath = FilePathTextBox.Text;
            if (!string.IsNullOrEmpty(filePath))
            {
                if (!filePath.EndsWith(".locked"))
                {
                    MessageBox.Show("檔案未鎖定，無需解鎖。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                DecryptFileOrFolder(filePath);
            }
        }

        // 產生金鑰（從密碼衍生）
        private byte[] DeriveKeyFromPassword(string password, byte[] salt)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256))
            {
                return pbkdf2.GetBytes(32); // 產生 32 bytes 的 AES 金鑰
            }
        }

        // 產生 Salt（避免相同密碼產生相同金鑰）
        private byte[] GenerateSalt()
        {
            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            return salt;
        }

        private void EncryptFileOrFolder(string path)
        {
            string password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show("請輸入密碼！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            byte[] salt = GenerateSalt(); // 生成 Salt
            byte[] key = DeriveKeyFromPassword(password, salt); // PBKDF2 產生金鑰
            byte[] iv = GenerateRandomIV(); // 仍然隨機產生 IV

            if (File.Exists(path))
            {
                string encryptedPath = path + ".locked";
                EncryptFile(path, encryptedPath, key, iv, salt);
                File.Delete(path);

                MessageBox.Show($"檔案已鎖定: {encryptedPath}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (Directory.Exists(path))
            {
                string zipPath = path + ".zip";
                string lockedPath = path + ".folder.locked";

                ZipFile.CreateFromDirectory(path, zipPath);
                Directory.Delete(path, true);

                EncryptFile(zipPath, lockedPath, key, iv, salt);
                File.Delete(zipPath);

                MessageBox.Show($"資料夾已鎖定: {lockedPath}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void DecryptFileOrFolder(string path)
        {
            MessageBox.Show($"解鎖的原始檔案: {path}", "Debug", MessageBoxButton.OK, MessageBoxImage.Information);

            string password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show("請輸入密碼！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 1️⃣ **先處理資料夾壓縮檔 (".folder.locked")**
            else if (File.Exists(path) && path.EndsWith(".folder.locked"))
            {
                MessageBox.Show($"📂 解鎖的原始資料夾檔案: {path}", "Debug", MessageBoxButton.OK, MessageBoxImage.Information);

                string zipPath = path.Replace(".folder.locked", ".zip");  // **變更為 ZIP 檔**
                string extractPath = path.Replace(".folder.locked", "");  // **解壓縮的資料夾名稱**

                MessageBox.Show($"📂 ZIP 檔案應該建立在: {zipPath}", "Debug", MessageBoxButton.OK, MessageBoxImage.Information);

                byte[] salt = new byte[16];
                byte[] iv = new byte[16];

                using (FileStream fsInput = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    fsInput.Read(salt, 0, salt.Length);
                    fsInput.Read(iv, 0, iv.Length);

                    byte[] key = DeriveKeyFromPassword(password, salt);

                    using (FileStream fsOutput = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = key;
                        aes.IV = iv;

                        using (CryptoStream cryptoStream = new CryptoStream(fsInput, aes.CreateDecryptor(), CryptoStreamMode.Read))
                        {
                            cryptoStream.CopyTo(fsOutput);
                        }
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();

                // **檢查 ZIP 是否成功建立**
                if (File.Exists(zipPath))
                {
                    try
                    {
                        string extractFolder = zipPath.Replace(".zip", ""); // **解壓縮目標資料夾**

                        // **解壓縮 ZIP**
                        ZipFile.ExtractToDirectory(zipPath, extractFolder);

                        // **檢查資料夾是否成功建立**
                        if (Directory.Exists(extractFolder))
                        {
                            MessageBox.Show($"✅ 資料夾成功解壓縮！: {extractFolder}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                            File.Delete(zipPath); // **刪除 ZIP 檔案（可選）**
                        }
                        else
                        {
                            MessageBox.Show($"❌ 解壓縮失敗，資料夾未建立！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"❌ 解壓縮時發生錯誤: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    MessageBox.Show($"✅ ZIP 檔案成功解密！: {zipPath}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    File.Delete(path); // 刪除 .locked 檔案
                }
                else
                {
                    MessageBox.Show($"❌ ZIP 檔案未建立！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            // 2️⃣ **再處理一般檔案 (".locked")**
            else if (File.Exists(path) && path.EndsWith(".locked"))
            {
                MessageBox.Show($"📄 解鎖的原始檔案: {path}", "Debug", MessageBoxButton.OK, MessageBoxImage.Information);

                string decryptedPath = path.Replace(".locked", "");  // **還原原始副檔名**

                byte[] salt = new byte[16];
                byte[] iv = new byte[16];

                using (FileStream fsInput = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    fsInput.Read(salt, 0, salt.Length);
                    fsInput.Read(iv, 0, iv.Length);

                    byte[] key = DeriveKeyFromPassword(password, salt);

                    using (FileStream fsOutput = new FileStream(decryptedPath, FileMode.Create, FileAccess.Write))
                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = key;
                        aes.IV = iv;

                        using (CryptoStream cryptoStream = new CryptoStream(fsInput, aes.CreateDecryptor(), CryptoStreamMode.Read))
                        {
                            cryptoStream.CopyTo(fsOutput);
                        }
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();

                // **檢查解密結果**
                if (File.Exists(decryptedPath))
                {
                    MessageBox.Show($"✅ 檔案解密成功！: {decryptedPath}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    File.Delete(path); // 刪除 .locked 檔案
                }
                else
                {
                    MessageBox.Show($"❌ 解密失敗！", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

        }

        private void RegisterFileAssociation_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("關聯副檔名功能尚未實作", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void EncryptFile(string inputFile, string outputFile, byte[] key, byte[] iv, byte[] salt)
        {
            using (FileStream fsOutput = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
            {
                // 先寫入 Salt 和 IV
                fsOutput.Write(salt, 0, salt.Length);
                fsOutput.Write(iv, 0, iv.Length);

                using (FileStream fsInput = new FileStream(inputFile, FileMode.Open, FileAccess.Read))
                using (Aes aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    using (CryptoStream cs = new CryptoStream(fsOutput, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        fsInput.CopyTo(cs);
                    }
                }
            }
        }

        private void DecryptFile(string inputFile, string outputFile, byte[] key, byte[] iv)
        {
            using (FileStream fsInput = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream fsOutput = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;

                using (CryptoStream csDecrypt = new CryptoStream(fsInput, aes.CreateDecryptor(), CryptoStreamMode.Read))
                {
                    csDecrypt.CopyTo(fsOutput);
                }
            }

            // 強制垃圾回收確保資源釋放
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // 確保檔案沒有被鎖定後才刪除
            for (int i = 0; i < 10; i++) // 最多嘗試 10 次
            {
                try
                {
                    if (File.Exists(inputFile))
                    {
                        File.Delete(inputFile);
                    }
                    break; // 刪除成功則跳出
                }
                catch (IOException)
                {
                    Thread.Sleep(100); // 等待 100ms 再試
                }
            }
        }

        private byte[] GenerateRandomKey()
        {
            using (Aes aes = Aes.Create())
            {
                aes.GenerateKey();
                return aes.Key;
            }
        }

        private byte[] GenerateRandomIV()
        {
            using (Aes aes = Aes.Create())
            {
                aes.GenerateIV();
                return aes.IV;
            }
        }
    }
}
