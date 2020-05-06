using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ADConsole.Services;
using Microsoft.Win32;

namespace ADConsole.Dialogs
{
    /// <summary>批量导入用户 CSV 对话框</summary>
    public partial class CsvImportDialog : Window
    {
        private readonly AdService _svc;
        private readonly string _defaultOuDn;

        private List<string> _header;
        private List<string[]> _data;
        private List<CsvUserRow> _rows = new List<CsvUserRow>();
        private List<ImportRow> _results = new List<ImportRow>();

        public CsvImportDialog(AdService svc, string defaultOuDn)
        {
            InitializeComponent();
            _svc = svc;
            _defaultOuDn = defaultOuDn;

            OuTreeNode root = _svc.BuildOuTree();
            AddOuItems(root, 0);
            for (int i = 0; i < OuCombo.Items.Count; i++)
            {
                NewUserDialog.OuItem it = OuCombo.Items[i] as NewUserDialog.OuItem;
                if (it != null && string.Equals(it.Dn, defaultOuDn, StringComparison.OrdinalIgnoreCase))
                {
                    OuCombo.SelectedIndex = i;
                    break;
                }
            }
            if (OuCombo.SelectedIndex < 0 && OuCombo.Items.Count > 0) OuCombo.SelectedIndex = 0;

            StatLine.Text = "尚未选择文件。";
        }

        private void AddOuItems(OuTreeNode node, int depth)
        {
            if (node.Kind == AdKind.OrganizationalUnit)
            {
                NewUserDialog.OuItem item = new NewUserDialog.OuItem();
                item.Dn = node.DistinguishedName;
                item.Indent = depth;
                item.Title = node.Title;
                item.FullTitle = node.DistinguishedName;
                OuCombo.Items.Add(item);
            }
            foreach (OuTreeNode c in node.Children)
                AddOuItems(c, node.Kind == AdKind.OrganizationalUnit ? depth + 1 : depth);
        }

        private string DefaultOuDn
        {
            get
            {
                NewUserDialog.OuItem it = OuCombo.SelectedItem as NewUserDialog.OuItem;
                return it == null ? _defaultOuDn : it.Dn;
            }
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";
            if (dlg.ShowDialog(this) != true) return;

            PathBox.Text = dlg.FileName;
            Parse_Click(sender, e);
        }

        private void Parse_Click(object sender, RoutedEventArgs e)
        {
            string path = PathBox.Text;
            if (string.IsNullOrEmpty(path))
            {
                StatLine.Text = "请先选择 CSV 文件。";
                return;
            }

            try
            {
                _header = CsvHelper.ParseWithEncodingAuto(path, out _data);

                if (_header.Count == 0)
                {
                    _rows.Clear();
                    PreviewGrid.ItemsSource = null;
                    StatLine.Text = "文件为空或没有可识别的表头。";
                    Hint.Text = "请确认 CSV 第一行是表头，例如：SamAccountName,Name,Password,OU,Department,Title,Email,Phone,Description";
                    ImportBtn.IsEnabled = false;
                    return;
                }

                string ouDn = DefaultOuDn;
                _rows = CsvHelper.MapRows(_header, _data, ouDn, delegate(string sam)
                {
                    return _svc.SamExists(sam);
                });

                PreviewGrid.ItemsSource = _rows;

                int valid = 0;
                foreach (CsvUserRow r in _rows) if (r.IsValid) valid++;
                int bad = _rows.Count - valid;

                StatLine.Text = string.Format("共 {0} 行数据，可导入 {1} 行，存在问题 {2} 行。", _rows.Count, valid, bad);
                Hint.Text = bad > 0 ? "存在问题的行不会被导入，请先修正 CSV 后重新解析。" : string.Empty;
                ImportBtn.IsEnabled = valid > 0;
            }
            catch (Exception ex)
            {
                _rows.Clear();
                PreviewGrid.ItemsSource = null;
                ImportBtn.IsEnabled = false;
                StatLine.Text = "解析失败。";
                Hint.Text = "解析 CSV 失败：" + ex.Message;
            }
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            List<CsvUserRow> valid = new List<CsvUserRow>();
            foreach (CsvUserRow r in _rows) if (r.IsValid) valid.Add(r);

            if (valid.Count == 0) return;

            MessageBoxResult confirm = MessageBox.Show(this,
                "即将在域中创建 " + valid.Count + " 个用户。\n\n" +
                "默认 OU：" + DefaultOuDn + "\n\n确定继续吗？",
                "确认导入", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes) return;

            ImportBtn.IsEnabled = false;
            _results.Clear();

            int done = 0;
            int okCount = 0;
            int failCount = 0;

            try
            {
                await System.Threading.Tasks.Task.Run(delegate
                {
                    foreach (CsvUserRow row in valid)
                    {
                        ImportRow r = new ImportRow();
                        r.LineNo = row.LineNo;
                        r.SamAccountName = row.SamAccountName;

                        try
                        {
                            _svc.CreateUser(row.OuDn, row.Name, row.SamAccountName, row.Password,
                                row.Department, row.Title, row.Email, row.Phone, row.Description);
                            r.Success = true;
                            r.Message = "创建成功";
                            okCount++;
                        }
                        catch (Exception ex)
                        {
                            r.Success = false;
                            r.Message = AdError.Describe(ex);
                            failCount++;
                        }

                        lock (_results) { _results.Add(r); }
                        done++;

                        int current = done;
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            Hint.Foreground = System.Windows.Media.Brushes.Gray;
                            Hint.Text = "正在导入 " + current + " / " + valid.Count + " …";
                        }));
                    }
                });

                ResultGrid.ItemsSource = _results;
                MainTabs.SelectedIndex = 1;

                StatLine.Text = string.Format("导入完成：成功 {0} 条，失败 {1} 条。", okCount, failCount);
                Hint.Foreground = failCount > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;
                Hint.Text = failCount > 0
                    ? "存在失败项，请在「执行结果」页查看原因，必要时导出失败明细。"
                    : "全部创建成功。";

                ExportFailBtn.IsEnabled = failCount > 0;
            }
            catch (Exception ex)
            {
                Hint.Foreground = System.Windows.Media.Brushes.Red;
                Hint.Text = "导入过程中发生异常：" + AdError.Describe(ex);
            }
            finally
            {
                ImportBtn.IsEnabled = true;
            }
        }

        private void ExportFail_Click(object sender, RoutedEventArgs e)
        {
            List<ImportRow> fails = new List<ImportRow>();
            foreach (ImportRow r in _results) if (!r.Success) fails.Add(r);

            if (fails.Count == 0)
            {
                MessageBox.Show(this, "没有失败项需要导出。", "导出失败明细",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "CSV 文件 (*.csv)|*.csv";
            dlg.FileName = "导入失败明细.csv";
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                List<string> header = new List<string>();
                header.AddRange(new string[] { "行号", "登录名", "失败原因" });

                List<List<string>> rows = new List<List<string>>();
                foreach (ImportRow r in fails)
                {
                    List<string> line = new List<string>();
                    line.Add(r.LineNo.ToString());
                    line.Add(r.SamAccountName);
                    line.Add(r.Message);
                    rows.Add(line);
                }

                CsvHelper.WriteCsv(dlg.FileName, header, rows);
                Hint.Foreground = System.Windows.Media.Brushes.Gray;
                Hint.Text = "已导出失败明细：" + dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败：" + ex.Message, "导出失败明细",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>结果行</summary>
        public class ImportRow
        {
            public int LineNo { get; set; }
            public string SamAccountName { get; set; }
            public bool Success { get; set; }
            public string Message { get; set; }
            public string ResultText { get { return Success ? "成功" : "失败"; } }
        }
    }
}
