using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using QuotaWidget;

// 千问 / 百炼 Token Plan：控制台网关信封、账期解析、Cookie 归一化与编辑框回填。
// 全部使用固定 JSON 与构造出来的配置，不发起网络请求、不读取真实 Cookie。
//
// 信封形态取自本机实测（2026-11-03，未登录）：
//   {"code":"200","data":{"success":false,"httpStatus":200,
//    "errorCode":"BailianGateway.Login.NotLogined","api":"…/usage","errorMsg":"…"},"successResponse":true}
// 成功形态按 dsh-token-plan-quota（MIT）公布的上游契约构造。
internal static class AliyunTests
{
    // 2026-09 起账期改成按月：usage 只回 per1Month*
    private const string UsageMonthJson = "{\"code\":\"200\",\"data\":{\"success\":true,\"httpStatus\":200," +
        "\"DataV2\":{\"data\":{\"per1MonthPercentage\":0.1186,\"per1MonthResetTime\":1798761600000}}}," +
        "\"successResponse\":true}";

    // 旧套餐仍是周度
    private const string UsageWeekJson = "{\"code\":\"200\",\"data\":{\"success\":true," +
        "\"data\":{\"per1WeekPercentage\":0.5,\"per1WeekResetTime\":1798761600000}},\"successResponse\":true}";

    // 上游偶尔直接给百分数而不是 0–1 小数
    private const string UsagePercentJson = "{\"code\":\"200\",\"data\":{\"success\":true," +
        "\"data\":{\"per1MonthPercentage\":11.86}},\"successResponse\":true}";

    private const string UsageFiveHourJson = "{\"code\":\"200\",\"data\":{\"success\":true," +
        "\"data\":{\"per5HourPercentage\":0.25}},\"successResponse\":true}";

    private const string UsageEmptyJson = "{\"code\":\"200\",\"data\":{\"success\":true,\"data\":{}},\"successResponse\":true}";

    private const string QuotaJson = "{\"code\":\"200\",\"data\":{\"success\":true,\"data\":{" +
        "\"standard\":{\"monthly\":25000,\"five_hour\":3000}," +
        "\"lite\":{\"monthly\":10000,\"five_hour\":1200}," +
        "\"addon_quota\":{\"extrabundle\":5000}}},\"successResponse\":true}";

    private const string QuotaWeekJson = "{\"code\":\"200\",\"data\":{\"success\":true,\"data\":{" +
        "\"standard\":{\"weekly\":10000}}},\"successResponse\":true}";

    // 只有一个档位的账号：订阅接口挂掉时也能无歧义地取到分母
    private const string QuotaSingleSpecJson = "{\"code\":\"200\",\"data\":{\"success\":true,\"data\":{" +
        "\"standard\":{\"monthly\":25000}}},\"successResponse\":true}";

    private const string SubscriptionJson = "{\"code\":\"200\",\"data\":{\"success\":true,\"data\":{" +
        "\"specCode\":\"standard\",\"remainingDays\":21,\"endTime\":\"2026-12-01 23:59:59\"}},\"successResponse\":true}";

    private const string NotLoginedJson = "{\"code\":\"200\",\"data\":{\"success\":false,\"httpStatus\":200," +
        "\"errorCode\":\"BailianGateway.Login.NotLogined\",\"api\":\"zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/usage\"," +
        "\"errorMsg\":\"BailianGateway.Login.NotLogined\"},\"successResponse\":true}";

    private const string BadRequestJson = "{\"code\":\"200\",\"data\":{\"success\":false,\"httpStatus\":200," +
        "\"errorCode\":\"Bad Request\",\"errorMsg\":\"Bad Request\"},\"successResponse\":true}";

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }

    private static string MessageOf(Action action)
    {
        try { action(); } catch (Exception ex) { return ex.Message; }
        return "";
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("FAILED: " + message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            Run(Check);
            Console.WriteLine("Completed " + _checks + " Aliyun checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static void Run(Action<bool, string> check)
    {
        // ---------- 类型映射 ----------
        check(ProviderConfig.TypeIndex("qwen", "cn") == 9, "千问对应下拉索引 9");
        check(ProviderConfig.TypeIndex("bailian", "cn") == 10, "百炼对应下拉索引 10");
        check(ProviderConfig.TypeIndex("custom", "cn") == 11, "自定义接口顺延到索引 11");
        check(ProviderConfig.TypeIndex("mimo", "cn") == 8, "MiMo 索引保持不变");
        check(ProviderConfig.TypeDisplay("qwen", "cn") == "千问 Token Plan", "千问展示名");
        check(ProviderConfig.TypeDisplay("bailian", "cn") == "百炼 Token Plan", "百炼展示名");
        check(ProviderConfig.TypeDefaultNames[9] == "千问" && ProviderConfig.TypeDefaultNames[10] == "百炼" &&
            ProviderConfig.TypeDefaultNames[11] == "自定义接口", "默认名列表与下拉顺序一致");

        // ---------- 站点常量：同一份实现、两套域名 ----------
        check(QuotaService.AliyunBrand("qwen") == "千问" && QuotaService.AliyunBrand("bailian") == "百炼", "品牌名跟随类型");
        check(QuotaService.AliyunGatewayUrl("qwen") == "https://cs-data.qianwenai.com/data/api.json", "千问走 cs-data 网关");
        check(QuotaService.AliyunGatewayUrl("bailian") == "https://bailian-cs.console.aliyun.com/data/api.json", "百炼走 bailian-cs 网关");
        check(QuotaService.AliyunConsoleSite("qwen") == "QIANWENAI" &&
            QuotaService.AliyunConsoleSite("bailian") == "BAILIAN_ALIYUN", "两家 consoleSite 不同");
        check(QuotaService.AliyunInfoUrl("qwen") == "https://platform-home.qianwenai.com/tool/user/info.json" &&
            QuotaService.AliyunInfoUrl("bailian") == "https://bailian.console.aliyun.com/tool/user/info.json",
            "sec_token 从各自控制台的 user/info.json 取");
        check(QuotaService.AliyunConsoleUrl("qwen").IndexOf("/analytics/token-plan/individual") > 0,
            "千问接口 Referer 保留后台子应用路径");
        check(ConsoleBinder.Qwen.LoginUrl == "https://platform.qianwenai.com/home/analytics/token-plan",
            "千问登录窗口走官网公开入口，不直接打开会空白的子应用");

        // ---------- Cookie 归一化与结构预检 ----------
        check(QuotaService.ConsoleCookie("Cookie: login_aliyunid=a; cna=b") == "login_aliyunid=a; cna=b", "去掉 Cookie: 前缀");
        check(QuotaService.ConsoleCookie("login_aliyunid=a;\r\ncna=b") == "login_aliyunid=a;cna=b", "去掉换行（粘贴常带）");
        check(QuotaService.ConsoleCookie(null) == "", "空 Cookie 归一化为空串");
        check(QuotaService.IsAliyunCookie("login_aliyunid_ticket=xyz; cna=1"), "识别阿里云登录 Cookie");
        check(QuotaService.IsAliyunCookie("aliyun_site=CN"), "aliyun_site 也算登录态线索");
        check(!QuotaService.IsAliyunCookie("cna=1; t=2"), "只有埋点 Cookie 判为无效");
        check(!QuotaService.IsAliyunCookie(null), "空 Cookie 判为无效");
        check(QuotaService.CookieValue("a=1; cna=abc; b=2", "cna") == "abc", "能从 Cookie 串里取 cna");
        check(QuotaService.CookieValue("a=1", "cna") == "", "取不到的 Cookie 返回空串");

        var qwen = new ProviderConfig { Type = "qwen", Region = "cn", Name = "千问", Key = "login_aliyunid=t" };
        var bailian = new ProviderConfig { Type = "bailian", Region = "cn", Name = "百炼", Key = "login_aliyunid=t" };
        check(qwen.HasSameQuery(qwen.Clone()), "千问同配置可复用结果");
        check(!qwen.HasSameQuery(bailian), "千问与百炼不能互相复用结果");
        var otherAccount = qwen.Clone(); otherAccount.SetKey("login_aliyunid=u");
        check(!qwen.HasSameQuery(otherAccount), "换账号（Cookie 变了）后不复用结果");

        // ---------- 账期解析：按月 ----------
        List<QuotaPool> month = QuotaService.ParseAliyunPlan(UsageMonthJson, QuotaJson, SubscriptionJson, "qwen");
        check(month.Count == 1, "月度套餐只产出一条计量");
        check(month[0].Label == "本月额度", "月度计量标签");
        check(Math.Abs(month[0].Total - 25000) < 1e-9, "档位额度从 quota-config[specCode].monthly 取");
        check(month[0].Percent == 12, "0–1 小数按已用百分比换算（0.1186 → 12%）");
        check(Math.Abs(month[0].Used - 25000 * 0.1186) < 1e-6 && Math.Abs(month[0].Remaining - 25000 * (1 - 0.1186)) < 1e-6,
            "剩余量 = 档位额度 × (1 − 官方比例)");
        check(month[0].Note == "standard", "档位代号挂在主计量上");
        check(!month[0].PercentOnly, "有分母时不按纯百分比展示");
        DateTime expectedReset = DateTimeOffset.FromUnixTimeMilliseconds(1798761600000).LocalDateTime;
        check(month[0].ResetLocal.HasValue && month[0].ResetLocal.Value == expectedReset, "重置时间按 epoch 毫秒解析");

        // 档位表里有多个档位、又没有 specCode 时绝不猜（猜错就是把别的档位额度当成你的）
        List<QuotaPool> noSpec = QuotaService.ParseAliyunPlan(UsageMonthJson, QuotaJson, null, "qwen");
        check(noSpec[0].PercentOnly && noSpec[0].Note == null, "多个档位且无 specCode 时不猜分母");
        // 只有一个档位时无歧义，可以直接取
        List<QuotaPool> oneSpec = QuotaService.ParseAliyunPlan(UsageMonthJson, QuotaSingleSpecJson, null, "qwen");
        check(Math.Abs(oneSpec[0].Total - 25000) < 1e-9 && oneSpec[0].Note == null, "只有一个档位时取其额度作分母");

        // ---------- 账期解析：旧套餐仍按周 ----------
        List<QuotaPool> week = QuotaService.ParseAliyunPlan(UsageWeekJson, QuotaWeekJson, null, "qwen");
        check(week.Count == 1 && week[0].Label == "7 天额度", "只有周度读数时产出 7 天计量");
        check(Math.Abs(week[0].Total - 10000) < 1e-9 && week[0].Percent == 50, "周度额度与比例");

        // ---------- 5 小时窗口 ----------
        List<QuotaPool> five = QuotaService.ParseAliyunPlan(UsageFiveHourJson, QuotaJson, SubscriptionJson, "qwen");
        check(five.Count == 1 && five[0].Label == "5 小时额度", "只有 5 小时读数时产出 5 小时计量");
        check(Math.Abs(five[0].Total - 3000) < 1e-9 && five[0].Percent == 25, "5 小时额度取 five_hour 字段");

        // ---------- 上游直接给百分数 ----------
        List<QuotaPool> asPercent = QuotaService.ParseAliyunPlan(UsagePercentJson, QuotaJson, SubscriptionJson, "qwen");
        check(asPercent[0].Percent == 12, "11.86 视为已经是百分数，不再乘 100");

        // ---------- 没有分母：只报官方比例，不派生绝对余量 ----------
        List<QuotaPool> noDenominator = QuotaService.ParseAliyunPlan(UsageMonthJson, null, null, "qwen");
        check(noDenominator[0].PercentOnly, "缺档位额度时标记为纯百分比");
        check(noDenominator[0].Total == 100 && noDenominator[0].Remaining == 88 && noDenominator[0].Used == 12,
            "纯百分比时用 100 作分母，数值不派生");
        check(noDenominator[0].Percent == 12, "纯百分比仍报官方已用比例");

        // ---------- 错误分类 ----------
        check(MessageOf(delegate { QuotaService.ParseAliyunPlan(NotLoginedJson, null, null, "qwen"); })
            == QuotaService.AliyunExpiredMessage("qwen"), "NotLogined 提示千问登录已过期");
        check(MessageOf(delegate { QuotaService.ParseAliyunPlan(NotLoginedJson, null, null, "bailian"); })
            == QuotaService.AliyunExpiredMessage("bailian"), "NotLogined 提示百炼登录已过期");
        check(MessageOf(delegate { QuotaService.ParseAliyunPlan(BadRequestJson, null, null, "qwen"); })
            == "控制台返回失败（Bad Request）", "网关其它错误保留 errorCode");
        check(MessageOf(delegate { QuotaService.ParseAliyunPlan(UsageEmptyJson, QuotaJson, SubscriptionJson, "bailian"); })
            == "该账号未开通 Token Plan", "没有 per1* 读数时仅提示未开通 Token Plan");
        check(Throws(delegate { QuotaService.ParseAliyunPlan("not-json", null, null, "qwen"); }), "非 JSON 响应抛出可读错误");
        check(Throws(delegate { QuotaService.ParseAliyunPlan("", null, null, "qwen"); }), "空响应抛出可读错误");

        // 订阅/档位接口失败不能拖垮主读数
        List<QuotaPool> degraded = QuotaService.ParseAliyunPlan(UsageMonthJson, BadRequestJson, NotLoginedJson, "qwen");
        check(degraded.Count == 1 && degraded[0].PercentOnly, "档位与订阅失败时仍报官方比例");

        // ---------- 状态装配 ----------
        ProviderStatus st = QuotaService.AliyunStatus(bailian, month);
        check(st.Type == "bailian" && st.Primary5h == month[0] && st.Error == null, "百炼状态主显本月额度");
        check(QuotaService.AliyunStatus(qwen, month).Type == "qwen", "千问状态带上自己的类型");

        // ---------- 下拉图标与配色 ----------
        check(BrandIcons.Get("qwen", false) != null && BrandIcons.Get("bailian", false) != null, "千问 / 百炼有可用图标");
        check(St.AccentFor("qwen") != St.AccentFor("bailian"), "千问与百炼主题色不同");

        // ---------- 编辑对话框 ----------
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "qwen", Name = "千问", Key = "login_aliyunid=t" }, SettingsPal.For(false), 1f))
        {
            var autoRect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcAuto", Private).GetValue(dialog);
            var boxRect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcKeyBox", Private).GetValue(dialog);
            check(autoRect != Rectangle.Empty, "千问编辑框显示自动获取按钮");
            check(autoRect.Right <= boxRect.Left && autoRect.Y == boxRect.Y, "自动获取按钮在 Cookie 输入框左边");
            var modeRect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcGlmMode", Private).GetValue(dialog);
            check(modeRect == Rectangle.Empty, "千问没有查询内容行（只有一个读数）");
            typeof(ProviderEditDialog).GetMethod("OnOk", Private).Invoke(dialog, null);
            check(dialog.Result != null && dialog.Result.Type == "qwen" && dialog.Result.GetKey() == "login_aliyunid=t",
                "千问编辑框保存 Cookie");
        }
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "bailian", Name = "百炼", Key = "Cookie: login_aliyunid=t; cna=c" }, SettingsPal.For(false), 1f))
        {
            typeof(ProviderEditDialog).GetMethod("OnOk", Private).Invoke(dialog, null);
            check(dialog.Result != null && dialog.Result.Type == "bailian" &&
                dialog.Result.GetKey() == "login_aliyunid=t; cna=c", "百炼保存时去掉 Cookie: 前缀");
        }
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "qwen", Name = "千问", Key = "login_aliyunid=t" }, SettingsPal.For(false), 1f))
        {
            // 自动获取成功后写的登录时间要能落库
            typeof(ProviderEditDialog).GetField("_mimoLoginAt", Private).SetValue(dialog, "2026-11-03T08:00:00.0000000Z");
            typeof(ProviderEditDialog).GetMethod("OnOk", Private).Invoke(dialog, null);
            check(dialog.Result.MimoLoginAt == "2026-11-03T08:00:00.0000000Z", "千问记录 Cookie 获取时间");
        }
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "ds", Name = "DeepSeek", Key = "example-key" }, SettingsPal.For(false), 1f))
        {
            var autoRect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcAuto", Private).GetValue(dialog);
            check(autoRect == Rectangle.Empty, "非控制台 Cookie 型不显示自动获取按钮");
        }

        // ---------- 登录时间展示 ----------
        check(QuotaService.MimoLoginText("2026-11-03T08:00:00.0000000Z").StartsWith("登录于 "), "控制台 Cookie 只显示登录时间");
        RunCancellationChecks(check);
    }

    private static void RunCancellationChecks(Action<bool, string> check)
    {
        var cancelled = new ConsoleBinder.Progress();
        cancelled.Cancel();
        ConsoleBinder.AutoFetch(cancelled, ConsoleBinder.Qwen, "login_aliyunid=fake");
        check(cancelled.Done && cancelled.Cookie == null, "预先取消的获取直接结束，不请求远端或打开浏览器");

        var dialogType = typeof(ProviderEditDialog);
        var stateField = dialogType.GetField("_mimoState", Private);
        var timerField = dialogType.GetField("_mimoTimer", Private);
        var select = dialogType.GetMethod("SelectType", Private);
        var pump = dialogType.GetMethod("PumpMimoFetch", Private);
        string[] from = { "qwen", "mimo", "qwen" };
        string[] to = { "bailian", "qwen", "ds" };
        for (int i = 0; i < from.Length; i++)
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = from[i], Name = from[i], Key = "old-cookie" }, SettingsPal.For(false), 1f))
        {
            var old = new ConsoleBinder.Progress();
            stateField.SetValue(dialog, old);
            var timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Start(); timerField.SetValue(dialog, timer);
            dialogType.GetField("_mimoLoginAt", Private).SetValue(dialog, "2026-11-03T08:00:00Z");
            dialogType.GetField("_mimoStatus", Private).SetValue(dialog, "获取中");
            select.Invoke(dialog, new object[] { ProviderConfig.TypeIndex(to[i], "cn") });
            var key = (TextBox)dialogType.GetField("_txtKey", Private).GetValue(dialog);
            check(old.Cancelled && stateField.GetValue(dialog) == null && !timer.Enabled,
                from[i] + " → " + to[i] + " 取消旧任务并停止状态计时器");
            check(key.Text == "" && dialogType.GetField("_mimoLoginAt", Private).GetValue(dialog) == null &&
                dialogType.GetField("_mimoStatus", Private).GetValue(dialog) == null,
                "跨品牌切换清空旧凭证、登录时间和获取状态");
            old.SetResult("late-old-cookie", null, "旧任务完成"); old.Finish();
            pump.Invoke(dialog, null);
            check(key.Text == "", "迟到的旧结果不能回填新供应商");
            if (i == 0)
            {
                var next = new ConsoleBinder.Progress(); stateField.SetValue(dialog, next); timer.Start();
                next.SetResult("new-cookie", null, "已登录"); next.Finish();
                pump.Invoke(dialog, null);
                check(key.Text == "new-cookie" && stateField.GetValue(dialog) == null,
                    "切换后新任务仍可正常完成并回填自己的 Cookie");
            }
        }
        var closing = new ProviderEditDialog(new ProviderConfig { Type = "qwen", Name = "千问", Key = "fake" }, SettingsPal.For(false), 1f);
        var closingState = new ConsoleBinder.Progress(); stateField.SetValue(closing, closingState);
        closing.Dispose();
        check(closingState.Cancelled, "销毁编辑框也取消后台任务");

        // 本地服务器故意不响应，验证取消真正中断请求，而不是等完 15 秒超时。
        foreach (string transport in new[] { "GET", "POST", "CDP" })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var pending = listener.AcceptTcpClientAsync();
            using (var cts = new CancellationTokenSource())
            {
                Exception result = null;
                var worker = new Thread(delegate()
                {
                    try
                    {
                        string url = "http://127.0.0.1:" + port + "/probe";
                        if (transport == "CDP")
                        {
                            var client = typeof(ConsoleBinder).GetNestedType("CdpClient", BindingFlags.NonPublic);
                            using ((IDisposable)Activator.CreateInstance(client, new object[] { "ws://127.0.0.1:" + port + "/devtools", 15000, cts.Token })) { }
                        }
                        else if (transport == "POST")
                            typeof(QuotaService).GetMethod("HttpPostForm", BindingFlags.Static | BindingFlags.NonPublic)
                                .Invoke(null, new object[] { url, "test", null, "action=test", 15000, cts.Token });
                        else
                            typeof(ConsoleBinder).GetMethod("HttpGetText", BindingFlags.Static | BindingFlags.NonPublic, null,
                                new[] { typeof(string), typeof(int), typeof(IDictionary<string, string>), typeof(CancellationToken) }, null)
                                .Invoke(null, new object[] { url, 15000, null, cts.Token });
                    }
                    catch (Exception ex) { result = ex; }
                });
                worker.IsBackground = true;
                try
                {
                    worker.Start();
                    check(pending.Wait(3000), transport + " 验证请求到达本地测试服务");
                    using (var peer = pending.Result)
                    {
                        var watch = Stopwatch.StartNew(); cts.Cancel();
                        check(worker.Join(2000) && result != null && watch.ElapsedMilliseconds < 2000,
                            transport + " 获取中取消立即中断阻塞请求");
                    }
                }
                finally { cts.Cancel(); listener.Stop(); worker.Join(2000); }
            }
        }
    }
}
