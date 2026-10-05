# 网络选路与优化

Windows 与 Linux 共用 C# 配置生成器；Android 使用相同的路由和 DNS 策略。三端固定核心为 Xray 26.9.9，支持本页所用的反选 IP 规则与 DNS 并行查询。

## 智能分流的优先级

统计 API 与受限探测入站的专用规则先执行；探测仅放行部署者配置的目标，其余目标被阻断。用户流量按以下顺序判断：

1. 已可见的私网 IP、BT 协议：阻断。
2. `geosite:cn` 与微软中国服务域名：直连。
3. 开启类别归因时，视频、社交、通信域名使用对应的代理出站 tag。
4. 常用国际服务的域名和已打包的 `geosite:geolocation-!cn`：代理。
5. 未收录域名才触发 `IPIfNonMatch` 的第二轮匹配。解析集合包含私网地址时阻断；否则任一国外公网地址先匹配代理，最后才匹配国内 IP 直连。

因此，未知域名的 `fd00::1 + 国内 IPv4` 不再因国内 IP 抢先匹配而直连；`国内 IPv4 + 国外 IPv6` 也改走代理。纯国内 IPv4/IPv6 仍可直连。

这是域名优先的策略：已命中域名时不额外解析，因此不能把上述私网检查解释成对所有已知域名的 DNS 回答进行检查。全局模式继续使用 `AsIs`，域名不为了 IP 分流额外解析，已提供的私网 IP 与 BT 仍优先阻断。

## 常用服务与 DNS

域名规则覆盖根域和子域，GitHub 下载/CDN、YouTube 视频与静态资源、Google API/资源、微软国际登录/365/Office/OneDrive/SharePoint/Teams/Azure/Visual Studio、X 的图片与短链接、ChatGPT 的登录/静态资源/文件和相关认证端点均有明确匹配。现有地理域名数据库补充其它国际域名。没有固定这些服务的 CDN IP。

国内域名优先保留：Azure 中国、微软中国登录、Office 365 中国及 `cn.bing.com` 直连；数据库已归入国内的 CDN 也保留直连，例如本轮实测的 `fonts.gstatic.com` 与 `officecdn.microsoft.com`。YouTube 与 X 的专用匹配仍使用原类别 tag，不会把其统计改记到通用代理出站。

直接命中域名可以省掉路由层的本地 IP 兜底解析，实际域名连接由原有出站处理。需要本地 DNS 时：

- 国内组：阿里与腾讯，使用相同的国内域名匹配及国内 IP 过滤，`skipFallback=true`，不参与其它域名的兜底。
- 国际组：Cloudflare 与 Google DoH，使用相同的国际域名规则，承担默认兜底；删除重复的 Cloudflare 条目。
- `enableParallelQuery=true`，组内取先成功的有效回答，组间按顺序回退。保留缓存和 `UseIP` 双栈查询。

并行查询会增加冷查询时对候选解析器的请求数；缓存可避免重复查询。本轮用实际核心、生产 DNS 分组及本地模拟的慢 600 ms/快 40 ms 解析器测量，八轮串行冷查询中位数 623.6 ms，八轮并行 65.1 ms。这个实验验证等待时间的减少，不能换算成公网下载带宽提升。

## 出口判断与 Linux 内网

设备配置目前只有代理连接入口，没有独立确认的实际出口地址。Windows/Linux 自检不再把入口用于出口比较；无出口证据时显示已连通、出口未知。即使回显与一个已知出口不同，也可能是双栈、NAT 或其它合法出口，因此不据此判定绕过。只有匹配可信出口地址时才确认该出口。

Linux 的 GNOME、KDE 和 `no_proxy/NO_PROXY` 绕过相同的回环、IPv4 RFC1918/链路本地、IPv6 ULA 与链路本地范围；停止代理时恢复用户原设置。桌面名单使用 `fc00::/7`、`fe80::/10`，环境变量将它们分别展开为 `fc00::/8`、`fd00::/8` 和 `fe80::/16` 至 `febf::/16`，范围不变。

这是为兼容部分 curl 版本：[curl 8.13](https://github.com/curl/curl/blob/curl-8_13_0/lib/noproxy.c#L72-L97) 与 [8.14.1](https://github.com/curl/curl/blob/curl-8_14_1/lib/noproxy.c#L72-L97) 的源码中，IPv6 非整字节前缀的比较条件存在反向判断；客户端识别出 IPv6 后，展开为整字节前缀可避开该分支。环境变量的读取和 CIDR 匹配仍取决于应用支持，不能保证所有终端工具均支持 IPv6 内网绕过。未使用 `::/0` 或 `*` 扩大绕过范围。

本机 Windows curl 8.13 的实际本地代理测试仍未识别 ULA/链路本地 CIDR，只识别 IPv4 CIDR 和精确的 IPv6 回环地址。不使用地址映射的本地 IPv6 对照测试也复现了 `::1` 绕过、`::1/128` 不绕过的行为。该版本在 [URL 处理](https://github.com/curl/curl/blob/curl-8_13_0/lib/url.c#L1672-L1688) 中去掉 IPv6 方括号，随后其 [no_proxy 分类逻辑](https://github.com/curl/curl/blob/curl-8_13_0/lib/noproxy.c#L137-L161) 将裸 IPv6 当作主机名；整字节前缀不能修复这一客户端问题。这个结果不代表 Linux 桌面实测，也不能宣称所有 curl 均兼容。

## 验证范围

本轮验证结果：

| 验证 | 结果与范围 |
| --- | --- |
| Windows 单元测试 | 437 项通过；沙箱无法运行注册表、证书存储等系统权限相关的三个测试类，未计入通过数 |
| Linux 单元测试 | 119 项通过、7 项按平台跳过；在 Windows 上执行，未操作真实 Linux 桌面 |
| Android | 271 项单元测试通过，Debug APK 构建与 Lint 通过；未做真机测试 |
| 实际 Xray 核心 | 133 项路由/协议/探测/统计用例通过，采用本地 DNS 与测试出站 |
| 三端配置 | Android 与共享 C# 生成器的四组 DNS、用户路由及出站配置一致 |
| IPv6 绕过范围 | 穷举全部 65,536 个 IPv6 首段，等价展开无遗漏、未扩大范围；另测回环、内网及公网边界 |
| 实际 Windows curl 8.13 | 15 项行为符合预期；4 项 IPv6 内网 CIDR 未识别，作为客户端兼容限制保留 |

以上测试不证明公网吞吐提升。Linux 桌面接管、Android 真机和实际节点下载吞吐仍需目标环境验收。

策略依据：[Xray 路由](https://xtls.github.io/config/routing.html)、[Xray DNS](https://xtls.github.io/config/dns.html)、[ChatGPT 网络端点](https://help.openai.com/en/articles/9247338-network-recommendations-for-chatgpt-errors-on-web-and-apps)、[Microsoft 365 端点](https://learn.microsoft.com/en-us/microsoft-365/enterprise/urls-and-ip-address-ranges)、[GitHub Copilot 端点](https://docs.github.com/en/copilot/reference/copilot-allowlist-reference)。
