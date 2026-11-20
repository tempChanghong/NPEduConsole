# NPEduConsole

学校管理桌面端。具有学校管理权限的本地账号可以查看班级大屏，为单台 NPEduTools 指定监测麦克风，并核对设备回执。

## 启动

安装 .NET 10 SDK 后，在仓库根目录运行以下命令。

```powershell
dotnet run --project src/NPEduConsole.App
```

在登录页输入 KV 服务地址、学校代码、短账号和密码。校内服务使用 HTTPS，本机联调可使用 HTTP 回环地址。账号须在目标学校具有现有 KV 接口允许的 OWNER 或 ADMIN 权限。凭据仅在本次运行期间使用。

服务器管理员可在登录页右上角打开「服务器状态」，使用 KV 部署时配置的只读监控凭据 `METRICS_TOKEN` 检查数据库、更新目录和自动备份。自动备份卡片读取定时任务记录，分别显示上次成功的时效和最近一次任务结果。监控凭据仅在该窗口内使用，切换 KV 地址时需要重新输入。

## 查看一个班

选择学校、班级和设备。设备列表显示查询时的连接状态与最近上报时间。麦克风详情分别展示学校配置和设备最近一次回执。学校已发布新版本、设备仍在旧版本时，点击刷新可重新核对。班级可以尚未配置大屏，也可以已有大屏但尚未接入 NPEduTools。

## 更换指定麦克风

在设备详情选择“更换指定麦克风”，从这台设备最近一分钟上报的清单中选择目标，核对班级、大屏和变更后发布。此次操作沿用原有摄像头和采集设备锁定策略。若设备处于临时解锁状态，新的锁定策略版本会结束这次临时解锁。

学校接受配置后，终端会在采集空闲时切换。可以通过设备回执中的已应用版本和实际麦克风核对切换结果。如果清单过期或其他管理端已更新策略，刷新后按最新信息重新选择。网络中断时先核对学校当前配置，避免重复发布。

## 本地联调

使用已安装依赖并生成 Prisma 客户端的 NPClassworksKV 工作副本，以及 PostgreSQL 的 bin 目录。下面的命令在临时目录启动独立数据库和 KV，准备三个示例班级，通过设备接口模拟教室上报，验证实际客户端、学校权限、策略写入及设备回执，然后关闭测试环境。

```powershell
node tests/live-kv.mjs 'D:/CodeProjects/NPClassworksKV' 'C:/Program Files/PostgreSQL/17/bin'
```

在命令末尾加 `--serve` 可保留本次临时服务用于桌面验收。控制台会给出临时登录信息文件的位置，按回车结束。

加 `--server-status` 可运行真实 KV/PostgreSQL 的状态回归，包括停库和恢复、更新目录、定时备份记录以及两类凭据的权限隔离。

单独检查已有测试服务时，在测试进程中设置 `NP_OPS_ORIGIN` 和 `NP_OPS_TOKEN`，再运行 `dotnet run --project tests/NPEduConsole.Smoke -c Release -- --server-status`。可用 `NP_OPS_DATABASE` 和 `NP_OPS_CATALOG` 核对预期状态。目录可用时用 `NP_OPS_RELEASE_COUNT` 指定预期整数（空目录为 `0`），未设置时预期返回 `null`。

可选设置 `NP_OPS_BACKUP_STATUS`、`NP_OPS_BACKUP_TIMELINESS`、`NP_OPS_BACKUP_RESULT`，分别核对备份状态、时效、最近任务结果。使用接口原值（如 `AVAILABLE`、`CURRENT`、`FAILED`），用字符串 `null` 核对对应字段为空。省略变量即可跳过该字段的核对。
