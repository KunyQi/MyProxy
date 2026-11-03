# MyProxy

[简体中文](README.md) · **English**

**Keep the complexity in the code. Users just connect.**

Your own server, three clients, one public API. MyProxy includes Windows, Android and Linux clients and a Python control plane. Pair once, then just switch it on and off. Connection parameters are configured in advance by the deployer.

This repository is for developers who deploy their own server; the current version is the 0.1.x public beta. To start deploying, see [Configuration and deployment](docs/deployment.en.md); to contribute, see the [contribution guide](CONTRIBUTING.en.md); for security reports, see [SECURITY.md](SECURITY.en.md). All documentation can also be browsed and searched on the [documentation site](https://kunyqi.github.io/MyProxy/en/).

## Why it exists

The starting point is simple: when you run your own VPN, the open-source tools are already capable enough, yet ordinary users still have to understand protocols, addresses and configuration before they get the simple experience of a commercial VPN. MyProxy sets out to close that gap.

The goal: enter a pairing code the first time, and from then on just use the connection switch. The server, certificates and connection parameters are filled in by the deployer or a professional developer. Smart routing and global mode are kept, with the necessary explanations in settings; ordinary users connect with the default policy, without having to learn network configuration first.

Engineering standards for the clients: lightweight, stable, maintainable, no ads. The server keeps the **3x-ui panel** for managing Xray, and adds a standard HTTP API, pairing and device management. The API has a contract, the configuration can be reviewed, and deployments can be reproduced.

Traffic forwarded according to the configuration is carried by your own server, reducing reliance on trust in third-party VPN providers. The default routing includes direct-connection rules, and what is actually forwarded is decided by the policy. Privacy depends on correct deployment, credential management and client maintenance; it cannot be guaranteed by the word "self-hosted" alone.

The code and the service are yours to control. If something is missing, add it. You can modify, test and build with the help of AI, and leave reusable improvements for the next person.

A considerable part of this project's code was built with the help of [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) and the open-source models [DeepSeek-V4.1-Flash](https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash) and [MiMo-V2.6-Pro](https://mimo.mi.com/docs/zh-CN/news/latest/v2-6).

Less configuration burden, less maintenance cost: let the tool work quietly.

## Components

| Directory | Contents |
| --- | --- |
| windows/MyProxy/ | WPF client, shared connection logic and tests |
| android/MyProxyAndroid/ | Compose client and libv2ray integration |
| linux/MyProxyLinux/ | Daemon, CLI, Avalonia tray interface and tests |
| server/ | Account and device API, admin interface, SQLite storage, 3x-ui adapter |
| scripts/ | Build, verification and packaging tools |
| deployment.json | Public server entry configuration shared by all four parts |

The framework provides configuration caching and fallback, system proxy restoration, device revocation, aggregated usage statistics and signed update verification. New installs have automatic update checks off by default; an existing explicit preference is kept. Before any update is accepted, the deployer must configure their own trust key; a check never installs a new version automatically, and installation requires the user's action.

## Configuring your own server

Change deployment.json in the repository root, for example:

~~~json
{
  "api_base_url": "https://api.example.com:8443"
}
~~~

Windows, Android and Linux use this configuration at build time; rebuild the clients after changing the entry point. The server uses the same file as its default host configuration, and the existing MYPROXY_* environment variables can explicitly override the corresponding settings. The clients use the standard HTTPS certificate chain and host name verification, and ordinary users only enter the pairing code provided by the administrator.

The connectivity check goes to your own server by default, without relying on a fixed public probe site. If you need fallback check endpoints, fill in `connectivity_check_urls` in the same configuration; see the deployment notes for details.

The .invalid addresses shipped in the repository are placeholders and must be replaced before anything can connect. The deployer also needs to configure the HTTPS certificate, the 3x-ui data plane, administrative credentials and server permissions. The control-plane API and the data-plane port are configured separately. For the full steps, see the [deployment notes](docs/deployment.en.md) and the [Server README](server/README.en.md).

## Building and testing

Dependencies: .NET SDK **8.0.424** and Python **3.12+**; Android needs JDK **17** and Android SDK **37**. Run from the repository root:

~~~text
dotnet build windows/MyProxy/MyProxy.csproj -c Release
dotnet test windows/MyProxy/Tests/MyProxy.Tests.csproj -c Debug
python -m unittest discover -s server/tests -v
python scripts/release_verify.py check
~~~

Run in android/MyProxyAndroid/ (use gradlew.bat on Windows):

~~~sh
./gradlew --no-daemon lintRelease testDebugUnitTest assembleRelease lintStaging testStagingUnitTest assembleStaging
~~~

Android Release supports ARM64; the Release APK built by default is unsigned and must be signed with your own key before installing. Debug uses a local development entry point; Staging and Release use the shared deployment configuration.

For building and using the Linux client, see the [Linux README](linux/MyProxyLinux/README.en.md). Production packaging verification requires the placeholder server configuration to be replaced first; ordinary source checks and compilation allow the example configuration to stay.

## Platform status

- Android has been tested on real devices; compatibility with different devices and system versions needs to be confirmed individually.
- Windows, Linux and the server have build and regression tests. Linux tray behaviour depends on the desktop environment; the full desktop experience under GNOME/KDE needs acceptance in the target environment.
- This repository provides source code and build inputs, with no prebuilt installers or ready-made online service.

## Notes and licenses

- The project's own code and documentation are licensed under [MIT](LICENSE), with copyright attributed to MyProxy Contributors. Third-party code, libraries and data keep their own licenses; see the [third-party notices](THIRD_PARTY_NOTICES.md).
- [Architecture](docs/architecture.en.md)
- [Configuration and deployment](docs/deployment.en.md)
- [HTTP API](server/docs/openapi.yaml)
- [Third-party dependencies and licenses](docs/third-party.en.md)
- [Android core source, rebuilding and replacement](docs/android-core-rebuild.en.md)
- [Changelog](CHANGELOG.en.md)
- [Maintenance and releases](docs/maintaining.en.md)
- [Windows / Android / Linux download page template](server/deploy/downloads/index.html)

## Open-source references

Capabilities have sources, and contributions are on record. The list below covers application dependencies, bundled resources, deployment integrations, development models, and build and test tools; exact package versions, the core's transitive dependencies and development sources are in the [third-party source and version records](docs/third-party.en.md). Upstream copyrights and licenses are retained.

| No. | Project | Use in MyProxy |
| --- | --- | --- |
| 1 | [Xray-core](https://github.com/XTLS/Xray-core) | Connection core on all three platforms; Windows/Linux use the v26.9.9 release assets |
| 2 | [AndroidLibXrayLite](https://github.com/2dust/AndroidLibXrayLite) | Android's libv2ray AAR and Go/Java bridge |
| 3 | [3x-ui](https://github.com/MHSanaei/3x-ui) | The retained server admin panel, managing Xray and user connections |
| 4 | [v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat) | Source of the GeoIP/GeoSite rule data used by the Xray release assets |
| 5 | [v2fly/geoip](https://github.com/v2fly/geoip), [domain-list-community](https://github.com/v2fly/domain-list-community) | Upstream IP and domain lists behind the rule data above |
| 6 | [.NET Runtime](https://github.com/dotnet/runtime) | Base runtime libraries, networking, storage and cryptography APIs on Windows/Linux |
| 7 | [WPF](https://github.com/dotnet/wpf) | Windows desktop interface |
| 8 | [Avalonia](https://github.com/AvaloniaUI/Avalonia) | Linux desktop, tray, Fluent theme and development diagnostics |
| 9 | [Inter](https://github.com/rsms/inter) | Font bundled with Avalonia |
| 10 | [SkiaSharp](https://github.com/mono/SkiaSharp), [Skia](https://skia.org/) | Avalonia's graphics drawing and native rendering dependencies |
| 11 | [HarfBuzz](https://github.com/harfbuzz/harfbuzz) | Text shaping through HarfBuzzSharp |
| 12 | [MicroCom](https://github.com/kekekeks/MicroCom), [Tmds.DBus](https://github.com/tmds/Tmds.DBus) | Avalonia native interop and Linux desktop communication |
| 13 | [ANGLE](https://chromium.googlesource.com/angle/angle) | Native Windows graphics dependency of Avalonia.Desktop |
| 14 | [AndroidX / Jetpack Compose](https://github.com/androidx/androidx) | Android interface, Activity, navigation, lifecycle, ViewModel and DataStore |
| 15 | [Kotlin](https://github.com/JetBrains/kotlin) | Android language, standard library and compiler plugins |
| 16 | [kotlinx.coroutines](https://github.com/Kotlin/kotlinx.coroutines) | Android coroutines and asynchronous tasks |
| 17 | [kotlinx.serialization](https://github.com/Kotlin/kotlinx.serialization) | Android JSON serialization |
| 18 | [OkHttp](https://square.github.io/okhttp/), [Okio](https://square.github.io/okio/) | Android HTTPS requests and I/O |
| 19 | [JetBrains annotations](https://github.com/JetBrains/java-annotations), [AndroidX Annotation](https://developer.android.com/jetpack/androidx/releases/annotation) | Annotation metadata for JVM dependencies |
| 20 | [Python / CPython](https://github.com/python/cpython) | Server standard library runtime and general tooling |
| 21 | [SQLite](https://www.sqlite.org/) | Server storage for devices, configuration and usage |
| 22 | [nginx](https://github.com/nginx/nginx) | Public HTTPS, route allowlist and static download page |
| 23 | [OpenSSL](https://github.com/openssl/openssl), [curl](https://github.com/curl/curl) | Certificate checks and HTTPS acceptance during deployment |
| 24 | [systemd](https://github.com/systemd/systemd), [GNU Bash](https://www.gnu.org/software/bash/) | Linux services, auto-start and deployment scripts |
| 25 | [GLib / GSettings](https://docs.gtk.org/gio/class.Settings.html) | GNOME system proxy settings and restoration |
| 26 | [.NET SDK](https://github.com/dotnet/sdk), [MSBuild](https://github.com/dotnet/msbuild) | Windows/Linux compilation and publishing |
| 27 | [OpenJDK](https://github.com/openjdk/jdk17u), [Gradle](https://github.com/gradle/gradle) | Android build toolchain |
| 28 | [Android Gradle Plugin](https://android.googlesource.com/platform/tools/base/), [Android SDK Build Tools](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/tools/aapt2/) | Android resources, packaging and build checks |
| 29 | [MSTest](https://github.com/microsoft/testfx), [VSTest](https://github.com/microsoft/vstest) | Windows/Linux unit and regression tests |
| 30 | [JUnit 4](https://github.com/junit-team/junit4), [Hamcrest](https://github.com/hamcrest/JavaHamcrest) | Android JVM unit tests and assertions |
| 31 | [Go](https://go.dev/), [Go mobile](https://pkg.go.dev/golang.org/x/mobile) | Upstream compilation and mobile bridge toolchain for Xray / libv2ray |
| 32 | [checkout](https://github.com/actions/checkout), [setup-python](https://github.com/actions/setup-python), [setup-dotnet](https://github.com/actions/setup-dotnet), [setup-java](https://github.com/actions/setup-java) | CI source checkout and toolchain installation |
| 33 | [setup-android](https://github.com/android-actions/setup-android), [upload-artifact](https://github.com/actions/upload-artifact), [download-artifact](https://github.com/actions/download-artifact), [attest](https://github.com/actions/attest) | CI Android SDK, check artifacts and public build provenance attestation |
| 34 | [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) | Agent framework and engineering task orchestration during development |
| 35 | [DeepSeek-V4.1-Flash](https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash) | Open-source model used in this project's development, assisting with code |
| 36 | [MiMo-V2.6-Pro](https://mimo.mi.com/docs/zh-CN/news/latest/v2-6) | Open-source model used in this project's development, assisting with code |
| 37 | [CodeQL Action](https://github.com/github/codeql-action) | C#, Java/Kotlin, Python and Actions security scanning on GitHub |
| 38 | [Dependabot](https://github.com/dependabot/dependabot-core) | Dependency update PRs for NuGet, Gradle and GitHub Actions |

Keep the licenses and copyright notices when using, modifying and redistributing. The source and release packages also provide the third-party licenses and directions to the corresponding source of the native libraries.
