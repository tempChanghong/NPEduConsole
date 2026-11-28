# NPEduConsole

学校管理桌面端。具有学校管理权限的本地账号可以查看班级大屏，为单台 NPEduTools 指定监测麦克风，并核对设备回执。

## Windows 管理员首次使用

从学校部署方的 HTTPS 页面获取 Windows x64 ZIP，核对公布的版本和 SHA-256 后，完整解压到本机目录并打开 `NPEduConsole.App.exe`。发行包自带 .NET 运行时。继续保留解压目录中的其余文件。若部署方提供安装 EXE，也可以安装到当前 Windows 用户，在开始菜单打开 NPEduConsole。

首次建校、续办或迁入时，在登录页点「首次配置学校服务器」。同源部署填写师生网页地址。师生网页和 KV 分域时勾选「使用独立 KV 地址」，再填写 API 地址。先检查连接，确认目标状态和地址一致，再按页面选择新建、继续初始化或从迁移包恢复。只有部署人员提供的初始化密钥适用于此步骤。已有学校使用「使用此地址登录」按正常账号登录。更详细的可核对状态见[首次建校与整校迁入](docs/school-management-migration.md#首次建校与整校迁入)。

### 生成发行包

在 Windows 的 PowerShell 中使用 .NET 10 或兼容 SDK，运行：

```powershell
./scripts/Publish-Windows.ps1 -Version 0.1.0
```

默认输出到 `.artifacts/windows-release/0.1.0/`。也可用 `-OutputDirectory` 指定尚不存在的发行目录。发布时将 ZIP 与生成的 `console-release.json` 一同放在师生网页的 `/downloads/npeduconsole/` 下。版本、文件名、字节数和 SHA-256 来自该次实际生成的 ZIP。需要核对本地文件时运行 `Get-FileHash -Algorithm SHA256 ./NPEduConsole-0.1.0-win-x64.zip`。

要在同次构建生成独立安装 EXE，额外传入 Inno Setup 7 的编译器路径（安装包目标为原生 Windows x64，编译器须带简体中文语言文件）。

```powershell
./scripts/Publish-Windows.ps1 -Version 0.1.0 -IsccPath $iscc
```

输出目录还会包含 `NPEduConsole-0.1.0-win-x64-setup.exe` 及其 SHA-256 文件。安装器和 ZIP 使用同一份自包含构建输出。预发行安装包另传递增的 `-InstallerRevision`（1–65534），同一前三段版本的正式版使用 65535。网页仍按原有 `console-release.json` 获取 ZIP。

安装版按当前 Windows 用户登记，路径固定为 `%LOCALAPPDATA%\Programs\NPEduConsole`。升级前关闭安装版和上次尚在执行的安装或卸载。卸载从 Windows「已安装的应用」进行，保留 `%LOCALAPPDATA%\NPEduConsole` 下的服务器地址和待核对记录。自行解压的便携版独立保留。

安装链路验收使用 Windows x64 虚拟机里的全新标准用户。先在 Windows 构建机编译验收程序，再把程序和两份不同版本的组包输出传入来宾。验收程序可从来宾桌面或来宾的独立 Windows 桌面运行。

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /platform:x64 '/reference:System.IO.Compression.dll,System.IO.Compression.FileSystem.dll,System.Management.dll,System.Web.Extensions.dll,Microsoft.CSharp.dll' /out:WindowsInstallerLifecycle.exe tests/WindowsInstallerLifecycle.cs
```

在来宾以测试账号运行 `WindowsInstallerLifecycle.exe --isolated-guest <旧版发行目录> <新版发行目录> <新证据目录>`。程序执行实际安装、升级、已登记的卸载命令和重装，比较落盘文件与同次构建的 ZIP，并验证服务器地址和待核对记录仍在。证据目录包含各步骤的 Inno 日志和 `result.json`。中文向导和 Windows 设置里的卸载入口可另用专用来宾桌面验收。

发行版桌面验收使用交互式 Windows、PowerShell 7 和隔离 KV/PostgreSQL。将 `NP_CONSOLE_RELEASE_ZIP` 指向本次发布或浏览器下载的 ZIP，运行 `node tests/live-kv.mjs <KV 工作副本> <PostgreSQL bin 目录> --windows-release-oobe`。若校验清单与下载件分开存放，通过 `NP_CONSOLE_RELEASE_MANIFEST` 指定清单。测试从独立解压目录启动发行 EXE，记录实际加载的运行时和宽窄窗截图。

## 开发启动

安装 .NET 10 SDK 后，在仓库根目录运行以下命令。

```powershell
dotnet run --project src/NPEduConsole.App
```

在登录页输入 KV 服务地址、学校代码、短账号和密码。校内服务使用 HTTPS，本机联调可使用 HTTP 回环地址。账号须在目标学校具有现有 KV 接口允许的 OWNER 或 ADMIN 权限。凭据仅在本次运行期间使用。

学校管理员登录后，左栏底部的「服务器状态」会自动检查当前 KV，约每 60 秒刷新。选学校之前就能查看，无需再输入监控凭据。点击状态可查看数据库、更新目录和定时备份的详情，也可手动刷新。自动备份分别显示上次成功的时效和最近一次任务结果。若检查失败，工作区仍可继续使用，业务操作仍按原有权限检查。

暂时无法登录时，服务器运维人员可从登录页右上角打开「运维诊断」，使用部署时配置的只读监控凭据 `METRICS_TOKEN` 检查 KV。监控凭据仅在该窗口内使用，切换 KV 地址时需要重新输入。

## 查看一个班

选择学校后，打开「班级与教务」。目录支持按班名、年级或代码筛选，勾选「仅已停用」可找回停用班。选中班级后，可从班名下方进入学生名单、授课科目和任课分配。班名相同时，可核对年级和班级代码。

在班级内点「查看大屏」打开对应屏幕。也可从「教室设备」跨班查找，用班名、屏名或绑定编号搜索，同时筛选 NPEduTools 接入和网页值守。两栏分别显示各自的状态与最近记录。点「查看」进入同一详情，返回目录会保留筛选并重新读取学校记录。

在大屏详情选中已绑定的终端，可查看学校配置的麦克风和设备最近一次回执。学校已发布新版本、设备仍在旧版本时，点击刷新可重新核对。

本班还没有大屏记录时，可点击「准备教室大屏」。登记确认后，控制台会打开这台新大屏的口令接入设置。学校管理员确认开放后，现场即可在 NPEduTools 的「学校互联」使用班级管理口令接入。若登记结果未确认，请先刷新大屏列表核对。

## 维护班级学生名单

在班级栏点击「新建行政班」，从当前生效学期选择现有年级。创建确认后，控制台会打开新班的学生名单，可立即录入或稍后从班级列表进入。如果建班结果暂未确认，选择「重试确认」会沿用原请求。

使用学校管理账号登录，选中行政班后点击「学生名单」。可以逐行编辑、调整顺序，也可以从 Excel 粘贴「学号、姓名」两列，或粘贴每行一个姓名或「学号 姓名」的纯文本。预览实际变更并确认后才写入学校名单。移出学生保留历史考勤。若名单已被修改，可对照学校当前名单并保留草稿，再决定如何调整。

## 查看学校审计记录

使用学校管理账号登录并选中学校后，点击左侧「审计记录」。可按来源、操作类型、结果和本地日期筛选，也可加载更多记录并查看单条详情。

核对任课、学期资料或学校账号时，可用「操作类型」分别查询预览与正式办理。配套 KV 会将预览过期或条件不满足的保存记在「失败」里，详情仍显示实际 HTTP 状态。升级前的记录保留当时写入的分类和结果。

旧网页各入口的迁移进度、OAuth 管理账号的过渡安排和审计查询的本地联调步骤见[学校管理网页迁移清单](docs/school-management-migration.md)。

## 更换指定麦克风

在设备详情选择“更换指定麦克风”，从这台设备最近一分钟上报的清单中选择目标，核对班级、大屏和变更后发布。此次操作沿用原有摄像头和采集设备锁定策略。若设备处于临时解锁状态，新的锁定策略版本会结束这次临时解锁。

学校接受配置后，终端会在采集空闲时切换。可以通过设备回执中的已应用版本和实际麦克风核对切换结果。如果清单过期或其他管理端已更新策略，刷新后按最新信息重新选择。网络中断时先核对学校当前配置，避免重复发布。

## 界面约定

班级、设备、待办等独立对象清单使用 `ListBox.object-list`。普通行保留圆角细框，选中与键盘焦点使用主题描边。名单和任课对照表保留连续列与固定表头。共用样式见 [Selection.axaml](src/NPEduConsole.App/Styles/Selection.axaml)。

长列表和弹层共用圆角滚动条。滑块在悬停时加宽，拖动热区保持 16 DIP，表头与表格的列宽保持对齐。见[滚动条样式](src/NPEduConsole.App/Styles/ScrollBar.axaml)。

## 本地联调

使用已安装依赖并生成 Prisma 客户端的 NPClassworksKV 工作副本，以及 PostgreSQL 的 bin 目录。下面的命令在临时目录启动独立数据库和 KV，准备三个示例班级，通过设备接口模拟教室上报，验证实际客户端、学校权限、策略写入及设备回执，然后关闭测试环境。

```powershell
node tests/live-kv.mjs 'D:/CodeProjects/NPClassworksKV' 'C:/Program Files/PostgreSQL/17/bin'
```

在命令末尾加 `--serve` 可保留本次临时服务用于桌面验收。控制台会给出临时登录信息文件的位置，按回车结束。

加 `--server-status` 可运行真实 KV/PostgreSQL 的状态回归，包括学校管理员会话、停库和恢复、更新目录、定时备份记录以及两类凭据的权限隔离。

单独检查已有测试服务时，在测试进程中设置 `NP_OPS_ORIGIN` 和 `NP_OPS_TOKEN`，再运行 `dotnet run --project tests/NPEduConsole.Smoke -c Release -- --server-status`。可用 `NP_OPS_DATABASE` 和 `NP_OPS_CATALOG` 核对预期状态。目录可用时用 `NP_OPS_RELEASE_COUNT` 指定预期整数（空目录为 `0`），未设置时预期返回 `null`。

检查学校账号时，改用 `dotnet run --project tests/NPEduConsole.Smoke -c Release -- --school-server-status`，设置 `NP_OPS_ORIGIN`、`NP_TEST_SCHOOL_CODE`、`NP_TEST_USERNAME` 和 `NP_TEST_PASSWORD`。两种入口使用相同的预期状态变量。隔离的回环服务可设置 `NP_TEST_RENEWAL_SECONDS`，按指定时长用同一会话反复检查，验证短令牌续用。

可选设置 `NP_OPS_BACKUP_STATUS`、`NP_OPS_BACKUP_TIMELINESS`、`NP_OPS_BACKUP_RESULT`，分别核对备份状态、时效、最近任务结果。使用接口原值（如 `AVAILABLE`、`CURRENT`、`FAILED`），用字符串 `null` 核对对应字段为空。省略变量即可跳过该字段的核对。
