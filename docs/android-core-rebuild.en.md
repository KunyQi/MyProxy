# Rebuilding and replacing the Android core

MyProxy's application code is licensed under MIT, AndroidLibXrayLite under LGPL-3.0, and Xray-core under MPL-2.0. Each part keeps its own license. The [third-party notices](../THIRD_PARTY_NOTICES.md) and the [libv2ray source directions](../android/MyProxyAndroid/vendor/libv2ray/SOURCE.md) are shipped with the source and the APK.

## Getting the corresponding source

`libv2ray-sources.jar` contains the Java bridge sources; the full native implementation also needs AndroidLibXrayLite, Xray-core and the Go modules. The wrapper's [go.mod / go.sum](../android/MyProxyAndroid/vendor/libv2ray/upstream/go.mod) record the build inputs; [BUILD-INFO.json](../android/MyProxyAndroid/vendor/libv2ray/BUILD-INFO.json) records the module versions of the actual AAR. The license index provides free source download addresses for each module at the corresponding version.

~~~sh
git clone https://github.com/2dust/AndroidLibXrayLite.git
cd AndroidLibXrayLite
git checkout --detach d0c6c4ae1b09c912070c8288bd0dbcc2e492ac29
~~~

You can also use the wrapper source included with this project under `vendor/libv2ray/upstream/`. Keep its license and copyright headers. When modifying the library, record which files you changed and when, and keep providing the corresponding modified source.

## Rebuilding the AAR

The upstream build workflow for this version uses Go, the Android SDK/NDK and gomobile. The actual AAR was built with Go **1.27.1**; the workflow's Android inputs are **platforms;android-37.0, build-tools;37.0.0, NDK 29.0.14206865**. These are the upstream core build tools; the MyProxy app itself uses SDK 37 and JDK 17 as listed in the README.

After installing the tools, set `JAVA_HOME`, `ANDROID_HOME` and `ANDROID_NDK_HOME` in the wrapper source directory, and put Go's bin directory on your PATH. Use a version-pinned Go mobile, so `@latest` cannot change the bridge tooling:

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

`gen_assets.sh` uses curl and jq to fetch public rule data; the `latest` data used upstream changes over time. To reuse your own build inputs, save the data, versions and digests you used and put them in assets/. This guide provides a path to modify, rebuild and relink; it does not promise a byte-for-byte reproduction of the upstream assets.

## Linking the modified library into the app

Replace `libv2ray.aar` and `libv2ray-sources.jar` in MyProxy's `android/MyProxyAndroid/vendor/libv2ray/` with the ones you built. Keep the wrapper source, module versions, licenses and change notes that correspond to the version you used, and update the digests in VERSION.txt. Then configure your own deployment.json and run in `android/MyProxyAndroid/`:

~~~sh
./gradlew --no-daemon assembleRelease
~~~

The app relinks the library through Gradle's local AAR dependency, with no need for the project maintainers' private keys or developer accounts. Release builds support ARM64; for other ABIs, use Debug or adjust the local build configuration.

## Installing your own signed modified build

Release builds produce an unsigned APK by default. Sign it with your own Android signing key, using zipalign and apksigner from the Android SDK:

~~~sh
zipalign -p -f 4 app-release-unsigned.apk app-aligned.apk
apksigner sign --ks my-release-key.jks --out MyProxy.apk app-aligned.apk
apksigner verify --verbose MyProxy.apk
adb install MyProxy.apk
~~~

Create and keep your own signing key, and never commit it to the repository. If the device already has an app with the same applicationId and a different signature, Android refuses to install over it; back up any needed data and uninstall the original app first, then install your own build, or change the applicationId. Regular sideloading does not depend on the original author's key.

When publishing a modified build, also provide the corresponding app source, directions to the library source, the build inputs, licenses and installation instructions. The download page should place the source and license materials near the binary download entry, and keep them freely accessible.
