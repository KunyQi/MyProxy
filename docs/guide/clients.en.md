# Using the clients

This article is for people who have received a pairing code. The Windows, Android and Linux clients work almost the same way: bind once, then just use the switch. If you don't have a pairing code yet, ask your administrator for one first.

The clients' interface is currently in Chinese. Below, each button and message is given with its on-screen Chinese label, followed by its meaning in English.

## Binding for the first time

Open the client. On the **首次绑定** (first-time binding) screen, enter the 8-character pairing code (letters or digits) your administrator sent you, then tap **绑定** (Bind). Once binding succeeds, the client remembers this device and goes straight to the home screen from then on; you won't need to enter the code again.

If binding does not succeed, the message usually tells you why:

- **配对码无效** (invalid pairing code): most likely a typo; check it and try again.
- **配对码已过期** (pairing code expired): pairing codes are valid for a limited time; ask the administrator to generate a new one.
- **无法验证服务器身份** (cannot verify the server's identity): something is wrong with the server's certificate. This is not a problem with your device; contact the administrator.
- **尚未配置服务器** (no server configured): this installer was built without a server address; contact whoever gave you the installer.
- **请求过于频繁** (too many requests): wait a moment and try again.

## Connecting and disconnecting

The connection switch is in the middle of the home screen. After you press it, the status shows **正在连接…** (connecting). At this point the client does more than start the connection: it confirms through the tunnel that the network really works, and only then shows **已连接** (connected) and **网络连接正常** (network connection OK). Press it again to disconnect.

To check whether the current connection is healthy, tap **检测连接** (Check connection; **检查连接** on Android). The client runs the check again and shows the current latency.

## Two connection modes

You can switch the connection mode on the home screen at any time; while switching, the screen briefly shows **正在切换…** (switching).

- **智能分流** (Smart routing, the default): domestic (mainland China) sites connect directly, and everything else goes through the proxy. This is the recommended mode for everyday use, since domestic sites don't detour through the server.
- **全局代理** (Global proxy): every site goes through the proxy.

In either mode, private-network addresses and BitTorrent traffic are always blocked.

## Settings

There are only a few options in Settings, and you can turn each one on as needed:

- **启动后自动连接** (connect on startup): connects automatically when the client opens.
- **自动启动客户端** (launch the client automatically; Windows and Linux): starts the client when the computer starts.
- **自动检查更新** (check for updates automatically): off by default. When on, the client checks for new versions periodically; an update is never installed automatically, and you have to confirm it. Every update must first pass verification against the deployer's signature. On Linux, this switch is currently saved only in the tray app and does not yet change the background periodic check.
- **界面外观** (appearance; Windows and Linux): switches between three looks, **经典** (Classic), **瓷白** (Porcelain) and **釉陶** (Ceramic). It only changes how the interface looks.
- **查看日志** (view log; Windows and Linux): when something goes wrong, you can open the log and send it to the administrator. Before sending it, remove anything that may be private.
- **重新绑定** (re-bind): stops the connection and clears this device's credentials; after that you need to enter a pairing code again. Usually you only need this when the administrator asks you to.

## Android features

**Quick Settings tile.** Add the MyProxy tile to Quick Settings, then tap it to connect or disconnect directly; if the connection fails, tap it to retry; long-press it to open the app. If the device is not bound yet, needs VPN permission, or is currently held by the kill switch, tapping the tile opens the app to deal with it.

**VPN permission.** The first time you connect, the system asks whether to allow MyProxy to set up a VPN connection; choose Allow.

**Kill switch** (**断网保护**, off by default). When on, if the connection drops unexpectedly, the client blocks all traffic so nothing goes around the proxy without you knowing. The status then shows **流量已阻断** (traffic blocked); on the home screen you can choose **停止并解除保护** (stop and release protection) or **重试连接** (retry connection), and the notification also keeps a **释放** (release) action.

**Auto-repair** (**自动修复**). While the screen is off and the connection is idle, the client automatically restarts a connection that is not working properly.

**Background restrictions.** If the home screen shows **后台限制可能中断连接** (background restrictions may interrupt the connection), tap the message and follow the system's instructions to turn off battery or background restrictions for MyProxy; the connection will be more stable.

## Using the Linux client

The Linux client has three parts: a background daemon that holds the connection, the command-line tool `myproxy`, and the tray app `myproxy-gui`. Both the command-line tool and the tray app only control the daemon, so closing the tray app does not disconnect you; to disconnect, use **停止代理** (stop proxy).

The first time, bind with your pairing code by running `myproxy bind` followed by the code in a terminal; this also starts the daemon. Then open the tray app. After that, you can use either the tray app or the terminal: `myproxy start`, `myproxy stop` and `myproxy status` connect, disconnect and show the status.

The system proxy supports GNOME, KDE and environment variables, LAN and local addresses are bypassed automatically, and your original settings are restored when you disconnect. How the tray icon and menu behave depends on your desktop environment.

## When something goes wrong

- **无法连接服务器** (cannot reach the server): first make sure the device itself can get online, then try again.
- **连接失败** (connection failed): press **重试** (Retry) once more; if it still fails, check the log and contact the administrator.
- **设备已失效，请重新绑定** (device no longer valid, please re-bind): the administrator has revoked this device; ask for a new pairing code.
- **系统代理设置失败** or **系统代理恢复失败** (failed to set / restore the system proxy; Windows): try again as prompted; if it still fails, restart the client.
- **本地端口被占用** (local port in use): usually waiting a moment and trying again is enough; if it keeps happening, check the log and contact the administrator.
