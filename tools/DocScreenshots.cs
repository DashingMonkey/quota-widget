using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using QuotaWidget;
using WidgetContext = QuotaWidget.AppContext;

// Documentation images use real UI renderers with in-memory demo data only.
// Never construct the normal AppContext: it loads credentials and starts queries.
internal static class DocScreenshots
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static string _output;
    private static object Get(object obj, string name) { return obj.GetType().GetField(name, Flags).GetValue(obj); }
    private static void Set(object obj, string name, object value) { obj.GetType().GetField(name, Flags).SetValue(obj, value); }
    private static object Call(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, Flags).Invoke(obj, args); }
    private static void StopTimers(object obj)
    {
        foreach (var field in obj.GetType().GetFields(Flags))
        {
            var timer = field.GetValue(obj) as System.Windows.Forms.Timer;
            if (timer != null) timer.Stop();
        }
    }

    private static ProviderStatus Plan(string type, string name, string level, int used, int weekly)
    {
        var p = new ProviderStatus { Type = type, Region = "cn", Name = name, ShowName = false, Level = level };
        p.Primary5h = new QuotaPool { Label = type == "mimo" ? "本月额度" : "5 小时窗口额度", Percent = used,
            Total = 10000, Used = used * 100, Remaining = 10000 - used * 100, ResetLocal = DateTime.Now.AddHours(3).AddMinutes(24) };
        p.Pools.Add(p.Primary5h);
        if (type != "mimo") p.Pools.Add(new QuotaPool { Label = "本月额度", Percent = weekly,
            Total = 50000, Used = weekly * 500, Remaining = 50000 - weekly * 500, ResetLocal = DateTime.Now.AddDays(12).AddHours(6) });
        return p;
    }

    private static WidgetContext Context(bool dark, bool all)
    {
        var ctx = (WidgetContext)FormatterServices.GetUninitializedObject(typeof(WidgetContext));
        Set(ctx, "_exiting", true); // prevents form-close persistence and any scheduled refresh
        ctx.Cfg = new Config { Theme = dark ? "dark" : "light", X = 100, Y = 100,
            TopMost = false, LockPosition = true, OpacityPct = 100, RefreshSec = 60 };
        ctx.Providers = new List<ProviderStatus> {
            Plan("qwen", "千问", "PRO", 26, 42),
            Plan("bailian", "百炼", "STANDARD", 38, 17),
            Plan("mimo", "小米 MiMo", "Token Plan", 19, 0)
        };
        if (all)
        {
            ctx.Providers.Add(Plan("codex", "Codex", "PLUS", 31, 22));
            ctx.Providers.Add(Plan("claude", "Claude", "PRO", 16, 33));
            ctx.Providers.Add(Plan("glm", "GLM", "PRO", 21, 40));
            ctx.Providers.Add(Plan("mx", "MiniMax", "Starter", 12, 19));
            ctx.Providers.Add(Plan("kimi", "Kimi", "Allegretto", 28, 11));
            var money = new QuotaPool { Label = "总余额", IsMoney = true, Currency = "¥", Remaining = 128.60 };
            ctx.Providers.Add(new ProviderStatus { Type = "ds", Region = "cn", Name = "DeepSeek", ShowName = false,
                Level = "可用", Primary5h = money, Pools = new List<QuotaPool> { money } });
        }
        foreach (var status in ctx.Providers)
            ctx.Cfg.Providers.Add(new ProviderConfig { Type = status.Type, Region = "cn", Name = status.Name,
                Key = "demo-not-a-real-credential", ShowName = false,
                LastResults = new List<ProviderStatus> { status }, LastFetchUtc = DateTime.UtcNow,
                NextRefreshUtc = DateTime.MaxValue });
        ctx.Tray = new NotifyIcon();
        St.Theme = dark ? 1 : 2;
        return ctx;
    }

    private static Bitmap RenderLayered(Form form)
    {
        var bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Call(form, "OnLayeredPaint", g);
        }
        return bitmap;
    }

    private static Bitmap Canvas(int width, int height, bool dark)
    {
        var image = new Bitmap(width, height);
        using (var g = Graphics.FromImage(image)) g.Clear(dark ? Color.FromArgb(25, 29, 33) : Color.FromArgb(233, 238, 243));
        return image;
    }

    private static void Footer(Graphics g, int height, bool dark)
    {
        using (var font = new Font("Microsoft YaHei UI", 14f, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(dark ? Color.FromArgb(145, 156, 166) : Color.FromArgb(104, 117, 130)))
            g.DrawString("QuotaWidget  /  " + (dark ? "深色主题" : "浅色主题") + "  /  演示数据", font, brush, 30, height - 33);
    }

    private static void Widget(bool dark, bool vertical)
    {
        var ctx = Context(dark, false);
        if (!vertical)
        {
            var glm = Plan("glm", "GLM", "PRO", 18, 34);
            var kimi = Plan("kimi", "Kimi", "Allegretto", 27, 16);
            glm.Pools[1].Label = "周额度";
            kimi.Pools[0].Label = "频率窗口额度";
            kimi.Pools[1].Label = "周额度";
            glm.Pools[1].ResetLocal = kimi.Pools[1].ResetLocal = DateTime.Now.AddDays(4).AddHours(6);
            var balance = new QuotaPool { Label = "总余额", IsMoney = true, Currency = "¥", Remaining = 128.60 };
            var deepseek = new ProviderStatus { Type = "ds", Region = "cn", Name = "DeepSeek", ShowName = false,
                Level = "可用", Primary5h = balance, Pools = new List<QuotaPool> {
                    balance,
                    new QuotaPool { Label = "充值余额", IsMoney = true, Currency = "¥", Remaining = 100 },
                    new QuotaPool { Label = "赠送余额", IsMoney = true, Currency = "¥", Remaining = 28.60 }
                }
            };
            ctx.Providers = new List<ProviderStatus> { glm, kimi, deepseek };
        }
        using (ctx.Tray)
        using (var floating = new FloatingForm(ctx))
        {
            StopTimers(floating);
            ctx.Floating = floating;
            Set(floating, "_scale", 1.75f);
            Set(floating, "_vertical", vertical);
            Set(floating, "_edgeDocked", false);
            // Rebuild also relocates the window and adopts monitor DPI. Keep the
            // export scale fixed while calling the same sizing/painting methods.
            floating.Width = vertical ? (int)Math.Round(54 * 1.75) : (int)Call(floating, "IslandWidth");
            floating.Height = (int)Math.Round((vertical ? 16 + ctx.Providers.Count * 70 : 54) * 1.75);
            var detail = floating._detail;
            detail.DpiScale = 1.75f;
            detail.UpdateData();
            using (var island = RenderLayered(floating))
            using (var panel = RenderLayered(detail))
            {
                int width = vertical ? island.Width + panel.Width + 92 : Math.Max(island.Width, panel.Width) + 80;
                int height = vertical ? Math.Max(island.Height, panel.Height) + 100 : island.Height + panel.Height + 116;
                using (var image = Canvas(width, height, dark))
                using (var g = Graphics.FromImage(image))
                {
                    if (vertical)
                    {
                        g.DrawImageUnscaled(island, 30, 38);
                        g.DrawImageUnscaled(panel, island.Width + 48, 38);
                    }
                    else
                    {
                        g.DrawImageUnscaled(island, (width - island.Width) / 2, 32);
                        g.DrawImageUnscaled(panel, (width - panel.Width) / 2, island.Height + 48);
                    }
                    Footer(g, height, dark);
                    Save(image, (vertical ? "sidebar" : "widget") + (dark ? "-dark.png" : "-light.png"));
                }
            }
        }
    }

    private static void Save(Bitmap image, string name)
    {
        image.Save(Path.Combine(_output, name), ImageFormat.Png);
        Console.WriteLine(name + " " + image.Width + "x" + image.Height);
    }

    private static void Capture(Form form, string name, bool dark)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(100, 100);
        form.Show();
        Application.DoEvents();
        if (form is SettingsForm)
        {
            Set(form, "_scale", 1.25f);
            Call(form, "ApplyScaleToSize");
            Call(form, "LayoutAll");
        }
        using (var content = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            if (form is SettingsForm)
            {
                using (var g = Graphics.FromImage(content))
                    Call(form, "OnPaint", new PaintEventArgs(g, new Rectangle(Point.Empty, content.Size)));
            }
            else form.DrawToBitmap(content, new Rectangle(Point.Empty, content.Size));
            using (var image = Canvas(content.Width + 64, content.Height + 92, dark))
            using (var g = Graphics.FromImage(image))
            {
                g.DrawImageUnscaled(content, 32, 24);
                Footer(g, image.Height, dark);
                Save(image, name);
            }
        }
        form.Hide();
    }

    private static void Settings(bool dark)
    {
        var ctx = Context(dark, true);
        ctx.Cfg.Providers.RemoveRange(5, 3);
        foreach (var p in ctx.Cfg.Providers) p.ShowName = true;
        using (ctx.Tray)
        using (var settings = new SettingsForm(ctx))
        {
            StopTimers(settings);
            Set(settings, "_scale", 1.5f);
            Call(settings, "ApplyScaleToSize");
            Call(settings, "LayoutAll");
            Capture(settings, "settings" + (dark ? "-dark.png" : "-light.png"), dark);
        }
    }

    private static void Custom(bool dark)
    {
        var provider = new ProviderConfig { Type = "custom", Name = "我的额度接口", ShowName = false,
            Key = "Authorization: Bearer DEMO_TOKEN", Custom = new CustomApiConfig {
                Url = "https://api.example.com/quota", IconType = "bailian", Rules = new List<CustomQuotaRule> {
                    new CustomQuotaRule { Name = "5 小时额度", ValuePath = "result.windows[0].remaining_percent" },
                    new CustomQuotaRule { Name = "周额度", ValuePath = "result.windows[1].remaining_percent" }
                }
            }
        };
        using (var dialog = new CustomApiDialog(provider, SettingsPal.For(dark), 1f))
        {
            object sample = CustomApiService.ParseJson("{\"windows\":[{\"remaining_percent\":74},{\"remaining_percent\":83}],\"balance\":128.6}");
            Set(dialog, "_rawRoot", sample); Set(dialog, "_previewRoot", sample); Set(dialog, "_hasSample", true);
            ((ApiJsonTree)Get(dialog, "_tree")).SetJson(sample);
            ((Label)Get(dialog, "_emptyTree")).Visible = false;
            ((Label)Get(dialog, "_responseStatus")).Text = "已获取响应 · 拖动节点填写路径";
            Call(dialog, "UpdatePreview");
            Call(dialog, "SelectTab", 1);
            Capture(dialog, "custom-api" + (dark ? "-dark.png" : "-light.png"), dark);
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass the documentation image output directory.");
        _output = Path.GetFullPath(args[0]); Directory.CreateDirectory(_output);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        St.Theme = 2;
        using (var icon = new Bitmap(192, 192))
        {
            using (var g = Graphics.FromImage(icon)) IconFactory.DrawGlyph(g, new Rectangle(0, 0, 192, 192));
            Save(icon, "app-icon.png");
        }
        foreach (bool dark in new[] { false, true })
        {
            Widget(dark, false); Widget(dark, true); Settings(dark); Custom(dark);
        }
    }
}
