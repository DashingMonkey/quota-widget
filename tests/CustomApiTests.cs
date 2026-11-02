using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using QuotaWidget;
using WidgetContext = QuotaWidget.AppContext;

// 本地 TCP 假接口验证 GET / 请求头 / 限速，不使用真实凭证或远端服务。
internal static class CustomApiTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static object Get(object target, string name) { return target.GetType().GetField(name, Fields).GetValue(target); }
    private static void Call(object target, string name, params object[] args) { target.GetType().GetMethod(name, Fields).Invoke(target, args); }
    private static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
    private static void Pump(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void Render(Form form, string filename)
    {
        if (!form.Visible) form.Show();
        Pump(60);
        using (var image = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename)); }
    }
    private static ProviderConfig Provider(string url)
    {
        var p = new ProviderConfig { Type = "custom", Name = "额度示例", Custom = new CustomApiConfig { Url = url } };
        p.SetKey("x-management-key: example-secret-123\nX-Other: second-value"); return p;
    }
    private static readonly string RelayJson = @"{""count"":3,""next_allowed_in"":120,""accounts"":[
        {""name"":""工作账号"",""plan"":""Plus"",""disabled"":false,""windows"":[
            {""label"":""5 小时"",""remaining_percent"":49,""resets_at"":""2026-11-02T12:00:00Z""},
            {""label"":""周额度"",""remaining_percent"":0,""resets_at"":1793620800}]},
        {""name"":""备用账号"",""windows"":[{""label"":""5 小时"",""remaining_percent"":88.5,""resets_at"":1793620800000}]},
        {""name"":""停用账号"",""disabled"":true,""windows"":[]}]}";

    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private volatile bool _stop;
        public int Requests;
        public string LastRequest;
        public readonly string Url;
        private readonly Func<string> _response;
        public Server(Func<string> response)
        {
            _response = response;
            _listener = new TcpListener(IPAddress.Loopback, 0); _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/quota";
            _thread = new Thread(Run); _thread.IsBackground = true; _thread.Start();
        }
        public static string Response(string json, int status, string headers)
        {
            return "HTTP/1.1 " + status + " Test\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(json)
                + "\r\nConnection: close\r\n" + headers + "\r\n" + json;
        }
        private void Run()
        {
            while (!_stop)
            {
                try
                {
                    using (var client = _listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    {
                        stream.ReadTimeout = 5000;
                        var reader = new StreamReader(stream, Encoding.ASCII);
                        var request = new StringBuilder(); string line;
                        while (!string.IsNullOrEmpty(line = reader.ReadLine())) request.AppendLine(line);
                        LastRequest = request.ToString(); Interlocked.Increment(ref Requests);
                        byte[] data = Encoding.UTF8.GetBytes(_response()); stream.Write(data, 0, data.Length);
                    }
                }
                catch (SocketException) { if (!_stop) throw; }
                catch (ObjectDisposedException) { if (!_stop) throw; }
            }
        }
        public void Dispose() { _stop = true; _listener.Stop(); _thread.Join(1000); }
    }

    public static void Run(Action<bool, string> check, WidgetContext ctx, FloatingForm form)
    {
        var p = Provider("http://localhost/quota");
        var ser = new JavaScriptSerializer();
        var data = ser.DeserializeObject(@"{""data"":{""a-b"":[{""value"":0.25}]}} ");
        check(JsonFieldPath.Number(JsonFieldPath.Get(data, "result.data['a-b'][0].value"), "value") == 0.25, "field paths support nested objects, quoted keys and array indices");
        check(JsonFieldPath.Get(data, "result.data.missing") == null, "missing path does not turn into zero");
        check(Throws(delegate { JsonFieldPath.Get(data, "result.value * 100"); }), "arbitrary JavaScript is rejected as a field path");
        check(Throws(delegate { JsonFieldPath.Number(true, "value"); }) && Throws(delegate { JsonFieldPath.Number("NaN", "value"); }), "boolean and NaN are rejected as numbers");
        p.Custom.ValuePath = "result.data['a-b'][0].value"; p.Custom.Fraction = true;
        var parsed = CustomApiService.Parse(p, ser.Serialize(data));
        check(parsed[0].Primary5h.Percent == 25 && parsed[0].Primary5h.Remaining == 75, "fractional used percentage normalizes to remaining percent");
        p.Custom.IsRemaining = true;
        check(CustomApiService.Parse(p, ser.Serialize(data))[0].Primary5h.Percent == 75, "fractional remaining percentage is inverted exactly once");
        p.Custom.ValuePath = "result.value";
        check(CustomApiService.Parse(p, "{}")[0].Error != null && CustomApiService.Parse(p, "{\"value\":null}")[0].Primary5h == null, "missing and null quota produce an explicit error");
        check(CustomApiService.Parse(p, "{\"value\":2}")[0].Error != null, "out-of-range percentage is rejected");
        p.Custom.Mode = "balance"; p.Custom.Multiplier = 0.01; p.Custom.Currency = "¥";
        var money = CustomApiService.Parse(p, "{\"value\":1234}")[0].Primary5h;
        check(money.IsMoney && Math.Abs(money.Remaining - 12.34) < 0.000001 && money.Currency == "¥", "balance supports currency and unit conversion");
        p.Custom.Mode = "usedTotal"; p.Custom.TotalPath = "total";
        var pool = CustomApiService.Parse(p, "{\"value\":20,\"total\":80}")[0].Primary5h;
        check(pool.Percent == 25 && pool.Remaining == 60 && !pool.PercentOnly, "used and total mode preserves absolute quantities");
        check(CustomApiService.Parse(p, "{\"value\":1,\"total\":0}")[0].Error != null, "zero total does not divide or report a false zero");

        p.Custom = CustomApiConfig.CodexRelay(); p.Custom.Url = "http://localhost/quota"; p.RefreshSec = 120;
        var relay = CustomApiService.Parse(p, RelayJson);
        check(relay.Count == 2 && relay[0].Name == "工作账号" && relay[1].Name == "备用账号", "relay exposes enabled accounts separately in response order");
        check(relay[0].Pools.Count == 2 && relay[0].Pools[0].Percent == 51 && relay[0].Primary5h.Label == "周额度", "all quota windows are retained and an exhausted window is promoted");
        check(relay[0].Pools[0].ResetLocal.HasValue && relay[0].Pools[1].ResetLocal == relay[1].Pools[0].ResetLocal, "ISO, epoch seconds and epoch milliseconds reset times are supported");
        check(relay[0].IconType == "codex" && relay[0].Type == "custom" && !relay[0].ShowName, "custom icon choice is independent from provider type and display mode");
        string saved = ser.Serialize(p);
        check(!saved.Contains("example-secret") && !saved.Contains("x-management-key") && !saved.Contains("LastResults"), "request headers are encrypted and runtime cache is not serialized");
        var restored = ser.Deserialize<ProviderConfig>(saved);
        check(restored.GetKey() == p.GetKey() && restored.RefreshSec == 120 && restored.Custom.ValuePath == "remaining_percent", "encrypted headers, interval and mapping survive a config round trip");
        var clone = p.Clone(); clone.Custom.ValuePath = "changed";
        check(p.Custom.ValuePath == "remaining_percent", "editing a custom provider uses an independent mapping copy");
        restored.Enabled = false;
        check(!restored.CanQuery && CustomApiService.Fetch(restored).Statuses.Count == 0, "disabled custom provider never requests or decrypts for a query");
        var publicApi = new ProviderConfig { Type = "custom", Custom = new CustomApiConfig { Url = "http://localhost/quota" } };
        check(publicApi.CanQuery && new Config { Providers = new List<ProviderConfig> { publicApi } }.IsConfigured, "public GET API can be queried without credentials");
        check(Throws(delegate { CustomApiService.ParseHeaders("x-key: one\nX-Key: two"); }) && Throws(delegate { CustomApiService.ParseHeaders("Host: attacker"); }), "ambiguous or transport-controlled headers are rejected");

        using (var server = new Server(delegate { return Server.Response(RelayJson, 200, ""); }))
        {
            p.Custom.Url = server.Url;
            var first = CustomApiService.Fetch(p);
            var reordered = p.Clone(); reordered.SetKey("X-Other: second-value\r\nX-Management-Key: example-secret-123");
            var second = CustomApiService.Fetch(reordered);
            check(server.Requests == 1 && server.LastRequest.StartsWith("GET /quota HTTP/") && server.LastRequest.Contains("x-management-key: example-secret-123"), "custom transport sends GET and configured authentication headers");
            check(second.Cached && second.WaitSeconds > 0 && server.Requests == 1, "server next_allowed_in prevents duplicate polling and preview requests");
            check(first.Statuses[0].UpdatedAt == second.Statuses[0].UpdatedAt, "cached quota retains its original fetch time");
            QuotaService.FetchProvider(p, false);
            check(p.NextRefreshUtc > DateTime.UtcNow.AddSeconds(110), "provider scheduler incorporates server cooldown before its next poll");
            var badMapping = p.Clone(); badMapping.Custom.AccountsPath = "missing";
            var bad = CustomApiService.Fetch(badMapping);
            check(bad.Statuses[0].Error != null && bad.PreviewJson.Contains("accounts"), "mapping failures still provide JSON for fixing field paths");
        }
        using (var server = new Server(delegate { return Server.Response("{\"retry_after\":125}", 429, "Retry-After: 120\r\n"); }))
        {
            var limited = Provider(server.Url);
            check(Throws(delegate { CustomApiService.Fetch(limited); }), "HTTP 429 reports a rate limit");
            check(Throws(delegate { CustomApiService.Fetch(limited); }) && server.Requests == 1, "HTTP 429 cooldown is enforced without successful cached data");
        }
        using (var destination = new Server(delegate { return Server.Response("{}", 200, ""); }))
        using (var redirect = new Server(delegate { return Server.Response("", 302, "Location: " + destination.Url + "\r\n"); }))
        {
            check(Throws(delegate { CustomApiService.Fetch(Provider(redirect.Url)); }) && destination.Requests == 0, "redirects cannot forward custom credentials to a different endpoint");
        }
        int attempts = 0;
        using (var server = new Server(delegate { return ++attempts == 1 ? Server.Response("{\"value\":10}", 200, "") : Server.Response("{}", 503, "Retry-After: 120\r\n"); }))
        {
            var unavailable = Provider(server.Url); CustomApiService.Fetch(unavailable);
            check(Throws(delegate { CustomApiService.Fetch(unavailable); }) && Throws(delegate { CustomApiService.Fetch(unavailable); }) && server.Requests == 2,
                "server unavailable cooldown retains the failure instead of presenting an old success");
        }
        using (var server = new Server(delegate { return Server.Response("{\"value\":10,\"access_token\":\"hidden\",\"echo\":\"example-secret-123\"}", 200, ""); }))
        {
            var preview = CustomApiService.Fetch(Provider(server.Url)).PreviewJson;
            check(!preview.Contains("example-secret-123") && !preview.Contains("\"hidden\""), "preview redacts tokens and echoed header values");
        }

        check(RefreshChoices.Values(77, true)[0] == 0 && RefreshChoices.Values(77, true).Contains(77), "refresh dropdown offers inheritance and preserves existing custom intervals");
        p.RefreshSec = 0; check(p.EffectiveRefreshSec(60) == 60, "provider follows the global refresh interval by default");
        p.RefreshSec = 120; check(p.EffectiveRefreshSec(60) == 120, "provider-specific interval overrides the global interval");
        p.LastResults = relay; p.NextRefreshUtc = DateTime.UtcNow.AddSeconds(10);
        check(!p.IsRefreshDue(DateTime.UtcNow) && p.IsRefreshDue(DateTime.UtcNow.AddSeconds(11)), "provider becomes due only after its own interval");
        check(p.Clone().LastResults == null, "editing settings invalidates runtime scheduling state");

        using (var fastServer = new Server(delegate { return Server.Response("{\"value\":20}", 200, ""); }))
        using (var slowServer = new Server(delegate { return Server.Response("{\"value\":40}", 200, ""); }))
        {
            var fast = Provider(fastServer.Url); fast.RefreshSec = 15;
            var slow = Provider(slowServer.Url); slow.RefreshSec = 120;
            ctx.Cfg.Providers = new List<ProviderConfig> { fast, slow };
            ctx.RefreshDue(false);
            for (int i = 0; i < 200 && (int)Get(ctx, "_fetching") != 0; i++) Pump(10);
            check(fastServer.Requests == 1 && slowServer.Requests == 1 && ctx.Providers.Count == 2, "first refresh queries each enabled provider");
            fast.NextRefreshUtc = DateTime.UtcNow.AddSeconds(-1);
            ctx.RefreshDue(false);
            for (int i = 0; i < 200 && (int)Get(ctx, "_fetching") != 0; i++) Pump(10);
            check(fastServer.Requests == 2 && slowServer.Requests == 1 && ctx.Providers[1].Primary5h.Percent == 40, "only the due provider refreshes while other quota remains displayed");
            ctx.RefreshDue(false); Pump(30);
            check(fastServer.Requests == 2 && slowServer.Requests == 1 && (int)Get(ctx, "_fetching") == 0, "timer tick does no background work when nothing is due");
        }
        ctx.Cfg.Providers = new List<ProviderConfig>();

        p = Provider("http://localhost:8787/v1/quota"); p.Custom = CustomApiConfig.CodexRelay(); p.Custom.Url = "http://localhost:8787/v1/quota"; p.RefreshSec = 77;
        foreach (bool dark in new[] { false, true })
        {
            using (var dialog = new CustomApiDialog(p, SettingsPal.For(dark), 1f))
            {
                var draft = dialog.ReadDraft();
                var headers = CustomApiService.ParseHeaders(draft.GetKey());
                check(!draft.ShowName && draft.IconType == "codex" && draft.RefreshSec == 77 && headers["x-management-key"] == "example-secret-123" && headers["x-other"] == "second-value", "custom editor preserves icon, refresh and headers in " + (dark ? "dark" : "light") + " theme");
                var display = (SettingsChoice)Get(dialog, "_display"); display.SelectedIndex = 1;
                check(dialog.ReadDraft().ShowName, "custom provider can switch to name display"); display.SelectedIndex = 0;
                var interval = (SettingsChoice)Get(dialog, "_interval"); interval.SelectedIndex = 0;
                check(dialog.ReadDraft().RefreshSec == 0, "custom refresh dropdown supports following global");
                for (int tab = 0; tab < 3; tab++) { dialog.SelectTab(tab); Render(dialog, "custom-" + (dark ? "dark" : "light") + "-tab" + tab + ".png"); }
            }
        }
        using (var server = new Server(delegate { return Server.Response(RelayJson, 200, ""); }))
        using (var dialog = new CustomApiDialog(p, SettingsPal.For(false), 1f))
        {
            var fields = (Dictionary<string, TextBox>)Get(dialog, "_fields"); fields["url"].Text = server.Url;
            dialog.Show(); Call(dialog, "Test");
            for (int i = 0; i < 200 && (bool)Get(dialog, "_testing"); i++) Pump(10);
            check(((TextBox)Get(dialog, "_report")).Text.Contains("工作账号") && ((TextBox)Get(dialog, "_raw")).Text.Contains("accounts"), "test-preview UI displays account quotas and JSON from an authenticated GET");
            Render(dialog, "custom-preview-result.png");
        }
        check(BrandIcons.Get("custom", false) != null && BrandIcons.Get("custom", true) != null, "generic custom API icon renders in both themes");
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "codex", RefreshSec = 77 }, SettingsPal.For(false), 1f))
        {
            Call(dialog, "OnOk"); check(dialog.Result.RefreshSec == 77, "built-in provider editor preserves a custom refresh interval");
        }
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "codex", RefreshSec = 120 }, SettingsPal.For(false), 1f)) Render(dialog, "builtin-refresh.png");
        ctx.Cfg.Providers = new List<ProviderConfig> { p };
        using (var settings = new SettingsForm(ctx))
        {
            Rectangle add = (Rectangle)Get(settings, "_rcAdd"), refresh = (Rectangle)Get(settings, "_rcSelInt"), test = (Rectangle)Get(settings, "_rcTest");
            check(refresh.Y == add.Y && refresh.Left > test.Right, "global refresh dropdown is right-aligned on the provider toolbar");
            Render(settings, "providers-custom.png");
            Call(settings, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, refresh.X + 10, refresh.Y + 10, 0));
            check(Get(settings, "_popup") != null, "global refresh dropdown opens from the provider page");
        }
        ctx.Cfg.Providers = new List<ProviderConfig>();
    }
}
