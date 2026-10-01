# 参与 MyProxy

MyProxy 面向自行部署服务器的开发者。客户端把日常操作收敛到配对和连接开关；服务器、证书、路由和发布配置由部署者负责。改动以轻量、稳定和可复现为先。

## 开始

先读 [README](README.md)、[框架说明](docs/architecture.md)、[部署说明](docs/deployment.md) 和 [API 契约](server/docs/openapi.yaml)。主开发分支为 `main`。从自己的 fork 建分支，提交范围清楚的改动。

源码检查和编译可以使用仓库自带的 `.invalid` 示例入口；真实连接前应替换 deployment.json，部署自己的 HTTPS API 与 3x-ui。开发或测试凭据保留在本地，日志提交前先脱敏。

运行与改动相关的检查：

~~~sh
python scripts/release_verify.py check
python -m unittest discover -s scripts -p 'test_*.py' -v
python -m unittest discover -s server/tests -v
dotnet test windows/MyProxy/Tests/MyProxy.Tests.csproj -c Debug
dotnet test linux/MyProxyLinux/tests/MyProxy.Linux.Tests.csproj -c Release
~~~

Windows 测试在 Windows 上运行，Linux 的系统集成测试在 Linux 上运行。涉及真实系统代理或自启动的测试可能修改本机设置，使用隔离的测试环境。Windows/Linux SDK 版本由 global.json 固定；Android 的 JDK、SDK 与 Gradle 版本见 README 和项目配置。

在 `android/MyProxyAndroid/` 运行：

~~~sh
./gradlew --no-daemon lintRelease testDebugUnitTest assembleRelease lintStaging testStagingUnitTest assembleStaging
~~~

Windows 使用 gradlew.bat。修改 Android 原生库时另读 [重建与替换指南](docs/android-core-rebuild.md)。单纯文档改动检查内容与链接即可；行为改动提供能证明问题与修复的回归结果。

## 提交与审查

PR 说明触发条件、改动后的行为和验证结果。接口、配置或兼容性发生变化时，同步修改公开文档与契约。避免夹带格式化、升级依赖和无关重构。

连接、断开、取消、恢复和进程退出都要有明确结果。保留标准 TLS 验证、API 禁止重定向、代理恢复备份及更新签名校验。故障文案告诉用户下一步，网络配置细节放在部署者文档和必要的设置解释里。

新增依赖前说明用途和体积、维护成本，更新版本与许可材料。修改随附原生资产时同步更新摘要、对应源码和重建路径。不要提交安装包、私钥、证书、数据库、个人目录、线上凭据或构建缓存。

可以使用 AI 协助开发。提交者仍需理解改动、检查来源与许可，并给出验证结果。审查围绕代码、事实和复现；尊重不同经验的贡献者。

## 反馈

普通故障和功能建议使用仓库 Issue 模板。安全漏洞按 [SECURITY.md](SECURITY.md) 私下报告。支持边界、发布流程和仓库设置见 [维护说明](docs/maintaining.md)。

提交贡献即表示你有权提供该内容，并同意项目原有部分按 [MIT](LICENSE) 分发；第三方代码须保留原许可。本项目不要求转让版权，也不要求使用特定开发工具。
