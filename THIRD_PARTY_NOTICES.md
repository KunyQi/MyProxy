# Third-party notices

MyProxy's original code and documentation are licensed under the [MIT License](LICENSE), copyright (c) 2026 MyProxy Contributors. Third-party code, binaries, rule data and fonts retain their own copyrights and licenses. The project license does not replace those terms.

| Component | Copyright / attribution | License and source |
| --- | --- | --- |
| AndroidLibXrayLite v26.9.9 | AndroidLibXrayLite contributors / 2dust | [LGPL-3.0](licenses/LGPL-3.0.txt), with [GPL-3.0](licenses/GPL-3.0.txt); [corresponding source directions](android/MyProxyAndroid/vendor/libv2ray/SOURCE.md) |
| Go mobile bindings included in libv2ray-sources.jar | The Go Authors; original 2014 copyright headers are retained | [Go BSD license](licenses/Go-BSD-3-Clause.txt); [source and native module versions](android/MyProxyAndroid/vendor/libv2ray/SOURCE.md) |
| Xray-core | Xray-core contributors | [MPL-2.0](licenses/MPL-2.0.txt); [v26.9.9 source](https://github.com/XTLS/Xray-core/tree/v26.9.9), [Android core commit](https://github.com/XTLS/Xray-core/tree/52a412d9e2f5) |
| GeoIP / GeoSite data derived from v2ray-rules-dat | Loyalsoldier and upstream data contributors | [GPL-3.0](licenses/GPL-3.0.txt); [source and generation scripts](https://github.com/Loyalsoldier/v2ray-rules-dat), [trim script](scripts/trim_geodata.py); original and trimmed hashes are recorded in the core version manifests |
| AndroidX / Jetpack Compose | The Android Open Source Project and AndroidX contributors | [Apache-2.0](licenses/Apache-2.0.txt); [version catalog](android/MyProxyAndroid/gradle/libs.versions.toml) |
| Gradle wrapper | Gradle contributors | Its original META-INF/LICENSE is retained inside gradle-wrapper.jar; [Apache-2.0](licenses/Apache-2.0.txt) |

Native Go module versions read from the shipped AAR are recorded in [BUILD-INFO.json](android/MyProxyAndroid/vendor/libv2ray/BUILD-INFO.json); the Windows and Linux Xray binaries are recorded in [CORE-BUILD-INFO.json](licenses/CORE-BUILD-INFO.json). Their version-specific notices are distributed under [licenses/go-modules](licenses/go-modules/INDEX.json), covering the 61 distinct module versions used across these binaries. Direct and transitive application dependencies, external server integrations, development tools and model credits are listed in [docs/third-party.md](docs/third-party.md).

The libv2ray AAR and sources JAR are unmodified upstream release assets; their checksums remain in [VERSION.txt](android/MyProxyAndroid/vendor/libv2ray/VERSION.txt). The sources JAR contains Java bridge sources, not all native Go source. Source retrieval, rebuilding the native library, replacing it in MyProxy, and installing a locally signed application are described in [the rebuild guide](docs/android-core-rebuild.md).

MyProxy does not add restrictions on modifying the LGPL-covered library or reverse engineering the application to debug those modifications. Publishing a modified library also requires preserving its upstream notices, identifying modifications, and making the corresponding source available under the applicable license. Distributors must keep the source directions available alongside their binaries; an attribution list alone does not replace license texts or corresponding source.

Windows and Linux packages carry this notice, LICENSE, licenses/, and the public source/rebuild documentation. Android packages carry them in assets/myproxy-licenses/. Preserve any additional package-specific copyright and NOTICE files when publishing a self-contained runtime or adding dependencies.
