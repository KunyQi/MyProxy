# 维护与发布

这个仓库为自建服务器的开发者提供框架、源码和构建输入。普通用户的连接入口由部署者配置，公开仓库不提供共用账号或现成的 VPN 服务。

## 首次建立公开仓库

公开仓库为 [KunyQi/MyProxy](https://github.com/KunyQi/MyProxy)，默认分支使用 `main`。只推送清理后的公开分支；内部仓库的其他分支、标签和旧历史继续保留在本地，不使用 `--all` 或 `--mirror` 上传。

首次公开前，在 GitHub 仓库设置中启用 Issues、Private vulnerability reporting、Dependabot alerts 与可用的 secret scanning/push protection。启用 GitHub Actions，运行交叉平台 CI 与 CodeQL 工作流；安全报告入口应能从 Security 页面找到。

为默认分支配置 ruleset：通过 PR 合入、至少一次审查、解决审查讨论，并要求以下检查成功：Release policy and packaging tests、Server tests and deployment syntax、Android lint, unit tests, and release APK、Linux client build, tests, and tray GUI、Windows tests, core hashes, and CI artifact。首次工作流完成后从实际检查列表选取名称。将 CodeQL 的 C#、Java/Kotlin、Python 和 Actions 分析纳入审查。

定期审查 Dependabot PR 与代码扫描结果，先复现、测试，再合入。自动更新工具不替代许可、摘要、ABI 和运行行为核对。修改仓库默认分支名称时同步调整工作流触发分支与本文。

## 发布前验证

先运行 [贡献指南](../CONTRIBUTING.md) 中与本版相关的检查。许可校验验证项目 MIT、第三方原文摘要、实际原生模块与许可索引，并要求原生包装层源码和重建文档存在。

~~~sh
python scripts/release_verify.py check
python -m unittest discover -s scripts -p 'test_*.py' -v
python -m unittest discover -s server/tests -v
~~~

生产客户端分发前，将 deployment.json 改为自己的 HTTPS 源站，验证配置与部署：

~~~sh
python scripts/release_verify.py check --require-configured
~~~

在实际部署中确认配对、连接、取消、断开、系统代理恢复、设备撤销和服务器证书续期；Android 使用目标手机，Linux 使用目标桌面环境。标准 TLS 失败与守护进程失联应表现为明确错误状态，不能沿用旧的连接成功显示。

更换 Xray/libv2ray 时更新对应版本、二进制摘要、Go build info、许可索引、源码下载方向和重建说明。保留规则数据的来源与裁剪摘要。不要让新增依赖的许可材料在压缩、裁剪或打包时丢失。

## 安装包与源代码

Windows 的打包入口是 `python scripts/package_delivery.py`；预检使用 `--preflight`。Linux 使用 `python scripts/package_linux.py --preflight` 和 `--build --rid linux-x64`。两者携带公开说明、MIT 与第三方许可目录。Windows 产物需要 .NET 8 Desktop Runtime；Linux 包的 CLI 与 GUI 共享自包含运行时。

Android Release 默认未签名；使用自己的密钥签名，验证包内 assets/myproxy-licenses/ 与实际源码版本一致。原生库修改与自行签名安装见 [Android 指南](android-core-rebuild.md)。所有平台说明清楚签名状态、支持架构、版本、摘要和已验证范围。

在安装包下载入口附近提供对应 MyProxy 源码及第三方源码下载方向，保持免费可获取；不要只提供二进制。发布时提供校验摘要，并更新 CHANGELOG。签名私钥、真实服务器配置与运行数据库留在部署者的私有环境。

发布清单的校验摘要可由现有工具生成。SBOM 命令必须指定发布目录与版本；例如已在 dist/MyProxy-linux-x64 发布 Linux 包时运行：

~~~sh
python scripts/release_verify.py sbom --root dist/MyProxy-linux-x64 --version 0.1.0 --output dist/MyProxy-linux-x64.sbom.json
~~~

将路径与版本替换为实际产物，保留 SBOM 与发布摘要。签名安装包的信任密钥、更新元数据及分发地址由部署者自行配置。自动更新检查默认关闭，正式上线前核验实际信任链。

## 版本与支持

发布记录至少说明新增行为、修复、兼容性变化、验证结果和未覆盖的平台范围。破坏 API、deployment.json 或本地状态格式的改动应提供迁移路径。已经分发的二进制若需要撤回，保留修复公告与对应源码获取途径。

目前 0.1.x 是公开测试版。源码公开和测试通过证明已有工程基础；持续的漏洞处理、依赖更新、平台验收和可复现发布需要在后续版本中保持。
