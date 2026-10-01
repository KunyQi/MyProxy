# MyProxy Linux GUI

基于 Avalonia 的托盘界面，包含绑定、连接主页和设置。GUI 通过本地控制通道读取守护进程状态并发送命令。

从仓库根构建：

~~~sh
dotnet build linux/MyProxyLinux/gui/MyProxy.Linux.Gui.csproj -c Release
~~~

安装后先通过 myproxy bind 或 myproxy start 启动守护进程，再运行 myproxy-gui。桌面支持托盘时，关闭窗口会隐藏到托盘；无托盘时关闭会退出 GUI。“停止代理”结束连接，“退出”只退出 GUI。守护进程的连接生命周期独立于 GUI。

自动检查开关目前仅保存在当前界面，尚不能读写守护进程设置。新安装的后台默认不自动检查；可使用手动检查入口，可信更新仍需签名与摘要验证。

托盘需要桌面提供相应支持。无托盘宿主的窗口可达性，以及不同桌面的菜单和激活行为，需要在目标环境检查。

配置、内核与 CLI 使用见 [Linux README](../README.md)。
