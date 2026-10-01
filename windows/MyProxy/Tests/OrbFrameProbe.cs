using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.ViewModels;
using MyProxy.Views;
using IOPath = System.IO.Path;

namespace MyProxy.Tests;

/// <summary>
/// 诊断工具，默认 no-op：设 <c>MYPROXY_FRAME_PROBE</c> 指向输出文件才跑。
///
/// 回答「呼吸动画在屏幕上到底有多少级可分辨状态」——观感的决定量。帧率再高，
/// 如果连续两帧渲染出来一模一样，人眼看到的就是卡顿。
///
/// 两个指标，回答的是不同的问题：
///   · **单点级数**：盯住一个像素，它取到过几种灰度。上限就是
///     Δ透明度 × 前景背景灰度差，青瓷这种低饱和色系下后者只有 90~110，
///     所以单点做不到三位数——要 81 级得 Δ透明度 ≈ 0.8，那不叫克制叫闪烁。
///   · **整帧级数**：整幅画面有多少种互不相同的渲染结果。它不受上面那条约束：
///     光晕在空间上是渐变的，不同半径处的像素在不同时刻跨过 8 位量化边界，
///     整帧的变化频率因此远高于任何单个像素。人眼积分的是整幅画面，
///     所以「看起来连不连续」跟的是这个数。
///
/// 做法是**回放**而不是抓屏：按 60Hz 的帧时刻，用动画自己的缓动函数
/// （<see cref="SineEase"/>，与 Storyboard 同一个）算出每一帧的透明度与缩放，
/// 写进真实控件模板里的 Halo / BreathScale，再用 RenderTargetBitmap 出图比对。
/// 幅度与时长全部从 DesignTokens 读，探针与界面不可能各写各的。
///
/// 为什么不抓真窗口：试过 <c>PrintWindow(PW_RENDERFULLCONTENT)</c>，在**本进程自己的
/// 窗口**上它返回 true 却画出一张全黑图（DDB 换成 DIBSection、抓帧挪到后台线程都不行；
/// 同一段 GDI 代码从屏幕 BitBlt 能正常抓到桌面，窗口也确实可见且在最前——
/// 排除法之后只剩「抓不了自己」这一条）。回放法反而更准：不丢帧、不受采样抖动影响、
/// 完全可复现。
///
/// 为什么不直接让 Storyboard 跑、定时抓 RenderTargetBitmap：实测过，
/// **RTB 取得到动画中的 Transform，取不到动画中的 Opacity**——让光晕的 Opacity
/// 从 0.04 动到 0.30，40 次采样灰度纹丝不动（缩放动画同样条件下是正常变化的）。
/// 本机写成本地值再渲染就一切正常，所以回放法绕开的正是这个坑。
///
/// 另一个坑：给元素挂 <c>BlurEffect</c> 后，RTB 会无视它的 <c>Opacity="0"</c> 照画不误，
/// 在球外糊出一圈本不存在的暗环——静态验收截图里那圈曾被当成投影看了很久。
/// 光晕改用径向渐变后这个假象一并消失了。
///
/// 已知偏差，不影响级数：RTB 不渲染 DropShadowEffect，所以球下面两层投影在这里
/// 缺席，球缘外的底色比真机略亮一点。那两层是静态的，不产生任何帧间差异。
/// </summary>
[TestClass]
public sealed class OrbFrameProbe
{
    private const int SettleMs = 700;

    private const double FrameRate = 60.0;

    [TestMethod]
    public void ProbeDistinctFrames()
    {
        string? outFile = Environment.GetEnvironmentVariable("MYPROXY_FRAME_PROBE");
        if (string.IsNullOrWhiteSpace(outFile))
        {
            return;
        }

        var lines = new List<string>();
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                // 一个 AppDomain 只能有一个 Application，而 MainViewSnapshotTool 也要建。
                // 无脑 new 的话，两个探针的环境变量同时设上、一次 dotnet test 跑完，
                // 后跑的那个必抛「不能在同一 AppDomain 中创建多个 Application 实例」。
                Application app = Application.Current
                    ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                EnsureTokensLoaded(app);

                lines.Add("已连接 · 呼吸        " + Probe(AppState.Connected, "Breath", true, true));
                lines.Add("已连接 · 只有透明度  " + Probe(AppState.Connected, "Breath", true, false));
                lines.Add("已连接 · 只有缩放    " + Probe(AppState.Connected, "Breath", false, true));
                lines.Add("连接中 · 脉冲        " + Probe(AppState.Connecting, "Pulse", true, true));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(120)), "frame probe timed out");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        File.WriteAllLines(outFile, lines);
        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }
    }

    /// <param name="alpha">让光晕透明度参与回放。</param>
    /// <param name="zoom">让球体缩放参与回放。</param>
    /// <summary>把 DesignTokens 并进 Application 资源；已经并过就不重复并。</summary>
    private static void EnsureTokensLoaded(Application app)
    {
        if (app.Resources.Contains("Motion.Orb.BreathAlphaFrom"))
        {
            return;
        }

        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/MyProxy;component/Themes/DesignTokens.xaml", UriKind.Relative)));
    }

    private static string Probe(AppState state, string motion, bool alpha, bool zoom)
    {
        ResourceDictionary res = Application.Current.Resources;
        double alphaFrom = (double)res[$"Motion.Orb.{motion}AlphaFrom"];
        double alphaTo = (double)res[$"Motion.Orb.{motion}AlphaTo"];
        double scaleTo = (double)res[$"Motion.Orb.{motion}Scale"];
        var duration = (Duration)res[$"Motion.Duration.{motion}"];

        var controller = new MainViewSnapshotTool.SnapshotController();
        var model = new MainViewModel(controller, new MainViewSnapshotTool.SnapshotLog())
        {
            IsLive = true
        };
        controller.SetState(state);

        var window = new Window
        {
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Left = -4000,
            Top = -4000,
            Background = (Brush)res["Brush.Background"],
            Content = new MainView { DataContext = model }
        };

        window.Show();
        Pump(SettleMs);

        Button orb = FindOrb(window)
            ?? throw new InvalidOperationException("orb button not found in the visual tree");
        var halo = (Ellipse)orb.Template.FindName("Halo", orb);
        var scale = (ScaleTransform)orb.Template.FindName("BreathScale", orb);

        // 触发器已经把呼吸 Storyboard 跑起来了。动画优先级高于本地值，
        // 不先停掉，下面写进去的 Opacity / Scale 会被动画当场覆盖回去。
        model.IsLive = false;
        Pump(120);

        Point orbTopLeft = orb.TransformToAncestor(window).Transform(new Point(0, 0));
        int w = (int)window.ActualWidth;
        int h = (int)window.ActualHeight;

        var box = new Int32Rect(
            (int)orbTopLeft.X, (int)orbTopLeft.Y,
            (int)orb.ActualWidth, (int)orb.ActualHeight);

        // 单点级数跟取哪一点强相关，定一个点等于先替结论选好答案。改为沿球心向右的
        // 一整条射线逐个半径统计，最后报最大的那一个——那才是「单点最多能有多少级」。
        // 起点取 74：球在峰值会胀到半径 73.5，更靠里的像素会被球本身扫过，量到的
        // 是球缘的明暗而不是光晕的。
        int centreX = box.X + (box.Width / 2);
        int probeY = box.Y + (box.Height / 2);
        const int RayFrom = 74;
        const int RayTo = 98;
        var ray = new HashSet<byte>[RayTo - RayFrom];
        for (int k = 0; k < ray.Length; k++)
        {
            ray[k] = new HashSet<byte>();
        }

        // 报告是裸文件名时 GetDirectoryName 给空串，是根路径时给 null——都落到当前目录。
        string reportDir =
            IOPath.GetDirectoryName(Environment.GetEnvironmentVariable("MYPROXY_FRAME_PROBE")!) is { Length: > 0 } dir
                ? dir
                : Directory.GetCurrentDirectory();

        int frameCount = (int)Math.Round(duration.TimeSpan.TotalSeconds * FrameRate);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        var frames = new HashSet<ulong>();
        HashSet<byte> greys;
        byte min = 255;
        byte max = 0;
        ulong previous = 0;
        int run = 0;
        int longestRun = 1;

        for (int i = 0; i < frameCount; i++)
        {
            double e = ease.Ease(i / (double)(frameCount - 1));
            halo.Opacity = alpha ? alphaFrom + ((alphaTo - alphaFrom) * e) : alphaFrom;
            scale.ScaleX = zoom ? 1 + ((scaleTo - 1) * e) : 1;
            scale.ScaleY = scale.ScaleX;

            window.UpdateLayout();
            byte[] px = Render(window, w, h, out RenderTargetBitmap bmp);

            // 峰值那一帧落盘：级数是数字，好不好看还得拿眼睛看。
            if (i == frameCount - 1 && alpha && zoom)
            {
                Save(bmp, IOPath.Combine(reportDir, $"orb-peak-{motion}.png"));
            }

            ulong signature = Fnv1a(px, w, box);
            frames.Add(signature);

            // 连续重复才是「掉帧感」的直接来源：画面卡住几帧不动，眼睛就读成顿挫。
            // 总级数高但集中在中段、两端各停十几帧，看起来照样是一卡一卡的。
            if (signature == previous)
            {
                run++;
                longestRun = Math.Max(longestRun, run);
            }
            else
            {
                run = 1;
                previous = signature;
            }

            for (int k = 0; k < ray.Length; k++)
            {
                // 绿通道对青瓷色最敏感
                byte g = px[((((probeY * w) + centreX + RayFrom + k) * 4)) + 1];
                ray[k].Add(g);
                min = Math.Min(min, g);
                max = Math.Max(max, g);
            }
        }

        int best = 0;
        int bestRadius = RayFrom;
        for (int k = 0; k < ray.Length; k++)
        {
            if (ray[k].Count > best)
            {
                best = ray[k].Count;
                bestRadius = RayFrom + k;
            }
        }

        greys = ray[bestRadius - RayFrom];

        window.Close();
        Pump(60);

        // 自检：回放必须真的在画面上产生变化。上一版探针就栽在这里——
        // 它给 ProbeAsync 传的透明度区间恒为 (0, 0)，40 次采样纹丝不动，
        // 却被读成「RenderTargetBitmap 取不到 Opacity 动画」，结论建立在假数据上。
        // 只跑缩放那一档不该要求单点灰度变化——光晕透明度是钉死的，那一点本来就不动。
        if (frames.Count <= 1 || (alpha && greys.Count <= 1))
        {
            throw new InvalidOperationException(
                $"replay produced {frames.Count} frames / {greys.Count} greys — nothing moved");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"整帧 {frames.Count,3} 级 / {frameCount,3} 帧，" +
            $"最长连续重复 {longestRun,2} 帧，" +
            $"单点最高 {greys.Count,3} 级 @r={bestRadius}（全射线灰度 {min}..{max}）");
    }

    private static byte[] Render(Window window, int width, int height, out RenderTargetBitmap bmp)
    {
        bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);
        var px = new byte[width * height * 4];
        bmp.CopyPixels(px, width * 4, 0);
        return px;
    }

    private static void Save(RenderTargetBitmap bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>只把球所在的方框喂进哈希：画面别处是静态的，算进去纯属浪费。</summary>
    private static ulong Fnv1a(byte[] px, int stridePixels, Int32Rect box)
    {
        ulong hash = 14695981039346656037;
        for (int y = box.Y; y < box.Y + box.Height; y++)
        {
            int row = y * stridePixels * 4;
            for (int i = row + (box.X * 4); i < row + ((box.X + box.Width) * 4); i++)
            {
                hash = (hash ^ px[i]) * 1099511628211;
            }
        }

        return hash;
    }

    /// <summary>能量球是界面上唯一一个 196×196 的 Button（Size.OrbHalo）。</summary>
    private static Button? FindOrb(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b && Math.Abs(b.ActualWidth - 196) < 0.5)
            {
                return b;
            }

            Button? hit = FindOrb(child);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static void Pump(int milliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
