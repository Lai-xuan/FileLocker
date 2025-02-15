using System;
using System.Security.Cryptography;
using System.Text;

namespace FileLockerApp.Helpers
{
    public static class EncryptionHelper
    {
        /// <summary>
        /// 使用 PBKDF2 (SHA-256) 從密碼和鹽值產生 256-bit 加密金鑰
        /// </summary>
        public static byte[] DeriveKeyFromPassword(string password, byte[] salt)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256))
            {
                return pbkdf2.GetBytes(32); // 256-bit 金鑰
            }
        }
    }
}

