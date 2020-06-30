using System;
using System.Text.RegularExpressions;
using System.Windows;
using ADConsole.Services;

namespace ADConsole.Dialogs
{
    /// <summary>编辑现有用户的部门/职位/邮箱/电话/说明</summary>
    public partial class EditUserDialog : Window
    {
        public string ResultDepartment { get; private set; }
        public string ResultTitle { get; private set; }
        public string ResultEmail { get; private set; }
        public string ResultPhone { get; private set; }
        public string ResultDescription { get; private set; }

        public EditUserDialog(AdObject user)
        {
            InitializeComponent();

            Title = "编辑用户属性 - " + user.DisplayNameOrName;
            NameText.Text = user.DisplayNameOrName;
            SamText.Text = string.IsNullOrEmpty(user.SamAccountName) ? "(无)" : user.SamAccountName;

            DeptBox.Text = user.Department;
            TitleBox.Text = user.Title;
            EmailBox.Text = user.Email;
            PhoneBox.Text = user.Phone;
            DescBox.Text = user.Description;
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            string email = EmailBox.Text.Trim();
            if (!string.IsNullOrEmpty(email) && !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                ShowError("邮箱格式不正确。");
                return;
            }

            ResultDepartment = DeptBox.Text.Trim();
            ResultTitle = TitleBox.Text.Trim();
            ResultEmail = email;
            ResultPhone = PhoneBox.Text.Trim();
            ResultDescription = DescBox.Text.Trim();

            DialogResult = true;
            Close();
        }

        private void ShowError(string message)
        {
            Hint.Foreground = System.Windows.Media.Brushes.Red;
            Hint.Text = message;
        }
    }
}
