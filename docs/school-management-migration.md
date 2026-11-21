# 学校管理网页迁移清单

## 学校审计记录的退役条件

使用本地学校管理账号登录 NPEduConsole，选择学校后即可查看审计记录。即使学校尚无启用学期、班级或大屏，也可以查阅。该入口读取 KV 的学校 `AuditLog`。每次查询时，KV 都会核对当前账号在该校的 OWNER 或 ADMIN 权限。

| 管理员要做的事 | Console 中的对应操作 |
| --- | --- |
| 找出某天失败的操作 | 选择操作来源、类型、结果及本地起止日期，读取选中日期全天的记录。 |
| 查更早的记录 | 按时间倒序浏览，点击「加载更多」查看下一页。点击「刷新」或「应用筛选」都会从第一页开始。 |
| 核对一条记录 | 查看操作者或大屏、时间、操作对象、请求路径、HTTP 状态、来源 IP 和脱敏参数。旧记录缺少的字段按缺失显示。 |

**网页入口目前保留。** 网页提供 OAuth 登录，桌面端目前使用本地学校管理账号。部署新版 KV 和 Console 后，先确认使用该入口的管理员均有可用的桌面登录方式，再移除 `section=audit` 的网页导航并处理指向旧地址的链接。只持有 OAuth 身份的管理员在此之前可以继续使用原网页，无需为本轮迁移额外设置口令。

## 当前学期的行政班学生名单

使用本地学校管理账号选择一个行政班后，打开「学生名单」。可逐行增改、移出和排序，也可粘贴 Excel 两列或纯文本名单，选择追加或替换。确认前会列出相对于学校原名单的变化。无大屏的班级也可以办理。

有学号的行按学号匹配，保留前导零。无学号的行按未编号学生的姓名匹配，遇到同名需要逐行核对。改名或改学号可直接编辑现有行，以保留学生 ID。移出学生后，已登记的历史考勤仍保留原 ID。

管理员和大屏共用同一份名单修订号。遇到并发修改，Console 保留草稿，读取当前名单供核对。保存回包中断时也会先读取服务器现状。新版 KV 会在写入时检查本班学期是否仍在使用，切换学期后需重新选择班级。

网页版仍承接 OAuth 管理账号和指定其他学期的名单维护。退役该入口前，需确认这两类使用场景都有可用的入口。

## 十三个旧入口的去向

| 旧网页入口 | 已有桌面能力或仍需网页完成的业务 |
| --- | --- |
| 审计记录 | 本地学校管理账号可在 Console 完成上述查询。 |
| 管理总览 | 学校配置诊断和管理待办仍在网页。学校管理员登录 Console 后可自动查看所连 KV 的运行状态。 |
| 组织与班级 | Console 可浏览当前学校的班级。维护年级、学科、班级与教学关系仍在网页。 |
| 学生名单 | Console 可维护当前学期一个行政班的名单，支持批量粘贴、差异预览、保存及冲突核对。 |
| 教师分配 | 教师职责、任课分配和作业快捷输入配置仍在网页。 |
| 大屏设备 | Console 可查看班级大屏和已接入的终端。创建或重置网页大屏账号、维护作业快捷截止时间仍在网页。 |
| 大屏互联 | Console 已覆盖部分班级接入、麦克风策略和软件维护。设备撤销、考试控制等仍有网页入口。 |
| 自习监测 | Console 可为单班增加自习安排、查看回执和场次。年级策略、完整日历、照片等仍需网页。 |
| 自习座位表 | 座位安排仍在网页。 |
| 账号与管理员 | 本地账号、管理员及身份分配仍在网页。 |
| 高级批量导入 | 组织导入的校验、预览和写入仍在网页。 |
| 服务器迁移 | 学校迁移包导出等操作仍在网页，与 KV 自动备份状态分别管理。 |
| 学期运维 | 建立学期草稿、启用检查及切换学期仍在网页。 |

下一项建议接上**在已有年级创建行政班并录入首份名单**。目前管理员遇到新开班仍要回网页创建班级，建班后才能在 Console 选择它。

## 本地复验

在 NPEduConsole 根目录运行以下命令，使用已安装依赖并生成 Prisma 客户端的 KV 工作副本和本机 PostgreSQL 工具。测试建立独立数据库，检查管理操作生成的记录、权限撤回、翻页、日界线和历史数据脱敏。

```powershell
node tests/live-kv.mjs 'D:/CodeProjects/NPClassworksKV' 'C:/Program Files/PostgreSQL/17/bin' --school-audit
node tests/live-kv.mjs 'D:/CodeProjects/NPClassworksKV' 'C:/Program Files/PostgreSQL/17/bin' --class-roster
```

名单联调会停用夹具中的 NPEP，检查无设备班级、同名学生、屏幕并发写入、学期切换、历史考勤和保存结果核对。网页名单的行为可参照 NPClassworks 的 `src/components/admin/ClassRosterManager.vue` 和 `src/utils/classRoster.js`，服务端规则见 NPClassworksKV 的 `services/classroomToolsService.js`。

要复验 Windows 桌面上的实际保存，把上面的名单命令改为 `--serve-class-roster`，保留临时服务。另开终端运行以下命令，将第一个占位路径替换为服务打印的测试登录文件，最后按回车关闭临时服务。

```powershell
dotnet run --project tests/NPEduConsole.RosterDesktop -c Release -- --live-save '<测试目录>/desktop.json' '.artifacts/class-roster-ui/desktop-live-save'
```

审计入口见 NPClassworks 的 `src/pages/classworks-admin.vue`、`src/components/admin/AuditLogViewer.vue`，以及 NPClassworksKV 的 `routes/v2/academic-admin.js`、`services/auditLogService.js`。
