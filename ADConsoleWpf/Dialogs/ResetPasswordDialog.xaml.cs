using System;
using System.Windows;
using ADConsole.Services;

namespace ADConsole.Dialogs
{
    /// <summary>重置密码对话框</summary>
    public partial class ResetPasswordDialog : Window
    {
        private readonly AdService _svc;
        private readonly AdObject _user;

        public string ResultPassword { get; private set; }
        public bool ResultForceChange { get; private set; }

        public ResetPasswordDialog(AdService svc, AdObject user)
        {
            InitializeComponent();
            _svc = svc;
            _user = user;

            UserLine.Text = user.DisplayNameOrName +
                            (string.IsNullOrEmpty(user.SamAccountName) ? "" : "   (" + user.SamAccountName + ")");
            DnLine.Text = user.DistinguishedName;
            Title = "重置密码 - " + user.DisplayNameOrName;

            PwdBox.Focus();
        }

        private void GenBtn_Click(object sender, RoutedEventArgs e)
        {
            PwdBox.Password = MainWindow.GeneratePassword(16);
            Hint.Foreground = System.Windows.Media.Brushes.Gray;
            Hint.Text = "已生成 16 位随机强密码。";
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            string pwd = PwdBox.Password;
            string err = AdService.ValidatePassword(pwd);
            if (err != null)
            {
                Hint.Foreground = System.Windows.Media.Brushes.Red;
                Hint.Text = err + "。";
                return;
            }

            if (!string.IsNullOrEmpty(_user.SamAccountName))
            {
                // 简单的自包含检查：密码不应包含登录名
                if (!string.IsNullOrEmpty(_user.SamAccountName) &&
                    pwd.IndexOf(_user.SamAccountName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Hint.Foreground = System.Windows.Media.Brushes.Red;
                    Hint.Text = "密码不能包含登录名。";
                    return;
                }
            }

            ResultPassword = pwd;
            ResultForceChange = ForceChangeBox.IsChecked == true;
            DialogResult = true;
            Close();
        }
    }
}
