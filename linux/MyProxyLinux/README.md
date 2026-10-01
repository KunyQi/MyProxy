# MyProxy Linux

Linux 客户端包含守护进程、CLI 和 Avalonia 托盘 GUI。守护进程持有内核和系统代理状态，CLI 与 GUI 通过本地控制通道操作它。

## 构建

需要 .NET SDK 8.0.424。先修改仓库根 deployment.json，从仓库根运行：

~~~sh
dotnet build linux/MyProxyLinux/core/MyProxy.Linux.Core.csproj -c Release
dotnet build linux/MyProxyLinux/cli/MyProxy.Linux.Cli.csproj -c Release
dotnet build linux/MyProxyLinux/gui/MyProxy.Linux.Gui.csproj -c Release
dotnet test linux/MyProxyLinux/tests/MyProxy.Linux.Tests.csproj -c Release
~~~

Linux Xray 根据 assets/VERSION.txt 的版本与摘要提供。需要获取内核时运行 python3 scripts/fetch_xray_linux.py；预检和打包见 python3 scripts/package_linux.py --help。Geo 数据和内核许可证复用 Windows Core 资源。

## 使用

安装后将 myproxy 与 myproxy-gui 放入 PATH，或使用完整路径。管理员提供配对码后，可使用 CLI 完成绑定：

~~~sh
myproxy bind <配对码>
myproxy start
myproxy status
myproxy stop
~~~

GUI 提供绑定、主页、设置与托盘入口。首次使用先执行 CLI 的 bind 或 start，启动守护进程，再打开 myproxy-gui；GUI 本身不会启动后台服务。GUI 退出后连接可由守护进程继续持有；需要结束连接时先停止代理。更多行为见 [GUI README](gui/README.md)。

## 桌面兼容性

系统代理和托盘能力依赖桌面环境。GNOME/KDE、X11/Wayland 的托盘菜单、窗口激活、自启与恢复应在目标桌面验证。源码构建和单测不替代完整桌面验收。

新安装默认关闭后台自动更新检查；已有显式设置保留。未配置更新信任密钥时拒绝接受更新。GUI 的自动检查开关目前只保存在界面内，尚不能读写守护进程偏好；手动检查与后台定期检查是不同入口。统一配置与 HTTPS 说明见 [部署文档](../../docs/deployment.md)。
