# libv2ray 对应源码

这里的 libv2ray.aar 与 libv2ray-sources.jar 来自 AndroidLibXrayLite v26.9.9，保持上游字节不变；[VERSION.txt](VERSION.txt) 记录下载地址与 SHA-256。AndroidLibXrayLite 使用 [LGPL-3.0](../../../../licenses/LGPL-3.0.txt)，其所引用的 [GPL-3.0](../../../../licenses/GPL-3.0.txt) 一并提供。Go Java 桥接源文件保留 Go Authors 版权头，对应 [BSD 许可](../../../../licenses/Go-BSD-3-Clause.txt)。

源码下载方向与 AAR 放在同一目录，供接收者免费取得、修改和重建：

| 组成 | 对应源码 |
| --- | --- |
| AndroidLibXrayLite / Go 包装层 | v26.9.9 对应提交 [d0c6c4ae1b09c912070c8288bd0dbcc2e492ac29](https://github.com/2dust/AndroidLibXrayLite/tree/d0c6c4ae1b09c912070c8288bd0dbcc2e492ac29)，[源码 ZIP](https://github.com/2dust/AndroidLibXrayLite/archive/d0c6c4ae1b09c912070c8288bd0dbcc2e492ac29.zip)；包装层和构建输入另在本目录 [upstream](upstream/README.md) 随附，[文件摘要](upstream/SOURCES.json) 可逐项核对 |
| Xray-core | [52a412d9e2f5 对应源码](https://github.com/XTLS/Xray-core/tree/52a412d9e2f5)，[源码 ZIP](https://github.com/XTLS/Xray-core/archive/52a412d9e2f5.zip)；MPL-2.0 |
| Go mobile / gobind | [8b95e45f8d3e 对应源码](https://github.com/golang/mobile/tree/8b95e45f8d3e)，[源码 ZIP](https://github.com/golang/mobile/archive/8b95e45f8d3e.zip)；Go BSD 许可 |
| 原生库中其余 Go 模块 | [BUILD-INFO.json](BUILD-INFO.json) 给出从 AAR 读取的版本及 Go 模块摘要；[许可与源码下载索引](../../../../licenses/go-modules/INDEX.json) 给出各版本的完整源码 ZIP 地址和许可文本 |
| Go 工具链 | 原生库记录为 go1.27.1；[Go 官方下载与源码](https://go.dev/dl/#go1.27.1) |
| 嵌入的规则数据 | [v2ray-rules-dat 的源列表和生成流程](https://github.com/Loyalsoldier/v2ray-rules-dat)，[Loyalsoldier/geoip](https://github.com/Loyalsoldier/geoip)；上游生成脚本随包装层源码提供 |

对于每个索引内的 Go 模块，也可从公开 Go module proxy 的 `<module>/@v/<version>.zip` 取得完整源码；URL 已按 Go 模块路径转义规则处理。下载无需账号或私有凭据。`go mod download` 使用包装层的 go.mod / go.sum 取得这些构建输入。

本项目没有上游构建机的完整环境快照；记录的原生模块和发布资产摘要可核对来源，但不宣称重新构建后与上游 AAR 逐字节一致。替换与安装自己的修改版本见 [Android 内核重建指南](../../../../docs/android-core-rebuild.md)。发布者若另行替换 AAR，应同时更新版本、摘要、源码方向和许可索引，并在二进制下载位置继续提供这些材料。
