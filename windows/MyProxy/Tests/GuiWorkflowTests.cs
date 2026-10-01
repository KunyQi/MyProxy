using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;
using MyProxy.ViewModels;
using MyProxy.Views;

namespace MyProxy.Tests;

// These tests exercise WPF controls, bindings and production view models on an
// STA dispatcher. They contact no real services; the reduced-motion regression
// uses one 1-DIP off-screen Window solely to attach the template to WPF's
// PresentationSource, without sending any input.
[TestClass]
public sealed class GuiWorkflowTests
{
    [TestMethod]
    public void BindingWorkflow_TypingEnablesSubmit_AndSuccessNotifiesOnce() => RunSta(() =>
    {
        var fixture = new BindingFixture();
        Assert.IsFalse(fixture.Submit.IsEnabled);
        foreach (char c in "abcd123")
        {
            fixture.Type(c.ToString());
            Assert.IsFalse(fixture.Submit.IsEnabled);
        }
        fixture.Type("4");
        Assert.AreEqual("ABCD-1234", fixture.Input.Text);
        Assert.AreEqual(fixture.Input.Text, fixture.Model.PairingCode);
        Assert.IsTrue(fixture.Submit.IsEnabled);

        fixture.Submit.Command.Execute(null);
        Pump();
        Assert.AreEqual("ABCD-1234", fixture.Service.LastCode);
        Assert.IsFalse(fixture.Input.IsEnabled);
        Assert.IsFalse(fixture.Submit.IsEnabled);
        Assert.AreEqual("正在绑定…", fixture.Submit.Content);
        fixture.Model.BindCommand.Execute(null);
        Assert.AreEqual(1, fixture.Service.Calls);

        fixture.Service.Pending.SetResult(new BindResult());
        PumpUntil(() => fixture.Completions == 1 && fixture.Submit.IsEnabled);
        Assert.AreEqual(1, fixture.Controller.MarkBoundCalls);
        Assert.AreEqual(AppState.Disconnected, fixture.Controller.State);
        Assert.IsTrue(fixture.Input.IsEnabled);
        Assert.AreEqual(Visibility.Collapsed, fixture.Error.Visibility);
        Assert.AreEqual(1, fixture.Completions);
    });

    /// <param name="blamesCode">
    /// 「配对码无效」该把输入框标红，「无法连接服务器」不该——后者与用户输入无关。
    /// 此前两者共用一个 HasInputError，任何失败都会让输入框变红，红框于是不再
    /// 是信息，而是噪声。
    /// </param>
    [DataTestMethod]
    [DataRow(ErrorCode.PairingInvalid, true)]
    [DataRow(ErrorCode.PairingExpired, true)]
    [DataRow(ErrorCode.ApiUnreachable, false)]
    public void BindingFailure_OnlyBlamesTheCodeWhenItIsTheCode(ErrorCode code, bool blamesCode) => RunSta(() =>
    {
        var fixture = new BindingFixture();
        fixture.Type("abcd1234");
        fixture.Submit.Command.Execute(null);
        string message = ErrorCodeMessages.Get(code);
        fixture.Service.Pending.SetException(new MyProxyException(message, code));

        TextBlock shown = blamesCode ? fixture.Error : fixture.FormError;
        TextBlock quiet = blamesCode ? fixture.FormError : fixture.Error;

        PumpUntil(() => shown.Visibility == Visibility.Visible && fixture.Submit.IsEnabled);
        Assert.AreEqual(message, shown.Text);
        Assert.AreEqual(Visibility.Collapsed, quiet.Visibility);
        Assert.AreEqual("ABCD-1234", fixture.Input.Text);
        Assert.IsTrue(fixture.Input.IsEnabled);
        Assert.AreEqual(0, fixture.Completions);
        Assert.AreEqual(0, fixture.Controller.MarkBoundCalls);
        if (blamesCode)
        {
            Assert.AreEqual(fixture.Resources["Brush.Error"], fixture.Input.BorderBrush);
        }
        else
        {
            Assert.AreNotEqual(fixture.Resources["Brush.Error"], fixture.Input.BorderBrush);
        }

        fixture.Input.Select(8, 1);
        fixture.Type("5");
        Assert.AreEqual("ABCD-1235", fixture.Model.PairingCode);
        Assert.AreEqual(Visibility.Collapsed, shown.Visibility);
        Assert.AreNotEqual(fixture.Resources["Brush.Error"], fixture.Input.BorderBrush);
        fixture.Submit.Command.Execute(null);
        Assert.AreEqual(2, fixture.Service.Calls);
        Assert.AreEqual("ABCD-1235", fixture.Service.LastCode);
        fixture.Service.Pending.SetResult(new BindResult());
        PumpUntil(() => fixture.Completions == 1);
    });

    [TestMethod]
    public void UnexpectedBindingFailure_UsesFriendlyError_AndResetClearsControls() => RunSta(() =>
    {
        var fixture = new BindingFixture();
        fixture.Type("abcd1234");
        fixture.Submit.Command.Execute(null);
        fixture.Service.Pending.SetException(new InvalidOperationException("internal-diagnostic-only"));
        // 未知失败与两个输入都无关，走表单级槽，不冤枉任何一个框。
        PumpUntil(() => fixture.FormError.Visibility == Visibility.Visible && fixture.Submit.IsEnabled);
        Assert.AreEqual(ErrorCodeMessages.Get(ErrorCode.Unknown), fixture.FormError.Text);
        Assert.IsFalse(fixture.FormError.Text.Contains("internal-diagnostic-only"));
        Assert.AreEqual(Visibility.Collapsed, fixture.Error.Visibility);
        Assert.AreEqual(1, fixture.Log.Errors);
        fixture.Model.Reset();
        Pump();
        Assert.AreEqual("", fixture.Input.Text);
        Assert.IsFalse(fixture.Submit.IsEnabled);
        Assert.IsTrue(fixture.Input.IsEnabled);
        Assert.AreEqual(Visibility.Collapsed, fixture.Error.Visibility);
        Assert.AreEqual(Visibility.Collapsed, fixture.FormError.Visibility);
        Assert.IsTrue(BindingOperations.IsDataBound(fixture.Input, TextBox.TextProperty));
    });

    [TestMethod]
    public void InvalidInput_NeverCallsBindingService() => RunSta(() =>
    {
        var fixture = new BindingFixture();
        foreach (string text in new[] { "", "ABC", "ABCD-123", "ABCD-12!?" })
        {
            fixture.Input.SelectAll();
            fixture.Type(text);
            Assert.IsFalse(fixture.Submit.IsEnabled, text);
            fixture.Model.BindCommand.Execute(null);
        }
        Assert.AreEqual(0, fixture.Service.Calls);
        Assert.AreEqual(0, fixture.Completions);
    });

    [TestMethod]
    public void BindingInProgress_DisabledPaletteWinsOverActiveHoverAnimation() => RunSta(() =>
    {
        var fixture = new BindingFixture();
        fixture.Type("abcd1234");
        fixture.Submit.ApplyTemplate();
        var hover = (Border)fixture.Submit.Template.FindName("HoverLayer", fixture.Submit);
        var pressed = (Border)fixture.Submit.Template.FindName("PressLayer", fixture.Submit);
        var background = (Border)fixture.Submit.Template.FindName("Bd", fixture.Submit);
        hover.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(100)));
        fixture.Submit.Command.Execute(null);
        Pump();
        Assert.AreEqual(Visibility.Hidden, hover.Visibility);
        Assert.AreEqual(Visibility.Hidden, pressed.Visibility);
        Assert.AreEqual(fixture.Resources["Brush.Border"], background.Background);
        Assert.AreEqual(fixture.Resources["Brush.TextSecondary"], fixture.Submit.Foreground);
        fixture.Service.Pending.SetResult(new BindResult());
        PumpUntil(() => fixture.Submit.IsEnabled);
        Assert.AreEqual(Visibility.Visible, hover.Visibility);
    });

    [DataTestMethod]
    [DataRow(AppState.Unbound, false, false, "启动")]
    [DataRow(AppState.Disconnected, true, false, "启动")]
    [DataRow(AppState.Connecting, false, false, "正在启动")]
    [DataRow(AppState.Connected, true, true, "停止")]
    [DataRow(AppState.Disconnecting, false, false, "正在停止")]
    [DataRow(AppState.Error, true, false, "重试")]
    public void MainViewState_DrivesBoundButtonAndModeAvailability(
        AppState state, bool primaryEnabled, bool modeEnabled, string buttonText) => RunSta(() =>
    {
        var controller = new FakeController();
        var model = new MainViewModel(controller, new TestLog());
        var button = new Button();
        Bind(button, Button.CommandProperty, model, nameof(model.PrimaryCommand));
        Bind(button, Button.ContentProperty, model, nameof(model.ButtonText));
        var mode = new RadioButton();
        Bind(mode, UIElement.IsEnabledProperty, model, nameof(model.IsModeSelectionEnabled));
        controller.SetState(state);
        Pump();
        Assert.AreEqual(primaryEnabled, button.IsEnabled);
        Assert.AreEqual(modeEnabled, mode.IsEnabled);
        Assert.AreEqual(buttonText, button.Content);

        // AnimationsEnabled 的出厂值来自 SystemParameters.ClientAreaAnimation，
        // 也就是跑测试这台机器的系统动效开关。不显式设定的话，这条断言只在
        // 开了动效的机器上成立——CI 绿、本机红，而两者都没说明产品有问题。
        model.AnimationsEnabled = true;
        model.IsLive = true;
        Pump();
        Assert.AreEqual(state == AppState.Connecting ? Visibility.Visible : Visibility.Collapsed,
            model.ConnectingSpinnerVisibility);

        // 反过来也钉住：关掉动效时转圈必须消失，这条此前完全没有覆盖。
        model.AnimationsEnabled = false;
        Pump();
        Assert.AreEqual(Visibility.Collapsed, model.ConnectingSpinnerVisibility);
    });

    [TestMethod]
    public void MainCommands_InvokeExpectedActionsAndSettingsNotification() => RunSta(() =>
    {
        var controller = new FakeController();
        var model = new MainViewModel(controller, new TestLog());
        controller.SetState(AppState.Disconnected);
        model.PrimaryCommand.Execute(null);
        Assert.AreEqual(1, controller.StartCalls);
        controller.SetState(AppState.Connected);
        model.PrimaryCommand.Execute(null);
        Assert.AreEqual(1, controller.StopCalls);
        model.IsGlobalMode = true;
        Assert.AreEqual(ProxyMode.Global, controller.Mode);
        Assert.IsTrue(model.IsGlobalMode);
        int settingsRequests = 0;
        model.SettingsRequested += () => settingsRequests++;
        model.SettingsCommand.Execute(null);
        Assert.AreEqual(1, settingsRequests);
    });

    [TestMethod]
    public void SwitchingFeedback_IsExclusive_AndDisablesConflictingActions() => RunSta(() =>
    {
        var controller = new FakeController { LatencyMs = 38 };
        var model = new MainViewModel(controller, new TestLog());
        controller.SetState(AppState.Connected);
        Assert.AreEqual(Visibility.Visible, model.LatencyTextVisibility);
        controller.IsSwitching = true;
        controller.LastSwitchErrorMessage = "previous error";
        controller.SetState(AppState.Connected);
        Assert.IsFalse(model.PrimaryCommand.CanExecute(null));
        Assert.IsFalse(model.IsModeSelectionEnabled);
        Assert.AreEqual(Visibility.Visible, model.IsSwitchingTextVisible);
        Assert.AreEqual(Visibility.Collapsed, model.SwitchErrorVisibility);
        Assert.AreEqual(Visibility.Collapsed, model.LatencyTextVisibility);
        controller.IsSwitching = false;
        controller.SetState(AppState.Connected);
        Assert.AreEqual(Visibility.Visible, model.SwitchErrorVisibility);
        Assert.AreEqual(Visibility.Collapsed, model.LatencyTextVisibility);
        controller.LastSwitchErrorMessage = null;
        controller.SetState(AppState.Connected);
        Assert.AreEqual(Visibility.Visible, model.LatencyTextVisibility);
    });

    [TestMethod]
    public void CheckFeedback_YieldsToSwitchingAndSwitchErrors() => RunSta(() =>
    {
        var controller = new FakeController { LatencyMs = 38 };
        var model = new MainViewModel(controller, new TestLog());
        controller.SetState(AppState.Connected);
        controller.SetCheck(Verdict(EgressVerdict.Verified, reachable: true, latencyMs: 20));
        controller.IsSwitching = true;
        controller.SetState(AppState.Connected);
        Assert.AreEqual(Visibility.Visible, model.IsSwitchingTextVisible);
        Assert.AreEqual(Visibility.Collapsed, model.CheckResultVisibility);

        controller.SetChecking(true);
        Assert.AreEqual(Visibility.Collapsed, model.CheckResultVisibility);
        controller.IsSwitching = false;
        controller.LastSwitchErrorMessage = "切换失败";
        controller.SetState(AppState.Connected);
        Assert.AreEqual(Visibility.Visible, model.SwitchErrorVisibility);
        Assert.AreEqual(Visibility.Collapsed, model.CheckResultVisibility);
        controller.SetChecking(false);
        Assert.AreEqual(Visibility.Collapsed, model.CheckResultVisibility);

        controller.LastSwitchErrorMessage = null;
        controller.SetState(AppState.Connected);
        Assert.AreEqual(Visibility.Visible, model.CheckResultVisibility);
        Assert.AreEqual(Visibility.Collapsed, model.LatencyTextVisibility);
    });

    [DataTestMethod]
    [DataRow(UpdateCheckStatus.Failed, false)]
    [DataRow(UpdateCheckStatus.UpToDate, true)]
    [DataRow(UpdateCheckStatus.UpdateAvailable, false)]
    public void UpdateStatus_OnlyConfirmsLatestAfterSuccessfulComparison(UpdateCheckStatus status, bool latest) => RunSta(() =>
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StorageService(dataRootOverride: root);
            Task initialSave = storage.SaveSettingsAsync(new AppSettings { AutoUpdateCheck = true }, CancellationToken.None);
            PumpUntil(() => initialSave.IsCompleted);
            initialSave.GetAwaiter().GetResult();
            var update = new ControlledUpdateService();
            var model = new SettingsViewModel(storage, new NoOpStartup(), update, new FakeController(), new TestLog());
            Assert.AreEqual($"当前 {model.Version}", model.VersionText, "请求尚未返回时只能显示版本号");
            update.Pending.SetResult(new UpdateCheckResult(status,
                status == UpdateCheckStatus.UpdateAvailable ? new UpdateInfo { Version = "9.0.0" } : null));
            Pump();
            Assert.AreEqual(latest, model.VersionText.Contains("已是最新版本"));
            Assert.AreEqual(status == UpdateCheckStatus.UpdateAvailable, model.UpdateInfo is not null);

            model.AutoUpdateCheck = false;
            Task save = model.FlushAsync();
            PumpUntil(() => save.IsCompleted);
            Assert.AreEqual($"当前 {model.Version}", model.VersionText);
            Assert.IsNull(model.UpdateInfo);
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    });

    [TestMethod]
    public void UpdateCheck_IsOffForNewInstallAndPreservesExplicitOptIn() => RunSta(() =>
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StorageService(dataRootOverride: root);
            var update = new ControlledUpdateService();
            var model = new SettingsViewModel(storage, new NoOpStartup(), update, new FakeController(), new TestLog());
            Pump();
            Assert.IsFalse(model.AutoUpdateCheck);
            Assert.AreEqual(0, update.CheckCalls, "新安装不应在打开设置时请求更新接口");

            model.AutoUpdateCheck = true;
            PumpUntil(() => update.CheckCalls == 1);
            update.Pending.SetResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate, null));
            Task save = model.FlushAsync();
            PumpUntil(() => save.IsCompleted);
            save.GetAwaiter().GetResult();

            var reloaded = new SettingsViewModel(storage, new NoOpStartup(), update, new FakeController(), new TestLog());
            Assert.IsTrue(reloaded.AutoUpdateCheck);
            Assert.AreEqual(2, update.CheckCalls, "已保存的显式开启偏好应在重新打开时保留");
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    });

    [TestMethod]
    public void CheckVerdict_MapsToTextSeverityAndYieldsTheLatencyLine() => RunSta(() =>
    {
        var controller = new FakeController();
        var model = new MainViewModel(controller, new TestLog());
        controller.SetState(AppState.Connected);
        controller.LatencyMs = 46;

        // 自检前：显示连接时自动量到的那一次延迟。
        Assert.AreEqual(Visibility.Visible, model.LatencyTextVisibility);
        Assert.AreEqual(CheckSeverity.None, model.CheckSeverity);
        Assert.AreEqual("", model.CheckResultText);

        controller.SetCheck(Verdict(EgressVerdict.Verified, reachable: true, latencyMs: 38));
        Assert.AreEqual("已验证 · 38 ms", model.CheckResultText);
        Assert.AreEqual(CheckSeverity.Good, model.CheckSeverity);
        Assert.AreEqual(Visibility.Visible, model.CheckResultVisibility);
        Assert.AreEqual(Visibility.Collapsed, model.LatencyTextVisibility,
            "实测结论比自动延迟更新更准，两者共用一个槽时自动延迟必须让位");
        Assert.AreEqual("网络连接正常", model.StatusSubText);

        controller.SetCheck(Verdict(EgressVerdict.Unknown, reachable: true, latencyMs: 38));
        Assert.AreEqual("已连通 · 38 ms", model.CheckResultText,
            "出口未判定时不得声称「已验证」");
        Assert.AreEqual(CheckSeverity.Good, model.CheckSeverity);
    });

    [TestMethod]
    public void BypassedVerdict_TurnsBadAndSilencesTheContradictingSubText() => RunSta(() =>
    {
        var controller = new FakeController();
        var model = new MainViewModel(controller, new TestLog());
        controller.SetState(AppState.Connected);
        controller.LatencyMs = 46;

        controller.SetCheck(Verdict(EgressVerdict.Bypassed, reachable: true, latencyMs: 12));

        Assert.AreEqual("连接失败，请重试", model.CheckResultText);
        Assert.AreEqual(CheckSeverity.Bad, model.CheckSeverity);
        // 一边写「网络连接正常」一边写「连接失败」，用户只会更没底。
        Assert.AreEqual("", model.StatusSubText);
        Assert.AreEqual(Visibility.Collapsed, model.StatusSubTextVisibility);
    });

    [TestMethod]
    public void UnreachableVerdict_ReportsFailureWithoutLatency() => RunSta(() =>
    {
        var controller = new FakeController();
        var model = new MainViewModel(controller, new TestLog());
        controller.SetState(AppState.Connected);

        controller.SetCheck(new ConnectionCheckResult
        {
            Reachable = false,
            Egress = EgressVerdict.Unknown,
            Error = ErrorCode.ConnectTestFailed
        });

        Assert.AreEqual("检测未通过，请重试", model.CheckResultText);
        Assert.AreEqual(CheckSeverity.Bad, model.CheckSeverity);
    });

    [TestMethod]
    public void Disconnecting_DropsTheVerdictFromTheSurface() => RunSta(() =>
    {
        var controller = new FakeController();
        var model = new MainViewModel(controller, new TestLog());
        controller.SetState(AppState.Connected);
        controller.SetCheck(Verdict(EgressVerdict.Verified, reachable: true, latencyMs: 38));
        Assert.AreEqual(Visibility.Visible, model.CheckResultVisibility);

        // 断开后那份结论描述的已经不是当前链路，展示旧值会比不展示更误导。
        controller.SetState(AppState.Disconnected);
        Assert.AreEqual("", model.CheckResultText);
        Assert.AreEqual(CheckSeverity.None, model.CheckSeverity);
        Assert.AreEqual(Visibility.Collapsed, model.CheckResultVisibility);
    });

    [TestMethod]
    public void CheckCommand_IsGatedByStateAndReentrancy() => RunSta(() =>
    {
        var controller = new FakeController();
        var model = new MainViewModel(controller, new TestLog());

        Assert.IsFalse(model.IsCheckEnabled, "未连接时没有链路可测");

        controller.SetState(AppState.Connected);
        Assert.IsTrue(model.IsCheckEnabled);
        Assert.AreEqual("检测连接", model.CheckButtonText);

        controller.IsSwitching = true;
        controller.SetState(AppState.Connected);
        Assert.IsFalse(model.IsCheckEnabled, "切换中链路正在重建，测了也不作数");

        controller.IsSwitching = false;
        controller.SetChecking(true);
        Assert.IsFalse(model.IsCheckEnabled, "一轮自检未结束前不得再发一轮");
        Assert.AreEqual("正在检测…", model.CheckButtonText);
        Assert.AreEqual("正在检测…", model.CheckResultText);
        Assert.AreEqual(CheckSeverity.Running, model.CheckSeverity);

        controller.SetChecking(false);
        model.CheckCommand.Execute(null);
        PumpUntil(() => controller.CheckCalls == 1);
    });

    [TestMethod]
    public void HidingTheWindow_SuspendsSamplingAndStopsTheSpinner() => RunSta(() =>
    {
        var controller = new FakeController();
        var model = new MainViewModel(controller, new TestLog()) { AnimationsEnabled = true };
        controller.SetState(AppState.Connecting);

        Assert.IsTrue(model.IsLive, "默认视为可见");
        Assert.AreEqual(Visibility.Visible, model.ConnectingSpinnerVisibility);

        // 主页不可见时停止采样与动画。
        model.IsLive = false;
        Assert.IsTrue(controller.TrafficSuspended);
        Assert.AreEqual(Visibility.Collapsed, model.ConnectingSpinnerVisibility,
            "不可见时 spinner 不该继续转");

        model.IsLive = true;
        Assert.IsFalse(controller.TrafficSuspended);
        Assert.AreEqual(Visibility.Visible, model.ConnectingSpinnerVisibility);

        // 重复赋同一个值不该反复通知控制器
        int before = controller.SuspendCalls;
        model.IsLive = true;
        Assert.AreEqual(before, controller.SuspendCalls);
    });

    [TestMethod]
    public void ReducedMotion_ReleasesOrbClocksWithoutSuspendingVisibleTraffic() => RunSta(() =>
    {
        var controller = new FakeController();
        var model = new MainViewModel(controller, new TestLog()) { AnimationsEnabled = true };
        controller.SetState(AppState.Connected);
        ResourceDictionary resources = IconGeometryTests.LoadTokens();
        var button = new Button { DataContext = model, Style = (Style)resources["OrbButtonStyle"] };
        var window = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -4000,
            Top = -4000,
            Content = button
        };
        window.Show();
        try
        {
            button.ApplyTemplate();
            Pump();
            var host = (FrameworkElement)button.Template.FindName("BreathHost", button);
            ScaleTransform CurrentScale() => (ScaleTransform)host.RenderTransform;
            Assert.IsTrue(CurrentScale().HasAnimatedProperties);

            model.AnimationsEnabled = false;
            PumpUntil(() => !CurrentScale().HasAnimatedProperties);
            Assert.IsFalse(CurrentScale().HasAnimatedProperties, "关闭动画应移除循环时钟");
            Assert.AreEqual(1d, CurrentScale().ScaleX);
            Assert.IsFalse(controller.TrafficSuspended, "减少动效不应停掉可见的数据");
            Assert.AreEqual("已连接", model.StatusText);

            model.AnimationsEnabled = true;
            PumpUntil(() => CurrentScale().HasAnimatedProperties);
            Assert.IsTrue(CurrentScale().HasAnimatedProperties);
            model.IsLive = false;
            PumpUntil(() => !CurrentScale().HasAnimatedProperties);
            Assert.IsFalse(CurrentScale().HasAnimatedProperties);
            Assert.IsTrue(controller.TrafficSuspended);
        }
        finally
        {
            window.Close();
            Pump();
        }
    });

    [TestMethod]
    public void TemporarilyLockedDevice_DoesNotCrashSettingsOrEraseIdentity() => RunSta(() =>
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StorageService(dataRootOverride: root);
            var update = new ControlledUpdateService();
            string path = System.IO.Path.Combine(root, "device.dat");
            var log = new TestLog();
            SettingsViewModel model;
            using (var locked = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
            {
                model = new SettingsViewModel(storage, new NoOpStartup(), update, new FakeController(), log);
                Assert.AreEqual(Visibility.Visible, model.ErrorVisibility);
                Assert.AreEqual(1, log.Errors);
                Assert.IsTrue(System.IO.File.Exists(path));
            }
            Task save = storage.SaveDeviceAsync(new DeviceConfig { DeviceName = "工作电脑" }, CancellationToken.None);
            PumpUntil(() => save.IsCompleted);
            save.GetAwaiter().GetResult();
            model.RefreshDevice();
            Assert.AreEqual("工作电脑", model.DeviceName);
            Assert.AreEqual(Visibility.Collapsed, model.ErrorVisibility);

            using (var locked = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
            {
                model.RefreshDevice();
                Assert.AreEqual("工作电脑", model.DeviceName);
                Assert.AreEqual(Visibility.Visible, model.ErrorVisibility);
            }
            Assert.AreEqual("工作电脑", storage.LoadDevice()!.DeviceName);
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    });

    private static ConnectionCheckResult Verdict(EgressVerdict egress, bool reachable, int latencyMs)
        => new()
        {
            Reachable = reachable,
            LatencyMs = latencyMs,
            BestLatencyMs = latencyMs - 4,
            SampleCount = 5,
            Egress = egress,
            Error = ErrorCode.ConnectTestFailed
        };

    private sealed class BindingFixture
    {
        internal ResourceDictionary Resources { get; } = (ResourceDictionary)Application.LoadComponent(
            new Uri("/MyProxy;component/Themes/DesignTokens.xaml", UriKind.Relative));
        internal ControlledBindingService Service { get; } = new();
        internal FakeController Controller { get; } = new();
        internal TestLog Log { get; } = new();
        internal BindViewModel Model { get; }
        internal TextBox Input { get; } = new() { MaxLength = 9, CharacterCasing = CharacterCasing.Upper };
        internal Button Submit { get; } = new();
        internal TextBlock Error { get; } = new();
        internal TextBlock FormError { get; } = new();
        internal int Completions { get; private set; }

        internal BindingFixture()
        {
            Model = new BindViewModel(Service, Controller, Log);
            Model.BindCompleted += () => Completions++;
            Input.Style = (Style)Resources["TextInputErrorStateStyle"];
            Submit.Style = (Style)Resources["PrimaryButtonStyle"];
            Bind(Input, TextBox.TextProperty, Model, nameof(Model.PairingCode), BindingMode.TwoWay);
            Bind(Input, UIElement.IsEnabledProperty, Model, nameof(Model.IsInputEnabled));
            Bind(Input, FrameworkElement.TagProperty, Model, nameof(Model.HasCodeError));
            Bind(Submit, Button.CommandProperty, Model, nameof(Model.BindCommand));
            Bind(Submit, Button.ContentProperty, Model, nameof(Model.BindButtonText));
            Bind(Error, TextBlock.TextProperty, Model, nameof(Model.CodeErrorMessage));
            Bind(Error, UIElement.VisibilityProperty, Model, nameof(Model.CodeErrorVisibility));
            Bind(FormError, TextBlock.TextProperty, Model, nameof(Model.FormErrorMessage));
            Bind(FormError, UIElement.VisibilityProperty, Model, nameof(Model.FormErrorVisibility));
            _ = new PairingCodeInput(Input);
            Pump();
        }

        internal void Type(string text)
        {
            int start = Input.SelectionStart;
            Input.SelectedText = text;
            Input.Select(start + text.Length, 0);
            Pump();
        }
    }

    private static void Bind(DependencyObject target, DependencyProperty property,
        object source, string path, BindingMode mode = BindingMode.OneWay) =>
        BindingOperations.SetBinding(target, property, new Binding(path)
        { Source = source, Mode = mode, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do { Pump(); if (complete()) return; Thread.Sleep(1); } while (DateTime.UtcNow < deadline);
        Assert.Fail("GUI command continuation did not complete");
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "GUI code test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ControlledUpdateService : IUpdateService
    {
        public TaskCompletionSource<UpdateCheckResult> Pending { get; } = new();
        public int CheckCalls { get; private set; }
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
        {
            CheckCalls++;
            return Pending.Task;
        }

        public Task<AssignedUpdate?> FetchAssignedAsync(CancellationToken ct)
            => Task.FromResult<AssignedUpdate?>(null);

        public Task ReportInstallAsync(string releaseId, string status, string detail, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class NoOpStartup : IStartupService
    {
        public bool IsAutoStartEnabled() => false;
        public void SetAutoStartEnabled(bool enabled) { }
        public void RemoveLegacyAutoStart() { }
    }

    private sealed class ControlledBindingService : IBindingService
    {
        internal int Calls { get; private set; }
        internal string? LastCode { get; private set; }

        internal TaskCompletionSource<BindResult> Pending { get; private set; } = new();

        public Task<BindResult> BindAsync(string pairingCode, CancellationToken ct)
        {
            Calls++;
            LastCode = pairingCode;
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Pending.Task;
        }
    }

    private sealed class FakeController : IConnectionController
    {
        public AppState State { get; private set; } = AppState.Unbound;
        public ProxyMode Mode { get; private set; } = ProxyMode.Rule;
        public bool IsSwitching { get; set; }
        public int? LatencyMs { get; set; }
        public ErrorCode LastErrorCode => ErrorCode.Unknown;
        public string LastErrorMessage => "无法连接服务器";
        public string? LastSwitchErrorMessage { get; set; }
        internal int MarkBoundCalls { get; private set; }
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }
        public event Action? StateChanged;
        public event Action? ModeChanged;
        public event Action? BindingRequired { add { } remove { } }
        internal void SetState(AppState state) { State = state; StateChanged?.Invoke(); }
        public void MarkBound() { MarkBoundCalls++; SetState(AppState.Disconnected); }
        public void InitializeMode(ProxyMode mode) { Mode = mode; ModeChanged?.Invoke(); }
        public Task StartAsync(CancellationToken ct) { StartCalls++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { StopCalls++; return Task.CompletedTask; }
        public Task SwitchModeAsync(ProxyMode mode, CancellationToken ct) { InitializeMode(mode); return Task.CompletedTask; }
        public Task RebindAsync(CancellationToken ct) => Task.CompletedTask;

        public bool IsChecking { get; private set; }
        public ConnectionCheckResult? LastCheck { get; private set; }
        public event Action? CheckChanged;
        internal int CheckCalls { get; private set; }

        /// <summary>供测试摆布自检结论，不经过真实网络。</summary>
        internal void SetCheck(ConnectionCheckResult? result)
        {
            LastCheck = result;
            CheckChanged?.Invoke();
        }

        internal void SetChecking(bool checking)
        {
            IsChecking = checking;
            CheckChanged?.Invoke();
        }


        internal int SuspendCalls { get; private set; }
        internal bool TrafficSuspended { get; private set; }

        public void SetTrafficSamplingSuspended(bool suspended)
        {
            SuspendCalls++;
            TrafficSuspended = suspended;
        }

        public TrafficRate TrafficRate => TrafficRate.Zero;
        public IReadOnlyList<TrafficRate> TrafficSamples => Array.Empty<TrafficRate>();
        public event Action? TrafficChanged { add { } remove { } }
        public event Action<HeartbeatResult>? HeartbeatReceived { add { } remove { } }

        public Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken ct)
        {
            CheckCalls++;
            ConnectionCheckResult result = LastCheck ?? new ConnectionCheckResult
            {
                Reachable = true,
                LatencyMs = 38,
                BestLatencyMs = 31,
                SampleCount = 5,
                Egress = EgressVerdict.Verified,
                Error = ErrorCode.ConnectTestFailed
            };
            SetCheck(result);
            return Task.FromResult(result);
        }
    }

    private sealed class TestLog : ILogService
    {
        internal int Errors { get; private set; }
        public void RegisterSensitiveValue(string value) { }
        public void Info(string scope, string message) { }
        public void Warn(string scope, string message) { }
        public void Error(string scope, string message, Exception? ex = null) => Errors++;
    }
}
