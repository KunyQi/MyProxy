# 第三方依赖与许可证

本文件记录当前源码使用的开源项目、版本证据和上游依赖清单，与 [README 的 References](../README.md#开源项目参考清单--references) 一起使用。项目自身代码与文档采用 [MIT](../LICENSE)，第三方部分的版权与许可独立保留，见 [第三方声明](../THIRD_PARTY_NOTICES.md)。

| 项目 | 用途与本地版本证据 |
| --- | --- |
| [Xray-core](https://github.com/XTLS/Xray-core) | Windows/Linux v26.9.9；[Windows 清单](../windows/MyProxy/Assets/Core/VERSION.txt)、[Linux 清单](../linux/MyProxyLinux/assets/VERSION.txt) 保留来源及 SHA-256；[随附 MPL-2.0](../windows/MyProxy/Assets/Core/LICENSE) |
| [AndroidLibXrayLite](https://github.com/2dust/AndroidLibXrayLite) | v26.9.9，libVersion 40；[vendor 清单](../android/MyProxyAndroid/vendor/libv2ray/VERSION.txt) 记录 AAR、sources JAR 与对应 Xray commit；[LGPL-3.0](../licenses/LGPL-3.0.txt) 与 [GPL-3.0](../licenses/GPL-3.0.txt) 原文随附，[对应源码与替换指南](android-core-rebuild.md) |
| [v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat) | GeoIP/GeoSite 来源经 Xray v26.9.9 的 [资产更新工作流](https://github.com/XTLS/Xray-core/blob/v26.9.9/.github/workflows/scheduled-assets-update.yml) 确认；项目裁剪使用到的标签，清单记录原始和裁剪后摘要 |
| [v2fly/geoip](https://github.com/v2fly/geoip)、[domain-list-community](https://github.com/v2fly/domain-list-community) | 规则数据的上游生成器和社区列表；数据与生成器遵循各自随附许可 |
| [.NET Runtime](https://github.com/dotnet/runtime)、[WPF](https://github.com/dotnet/wpf) | .NET 8，Windows WPF / Linux 通用运行库；实际自包含运行时版本由构建产物清单记录 |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia)、[Inter](https://github.com/rsms/inter) | Avalonia/Desktop/Fluent/Fonts.Inter 11.3.2，Diagnostics 仅 Debug；[GUI 项目](../linux/MyProxyLinux/gui/MyProxy.Linux.Gui.csproj) 固定直接包版本 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / [Skia](https://skia.org/)、[HarfBuzz](https://github.com/harfbuzz/harfbuzz)、[MicroCom](https://github.com/kekekeks/MicroCom)、[Tmds.DBus](https://github.com/tmds/Tmds.DBus)、[ANGLE](https://chromium.googlesource.com/angle/angle) | Avalonia 的原生渲染、文字整形与桌面互操作传递依赖；以对应 NuGet 包依赖及 native assets 记录为准 |
| [AndroidX / Compose](https://github.com/androidx/androidx) | Core KTX、Activity Compose、UI/Graphics/Tooling/Preview、Material3、Navigation、Lifecycle Runtime/ViewModel、DataStore；版本及 BOM 见 [version catalog](../android/MyProxyAndroid/gradle/libs.versions.toml) |
| [Kotlin](https://github.com/JetBrains/kotlin) | 2.4.20，含 Android/Compose/Serialization 编译插件与标准库；Android 使用 AGP 内置 Kotlin |
| [kotlinx.coroutines](https://github.com/Kotlin/kotlinx.coroutines)、[kotlinx.serialization](https://github.com/Kotlin/kotlinx.serialization) | 1.9.0 / 1.7.3；Android 异步任务与 JSON |
| [OkHttp](https://square.github.io/okhttp/)、[Okio](https://square.github.io/okio/)、[Java annotations](https://github.com/JetBrains/java-annotations) | OkHttp 4.12.0；Okio 与注解由 JVM 依赖树解析 |
| [Python / CPython](https://github.com/python/cpython)、[SQLite](https://www.sqlite.org/) | Server 仅使用 Python 标准库；SQLite 通过 sqlite3 提供，实际版本随 Python/系统构建 |
| [3x-ui](https://github.com/MHSanaei/3x-ui) | 外部部署集成；保留原面板，适配器读写其设备与配置数据 |
| [nginx](https://github.com/nginx/nginx)、[OpenSSL](https://github.com/openssl/openssl)、[curl](https://github.com/curl/curl) | 外部 HTTPS 网关、证书验证、下载与验收工具，版本由部署环境记录 |
| [systemd](https://github.com/systemd/systemd)、[GNU Bash](https://www.gnu.org/software/bash/)、[GLib / GSettings](https://docs.gtk.org/gio/class.Settings.html) | 用户/服务端服务管理、部署脚本、GNOME 代理设置 |
| [.NET SDK](https://github.com/dotnet/sdk)、[MSBuild](https://github.com/dotnet/msbuild) | SDK 8.0.424，见 [global.json](../global.json)；构建与发布 |
| [OpenJDK](https://github.com/openjdk/jdk17u)、[Gradle](https://github.com/gradle/gradle)、[AGP](https://android.googlesource.com/platform/tools/base/)、[Android SDK Build Tools](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/tools/aapt2/) | JDK 17、Gradle 9.8.0、AGP 9.4.1、SDK/Build Tools 37；wrapper JAR 和分发包摘要固定 |
| [MSTest](https://github.com/microsoft/testfx)、[VSTest](https://github.com/microsoft/vstest) | MSTest Framework/Adapter 3.6.4、Microsoft.NET.Test.Sdk 17.11.1；Windows/Linux 测试项目 |
| [JUnit 4](https://github.com/junit-team/junit4)、[Hamcrest](https://github.com/hamcrest/JavaHamcrest) | JUnit 4.13.2 与传递断言库；Android JVM 测试 |
| [Go](https://go.dev/)、[Go mobile](https://pkg.go.dev/golang.org/x/mobile) | 原生 AAR 记录 Go 1.27.1、mobile 8b95e45f8d3e；桥接源文件保留 Go Authors 版权头与 [BSD 许可全文](../licenses/Go-BSD-3-Clause.txt)；常规客户端构建无需重新编译 Go 核心 |

## 上游传递依赖记录

内核版本对应的完整 Go 模块条目由 [Xray v26.9.9 go.mod](https://github.com/XTLS/Xray-core/blob/v26.9.9/go.mod)、[AndroidLibXrayLite v26.9.9 go.mod](https://github.com/2dust/AndroidLibXrayLite/blob/v26.9.9/go.mod) 及各自 go.sum 记录，包括 QUIC、REALITY、uTLS、gRPC、Protobuf、gVisor、WireGuard、DNS 和 Go 扩展库等项目。它们的来源与许可随锁定模块版本核对。

[Android 实际原生模块清单](../android/MyProxyAndroid/vendor/libv2ray/BUILD-INFO.json) 与 [桌面内核模块清单](../licenses/CORE-BUILD-INFO.json) 从各二进制的 Go build info 读取；[各模块许可索引](../licenses/go-modules/INDEX.json) 覆盖三端所用的 61 个模块版本，保留对应版本的源码 ZIP 地址、下载摘要和许可证原文。五份通用许可的来源及 SHA-256 见 [SOURCES.json](../licenses/SOURCES.json)。

托管库的具体传递包由 NuGet 的 project.assets.json 与 Gradle dependencies 输出记录。发布时保留相应依赖的许可证和 native assets 版权文件；README 按项目列引用，同一项目的多个包共用一个来源条目。

## CI 项目

当前工作流在 [ci-cross-platform.yml](../.github/workflows/ci-cross-platform.yml) 固定 Action commit，使用以下开源工具：

- [actions/checkout](https://github.com/actions/checkout)：v5.0.0。
- [actions/setup-python](https://github.com/actions/setup-python)：v5.6.0。
- [actions/setup-dotnet](https://github.com/actions/setup-dotnet)：v5.2.0。
- [actions/setup-java](https://github.com/actions/setup-java)：v5.2.0。
- [android-actions/setup-android](https://github.com/android-actions/setup-android)：v4。
- [actions/upload-artifact](https://github.com/actions/upload-artifact)：v4.6.2。
- [actions/download-artifact](https://github.com/actions/download-artifact)：v5.0.0。
- [actions/attest](https://github.com/actions/attest)：v4.2.1。
- [github/codeql-action](https://github.com/github/codeql-action)：[安全扫描工作流](../.github/workflows/codeql.yml) 固定 v4 对应提交 2892aa5e19bbd11bc0cff5427e3b750a04d9e3c2。
- [Dependabot](https://github.com/dependabot/dependabot-core)：[更新配置](../.github/dependabot.yml) 跟踪 NuGet、Gradle 与 GitHub Actions。

## 开发工具与模型来源

本项目相当一部分代码借助以下开源工具与模型构建：

| 来源 | 用途与版本记录 |
| --- | --- |
| [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) | Agent 框架与工程任务编排；上游 MIT 许可，开发所用具体提交未随本项目记录 |
| [DeepSeek-V4.1-Flash](https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash) | 开源模型，辅助代码构建；对应开发时使用的 V4.1F |
| [MiMo-V2.6-Pro](https://mimo.mi.com/docs/zh-CN/news/latest/v2-6) | 开源模型，辅助代码构建；模型来源与许可见官方发布及模型卡 |

上述条目记录开发来源。常规客户端与服务端运行无需安装模型或 Agent 框架。

## 版权与许可

保留源文件与归档内的第三方版权说明。上游完整许可信息以相应版本的官方源码、包内文件及发布说明为准；本清单与随附许可证、对应源码方向一起分发。Android APK 的 assets/myproxy-licenses/ 及 Windows/Linux 包含项目许可、第三方声明和完整许可目录。
