import groovy.json.JsonSlurper
import groovy.json.JsonOutput
import java.net.URI
import org.gradle.api.tasks.Sync

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

val deploymentFile = rootProject.file("../../deployment.json")
val deployment = JsonSlurper().parse(deploymentFile) as? Map<*, *>
    ?: error("deployment.json must contain a JSON object")
val deploymentApiBaseUrl = deployment["api_base_url"] as? String
    ?: error("deployment.json requires api_base_url")
val deploymentOrigin = URI(deploymentApiBaseUrl)
val deploymentAuthority = deploymentOrigin.rawAuthority.orEmpty()
val deploymentHost = if (deploymentAuthority.startsWith('[')) deploymentAuthority.substringBefore(']') + "]" else deploymentAuthority.substringBefore(':')
val deploymentLabels = deploymentHost.split('.')
val canonicalDeploymentHost = when {
    deploymentHost.startsWith('[') -> deploymentHost.matches(Regex("\\[[0-9a-fA-F:.]+]"))
    deploymentHost.length !in 1..253 || deploymentHost.endsWith('.') -> false
    deploymentLabels.all { label -> label.all { it in '0'..'9' } } ->
        deploymentLabels.size == 4 && deploymentLabels.all { label ->
            val value = label.toIntOrNull()
            value != null && value in 0..255 && value.toString() == label
        }
    deploymentLabels.all { it.matches(Regex("(?i)(0x[0-9a-f]+|[0-9]+)")) } -> false
    else -> deploymentLabels.all { it.matches(Regex("[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?")) }
}
require(deploymentApiBaseUrl.isNotEmpty() && deploymentApiBaseUrl.none { it.isWhitespace() } &&
    '\\' !in deploymentApiBaseUrl && deploymentOrigin.scheme.equals("https", ignoreCase = true) &&
    !deploymentOrigin.host.isNullOrEmpty() && canonicalDeploymentHost && deploymentOrigin.userInfo == null &&
    deploymentOrigin.rawQuery == null && deploymentOrigin.rawFragment == null &&
    deploymentOrigin.rawPath in listOf("", "/") &&
    deploymentOrigin.rawAuthority?.endsWith(':') != true &&
    (deploymentOrigin.port == -1 || deploymentOrigin.port in 1..65535)) {
    "deployment.json api_base_url must be an HTTPS origin without credentials, path, query or fragment"
}

require(deployment.keys.all { it in setOf("api_base_url", "connectivity_check_urls") }) {
    "deployment.json contains an unknown setting"
}
val deploymentConnectivityUrls = if (deployment.containsKey("connectivity_check_urls")) {
    val urls = deployment["connectivity_check_urls"] as? List<*>
        ?: error("connectivity_check_urls requires one to four HTTPS URLs")
    require(urls.size in 1..4) { "connectivity_check_urls requires one to four HTTPS URLs" }
    urls.map { value ->
        val text = value as? String ?: error("connectivity_check_urls requires strings")
        val uri = URI(text)
        val authority = uri.rawAuthority.orEmpty()
        // Reuse the origin's strict host/port validator rather than accepting URI normalisation.
        val host = if (authority.startsWith('[')) authority.substringBefore(']') + "]" else authority.substringBefore(':')
        val labels = host.split('.')
        val canonicalHost = when {
            host.startsWith('[') -> host.matches(Regex("\\[[0-9a-fA-F:.]+]"))
            host.length !in 1..253 || host.endsWith('.') -> false
            labels.all { it.all { c -> c in '0'..'9' } } -> labels.size == 4 && labels.all {
                val number = it.toIntOrNull(); number != null && number in 0..255 && number.toString() == it
            }
            labels.all { it.matches(Regex("(?i)(0x[0-9a-f]+|[0-9]+)")) } -> false
            else -> labels.all { it.matches(Regex("[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?")) }
        }
        require(text.isNotEmpty() && text.none { it.isWhitespace() || it.code < 32 || it.code == 127 } &&
            '\\' !in text && uri.scheme.equals("https", ignoreCase = true) && !uri.host.isNullOrEmpty() && canonicalHost &&
            uri.userInfo == null && uri.rawQuery == null && uri.rawFragment == null &&
            !authority.endsWith(':') && (uri.port == -1 || uri.port in 1..65535)) {
            "connectivity_check_urls requires HTTPS URLs without credentials, query or fragment"
        }
        text
    }.distinct()
} else listOf(deploymentApiBaseUrl.trimEnd('/') + "/connectivity-check")

// Keep the user-facing license and corresponding-source documents inside every
// APK. The source tree is copied under one asset prefix with repository-relative
// paths intact, so Markdown links between notices and source directions remain
// usable after extraction from the APK.
val repositoryRoot = rootProject.file("../../").canonicalFile
val androidLicenseAssetPaths = listOf(
    "LICENSE",
    "THIRD_PARTY_NOTICES.md",
    "licenses/SOURCES.json",
    "licenses/Apache-2.0.txt",
    "licenses/GPL-3.0.txt",
    "licenses/LGPL-3.0.txt",
    "licenses/MPL-2.0.txt",
    "licenses/Go-BSD-3-Clause.txt",
    "licenses/go-modules/INDEX.json",
    "docs/android-core-rebuild.md",
    "docs/third-party.md",
    "scripts/trim_geodata.py",
    "android/MyProxyAndroid/gradle/libs.versions.toml",
    "android/MyProxyAndroid/vendor/libv2ray/SOURCE.md",
    "android/MyProxyAndroid/vendor/libv2ray/BUILD-INFO.json",
    "android/MyProxyAndroid/vendor/libv2ray/VERSION.txt",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/README.md",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/SOURCES.json",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/LICENSE",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/libv2ray_utils.go",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/libv2ray_main.go",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/libv2ray_certSha256.go",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/libv2ray_android.go",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/go.mod",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/go.sum",
    "android/MyProxyAndroid/vendor/libv2ray/upstream/gen_assets.sh",
)
val goModuleLicenseIndexFile = repositoryRoot.resolve("licenses/go-modules/INDEX.json")
val goModuleLicenseIndex = JsonSlurper().parse(goModuleLicenseIndexFile) as? Map<*, *>
    ?: error("licenses/go-modules/INDEX.json must contain a JSON object")
val goModuleLicenseRecords = goModuleLicenseIndex["modules"] as? List<*>
    ?: error("licenses/go-modules/INDEX.json must contain a modules array")
require(goModuleLicenseRecords.isNotEmpty()) {
    "licenses/go-modules/INDEX.json must list the native Go modules"
}
val versionedGoLicenseAssetPaths = goModuleLicenseRecords.flatMap { moduleValue ->
    val module = moduleValue as? Map<*, *>
        ?: error("licenses/go-modules/INDEX.json contains an invalid module record")
    val licenseRecords = module["licenses"] as? List<*>
        ?: error("licenses/go-modules/INDEX.json module is missing its licenses array")
    licenseRecords.map { licenseValue ->
        val license = licenseValue as? Map<*, *>
            ?: error("licenses/go-modules/INDEX.json contains an invalid license record")
        val relativePath = license["file"] as? String
            ?: error("licenses/go-modules/INDEX.json license record is missing its file path")
        val pathParts = relativePath.replace('\\', '/').split('/')
        require(relativePath.isNotBlank() && pathParts.none { it.isBlank() || it == "." || it == ".." }) {
            "licenses/go-modules/INDEX.json contains an unsafe license path"
        }
        "licenses/go-modules/$relativePath"
    }
}
val requiredAndroidLicenseAssets = androidLicenseAssetPaths + versionedGoLicenseAssetPaths
val missingAndroidLicenseAssets = requiredAndroidLicenseAssets.distinct().filterNot {
    repositoryRoot.resolve(it).isFile
}
require(missingAndroidLicenseAssets.isEmpty()) {
    "Android license/source package inputs are missing: ${missingAndroidLicenseAssets.joinToString()}"
}
val generatedLicenseAssets = layout.buildDirectory.dir("generated/myproxyLicenseAssets")
val prepareMyProxyLicenseAssets = tasks.register<Sync>("prepareMyProxyLicenseAssets") {
    from(repositoryRoot) {
        include(
            "LICENSE",
            "THIRD_PARTY_NOTICES.md",
            "licenses/**",
            "docs/android-core-rebuild.md",
            "docs/third-party.md",
            "scripts/trim_geodata.py",
            "android/MyProxyAndroid/gradle/libs.versions.toml",
            "android/MyProxyAndroid/vendor/libv2ray/SOURCE.md",
            "android/MyProxyAndroid/vendor/libv2ray/BUILD-INFO.json",
            "android/MyProxyAndroid/vendor/libv2ray/VERSION.txt",
            "android/MyProxyAndroid/vendor/libv2ray/upstream/**",
        )
        into("myproxy-licenses")
    }
    into(generatedLicenseAssets)
    doLast {
        val missingOutputs = requiredAndroidLicenseAssets.distinct().filterNot {
            generatedLicenseAssets.get().file("myproxy-licenses/$it").asFile.isFile
        }
        check(missingOutputs.isEmpty()) {
            "Android license/source asset generation omitted: ${missingOutputs.joinToString()}"
        }
    }
}

android {
    namespace = "com.myproxy.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.myproxy.android"
        minSdk = 24
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0"
        buildConfigField("String", "DEPLOYMENT_API_BASE_URL", "\"${deploymentApiBaseUrl.trimEnd('/')}\"")
        buildConfigField("String[]", "DEPLOYMENT_CONNECTIVITY_CHECK_URLS",
            "new String[] { ${deploymentConnectivityUrls.joinToString(", ") { JsonOutput.toJson(it) }} }")

        // 只保留英文与中文资源，丢掉 androidx/compose 带的其余语言字符串。
        resourceConfigurations += setOf("en", "zh")
    }

    buildTypes {
        debug {
            buildConfigField("String", "ENV_MODE", "\"LOCAL\"")
            // 默认保留全部四套 ABI，模拟器（x86_64）因此还能用——代价是 debug
            // APK 约 150MB，其中绝大部分是另外三套用不上的原生核心。
            // 只在真机上调试时加 -PmyproxyDebugArm64Only=true，包会降到约 40MB。
            // 只影响 debug/staging，release 本来就只打 arm64，CI 不传这个属性，
            // 因此发布路径与门禁完全不受影响。
            if (providers.gradleProperty("myproxyDebugArm64Only").orNull == "true") {
                ndk {
                    abiFilters += "arm64-v8a"
                }
            }
        }
        create("staging") {
            initWith(getByName("debug"))
            buildConfigField("String", "ENV_MODE", "\"STAGING\"")
            matchingFallbacks += listOf("debug")
        }
        release {
            buildConfigField("String", "ENV_MODE", "\"PRODUCTION\"")
            // Release supports ARM64 phones. Keep emulator ABIs in debug so
            // local testing remains broad, while avoiding shipping two native
            // cores that cannot run on supported production devices.
            ndk {
                abiFilters += "arm64-v8a"
            }

            // Minify is enabled to verify proguard-rules.pro.
            // Requires a local keystore for signed APK production.
            isMinifyEnabled = true
            // R8 已开，顺带裁掉未引用的资源——Compose 工程这一项省得可观。
            isShrinkResources = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    // 依赖清单块只对 Play 上架有意义（它是给 Play 看的签名元数据）。本项目
    // 走自签名 APK 直装，它留在包里只是多一份依赖列表。
    dependenciesInfo {
        includeInApk = false
        includeInBundle = false
    }

    // 单个 APK：不启用 ABI/密度分包，abiFilters 已经把原生库限定成 arm64。
    // .so **压缩**打包（extractNativeLibs=true，安装时解压到应用的 lib 目录）：
    // 内核 libgojni.so 未压缩 34.0 MB、压缩后 11.5 MB，APK 因此从约 36 MB 降到约 14 MB。
    // 代价在手机的安装占用上：APK 本体留在系统里、解压出的 .so 另占一份，总共比
    // 「未压缩、从 APK 直接映射」多约 11 MB。本项目是直装 APK（没有应用商店替我们按设备
    // 分发），用户实际要下载、传输的就是这个文件，所以取下载体积。
    // 加载方式不变：System.loadLibrary 两种打包都认，gomobile 桥接代码无需任何改动。
    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
        resources {
            excludes += setOf(
                // kotlinx-coroutines 的调试探针元数据，只有开了 coroutines-debug
                // 才会去读；这里没有那个依赖。
                "DebugProbesKt.bin",
                // Version stamps, AGP internals, and Kotlin tooling metadata are not notices.
                "/META-INF/*.version",
                "/META-INF/com.android.tools/**",
                "kotlin-tooling-metadata.json",
            )
            // Keep dependency license files instead of dropping them. Multiple
            // libraries commonly ship the same Apache/LGPL notice.
            merges += setOf("/META-INF/AL2.0", "/META-INF/LGPL2.1")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }
}

android.sourceSets.getByName("main").assets.srcDir(generatedLicenseAssets)
tasks.named("preBuild").configure {
    dependsOn(prepareMyProxyLicenseAssets)
}

dependencies {
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.datastore.preferences)
    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)
    implementation(files("../vendor/libv2ray/libv2ray.aar"))

    testImplementation(libs.junit)

    debugImplementation(libs.androidx.compose.ui.tooling)
}
