# ============================================================
#  ADConsole (WPF) 逻辑层自动化测试
#  加载编译产物 ADConsole.exe，对纯函数做断言。
#  不触碰真实 AD，不执行任何写操作。
# ============================================================
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ErrorActionPreference = 'Stop'
$projectDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'ADConsoleWpf'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) 'ADConsole.Tests'
if (-not (Test-Path $temp)) { New-Item -ItemType Directory -Path $temp | Out-Null }

$script:Pass = 0
$script:Fail = 0
$script:Failures = New-Object System.Collections.ArrayList

function Assert-True {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        $script:Pass++
        Write-Host "  [PASS] $Name" -ForegroundColor Green
    }
    else {
        $script:Fail++
        [void]$script:Failures.Add("$Name  $Detail")
        Write-Host "  [FAIL] $Name  $Detail" -ForegroundColor Red
    }
}

function Assert-Eq {
    param([string]$Name, $Actual, $Expected)
    Assert-True $Name ($Actual -eq $Expected) "(实际='$Actual' 期望='$Expected')"
}

function Assert-Null {
    param([string]$Name, $Actual)
    Assert-True $Name ($null -eq $Actual) "(实际='$Actual' 期望=null)"
}

function Assert-NotNull {
    param([string]$Name, $Actual)
    Assert-True $Name ($null -ne $Actual) "(实际=null 期望=非null)"
}

# ---- 列表构造辅助 ----
# 注意 1：不能用 $args 收集行，PowerShell 会把嵌套数组打平成多个参数。
# 注意 2：函数 return 集合会被管道枚举，空集合会变成 $null，
#         必须用 Write-Output -NoEnumerate 原样传出。
function New-Headers {
    param([string[]]$Names)
    $list = New-Object 'System.Collections.Generic.List[string]'
    $list.AddRange($Names)
    Write-Output -NoEnumerate $list
}

function New-Data {
    $list = New-Object 'System.Collections.Generic.List[string[]]'
    Write-Output -NoEnumerate $list
}

function Add-Data {
    param($List, [string[]]$Cells)
    if ($null -eq $List) { throw 'Add-Data: 目标列表为 null（New-Data 未正确返回）' }
    $List.Add($Cells)
}

# ---------------- 加载被测代码 ----------------
Write-Host "`n=== 加载被测代码 ===" -ForegroundColor Cyan

$exePath = Join-Path $projectDir "bin\Release\ADConsole.exe"
if (-not (Test-Path $exePath)) { throw "未找到 $exePath，请先运行 ADConsoleWpf\build.ps1 -Rebuild" }
# PS 5.1 的 Add-Type -Path 不接受 .exe，改用 Assembly.LoadFrom
[void][System.Reflection.Assembly]::LoadFrom($exePath)
Write-Host "  已加载 $exePath"

$Ad  = [ADConsole.Services.AdService]
$Csv = [ADConsole.Services.CsvHelper]
$Err = [ADConsole.Services.AdError]

# ---------------- 1. sAMAccountName 校验 ----------------
Write-Host "`n=== 1. sAMAccountName 校验 ===" -ForegroundColor Cyan

Assert-NotNull "空登录名被拒"        ($Ad::ValidateSam($null))
Assert-NotNull "空串登录名被拒"      ($Ad::ValidateSam(''))
Assert-NotNull "21字符被拒"          ($Ad::ValidateSam('a' * 21))
Assert-Null    "20字符通过"          ($Ad::ValidateSam('a' * 20))
Assert-Null    "1字符通过"           ($Ad::ValidateSam('a'))
Assert-NotNull "含@被拒"             ($Ad::ValidateSam('user@1'))
Assert-NotNull "含/被拒"             ($Ad::ValidateSam('user/1'))
Assert-NotNull "含[]被拒"            ($Ad::ValidateSam('user[1]'))
Assert-NotNull "含冒号被拒"          ($Ad::ValidateSam('user:1'))
Assert-NotNull "含空格被拒"          ($Ad::ValidateSam('user 1'))
Assert-NotNull "点开头被拒"          ($Ad::ValidateSam('.abc'))
Assert-NotNull "点结尾被拒"          ($Ad::ValidateSam('abc.'))
Assert-Null    "带点的登录名通过"    ($Ad::ValidateSam('zhang.san'))
Assert-Null    "带下划线通过"        ($Ad::ValidateSam('zhang_san01'))
Assert-Null    "纯数字通过"          ($Ad::ValidateSam('10001'))
Assert-Null    "中文名通过(AD允许)"  ($Ad::ValidateSam('张三'))
# AD 只禁 ASCII 那 11 个字符，全角逗号不在受限列表内
Assert-Null    "全角逗号属合法字符"  ($Ad::ValidateSam('user，1'))
Assert-NotNull "半角逗号被拒"        ($Ad::ValidateSam('user,1'))

# ---------------- 2. 密码校验 ----------------
Write-Host "`n=== 2. 密码校验 ===" -ForegroundColor Cyan

Assert-NotNull "空密码被拒"   ($Ad::ValidatePassword($null))
Assert-NotNull "7位密码被拒"  ($Ad::ValidatePassword('a' * 7))
Assert-Null    "8位密码通过"  ($Ad::ValidatePassword('a' * 8))
Assert-Null    "16位密码通过" ($Ad::ValidatePassword('a' * 16))

# ---------------- 3. LDAP / RDN 转义 ----------------
Write-Host "`n=== 3. LDAP 转义 ===" -ForegroundColor Cyan

Assert-Eq "转义星号"     ($Ad::EscapeLdap('a*b'))      'a\2ab'
Assert-Eq "转义左括号"   ($Ad::EscapeLdap('a(b'))      'a\28b'
Assert-Eq "转义右括号"   ($Ad::EscapeLdap('a)b'))      'a\29b'
Assert-Eq "转义反斜杠"   ($Ad::EscapeLdap('a\b'))      'a\5cb'
Assert-Eq "转义斜杠"     ($Ad::EscapeLdap('a/b'))      'a\2fb'
Assert-Eq "多特殊字符"   ($Ad::EscapeLdap('a*b(c)'))   'a\2ab\28c\29'
Assert-Eq "尾部字符保留" ($Ad::EscapeLdap('x*'))       'x\2a'
Assert-Eq "普通字符不变" ($Ad::EscapeLdap('张三_01'))  '张三_01'
Assert-Eq "空值返回空"   ($Ad::EscapeLdap(''))         ''

Write-Host "  --- RDN 转义 ---" -ForegroundColor DarkGray
Assert-Eq "RDN 转义逗号"   ($Ad::EscapeRdn('Smith, John')) 'Smith\, John'
Assert-Eq "RDN 转义加号"   ($Ad::EscapeRdn('a+b'))         'a\+b'
Assert-Eq "RDN 转义引号"   ($Ad::EscapeRdn('a"b'))         'a\"b'
Assert-Eq "RDN 转义反斜杠" ($Ad::EscapeRdn('a\b'))         'a\\b'
Assert-Eq "RDN 转义等号"   ($Ad::EscapeRdn('a=b'))         'a\=b'
Assert-Eq "RDN 无特殊字符" ($Ad::EscapeRdn('张三'))        '张三'

# ---------------- 4. DN 剥离 RDN ----------------
Write-Host "`n=== 4. DN 剥离 RDN ===" -ForegroundColor Cyan

Assert-Eq "普通DN"         ($Ad::RemoveFirstRdn('CN=Users,DC=a,DC=cn'))    'DC=a,DC=cn'
Assert-Eq "三层DN"         ($Ad::RemoveFirstRdn('OU=IT,OU=HQ,DC=a,DC=cn')) 'OU=HQ,DC=a,DC=cn'
Assert-Eq "转义逗号"       ($Ad::RemoveFirstRdn('CN=Smith\, John,OU=IT,DC=a')) 'OU=IT,DC=a'
Assert-Eq "转义反斜杠"     ($Ad::RemoveFirstRdn('CN=a\b,DC=a'))            'DC=a'
Assert-Null "单段DN返回空" ($Ad::RemoveFirstRdn('DC=cn'))
Assert-Null "null 安全"    ($Ad::RemoveFirstRdn($null))

# ---------------- 5. 组作用域与类别 ----------------
Write-Host "`n=== 5. 组作用域与类别 ===" -ForegroundColor Cyan

Assert-Eq "全局组"            ($Ad::DescribeGroupScope(0x1))            '全局'
Assert-Eq "域本地组"          ($Ad::DescribeGroupScope(0x2))            '域本地'
Assert-Eq "通用组"            ($Ad::DescribeGroupScope(0x8))            '通用'
Assert-Eq "全局安全组作用域"  ($Ad::DescribeGroupScope(0x80000001L))    '全局'
Assert-Eq "域本地安全组作用域" ($Ad::DescribeGroupScope(0x80000002L))   '域本地'
Assert-Eq "通用安全组作用域"  ($Ad::DescribeGroupScope(0x80000008L))    '通用'
Assert-Eq "全局通讯组作用域"  ($Ad::DescribeGroupScope(0x1))            '全局'
Assert-Eq "无作用域位"        ($Ad::DescribeGroupScope(0x80000000L))    '未知'

Assert-Eq "安全组"       ($Ad::DescribeGroupCategory(0x80000001L)) '安全组'
Assert-Eq "通讯组"       ($Ad::DescribeGroupCategory(0x1))         '通讯组'
Assert-Eq "全局通讯组"   ($Ad::DescribeGroupCategory(0x1))         '通讯组'
Assert-Eq "域本地安全组" ($Ad::DescribeGroupCategory(0x80000002L)) '安全组'

# ---------------- 6. 4624 登录类型 ----------------
Write-Host "`n=== 6. 4624 登录类型映射 ===" -ForegroundColor Cyan

Assert-Eq "2 交互式"   ($Ad::DescribeLogonType(2))  '交互式'
Assert-Eq "3 网络"     ($Ad::DescribeLogonType(3))  '网络'
Assert-Eq "4 批处理"   ($Ad::DescribeLogonType(4))  '批处理'
Assert-Eq "5 服务"     ($Ad::DescribeLogonType(5))  '服务'
Assert-Eq "7 解锁"     ($Ad::DescribeLogonType(7))  '解锁'
Assert-Eq "8 网络明文" ($Ad::DescribeLogonType(8))  '网络明文'
Assert-Eq "9 新凭据"   ($Ad::DescribeLogonType(9))  '新凭据'
Assert-Eq "10 RDP"     ($Ad::DescribeLogonType(10)) '远程交互 (RDP)'
Assert-Eq "11 缓存"    ($Ad::DescribeLogonType(11)) '缓存凭据'
Assert-Eq "未知类型"   ($Ad::DescribeLogonType(99)) '类型99'

# ---------------- 7. 密码到期计算 ----------------
Write-Host "`n=== 7. 密码到期计算 ===" -ForegroundColor Cyan
$now = Get-Date

$r = $Ad::ComputeExpiry('张三','zhangsan','OU=IT,DC=a,DC=cn', $now, $now.AddDays(10), $false, $true)
Assert-Eq "剩余10天状态"     $r.Status '即将到期'
Assert-True "剩余10天数值"   (($r.DaysLeft -ge 9) -and ($r.DaysLeft -le 10)) "(实际=$($r.DaysLeft))"
Assert-Eq "所在OU显示完整DN" $r.OuDisplay 'OU=IT,DC=a,DC=cn'
Assert-Eq "到期时间格式化"   $r.ExpireDateText ($now.AddDays(10).ToString('yyyy-MM-dd HH:mm'))

$r = $Ad::ComputeExpiry('李四','lisi','OU=IT,DC=a,DC=cn', $now, $now.AddDays(-3), $false, $true)
Assert-Eq "过期状态"       $r.Status '已过期'
Assert-True "过期天数为负" ($r.DaysLeft -lt 0) "(实际=$($r.DaysLeft))"
Assert-True "过期剩余天数文本含'已过期'" ($r.DaysLeftText -like '已过期*') "(实际=$($r.DaysLeftText))"

$r = $Ad::ComputeExpiry('王五','wangwu','OU=IT,DC=a,DC=cn', $now, $null, $true, $true)
Assert-Eq "永不过期状态"         $r.Status '永不过期'
Assert-Null "永不过期无剩余天数" $r.DaysLeft
Assert-Eq "永不过期到期列显示破折号" $r.ExpireDateText '—'

$r = $Ad::ComputeExpiry('赵六','zhaoliu','OU=IT,DC=a,DC=cn', $null, $null, $false, $true)
Assert-Eq "从未设置状态"           $r.Status '从未设置密码'
Assert-Null "从未设置无到期日"     $r.ExpireDate
Assert-Eq "从未设置剩余天数为破折号" $r.DaysLeftText '—'

$r = $Ad::ComputeExpiry('钱七','qianqi','OU=IT,DC=a,DC=cn', $now, $now.AddDays(3), $false, $false)
Assert-Eq "已禁用优先显示" $r.Status '已禁用'

$r = $Ad::ComputeExpiry('孙八','sunba','OU=IT,DC=a,DC=cn', $now, $now.AddHours(1), $false, $true)
Assert-Eq "今日到期" $r.Status '今日到期'

$r = $Ad::ComputeExpiry('周九','zhoujiu','OU=IT,DC=a,DC=cn', $now, $now.AddDays(10), $false, $true)
Assert-True "正常剩余天数文本为'N 天'" ($r.DaysLeftText -match '^\d+ 天$') "(实际=$($r.DaysLeftText))"

# ---------------- 8. AD 错误翻译 ----------------
Write-Host "`n=== 8. AD 错误翻译 ===" -ForegroundColor Cyan

$hrCases = [ordered]@{
    '0x80072023' = '拒绝访问'
    '0x8007202F' = '违反密码策略'
    '0x8007052D' = '违反密码策略'
    '0x80072035' = '约束违规'
    '0x8007200D' = '对象不存在'
    '0x8007200E' = '命名冲突'
    '0x8007200A' = '无效的命名语法'
    '0x8007054C' = '登录名不可用'
    '0x80070005' = '拒绝访问'
}
foreach ($hrHex in $hrCases.Keys) {
    $hr = [Convert]::ToInt32($hrHex, 16)
    $ex = New-Object System.Runtime.InteropServices.COMException 'test', $hr
    $msg = $Err::Describe($ex)
    Assert-True "$hrHex 映射到友好提示" ($msg -like "*$($hrCases[$hrHex])*") "(实际='$msg')"
    Assert-True "$hrHex 不含原始英文消息" (-not $msg.Contains('test')) "(实际='$msg')"
}

Assert-Eq "未知异常回退到原始消息" ($Err::Describe((New-Object Exception('some failure')))) 'some failure'
Assert-Eq "null 异常"              ($Err::Describe($null)) '未知错误'

# DirectoryServicesCOMException 继承自 COMException，
# 我们的代码正是靠这个判断来取 HResult 的。
$dsEx = New-Object System.DirectoryServices.DirectoryServicesCOMException 'adsi failure'
Assert-True "DirectoryServicesCOMException 属于 COMException" ($dsEx -is [System.Runtime.InteropServices.COMException]) ""

# 注入真实 ADSI 错误码后，应走同一套 HResult 映射
$hrField = [System.Exception].GetField('_HResult', [System.Reflection.BindingFlags]'Instance,NonPublic')
if ($hrField) {
    $hrField.SetValue($dsEx, [Convert]::ToInt32('0x80072023', 16))
    $dsMsg = $Err::Describe($dsEx)
    Assert-True "DirectoryServicesCOMException 也走 HResult 映射" ($dsMsg -like '*拒绝访问*') "(实际='$dsMsg')"
}
else {
    Write-Host "  [SKIP] 取不到 Exception._HResult 私有字段" -ForegroundColor Yellow
}

# ---------------- 9. CSV 单行解析 ----------------
Write-Host "`n=== 9. CSV 单行解析 ===" -ForegroundColor Cyan

Assert-Eq "简单三列"       (($Csv::ParseLine('a,b,c') -join '|')) 'a|b|c'
Assert-Eq "带引号含逗号"   (($Csv::ParseLine('a,"b,c",d') -join '|')) 'a|b,c|d'
Assert-Eq "引号内转义引号" (($Csv::ParseLine('a,"b""c",d') -join '|')) 'a|b"c|d'
Assert-Eq "去除首尾空格"   (($Csv::ParseLine(' a , b ') -join '|')) 'a|b'
Assert-Eq "空行一个空单元格" (($Csv::ParseLine('') -join '|')) ''
Assert-Eq "末尾空列保留"   (($Csv::ParseLine('a,b,') -join '|')) 'a|b|'
Assert-Eq "引号包裹整列"   (($Csv::ParseLine('"hello",world') -join '|')) 'hello|world'
Assert-Eq "中文含逗号引号" (($Csv::ParseLine('"李,四",x') -join '|')) '李,四|x'

# ---------------- 10. CSV 表头映射与行校验 ----------------
Write-Host "`n=== 10. CSV 表头映射与行校验 ===" -ForegroundColor Cyan

# 10.1 英文标准表头
$h = New-Headers @('SamAccountName','Name','Password','OU','Department','Title','Email','Phone','Description')
$d = New-Data
Add-Data $d @('zhangsan','张三','P@ssw0rd!234','OU=IT,DC=a,DC=cn','研发','工程师','a@b.cn','13800000000','备注')
$r = $Csv::MapRows($h, $d, 'DC=a,DC=cn', $null)
Assert-Eq "英文表头行数"     $r.Count 1
Assert-Eq "英文表头首行单元格数" $d[0].Count 9
Assert-Eq "英文表头登录名"   $r[0].SamAccountName 'zhangsan'
Assert-Eq "英文表头姓名"     $r[0].Name '张三'
Assert-Eq "英文表头密码"     $r[0].Password 'P@ssw0rd!234'
Assert-Eq "英文表头OU"       $r[0].OuDn 'OU=IT,DC=a,DC=cn'
Assert-Eq "英文表头部门"     $r[0].Department '研发'
Assert-Eq "英文表头职位"     $r[0].Title '工程师'
Assert-Eq "英文表头邮箱"     $r[0].Email 'a@b.cn'
Assert-Eq "英文表头电话"     $r[0].Phone '13800000000'
Assert-Eq "英文表头说明"     $r[0].Description '备注'
Assert-True "合法行通过校验" $r[0].IsValid "(错误=$($r[0].Error))"
Assert-Null "合法行无错误"   $r[0].Error
Assert-Eq "合法行校验文本为通过" $r[0].ValidationText '通过'

# 10.2 表头别名：Email 本身必须能识别（最常见列名）
$hEmail = New-Headers @('Email')
$dEmail = New-Data
Add-Data $dEmail @('only@b.cn')
$rEmail = $Csv::MapRows($hEmail, $dEmail, 'DC=a,DC=cn', $null)
Assert-Eq "表头 Email 本身可识别" $rEmail[0].Email 'only@b.cn'

# 10.3 中文表头
$h2 = New-Headers @('登录名','姓名','密码','所在OU','部门','职位','邮箱','电话','说明')
$d2 = New-Data
Add-Data $d2 @('lisi','李四','P@ssw0rd!234','OU=IT,DC=a,DC=cn','财务','会计','c@d.cn','13900000000','x')
$r2 = $Csv::MapRows($h2, $d2, 'DC=a,DC=cn', $null)
Assert-Eq "中文表头登录名" $r2[0].SamAccountName 'lisi'
Assert-Eq "中文表头姓名"   $r2[0].Name '李四'
Assert-Eq "中文表头密码"   $r2[0].Password 'P@ssw0rd!234'
Assert-Eq "中文表头OU"     $r2[0].OuDn 'OU=IT,DC=a,DC=cn'
Assert-Eq "中文表头邮箱"   $r2[0].Email 'c@d.cn'
Assert-Eq "中文表头电话"   $r2[0].Phone '13900000000'
Assert-Eq "中文表头说明"   $r2[0].Description 'x'

# 10.4 大小写混排 + 别名
$h3 = New-Headers @('samaccountname','DisplayName','PWD','Path','DEPARTMENT','jobtitle','mail','phone','备注')
$d3 = New-Data
Add-Data $d3 @('wangwu','王五','P@ssw0rd!234','OU=IT,DC=a,DC=cn','采购','主管','e@f.cn','13700000000','y')
$r3 = $Csv::MapRows($h3, $d3, 'DC=a,DC=cn', $null)
Assert-Eq "小写 samaccountname"    $r3[0].SamAccountName 'wangwu'
Assert-Eq "别名 DisplayName 作姓名" $r3[0].Name '王五'
Assert-Eq "别名 PWD"               $r3[0].Password 'P@ssw0rd!234'
Assert-Eq "别名 Path 映射 OU"      $r3[0].OuDn 'OU=IT,DC=a,DC=cn'
Assert-Eq "别名 jobtitle"          $r3[0].Title '主管'
Assert-Eq "别名 Mail 映射邮箱"     $r3[0].Email 'e@f.cn'
Assert-Eq "别名备注"               $r3[0].Description 'y'

# 10.5 缺省值回退
$h4 = New-Headers @('SamAccountName','Name','Password')
$d4 = New-Data
Add-Data $d4 @('zhaoliu','赵六','P@ssw0rd!234')
$r4 = $Csv::MapRows($h4, $d4, 'OU=默认,DC=a,DC=cn', $null)
Assert-Eq "缺省OU回退到默认值" $r4[0].OuDn 'OU=默认,DC=a,DC=cn'

$d4b = New-Data
Add-Data $d4b @('qianqi','','P@ssw0rd!234')
$r4b = $Csv::MapRows($h4, $d4b, 'DC=a,DC=cn', $null)
Assert-Eq "缺省姓名回退到登录名" $r4b[0].Name 'qianqi'

# 10.6 各类校验失败
$d5 = New-Data
Add-Data $d5 (('a' * 21), '张三', 'P@ssw0rd!234')
Add-Data $d5 @('ok_user', '', 'P@ssw0rd!234')
Add-Data $d5 @('ok_user2', '李四', 'short')
Add-Data $d5 @('', '王五', 'P@ssw0rd!234')
Add-Data $d5 @('bad@name', '赵六', 'P@ssw0rd!234')
$r5 = $Csv::MapRows($h4, $d5, 'DC=a,DC=cn', $null)
Assert-Eq "校验输入为 5 行" $d5.Count 5
Assert-Eq "校验输出为 5 行" $r5.Count 5
Assert-True "超长登录名被拒"    ($r5[0].Error -like '*20*')    "(错误=$($r5[0].Error))"
Assert-NotNull "密码过短被拒"   $r5[2].Error
Assert-True "密码过短提示含8"   ($r5[2].Error -like '*8*')     "(错误=$($r5[2].Error))"
Assert-NotNull "登录名为空被拒" $r5[3].Error
Assert-NotNull "非法字符被拒"   $r5[4].Error
Assert-Eq "五行中四行不合法" (($r5 | Where-Object { -not $_.IsValid }).Count) 4
# 姓名留空是设计上的便利：自动用登录名回填（AD 的 CN 不能为空）
Assert-True "姓名为空时自动回填登录名" ($r5[1].Name -eq 'ok_user') "(实际='$($r5[1].Name)')"
Assert-True "回填后该行合法" $r5[1].IsValid "(错误=$($r5[1].Error))"

# 10.7 行号从第 2 行开始（表头占 1 行）
Assert-Eq "首数据行号为2"   $r5[0].LineNo 2
Assert-Eq "第二数据行号为3" $r5[1].LineNo 3
Assert-Eq "第五数据行号为6" $r5[4].LineNo 6

# 10.8 重复登录名检查（委托）
$dup = [Func[string, bool]] { param($sam) return ($sam -eq 'existing') }
$d7 = New-Data
Add-Data $d7 @('existing','张三','P@ssw0rd!234')
Add-Data $d7 @('newuser','李四','P@ssw0rd!234')
$r7 = $Csv::MapRows($h4, $d7, 'DC=a,DC=cn', $dup)
Assert-True "已存在登录名被拒" ($r7[0].Error -like '*已存在*') "(错误=$($r7[0].Error))"
Assert-True "新登录名通过"     $r7[1].IsValid "(错误=$($r7[1].Error))"

# 10.9 姓名含逗号（已由 CSV 解析阶段保护）
$h8 = New-Headers @('SamAccountName','Name','Password')
$d8 = New-Data
Add-Data $d8 @('commauser','李,四','P@ssw0rd!234')
$r8 = $Csv::MapRows($h8, $d8, 'DC=a,DC=cn', $null)
Assert-Eq "含逗号的姓名保留完整" $r8[0].Name '李,四'

# ---------------- 11. CSV 读表与编码识别 ----------------
Write-Host "`n=== 11. CSV 读表与编码识别 ===" -ForegroundColor Cyan

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$utf8Bom   = New-Object System.Text.UTF8Encoding($true)
$gbk       = [System.Text.Encoding]::GetEncoding(936)

# 11.1 普通 UTF-8，引号内含逗号
$p1 = Join-Path $temp 'test-import.csv'
[System.IO.File]::WriteAllText($p1,
    "SamAccountName,Name,Password`r`nuser1,`"张三`",P@ssw0rd!234`r`nuser2,`"李,四`",P@ssw0rd!5678`r`n",
    $utf8NoBom)
$t1 = $Csv::ReadTable($p1, $utf8NoBom)
Assert-Eq "读到 3 行" $t1.Count 3
Assert-Eq "表头三列"  $t1[0].Count 3
Assert-Eq "含逗号的姓名不拆列" $t1[2][1] '李,四'
Assert-Eq "第二行登录名" $t1[2][0] 'user2'

# 11.2 GBK 编码 CSV（表头中文）
$p2 = Join-Path $temp 'test-import-gbk.csv'
[System.IO.File]::WriteAllText($p2, "登录名,姓名,密码`r`nlisi,李四,P@ssw0rd!234`r`n", $gbk)
$rowsRef = $null
$h2b = $Csv::ParseWithEncodingAuto($p2, [ref]$rowsRef)
Assert-Eq "GBK 自动识别表头列数" $h2b.Count 3
Assert-Eq "GBK 表头首列不乱码"   $h2b[0] '登录名'
Assert-Eq "GBK 数据行数"         $rowsRef.Count 1
Assert-Eq "GBK 中文不乱码"       $rowsRef[0][1] '李四'

# 11.3 UTF-8 带 BOM
$p3 = Join-Path $temp 'test-import-bom.csv'
[System.IO.File]::WriteAllText($p3,
    "SamAccountName,Name,Password`r`nzhangsan,张三,P@ssw0rd!234`r`n", $utf8Bom)
$rowsRef3 = $null
$h3b = $Csv::ParseWithEncodingAuto($p3, [ref]$rowsRef3)
Assert-Eq "BOM 表头列数"      $h3b.Count 3
Assert-Eq "BOM 表头无残留BOM" $h3b[0] 'SamAccountName'
Assert-Eq "BOM 数据行数"      $rowsRef3.Count 1

# 11.4 无已知表头时应识别为"无法识别"而非误用
$p4 = Join-Path $temp 'test-import-bad.csv'
[System.IO.File]::WriteAllText($p4, "foo,bar`r`n1,2`r`n", $utf8NoBom)
$rowsRef4 = $null
$h4b = $Csv::ParseWithEncodingAuto($p4, [ref]$rowsRef4)
Assert-Eq "无已知表头时返回空表头" $h4b.Count 0

# ---------------- 12. CSV 写出 ----------------
Write-Host "`n=== 12. CSV 写出 ===" -ForegroundColor Cyan

$outCsv = Join-Path $temp 'test-export.csv'
$oh = New-Headers @('列A','列B')
$orows = New-Object 'System.Collections.Generic.List[System.Collections.Generic.List[string]]'
$inner1 = New-Object 'System.Collections.Generic.List[string]'; $inner1.AddRange([string[]]@('简单','值'))
$inner2 = New-Object 'System.Collections.Generic.List[string]'; $inner2.AddRange([string[]]@('含,逗号','含"引号'))
$orows.Add($inner1); $orows.Add($inner2)
$Csv::WriteCsv($outCsv, $oh, $orows)

$bytes = [System.IO.File]::ReadAllBytes($outCsv)
Assert-True "导出文件带 UTF-8 BOM" (($bytes[0] -eq 0xEF) -and ($bytes[1] -eq 0xBB) -and ($bytes[2] -eq 0xBF))
$exported = [System.IO.File]::ReadAllText($outCsv, $utf8NoBom)
Assert-True "含表头" ($exported -like '列A,列B*') ""
Assert-True "含逗号的单元格被引号包裹" ($exported -like '*"含,逗号"*') ""
Assert-True "含引号的单元格被转义" ($exported -like '*含""引号*') ""

$back = $Csv::ReadTable($outCsv, $utf8NoBom)
Assert-Eq "往返后 3 行" $back.Count 3
Assert-Eq "往返后含逗号的值" $back[2][0] '含,逗号'
Assert-Eq "往返后含引号的值" $back[2][1] '含"引号'

# ---------------- 13. 强密码生成 ----------------
Write-Host "`n=== 13. 强密码生成 ===" -ForegroundColor Cyan

$distinct = New-Object System.Collections.Generic.HashSet[string]
$badLen = 0; $badSpace = 0
for ($i = 0; $i -lt 300; $i++) {
    $p = [ADConsole.MainWindow]::GeneratePassword(16)
    if ($p.Length -ne 16) { $badLen++ }
    if ($p -match '\s')    { $badSpace++ }
    [void]$distinct.Add($p)
}
Assert-Eq "300 次生成长度均为 16"  $badLen 0
Assert-Eq "300 次生成均无空白字符" $badSpace 0
Assert-True "300 次生成全部互不相同" ($distinct.Count -eq 300) "(去重后 $($distinct.Count))"

$hasUpper = $false; $hasLower = $false; $hasDigit = $false; $hasSpecial = $false
foreach ($ch in [ADConsole.MainWindow]::GeneratePassword(16).ToCharArray()) {
    if ($ch -cmatch '[A-Z]') { $hasUpper = $true }
    if ($ch -cmatch '[a-z]') { $hasLower = $true }
    if ($ch -match '\d')     { $hasDigit = $true }
    if ($ch -match '[!@#$%^&*?\-_=+]') { $hasSpecial = $true }
}
Assert-True "含大写"     $hasUpper ""
Assert-True "含小写"     $hasLower ""
Assert-True "含数字"     $hasDigit ""
Assert-True "含特殊字符" $hasSpecial ""

# 12 位密码也应满足四类字符
$short = [ADConsole.MainWindow]::GeneratePassword(12)
$u = $false; $l = $false; $dg = $false; $s = $false
foreach ($ch in $short.ToCharArray()) {
    if ($ch -cmatch '[A-Z]') { $u = $true }
    if ($ch -cmatch '[a-z]') { $l = $true }
    if ($ch -match '\d')     { $dg = $true }
    if ($ch -match '[!@#$%^&*?\-_=+]') { $s = $true }
}
Assert-Eq "12位密码长度正确" $short.Length 12
Assert-True "12位密码四类字符齐全" ($u -and $l -and $dg -and $s) "($short)"

# ---------------- 14. 凭据存储（DPAPI 往返） ----------------
Write-Host "`n=== 14. 凭据存储 (DPAPI) ===" -ForegroundColor Cyan
$credPath = Join-Path $env:APPDATA 'ADConsole\credential.dat'
$Cs = [ADConsole.Services.CredentialStore]

if (Test-Path $credPath) {
    Write-Host "  [SKIP] 已存在真实凭据文件，跳过以免覆盖" -ForegroundColor Yellow
}
else {
    $c = New-Object ADConsole.Services.StoredCredential
    $c.Domain = 'example.com'; $c.UserName = 'testuser'
    $c.Password = 'S3cret!P@ssw0rd'; $c.RememberPassword = $true; $c.AutoLogin = $true
    $Cs::Save($c)

    Assert-True "凭据文件已写入" (Test-Path $credPath) ""
    if (Test-Path $credPath) {
        $raw = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($credPath))
        Assert-True "磁盘上是密文（不含明文密码）" (-not $raw.Contains('S3cret!P@ssw0rd')) ""
        Assert-True "磁盘上不含明文用户名"         (-not $raw.Contains('testuser')) ""
    }

    $loaded = $Cs::Load()
    Assert-NotNull "读回凭据" $loaded
    if ($loaded) {
        Assert-Eq "域名往返"   $loaded.Domain 'example.com'
        Assert-Eq "用户名往返" $loaded.UserName 'testuser'
        Assert-Eq "密码往返"   $loaded.Password 'S3cret!P@ssw0rd'
        Assert-True "记住密码标记往返" $loaded.RememberPassword ""
        Assert-True "自动登录标记往返" $loaded.AutoLogin ""
    }

    # 未勾选"记住密码"时，磁盘上不应出现密码
    $c2 = New-Object ADConsole.Services.StoredCredential
    $c2.Domain = 'x.cn'; $c2.UserName = 'u'; $c2.Password = 'PlainText!123'
    $c2.RememberPassword = $false; $c2.AutoLogin = $false
    $Cs::Save($c2)
    $raw2 = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($credPath))
    Assert-True "未勾选记住密码时不落盘密码" (-not $raw2.Contains('PlainText!123')) ""
    $l2 = $Cs::Load()
    Assert-Eq "未记住时密码为空" $l2.Password ''
    Assert-True "未记住时记住标记为 false" (-not $l2.RememberPassword) ""

    # 损坏文件不应抛异常
    [System.IO.File]::WriteAllText($credPath, 'not-a-valid-cipher', $utf8NoBom)
    Assert-Null "损坏的凭据文件返回 null（不抛异常）" $Cs::Load()

    $Cs::Delete()
    Assert-True "凭据文件已删除" (-not (Test-Path $credPath)) ""
    Assert-Null "删除后再读返回 null" $Cs::Load()
}

# ---------------- 15. 权限等级描述 ----------------
Write-Host "`n=== 15. 权限等级描述 ===" -ForegroundColor Cyan
$Ps = [ADConsole.Services.PermissionService]
$Lv = [ADConsole.Services.PermissionLevel]

Assert-Eq "只读短名"   $Ps::ShortName($Lv::ReadOnly) '只读'
Assert-Eq "委派短名"   $Ps::ShortName($Lv::Delegated) '可写（委派）'
Assert-Eq "管理员短名" $Ps::ShortName($Lv::DomainAdmin) '域管理员'
Assert-True "只读完整描述含'只读'"         ($Ps::Describe($Lv::ReadOnly) -like '*只读*') ""
Assert-True "管理员完整描述含'所有写操作'" ($Ps::Describe($Lv::DomainAdmin) -like '*所有写操作*') ""
Assert-True "委派完整描述含'写操作'"       ($Ps::Describe($Lv::Delegated) -like '*写操作*') ""

# 凭据校验：空值不抛异常且返回 false
Assert-True "空域名校验返回 false"   (-not $Ps::ValidateCredential('', 'u', 'p')) ""
Assert-True "空用户名校验返回 false" (-not $Ps::ValidateCredential('x.cn', '', 'p')) ""
Assert-True "空密码校验返回 false"   (-not $Ps::ValidateCredential('x.cn', 'u', '')) ""
Assert-True "全空校验返回 false"     (-not $Ps::ValidateCredential($null, $null, $null)) ""

# ---------------- 16. 模型与枚举 ----------------
Write-Host "`n=== 16. 模型与枚举 ===" -ForegroundColor Cyan

$o = New-Object ADConsole.Services.AdObject
Assert-True "AdObject 默认已启用" $o.Enabled ""
Assert-Null "AdObject 默认显示名回退为空" $o.DisplayNameOrName
$o.DisplayName = '张三'
Assert-Eq "优先用 DisplayName" $o.DisplayNameOrName '张三'
$o.DisplayName = ' '
$o.Name = 'zhangsan'
Assert-Eq "空 DisplayName 回退到 Name" $o.DisplayNameOrName 'zhangsan'

$u = New-Object ADConsole.Services.AdObject
$u.Kind = [ADConsole.Services.AdKind]::User
$u.Enabled = $false
Assert-Eq "禁用用户状态列" $u.StatusText '已禁用'
$u.Enabled = $true
Assert-Eq "启用用户状态列" $u.StatusText '正常'
$u.LockedOut = $true
Assert-Eq "锁定用户状态列" $u.StatusText '已锁定'

$cc = New-Object ADConsole.Services.AdObject
$cc.Kind = [ADConsole.Services.AdKind]::Computer
$cc.Enabled = $false
Assert-Eq "禁用计算机状态列" $cc.StatusText '已禁用'
$cc.Enabled = $true
Assert-Eq "启用计算机状态列" $cc.StatusText '已启用'

$g = New-Object ADConsole.Services.AdObject
$g.Kind = [ADConsole.Services.AdKind]::Group
$g.GroupCategory = '安全组'
Assert-Eq "组状态列显示类别" $g.StatusText '安全组'

$ou = New-Object ADConsole.Services.AdObject
$ou.Kind = [ADConsole.Services.AdKind]::OrganizationalUnit
Assert-Eq "OU 状态列" $ou.StatusText 'OU'
$ou.Kind = [ADConsole.Services.AdKind]::Domain
Assert-Eq "域状态列" $ou.StatusText '域'
$ou.Kind = [ADConsole.Services.AdKind]::Container
Assert-Eq "容器状态列" $ou.StatusText '容器'

$t = New-Object ADConsole.Services.OuTreeNode
Assert-NotNull "OuTreeNode.Children 已初始化" $t.Children
Assert-Eq "Children 初始为空" $t.Children.Count 0
$t.Title = '研发中心'
Assert-Eq "ToString 返回 Title" $t.ToString() '研发中心'

# AdError 的回退顺序：先取 Message，取不到才用类型名
Assert-Eq "空消息异常回退到类型名" ($Err::Describe((New-Object System.Exception ''))) 'Exception'
Assert-True "默认消息的异常返回其消息" (($Err::Describe((New-Object System.Exception))) -like '*System.Exception*') ""

# ---------------- 汇总 ----------------
Write-Host "`n" + ('=' * 60) -ForegroundColor Cyan
$total = $script:Pass + $script:Fail
Write-Host ("通过 {0} / {1}    失败 {2}" -f $script:Pass, $total, $script:Fail) -ForegroundColor Cyan
if ($script:Fail -gt 0) {
    Write-Host "`n失败项：" -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "`n全部通过。" -ForegroundColor Green
exit 0
