using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using QuotaWidget;
using WidgetContext = QuotaWidget.AppContext;

// 与产品源码一起编译，无测试框架依赖。仅创建测试窗体，不加载/保存用户配置或请求额度接口。
internal static class WidgetBehaviorTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int _checks;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static object Get(object target, string name) { return target.GetType().GetField(name, Fields).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Fields).SetValue(target, value); }
    private static void Call(object target, string name, params object[] args)
    {
        target.GetType().GetMethod(name, Fields).Invoke(target, args);
    }
    private static bool Collapsed(FloatingForm form) { return (bool)Get(form, "_edgeCollapsed"); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }
    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Application.DoEvents();
    }

    private static void Dock(FloatingForm form, int side)
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        ((System.Windows.Forms.Timer)Get(form, "_trayRevealTimer")).Stop();
        var anim = (System.Windows.Forms.Timer)Get(form, "_animTimer");
        if (anim != null) anim.Stop();
        Call(form, "HideDetail");
        Set(form, "_edgeDocked", false);
        Set(form, "_edgeCollapsed", false);
        Set(form, "_vertical", side != 0);
        Set(form, "_edgeSide", side);
        form.Rebuild();
        int x = wa.Left + (Cursor.Position.X < wa.Left + wa.Width / 2 ? wa.Width * 2 / 3 : wa.Width / 4);
        int y = wa.Top + (Cursor.Position.Y < wa.Top + wa.Height / 2 ? wa.Height * 2 / 3 : wa.Height / 4);
        Set(form, "_edgeTargetX", x);
        Set(form, "_edgeTargetY", y);
        form.Location = side == 0 ? new Point(x, wa.Top) : new Point(side == 1 ? wa.Left : wa.Right - form.Width, y);
        Set(form, "_edgeDocked", true);
        form.Show();
        Call(form, "EdgeCollapse");
        Pump(350);
        Check(Collapsed(form), "dock side " + side + " starts collapsed");
    }

    private static void CheckMenu(WidgetContext ctx, string expected)
    {
        ctx.ShowMenu();
        WidgetMenu menu = null;
        foreach (Form form in Application.OpenForms) if (form is WidgetMenu) menu = (WidgetMenu)form;
        if (menu == null) throw new Exception("Context menu was not displayed");
        var items = (IList)Get(menu, "_items");
        Check((string)Get(items[0], "Label") == expected, "menu label: " + expected);
        menu.Close();
    }

    private static void RenderSettings(SettingsForm settings, string filename)
    {
        using (var bitmap = new Bitmap(settings.Width, settings.Height))
        {
            settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename));
        }
    }

    private static void CheckProviderSwitches(WidgetContext ctx, FloatingForm form)
    {
        ((System.Windows.Forms.Timer)Get(form, "_refreshTimer")).Stop();
        var ser = new JavaScriptSerializer();
        var old = ser.Deserialize<ProviderConfig>("{\"Type\":\"glm\",\"KeyEnc\":\"encrypted-example\"}");
        Check(old.Enabled && old.CanQuery, "old provider configs default to enabled");
        old.Enabled = false;
        var restored = ser.Deserialize<ProviderConfig>(ser.Serialize(old));
        Check(!restored.Enabled && restored.KeyEnc == old.KeyEnc, "disabled state persists without losing encrypted credentials");
        var clone = old.Clone();
        clone.Enabled = true;
        Check(!old.Enabled && clone.CanQuery && clone.KeyEnc == old.KeyEnc, "re-enabling a draft preserves credentials and does not mutate saved state");
        foreach (string type in new[] { "claude", "codex" })
        {
            var disabled = new ProviderConfig { Type = type, Enabled = false };
            Check(!disabled.CanQuery, "disabled " + type + " does not qualify for local credential lookup");
        }
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "codex", Enabled = false }, SettingsPal.For(false), 1f))
        {
            Call(dialog, "OnOk");
            Check(dialog.Result != null && !dialog.Result.Enabled, "editing a disabled provider keeps it disabled");
        }

        var active = new ProviderConfig { Type = "glm", Name = "启用示例", ShowName = true, Key = "test-example-key" };
        var paused = new ProviderConfig { Type = "glm", Name = "停用示例", ShowName = true, KeyEnc = "retained-example", Enabled = false };
        ctx.Cfg.Providers = new List<ProviderConfig> { active, paused,
            new ProviderConfig { Type = "codex", Name = "Codex", Enabled = false },
            new ProviderConfig { Type = "claude", Name = "Claude", Enabled = false } };
        Call(ctx, "ResetProviderDisplay");
        form.Rebuild();
        ctx.UpdateTrayTip();
        Check(ctx.Providers.Count == 1 && ctx.Providers[0].Name == active.Name,
            "display skips disabled providers while retaining an enabled account of the same type");
        Check(!ctx.Tray.Text.Contains(paused.Name) && !ctx.Tray.Text.Contains("Codex"), "tray summary excludes disabled providers");

        using (var settings = new SettingsForm(ctx))
        {
            var draft = (List<ProviderConfig>)Get(settings, "_editList");
            var cards = (List<Rectangle>)Get(settings, "_cardRects");
            Check(draft.Count == 4 && !draft[1].Enabled, "settings retain disabled entries for future re-enabling");
            Rectangle toggle = (Rectangle)typeof(SettingsForm).GetMethod("ProviderToggleRect", Fields).Invoke(settings, new object[] { cards[0] });
            Point hit = new Point(toggle.Left + toggle.Width / 2, toggle.Top + toggle.Height / 2);
            Call(settings, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, hit.X, hit.Y, 0));
            Check(!draft[0].Enabled && active.Enabled && draft.Count == 4, "row switch changes only its draft without deleting data");
            Call(settings, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, hit.X, hit.Y, 0));
            Check(draft[0].Enabled, "row switch can re-enable a provider");
            RenderSettings(settings, "providers-switches-light.png");
            Set(settings, "_pendDark", true);
            RenderSettings(settings, "providers-switches-dark.png");
            Check(!(bool)Get(draft[1], "_plainLoaded"), "rendering a disabled row does not decrypt its key");
        }
        Check(active.Enabled, "closing settings without saving leaves provider enabled");

        var inFlightSource = ctx.Cfg.Providers;
        var stopped = active.Clone();
        stopped.Enabled = false;
        ctx.Cfg.Providers = new List<ProviderConfig> { stopped, paused };
        Call(ctx, "ResetProviderDisplay");
        Check(!ctx.IsConfigured && ctx.Cfg.AllProvidersDisabled && ctx.Providers[0].Name == "已暂停",
            "disabling every provider immediately switches display to paused state");
        ctx.RefreshData();
        Check((int)Get(ctx, "_fetching") == 0 && ctx.Tray.Text.Contains("已暂停"), "all-disabled refresh stays idle and reports pause in tray");
        Call(ctx, "AutoOpenSettingsOnce");
        Check(Get(ctx, "_settings") == null, "all-disabled startup does not reopen settings");

        Set(ctx, "_fetching", 1);
        Call(ctx, "CompleteRefresh", inFlightSource, new List<ProviderStatus> { new ProviderStatus { Name = "stale disabled result" } });
        Check(ctx.Providers.Count == 1 && ctx.Providers[0].Name == "已暂停" && (int)Get(ctx, "_fetching") == 0,
            "an old in-flight response cannot bring a disabled provider back");
        var resumed = stopped.Clone();
        resumed.Enabled = true;
        ctx.Cfg.Providers = new List<ProviderConfig> { resumed, paused };
        Call(ctx, "ResetProviderDisplay");
        Check(ctx.IsConfigured && !ctx.Cfg.AllProvidersDisabled && ctx.Providers[0].Name == resumed.Name,
            "re-enabled provider returns to the display with its original settings");
    }

    [STAThread]
    private static int Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        try
        {
            var ser = new JavaScriptSerializer();
            var legacy = ser.Deserialize<Config>("{\"DisplayMode\":\"topbar\",\"AppBarScreen\":1,\"X\":300,\"Y\":120,\"FloatLayout\":\"card\",\"TopMost\":false,\"Providers\":[{\"Type\":\"glm\",\"KeyEnc\":\"retained-ciphertext\"}]}");
            Check(legacy.FloatingEnabled && legacy.FloatLayout == "card" && legacy.X == 300 && !legacy.TopMost,
                "legacy topbar config opens floating view and preserves preferences");
            Check(legacy.Providers[0].KeyEnc == "retained-ciphertext", "migration preserves encrypted provider data");
            string json = ser.Serialize(legacy);
            Check(!json.Contains("DisplayMode") && !json.Contains("AppBarScreen"), "saved schema removes topbar settings");
            legacy.FloatingEnabled = false;
            Check(!ser.Deserialize<Config>(ser.Serialize(legacy)).FloatingEnabled, "disabled state survives serialization");

            var ctx = (WidgetContext)FormatterServices.GetUninitializedObject(typeof(WidgetContext));
            var wa = Screen.PrimaryScreen.WorkingArea;
            ctx.Cfg = new Config { X = wa.Left + wa.Width / 3, Y = wa.Top + wa.Height / 3, TopMost = false, RefreshSec = 3600 };
            ctx.Providers = new List<ProviderStatus> { new ProviderStatus { Type = "glm", Name = "Test", Primary5h = new QuotaPool { Percent = 25 } } };
            ctx.Tray = new NotifyIcon();
            using (ctx.Tray)
            using (var form = new FloatingForm(ctx))
            {
                ctx.Floating = form;
                for (int side = 0; side < 3; side++)
                {
                    Dock(form, side);
                    form.RevealFromTray();
                    Check(!Collapsed(form), "tray reveals side " + side);
                    Pump(1200);
                    Check(!Collapsed(form), "one-second UI tick cannot end reveal on side " + side);
                    Pump(600);
                    Check(!Collapsed(form), "side " + side + " remains expanded before two seconds");
                    Pump(600);
                    Check(Collapsed(form), "side " + side + " collapses after two seconds");
                }

                Dock(form, 0);
                form.RevealFromTray();
                Pump(1200);
                form.RevealFromTray();
                Pump(1200);
                Check(!Collapsed(form), "repeated tray click restarts two-second interval");
                Pump(1100);
                Check(Collapsed(form), "repeated reveal eventually collapses");

                form.RevealFromTray();
                Pump(350);
                // 直接检查移入事件对定位计时的接管，不依赖测试期间用户保持光标不动。
                Call(form, "OnMouseEnter", EventArgs.Empty);
                Check(!Collapsed(form) && !((System.Windows.Forms.Timer)Get(form, "_trayRevealTimer")).Enabled,
                    "hover cancels the pending tray reveal deadline");
                Call(form, "OnMouseLeave", EventArgs.Empty);
                Pump(350);
                Check(Collapsed(form), "leaving after hover restores normal collapse");

                // 右键菜单必须保持展开，三条边及计时兜底都不能抢先收起。
                for (int side = 0; side < 3; side++)
                {
                    Dock(form, side); form.RevealFromTray();
                    Call(form, "OnMouseEnter", EventArgs.Empty);
                    ctx.ShowMenu();
                    var menu = (WidgetMenu)Get(ctx, "_menu");
                    Call(form, "OnMouseLeave", EventArgs.Empty);
                    Call(form, "EdgeCollapse");
                    Pump(2200);
                    Check(!Collapsed(form) && (bool)Get(form, "_contextMenuOpen"), "open context menu prevents collapse on side " + side);
                    menu.Close(); Pump(850);
                    Check(Collapsed(form) && !(bool)Get(form, "_contextMenuOpen"), "closing the menu restores automatic collapse on side " + side);
                }

                form.RevealFromTray(); ctx.ShowMenu();
                var outsideMenu = (WidgetMenu)Get(ctx, "_menu");
                Call(outsideMenu, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, -10, -10, 0));
                Pump(850);
                Check(Get(ctx, "_menu") == null && !(bool)Get(form, "_contextMenuOpen"), "clicking outside closes the menu and releases collapse hold");

                bool itemSelected = false;
                using (var clickMenu = new WidgetMenu())
                {
                    clickMenu.AddItem("测试动作", false, false, delegate { itemSelected = true; });
                    clickMenu.ShowAt(Cursor.Position);
                    IntPtr point = (IntPtr)(20 << 16 | 60);
                    SendMessage(clickMenu.Handle, 0x0201, (IntPtr)1, point);
                    SendMessage(clickMenu.Handle, 0x0202, IntPtr.Zero, point);
                    Pump(50);
                    Check(itemSelected, "mouse release still executes menu action before capture-loss dismissal");
                }

                form.Hide(); ctx.ShowMenu();
                var hiddenMenu = (WidgetMenu)Get(ctx, "_menu");
                Check(!form.Visible, "opening a tray menu does not enable a hidden floating window");
                hiddenMenu.Close(); form.Show();

                form.RevealFromTray();
                Call(form, "ShowDetail");
                Check(form._detail.Visible, "detail is visible before hiding host");
                form.Hide();
                form.RevealFromTray();
                form.RevealFromTray();
                Pump(2200);
                Check(!form.Visible && !form._detail.Visible, "hidden host and detail stay hidden after tray clicks and timeout");
                Check(((System.Windows.Forms.Timer)Get(form, "_refreshTimer")).Enabled, "background refresh continues while hidden");
                Check(!((System.Windows.Forms.Timer)Get(form, "_trayRevealTimer")).Enabled, "hide cancels pending reveal");
                CheckMenu(ctx, "开启悬浮窗");
                form.Show();
                CheckMenu(ctx, "关闭悬浮窗");

                ctx.Cfg.FloatingEnabled = false;
                Call(ctx, "ApplyFloatingConfig");
                Check(!form.Visible && ctx.MainForm == null, "applying settings preserves hidden state without an auto-shown MainForm");
                ctx.Cfg.FloatingEnabled = true;
                ctx.Cfg.FloatLayout = "card";
                Call(ctx, "ApplyFloatingConfig");
                Point cardPosition = form.Location;
                form.RevealFromTray();
                Pump(2200);
                Check(form.Visible && !Collapsed(form) && form.Location == cardPosition, "card layout remains visible in place");

                using (var settings = new SettingsForm(ctx))
                {
                    Call(settings, "SwitchPage", 1);
                    var rows = (IList)Get(settings, "_visRows");
                    Check(rows.Count == 2, "general settings contain layout and opacity after moving refresh to provider toolbar");
                    RenderSettings(settings, "settings-general.png");
                }
                CheckProviderSwitches(ctx, form);
                CustomApiTests.Run(Check, ctx, form);
            }
            Console.WriteLine("Completed " + _checks + " checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
