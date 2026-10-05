# MyProxy

**把复杂留在代码里。用户只管连接。**

自有服务器，三个客户端，一套公开接口。MyProxy 包含 Windows、Android、Linux 客户端和 Python 控制面。首次配对，日常开关。连接参数由部署者预先配置。

本仓库面向自行部署服务器的开发者，当前版本为 0.1.x 公开测试版。开始部署见 [配置与部署](docs/deployment.md)，参与开发见 [贡献指南](CONTRIBUTING.md)，安全报告见 [SECURITY.md](SECURITY.md)。

## 开发初心

起点很简单：自建 VPN 时，开源工具已经具备足够的能力，普通用户却还要理解协议、地址和配置，才能获得商业 VPN 那样简单的使用体验。MyProxy 要补上这段距离。

目标是首次输入配对码，日常操作连接开关。服务器、证书和连接参数由部署者或专业开发者填写。智能分流与全局模式保留，设置提供必要解释；普通用户按默认策略即可连接，不必先学习网络配置。

客户端的工程标准：轻量、稳定、可维护，无广告。服务端保留 **3x-ui 面板**管理 Xray，补充标准 HTTP API、配对与设备管理。接口有契约，配置可审查，部署可复现。

按配置转发的流量由自有服务器承载，减少对第三方 VPN 服务商的信任依赖。默认分流包含直连规则，实际转发范围由策略决定。隐私依赖正确的部署、凭据管理和客户端维护，不能由“自建”两个字保证。

代码与服务由自己掌握。缺什么，就补什么。可以在 AI 帮助下修改、测试、构建，也可以把可复用的改进留给下一个人。

本项目相当一部分代码借助 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 与开源模型 [DeepSeek-V4.1-Flash](https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash)、[MiMo-V2.6-Pro](https://mimo.mi.com/docs/zh-CN/news/latest/v2-6) 构建。

少一点配置负担，少一点维护成本，让工具安静地工作。

## 组成

| 目录 | 内容 |
| --- | --- |
| windows/MyProxy/ | WPF 客户端、共享连接逻辑和测试 |
| android/MyProxyAndroid/ | Compose 客户端与 libv2ray 集成 |
| linux/MyProxyLinux/ | 守护进程、CLI、Avalonia 托盘界面和测试 |
| server/ | 账号与设备 API、管理界面、SQLite 存储、3x-ui 适配器 |
| scripts/ | 构建、校验和打包工具 |
| deployment.json | 四端共用的公开服务器入口配置 |

框架提供配置缓存与回退、系统代理恢复、设备撤销、聚合用量统计及签名更新校验。新安装默认关闭自动更新检查；已有显式偏好保留。接受更新前需要部署者配置自己的信任密钥，检查不会自动安装新版本，安装需用户操作。

## 配置自己的服务器

修改仓库根目录的 deployment.json，例如：

~~~json
{
  "api_base_url": "https://api.example.com:8443"
}
~~~

Windows、Android、Linux 在构建时使用该配置；更换入口后重新构建客户端。服务端使用同一文件作为默认主机配置，现有 MYPROXY_* 环境变量可以显式覆盖相应设置。客户端使用标准 HTTPS 证书链与主机名验证，普通用户只输入管理员提供的配对码。

连接检测默认走自有服务器，不依赖固定的公共探测站。需要备用检测点时，在同一配置里填写 `connectivity_check_urls`；详见部署说明。

仓库自带的 .invalid 地址是占位示例，需要替换后才能连接。部署者还需配置 HTTPS 证书、3x-ui 数据面、管理凭据和服务端权限。控制面 API 与数据面端口分别配置。完整步骤见 [部署说明](docs/deployment.md) 和 [Server README](server/README.md)。

## 构建与测试

依赖：.NET SDK **9.0.318**、.NET **8** 运行时、Python **3.12+**；Android 需要 JDK **17** 与 Android SDK **35**。从仓库根运行：

~~~text
dotnet build windows/MyProxy/MyProxy.csproj -c Release
dotnet test windows/MyProxy/Tests/MyProxy.Tests.csproj -c Debug
python -m unittest discover -s server/tests -v
python scripts/release_verify.py check
~~~

在 android/MyProxyAndroid/ 运行（Windows 使用 gradlew.bat）：

~~~sh
./gradlew --no-daemon lintRelease testDebugUnitTest assembleRelease lintStaging testStagingUnitTest assembleStaging
~~~

Android Release 支持 ARM64；默认构建的 Release APK 未签名，安装前需使用自己的密钥签名。Debug 使用本地开发入口，Staging 与 Release 使用统一部署配置。

Linux 构建与使用见 [Linux README](linux/MyProxyLinux/README.md)。生产打包校验要求先替换占位服务器配置；普通源码检查和编译允许保留示例配置。

## 平台状态

- Android 已通过实机测试；不同设备与系统版本的兼容性需分别确认。
- Windows、Linux 和 Server 提供构建与回归测试。Linux 托盘行为依赖桌面环境；GNOME/KDE 的完整桌面体验需在目标环境验收。
- 本仓库提供源码与构建输入，不携带预制安装包或现成的线上服务。

## 说明与许可

- 项目自身代码与文档采用 [MIT](LICENSE)，版权署名为 MyProxy Contributors。第三方代码、库与数据保留各自许可，见 [第三方声明](THIRD_PARTY_NOTICES.md)。
- [框架说明](docs/architecture.md)
- [配置与部署](docs/deployment.md)
- [HTTP API](server/docs/openapi.yaml)
- [第三方依赖与许可证](docs/third-party.md)
- [Android 内核源码、重建与替换](docs/android-core-rebuild.md)
- [版本记录](CHANGELOG.md)
- [维护与发布](docs/maintaining.md)
- [Windows / Android / Linux 下载页模板](server/deploy/downloads/index.html)

## 开源项目参考清单 / References

能力有来源，贡献有记录。以下列出应用依赖、随附资源、部署集成、开发模型与构建测试工具；精确包版本、内核传递依赖和开发来源见 [第三方来源与版本记录](docs/third-party.md)。保留上游版权与许可证。

| 编号 | 项目 | 在 MyProxy 中的用途 |
| --- | --- | --- |
| 1 | [Xray-core](https://github.com/XTLS/Xray-core) | 三端连接内核；Windows/Linux 使用 v26.9.9 发布资产 |
| 2 | [AndroidLibXrayLite](https://github.com/2dust/AndroidLibXrayLite) | Android 的 libv2ray AAR 与 Go/Java 桥接 |
| 3 | [3x-ui](https://github.com/MHSanaei/3x-ui) | 保留的服务器管理面板，管理 Xray 与用户连接 |
| 4 | [v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat) | Xray 发布资产所用 GeoIP/GeoSite 规则数据来源 |
| 5 | [v2fly/geoip](https://github.com/v2fly/geoip)、[domain-list-community](https://github.com/v2fly/domain-list-community) | 上述规则数据的上游 IP 与域名列表 |
| 6 | [.NET Runtime](https://github.com/dotnet/runtime) | Windows/Linux 的基础运行库、网络、存储与加密 API |
| 7 | [WPF](https://github.com/dotnet/wpf) | Windows 桌面界面 |
| 8 | [Avalonia](https://github.com/AvaloniaUI/Avalonia) | Linux 桌面、托盘、Fluent 主题与开发诊断组件 |
| 9 | [Inter](https://github.com/rsms/inter) | Avalonia 随附字体 |
| 10 | [SkiaSharp](https://github.com/mono/SkiaSharp)、[Skia](https://skia.org/) | Avalonia 的图形绘制及原生渲染依赖 |
| 11 | [HarfBuzz](https://github.com/harfbuzz/harfbuzz) | 通过 HarfBuzzSharp 提供文字整形 |
| 12 | [MicroCom](https://github.com/kekekeks/MicroCom)、[Tmds.DBus](https://github.com/tmds/Tmds.DBus) | Avalonia 原生互操作与 Linux 桌面通信 |
| 13 | [ANGLE](https://chromium.googlesource.com/angle/angle) | Avalonia.Desktop 的 Windows 原生图形依赖 |
| 14 | [AndroidX / Jetpack Compose](https://github.com/androidx/androidx) | Android 界面、Activity、导航、生命周期、ViewModel 与 DataStore |
| 15 | [Kotlin](https://github.com/JetBrains/kotlin) | Android 语言、标准库与编译插件 |
| 16 | [kotlinx.coroutines](https://github.com/Kotlin/kotlinx.coroutines) | Android 协程与异步任务 |
| 17 | [kotlinx.serialization](https://github.com/Kotlin/kotlinx.serialization) | Android JSON 序列化 |
| 18 | [OkHttp](https://square.github.io/okhttp/)、[Okio](https://square.github.io/okio/) | Android HTTPS 请求及 I/O |
| 19 | [Java annotations](https://github.com/JetBrains/java-annotations) | JVM 依赖的注解元数据 |
| 20 | [Python / CPython](https://github.com/python/cpython) | Server 标准库运行时和通用工具 |
| 21 | [SQLite](https://www.sqlite.org/) | Server 的设备、配置与用量存储 |
| 22 | [nginx](https://github.com/nginx/nginx) | 公网 HTTPS、路由白名单与静态下载页 |
| 23 | [OpenSSL](https://github.com/openssl/openssl)、[curl](https://github.com/curl/curl) | 部署中的证书检查与 HTTPS 验收 |
| 24 | [systemd](https://github.com/systemd/systemd)、[GNU Bash](https://www.gnu.org/software/bash/) | Linux 服务、自启与部署脚本 |
| 25 | [GLib / GSettings](https://docs.gtk.org/gio/class.Settings.html) | GNOME 系统代理设置与恢复 |
| 26 | [.NET SDK / MSBuild](https://github.com/dotnet/sdk)、[MSBuild](https://github.com/dotnet/msbuild) | Windows/Linux 编译与发布 |
| 27 | [OpenJDK](https://github.com/openjdk/jdk17u)、[Gradle](https://github.com/gradle/gradle) | Android 构建工具链 |
| 28 | [Android Gradle Plugin](https://android.googlesource.com/platform/tools/base/)、[Android SDK Build Tools](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/tools/aapt2/) | Android 资源、打包及构建检查 |
| 29 | [MSTest](https://github.com/microsoft/testfx)、[VSTest](https://github.com/microsoft/vstest) | Windows/Linux 单元与回归测试 |
| 30 | [JUnit 4](https://github.com/junit-team/junit4)、[Hamcrest](https://github.com/hamcrest/JavaHamcrest) | Android JVM 单测与断言 |
| 31 | [Go](https://go.dev/)、[Go mobile](https://pkg.go.dev/golang.org/x/mobile) | Xray / libv2ray 的上游编译与移动桥接工具链 |
| 32 | [checkout](https://github.com/actions/checkout)、[setup-python](https://github.com/actions/setup-python)、[setup-dotnet](https://github.com/actions/setup-dotnet)、[setup-java](https://github.com/actions/setup-java) | CI 源码检出与工具链安装 |
| 33 | [setup-android](https://github.com/android-actions/setup-android)、[upload-artifact](https://github.com/actions/upload-artifact)、[download-artifact](https://github.com/actions/download-artifact)、[attest](https://github.com/actions/attest) | CI Android SDK、检查产物与公开构建来源证明 |
| 34 | [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) | 代码开发中的 Agent 框架与工程任务编排 |
| 35 | [DeepSeek-V4.1-Flash](https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash) | 本项目开发使用的开源模型，辅助代码构建 |
| 36 | [MiMo-V2.6-Pro](https://mimo.mi.com/docs/zh-CN/news/latest/v2-6) | 本项目开发使用的开源模型，辅助代码构建 |
| 37 | [CodeQL Action](https://github.com/github/codeql-action) | GitHub 上的 C#、Java/Kotlin、Python 与 Actions 安全扫描 |
| 38 | [Dependabot](https://github.com/dependabot/dependabot-core) | NuGet、Gradle 与 GitHub Actions 依赖更新 PR |

使用、修改和再分发时保留许可证与版权说明。源码与发布包一并提供第三方许可及原生库对应源码方向。
