# 桌面分身

从 **工具箱 → 桌面分身 → 打开** 进入，在分身窗口点击 **启动并连接**。
使用 Windows Child Session 与本机 RDP，在独立会话中运行星铁和三月七助手。
分身里的游戏保持前台，主桌面可以继续使用键盘鼠标。

## 使用

1. 以管理员身份启动三月七助手。
2. 如果 BetterGI 或其他程序已有桌面分身，先在原程序中关闭。Windows 的这一功能只提供一个子会话；三月七不会接管、注销其他程序的分身。
3. 在三月七的工具箱打开分身，点击“启动并连接”，按 Windows 登录框要求登录。
4. 登录完成后，三月七助手自动以管理员权限进入分身。首次启动较慢时可等待；需要重新打开助手时点击“启动三月七”。
5. 在分身内启动星铁并运行任务。游戏和任务程序必须处于同一个分身会话。

分身首次复制主桌面的配置到 `config/desktop-session/config.yaml`，之后独立保存。
两边的设置、任务时间戳、窗口状态不会互相覆盖。子任务会继承分身配置路径。
如需重置分身配置，在分身关闭后删除这个配置文件，再次启动时会重新复制。

## 控制

| 控制 | 行为 |
| --- | --- |
| 普通鼠标 / 游戏鼠标 | 游戏模式下，分身窗口获得焦点且星铁位于分身前台时，转发相对鼠标位移。分身内的三月七助手必须保持打开。 |
| Alt | 游戏模式下按住 Alt 临时释放鼠标；松开后等待新的移动确认再捕获。 |
| 置顶 | 置顶分身窗口。 |
| 静音 | 切换分身声音；通过重新连接 RDP 应用，不关闭会话内程序。 |
| 显示桌面 / 任务视图 | 向分身发送 Win+D / Win+Tab。 |
| 以管理员启动 | 从主桌面通过临时计划任务，以管理员身份启动选定程序到分身。 |
| 自适应缩放 | 缩放显示 1920×1080 的分身画面；关闭后显示原尺寸并提供滚动条。 |
| 保持 16:9 | 自适应缩放时，普通窗口按实际画面区域保持 16:9，自动计入工具栏、状态栏与边框的高度；拖动调整大小时也保持比例。最大化时按比例居中显示。 |
| 系统快捷键发送到分身 | 设置快捷键目标，通过重新连接应用。 |
| 小窗 / 还原 | 切换紧凑窗口大小。 |
| 隐藏或窗口右上角 X | 隐藏窗口并保持连接；通过工具箱重新显示。 |
| 重新连接 | 重新连接原来的会话，保留会话内运行的程序。 |
| 关闭分身并注销 | 结束分身会话，包括其中运行的程序。 |

退出主桌面的三月七助手会先要求宿主注销其创建的分身；注销失败时保留主窗口供重试。
主助手异常退出时，宿主通过进程句柄监测并尝试注销。直接强制结束宿主进程无法执行清理，
此时可在任务管理器“用户”页注销对应分身会话。

分身不自动接管主桌面已经运行的星铁，也不改变游戏的多开限制。
任务完成后的关机、睡眠等系统操作仍是系统操作，独立配置时应按需要设置。
锄大地和外部模拟宇宙会作为分身内的子进程启动，仍需针对实际使用的第三方版本测试。

## 源码运行与打包

Python 环境与项目其他功能一致；编译宿主需要 **.NET 8 或更新版本 SDK**。

```powershell
uv sync --group dev
uv run python tools/build_desktop_session.py
uv run python app.py
```

源码界面首次打开分身时也会异步执行构建，不阻塞界面。
宿主输出为 `build/desktop-session/March7th.Desktop.exe`，采用 Windows x64 自包含发布，
发行版用户无需额外安装 .NET。`March7th Launcher.spec` 会自动构建并收集宿主、GPL 许可证及来源说明。

## 实现与验证

- C# WinForms 宿主承载 RDP ActiveX，创建子会话、显示桌面、以管理员身份启动程序。
- 鼠标专用 Raw Input 捕获器采集主桌面相对位移，经同用户命名管道发送给子会话中的 Python GUI。
- 子会话仅在配置的游戏进程处于前台时，通过本地 `SendInput` 模拟相对移动。
- 服务端验证客户端真实 PID/Session，客户端验证宿主 PID 与会话；命名管道只允许当前用户。
- 最多一个未确认批次，样本数量受限；焦点切换、Alt、断线和超时都会清除过期位移。旧回执不能重新锁定鼠标。
- 主窗口单实例锁按用户与 Windows Session 区分；暂停控制文件及关闭游戏操作按会话隔离。

自动检查：

```powershell
uv run python tools/build_desktop_session.py
# 纯协议、状态转换测试；不会连接桌面或模拟真实输入
Start-Process -FilePath .\build\desktop-session\March7th.Desktop.exe -ArgumentList '--self-test' -WindowStyle Hidden -Wait
# 加载真实 RDP 控件与 COM 属性；不会启用子会话或建立连接
Start-Process -FilePath .\build\desktop-session\March7th.Desktop.exe -ArgumentList '--smoke-test' -WindowStyle Hidden -Wait
uv run pytest tests/test_module/test_desktop_session.py tests/test_app/test_desktop_session.py
```

实机验收应覆盖：登录与提权启动、星铁界面点击和持续转向、Alt 释放、切到主桌面其他应用、
隐藏窗口后的截图与完整任务、重连、关闭分身、退出主助手，以及实际使用的第三方任务。
自动化检查不能代替这些游戏内验收。

RDP 控件、Windows 子会话 API 封装与计划任务启动器改编自
[BetterGI](https://github.com/babalae/better-genshin-impact/tree/6a8a9a62232855069f0244bc992f66772f14fe0b)，
详情见 `native/March7th.Desktop/THIRD_PARTY_NOTICES.md`。
