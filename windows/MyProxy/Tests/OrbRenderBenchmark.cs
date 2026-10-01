using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MyProxy.Tests;

/// <summary>
/// 能量球的渲染帧率基准，不是断言测试。默认 no-op：设 <c>MYPROXY_FPS_BENCH</c>
/// 指向输出文件才会跑。
///
/// 量的是 <c>CompositionTarget.Rendering</c> 的触发次数——那是 WPF 合成器真正出帧的信号。
///
/// 两个踩过的坑，都写在这里免得再犯：
/// 1. **必须跑真正的消息循环**（<c>Application.Run</c>）。第一版用 `PushFrame` +
///    `Thread.Sleep(1)` 手动泵，量出来空窗口只有 33 fps、加动画掉到 10 fps，而去掉
///    全部 Effect 毫无改善——那不是应用慢，是那种泵法自己占住了 UI 线程，渲染排不上队。
/// 2. **一个 AppDomain 只能有一个 Application**，所以全部场景在同一线程顺序跑，
///    靠 `await Task.Delay` 让出消息泵，而不是每个场景起一个 STA 线程。
/// </summary>
[TestClass]
public sealed class OrbRenderBenchmark
{
    private const int SettleMs = 600;
    private const int MeasureMs = 2500;

    [TestMethod]
    public void MeasureOrbFrameRate()
    {
        string? outFile = Environment.GetEnvironmentVariable("MYPROXY_FPS_BENCH");
        if (string.IsNullOrWhiteSpace(outFile))
        {
            return;
        }

        var lines = new List<string>();
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                    new Uri("/MyProxy;component/Themes/DesignTokens.xaml", UriKind.Relative)));

                (string Name, Func<FrameworkElement> Build)[] scenarios =
                [
                    ("baseline (empty window)      ", () => new Grid()),
                    ("orb, effects + breathing     ", () => BuildOrb(effects: true, animate: true)),
                    ("orb, effects, no animation   ", () => BuildOrb(effects: true, animate: false)),
                    ("orb, NO effects + breathing  ", () => BuildOrb(effects: false, animate: true)),
                    ("orb, NO effects, no animation", () => BuildOrb(effects: false, animate: false)),
                ];

                app.Startup += async (_, _) =>
                {
                    foreach ((string name, Func<FrameworkElement> build) in scenarios)
                    {
                        lines.Add($"{name}: {await MeasureAsync(build)}");
                    }

                    app.Shutdown();
                };

                app.Run();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(90)), "fps benchmark timed out");
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

    private static async Task<string> MeasureAsync(Func<FrameworkElement> build)
    {
        var window = new Window
        {
            Width = 400,
            Height = 700,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = -4000,
            Top = -4000,
            Background = (Brush)Application.Current.Resources["Brush.Background"],
            Content = build()
        };
        window.Show();

        // 沉降：首帧布局与动画起步不该算进来。
        await Task.Delay(SettleMs);

        int frames = 0;
        EventHandler onRender = (_, _) => frames++;
        CompositionTarget.Rendering += onRender;
        var clock = Stopwatch.StartNew();

        await Task.Delay(MeasureMs);

        clock.Stop();
        CompositionTarget.Rendering -= onRender;
        window.Close();
        await Task.Delay(120);

        return string.Create(CultureInfo.InvariantCulture,
            $"{frames / clock.Elapsed.TotalSeconds,5:0.0} fps " +
            $"({frames} frames / {clock.Elapsed.TotalSeconds:0.00} s)");
    }

    /// <summary>
    /// 复刻能量球的图层结构。<paramref name="effects"/> 为 false 时整个去掉三个 Effect，
    /// 用来把「Effect 的成本」与「动画本身的成本」分开。
    /// </summary>
    private static FrameworkElement BuildOrb(bool effects, bool animate)
    {
        ResourceDictionary res = Application.Current.Resources;
        var root = new Grid { Width = 208, Height = 208 };

        var halo = new System.Windows.Shapes.Ellipse
        {
            Width = 196,
            Height = 196,
            Fill = (Brush)res["Brush.Success"],
            Opacity = 0.2
        };
        var ambient = NewOrbEllipse(res);
        var contact = NewOrbEllipse(res);

        if (effects)
        {
            halo.Effect = new BlurEffect { Radius = 26 };
            ambient.Effect = (Effect)res["Shadow.OrbAmbient"];
            contact.Effect = (Effect)res["Shadow.OrbContact"];
        }

        var orb = new System.Windows.Shapes.Ellipse
        {
            Width = 140,
            Height = 140,
            Fill = (Brush)res["Brush.Orb.Connected"]
        };

        var scale = new ScaleTransform(1, 1);
        var breathHost = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale };
        breathHost.Children.Add(orb);

        root.Children.Add(halo);
        root.Children.Add(ambient);
        root.Children.Add(contact);
        root.Children.Add(breathHost);

        if (animate)
        {
            var breath = new DoubleAnimation(1, 1.022, new Duration(TimeSpan.FromSeconds(2.4)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, breath);

            var glow = new DoubleAnimation(0.07, 0.20, new Duration(TimeSpan.FromSeconds(2.4)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            halo.BeginAnimation(UIElement.OpacityProperty, glow);
        }

        return root;
    }

    private static System.Windows.Shapes.Ellipse NewOrbEllipse(ResourceDictionary res)
        => new() { Width = 140, Height = 140, Fill = (Brush)res["Brush.Surface"] };
}
