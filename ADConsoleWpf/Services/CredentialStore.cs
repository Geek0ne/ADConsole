using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ADConsole.Services
{
    /// <summary>保存的登录凭据</summary>
    public class StoredCredential
    {
        public string Domain { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
        public bool RememberPassword { get; set; }
        public bool AutoLogin { get; set; }
    }

    /// <summary>
    /// 凭据持久化：用 Windows DPAPI（CurrentUser 作用域）加密后存到
    /// %APPDATA%\ADConsole\credential.dat
    /// 只有当前 Windows 账户能解密，换账户/换机器都无法读取。
    /// </summary>
    public static class CredentialStore
    {
        // 附加熵，避免直接拷贝文件到别的程序也能解
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ADConsole.Credential.v1");

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ADConsole");
                return Path.Combine(dir, "credential.dat");
            }
        }

        /// <summary>读取；文件不存在或解密失败一律返回 null（不抛异常）</summary>
        public static StoredCredential Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;

                byte[] cipher = File.ReadAllBytes(FilePath);
                byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);

                // 简单的自定义序列化：字段之间用 \x01 分隔，最后一位是两个布尔值
                string text = Encoding.UTF8.GetString(plain);
                string[] parts = text.Split('\x01');
                if (parts.Length < 5) return null;

                StoredCredential c = new StoredCredential();
                c.Domain = parts[0];
                c.UserName = parts[1];
                c.Password = parts[2];
                c.RememberPassword = parts[3] == "1";
                c.AutoLogin = parts[4] == "1";
                return c;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>保存。若未勾选"记住密码"则只存登录名不存密码</summary>
        public static void Save(StoredCredential cred)
        {
            try
            {
                if (cred == null)
                {
                    Delete();
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));

                string pwd = cred.RememberPassword ? (cred.Password ?? string.Empty) : string.Empty;
                string text = string.Join("\x01", new string[]
                {
                    cred.Domain ?? string.Empty,
                    cred.UserName ?? string.Empty,
                    pwd,
                    cred.RememberPassword ? "1" : "0",
                    cred.AutoLogin ? "1" : "0"
                });

                byte[] plain = Encoding.UTF8.GetBytes(text);
                byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(FilePath, cipher);
            }
            catch
            {
                // 写盘失败不应影响登录流程
            }
        }

        /// <summary>删除保存的凭据</summary>
        public static void Delete()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch
            {
            }
        }
    }
}
