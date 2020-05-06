using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics.Eventing.Reader;

namespace ADConsole.Services
{
    /// <summary>
    /// AD 操作封装（纯 LDAP / ADSI，不依赖 RSAT 模块）。
    /// 所有连接使用登录时提供的域账户凭据，因此写操作的权限由该账户决定。
    /// </summary>
    public class AdService
    {
        private string _domainDns;      // example.com
        private string _authUser;       // EXAMPLE\user1
        private string _password;
        private string _rootDn;         // DC=example,DC=com
        private string _forestDn;       // CN=Configuration,DC=example,DC=com
        private string _pdcHost;        // AD.example.com

        public string DomainDns { get { return _domainDns; } }
        public string RootDn { get { return _rootDn; } }
        public string ForestDn { get { return _forestDn; } }
        public string PdcHost { get { return _pdcHost; } }
        public string AuthUser { get { return _authUser; } }

        public void Connect(StoredCredential cred)
        {
            _domainDns = cred.Domain;
            _password = cred.Password;

            if (cred.UserName.Contains("\\"))
            {
                _authUser = cred.UserName;
            }
            else
            {
                string netbios = GetNetBiosName(cred.Domain);
                _authUser = netbios + "\\" + cred.UserName;
            }

            using (DirectoryEntry rootDse = BindDns("RootDSE"))
            {
                _rootDn = Convert.ToString(rootDse.Properties["defaultNamingContext"].Value);
                string configDn = Convert.ToString(rootDse.Properties["configurationNamingContext"].Value);
                _forestDn = configDn;

                object pdc = rootDse.Properties["dnsHostName"].Value;
                _pdcHost = pdc == null ? null : Convert.ToString(pdc);
            }

            if (string.IsNullOrEmpty(_rootDn))
            {
                throw new InvalidOperationException("无法获取域根 DN，当前账户可能不是有效的域账户。");
            }
        }

        // ---------------- 绑定 ----------------

        private string BuildPath(string dn)
        {
            if (string.IsNullOrEmpty(dn)) return "LDAP://" + _domainDns;
            if (dn.Equals("RootDSE", StringComparison.OrdinalIgnoreCase))
                return "LDAP://" + _domainDns + "/RootDSE";
            return "LDAP://" + dn;
        }

        private DirectoryEntry BindDns(string dn)
        {
            return new DirectoryEntry(BuildPath(dn), _authUser, _password, AuthenticationTypes.Secure);
        }

        private DirectoryEntry Bind(string dn)
        {
            return new DirectoryEntry(BuildPath(dn), _authUser, _password, AuthenticationTypes.Secure);
        }

        private string GetNetBiosName(string domainDns)
        {
            try
            {
                using (DirectoryEntry rootDse = BindDns("RootDSE"))
                {
                    string configDn = Convert.ToString(rootDse.Properties["configurationNamingContext"].Value);
                    using (DirectoryEntry part = new DirectoryEntry(
                        "LDAP://" + _domainDns + "/CN=Partitions," + configDn,
                        _authUser, _password, AuthenticationTypes.Secure))
                    {
                        foreach (DirectoryEntry c in part.Children)
                        {
                            string dns = Convert.ToString(c.Properties["dnsRoot"].Value);
                            string nb = Convert.ToString(c.Properties["netBIOSName"].Value);
                            string nc = Convert.ToString(c.Properties["nCName"].Value);
                            if (!string.IsNullOrEmpty(nb) &&
                                string.Equals(dns, domainDns, StringComparison.OrdinalIgnoreCase))
                            {
                                return nb;
                            }
                            c.Dispose();
                        }
                    }
                }
            }
            catch
            {
            }
            return domainDns;
        }

        // ---------------- 查询 ----------------

        /// <summary>构建 OU 树（含 CN=Users / CN=Computers 等内置容器）</summary>
        public OuTreeNode BuildOuTree()
        {
            OuTreeNode root = new OuTreeNode();
            root.Title = _domainDns;
            root.DistinguishedName = _rootDn;
            root.Kind = AdKind.Domain;
            root.Tip = _rootDn;

            Dictionary<string, OuTreeNode> map =
                new Dictionary<string, OuTreeNode>(StringComparer.OrdinalIgnoreCase);
            map[_rootDn] = root;

            List<string[]> items = new List<string[]>();
            string filter = "(|" +
                "(objectCategory=organizationalUnit)" +
                "(objectClass=container)(objectClass=domainDNS)" +
                ")";

            using (DirectorySearcher searcher = CreateSearcher(_rootDn))
            {
                searcher.Filter = filter;
                searcher.SearchScope = SearchScope.Subtree;
                searcher.PropertiesToLoad.AddRange(new string[] { "distinguishedName", "name", "objectClass", "ou" });

                foreach (SearchResult r in searcher.FindAll())
                {
                    string dn = Convert.ToString(r.Properties["distinguishedName"][0]);
                    if (string.Equals(dn, _rootDn, StringComparison.OrdinalIgnoreCase)) continue;

                    string name = null;
                    if (r.Properties["name"].Count > 0) name = Convert.ToString(r.Properties["name"][0]);

                    bool isOu = false;
                    if (r.Properties["objectClass"].Count > 0)
                    {
                        foreach (object oc in r.Properties["objectClass"])
                        {
                            if (string.Equals(Convert.ToString(oc), "organizationalUnit", StringComparison.OrdinalIgnoreCase))
                            {
                                isOu = true;
                                break;
                            }
                        }
                    }

                    items.Add(new string[] { dn, name, isOu ? "ou" : "container" });
                }
            }

            // 按父 DN 归并
            foreach (string[] it in items)
            {
                string dn = it[0];
                if (map.ContainsKey(dn)) continue;

                OuTreeNode node = new OuTreeNode();
                node.Title = string.IsNullOrEmpty(it[1]) ? dn : it[1];
                node.DistinguishedName = dn;
                node.Kind = it[2] == "ou" ? AdKind.OrganizationalUnit : AdKind.Container;
                node.Tip = dn;
                map[dn] = node;
            }

            foreach (KeyValuePair<string, OuTreeNode> kv in map)
            {
                if (string.Equals(kv.Key, _rootDn, StringComparison.OrdinalIgnoreCase)) continue;
                string parentDn = RemoveFirstRdn(kv.Key);
                OuTreeNode parentNode;
                if (!string.IsNullOrEmpty(parentDn) && map.TryGetValue(parentDn, out parentNode))
                {
                    parentNode.Children.Add(kv.Value);
                }
            }

            SortTree(root);
            return root;
        }

        /// <summary>去掉 DN 的第一段（跳过转义字符），返回父 DN</summary>
        public static string RemoveFirstRdn(string dn)
        {
            if (string.IsNullOrEmpty(dn)) return null;
            bool escaped = false;
            for (int i = 0; i < dn.Length; i++)
            {
                char c = dn[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == ',')
                {
                    if (i + 1 >= dn.Length) return null;
                    return dn.Substring(i + 1);
                }
            }
            return null;
        }

        private static void SortTree(OuTreeNode node)
        {
            node.Children.Sort(delegate(OuTreeNode a, OuTreeNode b)
            {
                if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });
            foreach (OuTreeNode c in node.Children) SortTree(c);
        }

        private DirectorySearcher CreateSearcher(string baseDn)
        {
            DirectoryEntry root = Bind(baseDn);
            DirectorySearcher s = new DirectorySearcher(root);
            s.PageSize = 1000;           // 分页，避免超过单次查询上限
            s.SizeLimit = 0;
            s.ClientTimeout = TimeSpan.FromSeconds(30);
            s.ServerTimeLimit = TimeSpan.FromSeconds(30);
            return s;
        }

        /// <summary>按类型 + 关键字查询对象</summary>
        public List<AdObject> SearchObjects(AdKind kind, string baseDn, string keyword, bool recursive)
        {
            List<AdObject> list = new List<AdObject>();
            string filter = BuildFilter(kind, keyword);

            using (DirectorySearcher searcher = CreateSearcher(baseDn))
            {
                searcher.Filter = filter;
                searcher.SearchScope = recursive ? SearchScope.Subtree : SearchScope.OneLevel;
                AddProperties(searcher, kind);

                foreach (SearchResult r in searcher.FindAll())
                {
                    AdObject o = FromSearchResult(r, kind);
                    if (o != null) list.Add(o);
                }
            }

            list.Sort(delegate(AdObject a, AdObject b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        private static string BuildFilter(AdKind kind, string keyword)
        {
            string baseFilter;
            switch (kind)
            {
                case AdKind.User:
                    baseFilter = "(&(objectCategory=person)(objectClass=user)(!(objectClass=computer)))";
                    break;
                case AdKind.Group:
                    baseFilter = "(objectCategory=group)";
                    break;
                case AdKind.Computer:
                    baseFilter = "(objectCategory=computer)";
                    break;
                default:
                    baseFilter = "(objectCategory=organizationalUnit)";
                    break;
            }

            if (string.IsNullOrEmpty(keyword)) return baseFilter;

            string k = EscapeLdap(keyword);
            string kw = "(|(displayName=*" + k + "*)(name=*" + k + "*)(sAMAccountName=*" + k + "*))";
            if (kind == AdKind.Computer)
            {
                kw = "(|(name=*" + k + "*)(dNSHostName=*" + k + "*)(operatingSystem=*" + k + "*))";
            }
            return "(&" + baseFilter + kw + ")";
        }

        private static void AddProperties(DirectorySearcher s, AdKind kind)
        {
            List<string> p = new List<string>();
            p.Add("distinguishedName"); p.Add("name"); p.Add("cn"); p.Add("description");
            p.Add("whenCreated"); p.Add("whenChanged"); p.Add("objectClass"); p.Add("displayName");

            if (kind == AdKind.User)
            {
                p.AddRange(new string[] {
                    "sAMAccountName","userPrincipalName","mail","department","title","telephoneNumber",
                    "physicalDeliveryOfficeName","userAccountControl","accountExpires","lastLogonTimestamp",
                    "pwdLastSet","lockoutTime","badPasswordTime","pwdAttributes","msDS-UserPasswordExpiryTimeComputed",
                    "userAccountControl" });
            }
            else if (kind == AdKind.Group)
            {
                p.AddRange(new string[] { "sAMAccountName","groupType","whenCreated" });
            }
            else if (kind == AdKind.Computer)
            {
                p.AddRange(new string[] { "sAMAccountName","dNSHostName","operatingSystem",
                    "userAccountControl","lastLogonTimestamp" });
            }
            else
            {
                p.Add("ou");
            }
            s.PropertiesToLoad.AddRange(p.ToArray());
        }

        /// <summary>从 SearchResult 构造模型。返回 null 表示跳过</summary>
        public static AdObject FromSearchResult(SearchResult r, AdKind kind)
        {
            if (r == null) return null;
            AdObject o = new AdObject();
            o.Kind = kind;

            o.DistinguishedName = GetStr(r, "distinguishedName");
            o.Name = GetStr(r, "name");
            if (string.IsNullOrEmpty(o.Name)) o.Name = GetStr(r, "cn");
            if (string.IsNullOrEmpty(o.Name)) o.Name = o.DistinguishedName;
            o.Description = GetStr(r, "description");
            o.DisplayName = GetStr(r, "displayName");
            o.WhenCreated = GetDate(r, "whenCreated");
            o.WhenChanged = GetDate(r, "whenChanged");

            if (kind == AdKind.User)
            {
                o.SamAccountName = GetStr(r, "sAMAccountName");
                o.UserPrincipalName = GetStr(r, "userPrincipalName");
                o.Email = GetStr(r, "mail");
                o.Department = GetStr(r, "department");
                o.Title = GetStr(r, "title");
                o.Phone = GetStr(r, "telephoneNumber");
                o.Office = GetStr(r, "physicalDeliveryOfficeName");

                int uac = GetInt(r, "userAccountControl");
                o.Enabled = (uac & 0x2) == 0;
                o.PasswordNeverExpires = (uac & 0x10000) != 0;

                long lockout = GetLong(r, "lockoutTime");
                long badTime = GetLong(r, "badPasswordTime");
                DateTime now = DateTime.Now;
                o.LockedOut = lockout > 0 && DateTime.FromFileTimeUtc(lockout).ToLocalTime() > now.AddMinutes(-30);

                o.PasswordLastSet = FileTimeOrNull(GetLong(r, "pwdLastSet"));
                o.LastLogonApprox = FileTimeOrNull(GetLong(r, "lastLogonTimestamp"));
            }
            else if (kind == AdKind.Group)
            {
                o.SamAccountName = GetStr(r, "sAMAccountName");
                long gt = GetLong(r, "groupType");
                o.GroupScope = DescribeGroupScope(gt);
                o.GroupCategory = DescribeGroupCategory(gt);
            }
            else if (kind == AdKind.Computer)
            {
                o.SamAccountName = GetStr(r, "sAMAccountName");
                o.DnsHostName = GetStr(r, "dNSHostName");
                o.OperatingSystem = GetStr(r, "operatingSystem");
                int uac = GetInt(r, "userAccountControl");
                o.Enabled = (uac & 0x2) == 0;
                o.LastLogonApprox = FileTimeOrNull(GetLong(r, "lastLogonTimestamp"));
            }
            else
            {
                o.SamAccountName = GetStr(r, "ou");
            }

            return o;
        }

        private static string GetStr(SearchResult r, string prop)
        {
            if (r.Properties.Contains(prop) && r.Properties[prop].Count > 0)
            {
                object v = r.Properties[prop][0];
                if (v == null) return null;
                string s = v.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            return null;
        }

        private static int GetInt(SearchResult r, string prop)
        {
            object v = GetObj(r, prop);
            if (v == null) return 0;
            try { return Convert.ToInt32(v); }
            catch { return 0; }
        }

        private static long GetLong(SearchResult r, string prop)
        {
            object v = GetObj(r, prop);
            if (v == null) return 0;
            try { return Convert.ToInt64(v); }
            catch { return 0; }
        }

        private static object GetObj(SearchResult r, string prop)
        {
            if (r.Properties.Contains(prop) && r.Properties[prop].Count > 0)
                return r.Properties[prop][0];
            return null;
        }

        private static DateTime? GetDate(SearchResult r, string prop)
        {
            object v = GetObj(r, prop);
            if (v == null) return null;
            if (v is DateTime) return (DateTime)v;
            try { return Convert.ToDateTime(v); }
            catch { return null; }
        }

        public static DateTime? FileTimeOrNull(long ft)
        {
            if (ft <= 0 || ft == long.MaxValue) return null;
            try { return DateTime.FromFileTimeUtc(ft).ToLocalTime(); }
            catch { return null; }
        }

        public static string DescribeGroupScope(long groupType)
        {
            // groupType 位标志：0x1 全局 / 0x2 域本地 / 0x8 通用 / 0x80000000 安全组
            long g = groupType & 0xFFFFFFFF;
            if ((g & 0x1L) != 0) return "全局";
            if ((g & 0x2L) != 0) return "域本地";
            if ((g & 0x8L) != 0) return "通用";
            return "未知";
        }

        public static string DescribeGroupCategory(long groupType)
        {
            if ((groupType & 0x80000000L) != 0) return "安全组";
            return "通讯组";
        }

        // ---------------- LDAP 转义 ----------------

        public static string EscapeLdap(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length * 2);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\5c"); break;
                    case '*': sb.Append("\\2a"); break;
                    case '(': sb.Append("\\28"); break;
                    case ')': sb.Append("\\29"); break;
                    case '\0': sb.Append("\\00"); break;
                    case '/': sb.Append("\\2f"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // ---------------- 写操作 ----------------

        /// <summary>新建用户（含设置密码与启用）</summary>
        public void CreateUser(string ouDn, string name, string sam, string password,
            string department, string title, string email, string phone, string description)
        {
            using (DirectoryEntry ou = Bind(ouDn))
            {
                using (DirectoryEntry user = ou.Children.Add("CN=" + EscapeRdn(name), "user"))
                {
                    user.Properties["sAMAccountName"].Value = sam;
                    user.CommitChanges();

                    // 密码必须在 CommitChanges 之后设置
                    user.Invoke("SetPassword", password);

                    user.Properties["userPrincipalName"].Value = sam + "@" + _domainDns;
                    if (!string.IsNullOrEmpty(department)) user.Properties["department"].Value = department;
                    if (!string.IsNullOrEmpty(title)) user.Properties["title"].Value = title;
                    if (!string.IsNullOrEmpty(email)) user.Properties["mail"].Value = email;
                    if (!string.IsNullOrEmpty(phone)) user.Properties["telephoneNumber"].Value = phone;
                    if (!string.IsNullOrEmpty(description)) user.Properties["description"].Value = description;

                    // 启用账户：清除 ACCOUNTDISABLE(0x2)
                    int uac = 0;
                    object v = user.Properties["userAccountControl"].Value;
                    if (v != null) uac = Convert.ToInt32(v);
                    user.Properties["userAccountControl"].Value = uac & ~0x2;
                    user.CommitChanges();
                }
            }
        }

        /// <summary>转义 RDN 值里的特殊字符</summary>
        public static string EscapeRdn(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length * 2);
            foreach (char c in s)
            {
                if (c == ',' || c == '\\' || c == '+' || c == '"' || c == '<' || c == '>' || c == ';' || c == '=')
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        public void SetPassword(string dn, string password)
        {
            using (DirectoryEntry e = Bind(dn))
            {
                e.Invoke("SetPassword", password);
                e.CommitChanges();
            }
        }

        /// <summary>启用/禁用账户</summary>
        public void SetEnabled(string dn, bool enabled)
        {
            using (DirectoryEntry e = Bind(dn))
            {
                int uac = Convert.ToInt32(e.Properties["userAccountControl"].Value);
                if (enabled) uac &= ~0x2; else uac |= 0x2;
                e.Properties["userAccountControl"].Value = uac;
                e.CommitChanges();
            }
        }

        /// <summary>解锁账户</summary>
        public void UnlockAccount(string dn)
        {
            using (DirectoryEntry e = Bind(dn))
            {
                e.Properties["lockoutTime"].Value = 0;
                e.CommitChanges();
            }
        }

        /// <summary>要求用户下次登录时必须修改密码（pwdLastSet = 0）</summary>
        public void ForceChangeAtLogon(string dn)
        {
            using (DirectoryEntry e = Bind(dn))
            {
                e.Properties["pwdLastSet"].Value = 0;
                e.CommitChanges();
            }
        }

        /// <summary>删除对象（递归删除其子对象）</summary>
        public void DeleteObject(string dn)
        {
            using (DirectoryEntry e = Bind(dn))
            {
                e.DeleteTree();
            }
        }

        /// <summary>新建组</summary>
        public void CreateGroup(string ouDn, string name, string sam, string description)
        {
            using (DirectoryEntry ou = Bind(ouDn))
            {
                using (DirectoryEntry g = ou.Children.Add("CN=" + EscapeRdn(name), "group"))
                {
                    g.Properties["sAMAccountName"].Value = sam;
                    // 0x80000000 = 安全组，0x1 = 全局作用域
                    g.Properties["groupType"].Value = unchecked((int)0x80000001L);
                    if (!string.IsNullOrEmpty(description)) g.Properties["description"].Value = description;
                    g.CommitChanges();
                }
            }
        }

        /// <summary>添加成员（支持用户/组/计算机）</summary>
        public void AddMember(string groupDn, string memberDn)
        {
            using (DirectoryEntry g = Bind(groupDn))
            {
                using (DirectoryEntry m = Bind(memberDn))
                {
                    g.Invoke("Add", new object[] { m.Path });
                    g.CommitChanges();
                }
            }
        }

        public void RemoveMember(string groupDn, string memberDn)
        {
            using (DirectoryEntry g = Bind(groupDn))
            {
                using (DirectoryEntry m = Bind(memberDn))
                {
                    g.Invoke("Remove", new object[] { m.Path });
                    g.CommitChanges();
                }
            }
        }

        /// <summary>直接成员</summary>
        public List<AdObject> GetMembers(string groupDn)
        {
            List<AdObject> list = new List<AdObject>();
            using (DirectoryEntry g = Bind(groupDn))
            {
                g.RefreshCache(new string[] { "member" });
                PropertyValueCollection col = g.Properties["member"];
                for (int i = 0; i < col.Count; i++)
                {
                    string dn = Convert.ToString(col[i]);
                    AdObject o = GetByDn(dn);
                    if (o != null) list.Add(o);
                    else
                    {
                        AdObject x = new AdObject();
                        x.DistinguishedName = dn;
                        x.Name = dn;
                        x.Kind = AdKind.Container;
                        list.Add(x);
                    }
                }
            }
            list.Sort(delegate(AdObject a, AdObject b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        /// <summary>递归成员数</summary>
        public int CountMembersRecursive(string groupDn)
        {
            int count = 0;
            Queue<string> queue = new Queue<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue(groupDn);
            seen.Add(groupDn);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                try
                {
                    using (DirectoryEntry g = Bind(current))
                    {
                        g.RefreshCache(new string[] { "member", "objectClass" });
                        PropertyValueCollection col = g.Properties["member"];
                        for (int i = 0; i < col.Count; i++)
                        {
                            string dn = Convert.ToString(col[i]);
                            if (!seen.Add(dn)) continue;
                            count++;
                            queue.Enqueue(dn);
                        }
                    }
                }
                catch
                {
                }
            }
            return count;
        }

        /// <summary>对象所属的组（直接）</summary>
        public List<AdObject> GetMemberOf(string dn)
        {
            List<AdObject> list = new List<AdObject>();
            using (DirectoryEntry e = Bind(dn))
            {
                e.RefreshCache(new string[] { "memberOf" });
                PropertyValueCollection col = e.Properties["memberOf"];
                for (int i = 0; i < col.Count; i++)
                {
                    string gdn = Convert.ToString(col[i]);
                    AdObject g = GetByDn(gdn);
                    if (g != null) list.Add(g);
                }
            }
            list.Sort(delegate(AdObject a, AdObject b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        /// <summary>按 DN 取单个对象（属性面板 / 成员解析用）</summary>
        public AdObject GetByDn(string dn)
        {
            if (string.IsNullOrEmpty(dn)) return null;
            try
            {
                using (DirectoryEntry e = Bind(dn))
                {
                    e.RefreshCache(new string[] { "objectClass", "objectCategory", "name", "cn", "description" });
                    object cls = e.Properties["objectClass"].Value;
                    PropertyValueCollection oc = e.Properties["objectClass"];
                    string cat = Convert.ToString(e.Properties["objectCategory"].Value);

                    AdKind kind;
                    if (oc.Contains("computer")) kind = AdKind.Computer;
                    else if (oc.Contains("group")) kind = AdKind.Group;
                    else if (oc.Contains("user")) kind = AdKind.User;
                    else if (oc.Contains("organizationalUnit")) kind = AdKind.OrganizationalUnit;
                    else if (oc.Contains("domainDNS")) kind = AdKind.Domain;
                    else kind = AdKind.Container;

                    AdObject o = new AdObject();
                    o.Kind = kind;
                    o.DistinguishedName = dn;
                    o.Name = Convert.ToString(e.Properties["name"].Value);
                    if (string.IsNullOrEmpty(o.Name)) o.Name = Convert.ToString(e.Properties["cn"].Value);
                    if (string.IsNullOrEmpty(o.Name)) o.Name = dn;
                    o.Description = Convert.ToString(e.Properties["description"].Value);

                    if (kind == AdKind.User || kind == AdKind.Computer)
                    {
                        e.RefreshCache(new string[] { "sAMAccountName", "userAccountControl", "displayName",
                            "mail", "department", "title", "telephoneNumber", "physicalDeliveryOfficeName",
                            "userPrincipalName", "pwdLastSet", "lockoutTime", "lastLogonTimestamp" });
                        o.SamAccountName = Convert.ToString(e.Properties["sAMAccountName"].Value);
                        o.DisplayName = Convert.ToString(e.Properties["displayName"].Value);
                        o.Email = Convert.ToString(e.Properties["mail"].Value);
                        o.Department = Convert.ToString(e.Properties["department"].Value);
                        o.Title = Convert.ToString(e.Properties["title"].Value);
                        o.Phone = Convert.ToString(e.Properties["telephoneNumber"].Value);
                        o.Office = Convert.ToString(e.Properties["physicalDeliveryOfficeName"].Value);
                        o.UserPrincipalName = Convert.ToString(e.Properties["userPrincipalName"].Value);
                        if (e.Properties["dNSHostName"].Count > 0)
                            o.DnsHostName = Convert.ToString(e.Properties["dNSHostName"].Value);

                        object uv = e.Properties["userAccountControl"].Value;
                        int uac = uv == null ? 0 : Convert.ToInt32(uv);
                        o.Enabled = (uac & 0x2) == 0;
                        o.PasswordNeverExpires = (uac & 0x10000) != 0;

                        object pl = e.Properties["pwdLastSet"].Value;
                        if (pl != null)
                        {
                            long ft = Convert.ToInt64(pl);
                            o.PasswordLastSet = ft > 0 ? (DateTime?)DateTime.FromFileTimeUtc(ft).ToLocalTime() : null;
                        }
                        object ll = e.Properties["lastLogonTimestamp"].Value;
                        if (ll != null)
                        {
                            long ft = Convert.ToInt64(ll);
                            o.LastLogonApprox = ft > 0 ? (DateTime?)DateTime.FromFileTimeUtc(ft).ToLocalTime() : null;
                        }
                    }
                    else if (kind == AdKind.Group)
                    {
                        e.RefreshCache(new string[] { "sAMAccountName", "groupType" });
                        o.SamAccountName = Convert.ToString(e.Properties["sAMAccountName"].Value);
                        object gv = e.Properties["groupType"].Value;
                        long gt = gv == null ? 0 : Convert.ToInt64(gv);
                        o.GroupScope = DescribeGroupScope(gt);
                        o.GroupCategory = DescribeGroupCategory(gt);
                    }
                    return o;
                }
            }
            catch
            {
                return null;
            }
        }

        // ---------------- 域信息 ----------------

        /// <summary>域概览（对象计数）</summary>
        public Dictionary<string, int> GetOverview()
        {
            Dictionary<string, int> d = new Dictionary<string, int>();
            d["用户"] = Count("(objectCategory=person)(objectClass=user)(!(objectClass=computer))");
            d["组"] = Count("(objectCategory=group)");
            d["计算机"] = Count("(objectCategory=computer)");
            d["OU"] = Count("(objectCategory=organizationalUnit)");
            d["域功能级别"] = 0;
            return d;
        }

        private int Count(string filter)
        {
            using (DirectorySearcher s = CreateSearcher(_rootDn))
            {
                s.Filter = filter;
                s.SearchScope = SearchScope.Subtree;
                s.PropertiesToLoad.Add("distinguishedName");
                return s.FindAll().Count;
            }
        }

        /// <summary>域密码与锁定策略</summary>
        public List<InfoRow> GetDomainPolicy()
        {
            List<InfoRow> rows = new List<InfoRow>();
            using (DirectoryEntry d = Bind(_rootDn))
            {
                d.RefreshCache(new string[] {
                    "maxPwdAge","minPwdLength","pwdHistoryCount","minPwdAge",
                    "lockoutThreshold","lockoutDuration","lockOutObservationWindow",
                    "msDS-Behavior-Version","dcFunctionality" });

                rows.Add(new InfoRow { Key = "域", Value = _domainDns, IsSection = false });
                rows.Add(new InfoRow { Key = "域根 DN", Value = _rootDn, IsSection = false });
                rows.Add(new InfoRow { Key = "PDC / 首选域控", Value = _pdcHost, IsSection = false });

                long maxAge = GetLongProp(d, "maxPwdAge");
                rows.Add(new InfoRow
                {
                    Key = "密码最长使用期限",
                    Value = maxAge == 0 ? "永不过期" : (TimeSpan.FromTicks(-maxAge).Days + " 天"),
                    IsSection = false
                });

                rows.Add(new InfoRow
                {
                    Key = "密码最短使用期限",
                    Value = TimeSpan.FromTicks(-GetLongProp(d, "minPwdAge")).Days + " 天",
                    IsSection = false
                });

                rows.Add(new InfoRow { Key = "密码最小长度", Value = GetIntProp(d, "minPwdLength") + " 位", IsSection = false });
                rows.Add(new InfoRow { Key = "密码历史记录数", Value = GetIntProp(d, "pwdHistoryCount") + " 个", IsSection = false });

                int threshold = GetIntProp(d, "lockoutThreshold");
                rows.Add(new InfoRow { Key = "账户锁定阈值", Value = threshold == 0 ? "不锁定" : (threshold + " 次"), IsSection = false });
                if (threshold > 0)
                {
                    rows.Add(new InfoRow
                    {
                        Key = "账户锁定时长",
                        Value = TimeSpan.FromTicks(-GetLongProp(d, "lockoutDuration")).Minutes + " 分钟",
                        IsSection = false
                    });
                    rows.Add(new InfoRow
                    {
                        Key = "锁定观察窗口",
                        Value = TimeSpan.FromTicks(-GetLongProp(d, "lockOutObservationWindow")).Minutes + " 分钟",
                        IsSection = false
                    });
                }

                rows.Add(new InfoRow { Key = "域功能级别", Value = GetIntProp(d, "msDS-Behavior-Version").ToString(), IsSection = false });
            }
            return rows;
        }

        private static long GetLongProp(DirectoryEntry e, string prop)
        {
            object v = e.Properties[prop].Value;
            if (v == null) return 0;
            try { return Convert.ToInt64(v); }
            catch { return 0; }
        }

        private static int GetIntProp(DirectoryEntry e, string prop)
        {
            object v = e.Properties[prop].Value;
            if (v == null) return 0;
            try { return Convert.ToInt32(v); }
            catch { return 0; }
        }

        /// <summary>枚举域控</summary>
        public List<AdObject> GetDomainControllers()
        {
            List<AdObject> list = new List<AdObject>();
            using (DirectorySearcher s = CreateSearcher("CN=Domain Controllers," + _rootDn))
            {
                s.Filter = "(objectCategory=computer)";
                s.SearchScope = SearchScope.OneLevel;
                s.PropertiesToLoad.AddRange(new string[] {
                    "name","dNSHostName","operatingSystem","operatingSystemVersion","distinguishedName","userAccountControl" });

                foreach (SearchResult r in s.FindAll())
                {
                    AdObject o = new AdObject();
                    o.Kind = AdKind.Computer;
                    o.DistinguishedName = GetStr(r, "distinguishedName");
                    o.Name = GetStr(r, "name");
                    o.DnsHostName = GetStr(r, "dNSHostName");
                    o.OperatingSystem = GetStr(r, "operatingSystem");
                    o.SamAccountName = GetStr(r, "name");
                    list.Add(o);
                }
            }
            return list;
        }

        /// <summary>枚举全局编目服务器</summary>
        public List<string> GetGlobalCatalogs()
        {
            List<string> gcs = new List<string>();
            string configDn = _forestDn;
            string filter = "(&(objectClass=nTDSDSA)(options:1.2.840.113556.1.4.803:=1))";

            using (DirectorySearcher s = CreateSearcher(configDn))
            {
                s.Filter = filter;
                s.SearchScope = SearchScope.Subtree;
                s.PropertiesToLoad.AddRange(new string[] { "distinguishedName" });

                foreach (SearchResult r in s.FindAll())
                {
                    string ntdsDn = GetStr(r, "distinguishedName");
                    // CN=NTDS Settings,CN=xxx,CN=Servers,CN=Site,...
                    string parent = RemoveFirstRdn(ntdsDn);   // CN=xxx,...
                    string server = RemoveFirstRdn(parent);   // CN=Servers,...
                    // 其实 parent 才是 server DN，取其 CN
                    string serverDn = parent;
                    string name = ExtractRdnValue(serverDn);
                    if (!string.IsNullOrEmpty(name)) gcs.Add(name);
                }
            }
            return gcs;
        }

        private static string ExtractRdnValue(string dn)
        {
            if (string.IsNullOrEmpty(dn)) return null;
            int i = dn.IndexOf('=');
            if (i < 0) return dn;
            string v = dn.Substring(i + 1);
            int comma = -1;
            bool esc = false;
            for (int k = 0; k < v.Length; k++)
            {
                if (esc) { esc = false; continue; }
                if (v[k] == '\\') { esc = true; continue; }
                if (v[k] == ',') { comma = k; break; }
            }
            if (comma >= 0) v = v.Substring(0, comma);
            return v;
        }

        /// <summary>五个 FSMO 角色的持有者</summary>
        public List<InfoRow> GetFsmoRoles()
        {
            List<InfoRow> rows = new List<InfoRow>();
            rows.Add(new InfoRow
            {
                Key = "架构主机 (Schema Master)",
                Value = OwnerOf("CN=Schema," + _forestDn)
            });
            rows.Add(new InfoRow
            {
                Key = "域命名主机 (Domain Naming Master)",
                Value = OwnerOf(_forestDn)
            });
            rows.Add(new InfoRow
            {
                Key = "PDC 模拟器 (PDC Emulator)",
                Value = OwnerOf(_rootDn)
            });
            rows.Add(new InfoRow
            {
                Key = "RID 主机 (RID Master)",
                Value = OwnerOf("CN=RID Manager$,CN=System," + _rootDn)
            });
            rows.Add(new InfoRow
            {
                Key = "基础结构主机 (Infrastructure Master)",
                Value = OwnerOf("CN=Infrastructure," + _rootDn)
            });
            return rows;
        }

        private string OwnerOf(string dn)
        {
            try
            {
                using (DirectoryEntry e = Bind(dn))
                {
                    e.RefreshCache(new string[] { "fSMORoleOwner" });
                    object v = e.Properties["fSMORoleOwner"].Value;
                    if (v == null) return "未获取到";
                    string ownerDn = Convert.ToString(v);
                    // CN=NTDS Settings,CN=<DC>,OU=Domain Controllers,...
                    string dcDn = RemoveFirstRdn(ownerDn);
                    string host = ExtractRdnValue(dcDn);
                    return string.IsNullOrEmpty(host) ? ownerDn : host;
                }
            }
            catch (Exception ex)
            {
                return "查询失败：" + ex.Message;
            }
        }

        // ---------------- 密码到期 ----------------

        /// <summary>查询密码即将到期的账号</summary>
        public List<PasswordExpiryRow> GetPasswordExpiry(int daysAhead, bool includeExpired, bool includeNeverExpires)
        {
            List<PasswordExpiryRow> rows = new List<PasswordExpiryRow>();

            using (DirectorySearcher s = CreateSearcher(_rootDn))
            {
                s.Filter = "(&(objectCategory=person)(objectClass=user)(!(objectClass=computer)))";
                s.SearchScope = SearchScope.Subtree;
                s.PropertiesToLoad.AddRange(new string[] {
                    "name","displayName","sAMAccountName","pwdLastSet","userAccountControl",
                    "msDS-UserPasswordExpiryTimeComputed","distinguishedName" });

                foreach (SearchResult r in s.FindAll())
                {
                    string name = GetStr(r, "name");
                    string sam = GetStr(r, "sAMAccountName");
                    int uac = GetInt(r, "userAccountControl");
                    bool neverExpires = (uac & 0x10000) != 0;
                    bool enabled = (uac & 0x2) == 0;

                    long pwdSet = GetLong(r, "pwdLastSet");
                    long computed = GetLong(r, "msDS-UserPasswordExpiryTimeComputed");

                    PasswordExpiryRow row = new PasswordExpiryRow();
                    row.Name = GetStr(r, "displayName");
                    if (string.IsNullOrEmpty(row.Name)) row.Name = name;
                    row.SamAccountName = sam;
                    row.OuDn = GetStr(r, "distinguishedName");
                    // 显示完整 DN：去掉第一段会把 OU 自己的名字一起丢掉
                    row.OuDisplay = row.OuDn;
                    row.PasswordLastSet = pwdSet > 0 && pwdSet != long.MaxValue
                        ? (DateTime?)DateTime.FromFileTimeUtc(pwdSet).ToLocalTime() : null;

                    if (pwdSet == 0)
                    {
                        row.Status = "从未设置密码";
                        row.ExpireDate = null;
                        row.DaysLeft = null;
                    }
                    else if (neverExpires || computed == 0 || computed == long.MaxValue)
                    {
                        if (!includeNeverExpires) continue;
                        row.Status = "永不过期";
                        row.ExpireDate = null;
                        row.DaysLeft = null;
                    }
                    else
                    {
                        row.ExpireDate = DateTime.FromFileTimeUtc(computed).ToLocalTime();
                        int days = (int)Math.Floor((row.ExpireDate.Value - DateTime.Now).TotalDays);
                        row.DaysLeft = days;

                        if (!enabled)
                        {
                            row.Status = "已禁用";
                        }
                        else if (days < 0)
                        {
                            row.Status = "已过期";
                            if (!includeExpired) continue;
                        }
                        else if (days <= daysAhead)
                        {
                            row.Status = "即将到期";
                        }
                        else
                        {
                            continue;
                        }
                    }

                    // 从未设置密码的账号也要能被筛出来
                    if (row.DaysLeft.HasValue && row.DaysLeft.Value > daysAhead && row.Status != "已禁用")
                        continue;

                    rows.Add(row);
                }
            }

            rows.Sort(delegate(PasswordExpiryRow a, PasswordExpiryRow b)
            {
                int? x = a.DaysLeft;
                int? y = b.DaysLeft;
                if (!x.HasValue && !y.HasValue) return 0;
                if (!x.HasValue) return -1;
                if (!y.HasValue) return 1;
                return x.Value.CompareTo(y.Value);
            });
            return rows;
        }

        /// <summary>密码到期计算函数（便于测试）</summary>
        public static PasswordExpiryRow ComputeExpiry(string name, string sam, string ouDn,
            DateTime? pwdLastSet, DateTime? computedExpire, bool neverExpires, bool enabled)
        {
            PasswordExpiryRow row = new PasswordExpiryRow();
            row.Name = name;
            row.SamAccountName = sam;
            row.OuDn = ouDn;
            row.OuDisplay = ouDn;
            row.PasswordLastSet = pwdLastSet;

            if (!pwdLastSet.HasValue)
            {
                row.Status = "从未设置密码";
                return row;
            }
            if (neverExpires || !computedExpire.HasValue)
            {
                row.Status = "永不过期";
                return row;
            }

            row.ExpireDate = computedExpire;
            row.DaysLeft = (int)Math.Floor((computedExpire.Value - DateTime.Now).TotalDays);
            if (!enabled) row.Status = "已禁用";
            else if (row.DaysLeft.Value < 0) row.Status = "已过期";
            else if (row.DaysLeft.Value == 0) row.Status = "今日到期";
            else row.Status = "即将到期";
            return row;
        }

        // ---------------- 登录历史 ----------------

        /// <summary>
        /// 逐台域控汇总 lastLogon。
        /// lastLogon 不复制，必须每台查才能得到精确值。
        /// </summary>
        public void FillExactLastLogon(List<AdObject> users, Action<string> progress)
        {
            List<string> servers = GetDcHosts();
            foreach (AdObject u in users)
            {
                if (progress != null) progress(u.Name);

                DateTime? best = null;
                string bestServer = null;
                foreach (string dc in servers)
                {
                    try
                    {
                        DateTime? v = ReadLastLogonOn(dc, u.DistinguishedName);
                        if (v.HasValue && (!best.HasValue || v.Value > best.Value))
                        {
                            best = v;
                            bestServer = dc;
                        }
                    }
                    catch
                    {
                    }
                }
                u.LastLogonExact = best;
                u.LastLogonServer = bestServer;
            }
        }

        private List<string> GetDcHosts()
        {
            List<string> hosts = new List<string>();
            List<AdObject> dcs = GetDomainControllers();
            foreach (AdObject d in dcs)
            {
                if (!string.IsNullOrEmpty(d.DnsHostName)) hosts.Add(d.DnsHostName);
            }
            if (hosts.Count == 0 && !string.IsNullOrEmpty(_pdcHost)) hosts.Add(_pdcHost);
            return hosts;
        }

        private DateTime? ReadLastLogonOn(string dc, string userDn)
        {
            using (DirectoryEntry e = new DirectoryEntry(
                "LDAP://" + dc + "/" + userDn, _authUser, _password, AuthenticationTypes.Secure))
            {
                e.RefreshCache(new string[] { "lastLogon" });
                object v = e.Properties["lastLogon"].Value;
                if (v == null) return null;
                long ft = Convert.ToInt64(v);
                return FileTimeOrNull(ft);
            }
        }

        /// <summary>登录类型中文名</summary>
        public static string DescribeLogonType(int type)
        {
            switch (type)
            {
                case 2: return "交互式";
                case 3: return "网络";
                case 4: return "批处理";
                case 5: return "服务";
                case 7: return "解锁";
                case 8: return "网络明文";
                case 9: return "新凭据";
                case 10: return "远程交互 (RDP)";
                case 11: return "缓存凭据";
                default: return "类型" + type;
            }
        }

        /// <summary>
        /// 查询域控安全日志中的 4624 登录事件。
        /// 需要域管理员或 Event Log Readers 权限，无权限时抛异常由调用方降级处理。
        /// </summary>
        public List<LoginEventRow> GetLogonEvents(int days, string accountFilter, Action<string> progress)
        {
            List<LoginEventRow> rows = new List<LoginEventRow>();
            List<string> servers = GetDcHosts();

            foreach (string dc in servers)
            {
                if (progress != null) progress(dc);
                try
                {
                    string q = "*[System[(EventID=4624) and TimeCreated[timediff(@SystemTime) <= " +
                               (int)TimeSpan.FromDays(days).TotalMilliseconds + "]]]";

                    // 用应用内登录的域账户凭据读取安全日志
                    string logonUser = _authUser;
                    int slash = logonUser.IndexOf('\\');
                    if (slash >= 0) logonUser = logonUser.Substring(slash + 1);

                    SecureString securePwd = new SecureString();
                    foreach (char ch in (_password ?? string.Empty)) securePwd.AppendChar(ch);
                    securePwd.MakeReadOnly();

                    EventLogSession session = new EventLogSession(
                        dc, _domainDns, logonUser, securePwd, SessionAuthentication.Default);

                    EventLogQuery query = new EventLogQuery("Security", PathType.LogName, q);
                    query.Session = session;

                    using (EventLogReader reader = new EventLogReader(query))
                    {
                        EventRecord rec;
                        int scanned = 0;
                        while ((rec = reader.ReadEvent()) != null)
                        {
                            using (rec)
                            {
                                if (++scanned > 20000) break;   // 上限保护

                                string acct = GetEventProp(rec, 5);
                                string domain = GetEventProp(rec, 6);
                                string typeText = GetEventProp(rec, 7);
                                string ip = GetEventProp(rec, 16);
                                if (string.IsNullOrEmpty(ip)) ip = GetEventProp(rec, 17);

                                if (!string.IsNullOrEmpty(acct))
                                {
                                    string full = string.IsNullOrEmpty(domain) ? acct : (domain + "\\" + acct);
                                    if (!string.IsNullOrEmpty(accountFilter) &&
                                        full.IndexOf(accountFilter, StringComparison.OrdinalIgnoreCase) < 0)
                                    {
                                        continue;
                                    }

                                    LoginEventRow row = new LoginEventRow();
                                    row.Time = rec.TimeCreated ?? DateTime.MinValue;
                                    row.Account = full;
                                    row.IpAddress = string.IsNullOrEmpty(ip) ? "—" : ip;
                                    row.DomainController = dc;

                                    int lt;
                                    if (int.TryParse(typeText, out lt)) row.LogonType = DescribeLogonType(lt);
                                    else row.LogonType = typeText;

                                    rows.Add(row);
                                }
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    throw;
                }
                catch (EventLogException ex)
                {
                    // 远程拒绝访问也以 EventLogException 形式出现
                    if (ex.Message.Contains("拒绝") || ex.Message.Contains("Access") ||
                        ex.Message.Contains("权限") || ex.Message.Contains("denied"))
                    {
                        UnauthorizedAccessException ua = new UnauthorizedAccessException(
                            "当前账户无权读取域控（" + dc + "）的安全日志。需要域管理员或 Event Log Readers 权限。", ex);
                        throw ua;
                    }
                    throw;
                }
            }

            rows.Sort(delegate(LoginEventRow a, LoginEventRow b) { return b.Time.CompareTo(a.Time); });
            return rows;
        }

        private static string GetEventProp(EventRecord rec, int index)
        {
            try
            {
                if (rec.Properties == null || index >= rec.Properties.Count) return null;
                object v = rec.Properties[index].Value;
                return v == null ? null : v.ToString();
            }
            catch
            {
                return null;
            }
        }

        // ---------------- 常用校验 ----------------

        public bool SamExists(string sam)
        {
            using (DirectorySearcher s = CreateSearcher(_rootDn))
            {
                s.Filter = "(sAMAccountName=" + EscapeLdap(sam) + ")";
                s.SearchScope = SearchScope.Subtree;
                s.PropertiesToLoad.Add("distinguishedName");
                return s.FindOne() != null;
            }
        }

        /// <summary>sAMAccountName 合法性（AD 允许的最大长度 20，且不得含以下字符）</summary>
        public static string ValidateSam(string sam)
        {
            if (string.IsNullOrEmpty(sam)) return "登录名不能为空";
            if (sam.Length > 20) return "登录名长度不能超过 20 个字符";
            if (Regex.IsMatch(sam, @"[\\""/\[\]:;|=,+*?<>@]"))
                return "登录名含非法字符";
            if (sam.IndexOf(' ') >= 0) return "登录名不能包含空格";
            if (sam.StartsWith(".")) return "登录名不能以点开头";
            if (sam.EndsWith(".")) return "登录名不能以点结尾";
            return null;
        }

        public static string ValidatePassword(string pwd)
        {
            if (string.IsNullOrEmpty(pwd)) return "密码不能为空";
            if (pwd.Length < 8) return "密码长度不能少于 8 位";
            return null;
        }
    }
}
