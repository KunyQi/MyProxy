# Network routing and optimization

Windows and Linux share a C# configuration generator; Android uses the same routing and DNS policy. All three platforms pin the core to Xray 26.9.9, which supports the inverted IP rules and parallel DNS queries used on this page.

## Smart routing priorities

The dedicated rules for the stats API and the restricted probe inbound run first; the probe only lets through the targets configured by the deployer, and every other target is blocked. User traffic is evaluated in this order:

1. Already visible private IPs and the BitTorrent protocol: blocked.
2. `geosite:cn` and Microsoft China service domains: direct.
3. With category attribution enabled, video, social and messaging domains use the corresponding proxy outbound tag.
4. Domains of common international services and the bundled `geosite:geolocation-!cn`: proxy.
5. Only domains not covered above trigger the second round of `IPIfNonMatch` matching. If the resolved set contains a private address, it is blocked; otherwise any foreign public address matches the proxy first, and the domestic-IP direct rule is matched last.

As a result, an unknown domain resolving to `fd00::1 + a domestic IPv4` no longer connects directly just because the domestic IP matched first; `a domestic IPv4 + a foreign IPv6` now goes through the proxy too. Purely domestic IPv4/IPv6 can still connect directly.

This is a domain-first policy: a domain that has matched is not resolved again, so the private-address check above must not be read as a check on the DNS answers of every known domain. Global mode keeps using `AsIs`; domains are not resolved for the sake of IP routing, and private IPs and BitTorrent that are already visible are still blocked first.

## Common services and DNS

Domain rules cover root domains and their subdomains. GitHub downloads/CDN, YouTube video and static assets, Google APIs/assets, Microsoft international sign-in/365/Office/OneDrive/SharePoint/Teams/Azure/Visual Studio, X images and short links, and ChatGPT sign-in/static assets/files and the related authentication endpoints all have explicit matches. The existing geographic domain database covers other international domains. No CDN IPs of these services are pinned.

Domestic domains keep priority: Azure China, Microsoft China sign-in, Office 365 China and `cn.bing.com` connect directly; CDNs that the database already classifies as domestic also stay direct, such as `fonts.gstatic.com` and `officecdn.microsoft.com` measured in this round. The dedicated matches for YouTube and X still use their original category tags, so their statistics are not moved to the generic proxy outbound.

A direct domain match saves the routing layer's local IP fallback lookup, and the actual domain connection is handled by the original outbound. When local DNS is needed:

- Domestic group: Alibaba and Tencent, using the same domestic domain matching and domestic IP filtering, with `skipFallback=true`, so they do not take part in the fallback for other domains.
- International group: Cloudflare and Google DoH, using the same international domain rules, and acting as the default fallback; the duplicate Cloudflare entry is removed.
- `enableParallelQuery=true`: within a group, the first successful valid answer is used; across groups, fallback happens in order. The cache and `UseIP` dual-stack queries are kept.

Parallel queries increase the number of requests to candidate resolvers on cold lookups; the cache avoids repeated queries. This round was measured with the actual core, the production DNS groups and locally simulated slow (600 ms) and fast (40 ms) resolvers: the median of eight serial cold lookups was 623.6 ms, and of eight parallel ones 65.1 ms. This experiment verifies a reduction in waiting time; it cannot be translated into higher download bandwidth on the internet.

## Exit detection and Linux internal networks

The device configuration currently has only the proxy connection entry, with no independently confirmed actual exit address. Windows/Linux self-checks no longer use the entry for exit comparison; without evidence of the exit, they show "connected, exit unknown". Even if the echoed address differs from a known exit, it may be due to dual stack, NAT or another legitimate exit, so it is not treated as a bypass. An exit is confirmed only when it matches a trusted exit address.

On Linux, GNOME, KDE and `no_proxy/NO_PROXY` bypass the same loopback, IPv4 RFC1918/link-local, IPv6 ULA and link-local ranges, and the user's original settings are restored when the proxy stops. The desktop lists use `fc00::/7` and `fe80::/10`; the environment variables expand them into `fc00::/8`, `fd00::/8` and `fe80::/16` through `febf::/16` respectively, covering the same ranges.

This is for compatibility with some curl versions: in the source of [curl 8.13](https://github.com/curl/curl/blob/curl-8_13_0/lib/noproxy.c#L72-L97) and [8.14.1](https://github.com/curl/curl/blob/curl-8_14_1/lib/noproxy.c#L72-L97), the comparison for IPv6 prefixes that are not whole bytes is inverted; once the client recognizes IPv6, expanding to whole-byte prefixes avoids that branch. Reading the environment variables and CIDR matching still depend on application support, and there is no guarantee that every terminal tool supports bypassing IPv6 internal networks. `::/0` or `*` are not used to widen the bypass.

An actual local proxy test with curl 8.13 on Windows still did not recognize ULA/link-local CIDRs, only IPv4 CIDRs and the exact IPv6 loopback address. A local IPv6 comparison test without address mapping also reproduced `::1` being bypassed while `::1/128` is not. That version strips the IPv6 brackets in its [URL handling](https://github.com/curl/curl/blob/curl-8_13_0/lib/url.c#L1672-L1688), after which its [no_proxy classification logic](https://github.com/curl/curl/blob/curl-8_13_0/lib/noproxy.c#L137-L161) treats the bare IPv6 address as a host name; whole-byte prefixes cannot fix this client-side problem. This result does not represent a test on a Linux desktop, and does not claim that every curl is compatible.

## Verification scope

The verification of this round of routing changes is below. The unit test counts for the three clients were re-checked against the GitHub Actions CI results of 2026-11-03; the other rows are one-off measurements from that time and are not part of the repository's tests.

| Verification | Result and scope |
| --- | --- |
| Windows unit tests | 472 passed, 0 skipped; run on the CI Windows runner, including the test classes that need system privileges such as the registry and certificate store |
| Linux unit tests | 126 passed, 0 skipped; run on the CI Ubuntu runner, without operating a real Linux desktop |
| Android | 271 unit tests passed (Debug and Staging variants), Release/Staging Lint and APK builds passed; this round's routing changes were not re-tested on a real device |
| Actual Xray core | 133 routing/protocol/probe/statistics cases passed, using local DNS and test outbounds |
| Configuration across platforms | Android and the shared C# generator produce the same four DNS groups, user routing and outbound configuration |
| IPv6 bypass ranges | All 65,536 first IPv6 hextets enumerated: the equivalent expansion misses nothing and widens nothing; loopback, internal and public boundaries also tested |
| Actual Windows curl 8.13 | 15 behaviours as expected; 4 IPv6 internal CIDRs not recognized, kept as a client compatibility limit |

These tests do not prove higher throughput on the internet. Linux desktop takeover, Android on real devices and download throughput through a real node still need acceptance in the target environments.

Policy references: [Xray routing](https://xtls.github.io/config/routing.html), [Xray DNS](https://xtls.github.io/config/dns.html), [ChatGPT network endpoints](https://help.openai.com/en/articles/9247338-network-recommendations-for-chatgpt-errors-on-web-and-apps), [Microsoft 365 endpoints](https://learn.microsoft.com/en-us/microsoft-365/enterprise/urls-and-ip-address-ranges), [GitHub Copilot endpoints](https://docs.github.com/en/copilot/reference/copilot-allowlist-reference).
