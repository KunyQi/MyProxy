using System.Runtime.Versioning;

// 本程序集只在 Linux 上跑（它引用的 MyProxy.Linux.Core 也是）。声明出来，
// 平台分析器（CA1416）就不会把「Linux 专有 API」的调用报成可能在别的平台上炸。
[assembly: SupportedOSPlatform("linux")]
