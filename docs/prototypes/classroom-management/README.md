# 教室管理页交互样稿

在 Windows 浏览器打开 [index.html](index.html)。页面只读取同目录的样式和脚本，所有班级、设备与回执都是本地示例。刷新页面可重置演示。

- 高二（1）班展示在线终端。点击「麦克风策略」或「软件维护」查看该设备的详情。这个示例沿用原 Console 在独立测试学校中显示的班级、设备、最近上报时间和麦克风回执。
- 高二（2）班有两块大屏。主讲大屏可演示「核对申请 → 批准替换 → 新设备接入 → 首次上报」，也可以拒绝申请。换机属于方案预览，业务状态只保存在本页内存中。
- 高二（3）班展示首台大屏尚待登记的情况。左侧搜索使用班名，可用半角括号输入。

本地设计参照为 SecRandom 仓库的 `resources/ScreenShots/zh-cn/pick_settings.png` 和 `SecRandom/Views/SettingsView.axaml`。样稿采用同类的侧栏选中标记、分组设置行与右侧详情面板。

## 本地复验

在 Console 仓库根目录，使用本机已有的 Playwright Core 与对应 Chromium 运行以下命令。若项目外已安装 Playwright Core，可将环境变量 `PLAYWRIGHT_CORE_PATH` 指向该包目录；也可通过 `CHROMIUM_PATH` 指定浏览器程序。

```powershell
node tests/classroom-design-preview.cjs
```

检查覆盖班级搜索、多屏切换、设备详情、换机批准及拒绝、首次回执、键盘返回和窄屏布局，同时检查无外部网络请求。截图输出到 `.artifacts/classroom-ui-redesign/console`。1160 × 780 用于和正式 Console 同尺寸核对，1128 × 779 对应本地 SecRandom 参考图。
