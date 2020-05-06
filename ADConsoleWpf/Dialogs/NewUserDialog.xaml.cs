using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using ADConsole.Services;

namespace ADConsole.Dialogs
{
    /// <summary>新建域用户对话框</summary>
    public partial class NewUserDialog : Window
    {
        private readonly AdService _svc;

        public string ResultName { get; private set; }
        public string ResultSam { get; private set; }
        public string ResultPassword { get; private set; }
        public string ResultOuDn { get; private set; }
        public string ResultDepartment { get; private set; }
        public string ResultTitle { get; private set; }
        public string ResultEmail { get; private set; }
        public string ResultPhone { get; private set; }
        public string ResultDescription { get; private set; }

        public NewUserDialog(AdService svc, string defaultOuDn, string defaultOuTitle)
        {
            InitializeComponent();
            _svc = svc;

            OuTreeNode root = _svc.BuildOuTree();
            AddOuItems(root, 0);

            // 定位到当前容器
            for (int i = 0; i < OuCombo.Items.Count; i++)
            {
                OuItem it = OuCombo.Items[i] as OuItem;
                if (it != null && string.Equals(it.Dn, defaultOuDn, StringComparison.OrdinalIgnoreCase))
                {
                    OuCombo.SelectedIndex = i;
                    break;
                }
            }
            if (OuCombo.SelectedIndex < 0 && OuCombo.Items.Count > 0) OuCombo.SelectedIndex = 0;

            SamBox.Focus();
        }

        private void AddOuItems(OuTreeNode node, int depth)
        {
            if (node.Kind == AdKind.OrganizationalUnit)
            {
                StringBuilderIndent(node, depth);
            }
            foreach (OuTreeNode c in node.Children)
            {
                AddOuItems(c, node.Kind == AdKind.OrganizationalUnit ? depth + 1 : depth);
            }
        }

        private void StringBuilderIndent(OuTreeNode node, int depth)
        {
            OuItem item = new OuItem();
            item.Dn = node.DistinguishedName;
            item.Indent = depth;
            item.Title = node.Title;
            item.FullTitle = node.DistinguishedName;
            OuCombo.Items.Add(item);
        }

        private void SamBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // 登录名自动填充 UPN 提示
        }

        private void GenBtn_Click(object sender, RoutedEventArgs e)
        {
            PwdBox.Password = MainWindow.GeneratePassword(16);
            Hint.Foreground = System.Windows.Media.Brushes.Gray;
            Hint.Text = "已生成 16 位随机强密码（已复制到可粘贴状态，请自行记录）。";
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            string sam = SamBox.Text.Trim();
            string pwd = PwdBox.Password;

            if (string.IsNullOrEmpty(name))
            {
                ShowError("请填写姓名（将作为对象的 CN）。");
                return;
            }
            if (name.IndexOf(',') >= 0 || name.IndexOf('"') >= 0 || name.IndexOf('+') >= 0 ||
                name.IndexOf('<') >= 0 || name.IndexOf('>') >= 0 || name.IndexOf(';') >= 0 ||
                name.IndexOf('=') >= 0 || name.IndexOf('\\') >= 0)
            {
                ShowError("姓名不能包含 , \" + < > ; = \\ 这些字符。");
                return;
            }

            string samErr = AdService.ValidateSam(sam);
            if (samErr != null)
            {
                ShowError(samErr + "。");
                return;
            }

            string pwdErr = AdService.ValidatePassword(pwd);
            if (pwdErr != null)
            {
                ShowError(pwdErr + "，请重新设置。");
                return;
            }

            string email = EmailBox.Text.Trim();
            if (!string.IsNullOrEmpty(email) && !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                ShowError("邮箱格式不正确。");
                return;
            }

            OuItem ou = OuCombo.SelectedItem as OuItem;
            if (ou == null)
            {
                ShowError("请选择所在 OU。");
                return;
            }

            // 重名预检查
            OkBtn.IsEnabled = false;
            try
            {
                Hint.Foreground = System.Windows.Media.Brushes.Gray;
                Hint.Text = "正在检查登录名是否可用…";

                if (_svc.SamExists(sam))
                {
                    ShowError("登录名「" + sam + "」在域中已存在。");
                    return;
                }

                ResultName = name;
                ResultSam = sam;
                ResultPassword = pwd;
                ResultOuDn = ou.Dn;
                ResultDepartment = DeptBox.Text.Trim();
                ResultTitle = TitleBox.Text.Trim();
                ResultEmail = email;
                ResultPhone = PhoneBox.Text.Trim();
                ResultDescription = DescBox.Text.Trim();

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("检查失败：" + AdError.Describe(ex));
            }
            finally
            {
                OkBtn.IsEnabled = true;
            }
        }

        private void ShowError(string msg)
        {
            Hint.Foreground = System.Windows.Media.Brushes.Red;
            Hint.Text = msg;
        }

        /// <summary>OU 下拉项</summary>
        public class OuItem
        {
            public string Dn { get; set; }
            public string Title { get; set; }
            public int Indent { get; set; }
            public string FullTitle { get; set; }

            public override string ToString()
            {
                return new string(' ', Indent * 3) + Title;
            }
        }
    }
}
