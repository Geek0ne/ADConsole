using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using ADConsole.Services;
using Microsoft.Win32;

namespace ADConsole.Dialogs
{
    /// <summary>密码到期提醒对话框</summary>
    public partial class PasswordExpiryDialog : Window
    {
        private readonly AdService _svc;
        private readonly bool _canWrite;
        private List<PasswordExpiryRow> _rows = new List<PasswordExpiryRow>();

        public PasswordExpiryDialog(AdService svc, bool canWrite)
        {
            InitializeComponent();
            _svc = svc;
            _canWrite = canWrite;

            Query_Click(null, null);
        }

        private void Query_Click(object sender, RoutedEventArgs e)
        {
            int days;
            if (!int.TryParse(DaysBox.Text.Trim(), out days) || days < 1 || days > 365)
            {
                Hint.Foreground = System.Windows.Media.Brushes.Red;
                Hint.Text = "提醒天数必须是 1 到 365 之间的整数。";
                return;
            }

            bool includeExpired = IncludeExpiredBox.IsChecked == true;
            bool includeNever = IncludeNeverBox.IsChecked == true;

            QueryBtn.IsEnabled = false;
            try
            {
                Hint.Foreground = System.Windows.Media.Brushes.Gray;
                Hint.Text = "正在查询…";
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

                _rows = _svc.GetPasswordExpiry(days, includeExpired, includeNever);
                ListGrid.ItemsSource = _rows;

                int expired = 0, soon = 0, never = 0, nopwd = 0;
                foreach (PasswordExpiryRow r in _rows)
                {
                    if (r.Status == "已过期") expired++;
                    else if (r.Status == "永不过期") never++;
                    else if (r.Status == "从未设置密码") nopwd++;
                    else soon++;
                }

                Summary.Text = string.Format(
                    "共 {0} 个账号需要关注：已过期 {1}，即将到期 {2}，永不过期 {3}，从未设置密码 {4}",
                    _rows.Count, expired, soon, never, nopwd);

                Hint.Foreground = System.Windows.Media.Brushes.Gray;
                Hint.Text = _rows.Count == 0 ? "没有需要提醒的账号。" : string.Empty;
            }
            catch (Exception ex)
            {
                Hint.Foreground = System.Windows.Media.Brushes.Red;
                Hint.Text = "查询失败：" + AdError.Describe(ex);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                QueryBtn.IsEnabled = true;
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_rows == null || _rows.Count == 0)
            {
                MessageBox.Show(this, "没有可导出的数据。", "导出 CSV",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "CSV 文件 (*.csv)|*.csv";
            dlg.FileName = "密码到期提醒.csv";
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                List<string> header = new List<string>();
                header.AddRange(new string[] { "姓名", "登录名", "密码最后设置", "到期时间",
                    "剩余天数", "状态", "所在OU" });

                List<List<string>> rows = new List<List<string>>();
                foreach (PasswordExpiryRow r in _rows)
                {
                    List<string> line = new List<string>();
                    line.Add(r.Name);
                    line.Add(r.SamAccountName);
                    line.Add(r.PasswordLastSetText);
                    line.Add(r.ExpireDateText);
                    line.Add(r.DaysLeft.HasValue ? r.DaysLeft.Value.ToString() : "");
                    line.Add(r.Status);
                    line.Add(r.OuDisplay);
                    rows.Add(line);
                }

                CsvHelper.WriteCsv(dlg.FileName, header, rows);
                Hint.Foreground = System.Windows.Media.Brushes.Gray;
                Hint.Text = "已导出 " + rows.Count + " 条记录：" + dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败：" + ex.Message, "导出 CSV",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ListGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_canWrite)
            {
                MessageBox.Show(this, "当前账户只有只读权限，无法重置密码。", "权限不足",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PasswordExpiryRow row = ListGrid.SelectedItem as PasswordExpiryRow;
            if (row == null || string.IsNullOrEmpty(row.OuDn)) return;

            AdObject target = _svc.GetByDn(row.OuDn);
            if (target == null)
            {
                MessageBox.Show(this, "未能读取该对象，可能已被删除。", "重置密码",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResetPasswordDialog dlg = new ResetPasswordDialog(_svc, target);
            dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;

            try
            {
                _svc.SetPassword(target.DistinguishedName, dlg.ResultPassword);
                if (dlg.ResultForceChange) _svc.ForceChangeAtLogon(target.DistinguishedName);

                MessageBox.Show(this, "密码已重置。", "重置密码",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Query_Click(null, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "重置密码失败：\n\n" + AdError.Describe(ex), "重置密码",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
