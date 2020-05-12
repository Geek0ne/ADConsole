using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ADConsole.Services;

namespace ADConsole.Dialogs
{
    /// <summary>域信息与密码策略对话框（纯代码构建界面）</summary>
    public class DomainInfoDialog : Window
    {
        private readonly AdService _svc;
        private readonly TabControl _tabs;

        public DomainInfoDialog(AdService svc)
        {
            _svc = svc;

            Title = "域信息与密码策略 - " + _svc.DomainDns;
            Width = 760;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            UiHelper.TrySetAppIcon(this);
            Grid root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _tabs = new TabControl();
            Grid.SetRow(_tabs, 0);
            root.Children.Add(_tabs);

            Button close = new Button { Content = "关闭", Width = 90, IsCancel = true,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            close.Click += delegate { Close(); };
            Grid.SetRow(close, 1);
            root.Children.Add(close);

            Content = root;

            Load();
        }

        private void Load()
        {
            try
            {
                _tabs.Items.Clear();

                // 概览
                AddTab("域概览", BuildOverview());
                AddTab("密码与锁定策略", BuildPolicy());
                AddTab("FSMO 角色", BuildFsmo());
                AddTab("域控 (DC)", BuildDcs());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读取域信息失败：\n\n" + AdError.Describe(ex), "域信息",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddTab(string header, List<InfoRow> rows)
        {
            DataGrid grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };

            DataGridTextColumn c1 = new DataGridTextColumn();
            c1.Header = "项目";
            c1.Binding = new System.Windows.Data.Binding("Key");
            c1.Width = 260;
            grid.Columns.Add(c1);

            DataGridTextColumn c2 = new DataGridTextColumn();
            c2.Header = "值";
            c2.Binding = new System.Windows.Data.Binding("Value");
            c2.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            grid.Columns.Add(c2);

            grid.ItemsSource = rows;

            TabItem item = new TabItem();
            item.Header = header;
            item.Content = grid;
            _tabs.Items.Add(item);
        }

        private List<InfoRow> BuildOverview()
        {
            List<InfoRow> rows = new List<InfoRow>();
            Dictionary<string, int> counts = _svc.GetOverview();

            rows.Add(new InfoRow { Key = "域名 (DNS)", Value = _svc.DomainDns });
            rows.Add(new InfoRow { Key = "域根 DN", Value = _svc.RootDn });
            rows.Add(new InfoRow { Key = "配置分区 DN", Value = _svc.ForestDn });
            rows.Add(new InfoRow { Key = "PDC / 首选域控", Value = _svc.PdcHost });
            rows.Add(new InfoRow { Key = "登录账户", Value = _svc.AuthUser });

            rows.Add(new InfoRow { Key = "用户数", Value = counts["用户"].ToString() });
            rows.Add(new InfoRow { Key = "组数", Value = counts["组"].ToString() });
            rows.Add(new InfoRow { Key = "计算机数", Value = counts["计算机"].ToString() });
            rows.Add(new InfoRow { Key = "OU 数", Value = counts["OU"].ToString() });
            return rows;
        }

        private List<InfoRow> BuildPolicy()
        {
            return _svc.GetDomainPolicy();
        }

        private List<InfoRow> BuildFsmo()
        {
            return _svc.GetFsmoRoles();
        }

        private List<InfoRow> BuildDcs()
        {
            List<InfoRow> rows = new List<InfoRow>();
            List<string> gcs = _svc.GetGlobalCatalogs();

            List<AdObject> dcs = _svc.GetDomainControllers();
            foreach (AdObject d in dcs)
            {
                bool isGc = false;
                foreach (string g in gcs)
                {
                    if (string.Equals(g, d.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(g, d.SamAccountName, StringComparison.OrdinalIgnoreCase))
                    {
                        isGc = true;
                        break;
                    }
                }

                rows.Add(new InfoRow
                {
                    Key = d.Name + (isGc ? "   [全局编目]" : ""),
                    Value = (d.DnsHostName ?? "—") + "     " + (d.OperatingSystem ?? "")
                });
            }

            if (rows.Count == 0)
            {
                rows.Add(new InfoRow { Key = "提示", Value = "未查询到域控对象（可能无权限读取）。" });
            }
            return rows;
        }
    }
}
