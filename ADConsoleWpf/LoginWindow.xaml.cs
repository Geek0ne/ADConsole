using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ADConsole.Services;

namespace ADConsole
{
    /// <summary>标签等宽分布面板：每个子元素放一个字符，首字左贴边、末字右贴边、字距均分。
    /// 用于“域服务器 / 用户名 / 密码”这类中文标签的两端对齐（WPF 的 Justify 对单行 CJK 不生效）。</summary>
    public class JustifyPanel : System.Windows.Controls.Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double total = 0;
            double height = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                total += child.DesiredSize.Width;
                if (child.DesiredSize.Height > height) height = child.DesiredSize.Height;
            }
            double width = double.IsInfinity(availableSize.Width) ? total : availableSize.Width;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int n = InternalChildren.Count;
            if (n == 0) return finalSize;

            double total = 0;
            for (int i = 0; i < n; i++) total += InternalChildren[i].DesiredSize.Width;

            // 字距 = 剩余宽度平均分配到字符间隙；首字左贴边，末字右贴边
            double gap = n > 1 ? (finalSize.Width - total) / (n - 1) : 0;
            if (gap < 0) gap = 0;

            double x = 0;
            for (int i = 0; i < n; i++)
            {
                UIElement c = InternalChildren[i];
                double ch = c.DesiredSize.Height;
                c.Arrange(new Rect(x, (finalSize.Height - ch) / 2, c.DesiredSize.Width, ch));
                x += c.DesiredSize.Width + gap;
            }
            return finalSize;
        }
    }

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

        // ---- 域输入框空值占位提示：没有内容时浅色显示“域服务器地址或IP” ----

        private void DomainBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateDomainHint();
        }

        private void DomainBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdateDomainHint();
        }

        private void DomainBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdateDomainHint();
        }

        private void UpdateDomainHint()
        {
            if (DomainHint == null || DomainBox == null) return;
            DomainHint.Visibility = string.IsNullOrEmpty(DomainBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
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
