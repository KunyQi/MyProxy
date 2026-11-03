# MyProxy Linux GUI

An Avalonia-based tray interface with binding, the connection home screen and settings. The GUI reads the daemon's state and sends commands through a local control channel.

Build from the repository root:

~~~sh
dotnet build linux/MyProxyLinux/gui/MyProxy.Linux.Gui.csproj -c Release
~~~

After installation, start the daemon with myproxy bind or myproxy start first, then run myproxy-gui. When the desktop supports a tray, closing the window hides it to the tray; without a tray, closing it exits the GUI. "Stop proxy" ends the connection, while "Quit" only exits the GUI. The daemon's connection lifecycle is independent of the GUI.

The automatic-check switch is currently saved only in the current interface and cannot yet read or write the daemon's settings. The background does not check automatically on a new install; the manual check entry is available, and trusted updates still require signature and digest verification.

The tray needs corresponding support from the desktop. Window reachability without a tray host, and menu and activation behaviour on different desktops, need to be checked in the target environment.

For configuration, the core and CLI usage, see the [Linux README](../README.en.md).
