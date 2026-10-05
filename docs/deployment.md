# 配置、构建与部署

入口只有一份，构建与部署共用。把环境参数留在配置里，把使用步骤留给客户端。

## 1. 准备依赖

安装 global.json 指定的 .NET SDK 9.0.318、.NET 8 运行时和 Python 3.12+。客户端仍以 .NET 8 为运行目标；新版 SDK 用于 Avalonia 12 的 XAML 生成器。Android 构建需要 JDK 17、Android SDK 平台 35 与 Build Tools 35.0.0，设置本机的 JAVA_HOME 和 ANDROID_HOME。SDK、缓存与密钥不纳入源码。

## 2. 设置统一入口

修改根目录 deployment.json 的 api_base_url。值应为 HTTPS 源站地址，例如 https://api.example.com:8443；省略端口使用 443。不在 URL 中填写账号、密码、接口路径、查询或片段。

客户端构建时读取该文件，修改后重新构建。Debug 保留本地开发入口。占位 .invalid 地址不会作为可用的生产部署配置。

默认连通性检测使用同一服务器的 `GET /connectivity-check`，经隧道验证空 HTTP 204。如果需要备用检测点，可在同一文件增加 `connectivity_check_urls`：

~~~json
{
  "api_base_url": "https://api.example.com:8443",
  "connectivity_check_urls": [
    "https://api.example.com:8443/connectivity-check",
    "https://check.example.com/204"
  ]
}
~~~

数组接受一至四个 HTTPS URL，可以包含路径，禁止凭据、查询和片段。每个端点必须使用有效证书并返回空 204；不跟随重定向。三端使用同一候选列表和探测主机白名单，失败后尝试下一项，全部失败则拒绝确认连接。未配置数组时无需依赖第三方探测服务。Debug 的控制面可使用本地 API，隧道检测仍使用这里配置的 HTTPS 候选；模拟器地址不能作为远端隧道的检测目标。部署脚本、打包和回滚保留整个配置文件。

Server 数据面主机默认从该 URL 提取，MYPROXY_SERVER_HOST 可以另行指定。控制面端口与 Xray inbound 端口互相独立；同一监听地址不能让两项服务占用同一端口。API 可以使用 8443，Xray 使用自己的 inbound 端口。

## 3. 部署 Server

准备 Linux 主机、systemd、nginx、公开 CA 签发的有效 HTTPS 证书及完整证书链，以及配置好的 3x-ui / Xray inbound。证书的域名或 IP 应与 API 入口匹配；为证书安排续期和 nginx 重载。

将统一配置与服务端运行文件一起部署。生产运行通过 MYPROXY_DEPLOYMENT_CONFIG 指定部署配置文件。API 与 helper 使用同一配置；管理员 token、设备 token secret、SSH 私钥和数据面私钥通过受保护的本机配置提供。

通用部署脚本要求显式指定 SSH 主机、身份与证书文件，并在远程变更前进行预检。管理入口保持私有。配置项、部署命令与管理员访问步骤见 [Server README](../server/README.md)。

安装后验证 API 健康状态和空 204 检测端点，再创建用户与配对码。以自己的证书和数据面完成绑定、连接、停止与撤销检查。

## 4. 构建客户端

Windows：

~~~text
dotnet build windows/MyProxy/MyProxy.csproj -c Release
dotnet test windows/MyProxy/Tests/MyProxy.Tests.csproj -c Debug
~~~

Windows 网络集成测试使用 Debug 的本地 HTTP 模拟服务，需要 `python` 命令可启动。Release 构建保留生产 HTTPS 校验。系统代理与自启注册表的集成用例在专用测试环境运行。

Android，在 android/MyProxyAndroid/ 运行：

~~~sh
./gradlew --no-daemon lintRelease testDebugUnitTest assembleRelease lintStaging testStagingUnitTest assembleStaging
~~~

Windows 可使用 gradlew.bat。Release APK 默认未签名，使用自己的长期密钥签名后安装；覆盖安装须保持签名身份一致。

Linux：

~~~text
dotnet build linux/MyProxyLinux/cli/MyProxy.Linux.Cli.csproj -c Release
dotnet build linux/MyProxyLinux/gui/MyProxy.Linux.Gui.csproj -c Release
dotnet test linux/MyProxyLinux/tests/MyProxy.Linux.Tests.csproj -c Release
~~~

运行内核需要匹配版本的 Linux Xray，获取和打包步骤见 [Linux README](../linux/MyProxyLinux/README.md)。

## 5. 校验与分发

~~~text
python scripts/release_verify.py check
python -m unittest scripts.test_release_verify -v
python scripts/test_package_delivery.py
python -m unittest scripts.test_package_linux -v
python -m unittest discover -s server/tests -v
~~~

生产打包要求有效且已配置的服务器入口。安装包签名与更新信任密钥由部署者管理；新安装默认关闭自动更新检查，已有显式偏好保留。发布的更新必须通过签名与摘要验证，安装由用户确认。保留第三方许可证，并确定项目级许可后再安排公开分发。
