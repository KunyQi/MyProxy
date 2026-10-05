using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyProxy.Models;

namespace MyProxy.Core;

public static class XrayConfigGenerator
{
    private const string InboundTag = "http-in";
    private const string InboundListen = "127.0.0.1";
    private const string InboundProtocol = "http";
    private const string ProxyOutboundTag = "proxy";
    private const string DirectOutboundTag = "direct";
    private const string BlockedOutboundTag = "blocked";
    private const string VlessProtocol = "vless";
    private const string FreedomProtocol = "freedom";
    private const string BlackholeProtocol = "blackhole";
    private const string RealitySecurity = "reality";
    private const string RealityNetwork = "tcp";
    private const string VlessFlow = "xtls-rprx-vision";
    private const string VlessEncryption = "none";
    private const string StatsApiTag = "api";

    /// <summary>
    /// 连通性探测专用入站的 tag。探测只经这个入站发出，首条路由规则把它强制送进代理。
    /// </summary>
    public const string ProbeInboundTag = "probe-in";

    /// <summary>
    /// 探测入站放行部署配置中的检测主机，以及可选出口回显主机。
    /// </summary>
    public static IReadOnlyList<string> ProbeHosts => DeploymentConfiguration.ConnectivityCheckUrls
        .Select(url => new Uri(url).Host.Trim('[', ']')).Append("www.cloudflare.com")
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private const string StatsApiProtocol = "dokodemo-door";
    private const string StatsService = "StatsService";
    private const string DirectDomainStrategy = "AsIs";
    private const string RuleRoutingDomainStrategy = "IPIfNonMatch";
    private const string GlobalRoutingDomainStrategy = "AsIs";
    private const string QueryStrategyUseIp = "UseIP";

    // Exact suffix rules avoid an extra local DNS round for common global
    // services. Domestic entries keep precedence and category tags are reused.
    private static readonly string[] DomesticMicrosoftDomains =
    {
        "domain:azure.cn",
        "domain:chinacloudapi.cn",
        "domain:microsoftonline.cn",
        "domain:microsoft.com.cn",
        "domain:office365.cn",
        "full:cn.bing.com",
    };

    private static readonly string[] OptimizedProxyDomains =
    {
        "domain:github.com",
        "domain:githubusercontent.com",
        "domain:githubassets.com",
        "domain:github.io",
        "domain:ghcr.io",
        "domain:githubcopilot.com",
        "domain:githubcopilot.net",
        "domain:google.com",
        "domain:googleapis.com",
        "domain:googleusercontent.com",
        "domain:gstatic.com",
        "domain:ggpht.com",
        "domain:googlevideo.com",
        "domain:googleadservices.com",
        "domain:googlesyndication.com",
        "domain:google-analytics.com",
        "domain:googleblog.com",
        "domain:blogspot.com",
        "domain:withgoogle.com",
        "domain:microsoft.com",
        "domain:microsoftonline.com",
        "domain:microsoft365.com",
        "domain:cloud.microsoft",
        "domain:office.com",
        "domain:office.net",
        "domain:office365.com",
        "domain:outlook.com",
        "domain:outlook.office365.com",
        "domain:live.com",
        "domain:onedrive.com",
        "domain:sharepoint.com",
        "domain:sharepointonline.com",
        "domain:teams.microsoft.com",
        "domain:msauth.net",
        "domain:msftauth.net",
        "domain:msauthimages.net",
        "domain:msftauthimages.net",
        "domain:azure.com",
        "domain:azure.net",
        "domain:windows.net",
        "domain:azureedge.net",
        "domain:azurefd.net",
        "domain:visualstudio.com",
        "domain:vsassets.io",
        "domain:bing.com",
        "domain:msn.com",
        "domain:openai.com",
        "domain:chatgpt.com",
        "domain:oaistatic.com",
        "domain:oaiusercontent.com",
        "domain:oaistatsig.com",
        "domain:openaimerge.com",
        "full:challenges.cloudflare.com",
        "full:cdn.workos.com",
        "full:setup.workos.com",
        "full:forwarder.workos.com",
        "full:images.workoscdn.com",
        "full:workos.imgix.net",
    };

    private static readonly string[] OptimizedVideoDomains =
    {
        "geosite:youtube",
        "domain:youtube.com",
        "domain:youtu.be",
        "domain:ytimg.com",
        "domain:googlevideo.com",
    };

    private static readonly string[] OptimizedSocialDomains =
    {
        "geosite:twitter",
        "domain:x.com",
        "domain:twitter.com",
        "domain:twimg.com",
        "domain:t.co",
    };

    public static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new DnsServerJsonConverter() }
        };
    }

    public static void ValidateProfile(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(profile.Server))
        {
            throw new ArgumentException("server 不能为空。", nameof(profile));
        }

        if (profile.Port is < 1 or > 65535)
        {
            throw new ArgumentException("port 必须在 1–65535 之间。", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Uuid) || !Guid.TryParse(profile.Uuid, out _))
        {
            throw new ArgumentException("uuid 必须为合法 GUID。", nameof(profile));
        }

        if (!string.Equals(profile.Security, RealitySecurity, StringComparison.Ordinal))
        {
            throw new ArgumentException("security 必须为 reality。", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.PublicKey))
        {
            throw new ArgumentException("publicKey 不能为空。", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.ShortId))
        {
            throw new ArgumentException("shortId 不能为空。", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Sni))
        {
            throw new ArgumentException("sni 不能为空。", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Fingerprint))
        {
            throw new ArgumentException("fingerprint 不能为空。", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Flow))
        {
            throw new ArgumentException("flow 不能为空。", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.SpiderX))
        {
            throw new ArgumentException("spiderX 不能为空。", nameof(profile));
        }
    }

    /// <param name="statsApiPort">
    /// 统计查询端口。为 null 时完全不生成 stats/api 段，配置与统计功能上线前逐字节一致。
    /// </param>
    /// <param name="categoryAttribution">
    /// 是否为服务类别归因生成额外的出站 tag（Observability Plane）。
    ///
    /// <b>默认 false，生成的配置与开启前逐字节一致。</b> 它由服务端的
    /// <c>usageCategories</c> feature flag 打开，因为每多一个 tag 就多一份到同一台
    /// 服务器的连接池——那是对数据面形状的改动，只应在管理员为某台设备显式
    /// 打开时才发生。几个出站的 VLESS 参数完全相同，区别只有 tag，所以流量的
    /// 去向不变，变的只是它被记到哪个计数器上。
    /// </param>
    /// <param name="probePort">
    /// 连通性探测专用入站的端口。为 null 时不生成（配置与此前逐字节一致）。
    /// 见 <see cref="ApplyProbeInbound"/>：智能分流模式下，探测若走用户的 http 入站，
    /// 会被 <c>geosite:cn → direct</c> 直接放行，测的根本不是隧道。
    /// </param>
    public static XrayRootConfig Generate(
        ServerProfile profile,
        ProxyMode mode,
        int localPort,
        string xrayErrorLogPath,
        int? statsApiPort = null,
        bool categoryAttribution = false,
        int? probePort = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);

        if (localPort is < 1024 or > 65535)
        {
            throw new ArgumentException("localPort 必须在 1024–65535 之间。", nameof(localPort));
        }

        if (string.IsNullOrWhiteSpace(xrayErrorLogPath) || !Path.IsPathRooted(xrayErrorLogPath))
        {
            throw new ArgumentException("xrayErrorLogPath 必须为绝对非空路径。", nameof(xrayErrorLogPath));
        }

        if (statsApiPort is int apiPort)
        {
            if (apiPort is < 1024 or > 65535)
            {
                throw new ArgumentException("statsApiPort 必须在 1024–65535 之间。", nameof(statsApiPort));
            }

            if (apiPort == localPort)
            {
                throw new ArgumentException("statsApiPort 不能与 localPort 相同。", nameof(statsApiPort));
            }
        }

        if (probePort is int probe)
        {
            if (probe is < 1024 or > 65535)
            {
                throw new ArgumentException("probePort 必须在 1024–65535 之间。", nameof(probePort));
            }

            if (probe == localPort || probe == statsApiPort)
            {
                throw new ArgumentException("probePort 不能与 localPort 或 statsApiPort 相同。", nameof(probePort));
            }
        }

        var config = new XrayRootConfig
        {
            Log = new LogConfig
            {
                Access = "none",
                DnsLog = false,
                Error = xrayErrorLogPath,
                LogLevel = "warning",
                MaskAddress = ""
            },
            Dns = BuildDnsConfig(),
            Routing = BuildRoutingConfig(mode, categoryAttribution),
            Inbounds = new List<InboundConfig>
            {
                new()
                {
                    Tag = InboundTag,
                    Listen = InboundListen,
                    Port = localPort,
                    Protocol = InboundProtocol,
                    Settings = new InboundSettings { Timeout = 0 },
                    Sniffing = new SniffingConfig
                    {
                        Enabled = true,
                        DestOverride = new List<string> { "http", "tls" },
                        MetadataOnly = false,
                        RouteOnly = false
                    }
                }
            },
            Outbounds = BuildOutbounds(profile, categoryAttribution),
            Policy = BuildPolicyConfig()
        };

        if (statsApiPort is int port)
        {
            ApplyStatsApi(config, port);
        }

        if (probePort is int probeInboundPort)
        {
            ApplyProbeInbound(config, probeInboundPort);
        }

        return config;
    }

    /// <summary>
    /// 检测入站仅放行配置中的目标，先于直连与私网规则强制走隧道。
    /// 用户访问相同地址时仍按所选模式路由；检测不会改变用户流量的去向。
    /// </summary>
    private static void ApplyProbeInbound(XrayRootConfig config, int probePort)
    {
        config.Inbounds.Add(new InboundConfig
        {
            Tag = ProbeInboundTag,
            Listen = InboundListen,
            Port = probePort,
            Protocol = InboundProtocol,
            Settings = new InboundSettings { Timeout = 0 },
            // 路由按入站命中，不需要嗅探。
            Sniffing = null
        });

        // 两条都插在最前面：先放行探测主机走代理，再把 probe-in 的其余目标一律拦下。
        // 不做第二条的话，这个无鉴权的回环入口就是一个绕开后面所有规则的代理——本机任何
        // 程序都能拿它走 BT（招来 VPS 商的滥用投诉），或经隧道直连 VPS 自己的回环服务。
        config.Routing.Rules.Insert(0, new RoutingRule
        {
            OutboundTag = BlockedOutboundTag,
            Type = "field",
            InboundTag = new List<string> { ProbeInboundTag }
        });
        var ipHosts = ProbeHosts.Where(host => IPAddress.TryParse(host, out _)).ToList();
        if (ipHosts.Count > 0) config.Routing.Rules.Insert(0, new RoutingRule
        {
            OutboundTag = ProxyOutboundTag,
            Type = "field",
            InboundTag = new List<string> { ProbeInboundTag },
            Ip = ipHosts
        });
        config.Routing.Rules.Insert(0, new RoutingRule
        {
            OutboundTag = ProxyOutboundTag,
            Type = "field",
            InboundTag = new List<string> { ProbeInboundTag },
            Domain = ProbeHosts.Where(host => !IPAddress.TryParse(host, out _)).Select(host => "full:" + host).ToList()
        });
    }

    /// <summary>
    /// 打开流量计数器并挂上本地 gRPC 查询入口。三处缺一不可：`stats` 开计数、
    /// `policy.system.statsOutbound*` 决定 outbound 维度是否被计、`api` + dokodemo-door
    /// inbound 提供查询通道。
    /// </summary>
    private static void ApplyStatsApi(XrayRootConfig config, int apiPort)
    {
        config.Stats = new StatsConfig();
        config.Api = new XrayApiConfig
        {
            Tag = StatsApiTag,
            Services = new List<string> { StatsService }
        };

        // 默认只统计 inbound；速率图要的是「经代理出去的量」，必须打开 outbound 维度。
        config.Policy.System.StatsOutboundUplink = true;
        config.Policy.System.StatsOutboundDownlink = true;

        config.Inbounds.Add(new InboundConfig
        {
            Tag = StatsApiTag,
            Listen = InboundListen,
            Port = apiPort,
            Protocol = StatsApiProtocol,
            Settings = new InboundSettings { Timeout = 0, Address = InboundListen },
            // 查询通道不嗅探。
            Sniffing = null
        });

        // 必须 Insert(0)：后面那条 `ip: geoip:private → blocked` 会把发往 127.0.0.1 的
        // 统计查询整个挡掉，规则是首命中即停，所以 api 规则只能排在它前面。
        config.Routing.Rules.Insert(0, new RoutingRule
        {
            OutboundTag = StatsApiTag,
            Type = "field",
            InboundTag = new List<string> { StatsApiTag }
        });
    }

    private static DnsConfig BuildDnsConfig()
    {
        return new DnsConfig
        {
            QueryStrategy = QueryStrategyUseIp,
            DisableCache = false,
            EnableParallelQuery = true,
            Servers = new List<DnsServer>
            {
                new DnsServerEntry
                {
                    Address = "223.5.5.5",
                    Domains = new List<string> { "geosite:cn" },
                    ExpectIPs = new List<string> { "geoip:cn" },
                    SkipFallback = true
                },
                new DnsServerEntry
                {
                    Address = "119.29.29.29",
                    Domains = new List<string> { "geosite:cn" },
                    ExpectIPs = new List<string> { "geoip:cn" },
                    SkipFallback = true
                },
                new DnsServerEntry
                {
                    Address = "https://1.1.1.1/dns-query",
                    Domains = new List<string> { "geosite:geolocation-!cn" }
                },
                new DnsServerEntry
                {
                    Address = "https://8.8.8.8/dns-query",
                    Domains = new List<string> { "geosite:geolocation-!cn" }
                }
            }
        };
    }

    /// <summary>
    /// 出站列表。类别归因关闭时返回与历史逐字节相同的三条；打开时在前面追加
    /// 几条只有 tag 不同的代理出站。
    /// </summary>
    private static List<OutboundConfig> BuildOutbounds(ServerProfile profile, bool categoryAttribution)
    {
        var outbounds = new List<OutboundConfig> { BuildProxyOutbound(profile) };

        if (categoryAttribution)
        {
            foreach ((_, string tag, _) in ServiceCategories.Routed)
            {
                OutboundConfig categoryOutbound = BuildProxyOutbound(profile);
                categoryOutbound.Tag = tag;
                outbounds.Add(categoryOutbound);
            }
        }

        outbounds.Add(BuildDirectOutbound());
        outbounds.Add(BuildBlockedOutbound());
        return outbounds;
    }

    private static RoutingConfig BuildRoutingConfig(ProxyMode mode, bool categoryAttribution = false)
    {
        // A resolved address set may contain both private and public addresses.
        // Block it before any domestic IP can win, in either mode.
        var rules = new List<RoutingRule>
        {
            new()
            {
                OutboundTag = BlockedOutboundTag,
                Type = "field",
                Ip = new List<string> { "geoip:private" }
            },
            new()
            {
                OutboundTag = BlockedOutboundTag,
                Type = "field",
                Protocol = new List<string> { "bittorrent" }
            }
        };

        if (mode == ProxyMode.Rule)
        {
            rules.Add(new RoutingRule
            {
                OutboundTag = DirectOutboundTag,
                Type = "field",
                Domain = new[] { "geosite:cn" }.Concat(DomesticMicrosoftDomains).ToList()
            });
        }

        // Attribution follows domestic domain decisions, but precedes the
        // generic proxy rule so YouTube and X retain their category counters.
        if (categoryAttribution)
        {
            foreach ((_, string tag, string[] geosites) in ServiceCategories.Routed)
            {
                IEnumerable<string> extraDomains = tag switch
                {
                    "cat-video" => OptimizedVideoDomains,
                    "cat-social" => OptimizedSocialDomains,
                    _ => Array.Empty<string>()
                };
                rules.Add(new RoutingRule
                {
                    OutboundTag = tag,
                    Type = "field",
                    Domain = geosites.Concat(extraDomains).Distinct(StringComparer.Ordinal).ToList()
                });
            }
        }

        if (mode == ProxyMode.Rule)
        {
            rules.Add(new RoutingRule
            {
                OutboundTag = ProxyOutboundTag,
                Type = "field",
                Domain = OptimizedProxyDomains.Concat(OptimizedVideoDomains).Concat(OptimizedSocialDomains)
                    .Append("geosite:geolocation-!cn").Distinct(StringComparer.Ordinal).ToList()
            });
            // Xray matches if ANY resolved address belongs to a rule. A foreign
            // address must therefore win before geoip:cn: mixed public sets go
            // through the proxy instead of accidentally leaking the foreign IP.
            rules.Add(new RoutingRule
            {
                OutboundTag = ProxyOutboundTag,
                Type = "field",
                Ip = new List<string> { "!geoip:cn" }
            });
            rules.Add(new RoutingRule
            {
                OutboundTag = DirectOutboundTag,
                Type = "field",
                Ip = new List<string> { "geoip:cn" }
            });
        }

        return new RoutingConfig
        {
            // Known domains avoid local DNS. Only unmatched Rule destinations
            // resolve for IP fallback; Global retains its original AsIs policy.
            DomainStrategy = mode == ProxyMode.Rule ? RuleRoutingDomainStrategy : GlobalRoutingDomainStrategy,
            Rules = rules
        };
    }

    private static OutboundConfig BuildProxyOutbound(ServerProfile profile)
    {
        return new OutboundConfig
        {
            Tag = ProxyOutboundTag,
            Protocol = VlessProtocol,
            Settings = new VlessOutboundSettings
            {
                Vnext = new List<VnextEntry>
                {
                    new()
                    {
                        Address = profile.Server,
                        Port = profile.Port,
                        Users = new List<VlessUser>
                        {
                            new()
                            {
                                Id = profile.Uuid,
                                Flow = profile.Flow,
                                Encryption = VlessEncryption,
                                Level = 0
                            }
                        }
                    }
                }
            },
            StreamSettings = new RealityStreamSettings
            {
                Network = RealityNetwork,
                Security = RealitySecurity,
                RealitySettings = new RealitySettings
                {
                    Show = false,
                    Fingerprint = profile.Fingerprint,
                    ServerName = profile.Sni,
                    PublicKey = profile.PublicKey,
                    ShortId = profile.ShortId,
                    SpiderX = profile.SpiderX
                }
            }
        };
    }

    private static OutboundConfig BuildDirectOutbound()
    {
        return new OutboundConfig
        {
            Tag = DirectOutboundTag,
            Protocol = FreedomProtocol,
            Settings = new FreedomSettings { DomainStrategy = DirectDomainStrategy }
        };
    }

    private static OutboundConfig BuildBlockedOutbound()
    {
        return new OutboundConfig
        {
            Tag = BlockedOutboundTag,
            Protocol = BlackholeProtocol,
            Settings = new BlackholeSettings()
        };
    }

    private static PolicyConfig BuildPolicyConfig()
    {
        return new PolicyConfig
        {
            Levels = new Dictionary<string, PolicyLevel>
            {
                ["0"] = new()
                {
                    StatsUserDownlink = true,
                    StatsUserOnline = true,
                    StatsUserUplink = true
                }
            },
            System = new PolicySystem
            {
                StatsInboundDownlink = true,
                StatsInboundUplink = true,
                StatsOutboundDownlink = false,
                StatsOutboundUplink = false
            }
        };
    }
}
