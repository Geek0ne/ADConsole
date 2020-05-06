using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ADConsole.Services
{
    /// <summary>
    /// 账户权限判定：
    ///   1) 校验凭据是否有效
    ///   2) 判断账户是否属于高权限组（按 RID 判断，与界面语言无关）
    ///   3) 判断账户对某个 OU 是否具有委派写权限（读取对象 ACL）
    /// </summary>
    public static class PermissionService
    {
        // 域内高权限组的 RID（不按名称匹配，避免中文/英文系统差异）
        private static readonly string[] AdminRids = new string[]
        {
            "-512", // Domain Admins
            "-519", // Enterprise Admins
            "-518", // Schema Admins
            "-548", // Account Operators
            "-544"  // BUILTIN\Administrators（在域上下文中）
        };

        /// <summary>校验域账号密码是否有效</summary>
        public static bool ValidateCredential(string domain, string user, string password)
        {
            if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(user) ||
                string.IsNullOrEmpty(password))
            {
                return false;
            }

            try
            {
                using (PrincipalContext ctx = new PrincipalContext(ContextType.Domain, domain))
                {
                    return ctx.ValidateCredentials(user, password, ContextOptions.Negotiate);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>查询当前账户对指定 OU 的写权限</summary>
        public static PermissionLevel GetLevel(string domainDn, string userDn)
        {
            try
            {
                HashSet<string> tokenSids = GetTokenGroupSids(userDn);
                if (tokenSids == null || tokenSids.Count == 0)
                {
                    return PermissionLevel.ReadOnly;
                }

                // 是否命中高权限组
                foreach (string sid in tokenSids)
                {
                    foreach (string rid in AdminRids)
                    {
                        if (sid.EndsWith(rid, StringComparison.OrdinalIgnoreCase))
                        {
                            return PermissionLevel.DomainAdmin;
                        }
                    }
                }

                // 否则看域根上是否有委派写权限（最常用的委派入口）
                if (HasWriteAce(domainDn, tokenSids))
                {
                    return PermissionLevel.Delegated;
                }

                return PermissionLevel.ReadOnly;
            }
            catch
            {
                return PermissionLevel.ReadOnly;
            }
        }

        /// <summary>判断账户对某个 DN 是否具有写权限（用于动态门禁）</summary>
        public static bool HasWritePermission(string dn, HashSet<string> tokenSids)
        {
            if (tokenSids == null) return false;
            try
            {
                return HasWriteAce(dn, tokenSids);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 取用户的 tokenGroups（AD 计算出的传递组 SID 集合，含主组）。
        /// 这是判定权限最可靠的方式，比遍历 memberOf 准确得多。
        /// </summary>
        public static HashSet<string> GetTokenGroupSids(string userDn)
        {
            HashSet<string> sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (DirectoryEntry entry = new DirectoryEntry("LDAP://" + userDn))
                {
                    // tokenGroups 是需要显式拉取的计算属性
                    entry.RefreshCache(new string[] { "tokenGroups" });

                    for (int i = 0; i < entry.Properties["tokenGroups"].Count; i++)
                    {
                        byte[] raw = entry.Properties["tokenGroups"][i] as byte[];
                        if (raw == null || raw.Length == 0) continue;
                        try
                        {
                            SecurityIdentifier sid = new SecurityIdentifier(raw, 0);
                            sids.Add(sid.Value);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            return sids;
        }

        /// <summary>读取指定对象的 DACL，看是否对账户所在组授予了写权限</summary>
        private static bool HasWriteAce(string dn, HashSet<string> tokenSids)
        {
            using (DirectoryEntry entry = new DirectoryEntry("LDAP://" + dn))
            {
                ActiveDirectorySecurity sec = entry.ObjectSecurity;
                AuthorizationRuleCollection rules =
                    sec.GetAccessRules(true, true, typeof(SecurityIdentifier));

                foreach (AuthorizationRule rule in rules)
                {
                    ActiveDirectoryAccessRule ace = rule as ActiveDirectoryAccessRule;
                    if (ace == null) continue;

                    SecurityIdentifier sid = ace.IdentityReference as SecurityIdentifier;
                    if (sid == null) continue;

                    if (!tokenSids.Contains(sid.Value)) continue;

                    ActiveDirectoryRights r = ace.ActiveDirectoryRights;
                    if ((r & (ActiveDirectoryRights.CreateChild |
                              ActiveDirectoryRights.DeleteChild |
                              ActiveDirectoryRights.WriteProperty |
                              ActiveDirectoryRights.WriteDacl |
                              ActiveDirectoryRights.WriteOwner |
                              ActiveDirectoryRights.GenericAll |
                              ActiveDirectoryRights.GenericWrite)) != 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>权限等级的中文描述</summary>
        public static string Describe(PermissionLevel level)
        {
            switch (level)
            {
                case PermissionLevel.DomainAdmin:
                    return "域管理员（可执行所有写操作）";
                case PermissionLevel.Delegated:
                    return "已委派（可执行写操作）";
                default:
                    return "只读（写操作已禁用）";
            }
        }

        public static string ShortName(PermissionLevel level)
        {
            switch (level)
            {
                case PermissionLevel.DomainAdmin: return "域管理员";
                case PermissionLevel.Delegated: return "可写（委派）";
                default: return "只读";
            }
        }
    }
}
