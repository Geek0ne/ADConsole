using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ADConsole.Services;

namespace ADConsole.Dialogs
{
    /// <summary>
    /// 通用多选列表对话框（纯代码构建界面）。
    /// 用于选择组成员等场景。
    /// </summary>
    public class PickItemsDialog : Window
    {
        private readonly TextBox _filter;
        private readonly ListBox _list;
        private readonly List<AdObject> _source;
        private readonly List<AdObject> _picked = new List<AdObject>();
        private readonly TextBlock _count;

        /// <summary>用户选中的对象</summary>
        public List<AdObject> Picked { get { return _picked; } }

        public PickItemsDialog(List<AdObject> items, string title)
        {
            _source = items ?? new List<AdObject>();

            Title = title;
            Width = 560;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            UiHelper.TrySetAppIcon(this);

            Grid root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock label = new TextBlock { Text = "筛选：", Margin = new Thickness(0, 0, 0, 4) };
            Grid.SetRow(label, 0);
            root.Children.Add(label);

            _filter = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            _filter.TextChanged += delegate { ApplyFilter(); };
            Grid.SetRow(_filter, 1);
            root.Children.Add(_filter);

            _list = new ListBox
            {
                SelectionMode = SelectionMode.Extended,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetRow(_list, 2);
            root.Children.Add(_list);

            _count = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = System.Windows.Media.Brushes.Gray };
            Grid.SetRow(_count, 3);
            root.Children.Add(_count);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Button ok = new Button { Content = "确定", Width = 96, IsDefault = true };
            ok.Click += OnOk;
            Button cancel = new Button { Content = "取消", Width = 88, IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            Content = root;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string kw = _filter == null ? string.Empty : _filter.Text.Trim();
            _list.Items.Clear();

            foreach (AdObject o in _source)
            {
                if (!string.IsNullOrEmpty(kw))
                {
                    bool hit = false;
                    if (!string.IsNullOrEmpty(o.Name) &&
                        o.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) hit = true;
                    if (!string.IsNullOrEmpty(o.SamAccountName) &&
                        o.SamAccountName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) hit = true;
                    if (!string.IsNullOrEmpty(o.DisplayName) &&
                        o.DisplayName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) hit = true;
                    if (!hit) continue;
                }

                ListBoxItem item = new ListBoxItem();
                item.Content = Format(o);
                item.Tag = o;
                _list.Items.Add(item);
            }

            _count.Text = "共 " + _list.Items.Count + " 项，可按住 Ctrl 或 Shift 多选";
        }

        private static string Format(AdObject o)
        {
            string display = o.DisplayNameOrName;
            if (!string.IsNullOrEmpty(o.SamAccountName)) display += "  (" + o.SamAccountName + ")";
            if (!string.IsNullOrEmpty(o.Department)) display += "   —   " + o.Department;
            return display;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            _picked.Clear();
            foreach (object o in _list.SelectedItems)
            {
                ListBoxItem item = o as ListBoxItem;
                if (item != null && item.Tag is AdObject) _picked.Add((AdObject)item.Tag);
            }

            if (_picked.Count == 0)
            {
                MessageBox.Show(this, "请至少选择一项。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }
    }
}
