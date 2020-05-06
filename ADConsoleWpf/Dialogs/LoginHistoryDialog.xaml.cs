using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using ADConsole.Services;
using Microsoft.Win32;

namespace ADConsole.Dialogs
{
    /// <summary>登录历史查询对话框</summary>
    public partial class LoginHistoryDialog : Window
    {
        private readonly AdService _svc;
        private List<AdObject> _exact = new List<AdObject>();
        private List<LoginEventRow> _events = new List<LoginEventRow>();
        private bool _busy;

        public LoginHistoryDialog(AdService svc)
        {
            InitializeComponent();
            _svc = svc;
        }

        private void KeywordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Query_Click(null, null);
                e.Handled = true;
            }
        }

        private async void Query_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            string kw = KeywordBox.Text.Trim();
            bool recursive = RecursiveBox.IsChecked == true;

            _busy = true;
            QueryBtn.IsEnabled = false;
            EventBtn.IsEnabled = false;
            try
            {
                Hint.Foreground = System.Windows.Media.Brushes.Gray;
                Hint.Text = "正在搜索用户…";
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

                List<AdObject> users = await System.Threading.Tasks.Task.Run(
                    delegate { return _svc.SearchObjects(AdKind.User, _svc.RootDn, kw, recursive); });

                if (users.Count == 0)
                {
                    _exact.Clear();
                    ExactGrid.ItemsSource = null;
                    Summary.Text = "未找到匹配的用户。";
                    Hint.Text = string.Empty;
                    ExportBtn.IsEnabled = false;
                    return;
                }

                if (users.Count == 1)
                {
                    // 精确查询整个域，不受"递归"开关影响
                    users[0].Note = string.Empty;
                }

                const int exactLimit = 20;
                List<AdObject> toProbe = new List<AdObject>();
                for (int i = 0; i < users.Count && i < exactLimit; i++) toProbe.Add(users[i]);

                for (int i = exactLimit; i < users.Count; i++)
                {
                    users[i].Note = "仅显示近似值（超出精确查询上限 " + exactLimit + " 个）";
                }

                Hint.Text = "正在逐台域控汇总精确最后登录（共 " + toProbe.Count + " 个账号）…";

                List<AdObject> probed = toProbe;
                await System.Threading.Tasks.Task.Run(delegate
                {
                    _svc.FillExactLastLogon(probed, delegate(string n) { });
                });

                // 合并：精确查询过的排在前
                List<AdObject> merged = new List<AdObject>();
                foreach (AdObject o in toProbe) merged.Add(o);
                for (int i = exactLimit; i < users.Count; i++) merged.Add(users[i]);

                merged.Sort(delegate(AdObject a, AdObject b)
                {
                    if (a.LastLogonExact.HasValue && !b.LastLogonExact.HasValue) return -1;
                    if (!a.LastLogonExact.HasValue && b.LastLogonExact.HasValue) return 1;
                    if (a.LastLogonExact.HasValue && b.LastLogonExact.HasValue)
                        return b.LastLogonExact.Value.CompareTo(a.LastLogonExact.Value);
                    if (a.LastLogonApprox.HasValue && b.LastLogonApprox.HasValue)
                        return b.LastLogonApprox.Value.CompareTo(a.LastLogonApprox.Value);
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });

                _exact = merged;
                ExactGrid.ItemsSource = _exact;
                MainTabs.SelectedIndex = 0;

                Summary.Text = "匹配用户 " + merged.Count + " 个，其中 " + toProbe.Count +
                               " 个已做逐域控精确查询。";
                Hint.Text = users.Count > exactLimit
                    ? "匹配结果超过 " + exactLimit + " 个，超出部分仅显示近似值（lastLogonTimestamp）。可加上关键字缩小范围。"
                    : string.Empty;
                ExportBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Hint.Foreground = System.Windows.Media.Brushes.Red;
                Hint.Text = "查询失败：" + AdError.Describe(ex);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _busy = false;
                QueryBtn.IsEnabled = true;
                EventBtn.IsEnabled = true;
            }
        }

        private async void Event_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            int days = 7;
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "查询最近多少天的登录明细？\n（需要域管理员或 Event Log Readers 权限才能读取域控安全日志）",
                "登录明细 (4624)", "7", -1, -1);

            if (string.IsNullOrEmpty(input)) return;
            if (!int.TryParse(input.Trim(), out days) || days < 1 || days > 30)
            {
                Hint.Foreground = System.Windows.Media.Brushes.Red;
                Hint.Text = "天数必须是 1 到 30 之间的整数。";
                return;
            }

            _busy = true;
            QueryBtn.IsEnabled = false;
            EventBtn.IsEnabled = false;
            try
            {
                Hint.Foreground = System.Windows.Media.Brushes.Gray;
                Hint.Text = "正在读取域控安全日志（可能耗时较长）…";
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

                string kw = KeywordBox.Text.Trim();
                List<LoginEventRow> rows = await System.Threading.Tasks.Task.Run(
                    delegate { return _svc.GetLogonEvents(days, kw, delegate(string s) { }); });

                _events = rows;
                EventGrid.ItemsSource = _events;
                MainTabs.SelectedIndex = 1;
                ExportBtn.IsEnabled = _events.Count > 0;

                Summary.Text = "最近 " + days + " 天登录明细：" + _events.Count + " 条";
                Hint.Foreground = System.Windows.Media.Brushes.Gray;
                Hint.Text = _events.Count == 0 ? "没有查到登录记录。" : string.Empty;
            }
            catch (UnauthorizedAccessException ex)
            {
                // 权限不足：优雅降级，不弹阻塞式错误框
                _events.Clear();
                EventGrid.ItemsSource = null;
                MainTabs.SelectedIndex = 0;
                ExportBtn.IsEnabled = false;

                Hint.Foreground = System.Windows.Media.Brushes.Red;
                Hint.Text = ex.Message + " 已自动切换回「最近登录」页，请改用该页的精确最后登录时间。";
                Summary.Text = "登录明细不可用（权限不足）";
            }
            catch (Exception ex)
            {
                Hint.Foreground = System.Windows.Media.Brushes.Red;
                Hint.Text = "查询登录明细失败：" + AdError.Describe(ex);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _busy = false;
                QueryBtn.IsEnabled = true;
                EventBtn.IsEnabled = true;
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            bool exactTab = MainTabs.SelectedIndex == 0;

            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "CSV 文件 (*.csv)|*.csv";
            dlg.FileName = exactTab ? "登录时间汇总.csv" : "登录明细.csv";
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                List<string> header = new List<string>();
                List<List<string>> rows = new List<List<string>>();

                if (exactTab)
                {
                    header.AddRange(new string[] { "姓名", "登录名", "最后登录(精确)", "来源域控",
                        "最近登录(近似)", "说明" });
                    foreach (AdObject o in _exact)
                    {
                        List<string> line = new List<string>();
                        line.Add(o.DisplayNameOrName);
                        line.Add(o.SamAccountName);
                        line.Add(o.LastLogonExact.HasValue ? o.LastLogonExact.Value.ToString("yyyy-MM-dd HH:mm:ss") : "");
                        line.Add(o.LastLogonServer);
                        line.Add(o.LastLogonApprox.HasValue ? o.LastLogonApprox.Value.ToString("yyyy-MM-dd HH:mm:ss") : "");
                        line.Add(o.Note);
                        rows.Add(line);
                    }
                }
                else
                {
                    header.AddRange(new string[] { "时间", "账户", "来源IP", "登录类型", "域控" });
                    foreach (LoginEventRow r in _events)
                    {
                        List<string> line = new List<string>();
                        line.Add(r.TimeText);
                        line.Add(r.Account);
                        line.Add(r.IpAddress);
                        line.Add(r.LogonType);
                        line.Add(r.DomainController);
                        rows.Add(line);
                    }
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

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
