# MyProxy 下载页模板

`index.html` 是可选的静态三端下载页，桌面并排展示，手机纵向展示。页面使用本地系统字体和内联样式，适合直接由 nginx 提供。

| 下载入口 | 部署者提供的文件 |
| --- | --- |
| `/windows` | Windows x64 的 `MyProxy.exe` |
| `/android` | 已签名的 Android ARM64 APK |
| `/linux` | Linux x64 的 `MyProxy-linux-x64.tar.gz` |

部署者在 nginx 模板预留的 `/etc/nginx/myproxy-downloads/*.locations` 中配置这些精确路由，并发布自己的安装包。Linux 压缩包应保留桌面入口、使用说明及第三方许可证；ARM64 等其他架构需提供对应文件和页面入口。页面版本号应与发布文件一致。

分发安装包时，同时在下载入口附近提供对应版本的 MyProxy 源码与 [第三方源码、许可及重建说明](../../../docs/android-core-rebuild.md)，保持免费可访问。Windows/Linux 打包工具随包携带 LICENSE、THIRD_PARTY_NOTICES.md 和 licenses/；Android APK 在 assets/myproxy-licenses/ 携带这些材料。修改内核后同步更新源码方向、版本与摘要。

页脚已链接到公开仓库和第三方声明。发布安装包时将源码入口固定到对应版本的标签或提交；派生版本使用自己的源码仓库，并保留第三方源码获取说明。

本目录仅保留页面源码。下载链接需在部署时映射到真实文件；模板没有预置安装包、服务器地址、广告、追踪脚本、外部字体或 CDN。
