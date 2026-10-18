# Screenbox mpv Preview（x64 测试版）

本版本基于 Tung Huynh 的 Screenbox，保留中文界面和主要操作，将普通视频、HDR 视频和音频交给现有的 mpv 播放。采用独立应用名称，可与原版 Screenbox 并存。

## 安装与首次播放

1. 使用 Windows 11 x64，将 ZIP 完整解压到一个文件夹。
2. 右键 `Install.cmd`，选择“以管理员身份运行”。这是 Windows 安装未签名 MSIX 的要求。脚本只安装应用及包内依赖，不导入证书、不修改执行策略。
3. 在开始菜单打开 **Screenbox mpv Preview**。
4. 第一次播放时，选择你已经使用且画面正常的 **mpv.exe**。保留原有 mpv 目录和 portable_config；程序会记住这个路径。
5. 先打开一段普通视频，再打开之前偏色的同一段 HDR / 杜比视界视频。比较画面颜色、暂停、拖动进度、字幕、音轨与全屏。

需要现有的 mpv.exe 支持 `gpu-next`（通常是较新的 mpv）。本安装包不包含 mpv；无需 Visual Studio、SDK 或编码插件。正常使用应用时无需管理员权限。取消首次文件选择后，可以再次点击播放重试。

## 当前范围与限制

- 所有播放均通过 mpv 的原生 `gpu-next` 输出，画面直接嵌入 Screenbox 窗口。Screenbox 与一个后台渲染工作进程通过 AppService 和 JSON IPC 通信。
- 读取已有 mpv 配置；禁用 mpv 自带 OSC、脚本和输入绑定，主界面由 Screenbox 提供。因此依赖 Lua 脚本的自定义效果不会自动加载。
- 保留播放/暂停、音量、倍速、进度、章节、音轨、字幕与截图的适配。截图可能经过色调映射，不能用于判断屏幕实际 HDR 效果。
- 原生视频窗口为标题和底部控制条留出空间，弹出菜单通过窗口裁剪显示；尚未完成与原版完全一致的悬浮叠层效果。
- 投屏与部分 VLC 专属高级设置暂未适配；裁剪/自定义比例、触摸手势和复杂多窗口场景需要进一步实机验证。
- 如果移动或删除了已选择的 mpv.exe，下次启动播放失败会清除路径，再次播放可重新选择。渲染进程异常退出后，先关闭并重开测试版。
- 云端可以验证编译、自动化测试、打包和 Win32 子窗口的几何/裁剪/释放行为。HDR、杜比视界颜色与实际桌面交互仍需要在你的显示器上检验，本预览版尚未据此宣称效果已验证。

安装失败时保留窗口内的错误文字。卸载：Windows 设置 → 应用 → 已安装的应用 → Screenbox mpv Preview → 卸载。

## 源码与构建

源码分支：https://github.com/ywz3135340080-glitch/Screenbox/tree/codex/mpv-backend

具体构建提交和可下载的对应源码见包内 `SOURCE.txt`。许可证为 GPL-3.0-or-later，完整文本见 `LICENSE`。原项目：https://github.com/huynhsontung/Screenbox

云端构建使用 Visual Studio 2026 MSBuild、Windows SDK 10.0.26100.0 与 .NET 10 UWP 工具链。步骤见 `.github/workflows/mpv-development.yml`：先发布 Screenbox.MpvBridge 到 artifacts/MpvBridge，再构建 UWP 包与测试。安装说明参考微软未签名 MSIX 文档：https://learn.microsoft.com/windows/msix/package/unsigned-package
