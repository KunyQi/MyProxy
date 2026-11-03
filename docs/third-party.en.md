# Third-party dependencies and licenses

This file records the open-source projects used by the current source, their version evidence and the upstream dependency lists, and is meant to be read together with the [References in the README](../README.en.md#open-source-references). The project's own code and documentation are licensed under [MIT](../LICENSE); the copyrights and licenses of third-party parts are kept independently, see the [third-party notices](../THIRD_PARTY_NOTICES.md).

| Project | Purpose and local version evidence |
| --- | --- |
| [Xray-core](https://github.com/XTLS/Xray-core) | Windows/Linux v26.9.9; the [Windows manifest](../windows/MyProxy/Assets/Core/VERSION.txt) and [Linux manifest](../linux/MyProxyLinux/assets/VERSION.txt) keep the source and SHA-256; [bundled MPL-2.0](../windows/MyProxy/Assets/Core/LICENSE) |
| [AndroidLibXrayLite](https://github.com/2dust/AndroidLibXrayLite) | v26.9.9, libVersion 40; the [vendor manifest](../android/MyProxyAndroid/vendor/libv2ray/VERSION.txt) records the AAR, the sources JAR and the corresponding Xray commit; the full texts of [LGPL-3.0](../licenses/LGPL-3.0.txt) and [GPL-3.0](../licenses/GPL-3.0.txt) are included, with the [corresponding source and replacement guide](android-core-rebuild.en.md) |
| [v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat) | The GeoIP/GeoSite source is confirmed by Xray v26.9.9's [asset update workflow](https://github.com/XTLS/Xray-core/blob/v26.9.9/.github/workflows/scheduled-assets-update.yml); the project trims the data to the tags it uses, and the manifests record the original and trimmed digests |
| [v2fly/geoip](https://github.com/v2fly/geoip), [domain-list-community](https://github.com/v2fly/domain-list-community) | Upstream generators and community lists for the rule data; the data and generators follow their own bundled licenses |
| [.NET Runtime](https://github.com/dotnet/runtime), [WPF](https://github.com/dotnet/wpf) | .NET 8, Windows WPF / Linux common runtime libraries; the actual self-contained runtime version is recorded in the build artifact manifest |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia), [Inter](https://github.com/rsms/inter) | Avalonia/Desktop/Fluent/Fonts.Inter 11.3.22, Diagnostics in Debug only; the [GUI project](../linux/MyProxyLinux/gui/MyProxy.Linux.Gui.csproj) pins the direct package versions |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / [Skia](https://skia.org/), [HarfBuzz](https://github.com/harfbuzz/harfbuzz), [MicroCom](https://github.com/kekekeks/MicroCom), [Tmds.DBus](https://github.com/tmds/Tmds.DBus), [ANGLE](https://chromium.googlesource.com/angle/angle) | Avalonia's transitive dependencies for native rendering, text shaping and desktop interop; the corresponding NuGet package dependencies and native asset records are authoritative |
| [AndroidX / Compose](https://github.com/androidx/androidx) | Core KTX, Activity Compose, UI/Graphics/Tooling/Preview, Material3, Navigation, Lifecycle Runtime/ViewModel, DataStore; versions and BOM in the [version catalog](../android/MyProxyAndroid/gradle/libs.versions.toml) |
| [Kotlin](https://github.com/JetBrains/kotlin) | 2.4.21, AGP's built-in Kotlin plus the Compose/Serialization compiler plugins and standard library |
| [kotlinx.coroutines](https://github.com/Kotlin/kotlinx.coroutines), [kotlinx.serialization](https://github.com/Kotlin/kotlinx.serialization) | 1.11.0 / 1.12.0; Android asynchronous tasks and JSON |
| [OkHttp](https://square.github.io/okhttp/), [Okio](https://square.github.io/okio/), [AndroidX annotations](https://developer.android.com/jetpack/androidx/releases/annotation) | OkHttp 5.5.1; Okio and the annotations are resolved through the Android dependency tree |
| [Python / CPython](https://github.com/python/cpython), [SQLite](https://www.sqlite.org/) | The server uses only the Python standard library; SQLite is provided through sqlite3, and the actual version follows the Python/system build |
| [3x-ui](https://github.com/MHSanaei/3x-ui) | External deployment integration; the original panel is kept, and the adapter reads and writes its device and configuration data |
| [nginx](https://github.com/nginx/nginx), [OpenSSL](https://github.com/openssl/openssl), [curl](https://github.com/curl/curl) | External HTTPS gateway, certificate verification, download and acceptance tools; versions are recorded by the deployment environment |
| [systemd](https://github.com/systemd/systemd), [GNU Bash](https://www.gnu.org/software/bash/), [GLib / GSettings](https://docs.gtk.org/gio/class.Settings.html) | User/server service management, deployment scripts, GNOME proxy settings |
| [.NET SDK](https://github.com/dotnet/sdk), [MSBuild](https://github.com/dotnet/msbuild) | SDK 8.0.424, see [global.json](../global.json); build and publish |
| [OpenJDK](https://github.com/openjdk/jdk17u), [Gradle](https://github.com/gradle/gradle), [AGP](https://android.googlesource.com/platform/tools/base/), [Android SDK Build Tools](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/tools/aapt2/) | JDK 17, Gradle 9.8.2, AGP 9.5.0, SDK/Build Tools 37; the wrapper JAR and distribution digests are pinned |
| [MSTest](https://github.com/microsoft/testfx), [VSTest](https://github.com/microsoft/vstest) | MSTest Framework/Adapter 3.11.1, Microsoft.NET.Test.Sdk 18.10.1; Windows/Linux test projects |
| [JUnit 4](https://github.com/junit-team/junit4), [Hamcrest](https://github.com/hamcrest/JavaHamcrest) | JUnit 4.13.2 and its transitive assertion library; Android JVM tests |
| [MkDocs](https://github.com/mkdocs/mkdocs), [Material for MkDocs](https://github.com/squidfunk/mkdocs-material), [jieba](https://github.com/fxsjy/jieba) | Documentation site build tools (BSD-2-Clause / MIT / MIT); versions and digests in [docs-requirements.txt](../scripts/docs-requirements.txt); used only to generate the documentation site, never part of client or server artifacts |
| [Go](https://go.dev/), [Go mobile](https://pkg.go.dev/golang.org/x/mobile) | The native AAR records Go 1.27.1 and mobile 8b95e45f8d3e; the bridge source files keep the Go Authors' copyright headers and the [full BSD license text](../licenses/Go-BSD-3-Clause.txt); regular client builds do not need to recompile the Go core |

## Upstream transitive dependency records

The complete Go module entries for the core versions are recorded in [Xray v26.9.9 go.mod](https://github.com/XTLS/Xray-core/blob/v26.9.9/go.mod), [AndroidLibXrayLite v26.9.9 go.mod](https://github.com/2dust/AndroidLibXrayLite/blob/v26.9.9/go.mod) and their respective go.sum files, covering projects such as QUIC, REALITY, uTLS, gRPC, Protobuf, gVisor, WireGuard, DNS and the Go extended libraries. Their sources and licenses are checked against the locked module versions.

The [actual Android native module list](../android/MyProxyAndroid/vendor/libv2ray/BUILD-INFO.json) and the [desktop core module list](../licenses/CORE-BUILD-INFO.json) are read from the Go build info of each binary; the [per-module license index](../licenses/go-modules/INDEX.json) covers the 61 module versions used across the three platforms, keeping the source ZIP address, download digest and full license text for each version. The sources and SHA-256 of the five general licenses are in [SOURCES.json](../licenses/SOURCES.json).

The specific transitive packages of managed libraries are recorded by NuGet's project.assets.json and Gradle's dependencies output. Releases keep the licenses and native asset copyright files of the corresponding dependencies; the README cites by project, and multiple packages from the same project share one source entry.

## CI projects

The current workflows pin Action commits in [ci-cross-platform.yml](../.github/workflows/ci-cross-platform.yml) and the [documentation site workflow](../.github/workflows/pages.yml), using the following open-source tools:

- [actions/checkout](https://github.com/actions/checkout): v5.0.0.
- [actions/setup-python](https://github.com/actions/setup-python): v7.0.0.
- [actions/setup-dotnet](https://github.com/actions/setup-dotnet): v6.0.0.
- [actions/setup-java](https://github.com/actions/setup-java): v6.0.1.
- [android-actions/setup-android](https://github.com/android-actions/setup-android): v4.
- [actions/upload-artifact](https://github.com/actions/upload-artifact): v4.6.2.
- [actions/download-artifact](https://github.com/actions/download-artifact): v8.0.1.
- [actions/attest](https://github.com/actions/attest): v4.2.2.
- [actions/upload-pages-artifact](https://github.com/actions/upload-pages-artifact), [actions/deploy-pages](https://github.com/actions/deploy-pages): v5.0.0 and v5.0.1, publishing the documentation site.
- [github/codeql-action](https://github.com/github/codeql-action): the [security scanning workflow](../.github/workflows/codeql.yml) pins the commit for v4, 2892aa5e19bbd11bc0cff5427e3b750a04d9e3c2.
- [Dependabot](https://github.com/dependabot/dependabot-core): the [update configuration](../.github/dependabot.yml) tracks NuGet, Gradle and GitHub Actions.

## Development tools and model sources

A considerable part of this project's code was built with the help of the following open-source tools and models:

| Source | Purpose and version record |
| --- | --- |
| [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) | Agent framework and engineering task orchestration; MIT-licensed upstream; the specific commit used for development is not recorded with this project |
| [DeepSeek-V4.1-Flash](https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash) | Open-source model that assisted in building the code; corresponds to the V4.1F used during development |
| [MiMo-V2.6-Pro](https://mimo.mi.com/docs/zh-CN/news/latest/v2-6) | Open-source model that assisted in building the code; see the official release and model card for its source and license |

These entries record development sources. Running the regular clients and server does not require installing any model or agent framework.

## Copyright and licenses

Third-party copyright notices in source files and archives are kept. The complete upstream license information is defined by the official source, in-package files and release notes of the corresponding version; this list is distributed together with the bundled licenses and the corresponding source directions. The Android APK's assets/myproxy-licenses/ and the Windows/Linux packages contain the project license, the third-party notices and the full license directory.
