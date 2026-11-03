using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using QuotaWidget;

// GLM 钱包余额解析与查询内容/币种切换：纯 JSON / 对话框用例，不发起网络请求、不读取真实配置或凭证。
internal static class GlmWalletTests
{
    private const string QuotaJson = "{\"code\":200,\"success\":true,\"data\":{\"level\":\"max\",\"limits\":[" +
        "{\"type\":\"CREDIT_LIMIT\",\"unit\":3,\"number\":5,\"usage\":28000,\"currentValue\":5184,\"remaining\":22815,\"percentage\":18,\"nextResetTime\":1788371521831}," +
        "{\"type\":\"CREDIT_LIMIT\",\"unit\":6,\"number\":1,\"usage\":140000,\"currentValue\":36192,\"remaining\":103807,\"percentage\":25,\"nextResetTime\":1788875329996}]}}";

    private const string WalletJson = "{\"code\":200,\"msg\":\"操作成功\",\"data\":{\"balance\":0E-9," +
        "\"rechargeAmount\":0.0,\"giveAmount\":3.969950,\"totalSpendAmount\":3.969950," +
        "\"availableBalance\":12.34,\"frozenBalance\":0E-9},\"success\":true}";

    private const string NoPlanJson = "{\"code\":500,\"msg\":\"当前用户不存在coding plan\",\"success\":false}";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
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
            Console.WriteLine("Completed " + _checks + " GLM wallet checks.");
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
        var p = new ProviderConfig { Type = "glm", Region = "cn", Name = "GLM" };

        // 钱包解析：availableBalance 为余额，其余字段是累计值
        List<QuotaPool> wallet = QuotaService.ParseGlmWallet(WalletJson, "¥");
        check(wallet.Count == 3, "GLM 钱包解析 3 行（余额 + 赠送 + 消费）");
        check(wallet[0].Label == "钱包余额" && Math.Abs(wallet[0].Remaining - 12.34) < 1e-9, "GLM 钱包可用余额取 availableBalance");
        check(wallet[0].IsMoney && wallet[0].Currency == "¥", "GLM 人民币钱包按 ¥ 展示");
        check(wallet[0].Percent == 0, "GLM 有余额时钱包环为满");
        check(wallet[1].Label == "累计赠送" && Math.Abs(wallet[1].Remaining - 3.969950) < 1e-9, "GLM 累计赠送为总额而非余额");
        check(wallet[2].Label == "累计消费" && Math.Abs(wallet[2].Remaining - 3.969950) < 1e-9, "GLM 累计消费解析");
        check(QuotaService.ParseGlmWallet(WalletJson, "$")[0].Currency == "$", "GLM 美元钱包按 $ 展示");

        ProviderStatus st = QuotaService.GlmWalletStatus(p, wallet);
        check(st.Error == null && st.Primary5h == wallet[0] && st.Pools.Count == 3, "GLM 钱包状态主显钱包余额");
        check(st.Level == "", "GLM 钱包状态不显示套餐等级");

        ProviderStatus zero = QuotaService.GlmWalletStatus(p, QuotaService.ParseGlmWallet(
            "{\"success\":true,\"data\":{\"availableBalance\":0,\"rechargeAmount\":0,\"giveAmount\":0,\"totalSpendAmount\":0}}", "¥"));
        check(zero.Pools.Count == 1 && zero.Primary5h.Percent == 100, "GLM 零余额保留钱包行并显示空环");

        check(Throws(delegate { QuotaService.ParseGlmWallet("not-json", "¥"); }), "GLM 钱包非 JSON 响应抛出可读错误");
        check(Throws(delegate { QuotaService.ParseGlmWallet("{\"code\":1001,\"success\":false,\"msg\":\"auth\"}", "¥"); }), "GLM 钱包鉴权失败抛出错误");

        // 套餐额度解析；业务失败只回传接口原文，不追加钱包切换提示
        ProviderStatus quota = QuotaService.ParseGlmQuota(p, QuotaJson);
        check(quota.Pools.Count == 2 && quota.Primary5h.Label.Contains("小时") && quota.Level == "MAX", "GLM 套餐额度解析");
        bool plainError = false;
        try { QuotaService.ParseGlmQuota(p, NoPlanJson); }
        catch (Exception ex) { plainError = ex.Message == "接口返回失败: 当前用户不存在coding plan"; }
        check(plainError, "GLM 套餐业务失败只保留接口原文");

        // 查询内容与币种：默认套餐/人民币；钱包币种独立于接口域名，不随区域换算
        var defaults = new ProviderConfig { Type = "glm", Region = "cn", Name = "GLM" };
        check(defaults.EffectiveGlmMode == "quota" && defaults.EffectiveGlmCurrency == "CNY", "GLM 默认套餐且币种为人民币");
        var walletCny = defaults.Clone(); walletCny.GlmMode = "wallet"; walletCny.GlmCurrency = "CNY";
        check(walletCny.EffectiveGlmCurrency == "CNY" && !walletCny.HasSameQuery(defaults), "GLM 切到钱包后不复用套餐额度");
        var walletUsd = walletCny.Clone(); walletUsd.Region = "intl"; walletUsd.GlmCurrency = "USD";
        check(walletUsd.EffectiveGlmCurrency == "USD" && walletUsd.Clone().GlmCurrency == "USD", "GLM 可切换美元币种并随配置复制");
        check(!walletUsd.HasSameQuery(walletCny), "GLM 切换币种后不复用旧余额");
        var ser = new JavaScriptSerializer();
        check(ser.Deserialize<ProviderConfig>(ser.Serialize(walletUsd)).EffectiveGlmCurrency == "USD", "GLM 币种持久化");
        check(new ProviderConfig { Type = "glm", Region = "intl", GlmMode = "wallet" }.EffectiveGlmCurrency == "CNY", "GLM 旧钱包配置默认人民币，不按域名推断");
        check(QuotaService.GlmBaseUrl(new ProviderConfig { Type = "glm", Region = "intl", GlmMode = "wallet", GlmCurrency = "CNY" }) == "https://open.bigmodel.cn", "GLM 人民币钱包固定走国内站");
        check(QuotaService.GlmBaseUrl(new ProviderConfig { Type = "glm", Region = "cn", GlmMode = "wallet", GlmCurrency = "USD" }) == "https://api.z.ai", "GLM 美元钱包固定走国际站");
        check(QuotaService.GlmBaseUrl(new ProviderConfig { Type = "glm", Region = "intl", GlmMode = "quota" }) == "https://api.z.ai", "GLM 套餐仍按接口区域选择站点");

        // Coding Plan 新版协议：个人版裸 Key 无参数；团队版 ?type=2 + 组织/项目头
        var personal = new ProviderConfig { Type = "glm", Region = "cn", Name = "GLM" };
        check(personal.EffectiveGlmPlanType == "personal", "GLM 旧配置默认个人套餐");
        check(QuotaService.GlmQuotaUrl(personal) == "https://open.bigmodel.cn/api/monitor/usage/quota/limit", "GLM 个人套餐 URL 不带 type 参数");
        check(QuotaService.GlmQuotaHeaders(personal).Count == 0, "GLM 个人套餐不带团队头");
        var team = personal.Clone();
        team.GlmPlanType = "team"; team.GlmOrganization = "org-abc"; team.GlmProject = "proj_123";
        check(QuotaService.GlmQuotaUrl(team) == "https://open.bigmodel.cn/api/monitor/usage/quota/limit?type=2", "GLM 团队套餐 URL 带 type=2");
        var teamHeaders = QuotaService.GlmQuotaHeaders(team);
        check(teamHeaders["bigmodel-organization"] == "org-abc" && teamHeaders["bigmodel-project"] == "proj_123", "GLM 团队套餐带组织和项目上下文头");
        check(Throws(delegate { QuotaService.GlmQuotaHeaders(new ProviderConfig { Type = "glm", GlmPlanType = "team" }); }), "GLM 团队套餐缺上下文时明确报错");
        check(!team.HasSameQuery(personal), "GLM 切换个人/团队后不复用旧额度");
        var teamMoved = team.Clone(); teamMoved.GlmProject = "proj_456";
        check(!teamMoved.HasSameQuery(team), "GLM 修改团队项目 ID 后不复用旧额度");
        bool emptyTeam = false;
        try { QuotaService.ParseGlmQuota(team, "{\"success\":true,\"data\":{}}"); }
        catch (Exception ex) { emptyTeam = ex.Message.Contains("组织"); }
        check(emptyTeam, "GLM 团队接口返回空 data 时提示检查组织/项目 ID");
        check(new ProviderConfig { Type = "glm", Region = "cn", GlmMode = "bad" }.EffectiveGlmMode == "quota", "GLM 未知查询内容回退为套餐");
        check(new ProviderConfig { Type = "glm", Region = "cn", GlmMode = "both" }.EffectiveGlmMode == "quota", "GLM 旧版 both 配置回退为套餐");
        defaults.GlmMode = null;
        var defaulted = defaults.Clone(); defaulted.GlmMode = "quota";
        check(defaults.HasSameQuery(defaulted), "GLM 旧配置缺失字段与默认值视为同一查询");

        // 编辑对话框：查询内容下拉回填与保存；非 GLM 不出现该行
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "glm", Region = "intl", Name = "GLM", Key = "example-key", GlmMode = "wallet", GlmCurrency = "USD" }, SettingsPal.For(false), 1f))
        {
            check((int)typeof(ProviderEditDialog).GetField("_glmMode", Private).GetValue(dialog) == 2, "GLM 美元钱包配置回填美元选项");
            var rect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcGlmMode", Private).GetValue(dialog);
            check(rect != Rectangle.Empty, "GLM 编辑框显示查询内容");
        }
        CheckDialogChoice(0, "quota", null, "GLM 编辑框选择 Coding Plan 后保存");
        CheckDialogChoice(1, "wallet", "CNY", "GLM 编辑框选择人民币钱包后保存");
        CheckDialogChoice(2, "wallet", "USD", "GLM 编辑框选择美元钱包后保存");
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "glm", Region = "cn", Name = "GLM", Key = "example-key" }, SettingsPal.For(false), 1f))
        {
            typeof(ProviderEditDialog).GetField("_glmPlanType", Private).SetValue(dialog, "team");
            typeof(ProviderEditDialog).GetField("_glmOrg", Private).SetValue(dialog, "org-x");
            typeof(ProviderEditDialog).GetField("_glmProj", Private).SetValue(dialog, "proj-y");
            var contextRect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcGlmContext", Private).GetValue(dialog);
            check(contextRect != Rectangle.Empty, "GLM 套餐模式显示上下文按钮");
            typeof(ProviderEditDialog).GetMethod("OnOk", Private).Invoke(dialog, null);
            check(dialog.Result != null && dialog.Result.GlmPlanType == "team" &&
                dialog.Result.GlmOrganization == "org-x" && dialog.Result.GlmProject == "proj-y", "GLM 编辑框保存团队上下文");
        }
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "glm", Region = "cn", Name = "GLM", Key = "example-key", GlmMode = "wallet", GlmCurrency = "CNY" }, SettingsPal.For(false), 1f))
        {
            check((Rectangle)typeof(ProviderEditDialog).GetField("_rcGlmContext", Private).GetValue(dialog) == Rectangle.Empty, "GLM 钱包模式不显示套餐上下文按钮");
        }
        using (var teamDialog = new GlmTeamContextDialog("team", "org-x", "proj-y", SettingsPal.For(false), 1f))
        {
            typeof(GlmTeamContextDialog).GetMethod("OnOk", Private).Invoke(teamDialog, null);
            check(teamDialog.PlanType == "team" && teamDialog.Organization == "org-x" && teamDialog.Project == "proj-y", "GLM 团队上下文对话框回传字段");
        }
        using (var personalDialog = new GlmTeamContextDialog("personal", "org-x", "proj-y", SettingsPal.For(false), 1f))
        {
            typeof(GlmTeamContextDialog).GetMethod("OnOk", Private).Invoke(personalDialog, null);
            check(personalDialog.PlanType == "personal" && personalDialog.Organization == "" && personalDialog.Project == "", "GLM 个人版对话框清空团队字段");
        }
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "ds", Region = "cn", Name = "DeepSeek", Key = "example-key" }, SettingsPal.For(false), 1f))
        {
            var rect = (Rectangle)typeof(ProviderEditDialog).GetField("_rcGlmMode", Private).GetValue(dialog);
            check(rect == Rectangle.Empty, "非 GLM 接口不显示查询内容行");
            typeof(ProviderEditDialog).GetMethod("OnOk", Private).Invoke(dialog, null);
            check(dialog.Result != null && dialog.Result.GlmMode == "quota" && dialog.Result.GlmCurrency == null, "非 GLM 接口保存默认值且不带币种");
        }
    }

    private static void CheckDialogChoice(int choice, string mode, string currency, string message)
    {
        using (var dialog = new ProviderEditDialog(new ProviderConfig { Type = "glm", Region = "intl", Name = "GLM", Key = "example-key" }, SettingsPal.For(false), 1f))
        {
            typeof(ProviderEditDialog).GetField("_glmMode", Private).SetValue(dialog, choice);
            typeof(ProviderEditDialog).GetMethod("OnOk", Private).Invoke(dialog, null);
            Check(dialog.Result != null && dialog.Result.GlmMode == mode && dialog.Result.GlmCurrency == currency, message);
        }
    }
}
