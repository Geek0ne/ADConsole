using System;
using System.Windows;
using ADConsole.Services;

namespace ADConsole
{
    /// <summary>登录框：支持记住密码与自动登录</summary>
    public partial class LoginWindow : Window
    {
        /// <summary>登录成功后返回的凭据</summary>
        public StoredCredential ResultCredential { get; private set; }

        private bool _busy;

        public LoginWindow(StoredCredential saved, string errorMessage)
        {
            InitializeComponent();

            string userDomain = Environment.UserDomainName;
            string userName = Environment.UserName;

            if (saved != null && !string.IsNullOrEmpty(saved.Domain))
            {
                DomainBox.Text = saved.Domain;
            }
            else
            {
                // 本机已入域时，用当前登录用户的域作为默认值
                DomainBox.Text = TryGuessDomain();
            }

            if (saved != null && !string.IsNullOrEmpty(saved.UserName))
            {
                UserBox.Text = StripDomainPrefix(saved.UserName);
                RememberBox.IsChecked = saved.RememberPassword;
                AutoLoginBox.IsChecked = saved.AutoLogin;
                AutoLoginBox.IsEnabled = saved.RememberPassword;

                if (saved.RememberPassword && !string.IsNullOrEmpty(saved.Password))
                {
                    PwdBox.Password = saved.Password;
                }
            }
            else if (!string.IsNullOrEmpty(userName))
            {
                UserBox.Text = userName;
            }

            if (!string.IsNullOrEmpty(errorMessage))
            {
                Hint.Text = errorMessage;
            }

            PwdBox.Focus();
        }

        private static string StripDomainPrefix(string user)
        {
            int i = user.IndexOf('\\');
            if (i >= 0) return user.Substring(i + 1);
            i = user.IndexOf('@');
            if (i >= 0) return user.Substring(0, i);
            return user;
        }

        /// <summary>从本机当前登录用户推断域 DNS 名</summary>
        private string TryGuessDomain()
        {
            try
            {
                System.DirectoryServices.ActiveDirectory.Domain d =
                    System.DirectoryServices.ActiveDirectory.Domain.GetCurrentDomain();
                return d.Name;
            }
            catch
            {
            }
            return Environment.UserDomainName;
        }

        private void RememberBox_Changed(object sender, RoutedEventArgs e)
        {
            if (AutoLoginBox == null) return;
            AutoLoginBox.IsEnabled = RememberBox.IsChecked == true;
            if (!AutoLoginBox.IsEnabled) AutoLoginBox.IsChecked = false;
        }

        private async void LoginBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            string domain = DomainBox.Text.Trim();
            string user = UserBox.Text.Trim();
            string pwd = PwdBox.Password;

            if (string.IsNullOrEmpty(domain))
            {
                Hint.Text = "请填写域名称。";
                return;
            }
            if (string.IsNullOrEmpty(user))
            {
                Hint.Text = "请填写账户名。";
                return;
            }
            if (string.IsNullOrEmpty(pwd))
            {
                Hint.Text = "请输入密码。";
                return;
            }

            _busy = true;
            LoginBtn.IsEnabled = false;
            CancelBtn.IsEnabled = false;
            Hint.Foreground = System.Windows.Media.Brushes.Gray;
            Hint.Text = "正在验证域账户…";

            bool ok = false;
            string error = null;
            try
            {
                ok = await System.Threading.Tasks.Task.Run(
                    delegate { return PermissionService.ValidateCredential(domain, user, pwd); });
            }
            catch (Exception ex)
            {
                error = ex.Message;
                ok = false;
            }

            if (ok)
            {
                StoredCredential c = new StoredCredential();
                c.Domain = domain;
                c.UserName = user;
                c.Password = pwd;
                c.RememberPassword = RememberBox.IsChecked == true;
                c.AutoLogin = AutoLoginBox.IsChecked == true;

                ResultCredential = c;
                CredentialStore.Save(c);

                DialogResult = true;
                Close();
                return;
            }

            _busy = false;
            LoginBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
            Hint.Foreground = System.Windows.Media.Brushes.Red;
            Hint.Text = string.IsNullOrEmpty(error)
                ? "验证失败：用户名或密码不正确。"
                : ("验证失败：" + error);
            PwdBox.SelectAll();
            PwdBox.Focus();
        }
    }
}
