using System;
using System.Security.Cryptography;
using System.Text;

namespace FileLockerApp.Helpers
{
    public static class EncryptionHelper
    {
        /// <summary>
        /// �ϥ� PBKDF2 (SHA-256) �q�K�X�M�Q�Ȳ��� 256-bit �[�K���_
        /// </summary>
        public static byte[] DeriveKeyFromPassword(string password, byte[] salt)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256))
            {
                return pbkdf2.GetBytes(32); // 256-bit ���_
            }
        }
    }
}

