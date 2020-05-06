#Requires -Version 5.1
<#
.SYNOPSIS
    AD 域控管理控制台（AD Console）
.DESCRIPTION
    基于 PowerShell + WinForms 的轻量级 Active Directory 管理界面。
    功能：OU 树浏览、用户/组/计算机管理、密码重置、账号启停与解锁、
          组成员维护、域信息与密码策略查看、结果导出 CSV。
.NOTES
    需要 RSAT ActiveDirectory 模块。
    只读操作用普通域账号即可；写操作（新建/修改/删除）需要相应委派权限。
.EXAMPLE
    .\ADConsole.ps1
.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File .\ADConsole.ps1
#>

# ----------------------------------------------------------------------- 初始化
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
    Start-Process powershell.exe -ArgumentList @(
        '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    )
    exit
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

try {
    Import-Module ActiveDirectory -ErrorAction Stop
}
catch {
    [void][System.Windows.Forms.MessageBox]::Show(
        "未能加载 ActiveDirectory 模块（需要 RSAT）。`r`n`r`n$($_.Exception.Message)",
        'AD Console', 'OK', 'Error')
    exit 1
}

# ----------------------------------------------------------------------- 脚本级状态
$script:Domain      = $null
$script:RootDN      = $null
$script:CurrentDN   = $null
$script:CurrentMode = 'Users'
$script:LastQuery   = ''
$script:OuMap       = @{}

$script:Frm = $null
$script:Tree = $null
$script:List = $null
$script:PropList = $null
$script:DetailTitle = $null
$script:StatusLbl = $null
$script:PositionLbl = $null
$script:SearchBox = $null
$script:ModeBox = $null

$script:ModeMap = @{
    '用户'   = 'Users'
    '组'     = 'Groups'
    '计算机' = 'Computers'
}

# ----------------------------------------------------------------------- 基础工具
function Set-Status {
    param([string]$Text)
    if ($script:StatusLbl) { $script:StatusLbl.Text = $Text }
}

function Set-Position {
    param([string]$Text)
    if ($script:PositionLbl) { $script:PositionLbl.Text = $Text }
}

function Enter-Busy {
    param([string]$Message)
    $script:Frm.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    Set-Status $Message
    [System.Windows.Forms.Application]::DoEvents()
}

function Exit-Busy {
    $script:Frm.Cursor = [System.Windows.Forms.Cursors]::Default
}

function Invoke-ADAction {
    param([string]$Title, [scriptblock]$Action)
    Enter-Busy $Title
    try {
        & $Action
        Set-Status "$Title —— 已完成"
        return $true
    }
    catch {
        [void][System.Windows.Forms.MessageBox]::Show(
            "$Title 失败：`r`n`r`n$($_.Exception.Message)", $Title, 'OK', 'Error')
        Set-Status "$Title —— 失败"
        return $false
    }
    finally {
        Exit-Busy
    }
}

function Confirm-Action {
    param([string]$Message, [string]$Title = '请确认')
    $r = [System.Windows.Forms.MessageBox]::Show(
        $script:Frm, $Message, $Title, 'YesNo', 'Warning')
    return ($r -eq [System.Windows.Forms.DialogResult]::Yes)
}

function Escape-Ldap {
    param([string]$Value)
    return ($Value -replace "'", "''")
}

function Get-SelectedItems {
    return @($script:List.SelectedItems)
}

function Get-SelectedIdentities {
    $ids = @()
    foreach ($it in $script:List.SelectedItems) {
        if ($it.Tag -and $it.Tag.DistinguishedName) { $ids += $it.Tag.DistinguishedName }
    }
    return $ids
}

function New-RandomPassword {
    param([int]$Length = 16)
    $sets = @(
        'ABCDEFGHJKLMNPQRSTUVWXYZ',
        'abcdefghijkmnpqrstuvwxyz',
        '23456789',
        '!@#$%&*?'
    )
    $all = ($sets -join '')
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    try {
        do {
            $bytes = New-Object byte[] $Length
            $rng.GetBytes($bytes)
            $chars = foreach ($b in $bytes) { $all[$b % $all.Length] }
            $pwd = -join $chars
            $ok = $true
            foreach ($s in $sets) {
                if ($pwd.IndexOfAny($s.ToCharArray()) -lt 0) { $ok = $false; break }
            }
        } while (-not $ok)
    }
    finally {
        $rng.Dispose()
    }
    return $pwd
}

# ----------------------------------------------------------------------- OU 树
function Build-OuTree {
    Enter-Busy '正在加载组织单位...'
    $script:Tree.BeginUpdate()
    try {
        $script:Tree.Nodes.Clear()
        $script:OuMap = @{}

        $rootNode = New-Object System.Windows.Forms.TreeNode $script:Domain.DNSRoot
        $rootNode.Tag = [pscustomobject]@{
            DN   = $script:RootDN
            Kind = 'Domain'
            Text = $script:Domain.DNSRoot
            Desc = "域根：$($script:Domain.DistinguishedName)"
        }
        [void]$script:Tree.Nodes.Add($rootNode)
        $script:OuMap[$script:RootDN] = $rootNode

        $items = @()
        $ous = Get-ADOrganizationalUnit -Filter * -SearchBase $script:RootDN `
            -Properties Description -ErrorAction Stop
        foreach ($ou in $ous) {
            $items += [pscustomobject]@{
                DN = $ou.DistinguishedName; Name = $ou.Name; Kind = 'OU'; Desc = $ou.Description
            }
        }
        $containers = Get-ADObject -Filter { ObjectClass -eq 'container' } `
            -SearchBase $script:RootDN -SearchScope OneLevel `
            -Properties Description -ErrorAction Stop
        foreach ($c in $containers) {
            $items += [pscustomobject]@{
                DN = $c.DistinguishedName; Name = $c.Name; Kind = 'Container'; Desc = $c.Description
            }
        }

        # 按 DN 深度排序，保证父节点先于子节点加入
        foreach ($it in ($items | Sort-Object { ($_.DN -split ',').Count })) {
            $parentDn = ($it.DN -split ',', 2)[1]
            $parent = if ($script:OuMap.ContainsKey($parentDn)) { $script:OuMap[$parentDn] } else { $rootNode }
            $node = New-Object System.Windows.Forms.TreeNode $it.Name
            $node.Tag = $it
            if ($it.Kind -eq 'Container') { $node.ForeColor = [System.Drawing.Color]::DimGray }
            [void]$parent.Nodes.Add($node)
            $script:OuMap[$it.DN] = $node
        }

        $rootNode.Expand()
        $script:Tree.SelectedNode = $rootNode
    }
    finally {
        $script:Tree.EndUpdate()
        Exit-Busy
    }
}

# ----------------------------------------------------------------------- 列表工具
function Reset-ListColumns {
    param($Columns)
    $script:List.BeginUpdate()
    $script:List.Columns.Clear()
    foreach ($c in $Columns) {
        [void]$script:List.Columns.Add($c.Text, [int]$c.Width)
    }
    $script:List.EndUpdate()
}

function Clear-List {
    $script:List.Items.Clear()
    $script:PropList.Items.Clear()
    if ($script:DetailTitle) { $script:DetailTitle.Text = '属性' }
}

function Show-StatusLine {
    param([int]$Count, [string]$Label)
    Set-Status "共 $Count 个$Label"
}

# ----------------------------------------------------------------------- 列表内容
function Show-Overview {
    Reset-ListColumns @(
        @{ Text = '项目'; Width = 240 },
        @{ Text = '值'; Width = 460 }
    )
    Enter-Busy '正在统计域对象...'
    try {
        $rows = New-Object System.Collections.ArrayList
        [void]$rows.Add(@('域（DNS）', $script:Domain.DNSRoot))
        [void]$rows.Add(@('域 DN', $script:Domain.DistinguishedName))
        [void]$rows.Add(@('NetBIOS 名称', $script:Domain.NetBIOSName))
        [void]$rows.Add(@('PDC 仿真器', $script:Domain.PDCEmulator))
        [void]$rows.Add(@('目录分区', ($script:Domain.Partitions -join '  |  ')))
        [void]$rows.Add(@('当前身份', ([Security.Principal.WindowsIdentity]::GetCurrent().Name)))
        [void]$rows.Add(@('机器状态', '已加入域'))

        $stat = @(
            @('组织单位 (OU)', { (Get-ADOrganizationalUnit -Filter * -SearchBase $script:RootDN | Measure-Object).Count }),
            @('用户对象',      { (Get-ADUser -Filter * -SearchBase $script:RootDN | Measure-Object).Count }),
            @('组对象',        { (Get-ADGroup -Filter * -SearchBase $script:RootDN | Measure-Object).Count }),
            @('计算机对象',    { (Get-ADComputer -Filter * -SearchBase $script:RootDN | Measure-Object).Count }),
            @('联系人',        { (Get-ADObject -LDAPFilter '(objectClass=contact)' -SearchBase $script:RootDN | Measure-Object).Count })
        )
        foreach ($s in $stat) {
            $v = & $s[1]
            [void]$rows.Add(@($s[0], $v))
        }

        $script:List.BeginUpdate()
        $script:List.Items.Clear()
        foreach ($r in $rows) {
            $it = New-Object System.Windows.Forms.ListViewItem $r[0]
            [void]$it.SubItems.Add([string]$r[1])
            $it.Tag = $null
            [void]$script:List.Items.Add($it)
        }
        $script:List.EndUpdate()
        Show-StatusLine $script:List.Items.Count '项'
    }
    catch {
        [void][System.Windows.Forms.MessageBox]::Show(
            "统计失败：`r`n`r`n$($_.Exception.Message)", '域信息', 'OK', 'Error')
        Clear-List
    }
    finally { Exit-Busy }
}

function Show-Users {
    param([string]$DN, [string]$Query, [switch]$Recursive)
    Reset-ListColumns @(
        @{ Text = '姓名';     Width = 150 },
        @{ Text = '登录名';   Width = 130 },
        @{ Text = '状态';     Width = 70  },
        @{ Text = '最后登录'; Width = 130 },
        @{ Text = '邮箱';     Width = 190 },
        @{ Text = '部门';     Width = 110 },
        @{ Text = '职位';     Width = 130 },
        @{ Text = '说明';     Width = 200 }
    )
    Enter-Busy '正在查询用户...'
    try {
        $scope = if ($Recursive) { 'Subtree' } else { 'OneLevel' }
        $q = Escape-Ldap $Query
        $filter = if ($Query) {
            "Name -like '*$q*' -or SamAccountName -like '*$q*' -or GivenName -like '*$q*' -or Surname -like '*$q*' -or Mail -like '*$q*'"
        } else { '*' }
        $props = @('DisplayName', 'Enabled', 'LastLogonDate', 'EmailAddress', 'Department',
                   'Title', 'Description', 'LockedOut', 'GivenName', 'Surname', 'whenCreated')
        $users = Get-ADUser -Filter $filter -SearchBase $DN -SearchScope $scope `
            -Properties $props -ErrorAction Stop |
            Sort-Object Name

        $script:List.BeginUpdate()
        $script:List.Items.Clear()
        foreach ($u in $users) {
            $name = if ($u.DisplayName) { $u.DisplayName } else { $u.Name }
            $it = New-Object System.Windows.Forms.ListViewItem $name
            [void]$it.SubItems.Add($u.SamAccountName)
            $state = if (-not $u.Enabled) { '已禁用' }
                     elseif ($u.LockedOut) { '已锁定' }
                     else { '正常' }
            $st = $it.SubItems.Add($state)
            if ($state -ne '正常') { $st.ForeColor = [System.Drawing.Color]::Firebrick }
            [void]$it.SubItems.Add($(if ($u.LastLogonDate) { $u.LastLogonDate.ToString('yyyy-MM-dd HH:mm') } else { '' }))
            [void]$it.SubItems.Add([string]$u.EmailAddress)
            [void]$it.SubItems.Add([string]$u.Department)
            [void]$it.SubItems.Add([string]$u.Title)
            [void]$it.SubItems.Add([string]$u.Description)
            $it.Tag = $u
            [void]$script:List.Items.Add($it)
        }
        $script:List.EndUpdate()
        Show-StatusLine $script:List.Items.Count '个用户'
    }
    catch {
        [void][System.Windows.Forms.MessageBox]::Show(
            "查询用户失败：`r`n`r`n$($_.Exception.Message)", '查询', 'OK', 'Error')
        Clear-List
    }
    finally { Exit-Busy }
}

function Show-Groups {
    param([string]$DN, [string]$Query, [switch]$Recursive)
    Reset-ListColumns @(
        @{ Text = '名称';     Width = 200 },
        @{ Text = '登录名';   Width = 160 },
        @{ Text = '类别';     Width = 90  },
        @{ Text = '范围';     Width = 90  },
        @{ Text = '说明';     Width = 260 },
        @{ Text = '创建时间'; Width = 130 }
    )
    Enter-Busy '正在查询组...'
    try {
        $scope = if ($Recursive) { 'Subtree' } else { 'OneLevel' }
        $q = Escape-Ldap $Query
        $filter = if ($Query) { "Name -like '*$q*' -or SamAccountName -like '*$q*'" } else { '*' }
        $groups = Get-ADGroup -Filter $filter -SearchBase $DN -SearchScope $scope `
            -Properties Description, whenCreated -ErrorAction Stop |
            Sort-Object Name

        $script:List.BeginUpdate()
        $script:List.Items.Clear()
        foreach ($g in $groups) {
            $it = New-Object System.Windows.Forms.ListViewItem $g.Name
            [void]$it.SubItems.Add($g.SamAccountName)
            [void]$it.SubItems.Add([string]$g.GroupCategory)
            [void]$it.SubItems.Add([string]$g.GroupScope)
            [void]$it.SubItems.Add([string]$g.Description)
            [void]$it.SubItems.Add($(if ($g.whenCreated) { $g.whenCreated.ToString('yyyy-MM-dd HH:mm') } else { '' }))
            $it.Tag = $g
            [void]$script:List.Items.Add($it)
        }
        $script:List.EndUpdate()
        Show-StatusLine $script:List.Items.Count '个组'
    }
    catch {
        [void][System.Windows.Forms.MessageBox]::Show(
            "查询组失败：`r`n`r`n$($_.Exception.Message)", '查询', 'OK', 'Error')
        Clear-List
    }
    finally { Exit-Busy }
}

function Show-Computers {
    param([string]$DN, [string]$Query, [switch]$Recursive)
    Reset-ListColumns @(
        @{ Text = '名称';       Width = 180 },
        @{ Text = '状态';       Width = 80  },
        @{ Text = '最后登录';   Width = 130 },
        @{ Text = '操作系统';   Width = 220 },
        @{ Text = 'DNS 名称';   Width = 200 },
        @{ Text = '说明';       Width = 200 }
    )
    Enter-Busy '正在查询计算机...'
    try {
        $scope = if ($Recursive) { 'Subtree' } else { 'OneLevel' }
        $q = Escape-Ldap $Query
        $filter = if ($Query) { "Name -like '*$q*' -or DNSHostName -like '*$q*'" } else { '*' }
        $computers = Get-ADComputer -Filter $filter -SearchBase $DN -SearchScope $scope `
            -Properties Description, LastLogonDate, OperatingSystem, DNSHostName, whenCreated `
            -ErrorAction Stop |
            Sort-Object Name

        $script:List.BeginUpdate()
        $script:List.Items.Clear()
        foreach ($c in $computers) {
            $it = New-Object System.Windows.Forms.ListViewItem $c.Name
            $st = $it.SubItems.Add($(if ($c.Enabled) { '已启用' } else { '已禁用' }))
            if (-not $c.Enabled) { $st.ForeColor = [System.Drawing.Color]::Firebrick }
            [void]$it.SubItems.Add($(if ($c.LastLogonDate) { $c.LastLogonDate.ToString('yyyy-MM-dd HH:mm') } else { '' }))
            [void]$it.SubItems.Add([string]$c.OperatingSystem)
            [void]$it.SubItems.Add([string]$c.DNSHostName)
            [void]$it.SubItems.Add([string]$c.Description)
            $it.Tag = $c
            [void]$script:List.Items.Add($it)
        }
        $script:List.EndUpdate()
        Show-StatusLine $script:List.Items.Count '台计算机'
    }
    catch {
        [void][System.Windows.Forms.MessageBox]::Show(
            "查询计算机失败：`r`n`r`n$($_.Exception.Message)", '查询', 'OK', 'Error')
        Clear-List
    }
    finally { Exit-Busy }
}

function Update-List {
    param([string]$DN, [string]$Mode, [string]$Query)
    $script:CurrentDN = $DN
    $script:CurrentMode = $Mode
    $script:LastQuery = $Query
    Clear-List

    # 有搜索词时递归搜索子树；
    # 选中域根时也递归——因为 AD 中用户/计算机位于 CN=Users、CN=Computers 等容器内，
    # 并非域根的直接子对象，若按 OneLevel 查询首屏会是空列表。
    $recursive = ($Query -ne '') -or ($DN -eq $script:RootDN)
    switch ($Mode) {
        'Users'     { Show-Users     -DN $DN -Query $Query -Recursive:$recursive }
        'Groups'    { Show-Groups    -DN $DN -Query $Query -Recursive:$recursive }
        'Computers' { Show-Computers -DN $DN -Query $Query -Recursive:$recursive }
        default     { Show-Overview }
    }
}

function Refresh-Current {
    Update-List -DN $script:CurrentDN -Mode $script:CurrentMode -Query $script:LastQuery
}

# ----------------------------------------------------------------------- 属性面板
function Show-Properties {
    param($Obj)
    $script:PropList.Items.Clear()
    if ($script:DetailTitle) { $script:DetailTitle.Text = '属性' }
    if (-not $Obj) { return }

    $rows = New-Object System.Collections.ArrayList
    function Add-Row($K, $V) {
        if ($null -ne $V -and "$V" -ne '') { [void]$rows.Add(@($K, "$V")) }
    }

    $class = [string]$Obj.ObjectClass
    Add-Row '对象类别' $class
    Add-Row '名称' $Obj.Name
    Add-Row '登录名' $Obj.SamAccountName
    Add-Row 'DN' $Obj.DistinguishedName

    if ($class -eq 'user') {
        Add-Row '显示名' $Obj.DisplayName
        Add-Row 'UPN' $Obj.UserPrincipalName
        Add-Row '启用' $(if ($Obj.Enabled) { '是' } else { '否' })
        Add-Row '锁定' $(if ($Obj.LockedOut) { '是' } else { '否' })
        Add-Row '部门' $Obj.Department
        Add-Row '职位' $Obj.Title
        Add-Row '电话' $Obj.TelephoneNumber
        Add-Row '邮箱' $Obj.EmailAddress
        Add-Row '办公室' $Obj.Office
        Add-Row '描述' $Obj.Description
        Add-Row '最后登录' $(if ($Obj.LastLogonDate) { $Obj.LastLogonDate } else { '' })
        Add-Row '最近失败登录' $Obj.LastBadPasswordAttempt
        Add-Row '密码最后设置' $Obj.PasswordLastSet
        Add-Row '密码永不过期' $(if ($Obj.PasswordNeverExpires) { '是' } else { '否' })
        Add-Row '密码已过期' $(if ($Obj.PasswordExpired) { '是' } else { '否' })
        Add-Row '必须改密' $(if ($Obj.ChangePasswordAtLogon) { '是' } else { '否' })
        Add-Row '创建时间' $Obj.whenCreated
        Add-Row '修改时间' $Obj.whenChanged
    }
    elseif ($class -eq 'group') {
        Add-Row '类别' $Obj.GroupCategory
        Add-Row '范围' $Obj.GroupScope
        Add-Row '描述' $Obj.Description
        Add-Row '创建时间' $Obj.whenCreated
        Add-Row '修改时间' $Obj.whenChanged
        try {
            $m = Get-ADGroupMember -Identity $Obj.DistinguishedName -Recursive -ErrorAction Stop
            Add-Row '成员数（含嵌套）' (@($m).Count)
        }
        catch { Add-Row '成员数（含嵌套）' "读取失败：$($_.Exception.Message)" }
    }
    elseif ($class -eq 'computer') {
        Add-Row 'DNS 名称' $Obj.DNSHostName
        Add-Row '操作系统' $Obj.OperatingSystem
        Add-Row 'OS 版本' $Obj.OperatingSystemVersion
        Add-Row '启用' $(if ($Obj.Enabled) { '是' } else { '否' })
        Add-Row '最后登录' $(if ($Obj.LastLogonDate) { $Obj.LastLogonDate } else { '' })
        Add-Row '描述' $Obj.Description
        Add-Row '创建时间' $Obj.whenCreated
    }
    elseif ($class -eq 'organizationalUnit' -or $class -eq 'container') {
        Add-Row '描述' $Obj.Description
        Add-Row '创建时间' $Obj.whenCreated
        Add-Row '修改时间' $Obj.whenChanged
    }
    else {
        Add-Row '描述' $Obj.Description
        Add-Row '创建时间' $Obj.whenCreated
    }

    $script:PropList.BeginUpdate()
    foreach ($r in $rows) {
        $it = New-Object System.Windows.Forms.ListViewItem $r[0]
        [void]$it.SubItems.Add($r[1])
        [void]$script:PropList.Items.Add($it)
    }
    $script:PropList.EndUpdate()
    if ($script:DetailTitle) {
        $script:DetailTitle.Text = "属性 - $($Obj.Name)"
    }
}

# ----------------------------------------------------------------------- 账号操作
function Invoke-ToggleEnabled {
    param([bool]$Enable)
    $ids = Get-SelectedIdentities
    if (-not $ids) { return }
    $verb = if ($Enable) { '启用' } else { '禁用' }
    if (-not (Confirm-Action "确定要${verb}选中的 $($ids.Count) 个账号吗？")) { return }
    if (Invoke-ADAction "${verb}账号" {
        foreach ($id in $ids) {
            if ($Enable) { Enable-ADAccount -Identity $id -ErrorAction Stop }
            else { Disable-ADAccount -Identity $id -ErrorAction Stop }
        }
    }) { Refresh-Current }
}

function Invoke-Unlock {
    $ids = Get-SelectedIdentities
    if (-not $ids) { return }
    if (-not (Confirm-Action "确定要解锁选中的 $($ids.Count) 个账号吗？")) { return }
    if (Invoke-ADAction '解锁账号' {
        foreach ($id in $ids) { Unlock-ADAccount -Identity $id -ErrorAction Stop }
    }) { Refresh-Current }
}

function Invoke-Delete {
    param([string]$Kind)
    $ids = Get-SelectedIdentities
    if (-not $ids) { return }
    $list = ($ids | Select-Object -First 8) -join "`r`n"
    if ($ids.Count -gt 8) { $list += "`r`n... 共 $($ids.Count) 项" }
    if (-not (Confirm-Action "永久删除以下$Kind（不可恢复）：`r`n`r`n$list")) { return }

    if (Invoke-ADAction "删除$Kind" {
        foreach ($id in $ids) {
            switch ($Kind) {
                '用户'   { Remove-ADUser -Identity $id -Confirm:$false -ErrorAction Stop }
                '组'     { Remove-ADGroup -Identity $id -Confirm:$false -ErrorAction Stop }
                '计算机' { Remove-ADComputer -Identity $id -Confirm:$false -ErrorAction Stop }
            }
        }
    }) { Refresh-Current }
}

# ----------------------------------------------------------------------- 组操作
function Invoke-AddGroupMember {
    param($Group)
    if (-not $Group) { return }
    $picked = Show-UserPickerDialog -Title "为组「$($Group.Name)」选择成员"
    if (-not $picked) { return }
    $dn = $picked.DistinguishedName
    if (Invoke-ADAction '添加组成员' {
        Add-ADGroupMember -Identity $Group.DistinguishedName -Members $dn -ErrorAction Stop
    }) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, "已添加成员：$($picked.Name)", '添加成员', 'OK', 'Information')
        Refresh-Current
    }
}

function Invoke-RemoveGroupMember {
    param($Group)
    if (-not $Group) { return }
    Enter-Busy '读取组成员...'
    try {
        $members = @(Get-ADGroupMember -Identity $Group.DistinguishedName -ErrorAction Stop)
    }
    catch {
        Exit-Busy
        [void][System.Windows.Forms.MessageBox]::Show(
            "读取成员失败：`r`n`r`n$($_.Exception.Message)", '移除成员', 'OK', 'Error')
        return
    }
    Exit-Busy
    if (-not $members) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, "组「$($Group.Name)」没有直接成员。", '移除成员', 'OK', 'Information')
        return
    }

    $picked = Show-MemberPickerDialog -Title "从「$($Group.Name)」移除成员" -Members $members
    if (-not $picked) { return }
    if (Invoke-ADAction '移除组成员' {
        foreach ($m in $picked) { Remove-ADGroupMember -Identity $Group.DistinguishedName -Members $m -Confirm:$false -ErrorAction Stop }
    }) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, "已移除 $($picked.Count) 个成员。", '移除成员', 'OK', 'Information')
        Refresh-Current
    }
}

function Show-MembersDialog {
    param($Group)
    if (-not $Group) { return }
    $ctx = New-Dialog -Title "组成员：$($Group.Name)" -Width 640 -Height 480
    $lv = New-Object System.Windows.Forms.ListView
    $lv.View = 'Details'
    $lv.FullRowSelect = $true
    $lv.GridLines = $true
    $lv.Dock = 'Fill'
    [void]$lv.Columns.Add('名称', 200)
    [void]$lv.Columns.Add('登录名', 150)
    [void]$lv.Columns.Add('类别', 120)
    [void]$lv.Columns.Add('DN', 320)
    $ctx.Table.RowCount++
    $r = $ctx.Table.RowCount - 1
    $ctx.Table.Controls.Add($lv, 0, $r)
    $ctx.Table.SetColumnSpan($lv, 2)
    $ctx.Table.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Percent, 100))) | Out-Null

    Enter-Busy '读取组成员...'
    try {
        $members = @(Get-ADGroupMember -Identity $Group.DistinguishedName -Recursive -ErrorAction Stop)
    }
    catch {
        Exit-Busy
        [void][System.Windows.Forms.MessageBox]::Show(
            "读取成员失败：`r`n`r`n$($_.Exception.Message)", '组成员', 'OK', 'Error')
        return
    }
    Exit-Busy

    foreach ($m in $members) {
        $it = New-Object System.Windows.Forms.ListViewItem $m.Name
        [void]$it.SubItems.Add([string]$m.SamAccountName)
        [void]$it.SubItems.Add([string]$m.ObjectClass)
        [void]$it.SubItems.Add($m.DistinguishedName)
        $it.Tag = $m
        [void]$lv.Items.Add($it)
    }

    $btns = Add-DialogButtons -Ctx $ctx -OkText '关闭' -CancelText '导出 CSV'
    $btns.Cancel.DialogResult = 'None'
    $btns.Cancel.Text = '导出 CSV'
    $btns.Cancel.Add_Click({
        $objs = foreach ($it in $lv.Items) {
            [pscustomobject]@{ Name = $it.Text; Sam = $it.SubItems[1].Text; Class = $it.SubItems[2].Text; DN = $it.SubItems[3].Text }
        }
        if (-not $objs) { return }
        $d = New-Object System.Windows.Forms.SaveFileDialog
        $d.Filter = 'CSV 文件 (*.csv)|*.csv'
        $d.FileName = "Group_$($Group.Name)_Members.csv"
        if ($d.ShowDialog($script:Frm) -eq 'OK') {
            $objs | Export-Csv -Path $d.FileName -NoTypeInformation -Encoding UTF8
        }
    })
    [void]$ctx.Form.ShowDialog($script:Frm)
}

# ----------------------------------------------------------------------- 对话框框架
function New-Dialog {
    param([string]$Title, [int]$Width = 480, [int]$Height = 440)
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = $Title
    $dlg.StartPosition = 'CenterParent'
    $dlg.FormBorderStyle = 'FixedDialog'
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    $dlg.ShowInTaskbar = $false
    $dlg.ClientSize = New-Object System.Drawing.Size($Width, $Height)
    $dlg.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

    $table = New-Object System.Windows.Forms.TableLayoutPanel
    $table.Dock = 'Fill'
    $table.ColumnCount = 2
    [void]$table.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle(
        [System.Windows.Forms.SizeType]::Absolute, 140)))
    [void]$table.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle(
        [System.Windows.Forms.SizeType]::Percent, 100)))
    $table.Padding = New-Object System.Windows.Forms.Padding(12)
    [void]$dlg.Controls.Add($table)

    return @{ Form = $dlg; Table = $table }
}

function Add-DialogRow {
    param($Ctx, [string]$Label, [switch]$Password, [switch]$MultiLine, [string]$Text = '')
    $Ctx.Table.RowCount++
    $r = $Ctx.Table.RowCount - 1
    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = $Label
    $lbl.TextAlign = 'MiddleLeft'
    $lbl.Dock = 'Fill'
    [void]$Ctx.Table.Controls.Add($lbl, 0, $r)

    if ($Password) {
        $ctl = New-Object System.Windows.Forms.TextBox
        $ctl.UseSystemPasswordChar = $true
    }
    elseif ($MultiLine) {
        $ctl = New-Object System.Windows.Forms.TextBox
        $ctl.Multiline = $true
        $ctl.Height = 64
        $ctl.ScrollBars = 'Vertical'
        $ctl.Height = 64
    }
    else {
        $ctl = New-Object System.Windows.Forms.TextBox
    }
    $ctl.Text = $Text
    $ctl.Dock = 'Fill'
    [void]$Ctx.Table.Controls.Add($ctl, 1, $r)
    return $ctl
}

function Add-DialogCheckbox {
    param($Ctx, [string]$Label, [bool]$Checked, [switch]$Indent)
    $Ctx.Table.RowCount++
    $r = $Ctx.Table.RowCount - 1
    $cb = New-Object System.Windows.Forms.CheckBox
    $cb.Text = $Label
    $cb.Checked = $Checked
    $cb.AutoSize = $true
    $cb.Margin = New-Object System.Windows.Forms.Padding(4, 6, 4, 2)
    $col = 0
    $span = 2
    if ($Indent) { $col = 1; $span = 1 }
    [void]$Ctx.Table.Controls.Add($cb, $col, $r)
    if ($span -gt 1) { $Ctx.Table.SetColumnSpan($cb, $span) }
    return $cb
}

function Add-DialogCombo {
    param($Ctx, [string]$Label, [string[]]$Items, [string]$Selected = '')
    $Ctx.Table.RowCount++
    $r = $Ctx.Table.RowCount - 1
    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = $Label
    $lbl.TextAlign = 'MiddleLeft'
    $lbl.Dock = 'Fill'
    [void]$Ctx.Table.Controls.Add($lbl, 0, $r)

    $cb = New-Object System.Windows.Forms.ComboBox
    $cb.DropDownStyle = 'DropDownList'
    foreach ($i in $Items) { [void]$cb.Items.Add($i) }
    if ($Selected -and $cb.Items.Contains($Selected)) { $cb.SelectedItem = $Selected }
    elseif ($cb.Items.Count -gt 0) { $cb.SelectedIndex = 0 }
    $cb.Dock = 'Fill'
    [void]$Ctx.Table.Controls.Add($cb, 1, $r)
    return $cb
}

function Add-DialogButtons {
    param($Ctx, [string]$OkText = '确定', [string]$CancelText = '取消')
    $Ctx.Table.RowCount++
    $r = $Ctx.Table.RowCount - 1

    $flow = New-Object System.Windows.Forms.FlowLayoutPanel
    $flow.FlowDirection = 'RightToLeft'
    $flow.Dock = 'Fill'
    $flow.Height = 40

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = $OkText
    $ok.Width = 92
    $ok.Height = 30
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = $CancelText
    $cancel.Width = 92
    $cancel.Height = 30
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

    [void]$flow.Controls.Add($ok)
    [void]$flow.Controls.Add($cancel)
    [void]$Ctx.Table.Controls.Add($flow, 0, $r)
    $Ctx.Table.SetColumnSpan($flow, 2)
    return @{ OK = $ok; Cancel = $cancel }
}

# ----------------------------------------------------------------------- 新建用户
function Show-NewUserDialog {
    param([string]$Path)
    $ouName = if ($script:OuMap.ContainsKey($Path)) { $script:OuMap[$Path].Text } else { $Path }
    $ctx = New-Dialog -Title "新建用户 - $ouName" -Width 520 -Height 560
    $t = $ctx.Table

    $nameBox   = Add-DialogRow -Ctx $ctx -Label '姓名 *'
    $samBox    = Add-DialogRow -Ctx $ctx -Label '登录名 *'
    $upnBox    = Add-DialogRow -Ctx $ctx -Label 'UPN'
    $pwdRow = $t.RowCount; $t.RowCount++
    $pwdLbl = New-Object System.Windows.Forms.Label
    $pwdLbl.Text = '初始密码 *'
    $pwdLbl.TextAlign = 'MiddleLeft'
    $pwdLbl.Dock = 'Fill'
    [void]$t.Controls.Add($pwdLbl, 0, $pwdRow)
    $pwdFlow = New-Object System.Windows.Forms.FlowLayoutPanel
    $pwdFlow.Dock = 'Fill'
    $pwdFlow.Height = 30
    $pwdBox = New-Object System.Windows.Forms.TextBox
    $pwdBox.UseSystemPasswordChar = $true
    $pwdBox.Width = 300
    $genBtn = New-Object System.Windows.Forms.Button
    $genBtn.Text = '随机生成'
    $genBtn.Width = 92
    $genBtn.Height = 26
    $genBtn.Add_Click({ $pwdBox.Text = New-RandomPassword })
    [void]$pwdFlow.Controls.Add($pwdBox)
    [void]$pwdFlow.Controls.Add($genBtn)
    [void]$t.Controls.Add($pwdFlow, 1, $pwdRow)

    $deptBox = Add-DialogRow -Ctx $ctx -Label '部门'
    $titleBox = Add-DialogRow -Ctx $ctx -Label '职位'
    $mailBox = Add-DialogRow -Ctx $ctx -Label '邮箱'
    $telBox  = Add-DialogRow -Ctx $ctx -Label '电话'
    $descBox = Add-DialogRow -Ctx $ctx -Label '描述'

    $enableBox  = Add-DialogCheckbox -Ctx $ctx -Label '启用账号' -Checked $true
    $changeBox  = Add-DialogCheckbox -Ctx $ctx -Label '首次登录必须修改密码' -Checked $true
    $neverBox   = Add-DialogCheckbox -Ctx $ctx -Label '密码永不过期（与上一项互斥）' -Checked $false

    $btns = Add-DialogButtons -Ctx $ctx
    $ctx.Form.AcceptButton = $btns.OK
    $ctx.Form.CancelButton = $btns.Cancel
    $ctx.Form.ActiveControl = $nameBox

    if ($ctx.Form.ShowDialog($script:Frm) -ne [System.Windows.Forms.DialogResult]::OK) { return }

    $name = $nameBox.Text.Trim()
    $sam  = $samBox.Text.Trim()
    $pwd  = $pwdBox.Text
    if (-not $name -or -not $sam -or -not $pwd) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $ctx.Form, '姓名、登录名、初始密码均为必填项。', '新建用户', 'OK', 'Warning')
        return
    }
    if ($sam.Length -gt 20) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $ctx.Form, '登录名不能超过 20 个字符。', '新建用户', 'OK', 'Warning')
        return
    }
    if ($neverBox.Checked) { $changeBox.Checked = $false }

    $upn = if ($upnBox.Text.Trim()) { $upnBox.Text.Trim() } else { "$sam@$($script:Domain.DNSRoot)" }
    $secure = ConvertTo-SecureString $pwd -AsPlainText -Force

    $ok = Invoke-ADAction '新建用户' {
        $params = @{
            Name = $name; SamAccountName = $sam; UserPrincipalName = $upn
            Path = $Path; AccountPassword = $secure
            Enabled = $enableBox.Checked
            ChangePasswordAtLogon = $changeBox.Checked
            PasswordNeverExpires = $neverBox.Checked
        }
        foreach ($pair in @(
            @('Department', $deptBox.Text), @('Title', $titleBox.Text),
            @('EmailAddress', $mailBox.Text), @('TelephoneNumber', $telBox.Text),
            @('Description', $descBox.Text)
        )) {
            if ($pair[1].Trim()) { $params[$pair[0]] = $pair[1].Trim() }
        }
        New-ADUser @params -ErrorAction Stop
    }
    if ($ok) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm,
            "用户已创建：`r`n`r`n登录名：$sam`r`nUPN：$upn`r`n位置：$Path",
            '新建用户', 'OK', 'Information')
        Refresh-Current
    }
}

# ----------------------------------------------------------------------- 重置密码
function Show-ResetPasswordDialog {
    param($Obj)
    if (-not $Obj) { return }
    $ctx = New-Dialog -Title "重置密码 - $($Obj.Name)" -Width 480 -Height 320
    $t = $ctx.Table

    $row1 = $t.RowCount; $t.RowCount++
    $l1 = New-Object System.Windows.Forms.Label
    $l1.Text = '新密码 *'; $l1.TextAlign = 'MiddleLeft'; $l1.Dock = 'Fill'
    [void]$t.Controls.Add($l1, 0, $row1)
    $f1 = New-Object System.Windows.Forms.FlowLayoutPanel
    $f1.Dock = 'Fill'; $f1.Height = 30
    $pwd1 = New-Object System.Windows.Forms.TextBox
    $pwd1.UseSystemPasswordChar = $true; $pwd1.Width = 300
    $gen = New-Object System.Windows.Forms.Button
    $gen.Text = '随机生成'; $gen.Width = 92; $gen.Height = 26
    $gen.Add_Click({ $pwd1.Text = New-RandomPassword })
    [void]$f1.Controls.Add($pwd1); [void]$f1.Controls.Add($gen)
    [void]$t.Controls.Add($f1, 1, $row1)

    $pwd2 = Add-DialogRow -Ctx $ctx -Label '确认密码 *' -Password
    $changeBox = Add-DialogCheckbox -Ctx $ctx -Label '用户下次登录必须修改密码' -Checked $true
    $neverBox  = Add-DialogCheckbox -Ctx $ctx -Label '密码永不过期' -Checked $false

    $btns = Add-DialogButtons -Ctx $ctx
    $ctx.Form.AcceptButton = $btns.OK
    $ctx.Form.CancelButton = $btns.Cancel

    if ($ctx.Form.ShowDialog($script:Frm) -ne [System.Windows.Forms.DialogResult]::OK) { return }

    if ($pwd1.Text.Length -lt 8) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $ctx.Form, '密码长度至少 8 位。', '重置密码', 'OK', 'Warning')
        return
    }
    if ($pwd1.Text -cne $pwd2.Text) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $ctx.Form, '两次输入的密码不一致。', '重置密码', 'OK', 'Warning')
        return
    }
    if ($neverBox.Checked) { $changeBox.Checked = $false }

    $secure = ConvertTo-SecureString $pwd1.Text -AsPlainText -Force
    $id = $Obj.DistinguishedName

    if (Invoke-ADAction '重置密码' {
        Set-ADAccountPassword -Identity $id -Reset -NewPassword $secure -ErrorAction Stop
        Set-ADUser -Identity $id `
            -ChangePasswordAtLogon $changeBox.Checked `
            -PasswordNeverExpires $neverBox.Checked `
            -ErrorAction Stop
    }) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, "「$($Obj.Name)」的密码已重置。", '重置密码', 'OK', 'Information')
        Refresh-Current
    }
}

# ----------------------------------------------------------------------- 新建组
function Show-NewGroupDialog {
    param([string]$Path)
    $ouName = if ($script:OuMap.ContainsKey($Path)) { $script:OuMap[$Path].Text } else { $Path }
    $ctx = New-Dialog -Title "新建组 - $ouName" -Width 500 -Height 360

    $nameBox = Add-DialogRow -Ctx $ctx -Label '组名 *'
    $samBox  = Add-DialogRow -Ctx $ctx -Label '登录名 *'
    $descBox = Add-DialogRow -Ctx $ctx -Label '描述'
    $catBox  = Add-DialogCombo -Ctx $ctx -Label '类别' -Items @('安全组', '通讯组') -Selected '安全组'
    $scopeBox = Add-DialogCombo -Ctx $ctx -Label '范围' -Items @('全局', '域本地', '通用') -Selected '全局'
    $pathBox = Add-DialogRow -Ctx $ctx -Label '所在 OU' -Text $Path

    $btns = Add-DialogButtons -Ctx $ctx
    $ctx.Form.AcceptButton = $btns.OK
    $ctx.Form.CancelButton = $btns.Cancel
    $ctx.Form.ActiveControl = $nameBox

    if ($ctx.Form.ShowDialog($script:Frm) -ne [System.Windows.Forms.DialogResult]::OK) { return }

    $name = $nameBox.Text.Trim()
    $sam  = $samBox.Text.Trim()
    if (-not $name -or -not $sam) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $ctx.Form, '组名和登录名均为必填项。', '新建组', 'OK', 'Warning')
        return
    }

    $category = if ($catBox.SelectedItem -eq '通讯组') { 'Distribution' } else { 'Security' }
    $scope = switch ($scopeBox.SelectedItem) {
        '域本地' { 'DomainLocal' }
        '通用'   { 'Universal' }
        default  { 'Global' }
    }
    $target = $pathBox.Text.Trim()
    if (-not $target) { $target = $Path }

    if (Invoke-ADAction '新建组' {
        New-ADGroup -Name $name -SamAccountName $sam `
            -GroupCategory $category -GroupScope $scope `
            -Path $target `
            -Description $descBox.Text.Trim() `
            -ErrorAction Stop
    }) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, "组已创建：$name", '新建组', 'OK', 'Information')
        Refresh-Current
    }
}

# ----------------------------------------------------------------------- 选择器
function Show-UserPickerDialog {
    param([string]$Title = '选择用户')
    $script:PickerResult = $null

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = $Title
    $dlg.StartPosition = 'CenterParent'
    $dlg.Size = New-Object System.Drawing.Size(760, 520)
    $dlg.MinimumSize = New-Object System.Drawing.Size(600, 380)
    $dlg.ShowInTaskbar = $false
    $dlg.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

    $top = New-Object System.Windows.Forms.Panel
    $top.Dock = 'Top'
    $top.Height = 34
    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = '搜索：'
    $lbl.Location = New-Object System.Drawing.Point(12, 9)
    $lbl.AutoSize = $true
    $search = New-Object System.Windows.Forms.TextBox
    $search.Location = New-Object System.Drawing.Point(66, 6)
    $search.Size = New-Object System.Drawing.Size(540, 24)
    $btnSearch = New-Object System.Windows.Forms.Button
    $btnSearch.Text = '搜索'
    $btnSearch.Location = New-Object System.Drawing.Point(614, 4)
    $btnSearch.Size = New-Object System.Drawing.Size(80, 26)
    [void]$top.Controls.Add($lbl)
    [void]$top.Controls.Add($search)
    [void]$top.Controls.Add($btnSearch)

    $lv = New-Object System.Windows.Forms.ListView
    $lv.View = 'Details'
    $lv.FullRowSelect = $true
    $lv.GridLines = $true
    $lv.MultiSelect = $false
    $lv.Dock = 'Fill'
    [void]$lv.Columns.Add('姓名', 160)
    [void]$lv.Columns.Add('登录名', 140)
    [void]$lv.Columns.Add('邮箱', 200)
    [void]$lv.Columns.Add('部门', 120)
    [void]$lv.Columns.Add('DN', 320)

    $bottom = New-Object System.Windows.Forms.Panel
    $bottom.Dock = 'Bottom'
    $bottom.Height = 46
    $okBtn = New-Object System.Windows.Forms.Button
    $okBtn.Text = '确定'
    $okBtn.Size = New-Object System.Drawing.Size(92, 30)
    $okBtn.Location = New-Object System.Drawing.Point(548, 8)
    $okBtn.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text = '取消'
    $cancelBtn.Size = New-Object System.Drawing.Size(92, 30)
    $cancelBtn.Location = New-Object System.Drawing.Point(648, 8)
    $cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    [void]$bottom.Controls.Add($okBtn)
    [void]$bottom.Controls.Add($cancelBtn)

    [void]$dlg.Controls.Add($lv)
    [void]$dlg.Controls.Add($bottom)
    [void]$dlg.Controls.Add($top)
    $dlg.AcceptButton = $okBtn
    $dlg.CancelButton = $cancelBtn

    $doSearch = {
        $q = $search.Text.Trim()
        $lv.Items.Clear()
        Enter-Busy '搜索用户...'
        try {
            $filter = if ($q) {
                $e = Escape-Ldap $q
                "Name -like '*$e*' -or SamAccountName -like '*$e*' -or Mail -like '*$e*'"
            } else { '*' }
            $found = Get-ADUser -Filter $filter -SearchBase $script:RootDN -SearchScope Subtree `
                -Properties DisplayName, EmailAddress, Department -ErrorAction Stop |
                Sort-Object Name | Select-Object -First 500
            foreach ($u in $found) {
                $it = New-Object System.Windows.Forms.ListViewItem `
                    $(if ($u.DisplayName) { $u.DisplayName } else { $u.Name })
                [void]$it.SubItems.Add($u.SamAccountName)
                [void]$it.SubItems.Add([string]$u.EmailAddress)
                [void]$it.SubItems.Add([string]$u.Department)
                [void]$it.SubItems.Add($u.DistinguishedName)
                $it.Tag = $u
                [void]$lv.Items.Add($it)
            }
            Set-Status "找到 $($lv.Items.Count) 个用户"
        }
        catch {
            [void][System.Windows.Forms.MessageBox]::Show(
                "搜索失败：`r`n`r`n$($_.Exception.Message)", '搜索', 'OK', 'Error')
        }
        finally { Exit-Busy }
    }

    $btnSearch.Add_Click($doSearch)
    $search.Add_KeyDown({
        if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) { & $doSearch; $_.Handled = $true }
    })
    $lv.Add_DoubleClick({
        if ($lv.SelectedItems.Count -gt 0) {
            $script:PickerResult = $lv.SelectedItems[0].Tag
            $dlg.DialogResult = [System.Windows.Forms.DialogResult]::OK
            $dlg.Close()
        }
    })

    [void]$dlg.ShowDialog($script:Frm)
    $dlg.Dispose()
    return $script:PickerResult
}

function Show-MemberPickerDialog {
    param([string]$Title, [object[]]$Members = @())
    $script:PickerResult = $null

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = $Title
    $dlg.StartPosition = 'CenterParent'
    $dlg.Size = New-Object System.Drawing.Size(760, 480)
    $dlg.ShowInTaskbar = $false
    $dlg.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

    $lv = New-Object System.Windows.Forms.ListView
    $lv.View = 'Details'
    $lv.FullRowSelect = $true
    $lv.GridLines = $true
    $lv.MultiSelect = $true
    $lv.Dock = 'Fill'
    [void]$lv.Columns.Add('名称', 200)
    [void]$lv.Columns.Add('登录名', 150)
    [void]$lv.Columns.Add('类别', 110)
    [void]$lv.Columns.Add('DN', 340)

    foreach ($m in $Members) {
        $it = New-Object System.Windows.Forms.ListViewItem $m.Name
        [void]$it.SubItems.Add([string]$m.SamAccountName)
        [void]$it.SubItems.Add([string]$m.ObjectClass)
        [void]$it.SubItems.Add($m.DistinguishedName)
        $it.Tag = $m
        [void]$lv.Items.Add($it)
    }

    $bottom = New-Object System.Windows.Forms.Panel
    $bottom.Dock = 'Bottom'
    $bottom.Height = 46
    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = '可多选（Ctrl / Shift）'
    $hint.Location = New-Object System.Drawing.Point(12, 16)
    $hint.AutoSize = $true
    $okBtn = New-Object System.Windows.Forms.Button
    $okBtn.Text = '确定'
    $okBtn.Size = New-Object System.Drawing.Size(92, 30)
    $okBtn.Location = New-Object System.Drawing.Point(548, 8)
    $okBtn.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text = '取消'
    $cancelBtn.Size = New-Object System.Drawing.Size(92, 30)
    $cancelBtn.Location = New-Object System.Drawing.Point(648, 8)
    $cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    [void]$bottom.Controls.Add($hint)
    [void]$bottom.Controls.Add($okBtn)
    [void]$bottom.Controls.Add($cancelBtn)

    [void]$dlg.Controls.Add($lv)
    [void]$dlg.Controls.Add($bottom)
    $dlg.AcceptButton = $okBtn
    $dlg.CancelButton = $cancelBtn

    if ($dlg.ShowDialog($script:Frm) -eq [System.Windows.Forms.DialogResult]::OK) {
        $sel = @()
        foreach ($it in $lv.SelectedItems) { $sel += $it.Tag }
        $script:PickerResult = $sel
    }
    $dlg.Dispose()
    return $script:PickerResult
}

# ----------------------------------------------------------------------- 域信息
function Show-DomainInfoDialog {
    $ctx = New-Dialog -Title '域信息与密码策略' -Width 640 -Height 560
    $lv = New-Object System.Windows.Forms.ListView
    $lv.View = 'Details'
    $lv.FullRowSelect = $true
    $lv.GridLines = $true
    $lv.Dock = 'Fill'
    [void]$lv.Columns.Add('项目', 240)
    [void]$lv.Columns.Add('值', 400)

    $ctx.Table.RowCount++
    $r = $ctx.Table.RowCount - 1
    [void]$ctx.Table.Controls.Add($lv, 0, $r)
    $ctx.Table.SetColumnSpan($lv, 2)

    $btns = Add-DialogButtons -Ctx $ctx -OkText '关闭' -CancelText '复制'
    $btns.Cancel.DialogResult = [System.Windows.Forms.DialogResult]::None

    Enter-Busy '读取域信息...'
    try {
        $f = Get-ADForest -ErrorAction Stop
        $d = Get-ADDomain -ErrorAction Stop
        $p = Get-ADDefaultDomainPasswordPolicy -ErrorAction Stop

        $rows = New-Object System.Collections.ArrayList
        [void]$rows.Add(@('--- 基本信息 ---', ''))
        [void]$rows.Add(@('域（DNS）', $d.DNSRoot))
        [void]$rows.Add(@('域 DN', $d.DistinguishedName))
        [void]$rows.Add(@('NetBIOS', $d.NetBIOSName))
        [void]$rows.Add(@('森林', $f.Name))
        [void]$rows.Add(@('功能级别', "$($d.DomainMode) / $($f.ForestMode)"))
        [void]$rows.Add(@('PDC 仿真器', $d.PDCEmulator))

        [void]$rows.Add(@('--- FSMO 角色 ---', ''))
        [void]$rows.Add(@('域命名主控', $f.DomainNamingMaster))
        [void]$rows.Add(@('架构主控', $f.SchemaMaster))
        [void]$rows.Add(@('RID 主控', $d.RIDMaster))
        [void]$rows.Add(@('基础设施主控', $d.InfrastructureMaster))
        [void]$rows.Add(@('全局编录', ($f.GlobalCatalogs -join '  |  ')))
        [void]$rows.Add(@('DNS 服务器', (($f.ReplicaDirectoryServers) -join '  |  ')))

        [void]$rows.Add(@('--- 密码策略 ---', ''))
        [void]$rows.Add(@('最小密码长度', $p.MinPasswordLength))
        [void]$rows.Add(@('复杂度要求', $(if ($p.ComplexityEnabled) { '启用' } else { '禁用' })))
        [void]$rows.Add(@('密码最长使用期限', "$($p.MaxPasswordAge.Days) 天"))
        [void]$rows.Add(@('密码最短使用期限', "$($p.MinPasswordAge.Days) 天"))
        [void]$rows.Add(@('密码历史记录数', $p.PasswordHistoryCount))
        [void]$rows.Add(@('锁定阈值', $(if ($p.LockoutThreshold -eq 0) { '不锁定' } else { "$($p.LockoutThreshold) 次" })))
        [void]$rows.Add(@('锁定时长', "$($p.LockoutDuration.Minutes) 分钟"))
        [void]$rows.Add(@('锁定观察窗口', "$($p.LockoutObservationWindow.Minutes) 分钟"))

        foreach ($row in $rows) {
            $it = New-Object System.Windows.Forms.ListViewItem $row[0]
            [void]$it.SubItems.Add([string]$row[1])
            if ($row[0] -like '---*') {
                $it.Font = New-Object System.Drawing.Font(
                    'Microsoft YaHei UI', 9, [System.Drawing.FontStyle]::Bold)
                $it.ForeColor = [System.Drawing.Color]::DarkBlue
            }
            [void]$lv.Items.Add($it)
        }
    }
    catch {
        [void][System.Windows.Forms.MessageBox]::Show(
            "读取域信息失败：`r`n`r`n$($_.Exception.Message)", '域信息', 'OK', 'Error')
    }
    finally { Exit-Busy }

    $btns.Cancel.Add_Click({
        $lines = foreach ($it in $lv.Items) { "$($it.Text)`t$($it.SubItems[1].Text)" }
        if ($lines) {
            [System.Windows.Forms.Clipboard]::SetDataObject(($lines -join "`r`n"), $true)
            Set-Status '域信息已复制到剪贴板'
        }
    })

    [void]$ctx.Form.ShowDialog($script:Frm)
    $ctx.Form.Dispose()
}

# ----------------------------------------------------------------------- 导出 CSV
function Export-CurrentList {
    if ($script:List.Items.Count -eq 0) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, '当前列表为空。', '导出', 'OK', 'Information')
        return
    }
    $objs = @()
    foreach ($it in $script:List.Items) {
        if ($it.Tag -and $it.Tag.DistinguishedName) { $objs += $it.Tag }
    }
    if (-not $objs) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, '当前视图没有可导出的对象。', '导出', 'OK', 'Information')
        return
    }

    $dlg = New-Object System.Windows.Forms.SaveFileDialog
    $dlg.Filter = 'CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*'
    $dlg.FileName = "AD_$($script:CurrentMode)_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
    if ($dlg.ShowDialog($script:Frm) -ne [System.Windows.Forms.DialogResult]::OK) { return }

    Enter-Busy '导出中...'
    try {
        $props = switch ($script:CurrentMode) {
            'Users'     { @('Name', 'DisplayName', 'SamAccountName', 'UserPrincipalName', 'Enabled',
                            'LockedOut', 'EmailAddress', 'Department', 'Title', 'TelephoneNumber',
                            'LastLogonDate', 'Description', 'DistinguishedName') }
            'Groups'    { @('Name', 'SamAccountName', 'GroupCategory', 'GroupScope',
                            'Description', 'DistinguishedName') }
            'Computers' { @('Name', 'DNSHostName', 'Enabled', 'OperatingSystem',
                            'LastLogonDate', 'Description', 'DistinguishedName') }
            default     { @('Name', 'DistinguishedName') }
        }
        $objs | Select-Object -Property $props |
            Export-Csv -Path $dlg.FileName -NoTypeInformation -Encoding UTF8
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, "已导出 $($objs.Count) 条记录：`r`n$($dlg.FileName)",
            '导出', 'OK', 'Information')
    }
    catch {
        [void][System.Windows.Forms.MessageBox]::Show(
            "导出失败：`r`n`r`n$($_.Exception.Message)", '导出', 'OK', 'Error')
    }
    finally { Exit-Busy }
}

# ----------------------------------------------------------------------- 批量导入 CSV（新功能）
function Get-ImportFieldMap {
    # 返回 表头关键字(小写) -> 规范字段名 的映射；表头不区分大小写，中英文均可
    return @{
        'samaccountname' = 'Sam'
        'account'        = 'Sam'
        '登录名'          = 'Sam'
        'name'           = 'Name'
        'displayname'    = 'Name'
        '姓名'            = 'Name'
        'password'       = 'Password'
        '密码'            = 'Password'
        'ou'             = 'OU'
        'path'           = 'OU'
        '所在ou'          = 'OU'
        'department'     = 'Department'
        '部门'            = 'Department'
        'title'          = 'Title'
        '职位'            = 'Title'
        'emailaddress'   = 'Email'
        'email'          = 'Email'
        '邮箱'            = 'Email'
        'telephonenumber' = 'Phone'
        'phone'          = 'Phone'
        '电话'            = 'Phone'
        'description'    = 'Description'
        '说明'            = 'Description'
        '描述'            = 'Description'
    }
}

function Test-ImportHeaderKnown {
    # 判断表头行是否包含任一已知字段名（用于编码识别后的校验）
    param([string]$Header)
    if (-not $Header) { return $false }
    $map = Get-ImportFieldMap
    foreach ($col in ($Header -split ',')) {
        $key = $col.Trim().Trim('"').ToLower()
        if ($key -and $map.ContainsKey($key)) { return $true }
    }
    return $false
}

function Read-ImportCsvText {
    # 读取 CSV 文本，自动识别 UTF-8（含 BOM）与 GBK(936) 编码
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        return [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3)
    }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        return [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
    }
    # 无 BOM：先按严格 UTF-8 解码；解码失败或表头无法识别则回退 GBK(936)
    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $text = $null
    try { $text = $strictUtf8.GetString($bytes) } catch { $text = $null }
    if ($null -ne $text) {
        $firstLine = ($text -split "`r?`n")[0]
        if (Test-ImportHeaderKnown -Header $firstLine) { return $text }
    }
    return [System.Text.Encoding]::GetEncoding(936).GetString($bytes)
}

function ConvertFrom-AdImportCsv {
    # 把 CSV 文本解析为规范化行对象数组（含行号）；姓名缺省时用登录名
    param([string]$Text)
    $map = Get-ImportFieldMap
    $rows = New-Object System.Collections.ArrayList
    $lines = $Text -split "`r?`n"
    if ($lines.Count -lt 2) { return @() }

    # 解析表头 -> 规范字段名到列号的映射
    $headers = @()
    foreach ($col in ($lines[0] -split ',')) { $headers += $col.Trim().Trim('"') }
    $fieldIdx = @{}
    for ($i = 0; $i -lt $headers.Count; $i++) {
        $key = $headers[$i].Trim().ToLower()
        if ($map.ContainsKey($key) -and -not $fieldIdx.ContainsKey($map[$key])) {
            $fieldIdx[$map[$key]] = $i
        }
    }

    # 逐行解析（支持带引号字段与字段内逗号）
    for ($li = 1; $li -lt $lines.Count; $li++) {
        $line = $lines[$li]
        if (-not $line.Trim()) { continue }
        $cells = New-Object System.Collections.ArrayList
        $cur = ''
        $inQ = $false
        for ($ci = 0; $ci -lt $line.Length; $ci++) {
            $ch = $line[$ci]
            if ($ch -eq '"') {
                if ($inQ -and ($ci + 1) -lt $line.Length -and $line[$ci + 1] -eq '"') {
                    $cur += '"'; $ci++
                }
                else { $inQ = -not $inQ }
            }
            elseif ($ch -eq ',' -and -not $inQ) { [void]$cells.Add($cur); $cur = '' }
            else { $cur += $ch }
        }
        [void]$cells.Add($cur)

        $get = {
            param($f)
            if ($fieldIdx.ContainsKey($f)) {
                $idx = $fieldIdx[$f]
                if ($idx -lt $cells.Count) { return $cells[$idx].Trim() }
            }
            return ''
        }
        $sam = & $get 'Sam'
        $rec = [pscustomobject]@{
            Line        = $li + 1
            Sam         = $sam
            Name        = & $get 'Name'
            Password    = & $get 'Password'
            OU          = & $get 'OU'
            Department  = & $get 'Department'
            Title       = & $get 'Title'
            Email       = & $get 'Email'
            Phone       = & $get 'Phone'
            Description = & $get 'Description'
        }
        if (-not $rec.Name) { $rec.Name = $sam }
        [void]$rows.Add($rec)
    }
    return , @($rows.ToArray())
}

function Test-AdImportRow {
    # 校验一行导入数据，返回问题描述数组（空数组 = 通过）
    param($Row, $ExistingSams = $null, $OuSet = $null)
    $problems = @()
    $sam = [string]$Row.Sam
    if (-not $sam) {
        $problems += '登录名为空'
    }
    else {
        if ($sam.Length -gt 20) { $problems += '登录名超过 20 个字符' }
        # 非法字符："/\[]:;|=,+*?<>'@ 及空格
        if ($sam -match '["/\\\[\]:;|=,+*?<>@'' ]') { $problems += '登录名含非法字符' }
        if ($ExistingSams) {
            $dup = $false
            if ($ExistingSams -is [hashtable]) { $dup = $ExistingSams.ContainsKey($sam) }
            else {
                foreach ($e in @($ExistingSams)) {
                    if ($e -and $sam -ieq [string]$e) { $dup = $true; break }
                }
            }
            if ($dup) { $problems += '登录名在域中已存在' }
        }
    }
    if (-not [string]$Row.Name) { $problems += '姓名为空' }
    $pwd = [string]$Row.Password
    if (-not $pwd) { $problems += '密码为空' }
    elseif ($pwd.Length -lt 8) { $problems += '密码长度不足 8 位' }
    $ou = [string]$Row.OU
    if ($ou -and $OuSet -and -not $OuSet.ContainsKey($ou)) { $problems += '指定的 OU 不存在' }
    return , $problems
}

function Export-ListViewCsv {
    # 将 ListView 内容按列头导出为 CSV
    param($ListView, [string]$DefaultName)
    if (-not $ListView -or $ListView.Items.Count -eq 0) { return }
    $d = New-Object System.Windows.Forms.SaveFileDialog
    $d.Filter = 'CSV 文件 (*.csv)|*.csv'
    $d.FileName = $DefaultName
    if ($d.ShowDialog($script:Frm) -ne [System.Windows.Forms.DialogResult]::OK) { return }
    $headers = @()
    foreach ($col in $ListView.Columns) { $headers += $col.Text }
    $objs = @()
    foreach ($it in $ListView.Items) {
        $hash = [ordered]@{}
        for ($i = 0; $i -lt $headers.Count; $i++) {
            $val = ''
            if ($i -lt $it.SubItems.Count) { $val = $it.SubItems[$i].Text }
            $hash[$headers[$i]] = $val
        }
        $objs += [pscustomobject]$hash
    }
    $objs | Export-Csv -Path $d.FileName -NoTypeInformation -Encoding UTF8
}

function Invoke-BulkImportRows {
    # 逐行执行导入，每行独立 try/catch，绝不静默失败
    param($Rows)
    $results = @()
    $total = @($Rows).Count
    $n = 0
    Enter-Busy "正在导入 0/$total ..."
    foreach ($r in $Rows) {
        $n++
        Set-Status "正在导入 $n/$total ..."
        [System.Windows.Forms.Application]::DoEvents()
        try {
            $ouPath = if ([string]$r.OU) { [string]$r.OU } else { Get-CreateTarget }
            $secure = ConvertTo-SecureString ([string]$r.Password) -AsPlainText -Force
            $params = @{
                Name                 = [string]$r.Name
                SamAccountName       = [string]$r.Sam
                UserPrincipalName    = "$($r.Sam)@$($script:Domain.DNSRoot)"
                Path                 = $ouPath
                AccountPassword      = $secure
                Enabled              = $true
                ChangePasswordAtLogon = $true
            }
            if ([string]$r.Department)  { $params['Department'] = [string]$r.Department }
            if ([string]$r.Title)       { $params['Title'] = [string]$r.Title }
            if ([string]$r.Email)       { $params['EmailAddress'] = [string]$r.Email }
            if ([string]$r.Phone)       { $params['TelephoneNumber'] = [string]$r.Phone }
            if ([string]$r.Description) { $params['Description'] = [string]$r.Description }
            New-ADUser @params -ErrorAction Stop
            $results += [pscustomobject]@{ Line = $r.Line; Sam = [string]$r.Sam; Ok = $true; Error = '' }
        }
        catch {
            $results += [pscustomobject]@{
                Line = $r.Line; Sam = [string]$r.Sam; Ok = $false; Error = $_.Exception.Message
            }
        }
    }
    Exit-Busy
    return , $results
}

function Show-BulkImportResult {
    # 导入结果对话框：成功/失败统计 + 失败明细 + 导出失败明细 CSV
    param($Results)
    $okN = @($Results | Where-Object { $_.Ok }).Count
    $badN = @($Results).Count - $okN
    $ctx = New-Dialog -Title '批量导入结果' -Width 680 -Height 440

    $ctx.Table.RowCount++
    $r0 = $ctx.Table.RowCount - 1
    $summary = New-Object System.Windows.Forms.Label
    $summary.Text = "导入完成：成功 $okN，失败 $badN"
    $summary.Dock = 'Fill'
    $summary.TextAlign = 'MiddleLeft'
    [void]$ctx.Table.Controls.Add($summary, 0, $r0)
    $ctx.Table.SetColumnSpan($summary, 2)

    $lv = New-Object System.Windows.Forms.ListView
    $lv.View = 'Details'
    $lv.FullRowSelect = $true
    $lv.GridLines = $true
    $lv.Dock = 'Fill'
    [void]$lv.Columns.Add('行号', 60)
    [void]$lv.Columns.Add('登录名', 140)
    [void]$lv.Columns.Add('失败原因', 440)
    $fails = @($Results | Where-Object { -not $_.Ok })
    foreach ($f in $fails) {
        $it = New-Object System.Windows.Forms.ListViewItem ([string]$f.Line)
        [void]$it.SubItems.Add([string]$f.Sam)
        [void]$it.SubItems.Add([string]$f.Error)
        [void]$lv.Items.Add($it)
    }
    $ctx.Table.RowCount++
    $r1 = $ctx.Table.RowCount - 1
    [void]$ctx.Table.Controls.Add($lv, 0, $r1)
    $ctx.Table.SetColumnSpan($lv, 2)
    [void]$ctx.Table.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
        [System.Windows.Forms.SizeType]::Percent, 100)))

    $btns = Add-DialogButtons -Ctx $ctx -OkText '关闭' -CancelText '导出失败明细 CSV'
    $btns.Cancel.DialogResult = [System.Windows.Forms.DialogResult]::None
    $btns.Cancel.Add_Click({
        if ($fails.Count -eq 0) { return }
        $d = New-Object System.Windows.Forms.SaveFileDialog
        $d.Filter = 'CSV 文件 (*.csv)|*.csv'
        $d.FileName = "Import_Failures_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
        if ($d.ShowDialog($script:Frm) -eq 'OK') {
            $fails | Select-Object @{n = '行号'; e = { $_.Line } },
                @{n = '登录名'; e = { $_.Sam } }, @{n = '失败原因'; e = { $_.Error }} |
                Export-Csv -Path $d.FileName -NoTypeInformation -Encoding UTF8
        }
    })
    [void]$ctx.Form.ShowDialog($script:Frm)
    $ctx.Form.Dispose()
}

function Show-BulkImportDialog {
    # 批量导入用户（CSV）：选择文件 -> 校验预览 -> 逐行导入 -> 结果反馈
    $ofd = New-Object System.Windows.Forms.OpenFileDialog
    $ofd.Filter = 'CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*'
    $ofd.Title = '选择要导入的用户 CSV 文件'
    if ($ofd.ShowDialog($script:Frm) -ne [System.Windows.Forms.DialogResult]::OK) { return }
    $path = $ofd.FileName

    Enter-Busy '正在读取 CSV...'
    try {
        $text = Read-ImportCsvText -Path $path
        $rows = @(ConvertFrom-AdImportCsv -Text $text)
    }
    catch {
        Exit-Busy
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, "读取 CSV 失败：`r`n`r`n$($_.Exception.Message)", '批量导入', 'OK', 'Error')
        return
    }
    Exit-Busy
    if ($rows.Count -eq 0) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $script:Frm, 'CSV 中没有可导入的数据行（至少需要表头 + 1 行数据）。',
            '批量导入', 'OK', 'Warning')
        return
    }

    # 域中已存在的登录名预检查（批量）
    $existing = @{}
    Enter-Busy '正在检查域中已存在的登录名...'
    try {
        $sams = @($rows | ForEach-Object { [string]$_.Sam } | Where-Object { $_ } |
            Select-Object -Unique)
        foreach ($s in $sams) {
            $e = Escape-Ldap $s
            $found = @(Get-ADUser -Filter "SamAccountName -eq '$e'" -SearchBase $script:RootDN `
                -ErrorAction SilentlyContinue)
            if ($found.Count -gt 0) { $existing[$s] = $true }
            [System.Windows.Forms.Application]::DoEvents()
        }
    }
    catch { }
    Exit-Busy

    # 逐行校验（重复登录名也一并拦截）
    $checked = @()
    $seen = @{}
    $okCount = 0
    $badCount = 0
    foreach ($r in $rows) {
        $existSet = $existing.Clone()
        foreach ($k in $seen.Keys) { $existSet[$k] = $true }
        $problems = Test-AdImportRow -Row $r -ExistingSams $existSet -OuSet $script:OuMap
        if ([string]$r.Sam) { $seen[[string]$r.Sam] = $true }
        if ($problems.Count -eq 0) { $okCount++ } else { $badCount++ }
        $checked += [pscustomobject]@{ Row = $r; Problems = $problems }
    }

    # 预览对话框
    $ctx = New-Dialog -Title '批量导入用户 - 预览' -Width 980 -Height 560
    $lv = New-Object System.Windows.Forms.ListView
    $lv.View = 'Details'
    $lv.FullRowSelect = $true
    $lv.GridLines = $true
    $lv.Dock = 'Fill'
    [void]$lv.Columns.Add('行号', 50)
    [void]$lv.Columns.Add('登录名', 110)
    [void]$lv.Columns.Add('姓名', 110)
    [void]$lv.Columns.Add('密码', 70)
    [void]$lv.Columns.Add('OU', 170)
    [void]$lv.Columns.Add('部门', 90)
    [void]$lv.Columns.Add('职位', 90)
    [void]$lv.Columns.Add('邮箱', 130)
    [void]$lv.Columns.Add('电话', 100)
    [void]$lv.Columns.Add('说明', 110)
    [void]$lv.Columns.Add('校验', 240)
    $ctx.Table.RowCount++
    $r0 = $ctx.Table.RowCount - 1
    [void]$ctx.Table.Controls.Add($lv, 0, $r0)
    $ctx.Table.SetColumnSpan($lv, 2)
    [void]$ctx.Table.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
        [System.Windows.Forms.SizeType]::Percent, 100)))

    foreach ($c in $checked) {
        $r = $c.Row
        $it = New-Object System.Windows.Forms.ListViewItem ([string]$r.Line)
        [void]$it.SubItems.Add([string]$r.Sam)
        [void]$it.SubItems.Add([string]$r.Name)
        [void]$it.SubItems.Add('******')
        [void]$it.SubItems.Add([string]$r.OU)
        [void]$it.SubItems.Add([string]$r.Department)
        [void]$it.SubItems.Add([string]$r.Title)
        [void]$it.SubItems.Add([string]$r.Email)
        [void]$it.SubItems.Add([string]$r.Phone)
        [void]$it.SubItems.Add([string]$r.Description)
        if ($c.Problems.Count -eq 0) {
            [void]$it.SubItems.Add('通过')
        }
        else {
            $st = $it.SubItems.Add(($c.Problems -join '；'))
            $st.ForeColor = [System.Drawing.Color]::Gray
            $it.ForeColor = [System.Drawing.Color]::Gray
        }
        [void]$lv.Items.Add($it)
    }

    $ctx.Table.RowCount++
    $rs = $ctx.Table.RowCount - 1
    $statLbl = New-Object System.Windows.Forms.Label
    $statLbl.Text = "共 $($rows.Count) 行 / 可导入 $okCount / 有问题 $badCount"
    $statLbl.Dock = 'Fill'
    $statLbl.TextAlign = 'MiddleLeft'
    [void]$ctx.Table.Controls.Add($statLbl, 0, $rs)
    $ctx.Table.SetColumnSpan($statLbl, 2)

    $btns = Add-DialogButtons -Ctx $ctx -OkText '开始导入' -CancelText '取消'
    if ($okCount -eq 0) { $btns.OK.Enabled = $false }
    $btns.OK.DialogResult = [System.Windows.Forms.DialogResult]::None
    $btns.OK.Add_Click({
        if ($okCount -eq 0) { return }
        if (-not (Confirm-Action "确定要导入 $okCount 个用户吗？")) { return }
        $toImport = @($checked | Where-Object { $_.Problems.Count -eq 0 } |
            ForEach-Object { $_.Row })
        $results = Invoke-BulkImportRows -Rows $toImport
        $ctx.Form.Close()
        Show-BulkImportResult -Results $results
        Refresh-Current
    })

    [void]$ctx.Form.ShowDialog($script:Frm)
    $ctx.Form.Dispose()
}

# ----------------------------------------------------------------------- 密码到期提醒（新功能）
function Get-PasswordExpiryInfo {
    # 纯计算函数：根据用户对象属性与域密码策略计算到期信息
    # 返回 @{ ExpireDate; RemainingDays; Status; SortKey }
    # SortKey：从未设置 = -1000000（排最前），永不过期 = 1000000（排最后），其余按剩余天数升序
    param($User, $MaxAge, [datetime]$Now = $(Get-Date))
    $lastSet = $User.PasswordLastSet
    $never   = [bool]$User.PasswordNeverExpires
    $expired = [bool]$User.PasswordExpired
    $enabled = $true
    if ($null -ne $User.Enabled) { $enabled = [bool]$User.Enabled }

    $expireDate = $null
    $remaining  = $null
    $hasAge = ($null -ne $MaxAge) -and ($MaxAge -is [timespan]) -and ($MaxAge.TotalDays -gt 0)

    if ($null -eq $lastSet) {
        $status = '从未设置密码'
    }
    elseif ($never -or -not $hasAge) {
        $status = '永不过期'
    }
    else {
        $expireDate = $lastSet + $MaxAge
        $remaining = [int][math]::Floor(($expireDate - $Now).TotalDays)
        if (-not $enabled) { $status = '已禁用' }
        elseif ($expired -or $remaining -lt 0) { $status = '已过期' }
        else { $status = '正常' }
    }

    $sortKey = if ($null -eq $lastSet) { -1000000 }
               elseif ($status -eq '永不过期') { 1000000 }
               elseif ($null -ne $remaining) { $remaining }
               else { 999999 }

    return [pscustomobject]@{
        ExpireDate    = $expireDate
        RemainingDays = $remaining
        Status        = $status
        SortKey       = $sortKey
    }
}

function Show-PasswordExpiryDialog {
    # 密码到期提醒对话框
    $ctx = New-Dialog -Title '密码到期提醒' -Width 920 -Height 580
    $t = $ctx.Table

    # 提前提醒天数
    $t.RowCount++
    $r0 = $t.RowCount - 1
    $lblDays = New-Object System.Windows.Forms.Label
    $lblDays.Text = '提前提醒天数'
    $lblDays.TextAlign = 'MiddleLeft'
    $lblDays.Dock = 'Fill'
    [void]$t.Controls.Add($lblDays, 0, $r0)
    $days = New-Object System.Windows.Forms.NumericUpDown
    $days.Minimum = 1
    $days.Maximum = 90
    $days.Value = 14
    $days.Width = 90
    [void]$t.Controls.Add($days, 1, $r0)

    $cbExpired = Add-DialogCheckbox -Ctx $ctx -Label '包含已过期' -Checked $true
    $cbNever   = Add-DialogCheckbox -Ctx $ctx -Label '包含密码永不过期的账号' -Checked $false

    # 查询按钮
    $t.RowCount++
    $rQ = $t.RowCount - 1
    $queryBtn = New-Object System.Windows.Forms.Button
    $queryBtn.Text = '查询'
    $queryBtn.Width = 92
    $queryBtn.Height = 28
    [void]$t.Controls.Add($queryBtn, 0, $rQ)

    # 结果列表
    $lv = New-Object System.Windows.Forms.ListView
    $lv.View = 'Details'
    $lv.FullRowSelect = $true
    $lv.GridLines = $true
    $lv.Dock = 'Fill'
    [void]$lv.Columns.Add('姓名', 120)
    [void]$lv.Columns.Add('登录名', 110)
    [void]$lv.Columns.Add('密码最后设置', 130)
    [void]$lv.Columns.Add('到期时间', 130)
    [void]$lv.Columns.Add('剩余天数', 80)
    [void]$lv.Columns.Add('状态', 100)
    [void]$lv.Columns.Add('所在OU', 230)
    $t.RowCount++
    $rL = $t.RowCount - 1
    [void]$t.Controls.Add($lv, 0, $rL)
    $t.SetColumnSpan($lv, 2)
    [void]$t.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
        [System.Windows.Forms.SizeType]::Percent, 100)))

    # 底部统计
    $t.RowCount++
    $rS = $t.RowCount - 1
    $statLbl = New-Object System.Windows.Forms.Label
    $statLbl.Text = ''
    $statLbl.Dock = 'Fill'
    $statLbl.TextAlign = 'MiddleLeft'
    [void]$t.Controls.Add($statLbl, 0, $rS)
    $t.SetColumnSpan($statLbl, 2)

    $btns = Add-DialogButtons -Ctx $ctx -OkText '关闭' -CancelText '导出 CSV'
    $btns.Cancel.DialogResult = [System.Windows.Forms.DialogResult]::None

    $doQuery = {
        $lv.Items.Clear()
        Enter-Busy '正在查询密码到期信息...'
        try {
            $policy = Get-ADDefaultDomainPasswordPolicy -ErrorAction Stop
            $maxAge = $policy.MaxPasswordAge
            $props = @('PasswordLastSet', 'PasswordNeverExpires', 'PasswordExpired',
                       'Enabled', 'DistinguishedName')
            $users = Get-ADUser -Filter * -SearchBase $script:RootDN -SearchScope Subtree `
                -Properties $props -ErrorAction Stop
            $limit = [int]$days.Value
            $now = Get-Date
            $rows = @()
            foreach ($u in $users) {
                $info = Get-PasswordExpiryInfo -User $u -MaxAge $maxAge -Now $now
                $show = $false
                if ($info.Status -eq '从未设置密码') { $show = $true }
                elseif ($info.Status -eq '永不过期') { $show = $cbNever.Checked }
                elseif ($info.Status -eq '已过期') { $show = $cbExpired.Checked }
                elseif ($null -ne $info.RemainingDays -and $info.RemainingDays -le $limit) {
                    $show = $true
                }
                if ($show) { $rows += [pscustomobject]@{ U = $u; I = $info } }
            }
            $rows = @($rows | Sort-Object { $_.I.SortKey })
            foreach ($row in $rows) {
                $u = $row.U
                $info = $row.I
                $it = New-Object System.Windows.Forms.ListViewItem $(
                    if ($u.DisplayName) { $u.DisplayName } else { $u.Name })
                [void]$it.SubItems.Add($u.SamAccountName)
                [void]$it.SubItems.Add($(
                    if ($u.PasswordLastSet) { $u.PasswordLastSet.ToString('yyyy-MM-dd HH:mm') }
                    else { '' }))
                [void]$it.SubItems.Add($(
                    if ($info.ExpireDate) { $info.ExpireDate.ToString('yyyy-MM-dd HH:mm') }
                    else { '' }))
                [void]$it.SubItems.Add($(
                    if ($null -ne $info.RemainingDays) { [string]$info.RemainingDays }
                    else { '' }))
                $st = $it.SubItems.Add($info.Status)
                if ($info.Status -eq '已过期') {
                    $st.ForeColor = [System.Drawing.Color]::Firebrick
                }
                elseif ($info.Status -eq '从未设置密码') {
                    $st.ForeColor = [System.Drawing.Color]::DarkOrange
                }
                $ouDn = ''
                if ($u.DistinguishedName -like 'OU=*,*') {
                    $ouDn = ($u.DistinguishedName -split ',', 2)[1]
                }
                [void]$it.SubItems.Add($ouDn)
                $it.Tag = $u
                [void]$lv.Items.Add($it)
            }
            $statLbl.Text = "共 $($lv.Items.Count) 个账号需要关注"
            Set-Status $statLbl.Text
        }
        catch {
            [void][System.Windows.Forms.MessageBox]::Show(
                $script:Frm, "查询失败：`r`n`r`n$($_.Exception.Message)",
                '密码到期提醒', 'OK', 'Error')
        }
        finally { Exit-Busy }
    }

    $queryBtn.Add_Click($doQuery)
    $lv.Add_DoubleClick({
        if ($lv.SelectedItems.Count -gt 0 -and $lv.SelectedItems[0].Tag) {
            Show-ResetPasswordDialog -Obj $lv.SelectedItems[0].Tag
        }
    })
    $btns.Cancel.Add_Click({
        Export-ListViewCsv -ListView $lv `
            -DefaultName "PasswordExpiry_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
    })

    [void]$ctx.Form.ShowDialog($script:Frm)
    $ctx.Form.Dispose()
}

# ----------------------------------------------------------------------- 登录历史查询（新功能）
function Get-LogonTypeName {
    # 纯函数：安全日志 4624 的登录类型数值 -> 中文名
    param([int]$Id)
    switch ($Id) {
        2  { return '交互' }
        3  { return '网络' }
        4  { return '批处理' }
        5  { return '服务' }
        7  { return '解锁' }
        8  { return '网络明文' }
        9  { return '新凭据' }
        10 { return '远程交互(RDP)' }
        11 { return '缓存' }
        default { return "未知($Id)" }
    }
}

function ConvertFrom-FileTimeInt {
    # 纯函数：Int64 FILETIME -> DateTime；0/null/非法值返回 $null（从未登录）
    param($Value)
    if ($null -eq $Value) { return $null }
    $v = 0L
    try { $v = [int64]$Value } catch { return $null }
    if ($v -le 0) { return $null }
    try { return [datetime]::FromFileTime($v) } catch { return $null }
}

function Show-LogonHistoryDialog {
    # 登录历史查询：Tab1 逐域控精确 lastLogon；Tab2 安全日志 4624 明细
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = '登录历史查询'
    $dlg.StartPosition = 'CenterParent'
    $dlg.FormBorderStyle = 'FixedDialog'
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    $dlg.ShowInTaskbar = $false
    $dlg.ClientSize = New-Object System.Drawing.Size(960, 580)
    $dlg.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

    # 顶部搜索区
    $top = New-Object System.Windows.Forms.Panel
    $top.Dock = 'Top'
    $top.Height = 36
    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = '搜索（姓名或登录名）：'
    $lbl.AutoSize = $true
    $lbl.Location = New-Object System.Drawing.Point(12, 10)
    $search = New-Object System.Windows.Forms.TextBox
    $search.Location = New-Object System.Drawing.Point(150, 7)
    $search.Size = New-Object System.Drawing.Size(280, 24)
    $btnSearch = New-Object System.Windows.Forms.Button
    $btnSearch.Text = '查询'
    $btnSearch.Location = New-Object System.Drawing.Point(438, 5)
    $btnSearch.Size = New-Object System.Drawing.Size(80, 26)
    $cbRecurse = New-Object System.Windows.Forms.CheckBox
    $cbRecurse.Text = '递归搜索全域'
    $cbRecurse.Checked = $true
    $cbRecurse.AutoSize = $true
    $cbRecurse.Location = New-Object System.Drawing.Point(534, 10)
    [void]$top.Controls.Add($lbl)
    [void]$top.Controls.Add($search)
    [void]$top.Controls.Add($btnSearch)
    [void]$top.Controls.Add($cbRecurse)

    # Tab 控件
    $tabs = New-Object System.Windows.Forms.TabControl
    $tabs.Dock = 'Fill'

    $tab1 = New-Object System.Windows.Forms.TabPage
    $tab1.Text = '最近登录（精确）'
    $lv1 = New-Object System.Windows.Forms.ListView
    $lv1.View = 'Details'
    $lv1.FullRowSelect = $true
    $lv1.GridLines = $true
    $lv1.Dock = 'Fill'
    [void]$lv1.Columns.Add('姓名', 130)
    [void]$lv1.Columns.Add('登录名', 120)
    [void]$lv1.Columns.Add('最后登录(精确)', 140)
    [void]$lv1.Columns.Add('来源域控', 180)
    [void]$lv1.Columns.Add('说明', 280)
    [void]$tab1.Controls.Add($lv1)

    $tab2 = New-Object System.Windows.Forms.TabPage
    $tab2.Text = '登录明细(4624)'
    $top2 = New-Object System.Windows.Forms.Panel
    $top2.Dock = 'Top'
    $top2.Height = 34
    $lbl2 = New-Object System.Windows.Forms.Label
    $lbl2.Text = '最近天数：'
    $lbl2.AutoSize = $true
    $lbl2.Location = New-Object System.Drawing.Point(12, 9)
    $days2 = New-Object System.Windows.Forms.NumericUpDown
    $days2.Minimum = 1
    $days2.Maximum = 30
    $days2.Value = 7
    $days2.Location = New-Object System.Drawing.Point(80, 6)
    $days2.Width = 70
    [void]$top2.Controls.Add($lbl2)
    [void]$top2.Controls.Add($days2)
    $permLabel = New-Object System.Windows.Forms.Label
    $permLabel.Text = ''
    $permLabel.Dock = 'Bottom'
    $permLabel.Height = 32
    $permLabel.ForeColor = [System.Drawing.Color]::Firebrick
    $lv2 = New-Object System.Windows.Forms.ListView
    $lv2.View = 'Details'
    $lv2.FullRowSelect = $true
    $lv2.GridLines = $true
    $lv2.Dock = 'Fill'
    [void]$lv2.Columns.Add('时间', 150)
    [void]$lv2.Columns.Add('用户', 130)
    [void]$lv2.Columns.Add('来源IP', 120)
    [void]$lv2.Columns.Add('登录类型', 120)
    [void]$lv2.Columns.Add('域控', 180)
    [void]$tab2.Controls.Add($lv2)
    [void]$tab2.Controls.Add($permLabel)
    [void]$tab2.Controls.Add($top2)

    [void]$tabs.TabPages.Add($tab1)
    [void]$tabs.TabPages.Add($tab2)

    # 底部
    $bottom = New-Object System.Windows.Forms.Panel
    $bottom.Dock = 'Bottom'
    $bottom.Height = 46
    $statLbl = New-Object System.Windows.Forms.Label
    $statLbl.Text = '就绪'
    $statLbl.AutoSize = $true
    $statLbl.Location = New-Object System.Drawing.Point(12, 16)
    $btnExport = New-Object System.Windows.Forms.Button
    $btnExport.Text = '导出 CSV'
    $btnExport.Size = New-Object System.Drawing.Size(92, 30)
    $btnExport.Location = New-Object System.Drawing.Point(748, 8)
    $btnClose = New-Object System.Windows.Forms.Button
    $btnClose.Text = '关闭'
    $btnClose.Size = New-Object System.Drawing.Size(92, 30)
    $btnClose.Location = New-Object System.Drawing.Point(848, 8)
    $btnClose.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    [void]$bottom.Controls.Add($statLbl)
    [void]$bottom.Controls.Add($btnExport)
    [void]$bottom.Controls.Add($btnClose)

    [void]$dlg.Controls.Add($tabs)
    [void]$dlg.Controls.Add($bottom)
    [void]$dlg.Controls.Add($top)
    $dlg.AcceptButton = $btnSearch
    $dlg.CancelButton = $btnClose

    $doSearch = {
        $lv1.Items.Clear()
        $lv2.Items.Clear()
        $permLabel.Text = ''
        $q = $search.Text.Trim()
        if (-not $q) {
            $statLbl.Text = '请输入搜索条件'
            return
        }

        # 1) 搜索用户
        Enter-Busy '正在搜索用户...'
        $found = @()
        try {
            $e = Escape-Ldap $q
            $filter = "Name -like '*$e*' -or SamAccountName -like '*$e*'"
            if ($cbRecurse.Checked) {
                $found = @(Get-ADUser -Filter $filter -SearchBase $script:RootDN `
                    -SearchScope Subtree -Properties LastLogonDate, lastLogonTimestamp `
                    -ErrorAction Stop | Sort-Object Name)
            }
            else {
                $base = if ($script:CurrentDN) { $script:CurrentDN } else { $script:RootDN }
                $found = @(Get-ADUser -Filter $filter -SearchBase $base `
                    -SearchScope OneLevel -Properties LastLogonDate, lastLogonTimestamp `
                    -ErrorAction Stop | Sort-Object Name)
            }
        }
        catch {
            Exit-Busy
            [void][System.Windows.Forms.MessageBox]::Show(
                $script:Frm, "搜索失败：`r`n`r`n$($_.Exception.Message)",
                '登录历史查询', 'OK', 'Error')
            return
        }
        Exit-Busy
        if ($found.Count -eq 0) {
            $statLbl.Text = '未找到匹配的用户'
            return
        }

        # 2) 枚举域控
        Enter-Busy '正在枚举域控...'
        $dcs = @()
        try { $dcs = @(Get-ADDomainController -Filter * -ErrorAction Stop) }
        catch {
            Exit-Busy
            [void][System.Windows.Forms.MessageBox]::Show(
                $script:Frm, "枚举域控失败：`r`n`r`n$($_.Exception.Message)",
                '登录历史查询', 'OK', 'Error')
            return
        }
        Exit-Busy

        # 3) Tab1：逐域控读取精确 lastLogon（性能保护：最多精确查 20 个）
        $preciseLimit = 20
        $precise = @($found | Select-Object -First $preciseLimit)
        $rest = @($found | Select-Object -Skip $preciseLimit)
        Enter-Busy '正在逐域控查询精确最后登录...'
        $n = 0
        foreach ($u in $precise) {
            $n++
            Set-Status "精确查询 $n/$($precise.Count)：$($u.SamAccountName) ..."
            [System.Windows.Forms.Application]::DoEvents()
            $best = $null
            $bestDc = ''
            foreach ($dc in $dcs) {
                try {
                    $o = Get-ADUser -Identity $u.DistinguishedName -Server $dc.HostName `
                        -Properties lastLogon -ErrorAction Stop
                    $t = ConvertFrom-FileTimeInt $o.lastLogon
                    if ($t -and ($null -eq $best -or $t -gt $best)) {
                        $best = $t
                        $bestDc = $dc.HostName
                    }
                }
                catch { }  # 单个域控不可达不影响整体
            }
            $it = New-Object System.Windows.Forms.ListViewItem $(
                if ($u.DisplayName) { $u.DisplayName } else { $u.Name })
            [void]$it.SubItems.Add($u.SamAccountName)
            [void]$it.SubItems.Add($(
                if ($best) { $best.ToString('yyyy-MM-dd HH:mm') } else { '从未登录' }))
            [void]$it.SubItems.Add($bestDc)
            [void]$it.SubItems.Add('精确（逐域控 lastLogon）')
            [void]$lv1.Items.Add($it)
        }
        foreach ($u in $rest) {
            $t = ConvertFrom-FileTimeInt $u.lastLogonTimestamp
            $it = New-Object System.Windows.Forms.ListViewItem $(
                if ($u.DisplayName) { $u.DisplayName } else { $u.Name })
            [void]$it.SubItems.Add($u.SamAccountName)
            [void]$it.SubItems.Add($(
                if ($t) { $t.ToString('yyyy-MM-dd HH:mm') } else { '从未登录' }))
            [void]$it.SubItems.Add('')
            $st = $it.SubItems.Add('近似值(lastLogonTimestamp)，未逐域控精确查询')
            $st.ForeColor = [System.Drawing.Color]::DimGray
            [void]$lv1.Items.Add($it)
        }
        Exit-Busy

        # 4) Tab2：安全日志 4624 明细（权限不足优雅降级）
        $days = [int]$days2.Value
        $denied = $false
        $events = @()
        $matchSet = @{}
        foreach ($u in $found) { $matchSet[[string]$u.SamAccountName] = $true }
        Enter-Busy "正在读取各域控安全日志（最近 $days 天的 4624 事件）..."
        foreach ($dc in $dcs) {
            Set-Status "读取域控 $($dc.Name) 的安全日志..."
            [System.Windows.Forms.Application]::DoEvents()
            try {
                $ev = Get-WinEvent -FilterHashtable @{
                    LogName   = 'Security'
                    Id        = 4624
                    StartTime = (Get-Date).AddDays(-$days)
                } -ComputerName $dc.HostName -ErrorAction Stop
            }
            catch {
                if ($_.FullyQualifiedErrorId -like '*NoMatchingEventsFound*') { continue }
                $denied = $true
                continue
            }
            foreach ($e2 in $ev) {
                try {
                    if ($e2.Properties.Count -lt 9) { continue }
                    $targetUser = [string]$e2.Properties[5].Value
                    if (-not $matchSet.ContainsKey($targetUser)) { continue }
                    $typeId = 0
                    try { $typeId = [int]$e2.Properties[8].Value } catch { }
                    $ip = ''
                    if ($e2.Properties.Count -gt 17) {
                        try { $ip = [string]$e2.Properties[17].Value } catch { }
                    }
                    $events += [pscustomobject]@{
                        Time = $e2.TimeCreated; User = $targetUser; Ip = $ip
                        TypeId = $typeId; TypeName = (Get-LogonTypeName -Id $typeId)
                        Dc = $dc.Name
                    }
                }
                catch { }
            }
        }
        Exit-Busy
        if ($denied -and $events.Count -eq 0) {
            $permLabel.Text = '当前账户无权读取域控安全日志（需要域管理员或 Event Log Readers 权限）。'
            $tabs.SelectedTab = $tab1
            $statLbl.Text = "匹配 $($found.Count) 个用户；登录明细不可用（权限不足），已切换到「最近登录」"
            return
        }
        $events = @($events | Sort-Object Time -Descending | Select-Object -First 1000)
        foreach ($ev2 in $events) {
            $it = New-Object System.Windows.Forms.ListViewItem $(
                $ev2.Time.ToString('yyyy-MM-dd HH:mm:ss'))
            [void]$it.SubItems.Add($ev2.User)
            [void]$it.SubItems.Add($ev2.Ip)
            [void]$it.SubItems.Add($ev2.TypeName)
            [void]$it.SubItems.Add($ev2.Dc)
            [void]$lv2.Items.Add($it)
        }
        $note = ''
        if ($rest.Count -gt 0) {
            $note = "；仅精确查询前 $preciseLimit 个，其余显示近似值(lastLogonTimestamp)"
        }
        $statLbl.Text = "匹配 $($found.Count) 个用户；4624 明细 $($lv2.Items.Count) 条$note"
    }

    $btnSearch.Add_Click($doSearch)
    $search.Add_KeyDown({
        if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
            & $doSearch
            $_.Handled = $true
            $_.SuppressKeyPress = $true
        }
    })
    $btnExport.Add_Click({
        if ($tabs.SelectedIndex -eq 0) {
            Export-ListViewCsv -ListView $lv1 `
                -DefaultName "LogonExact_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
        }
        else {
            Export-ListViewCsv -ListView $lv2 `
                -DefaultName "Logon4624_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
        }
    })
    $btnClose.Add_Click({ $dlg.Close() })

    [void]$dlg.ShowDialog($script:Frm)
    $dlg.Dispose()
}

# ----------------------------------------------------------------------- UI 构建
function New-MenuItem {
    param([string]$Text, [scriptblock]$OnClick = $null, [string]$Keys = '')
    $mi = New-Object System.Windows.Forms.ToolStripMenuItem
    $mi.Text = $Text
    if ($OnClick) { [void]$mi.Add_Click($OnClick) }
    if ($Keys) {
        $mi.ShortcutKeys = [System.Windows.Forms.Keys](
            [System.Enum]::Parse([System.Windows.Forms.Keys], $Keys))
    }
    return $mi
}

function New-Separator {
    return (New-Object System.Windows.Forms.ToolStripSeparator)
}

function Get-SelectedObject {
    $items = Get-SelectedItems
    if ($items.Count -eq 0) { return $null }
    return $items[0].Tag
}

function Get-CreateTarget {
    if ($script:CurrentDN) { return $script:CurrentDN }
    return $script:RootDN
}

function Get-CurrentMode {
    if ($script:ModeBox -and $script:ModeBox.SelectedItem) {
        $name = [string]$script:ModeBox.SelectedItem
        if ($script:ModeMap.ContainsKey($name)) { return $script:ModeMap[$name] }
    }
    return 'Users'
}

function Get-CurrentKind {
    switch ($script:CurrentMode) {
        'Users'     { return '用户' }
        'Groups'    { return '组' }
        'Computers' { return '计算机' }
        default     { return '对象' }
    }
}

function Build-UI {
    $script:Frm = New-Object System.Windows.Forms.Form
    $script:Frm.Text = "AD 域控管理控制台 - $($script:Domain.DNSRoot)"
    $script:Frm.StartPosition = 'CenterScreen'
    $script:Frm.Size = New-Object System.Drawing.Size(1320, 820)
    $script:Frm.MinimumSize = New-Object System.Drawing.Size(960, 600)
    $script:Frm.WindowState = 'Maximized'
    $script:Frm.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

    # ---------------- 菜单栏 ----------------
    $menu = New-Object System.Windows.Forms.MenuStrip

    $mFile = New-Object System.Windows.Forms.ToolStripMenuItem; $mFile.Text = '文件(&F)'
    [void]$mFile.DropDownItems.Add((New-MenuItem -Text '刷新组织单位树(&R)' -Keys 'F5' -OnClick { Build-OuTree }))
    [void]$mFile.DropDownItems.Add((New-Separator))
    [void]$mFile.DropDownItems.Add((New-MenuItem -Text '导出当前列表 CSV(&E)' -OnClick { Export-CurrentList }))
    [void]$mFile.DropDownItems.Add((New-MenuItem -Text '批量导入用户(CSV)(&I)...' -OnClick { Show-BulkImportDialog }))
    [void]$mFile.DropDownItems.Add((New-Separator))
    [void]$mFile.DropDownItems.Add((New-MenuItem -Text '退出(&X)' -Keys 'Alt+F4' -OnClick { $script:Frm.Close() }))

    $mUser = New-Object System.Windows.Forms.ToolStripMenuItem; $mUser.Text = '用户(&U)'
    [void]$mUser.DropDownItems.Add((New-MenuItem -Text '新建用户(&N)...' -OnClick { Show-NewUserDialog -Path (Get-CreateTarget) }))
    [void]$mUser.DropDownItems.Add((New-Separator))
    [void]$mUser.DropDownItems.Add((New-MenuItem -Text '重置密码(&P)...' -OnClick { Show-ResetPasswordDialog -Obj (Get-SelectedObject) }))
    [void]$mUser.DropDownItems.Add((New-MenuItem -Text '启用账号(&E)' -OnClick { Invoke-ToggleEnabled -Enable $true }))
    [void]$mUser.DropDownItems.Add((New-MenuItem -Text '禁用账号(&D)' -OnClick { Invoke-ToggleEnabled -Enable $false }))
    [void]$mUser.DropDownItems.Add((New-MenuItem -Text '解锁账号(&L)' -OnClick { Invoke-Unlock }))
    [void]$mUser.DropDownItems.Add((New-Separator))
    [void]$mUser.DropDownItems.Add((New-MenuItem -Text '删除用户(&X)' -OnClick { Invoke-Delete -Kind '用户' }))
    [void]$mUser.DropDownItems.Add((New-Separator))
    [void]$mUser.DropDownItems.Add((New-MenuItem -Text '查看属性(&I)' -OnClick { Show-Properties (Get-SelectedObject) }))

    $mGroup = New-Object System.Windows.Forms.ToolStripMenuItem; $mGroup.Text = '组(&G)'
    [void]$mGroup.DropDownItems.Add((New-MenuItem -Text '新建组(&N)...' -OnClick { Show-NewGroupDialog -Path (Get-CreateTarget) }))
    [void]$mGroup.DropDownItems.Add((New-Separator))
    [void]$mGroup.DropDownItems.Add((New-MenuItem -Text '添加成员(&A)...' -OnClick { Invoke-AddGroupMember -Group (Get-SelectedObject) }))
    [void]$mGroup.DropDownItems.Add((New-MenuItem -Text '移除成员(&R)...' -OnClick { Invoke-RemoveGroupMember -Group (Get-SelectedObject) }))
    [void]$mGroup.DropDownItems.Add((New-MenuItem -Text '查看成员(&M)...' -OnClick { Show-MembersDialog -Group (Get-SelectedObject) }))
    [void]$mGroup.DropDownItems.Add((New-Separator))
    [void]$mGroup.DropDownItems.Add((New-MenuItem -Text '删除组(&X)' -OnClick { Invoke-Delete -Kind '组' }))

    $mComp = New-Object System.Windows.Forms.ToolStripMenuItem; $mComp.Text = '计算机(&C)'
    [void]$mComp.DropDownItems.Add((New-MenuItem -Text '启用(&E)' -OnClick { Invoke-ToggleEnabled -Enable $true }))
    [void]$mComp.DropDownItems.Add((New-MenuItem -Text '禁用(&D)' -OnClick { Invoke-ToggleEnabled -Enable $false }))
    [void]$mComp.DropDownItems.Add((New-Separator))
    [void]$mComp.DropDownItems.Add((New-MenuItem -Text '删除计算机(&X)' -OnClick { Invoke-Delete -Kind '计算机' }))

    $mTool = New-Object System.Windows.Forms.ToolStripMenuItem; $mTool.Text = '工具(&T)'
    [void]$mTool.DropDownItems.Add((New-MenuItem -Text '域信息与密码策略(&D)...' -OnClick { Show-DomainInfoDialog }))
    [void]$mTool.DropDownItems.Add((New-MenuItem -Text '密码到期提醒(&P)...' -OnClick { Show-PasswordExpiryDialog }))
    [void]$mTool.DropDownItems.Add((New-MenuItem -Text '登录历史查询(&L)...' -OnClick { Show-LogonHistoryDialog }))
    [void]$mTool.DropDownItems.Add((New-Separator))
    [void]$mTool.DropDownItems.Add((New-MenuItem -Text '全域搜索(&S)' -OnClick {
        Update-List -DN $script:RootDN -Mode (Get-CurrentMode) -Query $script:SearchBox.Text.Trim()
        Set-Position $script:RootDN
    }))
    [void]$mTool.DropDownItems.Add((New-MenuItem -Text '清除搜索条件(&C)' -OnClick {
        $script:SearchBox.Text = ''
        Update-List -DN $script:CurrentDN -Mode (Get-CurrentMode) -Query ''
    }))

    $mHelp = New-Object System.Windows.Forms.ToolStripMenuItem; $mHelp.Text = '帮助(&H)'
    [void]$mHelp.DropDownItems.Add((New-MenuItem -Text '关于(&A)' -OnClick {
        [void][System.Windows.Forms.MessageBox]::Show($script:Frm,
            "AD 域控管理控制台`r`n`r`n基于 PowerShell + WinForms + RSAT ActiveDirectory 模块。`r`n" +
            "当前身份：$([Security.Principal.WindowsIdentity]::GetCurrent().Name)`r`n" +
            "目标域：$($script:Domain.DNSRoot)`r`n" +
            "PDC：$($script:Domain.PDCEmulator)`r`n`r`n" +
            "只读操作需要普通域读取权限；写操作需要相应委派权限或域管理员权限。",
            '关于', 'OK', 'Information')
    }))

    [void]$menu.Items.Add($mFile)
    [void]$menu.Items.Add($mUser)
    [void]$menu.Items.Add($mGroup)
    [void]$menu.Items.Add($mComp)
    [void]$menu.Items.Add($mTool)
    [void]$menu.Items.Add($mHelp)
    $script:Frm.MainMenuStrip = $menu
    # 注意：此处暂不加入 Controls，待工具栏添加后再加入——
    # WinForms 停靠按添加顺序的反向分配边缘，后加入者占据更外侧位置。

    # ---------------- 工具栏 ----------------
    $ts = New-Object System.Windows.Forms.ToolStrip
    $ts.Dock = 'Top'
    $ts.GripStyle = 'Hidden'

    $btnRefresh = New-Object System.Windows.Forms.ToolStripButton
    $btnRefresh.Text = '刷新'
    $btnRefresh.ToolTipText = '重新加载当前视图 (F5)'
    [void]$ts.Items.Add($btnRefresh)
    [void]$ts.Items.Add((New-Separator))

    [void]$ts.Items.Add((New-Object System.Windows.Forms.ToolStripLabel -ArgumentList '对象：'))
    $script:ModeBox = New-Object System.Windows.Forms.ToolStripComboBox
    $script:ModeBox.DropDownStyle = 'DropDownList'
    foreach ($m in @('用户', '组', '计算机')) { [void]$script:ModeBox.Items.Add($m) }
    $script:ModeBox.SelectedIndex = 0
    $script:ModeBox.Width = 90
    [void]$ts.Items.Add($script:ModeBox)
    [void]$ts.Items.Add((New-Separator))

    [void]$ts.Items.Add((New-Object System.Windows.Forms.ToolStripLabel -ArgumentList '搜索：'))
    $script:SearchBox = New-Object System.Windows.Forms.ToolStripTextBox
    $script:SearchBox.Width = 240
    $script:SearchBox.ToolTipText = '留空则显示所选 OU 的直接子对象；输入关键词则递归搜索其子树'
    [void]$ts.Items.Add($script:SearchBox)

    $btnSearch = New-Object System.Windows.Forms.ToolStripButton
    $btnSearch.Text = '搜索'
    [void]$ts.Items.Add($btnSearch)
    [void]$ts.Items.Add((New-Separator))

    $posLabel = New-Object System.Windows.Forms.ToolStripLabel
    $posLabel.Text = '位置：'
    [void]$ts.Items.Add($posLabel)
    $script:PositionLbl = New-Object System.Windows.Forms.ToolStripLabel
    $script:PositionLbl.Text = ''
    $script:PositionLbl.ForeColor = [System.Drawing.Color]::DimGray
    [void]$ts.Items.Add($script:PositionLbl)
    [void]$script:Frm.Controls.Add($ts)

    # 菜单栏最后加入，使其停靠在工具栏之上
    [void]$script:Frm.Controls.Add($menu)

    # ---------------- 主体 ----------------
    $outer = New-Object System.Windows.Forms.SplitContainer
    $outer.Dock = 'Fill'
    $outer.Orientation = 'Vertical'
    $outer.SplitterDistance = 300
    $outer.Panel1MinSize = 220
    $outer.Panel2MinSize = 480

    $script:Tree = New-Object System.Windows.Forms.TreeView
    $script:Tree.Dock = 'Fill'
    $script:Tree.HideSelection = $false
    $script:Tree.ShowNodeToolTips = $true
    $script:Tree.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
    [void]$outer.Panel1.Controls.Add($script:Tree)

    $inner = New-Object System.Windows.Forms.SplitContainer
    $inner.Dock = 'Fill'
    $inner.Orientation = 'Horizontal'
    $inner.SplitterDistance = 480
    $inner.Panel1MinSize = 200
    $inner.Panel2MinSize = 140

    $script:List = New-Object System.Windows.Forms.ListView
    $script:List.Dock = 'Fill'
    $script:List.View = 'Details'
    $script:List.FullRowSelect = $true
    $script:List.GridLines = $true
    $script:List.MultiSelect = $true
    $script:List.HideSelection = $false
    $script:List.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
    $script:List.ShowItemToolTips = $true
    [void]$inner.Panel1.Controls.Add($script:List)

    $propPanel = New-Object System.Windows.Forms.Panel
    $propPanel.Dock = 'Fill'

    $script:DetailTitle = New-Object System.Windows.Forms.Label
    $script:DetailTitle.Text = '属性'
    $script:DetailTitle.Dock = 'Top'
    $script:DetailTitle.Height = 24
    $script:DetailTitle.Padding = New-Object System.Windows.Forms.Padding(4, 5, 0, 0)
    $script:DetailTitle.ForeColor = [System.Drawing.Color]::DimGray

    $script:PropList = New-Object System.Windows.Forms.ListView
    $script:PropList.Dock = 'Fill'
    $script:PropList.View = 'Details'
    $script:PropList.FullRowSelect = $true
    $script:PropList.GridLines = $true
    $script:PropList.HeaderStyle = 'Nonclickable'
    [void]$script:PropList.Columns.Add('属性', 170)
    [void]$script:PropList.Columns.Add('值', 700)

    [void]$propPanel.Controls.Add($script:PropList)
    [void]$propPanel.Controls.Add($script:DetailTitle)
    [void]$inner.Panel2.Controls.Add($propPanel)

    [void]$outer.Panel2.Controls.Add($inner)
    [void]$script:Frm.Controls.Add($outer)
    $outer.BringToFront()

    # ---------------- 状态栏 ----------------
    $status = New-Object System.Windows.Forms.StatusStrip
    $script:StatusLbl = New-Object System.Windows.Forms.ToolStripStatusLabel
    $script:StatusLbl.Text = '就绪'
    $script:StatusLbl.Spring = $true
    $script:StatusLbl.TextAlign = 'MiddleLeft'
    $identLbl = New-Object System.Windows.Forms.ToolStripStatusLabel
    $identLbl.Text = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    [void]$status.Items.Add($script:StatusLbl)
    [void]$status.Items.Add($identLbl)
    [void]$script:Frm.Controls.Add($status)

    # ---------------- 事件 ----------------
    $btnRefresh.Add_Click({ Refresh-Current })
    $btnSearch.Add_Click({
        Update-List -DN $script:CurrentDN -Mode (Get-CurrentMode) -Query $script:SearchBox.Text.Trim()
    })
    $script:SearchBox.Add_KeyDown({
        if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
            Update-List -DN $script:CurrentDN -Mode (Get-CurrentMode) -Query $script:SearchBox.Text.Trim()
            $_.Handled = $true
            $_.SuppressKeyPress = $true
        }
    })
    $script:ModeBox.Add_SelectedIndexChanged({
        Update-List -DN $script:CurrentDN -Mode (Get-CurrentMode) -Query $script:LastQuery
    })

    $script:Tree.Add_AfterSelect({
        $node = $script:Tree.SelectedNode
        if (-not $node -or -not $node.Tag) { return }
        $script:CurrentDN = $node.Tag.DN
        Set-Position $node.Tag.DN
        Update-List -DN $node.Tag.DN -Mode (Get-CurrentMode) -Query ''
        $script:SearchBox.Text = ''
    })

    $script:List.Add_SelectedIndexChanged({
        if ($script:List.SelectedItems.Count -gt 0) { Show-Properties $script:List.SelectedItems[0].Tag }
        else { Show-Properties $null }
    })

    $script:List.Add_DoubleClick({
        Show-Properties (Get-SelectedObject)
    })

    # 列表右键菜单
    $listMenu = New-Object System.Windows.Forms.ContextMenuStrip
    [void]$listMenu.Items.Add((New-MenuItem -Text '查看属性' -OnClick { Show-Properties (Get-SelectedObject) }))
    [void]$listMenu.Items.Add((New-Separator))
    [void]$listMenu.Items.Add((New-MenuItem -Text '重置密码...' -OnClick { Show-ResetPasswordDialog -Obj (Get-SelectedObject) }))
    [void]$listMenu.Items.Add((New-MenuItem -Text '启用' -OnClick { Invoke-ToggleEnabled -Enable $true }))
    [void]$listMenu.Items.Add((New-MenuItem -Text '禁用' -OnClick { Invoke-ToggleEnabled -Enable $false }))
    [void]$listMenu.Items.Add((New-MenuItem -Text '解锁' -OnClick { Invoke-Unlock }))
    [void]$listMenu.Items.Add((New-Separator))
    [void]$listMenu.Items.Add((New-MenuItem -Text '组：添加成员...' -OnClick { Invoke-AddGroupMember -Group (Get-SelectedObject) }))
    [void]$listMenu.Items.Add((New-MenuItem -Text '组：移除成员...' -OnClick { Invoke-RemoveGroupMember -Group (Get-SelectedObject) }))
    [void]$listMenu.Items.Add((New-MenuItem -Text '组：查看成员...' -OnClick { Show-MembersDialog -Group (Get-SelectedObject) }))
    [void]$listMenu.Items.Add((New-Separator))
    [void]$listMenu.Items.Add((New-MenuItem -Text '删除' -OnClick { Invoke-Delete -Kind (Get-CurrentKind) }))
    [void]$listMenu.Items.Add((New-Separator))
    [void]$listMenu.Items.Add((New-MenuItem -Text '复制 DN' -OnClick {
        $o = Get-SelectedObject
        if ($o) {
            [System.Windows.Forms.Clipboard]::SetText($o.DistinguishedName)
            Set-Status 'DN 已复制'
        }
    }))
    [void]$listMenu.Items.Add((New-MenuItem -Text '导出 CSV...' -OnClick { Export-CurrentList }))
    $script:List.ContextMenuStrip = $listMenu

    # 树右键菜单
    $treeMenu = New-Object System.Windows.Forms.ContextMenuStrip
    [void]$treeMenu.Items.Add((New-MenuItem -Text '刷新组织单位树' -Keys 'F5' -OnClick { Build-OuTree }))
    [void]$treeMenu.Items.Add((New-Separator))
    [void]$treeMenu.Items.Add((New-MenuItem -Text '在此处新建用户...' -OnClick { Show-NewUserDialog -Path (Get-CreateTarget) }))
    [void]$treeMenu.Items.Add((New-MenuItem -Text '在此处新建组...' -OnClick { Show-NewGroupDialog -Path (Get-CreateTarget) }))
    [void]$treeMenu.Items.Add((New-Separator))
    [void]$treeMenu.Items.Add((New-MenuItem -Text '复制 DN' -OnClick {
        if ($script:Tree.SelectedNode) {
            [System.Windows.Forms.Clipboard]::SetText($script:Tree.SelectedNode.Tag.DN)
            Set-Status 'DN 已复制'
        }
    }))
    [void]$treeMenu.Items.Add((New-MenuItem -Text '在此处全域搜索' -OnClick {
        Update-List -DN $script:Tree.SelectedNode.Tag.DN -Mode (Get-CurrentMode) -Query $script:SearchBox.Text.Trim()
        Set-Position $script:Tree.SelectedNode.Tag.DN
    }))
    $script:Tree.ContextMenuStrip = $treeMenu

    $script:Frm.Add_Shown({
        # 保留连接信息，同时显示当前列表统计（Update-List 已写入状态栏）
        Set-Status "已连接 $($script:Domain.DNSRoot) —— PDC: $($script:Domain.PDCEmulator)  |  $($script:StatusLbl.Text)"
    })
}

# ----------------------------------------------------------------------- 主流程
function Start-ADConsole {
    Enter-Busy '正在连接域...'
    try {
        $script:Domain = Get-ADDomain -ErrorAction Stop
    }
    catch {
        [void][System.Windows.Forms.MessageBox]::Show(
            "无法连接到域：`r`n`r`n$($_.Exception.Message)", 'AD Console', 'OK', 'Error')
        return
    }
    $script:RootDN = $script:Domain.DistinguishedName
    $script:CurrentDN = $script:RootDN
    Exit-Busy

    Build-UI
    Build-OuTree
    Set-Position $script:RootDN
    Update-List -DN $script:RootDN -Mode 'Users' -Query ''

    [void]$script:Frm.ShowDialog()
}

Start-ADConsole



