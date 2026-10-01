using System.Runtime.Versioning;

// 本程序集只在 Linux 上跑。声明出来有两个好处：平台分析器（CA1416）不再把
// File.SetUnixFileMode 这类调用报成「可能在 Windows 上炸」，而将来若有人把这个
// 程序集引用进别的平台的工程，编译器会立刻告诉他这些 API 在那里不可用。
[assembly: SupportedOSPlatform("linux")]
