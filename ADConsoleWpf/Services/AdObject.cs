using System;
using System.Collections.Generic;

namespace ADConsole.Services
{
    /// <summary>对象类别</summary>
    public enum AdKind
    {
        Domain,
        OrganizationalUnit,
        Container,
        User,
        Group,
        Computer
    }

    /// <summary>权限级别：只读 / 已委派可写 / 域管理员</summary>
    public enum PermissionLevel
    {
        ReadOnly,
        Delegated,
        DomainAdmin
    }

    /// <summary>统一的 AD 对象模型（供列表与属性面板绑定）</summary>
    public class AdObject
    {
        public AdKind Kind { get; set; }
        public string DistinguishedName { get; set; }
        public string Name { get; set; }
        public string SamAccountName { get; set; }
        public string ObjectClass { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        public DateTime? WhenCreated { get; set; }
        public DateTime? WhenChanged { get; set; }

        public AdObject()
        {
            // 默认按"已启用"处理，避免读取不到 uAC 时显示成禁用
            Enabled = true;
        }

        // 用户
        public string DisplayName { get; set; }
        public string UserPrincipalName { get; set; }
        public string Email { get; set; }
        public string Department { get; set; }
        public string Title { get; set; }
        public string Phone { get; set; }
        public string Office { get; set; }
        public bool LockedOut { get; set; }
        public DateTime? PasswordLastSet { get; set; }
        public bool PasswordNeverExpires { get; set; }
        public bool PasswordExpired { get; set; }
        public bool ChangePasswordAtLogon { get; set; }
        public DateTime? LastBadPasswordAttempt { get; set; }

        // 组
        public string GroupCategory { get; set; }
        public string GroupScope { get; set; }

        // 计算机
        public string DnsHostName { get; set; }
        public string OperatingSystem { get; set; }

        /// <summary>显示名：优先 DisplayName（忽略纯空白），其次 Name</summary>
        public string DisplayNameOrName
        {
            get { return string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName; }
        }

        /// <summary>状态列文本</summary>
        public string StatusText
        {
            get
            {
                if (Kind == AdKind.User || Kind == AdKind.Computer)
                {
                    if (!Enabled) return Kind == AdKind.User ? "已禁用" : "已禁用";
                    if (Kind == AdKind.User && LockedOut) return "已锁定";
                    return Kind == AdKind.User ? "正常" : "已启用";
                }
                if (Kind == AdKind.Group) return GroupCategory;
                if (Kind == AdKind.OrganizationalUnit) return "OU";
                if (Kind == AdKind.Container) return "容器";
                if (Kind == AdKind.Domain) return "域";
                return string.Empty;
            }
        }

        /// <summary>最后登录（近似值，来自复制的 lastLogonTimestamp）</summary>
        public DateTime? LastLogonApprox { get; set; }

        /// <summary>最后登录（精确值，逐 DC 汇总的 lastLogon）</summary>
        public DateTime? LastLogonExact { get; set; }
        public string LastLogonServer { get; set; }

        /// <summary>附加说明（如登录历史里的降级提示）</summary>
        public string Note { get; set; }
    }

    /// <summary>密码到期提醒的一行数据</summary>
    public class PasswordExpiryRow
    {
        public string Name { get; set; }
        public string SamAccountName { get; set; }
        public DateTime? PasswordLastSet { get; set; }
        public DateTime? ExpireDate { get; set; }
        public int? DaysLeft { get; set; }
        public string Status { get; set; }
        public string OuDn { get; set; }
        public string OuDisplay { get; set; }
        public AdObject Source { get; set; }

        public string PasswordLastSetText
        {
            get { return PasswordLastSet.HasValue ? PasswordLastSet.Value.ToString("yyyy-MM-dd HH:mm") : "—"; }
        }
        public string ExpireDateText
        {
            get { return ExpireDate.HasValue ? ExpireDate.Value.ToString("yyyy-MM-dd HH:mm") : "—"; }
        }
        public string DaysLeftText
        {
            get
            {
                if (!DaysLeft.HasValue) return "—";
                return DaysLeft.Value < 0 ? ("已过期 " + Math.Abs(DaysLeft.Value) + " 天") : (DaysLeft.Value + " 天");
            }
        }
    }

    /// <summary>4624 登录明细</summary>
    public class LoginEventRow
    {
        public DateTime Time { get; set; }
        public string Account { get; set; }
        public string IpAddress { get; set; }
        public string LogonType { get; set; }
        public string DomainController { get; set; }

        public string TimeText { get { return Time.ToString("yyyy-MM-dd HH:mm:ss"); } }
    }

    /// <summary>CSV 导入的一行</summary>
    public class CsvUserRow
    {
        public int LineNo { get; set; }
        public string SamAccountName { get; set; }
        public string Name { get; set; }
        public string Password { get; set; }
        public string OuDn { get; set; }
        public string Department { get; set; }
        public string Title { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string Description { get; set; }

        /// <summary>校验错误，空表示可导入</summary>
        public string Error { get; set; }
        public bool IsValid { get { return string.IsNullOrEmpty(Error); } }
        public string ValidationText { get { return string.IsNullOrEmpty(Error) ? "通过" : Error; } }
    }

    /// <summary>导入执行结果</summary>
    public class CsvImportResult
    {
        public int LineNo { get; set; }
        public string SamAccountName { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>域信息对话框的一行</summary>
    public class InfoRow
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public bool IsSection { get; set; }
    }

    /// <summary>简单的列表行（概览/属性面板用）</summary>
    public class KeyValueRow
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    /// <summary>OU 树节点</summary>
    public class OuTreeNode
    {
        public string Title { get; set; }
        public string DistinguishedName { get; set; }
        public AdKind Kind { get; set; }
        public string Tip { get; set; }
        public List<OuTreeNode> Children { get; set; }

        public OuTreeNode()
        {
            Children = new List<OuTreeNode>();
        }

        public override string ToString() { return Title; }
    }

    /// <summary>AD 异常的友好描述</summary>
    public static class AdError
    {
        /// <summary>把 ADSI/COM 异常翻译成中文提示</summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "未知错误";

            int hr = 0;
            System.Runtime.InteropServices.COMException com =
                ex as System.Runtime.InteropServices.COMException;
            if (com != null)
            {
                hr = com.ErrorCode;
            }
            else
            {
                try { hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex); }
                catch { hr = 0; }
            }

            switch ((uint)hr)
            {
                case 0x80072023: return "拒绝访问：当前账户没有该对象的写入权限（需要域管理员或相应委派权限）。";
                case 0x80070005: return "拒绝访问：操作被安全策略阻止。";
                case 0x8007202F: return "违反密码策略：密码不符合域密码策略要求。";
                case 0x8007052D: return "违反密码策略：密码长度或复杂度不满足要求。";
                case 0x80072035: return "约束违规：填写的属性值不符合对象类要求（常见于必填属性缺失）。";
                case 0x8007200D: return "对象不存在：目标 DN 已失效或被删除。";
                case 0x8007200E: return "命名冲突：同名对象已存在于该容器中。";
                case 0x80072020: return "并发操作冲突：请稍后重试。";
                case 0x8007200A: return "无效的命名语法：DN 格式不正确。";
                case 0x80070035: return "找不到网络路径：无法联系域控。";
                case 0x8007054C: return "登录名不可用：该 sAMAccountName 已被占用或含非法字符。";
            }

            string msg = ex.Message;
            if (!string.IsNullOrEmpty(msg)) return msg;
            return ex.GetType().Name;
        }
    }
}
