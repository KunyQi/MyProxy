# 图形化发布与指派

私有管理后台的「发布指派」页提供发布向导：选择安装包，在当前浏览器生成或导入发布密钥，签署更新清单，上传安装包，发布并保存指派。签名在浏览器完成；服务端只接收清单、签名和安装包，不接收私钥。

## 首次配置

1. 部署此版本的服务端与 nginx 模板。后台仍通过原有 SSH 管理入口访问；公网只开放已发布安装包的下载地址。`deployment.json` 中的 HTTPS API 源站必须已配置。
2. 使用支持 Ed25519 Web Crypto 的现代浏览器，从 `localhost` 或 HTTPS 打开后台。在向导中生成密钥，下载私钥 PEM 备份，填写稳定的 `keyId`；以后发布使用同一密钥。也可导入已有的 Ed25519 PKCS#8 PEM/DER 私钥。私钥不保存在浏览器存储，刷新或退出后需重新选择。
3. 将页面给出的 `keyId:公钥hex` 写入 `myproxy-api.service` 的 systemd drop-in：

   ```ini
   [Service]
   Environment="MYPROXY_RELEASE_SIGNING_KEYS=你的keyId:页面显示的64位公钥hex"
   ```

   执行 `systemctl daemon-reload` 和 `systemctl restart myproxy-api` 后，刷新后台。不要把该配置写入 `admin.env`，部署器只允许该文件包含管理员与设备认证密钥。
4. 将同一发布公钥编译进客户端的 `ReleaseSigningKeys`。Linux 还需配置 `LinuxArtifactSigningKeys`，格式为 `SHA256(原始32字节公钥):公钥hex`。当前仓库的信任表默认为空；已经分发的空表版本，需要先手动安装一次配置好公钥的客户端。

配置好的公钥只决定哪些签名可被接受。生成一个密钥不会自动把它加入服务端或客户端的信任表。

浏览器签名需要可信的后台页面来源。私钥不会通过当前页面上传，但后台若被篡改，恶意脚本仍可能读取浏览器内存；需要隔离发布密钥时，应使用离线签名工具，再通过高级入口登记清单。

## 发布一个版本

1. 提高源码版本号并构建安装包。Android 还需递增 `versionCode`，覆盖安装沿用原包名和签名证书。
2. 在向导中选择平台、版本和通道，再选择安装包：

   | 平台 | 安装包要求 |
   | --- | --- |
   | Windows | ZIP 根目录包含已经过 Authenticode 签名的 `MyProxy.exe`；便携构建的更新包只包含该 EXE |
   | Android | 已签名的 APK；填写实际 APK 签名证书的 SHA-256 |
   | Linux | 客户端 `.tar.gz`；向导用当前发布密钥生成安装包的分离 Ed25519 签名 |

   Windows 同样填写实际 Authenticode 签名证书的 SHA-256。向导负责更新清单签名，不替代 Windows/Android 的平台签名工具；大小、摘要和声明的证书指纹不能证明一个包确实已经过平台签名，客户端安装时仍会验证。
3. 导入或生成发布密钥，选择目标范围。平台默认面向该平台所有设备；按用户需要选择用户；按设备需要选择设备。向导计算安装包大小和 SHA-256，生成签名清单，展示可审查的版本、文件和目标摘要。
4. 检查摘要后点击上传发布按钮。操作依次登记草稿、上传安装包（Linux 另上传分离签名）、发布 release、保存指派。结果会显示具体完成阶段；失败后可在同一页面继续重试。已经发布的版本不允许换包。
5. 在 release 与指派列表核对结果。设备版本指派的优先级为设备、用户、平台。发现问题可撤销 release，停止继续下发该版本；撤销本身不会把已经安装的客户端自动降级。

安装包默认保存在数据库同级的 `releases` 目录，下载地址形如 `/client/releases/<安装包SHA256>/myproxy-<平台>-<版本>.<扩展名>`。草稿和已撤销 release 不提供公开下载。上传会校验清单签名、大小及实际 SHA-256；Linux 分离签名也必须通过验证，才允许发布。

下载源站默认沿用 `deployment.json` 的 API 源站。需要保留既有 API 地址及证书、让安装包使用独立可信 CA 下载站时，可在 `myproxy-api.service` 的 systemd drop-in 中设置：

```ini
[Service]
Environment="MYPROXY_RELEASE_ARTIFACT_ORIGIN=https://downloads.example.com:8443"
```

该配置改变向导使用的托管安装包下载地址，不改变 API 地址、端口、探测 URL 或 API 证书。值必须为已配置的 HTTPS 纯源站，可带端口；不能包含路径、查询参数、片段、用户信息，也不能使用 `.invalid` 占位域名。空值保持默认行为。独立下载站须使用匹配主机名的可信 CA 证书，并将 `/client/releases/` 的严格下载路由转发到同一服务；不能暴露后台及管理 API。保存后执行 `systemctl daemon-reload` 和 `systemctl restart myproxy-api`，在后台刷新并核对返回的 `artifactBaseUrl`。不要将此环境变量写入 `admin.env`。托管地址识别也随源站切换，旧源站的清单不再属于当前服务的托管地址；若已有发布包，切换前须另行保留旧下载服务。配置切换不会重写既有清单的签名或 URL。

单个安装包默认上限为 512 MiB，可在 systemd drop-in 中设置 `MYPROXY_RELEASE_ARTIFACT_MAX_BYTES`（字节，`0` 禁用上传）。`MYPROXY_RELEASE_ARTIFACT_DIR` 可修改存储目录；使用自定义路径时还需赋予 `myproxy` 用户写入权限，并将它加入服务的 `ReadWritePaths`。

向导也可导出已签署的清单，供手工流程使用；原来的 Base64 登记表单保留为高级入口。导出与生成清单不会自动发布。

## 客户端当前范围

这个向导完成发布者的签名、上传、发布与指派操作。Windows/Android 当前界面只检查平台公开版本，尚未接通按用户/设备指派的查询与内置安装；Linux 可用 `myproxy update check` 与 `myproxy update apply`，GUI 的自动检查开关尚未写入守护进程偏好。客户端后续接线与真实平台安装验收仍需单独完成。
