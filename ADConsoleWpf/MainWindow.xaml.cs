using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ADConsole.Services;
using ADConsole.Dialogs;
using Microsoft.Win32;

namespace ADConsole
{
    /// <summary>供菜单 IsEnabled 绑定使用的简单视图模型</summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private bool _canWrite;
        public bool CanWrite
        {
            get { return _canWrite; }
            set
            {
                if (_canWrite == value) return;
                _canWrite = value;
                OnPropertyChanged("CanWrite");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>主窗口</summary>
    public partial class MainWindow : Window
    {
        private readonly AdService _svc = new AdService();
        private readonly MainViewModel _vm = new MainViewModel();

        private PermissionLevel _level = PermissionLevel.ReadOnly;
        private HashSet<string> _tokenSids;
        private OuTreeNode _treeRoot;
        private OuTreeNode _currentNode;
        private AdKind _mode = AdKind.User;
        private List<AdObject> _currentList = new List<AdObject>();
        private ContextMenu _rowMenu;

        public MainWindow(StoredCredential cred)
        {
            InitializeComponent();
            DataContext = _vm;

            try
            {
                _svc.Connect(cred);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "连接域失败：\n\n" + AdError.Describe(ex),
                    "AD 控制台", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }

            _tokenSids = PermissionService.GetTokenGroupSids(GetUserDn(cred));
            _level = PermissionService.GetLevel(_svc.RootDn, GetUserDn(cred));
            _vm.CanWrite = (_level != PermissionLevel.ReadOnly);

            Title = "AD 域控管理控制台 - " + _svc.DomainDns;
            ConnText.Text = "已连接 " + _svc.PdcHost + "  |  账户 " + cred.UserName +
                            "  |  " + _svc.RootDn;
            UpdatePermBadge();

            BuildContextMenu();
            BuildTree();

            if (_treeRoot != null && TreeOu.Items.Count > 0)
            {
                TreeViewItem top = (TreeViewItem)TreeOu.Items[0];
                top.IsSelected = true;
            }
        }

        private string GetUserDn(StoredCredential cred)
        {
            string sam = cred.UserName;
            int i = sam.IndexOf('\\');
            if (i >= 0) sam = sam.Substring(i + 1);
            i = sam.IndexOf('@');
            if (i >= 0) sam = sam.Substring(0, i);
            return "CN=" + AdService.EscapeRdn(sam) + ",CN=Users," + _svc.RootDn;
        }

        private void UpdatePermBadge()
        {
            PermText.Text = PermissionService.ShortName(_level);
            if (_level == PermissionLevel.DomainAdmin)
            {
                PermBadge.Background = new SolidColorBrush(Color.FromRgb(0xD4, 0xED, 0xDA));
                PermText.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x57, 0x24));
            }
            else if (_level == PermissionLevel.Delegated)
            {
                PermBadge.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xCD));
                PermText.Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x64, 0x04));
            }
            else
            {
                PermBadge.Background = new SolidColorBrush(Color.FromRgb(0xE2, 0xE3, 0xE5));
                PermText.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0x3D, 0x41));
            }
            PermBadge.ToolTip = PermissionService.Describe(_level);
        }

        // ---------------- 树 ----------------

        private void BuildTree()
        {
            try
            {
                SetStatus("正在加载组织单位树…");
                _treeRoot = _svc.BuildOuTree();
                TreeOu.Items.Clear();

                TreeViewItem top = MakeNode(_treeRoot);
                TreeOu.Items.Add(top);
                SetStatus("就绪");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "加载 OU 树失败：\n\n" + AdError.Describe(ex),
                    "AD 控制台", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("OU 树加载失败");
            }
        }

        private TreeViewItem MakeNode(OuTreeNode node)
        {
            TreeViewItem item = new TreeViewItem();

            // 图标 + 文字（对齐 dsa.msc 的树样式）
            StackPanel header = new StackPanel();
            header.Orientation = Orientation.Horizontal;

            Image img = new Image();
            img.Source = GetKindIcon(node.Kind);
            img.Width = 16;
            img.Height = 16;
            img.Margin = new Thickness(0, 0, 5, 0);
            img.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(img);

            TextBlock tb = new TextBlock();
            tb.Text = node.Title;
            tb.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(tb);

            item.Header = header;
            item.Tag = node;
            item.ToolTip = node.Tip;
            item.IsExpanded = (node.Kind == AdKind.Domain);

            foreach (OuTreeNode c in node.Children)
            {
                item.Items.Add(MakeNode(c));
            }
            return item;
        }

        /// <summary>按对象类别取树节点图标；资源缺失时返回 null（树仍可用，只是无图标）</summary>
        private static ImageSource GetKindIcon(AdKind kind)
        {
            if (_kindIcons == null)
            {
                _kindIcons = new Dictionary<AdKind, ImageSource>();
                _kindIcons[AdKind.Domain] = LoadIcon("domain");
                _kindIcons[AdKind.OrganizationalUnit] = LoadIcon("ou");
                _kindIcons[AdKind.Container] = LoadIcon("container");
            }
            ImageSource src;
            if (_kindIcons.TryGetValue(kind, out src)) return src;
            // 用户/组/计算机不在树上出现；兜底用容器图标
            if (_kindIcons.TryGetValue(AdKind.Container, out src)) return src;
            return null;
        }

        private static Dictionary<AdKind, ImageSource> _kindIcons;

        /// <summary>从嵌入资源加载 ico；失败返回 null，不影响功能</summary>
        private static ImageSource LoadIcon(string name)
        {
            try
            {
                Uri uri = new Uri("pack://application:,,,/icons/" + name + ".ico", UriKind.Absolute);
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void TreeOu_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            TreeViewItem item = TreeOu.SelectedItem as TreeViewItem;
            if (item == null) return;
            OuTreeNode node = item.Tag as OuTreeNode;
            if (node == null) return;

            _currentNode = node;
            LoadObjects();
        }

        // ---------------- 列表 ----------------

        private void LoadObjects()
        {
            if (_currentNode == null) return;

            try
            {
                SetStatus("正在查询 " + _currentNode.Title + " …");
                Mouse.OverrideCursor = Cursors.Wait;

                string kw = SearchBox.Text == null ? string.Empty : SearchBox.Text.Trim();
                bool recursive = RecursiveBox.IsChecked == true;

                _currentList = _svc.SearchObjects(_mode, _currentNode.DistinguishedName, kw, recursive);
                ObjectGrid.ItemsSource = _currentList;

                DetailGrid.ItemsSource = null;
                MemberGrid.ItemsSource = null;
                MemberOfGrid.ItemsSource = null;

                SetStatus("共 " + _currentList.Count + " 个对象");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "查询失败：\n\n" + AdError.Describe(ex),
                    "AD 控制台", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("查询失败");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void SetMode(AdKind kind)
        {
            _mode = kind;
            LoadObjects();
        }

        // ---------------- 选中与详情 ----------------

        private AdObject Selected
        {
            get { return ObjectGrid.SelectedItem as AdObject; }
        }

        private void ObjectGrid_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            AdObject o = Selected;
            if (o == null) return;

            if (o.Kind == AdKind.OrganizationalUnit || o.Kind == AdKind.Container)
            {
                // 双击进入该容器：在树里查找并选中
                SelectTreeNode(o.DistinguishedName);
                return;
            }

            ShowDetails(o);
        }

        private void SelectTreeNode(string dn)
        {
            TreeViewItem found = FindNode(TreeOu.Items, dn);
            if (found != null)
            {
                found.IsSelected = true;
                found.BringIntoView();
            }
        }

        private TreeViewItem FindNode(ItemCollection items, string dn)
        {
            foreach (object o in items)
            {
                TreeViewItem tvi = o as TreeViewItem;
                if (tvi == null) continue;
                OuTreeNode n = tvi.Tag as OuTreeNode;
                if (n != null && string.Equals(n.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase))
                    return tvi;

                TreeViewItem child = FindNode(tvi.Items, dn);
                if (child != null) return child;
            }
            return null;
        }

        private void ShowDetails(AdObject o)
        {
            List<KeyValueRow> rows = new List<KeyValueRow>();
            rows.Add(new KeyValueRow { Key = "名称", Value = o.Name });
            rows.Add(new KeyValueRow { Key = "显示名", Value = o.DisplayName });
            rows.Add(new KeyValueRow { Key = "登录名 (sAMAccountName)", Value = o.SamAccountName });
            rows.Add(new KeyValueRow { Key = "UPN", Value = o.UserPrincipalName });
            rows.Add(new KeyValueRow { Key = "对象类别", Value = o.Kind.ToString() });

            if (o.Kind == AdKind.User || o.Kind == AdKind.Computer)
            {
                rows.Add(new KeyValueRow { Key = "启用状态", Value = o.Enabled ? "已启用" : "已禁用" });
            }
            if (o.Kind == AdKind.User)
            {
                rows.Add(new KeyValueRow { Key = "锁定状态", Value = o.LockedOut ? "已锁定" : "未锁定" });
                rows.Add(new KeyValueRow { Key = "邮箱", Value = o.Email });
                rows.Add(new KeyValueRow { Key = "部门", Value = o.Department });
                rows.Add(new KeyValueRow { Key = "职位", Value = o.Title });
                rows.Add(new KeyValueRow { Key = "电话", Value = o.Phone });
                rows.Add(new KeyValueRow { Key = "办公室", Value = o.Office });
                rows.Add(new KeyValueRow { Key = "密码最后设置", Value = FormatDate(o.PasswordLastSet) });
                rows.Add(new KeyValueRow { Key = "密码永不过期", Value = o.PasswordNeverExpires ? "是" : "否" });
                rows.Add(new KeyValueRow { Key = "最近登录(近似)", Value = FormatDate(o.LastLogonApprox) });
            }
            if (o.Kind == AdKind.Computer)
            {
                rows.Add(new KeyValueRow { Key = "DNS 主机名", Value = o.DnsHostName });
                rows.Add(new KeyValueRow { Key = "操作系统", Value = o.OperatingSystem });
            }
            if (o.Kind == AdKind.Group)
            {
                rows.Add(new KeyValueRow { Key = "组类别", Value = o.GroupCategory });
                rows.Add(new KeyValueRow { Key = "组作用域", Value = o.GroupScope });
            }

            rows.Add(new KeyValueRow { Key = "创建时间", Value = FormatDate(o.WhenCreated) });
            rows.Add(new KeyValueRow { Key = "修改时间", Value = FormatDate(o.WhenChanged) });
            rows.Add(new KeyValueRow { Key = "描述", Value = o.Description });
            rows.Add(new KeyValueRow { Key = "Distinguished Name", Value = o.DistinguishedName });

            DetailGrid.ItemsSource = rows;
            DetailTabs.SelectedIndex = 0;

            LoadRelated(o);
        }

        private void LoadRelated(AdObject o)
        {
            try
            {
                if (o.Kind == AdKind.Group)
                {
                    MemberGrid.ItemsSource = _svc.GetMembers(o.DistinguishedName);
                    MemberOfGrid.ItemsSource = null;
                }
                else if (o.Kind == AdKind.User || o.Kind == AdKind.Computer)
                {
                    MemberOfGrid.ItemsSource = _svc.GetMemberOf(o.DistinguishedName);
                    MemberGrid.ItemsSource = null;
                }
                else
                {
                    MemberGrid.ItemsSource = null;
                    MemberOfGrid.ItemsSource = null;
                }
            }
            catch
            {
                MemberGrid.ItemsSource = null;
                MemberOfGrid.ItemsSource = null;
            }
        }

        private static string FormatDate(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm:ss") : "—";
        }

        // ---------------- 写操作门禁 ----------------

        /// <summary>统一的写操作执行器：权限检查 + 错误提示</summary>
        private bool RunWrite(string title, Action action)
        {
            if (_level == PermissionLevel.ReadOnly)
            {
                MessageBox.Show(this,
                    "当前账户只有只读权限，无法执行「" + title + "」。\n\n" +
                    "请使用具备域管理员权限或已被委派写权限的域账户重新登录（工具 → 重新连接）。",
                    "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                SetStatus("正在执行：" + title + " …");
                Mouse.OverrideCursor = Cursors.Wait;
                action();
                SetStatus(title + " 完成");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, title + " 失败：\n\n" + AdError.Describe(ex),
                    "AD 控制台", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus(title + " 失败");
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private bool Confirm(string message, string title)
        {
            MessageBoxResult r = MessageBox.Show(this, message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            return r == MessageBoxResult.Yes;
        }

        // ---------------- 菜单：文件 ----------------

        private void Menu_ExportCsv(object sender, RoutedEventArgs e)
        {
            if (_currentList == null || _currentList.Count == 0)
            {
                MessageBox.Show(this, "当前列表为空，没有可导出的数据。", "导出 CSV",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "CSV 文件 (*.csv)|*.csv";
            dlg.FileName = _currentNode.Title + "-" + _mode + ".csv";
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                List<string> header = new List<string>();
                header.AddRange(new string[] { "名称", "登录名", "状态", "部门", "职位",
                    "邮箱", "电话", "最后设置密码", "最近登录(近似)", "描述", "DN" });

                List<List<string>> rows = new List<List<string>>();
                foreach (AdObject o in _currentList)
                {
                    List<string> r = new List<string>();
                    r.Add(o.DisplayNameOrName);
                    r.Add(o.SamAccountName);
                    r.Add(o.StatusText);
                    r.Add(o.Department);
                    r.Add(o.Title);
                    r.Add(o.Email);
                    r.Add(o.Phone);
                    r.Add(FormatDate(o.PasswordLastSet));
                    r.Add(FormatDate(o.LastLogonApprox));
                    r.Add(o.Description);
                    r.Add(o.DistinguishedName);
                    rows.Add(r);
                }

                CsvHelper.WriteCsv(dlg.FileName, header, rows);
                SetStatus("已导出 " + rows.Count + " 条记录");
                MessageBox.Show(this, "导出完成，共 " + rows.Count + " 条记录。\n\n" + dlg.FileName,
                    "导出 CSV", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败：" + ex.Message, "导出 CSV",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Menu_Exit(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Menu_Refresh(object sender, RoutedEventArgs e)
        {
            LoadObjects();
        }

        private void Menu_Reconnect(object sender, RoutedEventArgs e)
        {
            LoginWindow login = new LoginWindow(new StoredCredential
            {
                Domain = _svc.DomainDns,
                UserName = _svc.AuthUser,
                RememberPassword = true,
                AutoLogin = true
            }, null);

            if (login.ShowDialog() != true) return;

            try
            {
                _svc.Connect(login.ResultCredential);
                _tokenSids = PermissionService.GetTokenGroupSids(GetUserDn(login.ResultCredential));
                _level = PermissionService.GetLevel(_svc.RootDn, GetUserDn(login.ResultCredential));
                _vm.CanWrite = (_level != PermissionLevel.ReadOnly);
                UpdatePermBadge();

                ConnText.Text = "已连接 " + _svc.PdcHost + "  |  账户 " +
                                login.ResultCredential.UserName + "  |  " + _svc.RootDn;
                BuildTree();
                if (TreeOu.Items.Count > 0) ((TreeViewItem)TreeOu.Items[0]).IsSelected = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "重新连接失败：\n\n" + AdError.Describe(ex), "AD 控制台",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Menu_About(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(this,
                "AD 域控管理控制台\n\n" +
                "技术：C# + WPF (.NET Framework 4.8) + System.DirectoryServices\n" +
                "连接账户：" + _svc.AuthUser + "\n" +
                "权限等级：" + PermissionService.Describe(_level) + "\n\n" +
                "所有写操作均通过当前登录的域账户执行，权限由 AD 本身判定。",
                "关于", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Menu_Search(object sender, RoutedEventArgs e)
        {
            LoadObjects();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { LoadObjects(); e.Handled = true; }
        }

        // ---------------- 菜单：用户 ----------------

        private void Menu_NewUser(object sender, RoutedEventArgs e)
        {
            if (_currentNode == null) return;

            NewUserDialog dlg = new NewUserDialog(_svc, _currentNode.DistinguishedName, _currentNode.Title);
            dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;

            RunWrite("新建用户", delegate
            {
                _svc.CreateUser(dlg.ResultOuDn, dlg.ResultName, dlg.ResultSam, dlg.ResultPassword,
                    dlg.ResultDepartment, dlg.ResultTitle, dlg.ResultEmail, dlg.ResultPhone,
                    dlg.ResultDescription);
            });
            LoadObjects();
        }

        private void Menu_ResetPassword(object sender, RoutedEventArgs e)
        {
            AdObject o = Selected;
            if (o == null) { MessageBox.Show(this, "请先选择一个用户。", "重置密码",
                MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (o.Kind != AdKind.User) { MessageBox.Show(this, "请选择用户对象。", "重置密码",
                MessageBoxButton.OK, MessageBoxImage.Information); return; }

            ResetPasswordDialog dlg = new ResetPasswordDialog(_svc, o);
            dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;

            RunWrite("重置密码", delegate
            {
                _svc.SetPassword(o.DistinguishedName, dlg.ResultPassword);
                if (dlg.ResultForceChange) _svc.ForceChangeAtLogon(o.DistinguishedName);
            });
            LoadObjects();
        }

        private void Menu_Enable(object sender, RoutedEventArgs e)
        {
            AdObject o = Selected;
            if (o == null || o.Kind != AdKind.User) { ShowSelectUser(); return; }

            if (!Confirm("确定要启用账户「" + o.DisplayNameOrName + "」吗？", "启用账号")) return;
            RunWrite("启用账号", delegate { _svc.SetEnabled(o.DistinguishedName, true); });
            LoadObjects();
        }

        private void Menu_Disable(object sender, RoutedEventArgs e)
        {
            AdObject o = Selected;
            if (o == null || o.Kind != AdKind.User) { ShowSelectUser(); return; }

            if (!Confirm("确定要禁用账户「" + o.DisplayNameOrName + "」吗？\n禁用后该用户将无法登录域。", "禁用账号")) return;
            RunWrite("禁用账号", delegate { _svc.SetEnabled(o.DistinguishedName, false); });
            LoadObjects();
        }

        private void Menu_Unlock(object sender, RoutedEventArgs e)
        {
            AdObject o = Selected;
            if (o == null || o.Kind != AdKind.User) { ShowSelectUser(); return; }

            RunWrite("解锁账号", delegate { _svc.UnlockAccount(o.DistinguishedName); });
            LoadObjects();
        }

        private void Menu_DeleteUser(object sender, RoutedEventArgs e)
        {
            AdObject o = Selected;
            if (o == null || o.Kind != AdKind.User) { ShowSelectUser(); return; }

            if (!Confirm("此操作不可恢复！\n\n确定要删除用户「" + o.DisplayNameOrName +
                         "」（" + o.SamAccountName + "）吗？", "删除用户")) return;

            RunWrite("删除用户", delegate { _svc.DeleteObject(o.DistinguishedName); });
            LoadObjects();
        }

        private void ShowSelectUser()
        {
            MessageBox.Show(this, "请先在列表中选择一个用户。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------------- 菜单：组 ----------------

        private void Menu_NewGroup(object sender, RoutedEventArgs e)
        {
            if (_currentNode == null) return;
            NewGroupDialog dlg = new NewGroupDialog(_svc, _currentNode.DistinguishedName, _currentNode.Title);
            dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;

            RunWrite("新建组", delegate
            {
                _svc.CreateGroup(dlg.ResultOuDn, dlg.ResultName, dlg.ResultSam, dlg.ResultDescription);
            });
            LoadObjects();
        }

        private void Menu_AddMember(object sender, RoutedEventArgs e)
        {
            AdObject group = Selected;
            if (group == null || group.Kind != AdKind.Group)
            {
                MessageBox.Show(this, "请先选择一个组。", "添加成员",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<AdObject> picked = PickUsers("选择要加入组「" + group.Name + "」的用户");
            if (picked == null || picked.Count == 0) return;

            int n = 0;
            RunWrite("添加组成员", delegate
            {
                foreach (AdObject u in picked)
                {
                    _svc.AddMember(group.DistinguishedName, u.DistinguishedName);
                    n++;
                }
            });
            MessageBox.Show(this, "已添加 " + n + " 个成员。", "添加成员",
                MessageBoxButton.OK, MessageBoxImage.Information);
            LoadObjects();
        }

        private void Menu_RemoveMember(object sender, RoutedEventArgs e)
        {
            AdObject group = Selected;
            if (group == null || group.Kind != AdKind.Group)
            {
                MessageBox.Show(this, "请先选择一个组。", "移除成员",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<AdObject> members;
            try { members = _svc.GetMembers(group.DistinguishedName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读取成员失败：" + AdError.Describe(ex), "移除成员",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (members.Count == 0)
            {
                MessageBox.Show(this, "该组没有直接成员。", "移除成员",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PickItemsDialog dlg = new PickItemsDialog(members, "选择要从组「" + group.Name + "」移除的成员");
            dlg.Owner = this;
            if (dlg.ShowDialog() != true || dlg.Picked.Count == 0) return;

            if (!Confirm("确定要从组中移除选中的 " + dlg.Picked.Count + " 个成员吗？", "移除成员")) return;

            int n = 0;
            RunWrite("移除组成员", delegate
            {
                foreach (AdObject m in dlg.Picked)
                {
                    _svc.RemoveMember(group.DistinguishedName, m.DistinguishedName);
                    n++;
                }
            });
            MessageBox.Show(this, "已移除 " + n + " 个成员。", "移除成员",
                MessageBoxButton.OK, MessageBoxImage.Information);
            LoadObjects();
        }

        private void Menu_ViewMembers(object sender, RoutedEventArgs e)
        {
            AdObject group = Selected;
            if (group == null || group.Kind != AdKind.Group)
            {
                MessageBox.Show(this, "请先选择一个组。", "查看成员",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                int total = _svc.CountMembersRecursive(group.DistinguishedName);
                List<AdObject> direct = _svc.GetMembers(group.DistinguishedName);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("组：" + group.Name);
                sb.AppendLine("直接成员：" + direct.Count + " 个");
                sb.AppendLine("递归成员：" + total + " 个");
                sb.AppendLine();

                int shown = 0;
                foreach (AdObject m in direct)
                {
                    if (shown++ >= 40) { sb.AppendLine("…（其余省略）"); break; }
                    sb.AppendLine("  " + m.Name + "   [" + m.Kind + "]");
                }

                MessageBox.Show(this, sb.ToString(), "查看成员", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读取成员失败：" + AdError.Describe(ex), "查看成员",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>打开用户选择器，返回选中的用户（不选则返回空列表）</summary>
        private List<AdObject> PickUsers(string title)
        {
            List<AdObject> users;
            try
            {
                SetStatus("正在加载用户列表…");
                users = _svc.SearchObjects(AdKind.User, _svc.RootDn, null, true);
                SetStatus("就绪");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "加载用户失败：" + AdError.Describe(ex), "选择用户",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }

            PickItemsDialog dlg = new PickItemsDialog(users, title);
            dlg.Owner = this;
            if (dlg.ShowDialog() != true) return null;
            return dlg.Picked;
        }

        // ---------------- 菜单：计算机 ----------------

        private void Menu_CompEnable(object sender, RoutedEventArgs e)
        {
            AdObject o = Selected;
            if (o == null || o.Kind != AdKind.Computer) { ShowSelectComputer(); return; }

            if (!Confirm("确定要启用计算机「" + o.Name + "」吗？", "启用计算机")) return;
            RunWrite("启用计算机", delegate { _svc.SetEnabled(o.DistinguishedName, true); });
            LoadObjects();
        }

        private void Menu_CompDisable(object sender, RoutedEventArgs e)
        {
            AdObject o = Selected;
            if (o == null || o.Kind != AdKind.Computer) { ShowSelectComputer(); return; }

            if (!Confirm("确定要禁用计算机「" + o.Name + "」吗？\n禁用后该计算机将无法加入域认证。", "禁用计算机")) return;
            RunWrite("禁用计算机", delegate { _svc.SetEnabled(o.DistinguishedName, false); });
            LoadObjects();
        }

        private void Menu_CompDelete(object sender, RoutedEventArgs e)
        {
            AdObject o = Selected;
            if (o == null || o.Kind != AdKind.Computer) { ShowSelectComputer(); return; }

            if (!Confirm("此操作不可恢复！\n\n确定要删除计算机「" + o.Name + "」吗？\n" +
                         "若该计算机仍在使用，请先在客户端执行 unjoin。", "删除计算机")) return;

            RunWrite("删除计算机", delegate { _svc.DeleteObject(o.DistinguishedName); });
            LoadObjects();
        }

        private void ShowSelectComputer()
        {
            MessageBox.Show(this, "请先在列表中选择一台计算机。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------------- 菜单：工具 ----------------

        private void Menu_DomainInfo(object sender, RoutedEventArgs e)
        {
            DomainInfoDialog dlg = new DomainInfoDialog(_svc);
            dlg.Owner = this;
            dlg.ShowDialog();
        }

        private void Menu_CsvImport(object sender, RoutedEventArgs e)
        {
            CsvImportDialog dlg = new CsvImportDialog(_svc,
                _currentNode == null ? _svc.RootDn : _currentNode.DistinguishedName);
            dlg.Owner = this;
            dlg.ShowDialog();
            LoadObjects();
        }

        private void Menu_PasswordExpiry(object sender, RoutedEventArgs e)
        {
            PasswordExpiryDialog dlg = new PasswordExpiryDialog(_svc, _level != PermissionLevel.ReadOnly);
            dlg.Owner = this;
            dlg.ShowDialog();
        }

        private void Menu_LoginHistory(object sender, RoutedEventArgs e)
        {
            LoginHistoryDialog dlg = new LoginHistoryDialog(_svc);
            dlg.Owner = this;
            dlg.ShowDialog();
        }

        // ---------------- 右键菜单 ----------------

        private void BuildContextMenu()
        {
            _rowMenu = new ContextMenu();

            _rowMenu.Items.Add(MakeMenuItem("查看属性", ShowDetailsMenu));
            _rowMenu.Items.Add(new Separator());

            MenuItem mPwd = MakeMenuItem("重置密码…", Menu_ResetPassword);
            mPwd.SetBinding(MenuItem.IsEnabledProperty, new System.Windows.Data.Binding("CanWrite"));
            _rowMenu.Items.Add(mPwd);

            MenuItem mEnable = MakeMenuItem("启用", Menu_Enable);
            mEnable.SetBinding(MenuItem.IsEnabledProperty, new System.Windows.Data.Binding("CanWrite"));
            _rowMenu.Items.Add(mEnable);

            MenuItem mDisable = MakeMenuItem("禁用", Menu_Disable);
            mDisable.SetBinding(MenuItem.IsEnabledProperty, new System.Windows.Data.Binding("CanWrite"));
            _rowMenu.Items.Add(mDisable);

            MenuItem mUnlock = MakeMenuItem("解锁", Menu_Unlock);
            mUnlock.SetBinding(MenuItem.IsEnabledProperty, new System.Windows.Data.Binding("CanWrite"));
            _rowMenu.Items.Add(mUnlock);

            _rowMenu.Items.Add(new Separator());

            MenuItem mAdd = MakeMenuItem("加入组…", Menu_AddMember);
            mAdd.SetBinding(MenuItem.IsEnabledProperty, new System.Windows.Data.Binding("CanWrite"));
            _rowMenu.Items.Add(mAdd);

            MenuItem mDel = MakeMenuItem("删除…", Menu_DeleteUser);
            mDel.SetBinding(MenuItem.IsEnabledProperty, new System.Windows.Data.Binding("CanWrite"));
            _rowMenu.Items.Add(mDel);

            _rowMenu.Items.Add(new Separator());
            _rowMenu.Items.Add(MakeMenuItem("刷新", Menu_Refresh));

            ObjectGrid.ContextMenu = _rowMenu;
        }

        private static MenuItem MakeMenuItem(string header, RoutedEventHandler handler)
        {
            MenuItem mi = new MenuItem();
            mi.Header = header;
            if (handler != null) mi.Click += handler;
            return mi;
        }

        private void ShowDetailsMenu(object sender, RoutedEventArgs e)
        {
            AdObject o = Selected;
            if (o != null) ShowDetails(o);
        }

        private void ObjectGrid_RightClick(object sender, MouseButtonEventArgs e)
        {
            // 右键时选中鼠标所在的行，符合使用习惯
            DependencyObject src = e.OriginalSource as DependencyObject;
            DataGridRow row = FindVisualParent<DataGridRow>(src);
            if (row != null)
            {
                row.IsSelected = true;
                row.Focus();
            }
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject current = child;
            while (current != null)
            {
                T match = current as T;
                if (match != null) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ---------------- 工具 ----------------

        private void SetStatus(string text)
        {
            StatusText.Text = text;
        }

        /// <summary>生成 16 位随机强密码</summary>
        public static string GeneratePassword(int length)
        {
            const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string lower = "abcdefghijkmnpqrstuvwxyz";
            const string digits = "23456789";
            const string special = "!@#$%^&*?-_=+";
            string all = upper + lower + digits + special;

            StringBuilder sb = new StringBuilder(length);
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                byte[] buf = new byte[4];

                // 保证四类字符各出现一次
                sb.Append(Pick(upper, rng, buf));
                sb.Append(Pick(lower, rng, buf));
                sb.Append(Pick(digits, rng, buf));
                sb.Append(Pick(special, rng, buf));

                while (sb.Length < length)
                {
                    sb.Append(Pick(all, rng, buf));
                }
            }

            // 洗牌，避免前四位固定是"大小数特"
            char[] chars = sb.ToString().ToCharArray();
            using (RNGCryptoServiceProvider rng2 = new RNGCryptoServiceProvider())
            {
                byte[] buf = new byte[4];
                for (int i = chars.Length - 1; i > 0; i--)
                {
                    int j = NextInt(rng2, buf, i + 1);
                    char t = chars[i]; chars[i] = chars[j]; chars[j] = t;
                }
            }
            return new string(chars);
        }

        private static char Pick(string set, RNGCryptoServiceProvider rng, byte[] buf)
        {
            return set[NextInt(rng, buf, set.Length)];
        }

        private static int NextInt(RNGCryptoServiceProvider rng, byte[] buf, int max)
        {
            rng.GetBytes(buf);
            uint v = BitConverter.ToUInt32(buf, 0);
            return (int)(v % (uint)max);
        }
    }
}
