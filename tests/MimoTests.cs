using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using QuotaWidget;

// 小米 MiMo：余额 / Token Plan 解析、查询内容切换、Cookie 归一化与编辑框回填。
// 全部使用固定 JSON 与构造出来的配置，不发起网络请求、不读取真实 Cookie。
internal static class MimoTests
{
    private const string BalanceCnyJson = "{\"code\":0,\"message\":\"\",\"data\":{" +
        "\"balance\":\"12.34\",\"currency\":\"CNY\",\"cashBalance\":\"10.00\",\"giftBalance\":\"2.34\",\"creditBalance\":null}}";

    private const string BalanceUsdJson = "{\"code\":0,\"message\":\"OK\",\"data\":{\"balance\":\"0\",\"currency\":\"USD\"}}";

    private const string PlanJson = "{\"code\":0,\"message\":\"\",\"data\":{" +
        "\"usage\":{\"items\":[{\"name\":\"plan_total_token\",\"used\":1025000000,\"limit\":4100000000,\"percent\":25}]}," +
        "\"monthUsage\":{\"percent\":25,\"items\":[" +
        "{\"name\":\"month_total_token\",\"used\":1025000000,\"limit\":4100000000,\"percent\":25}," +
        "{\"name\":\"compensation_total_token\",\"used\":0,\"limit\":500000000,\"percent\":0}]}}}";

    private const string PlanDetailJson = "{\"code\":0,\"data\":{\"planCode\":\"lite\",\"currentPeriodEnd\":\"2026-12-01 00:00:00\",\"expired\":false}}";

    private const string UsageOnlyJson = "{\"code\":0,\"data\":{\"usage\":{\"items\":[" +
        "{\"name\":\"plan_total_token\",\"used\":100000000,\"limit\":500000000,\"percent\":20}]}}}";

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

    private static int Main()
    {
        try
        {
            Run(Check);
            Console.WriteLine("Completed " + _checks + " MiMo checks.");
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
        var plan = new ProviderConfig { Type = "mimo", Region = "cn", Name = "MiMo" };
        check(plan.EffectiveMimoMode == "plan", "MiMo 旧配置默认 Token Plan 套餐");
        check(new ProviderConfig { Type = "mimo", MimoMode = "balance" }.EffectiveMimoMode == "balance", "MiMo 可切换账号余额");
        check(new ProviderConfig { Type = "mimo", MimoMode = "bad" }.EffectiveMimoMode == "plan", "MiMo 未知查询内容回退套餐");

        // 查询内容对应两个不同接口
        check(QuotaService.MimoUrl(plan) == "https://platform.xiaomimimo.com/api/v1/tokenPlan/usage", "MiMo 套餐走 tokenPlan/usage");
        var balanceMode = plan.Clone(); balanceMode.MimoMode = "balance";
        check(QuotaService.MimoUrl(balanceMode) == "https://platform.xiaomimimo.com/api/v1/balance", "MiMo 余额走 balance");
        check(!balanceMode.HasSameQuery(plan), "MiMo 切换查询内容后不复用旧结果");
        check(balanceMode.HasSameQuery(balanceMode.Clone()), "MiMo 同配置可复用结果");

        // Cookie 归一化：允许整段粘贴 "Cookie: ..."，并校验两个必需项
        check(QuotaService.MimoCookie("Cookie: userId=1; api-platform_serviceToken=abc") == "userId=1; api-platform_serviceToken=abc", "MiMo 去掉 Cookie: 前缀");
        check(QuotaService.MimoCookie("  userId=1  ") == "userId=1", "MiMo Cookie 去空白");
        check(QuotaService.IsMimoCookie("userId=1; api-platform_serviceToken=abc"), "MiMo 识别完整 Cookie");
        check(!QuotaService.IsMimoCookie("userId=1"), "MiMo 缺 serviceToken 时判为无效");
        check(!QuotaService.IsMimoCookie(null), "MiMo 空 Cookie 判为无效");

        // 币种由接口给出，不按域名猜
        check(QuotaService.MimoCurrencySymbol("CNY") == "¥" && QuotaService.MimoCurrencySymbol("usd") == "$", "MiMo 币种符号跟随接口");
        check(QuotaService.MimoCurrencySymbol("") == "¥", "MiMo 缺币种默认人民币");
        check(QuotaService.MimoCurrencySymbol("JPY") == "JPY ", "MiMo 未知币种原样带出");

        // 余额解析
        List<QuotaPool> cny = QuotaService.ParseMimoBalance(BalanceCnyJson);
        check(cny.Count == 3, "MiMo 余额解析 3 行（余额 + 现金 + 赠送）");
        check(cny[0].Label == "账户余额" && cny[0].IsMoney && cny[0].Currency == "¥" &&
            Math.Abs(cny[0].Remaining - 12.34) < 1e-9, "MiMo 余额取 data.balance 字符串并标 ¥");
        check(cny[0].Percent == 0, "MiMo 有余额时环为满");
        check(cny[1].Label == "现金余额" && Math.Abs(cny[1].Remaining - 10.0) < 1e-9, "MiMo 现金余额明细");
        check(cny[2].Label == "赠送余额" && Math.Abs(cny[2].Remaining - 2.34) < 1e-9, "MiMo 赠送余额明细");

        List<QuotaPool> usd = QuotaService.ParseMimoBalance(BalanceUsdJson);
        check(usd.Count == 1 && usd[0].Currency == "$" && usd[0].Percent == 100, "MiMo 美元零余额只保留一行且显示空环");

        ProviderStatus walletStatus = QuotaService.MimoStatus(balanceMode, usd);
        check(walletStatus.Type == "mimo" && walletStatus.Primary5h == usd[0] && walletStatus.Error == null, "MiMo 余额状态主显余额");

        // Token Plan 解析：Credits 按亿折算，百分比用 used/limit
        List<QuotaPool> pools = QuotaService.ParseMimoPlan(PlanJson, PlanDetailJson);
        check(pools.Count == 2, "MiMo 套餐解析主额度 + 补偿额度");
        check(pools[0].Label == "本月额度（亿 Credits）", "MiMo 主额度按亿显示");
        check(Math.Abs(pools[0].Total - 41) < 1e-9 && Math.Abs(pools[0].Used - 10.25) < 1e-9 &&
            Math.Abs(pools[0].Remaining - 30.75) < 1e-9, "MiMo 额度数值折算到亿");
        check(pools[0].Percent == 25, "MiMo 百分比按 used/limit 计算");
        check(pools[1].Label == "补偿额度（亿 Credits）" && Math.Abs(pools[1].Total - 5) < 1e-9, "MiMo 补偿额度单独一行");

        DateTime expectedReset = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Local);
        check(pools[0].ResetLocal.HasValue && pools[0].ResetLocal.Value == expectedReset, "MiMo 重置时间按 UTC 解析 currentPeriodEnd");
        check(!QuotaService.MimoPeriodEnd(null).HasValue, "MiMo 无详情响应时不显示重置时间");
        check(QuotaService.MimoPeriodEnd("not-json").HasValue == false, "MiMo 详情非 JSON 时安全降级");

        ProviderStatus planStatus = QuotaService.MimoStatus(plan, pools);
        check(planStatus.Primary5h == pools[0] && planStatus.Pools.Count == 2, "MiMo 套餐状态主显本月额度");

        // 没有 monthUsage 时回退 usage.items
        List<QuotaPool> fallbackPools = QuotaService.ParseMimoPlan(UsageOnlyJson, null);
        check(fallbackPools.Count == 1 && fallbackPools[0].Label == "本期额度（亿 Credits）" &&
            Math.Abs(fallbackPools[0].Total - 5) < 1e-9, "MiMo 无 monthUsage 时回退 usage.items");

        // 错误分类
        check(MessageOf(delegate { QuotaService.ParseMimoPlan("{\"code\":401,\"loginUrl\":\"https://account.xiaomi.com\"}", null); })
            == QuotaService.MimoExpiredMessage, "MiMo code 401 提示重新获取");
        check(MessageOf(delegate { QuotaService.ParseMimoBalance("{\"code\":403}"); }) == QuotaService.MimoExpiredMessage,
            "MiMo HTTP 信封 code 403 提示重新获取");
        check(MessageOf(delegate { QuotaService.ParseMimoBalance("{\"code\":500,\"message\":\"服务异常\"}"); })
            == "接口返回失败: 服务异常", "MiMo 业务失败只保留接口原文");
        check(MessageOf(delegate { QuotaService.ParseMimoPlan("{\"code\":0,\"data\":{\"monthUsage\":{\"items\":[]}}}", null); })
            == "该账号未开通 Token Plan", "MiMo 空额度仅提示未开通 Token Plan");
        check(Throws(delegate { QuotaService.ParseMimoBalance("not-json"); }), "MiMo 非 JSON 响应抛出可读错误");

        // 24 小时有效期展示：只用于界面提示，不参与请求判断
        check(QuotaService.MimoLoginText(null) == "", "MiMo 无登录时间时不显示登录时间");
        check(QuotaService.MimoLoginText("not-a-date") == "", "MiMo 非法登录时间安全忽略");
        check(QuotaService.MimoLoginText(DateTime.UtcNow.ToString("o")).StartsWith("登录于 "), "MiMo 显示登录时间而不是倒计时");
        check(QuotaService.MimoLoginText(DateTime.UtcNow.AddHours(-25).ToString("o")).StartsWith("登录于 "), "MiMo 超过 24 小时也不作废，只显示登录时间");
        var withLogin = new ProviderConfig { Type = "mimo", MimoMode = "plan", MimoLoginAt = "2026-11-03T08:00:00.0000000Z" };
        var loginSer = new JavaScriptSerializer();
        check(loginSer.Deserialize<ProviderConfig>(loginSer.Serialize(withLogin)).MimoLoginAt == withLogin.MimoLoginAt, "MiMo 登录时间可持久化");
        check(withLogin.Clone().MimoLoginAt == withLogin.MimoLoginAt, "MiMo 登录时间随配置复制");
        // 接口类型映射：MiMo 在下拉第 9 项，自定义接口名不再错位
        check(ProviderConfig.TypeIndex("mimo", "cn") == 8, "MiMo 对应下拉索引 8");
        check(ProviderConfig.TypeDisplay("mimo", "cn") == "小米 MiMo", "MiMo 展示名");
        check(ProviderConfig.TypeDefaultNames[8] == "MiMo" && ProviderConfig.TypeIndex("mimo", "cn") == 8,
            "默认名列表与下拉顺序一致");

        var ser = new JavaScriptSerializer();
        var roundTrip = ser.Deserialize<ProviderConfig>(ser.Serialize(new ProviderConfig { Type = "mimo", MimoMode = "balance" }));
        check(roundTrip.EffectiveMimoMode == "balance" && roundTrip.Clone().EffectiveMimoMode == "balance", "MiMo 查询内容持久化并随配置复制");

        // 编辑对话框：MiMo 显示「自动获取」按钮（在 Cookie 输入框左边）与两档查询内容，保存时归一化 Cookie
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "mimo", Name = "MiMo", Key = "userId=1; api-platform_serviceToken=abc" }, SettingsPal.For(false), 1f))
        {
            var autoRect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcAuto", Private).GetValue(dialog);
            var boxRect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcKeyBox", Private).GetValue(dialog);
            check(autoRect != Rectangle.Empty, "MiMo 编辑框显示自动获取按钮");
            check(autoRect.Right <= boxRect.Left && autoRect.Y == boxRect.Y, "自动获取按钮在 Cookie 输入框左边");
            check((int)typeof(ProviderEditDialog).GetField("_mimoMode", Private).GetValue(dialog) == 0, "MiMo 默认回填 Token Plan");
            var labels = (string[])typeof(ProviderEditDialog).GetField("MimoModeLabels", Private | BindingFlags.Static).GetValue(null);
            check(labels.Length == 2 && labels[0] == "Token Plan 套餐", "MiMo 查询内容只有套餐 / 余额两项");
            typeof(ProviderEditDialog).GetMethod("OnOk", Private).Invoke(dialog, null);
            check(dialog.Result != null && dialog.Result.Type == "mimo" && dialog.Result.MimoMode == "plan" &&
                dialog.Result.Name == "MiMo", "MiMo 编辑框保存套餐模式");
        }
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "mimo", Name = "MiMo", Key = "Cookie: userId=1; api-platform_serviceToken=abc" }, SettingsPal.For(false), 1f))
        {
            typeof(ProviderEditDialog).GetField("_mimoMode", Private).SetValue(dialog, 1);
            typeof(ProviderEditDialog).GetMethod("OnOk", Private).Invoke(dialog, null);
            check(dialog.Result != null && dialog.Result.MimoMode == "balance", "MiMo 编辑框保存余额模式");
            check(dialog.Result.GetKey() == "userId=1; api-platform_serviceToken=abc", "MiMo 保存时去掉 Cookie: 前缀");
        }
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "ds", Name = "DeepSeek", Key = "example-key" }, SettingsPal.For(false), 1f))
        {
            var autoRect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcAuto", Private).GetValue(dialog);
            check(autoRect == Rectangle.Empty, "非 MiMo 不显示自动获取按钮");
            typeof(ProviderEditDialog).GetMethod("OnOk", Private).Invoke(dialog, null);
            check(dialog.Result != null && dialog.Result.MimoMode == null, "非 MiMo 不写入查询内容");
        }
    }
}
