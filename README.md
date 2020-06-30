# AD 域控管理控制台（WPF 客户端）

基于 **.NET Framework 4.8 + WPF + System.DirectoryServices（纯 LDAP/ADSI）** 的 Active Directory
桌面管理客户端。无需安装 RSAT，无需 Visual Studio，编译后为单个 `ADConsole.exe`。

> 本项目现在只维护 WPF 客户端。旧的 PowerShell/WinForms 版本已从仓库移除，
> 如仍需查阅，可在 Git 历史中找回。

## 功能

- **登录与凭据**
  - 支持域服务器地址、IP、PDC 主机名
  - 「记住密码」「自动登录」
  - 凭据使用 Windows DPAPI（CurrentUser）加密保存，换账户或换机器无法解密
- **目录浏览**
  - OU 树：域根、OU、常用容器，节点带图标
  - 用户 / 组 / 计算机列表，支持关键字搜索与递归查询
  - 属性面板：DN、状态、部门、职位、邮箱、电话、密码时间、最后登录等
- **用户管理**
  - 新建用户、编辑属性（部门 / 职位 / 邮箱 / 电话 / 说明）
  - 重置密码（可生成 16 位强密码）
  - 启用 / 禁用 / 解锁 / 删除
- **组管理**
  - 新建组、添加 / 移除成员、查看成员（含嵌套成员）
- **计算机管理**
  - 查看、启用 / 禁用、删除
- **工具**
  - 域信息与密码策略（FSMO、全局编录、DNS、密码/锁定策略）
  - 密码过期查询、登录历史查询
  - CSV 导入用户、导出当前列表
- **权限门禁**
  - 状态栏显示「域管理员 / 可写（委派）/ 只读」徽标
  - 只读账户的写操作菜单自动禁用
  - 所有写操作均由用户主动触发，不做后台静默修改

## 环境要求

- Windows 10 / Windows 11
- .NET Framework **4.8**
- 已加入目标域，或能通过网络访问域控的 LDAP 端口（389）
- 读操作：普通域账户即可
- 写操作：需要相应 OU 的委派权限或域管理员权限

## 构建

项目使用旧式 csproj + Windows 自带的 .NET Framework MSBuild，不需要 Visual Studio。

**方式一：双击 / 运行构建脚本**

```bat
ADConsoleWpf\build.cmd
```

或：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\ADConsoleWpf\build.ps1 -Rebuild
```

**方式二：直接调用 MSBuild**

```bat
set MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe
"%MSBUILD%" ADConsoleWpf\ADConsoleWpf.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64
```

输出：

```
ADConsoleWpf\bin\Release\ADConsole.exe
```

> 修改代码后建议先删除 `ADConsoleWpf\obj` 和 `ADConsoleWpf\bin` 再执行 `Rebuild`。
> 源文件需保存为 **UTF-8 with BOM**（`build.ps1` 会自动补齐），否则中文可能乱码。

## 运行

双击 `ADConsoleWpf\bin\Release\ADConsole.exe`，输入域服务器、用户名、密码登录。

- 域服务器可填：`domain.example.com`、`192.0.2.10` 或 `dc01.domain.example.com`
- 用户名只需登录名，不需要带 `DOMAIN\` 前缀
- 首次登录后可勾选「记住密码」和「自动登录」

## 凭据与安全

| 项目 | 说明 |
|---|---|
| 凭据文件 | `%APPDATA%\ADConsole\credential.dat` |
| 加密方式 | Windows DPAPI，`CurrentUser` 作用域 |
| 明文落盘 | 否；未勾选「记住密码」时密码不保存 |
| 写操作 | 由当前登录的域账户权限决定，界面按权限禁用 |
| 日志 | 不记录密码 |

连接时会把域名解析为 IPv4 并固定走同一台域控，避免 LDAP 主机名解析和 DC 自动发现
造成的长时间等待。主窗口先显示，权限、OU 树和对象列表在后台加载。

## 权限模型

权限通过当前账户的 `tokenGroups`（传递组 SID）判定，并结合域根上的写 ACE 判断委派权限：

| 级别 | 判定依据 | 界面表现 |
|---|---|---|
| 域管理员 | 命中 Domain Admins / Enterprise Admins / Administrators 等高权限组 RID | 绿色「域管理员」徽标 |
| 可写（委派） | 对域根或目标对象存在写权限 ACE | 橙色「可写（委派）」徽标 |
| 只读 | 以上都不满足 | 灰色「只读」徽标，写操作菜单禁用 |

## 项目结构

```
.
├─ ADConsoleWpf/
│  ├─ ADConsoleWpf.csproj
│  ├─ App.xaml / App.xaml.cs
│  ├─ LoginWindow.xaml / LoginWindow.xaml.cs
│  ├─ MainWindow.xaml / MainWindow.xaml.cs
│  ├─ Services/
│  │  ├─ AdObject.cs
│  │  ├─ AdService.cs
│  │  ├─ CredentialStore.cs
│  │  ├─ CsvHelper.cs
│  │  └─ PermissionService.cs
│  ├─ Dialogs/                 # 新建用户、编辑属性、重置密码、CSV 导入等对话框
│  ├─ icons/                   # OU / 容器 / 用户图标
│  ├─ app.ico
│  ├─ build.cmd
│  └─ build.ps1
├─ tests/
│  └─ ADConsoleWpf.Tests.ps1   # 逻辑层自动化测试
├─ .editorconfig
├─ .gitattributes
├─ .gitignore
└─ README.md
```

## 测试

先构建，再运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\ADConsoleWpf.Tests.ps1
```

当前结果：**214 / 214 通过**。测试只加载编译产物做纯逻辑断言，不连接 AD、不执行任何写操作。
若本机已存在真实凭据文件，DPAPI 往返测试会自动跳过，避免覆盖。

## 写操作验证清单

读路径已在真实域验证；写操作尚未在生产域实测。首次投入使用前，请用**测试账号 + 测试 OU**
按下表逐项验证，不要直接在真实用户对象上试。

| # | 操作 | 验证要点 | 预期结果 |
|---|---|---|---|
| 1 | 登录 / 权限判定 | 普通账号与域管理员账号分别登录，看状态栏徽标 | 只读账号显示灰色「只读」且写菜单禁用；管理员显示绿色徽标 |
| 2 | 新建用户 | 在测试 OU 建用户，勾选「必须修改密码」 | 用户出现在测试 OU；`dsa.msc` 中 `pwdLastSet=0`、账号已启用 |
| 3 | 编辑属性 | 改部门 / 职位 / 邮箱 / 电话 / 说明后保存，重开对话框 | 值已持久化；邮箱格式非法时应给出提示 |
| 4 | 重置密码 | 重置为随机强密码与指定密码各一次，用新密码登录 | 新密码可登录，旧密码失效 |
| 5 | 启用 / 禁用 / 解锁 | 对测试账号依次禁用、启用、连续输错密码后解锁 | 状态列同步变化；解锁后 `lockoutTime=0` |
| 6 | 删除用户 | 对测试账号执行删除，确认二次确认框 | 对象从 AD 移除且不可恢复（仅测试对象） |
| 7 | 组管理 | 新建组、添加成员、移除成员、查看嵌套成员 | 成员列表与 `dsa.msc` 一致 |
| 8 | 计算机管理 | 测试计算机启用 / 禁用 / 删除 | 状态同步，操作有二次确认 |
| 9 | CSV 导入 | 用 3–5 行含中文、逗号、引号的 CSV 导入到测试 OU | 逐行结果有成功/失败提示，失败行不中断整批 |
| 10 | CSV 导出 | 导出当前列表后用 Excel 打开 | 中文不乱码，字段顺序与表头一致 |
| 11 | 权限边界 | 用仅对某个测试 OU 委派的账号，操作该 OU 之外的对象 | 越权操作应失败并给出明确错误，不静默成功 |

记录每项的账号、OU、时间与结果，全部通过后再对生产对象操作。

## 已知限制

- **写操作尚未在生产域实测**。读取路径已验证；重置密码、删除、CSV 导入、编辑属性保存等
  请先按上面的验证清单在测试账号 / 测试 OU 上逐项确认。
- 删除操作有二次确认，但不可恢复。
- 组成员查询使用递归方式，超大组（数千成员）可能稍慢。
- 用户选择器单次最多返回 500 条，可用关键字缩小范围。
