# 服务端部署

## 准备

使用 Linux 主机，安装 Python 3、systemd、nginx、OpenSSL、curl、3x-ui 与 Xray。SSH 使用显式私钥和已核验的 `known_hosts`；脚本不会下载或安装第三方服务。

1. 修改仓库根 `deployment.json`，例如 `{"api_base_url":"https://api.example.com:8443"}`；生产地址只接受 HTTPS 源站，可省略或保留单个根斜杠，不接受其他路径、查询、片段、凭据或空白。未填写端口使用 443。
2. 在 3x-ui 建立自己的 VLESS/REALITY inbound、生成密钥和 short ID、设置有效的 SNI。现成 nginx 模板监听全部接口，同一主机上的 API 与 Xray 必须使用不同端口；例如 API 使用 443，Xray inbound 使用 8443。若分别部署在不同主机，可各自使用 443；同机通过不同地址复用端口需自行修改 nginx 监听模板并设置 Xray 绑定地址。`MYPROXY_SERVER_PORT` 只覆盖数据面端口。
3. 为 API 域名准备公开 CA 签发的完整证书链和私钥。nginx 默认使用 `/etc/letsencrypt/live/<API主机>/fullchain.pem` 和 `privkey.pem`；可显式传入其他绝对路径。续期管理的符号链接允许保留，不复制或改变公网私钥权限。
4. 为私有后端另备 `/etc/myproxy/tls/server.crt`、`server.key`，可使用自签证书。后端只监听本机回环，文件及父目录不能是符号链接。不要复用公网证书路径。

示例域名用于说明，不提供共享服务器、账号、密钥或安装包。nginx 部署器支持 DNS 名称与 IPv4 地址；IPv6 可用于客户端源站配置，网关部署需要自行适配监听与 server_name。

## 安装

```sh
MYPROXY_DEPLOY_CONFIRM=YES \
MYPROXY_VPS_HOST=server.example.com \
MYPROXY_SSH_KEY=/path/to/private-key \
MYPROXY_DEVICE_API_TLS_CERT=/etc/letsencrypt/live/api.example.com/fullchain.pem \
MYPROXY_DEVICE_API_TLS_KEY=/etc/letsencrypt/live/api.example.com/privkey.pem \
bash server/deploy/deploy.sh
```

公网名称和端口从共享配置派生，显式 `MYPROXY_DEVICE_API_SERVER_NAME` 或 `MYPROXY_DEVICE_API_PUBLIC_PORT` 必须与配置一致。TLS 模式为 `public-ca`；不再使用固定证书指纹。部署前验证配置，上传采用运行文件白名单，配置进入 `/opt/myproxy-api/deployment.json`。两份 systemd unit 均显式读取该文件。

首次安装生成独立 Admin Token 与设备签名密钥，保存在 `/etc/myproxy-api/admin.env`，仅 root 与 API 服务组可读；既有密钥不会自动轮换。x-ui helper 使用 root-only `/etc/myproxy-api/xui-helper.env`。可修改 inbound ID 和必要的显式数据面覆盖；未设置或为空的主机覆盖使用共享配置主机。

脚本快照服务目录（包括 `deployment.json`）、数据库、unit、凭据、nginx 与防火墙状态；失败恢复快照。API 使用独立无附加组的系统账户，root helper 通过 Unix socket 操作 x-ui。公网证书文件不会被部署脚本改写，续期后应执行 `nginx -t && systemctl reload nginx`。

## 验证与管理

```sh
curl --fail https://api.example.com:8443/healthz
curl --fail -o /dev/null -w '%{http_code}\n' https://api.example.com:8443/connectivity-check
curl -o /dev/null -w '%{http_code}\n' https://api.example.com:8443/api/admin/user
```

health 应为 200，connectivity-check 应为 204 且无响应体；公网 Admin API、`/admin`、`/readyz` 和未知路由应为 404。公网验收使用系统 CA 信任库，检查完整证书链、有效期及主机名；证书不被信任、过期或名称不匹配会使部署失败。只有本机私有自签后端验收使用 `curl -k`。

默认客户端通过隧道访问自有服务器的检测端点。可在共享配置中添加一至四个 `connectivity_check_urls` 作为候选，要求完整 HTTPS URL、有效证书和空 204；候选允许路径，禁止凭据、查询和片段。修改后同步部署配置与重建客户端。详细示例见 [根部署说明](../../docs/deployment.md)。

管理经 SSH 隧道进入 `127.0.0.1:1820`，需要自行安全提供 Admin Bearer。不要向公网代理 Admin 路由。`predeploy_private.py` 可先执行 `--audit-only`；带 `--deploy --confirm PRIVATE-PREDEPLOY` 时仅安装私有后端并将同一配置纳入事务，不修改 nginx 或防火墙。

根配置、客户端发布构建与服务端配置应同步更新。开放的 API 端口与 Xray 端口需在主机和云防火墙放行，私有 1820 保持禁止公网连接。数据库含设备记录与地址信息，备份按私有数据处理。
