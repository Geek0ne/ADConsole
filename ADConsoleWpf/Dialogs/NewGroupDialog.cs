using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ADConsole.Services;

namespace ADConsole.Dialogs
{
    /// <summary>新建组对话框（纯代码构建界面）</summary>
    public class NewGroupDialog : Window
    {
        private readonly AdService _svc;
        private readonly TextBox _nameBox;
        private readonly TextBox _samBox;
        private readonly TextBox _descBox;
        private readonly ComboBox _ouCombo;
        private readonly TextBlock _hint;
        private readonly Button _okBtn;

        public string ResultName { get; private set; }
        public string ResultSam { get; private set; }
        public string ResultDescription { get; private set; }
        public string ResultOuDn { get; private set; }

        public NewGroupDialog(AdService svc, string defaultOuDn, string defaultOuTitle)
        {
            _svc = svc;

            Title = "新建组";
            Width = 520;
            Height = 330;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            Grid root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid form = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++)
                form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _nameBox = AddRow(form, 0, "组名 (CN) *");
            _samBox = AddRow(form, 1, "组登录名 (≤20字符) *");
            _ouCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), MaxDropDownHeight = 240 };
            Grid.SetRow(_ouCombo, 2);
            Grid.SetColumn(_ouCombo, 1);
            form.Children.Add(_ouCombo);
            Label formLabel = MakeLabel("所在 OU");
            Grid.SetRow(formLabel, 2); Grid.SetColumn(formLabel, 0);
            form.Children.Add(formLabel);

            _descBox = new TextBox { Margin = new Thickness(0, 0, 0, 8), Height = 46,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_descBox, 3);
            Grid.SetColumn(_descBox, 1);
            form.Children.Add(_descBox);
            Label descLabel = MakeLabel("描述");
            Grid.SetRow(descLabel, 3); Grid.SetColumn(descLabel, 0);
            descLabel.VerticalAlignment = VerticalAlignment.Top;
            form.Children.Add(descLabel);

            root.Children.Add(form);

            _hint = new TextBlock { Foreground = System.Windows.Media.Brushes.Red, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_hint, 1);
            root.Children.Add(_hint);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            _okBtn = new Button { Content = "创建", Width = 100, IsDefault = true };
            _okBtn.Click += OkBtn_Click;
            Button cancel = new Button { Content = "取消", Width = 88, IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
            buttons.Children.Add(_okBtn);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;

            // 填充 OU
            OuTreeNode ouRoot = _svc.BuildOuTree();
            AddOuItems(ouRoot, 0);
            for (int i = 0; i < _ouCombo.Items.Count; i++)
            {
                NewUserDialog.OuItem it = _ouCombo.Items[i] as NewUserDialog.OuItem;
                if (it != null && string.Equals(it.Dn, defaultOuDn, StringComparison.OrdinalIgnoreCase))
                {
                    _ouCombo.SelectedIndex = i;
                    break;
                }
            }
            if (_ouCombo.SelectedIndex < 0 && _ouCombo.Items.Count > 0) _ouCombo.SelectedIndex = 0;

            _nameBox.Focus();
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
                _ouCombo.Items.Add(item);
            }
            foreach (OuTreeNode c in node.Children)
                AddOuItems(c, node.Kind == AdKind.OrganizationalUnit ? depth + 1 : depth);
        }

        private static Label MakeLabel(string text)
        {
            Label l = new Label { Content = text, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
            return l;
        }

        private TextBox AddRow(Grid form, int row, string labelText)
        {
            Label l = MakeLabel(labelText);
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            form.Children.Add(l);

            TextBox t = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(t, row); Grid.SetColumn(t, 1);
            form.Children.Add(t);
            return t;
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            string name = _nameBox.Text.Trim();
            string sam = _samBox.Text.Trim();

            if (string.IsNullOrEmpty(name)) { Err("请填写组名。"); return; }

            string samErr = AdService.ValidateSam(sam);
            if (samErr != null) { Err(samErr + "。"); return; }

            NewUserDialog.OuItem ou = _ouCombo.SelectedItem as NewUserDialog.OuItem;
            if (ou == null) { Err("请选择所在 OU。"); return; }

            _okBtn.IsEnabled = false;
            try
            {
                if (_svc.SamExists(sam)) { Err("组登录名「" + sam + "」在域中已存在。"); return; }

                ResultName = name;
                ResultSam = sam;
                ResultDescription = _descBox.Text.Trim();
                ResultOuDn = ou.Dn;

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                Err("检查失败：" + AdError.Describe(ex));
            }
            finally
            {
                _okBtn.IsEnabled = true;
            }
        }

        private void Err(string msg)
        {
            _hint.Foreground = System.Windows.Media.Brushes.Red;
            _hint.Text = msg;
        }
    }
}
