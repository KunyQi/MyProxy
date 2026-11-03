# MyProxy Linux

The Linux client consists of a daemon, a CLI and an Avalonia tray GUI. The daemon holds the core and the system proxy state, and the CLI and GUI operate it through a local control channel.

## Building

Requires .NET SDK 8.0.424. First change deployment.json at the repository root, then run from the repository root:

~~~sh
dotnet build linux/MyProxyLinux/core/MyProxy.Linux.Core.csproj -c Release
dotnet build linux/MyProxyLinux/cli/MyProxy.Linux.Cli.csproj -c Release
dotnet build linux/MyProxyLinux/gui/MyProxy.Linux.Gui.csproj -c Release
dotnet test linux/MyProxyLinux/tests/MyProxy.Linux.Tests.csproj -c Release
~~~

The Linux Xray is provided according to the version and digest in assets/VERSION.txt. To fetch the core, run python3 scripts/fetch_xray_linux.py; for pre-flight checks and packaging, see python3 scripts/package_linux.py --help. Geo data and the core licenses reuse the Windows Core resources.

## Usage

After installation, put myproxy and myproxy-gui on your PATH, or use their full paths. Once the administrator has given you a pairing code, you can bind with the CLI:

~~~sh
myproxy bind <pairing-code>
myproxy start
myproxy status
myproxy stop
~~~

The GUI provides binding, the home screen, settings and a tray entry. The first time, run the CLI's bind or start to launch the daemon, then open myproxy-gui; the GUI does not start the background service itself. After the GUI exits, the daemon can keep holding the connection; to end the connection, stop the proxy first. More behaviour is described in the [GUI README](gui/README.en.md).

## Desktop compatibility

System proxy and tray capabilities depend on the desktop environment. Tray menus, window activation, auto-start and restoration under GNOME/KDE and X11/Wayland should be verified on the target desktop. Source builds and unit tests do not replace a full desktop acceptance test.

New installs have background automatic update checks off by default; an existing explicit setting is kept. Updates are rejected when no update trust key is configured. The GUI's automatic-check switch is currently saved only within the interface and cannot yet read or write the daemon's preference; a manual check and the background periodic check are separate entry points. For the shared configuration and HTTPS, see the [deployment documentation](../../docs/deployment.en.md).
