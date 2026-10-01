# MyProxy Server

MyProxy 的 Python 控制面，为 Windows、Android 和 Linux 客户端提供配对、配置、心跳和用量接口。数据流量由客户端直连 Xray，服务端 API 不转发代理流量。运行时只使用 Python 标准库；Linux 部署另需 3x-ui/Xray、nginx、OpenSSL 与 systemd。

## 本地运行

```sh
cd server
python run_local.py
```

本地入口使用内存中的 x-ui 模拟器和 `server/data/` 下的 SQLite；监听 `http://127.0.0.1:8090`。未提供 `MYPROXY_ADMIN_TOKEN` 时仅本地使用 `admin/admin`。本地示例配置不连接真实服务器。

```sh
python -m unittest discover -s tests -v
```

## 配置与部署

根目录 `deployment.json` 中的 `api_base_url` 是客户端与服务端共享的 HTTPS 源站。示例 `https://api.example.invalid` 必须在生产部署及客户端分发前替换。可通过 `MYPROXY_DEPLOYMENT_CONFIG` 指向另一份文件；服务端默认从该 URL 取数据面主机，`MYPROXY_SERVER_HOST` 可显式覆盖。数据面端口来自 3x-ui inbound 或 `MYPROXY_SERVER_PORT`，与 HTTPS API 端口独立。

生产 API 监听 `127.0.0.1:1820`，nginx 只公开 Device API 白名单。Admin API、管理网页和 readiness 保持私有；生产进程和 root helper 使用同一 `/opt/myproxy-api/deployment.json`。

公开部署方法见 [部署说明](docs/DEPLOYMENT.md)，请求和响应见 [OpenAPI](docs/openapi.yaml)。自动更新默认不登记 release；启用时需单独配置 release 验签公钥与签名发布流程。
