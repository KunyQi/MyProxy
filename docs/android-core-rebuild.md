# Android 内核重建与替换

MyProxy 的应用代码使用 MIT，AndroidLibXrayLite 使用 LGPL-3.0，Xray-core 使用 MPL-2.0。各部分保留自己的许可。[第三方声明](../THIRD_PARTY_NOTICES.md) 与 [libv2ray 源码方向](../android/MyProxyAndroid/vendor/libv2ray/SOURCE.md) 随源码和 APK 提供。

## 取得对应源码

`libv2ray-sources.jar` 是 Java 桥接源码，完整原生实现还需要 AndroidLibXrayLite、Xray-core 与 Go 模块。包装层 [go.mod / go.sum](../android/MyProxyAndroid/vendor/libv2ray/upstream/go.mod) 记录构建输入；[BUILD-INFO.json](../android/MyProxyAndroid/vendor/libv2ray/BUILD-INFO.json) 记录实际 AAR 的模块版本。许可索引提供各模块对应版本的免费源码下载地址。

~~~sh
git clone https://github.com/2dust/AndroidLibXrayLite.git
cd AndroidLibXrayLite
git checkout --detach d0c6c4ae1b09c912070c8288bd0dbcc2e492ac29
~~~

也可使用本项目随附的 `vendor/libv2ray/upstream/` 包装层源码。保留其许可证和版权头。修改库时记录所改文件与日期，并继续提供修改后的对应源码。

## 重建 AAR

上游该版本的构建工作流使用 Go、Android SDK/NDK 和 gomobile。实际 AAR 的 Go 版本为 **1.27.1**；工作流的 Android 输入为 **platforms;android-37.0、build-tools;37.0.0、NDK 29.0.14206865**。这些是上游内核构建工具；MyProxy 应用使用 README 中的 SDK 37 和 JDK 17。

安装工具后，在包装层源码目录设置 `JAVA_HOME`、`ANDROID_HOME` 和 `ANDROID_NDK_HOME`，并将 Go 的 bin 目录放入 PATH。使用版本锁定的 Go mobile，避免用 `@latest` 改变桥接工具：

~~~sh
go install golang.org/x/mobile/cmd/gomobile@v0.0.0-20260908204917-8b95e45f8d3e
go install golang.org/x/mobile/cmd/gobind@v0.0.0-20260908204917-8b95e45f8d3e
go mod download
mkdir -p assets data
bash gen_assets.sh download
cp data/*.dat assets/
gomobile init
gomobile bind -v -androidapi 24 -trimpath -ldflags='-s -w -buildid= -checklinkname=0' ./
~~~

`gen_assets.sh` 使用 curl 和 jq 获取公开规则数据；上游使用的 `latest` 数据会随时间变化。为重用自己的构建输入，可保存所使用的数据、版本与摘要后放入 assets/。本指南提供修改、重建和重新链接路径，不承诺与上游资产逐字节复现。

## 将修改的库链接到应用

将生成的 `libv2ray.aar` 和 `libv2ray-sources.jar` 替换到 MyProxy 的 `android/MyProxyAndroid/vendor/libv2ray/`。保留与所用版本对应的包装层源码、模块版本、许可和变更说明，更新 VERSION.txt 中的摘要。然后配置自己的 deployment.json，在 `android/MyProxyAndroid/` 运行：

~~~sh
./gradlew --no-daemon assembleRelease
~~~

应用通过 Gradle 的本地 AAR 依赖重新链接库，不需要项目维护者的私有密钥或开发账号。Release 支持 ARM64；其他 ABI 可使用 Debug 或调整本地构建配置。

## 安装自己签名的修改版本

Release 默认生成未签 APK。用自己的 Android 签名密钥，经 Android SDK 的 zipalign 和 apksigner 签名：

~~~sh
zipalign -p -f 4 app-release-unsigned.apk app-aligned.apk
apksigner sign --ks my-release-key.jks --out MyProxy.apk app-aligned.apk
apksigner verify --verbose MyProxy.apk
adb install MyProxy.apk
~~~

自行创建和保管签名密钥，不提交进仓库。若设备已安装相同 applicationId 且签名不同的应用，Android 会拒绝覆盖；可在备份必要数据后卸载原应用，再安装自己的版本，或修改 applicationId。常规侧载不依赖原作者密钥。

公开修改版时，一并提供对应应用源码、库源码方向、构建输入、许可和安装说明。下载页应将源码和许可材料放在二进制下载入口附近，保持免费可访问。
