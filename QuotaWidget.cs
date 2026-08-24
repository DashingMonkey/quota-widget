// QuotaWidget - AI 额度悬浮窗 (GLM Coding Plan / MiniMax Token Plan / Kimi Code Plan / DeepSeek 余额)
// 编译: csc.exe /nologo /utf8output /target:winexe /optimize+ /win32icon:QuotaWidget.ico /out:QuotaWidget.exe QuotaWidget.cs /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace QuotaWidget
{
    // ============================= 应用信息 =============================
    internal static class AppInfo
    {
        public const string Version = "1.1.5";
    }

    // ============================= 数据模型 =============================
    internal class ProviderConfig
    {
        public string Type { get; set; }     // "glm" / "mx"
        public string Region { get; set; }   // "cn" / "intl"
        public string Key { get; set; }
        public string Name { get; set; }     // 显示名称

        public ProviderConfig() { Type = "glm"; Region = "cn"; Key = ""; Name = "GLM"; }

        [ScriptIgnore]
        public string TypeName
        {
            get
            {
                if (Type == "glm") return Region == "intl" ? "GLM 国际版" : "GLM 国内版";
                if (Type == "mx") return Region == "intl" ? "MiniMax 国际版" : "MiniMax 国内版";
                if (Type == "kimi") return "Kimi Code Plan";
                if (Type == "ds") return "DeepSeek";
                return Type;
            }
        }
    }

    internal class QuotaPool
    {
        public string Label = "";
        public double Total;
        public double Used;
        public double Remaining;
        public int Percent;            // 已用百分比 0-100
        public DateTime? ResetLocal;   // 本地时间
        public bool ResetEstimated;    // 重置时间是否为估算
        public bool PercentOnly;       // 仅按百分比展示（接口计数字段不可靠）
        public string Note;            // 附加说明（未启用/已耗尽/周加成等）
        public bool IsMoney;           // 金额展示模式（DeepSeek 余额）：显示金额而非百分比，不画进度条
        public string Currency = "";   // 货币符号，如 ¥ / $
    }

    internal class ProviderStatus
    {
        public string Name = "";
        public string Type = "";        // "glm" / "mx" 用于决定主题色
        public string Level = "";
        public string Error = null;
        public List<QuotaPool> Pools = new List<QuotaPool>();
        public DateTime UpdatedAt = DateTime.Now;
        public QuotaPool Primary5h;
    }

    // ============================= 配置 =============================
    internal class Config
    {
        public List<ProviderConfig> Providers { get; set; }
        // 旧字段仅用于从旧 config.json 迁移
        public string GlmKey { get; set; }
        public string GlmRegion { get; set; }
        public string MxKey { get; set; }
        public string MxRegion { get; set; }
        public int RefreshSec { get; set; }
        public int OpacityPct { get; set; }
        public bool TopMost { get; set; }
        public string Theme { get; set; }   // "dark" / "light"
        public int X { get; set; }
        public int Y { get; set; }
        public bool AutoStart { get; set; }
        public bool MiniMode { get; set; }   // mini 模式：主悬浮窗整体缩小一倍
        public bool LockPosition { get; set; }   // 固定位置：禁止拖拽悬浮窗
        public string DisplayMode { get; set; }  // "float"(悬浮窗) / "topbar"(置顶栏)
        public int AppBarScreen { get; set; }    // 置顶栏模式：显示在哪个屏幕（索引，默认0=主屏）

        public Config()
        {
            Providers = new List<ProviderConfig>();
            GlmKey = ""; GlmRegion = "cn"; MxKey = ""; MxRegion = "cn";
            RefreshSec = 60; OpacityPct = 95; TopMost = true; Theme = "light"; X = -1; Y = -1; AutoStart = false; MiniMode = false; LockPosition = false;
            DisplayMode = "float"; AppBarScreen = 0;
        }

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); }
        }

        public static Config Load()
        {
            Config cfg = new Config();
            try
            {
                if (File.Exists(FilePath))
                {
                    var ser = new JavaScriptSerializer();
                    cfg = ser.Deserialize<Config>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new Config();
                }
            }
            catch
            {
                // 解析失败（文件损坏/截断）：先备份原文件便于找回 API Key，再回退默认配置
                try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
                cfg = new Config();
            }
            // 从旧格式迁移：如果 Providers 为空但旧字段有值，则迁移
            if ((cfg.Providers == null || cfg.Providers.Count == 0) && cfg.HasLegacyKeys())
                cfg.MigrateLegacy();
            // "Providers": null 是合法 JSON，反序列化后仍为 null 且无旧 Key 可迁移；
            // 不归一化的话 RefreshData 在线程池线程访问 Providers.Count 抛 NRE，
            // 未处理异常直接终止进程，形成启动即崩的死循环，用户无法进入设置页自救
            if (cfg.Providers == null) cfg.Providers = new List<ProviderConfig>();
            // 主题字段为空时默认浅色
            if (string.IsNullOrEmpty(cfg.Theme)) cfg.Theme = "light";
            return cfg;
        }

        private bool HasLegacyKeys()
        {
            return !string.IsNullOrWhiteSpace(GlmKey) || !string.IsNullOrWhiteSpace(MxKey);
        }

        private void MigrateLegacy()
        {
            if (Providers == null) Providers = new List<ProviderConfig>();
            if (!string.IsNullOrWhiteSpace(GlmKey))
                Providers.Add(new ProviderConfig { Type = "glm", Region = GlmRegion, Key = GlmKey, Name = "GLM" });
            if (!string.IsNullOrWhiteSpace(MxKey))
                Providers.Add(new ProviderConfig { Type = "mx", Region = MxRegion, Key = MxKey, Name = "MiniMax" });
            GlmKey = ""; GlmRegion = "cn"; MxKey = ""; MxRegion = "cn";
        }

        /// 保存成功返回 true；失败（磁盘满/文件被占用等）返回 false，供调用方提示用户
        public bool Save()
        {
            try
            {
                var ser = new JavaScriptSerializer();
                // 原子写：先写临时文件再替换，避免写入中途断电/崩溃留下截断的损坏文件，
                // 否则下次启动静默回退空配置，一次保存就会让全部 API Key 永久丢失
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, ser.Serialize(this), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("配置保存失败: " + ex.Message); return false; }
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("QuotaWidget", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("QuotaWidget", false);
                }
            }
            catch { }
        }
    }

    // ============================= JSON 辅助 =============================
    internal static class J
    {
        public static string S(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }
        public static double D(Dictionary<string, object> d, string k)
        {
            object v; if (!d.TryGetValue(k, out v) || v == null) return 0;
            double r; return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out r) ? r : 0;
        }
        public static bool HasKey(Dictionary<string, object> d, string k) { return d.ContainsKey(k) && d[k] != null; }
        public static IEnumerable<object> Each(object o)
        {
            if (o is ArrayList) { foreach (var i in (ArrayList)o) yield return i; }
            else if (o is object[]) { foreach (var i in (object[])o) yield return i; }
        }
    }

    // ============================= 查询服务 =============================
    internal static class QuotaService
    {
        // 携带 HTTP 状态码的查询异常（当前作为错误信息载体；待验证 MiniMax 两端点鉴权一致后，
        // 可据此对 401/403 做跳过回退的短路优化）
        private class HttpException : Exception
        {
            public readonly int StatusCode;
            public HttpException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
        }

        private static string HttpGet(string url, string authorization)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = "QuotaWidget/" + AppInfo.Version;
            req.Accept = "application/json";
            if (authorization != null) req.Headers["Authorization"] = authorization;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException we)
            {
                string body = "";
                int status = 0;
                try
                {
                    if (we.Response != null)
                    {
                        status = (int)((HttpWebResponse)we.Response).StatusCode;
                        using (var sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                            body = sr.ReadToEnd();
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(body) && body.Length > 300) body = body.Substring(0, 300);
                throw new HttpException(status, string.IsNullOrEmpty(body) ? we.Message : body);
            }
        }

        public static DateTime FromMs(long ms)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        }

        public static DateTime NextFiveHourBoundary(DateTime now)
        {
            int nextH = (now.Hour / 5 + 1) * 5;
            if (nextH >= 24) return now.Date.AddDays(1);
            return now.Date.AddHours(nextH);
        }

        private static DateTime? ParseTimeFlexible(object v)
        {
            if (v == null) return null;
            string s = v.ToString();
            long n;
            if (long.TryParse(s, out n))
            {
                if (n > 100000000000L) return FromMs(n);                                   // 毫秒
                if (n > 1000000000L) return DateTimeOffset.FromUnixTimeSeconds(n).LocalDateTime; // 秒
                return null;
            }
            // 兼容 ISO 8601（含 Z 或时区偏移，如 Kimi 的 resetTime），统一转为本地时间
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
                return dto.LocalDateTime;
            DateTime dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt)) return dt;
            return null;
        }

        // ---------- GLM ----------
        // 200+success=false+code 1000/1001 = 鉴权失败（Key 无效/被删/填错，或鉴权格式被拒），
        // 回退循环与最终错误分类共用此判据，避免两处判定不一致
        private static bool IsAuthFailure(Dictionary<string, object> root)
        {
            bool success = false;
            object sv;
            if (root.TryGetValue("success", out sv) && sv is bool) success = (bool)sv;
            if (success) return false;
            int code = (int)Math.Round(J.D(root, "code"));
            return code == 1000 || code == 1001;
        }

        public static ProviderStatus FetchGlm(ProviderConfig p)
        {
            string baseUrl = p.Region == "intl" ? "https://api.z.ai" : "https://open.bigmodel.cn";
            string url = baseUrl + "/api/monitor/usage/quota/limit";
            // 鉴权格式：先 Bearer 前缀（现行官方口径，与智谱控制台/主流工具一致），
            // 失败（401/403 或 200+鉴权失败 JSON）或空响应时回退旧式裸 Key（历史上 GLM 曾要求无 Bearer 前缀）。
            // 2026-08 起智谱监控接口故障：对有效 Key 返回 200+空 body，空体经
            // JavaScriptSerializer 反序列化得 null，后续 root.TryGetValue 会抛
            // NullReferenceException（"未将对象引用设置到对象的实例"），必须在此拦截
            string json = null;            // 有效（非空、非鉴权失败）响应体
            Exception firstErr = null;     // 首个鉴权相关失败（401/403 或 200+鉴权失败 JSON）；双失败时抛出，保留真实根因
            var ser = new JavaScriptSerializer();
            foreach (var auth in new[] { "Bearer " + p.Key, p.Key })
            {
                try
                {
                    string body = HttpGet(url, auth);
                    if (string.IsNullOrWhiteSpace(body)) continue;   // 200 空体：服务端故障特征
                    // 服务端可能用 200+success=false+code 1000/1001 表达"鉴权失败/格式被拒"而非 HTTP 4xx：
                    // 该形态视为本轮失败，继续回退下一种鉴权格式，避免仅兼容裸 Key 的合法 Key 被误判为无效
                    Dictionary<string, object> probe = null;
                    try { probe = ser.Deserialize<Dictionary<string, object>>(body); }
                    catch { }   // 不可解析的响应体按普通响应处理，交由下方反序列化统一报错
                    if (probe != null && IsAuthFailure(probe))
                    {
                        if (firstErr == null)
                            firstErr = new Exception("GLM API Key 无效或已失效（" + J.S(probe, "msg") + "）");
                        continue;
                    }
                    json = body; break;
                }
                catch (HttpException he)
                {
                    // 仅鉴权格式被拒（401/403）才回退下一种格式；超时/5xx/网络错误与鉴权格式无关，
                    // 重试不会改变结果，直接抛出，避免无谓的第二次请求（最坏 15s→30s 等待）
                    if (he.StatusCode == 401 || he.StatusCode == 403) { if (firstErr == null) firstErr = he; }
                    else throw;
                }
            }
            if (json == null)
            {
                // 两种格式都未取得有效响应：有真实鉴权错误优先抛出；
                // 否则说明只收到空体 —— 显式告知服务端故障，而非让空体触发 NRE
                if (firstErr != null) throw firstErr;
                throw new Exception("智谱接口返回空响应（服务端故障中，非 Key 或网络问题），请稍后重试");
            }

            var root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null) throw new Exception("智谱接口返回无法解析的响应: " + json.Substring(0, Math.Min(120, json.Length)));
            bool success = false;
            object sv;
            if (root.TryGetValue("success", out sv) && sv is bool) success = (bool)sv;
            if (!success)
            {
                // 鉴权失败（code 1000/1001）已在回退循环内拦截并优先抛出，此处只剩通用业务错误
                throw new Exception("接口返回失败: " + J.S(root, "msg"));
            }

            var st = new ProviderStatus { Name = p.Name, Type = "glm" };
            var data = root["data"] as Dictionary<string, object>;
            if (data == null) throw new Exception("返回缺少 data 字段");
            st.Level = J.S(data, "level").ToUpper();

            object limitsObj;
            if (data.TryGetValue("limits", out limitsObj))
            {
                foreach (var item in J.Each(limitsObj))
                {
                    var lim = item as Dictionary<string, object>;
                    if (lim == null) continue;
                    string type = J.S(lim, "type");
                    int unit = (int)Math.Round(J.D(lim, "unit"));
                    int number = (int)Math.Round(J.D(lim, "number"));
                    string label;
                    bool is5h = false;
                    if (type == "CREDIT_LIMIT")
                    {
                        if (unit == 3) { label = number + " 小时窗口额度"; is5h = true; }
                        else if (unit == 6) label = "周额度";
                        else label = "周期额度";
                    }
                    else if (type == "TOKENS_LIMIT") { label = "5 小时 Token 限额"; is5h = true; }
                    else if (type == "TIME_LIMIT") label = "MCP 工具月度配额";
                    else label = string.IsNullOrEmpty(type) ? "额度" : type;

                    var pool = new QuotaPool
                    {
                        Label = label,
                        Total = J.D(lim, "usage"),
                        Used = J.D(lim, "currentValue"),
                        Remaining = J.D(lim, "remaining"),
                        Percent = (int)Math.Round(J.D(lim, "percentage"))
                    };
                    if (pool.Percent == 0 && pool.Total > 0)
                        pool.Percent = (int)Math.Round(pool.Used / pool.Total * 100);

                    if (J.HasKey(lim, "nextResetTime"))
                    {
                        var t = ParseTimeFlexible(lim["nextResetTime"]);
                        if (t.HasValue) pool.ResetLocal = t.Value;
                    }
                    if (is5h)
                    {
                        if (!pool.ResetLocal.HasValue)
                        {
                            pool.ResetLocal = NextFiveHourBoundary(DateTime.Now);
                            pool.ResetEstimated = true;
                        }
                        if (st.Primary5h == null) st.Primary5h = pool;
                    }
                    st.Pools.Add(pool);
                }
            }
            st.UpdatedAt = DateTime.Now;
            return st;
        }

        // ---------- MiniMax ----------
        private static void CollectModelEntries(object node, List<Dictionary<string, object>> acc)
        {
            var dict = node as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.ContainsKey("current_interval_total_count")) { acc.Add(dict); return; }
                foreach (var v in dict.Values) CollectModelEntries(v, acc);
                return;
            }
            foreach (var item in J.Each(node)) CollectModelEntries(item, acc);
        }

        // 按 token_plan 的窗口字段构建额度池；以 remaining_percent 为准（计数字段恒为 0，不可用）
        private static QuotaPool MxWindowPool(Dictionary<string, object> e, string label, string pctKey, string statusKey, string remainMsKey, string endTimeKey)
        {
            if (!J.HasKey(e, pctKey) && !J.HasKey(e, statusKey)) return null;
            int status = (int)J.D(e, statusKey);   // 1=正常 2=耗尽 3=未启用
            double remainPct = J.D(e, pctKey);
            var pool = new QuotaPool
            {
                Label = label,
                PercentOnly = true,
                Total = 100,
                Remaining = remainPct,
                Used = Math.Max(0, 100 - remainPct)
            };
            pool.Percent = (int)Math.Round(pool.Used);
            if (status == 2) { pool.Percent = 100; pool.Note = "已耗尽"; }
            else if (status == 3) { pool.Percent = 0; pool.Note = "未启用"; }

            // 重置时间：优先「距窗口结束毫秒数」，其次绝对结束时间
            if (J.HasKey(e, remainMsKey))
            {
                double ms = J.D(e, remainMsKey);
                if (ms > 0) pool.ResetLocal = DateTime.Now.AddMilliseconds(ms);
            }
            if (!pool.ResetLocal.HasValue && J.HasKey(e, endTimeKey))
            {
                var t = ParseTimeFlexible(e[endTimeKey]);
                if (t.HasValue) pool.ResetLocal = t.Value;
            }
            return pool;
        }

        public static ProviderStatus FetchMiniMax(ProviderConfig p)
        {
            string baseUrl = p.Region == "intl" ? "https://www.minimax.io" : "https://www.minimaxi.com";
            // 优先官方文档接口，失败则回退到 coding_plan 接口
            string json = null;
            try { json = HttpGet(baseUrl + "/v1/token_plan/remains", "Bearer " + p.Key); }
            catch (Exception first)
            {
                // 两端点鉴权范围是否一致未经服务端验证，401/403 也保留回退（不缩小既有覆盖面）；
                // 双失败时抛第一个异常，保留更接近真实根因的 token_plan 错误
                try { json = HttpGet(baseUrl + "/v1/api/openplatform/coding_plan/remains", "Bearer " + p.Key); }
                catch { throw first; } // 回退也失败时保留第一个异常（更接近真实根因）
            }

            var ser = new JavaScriptSerializer();
            var root = ser.Deserialize<Dictionary<string, object>>(json);

            object brObj;
            if (root.TryGetValue("base_resp", out brObj))
            {
                var br = brObj as Dictionary<string, object>;
                if (br != null && J.D(br, "status_code") != 0)
                    throw new Exception("MiniMax: " + J.S(br, "status_msg"));
            }

            var st = new ProviderStatus { Name = p.Name, Type = "mx" };

            // 优先 token_plan 的 model_remains 结构：跳过 video，只保留 5 小时窗口 + 周窗口
            var models = new List<Dictionary<string, object>>();
            object mrObj;
            if (root.TryGetValue("model_remains", out mrObj))
            {
                foreach (var item in J.Each(mrObj))
                {
                    var d = item as Dictionary<string, object>;
                    if (d != null) models.Add(d);
                }
            }

            if (models.Count > 0)
            {
                foreach (var e in models)
                {
                    string model = J.S(e, "model_name");
                    if (string.IsNullOrEmpty(model)) model = J.S(e, "model");
                    if (string.IsNullOrEmpty(model)) model = "general";
                    if (model.Equals("video", StringComparison.OrdinalIgnoreCase)) continue;

                    string suffix = model == "general" ? "" : " · " + model;

                    var p5 = MxWindowPool(e, "5 小时窗口额度" + suffix, "current_interval_remaining_percent", "current_interval_status", "remains_time", "end_time");
                    if (p5 != null) st.Pools.Add(p5);

                    var pw = MxWindowPool(e, "周额度" + suffix, "current_weekly_remaining_percent", "current_weekly_status", "weekly_remains_time", "weekly_end_time");
                    if (pw != null)
                    {
                        double boost = J.D(e, "weekly_boost_permille");
                        if (boost > 0)
                            pw.Note = (string.IsNullOrEmpty(pw.Note) ? "" : pw.Note + " · ") + "+" + (boost / 10).ToString("0.#", CultureInfo.InvariantCulture) + "% 周加成";
                        st.Pools.Add(pw);
                    }
                }
            }
            else
            {
                // 旧结构回退：递归查找含 current_interval_total_count 的条目
                var entries = new List<Dictionary<string, object>>();
                CollectModelEntries(root, entries);
                if (entries.Count == 0)
                    throw new Exception("未识别的返回结构，请通过设置页「测试连接」查看原始返回");

                foreach (var e in entries)
                {
                    string model = J.S(e, "model_name");
                    if (string.IsNullOrEmpty(model)) model = J.S(e, "model");
                    if (string.IsNullOrEmpty(model)) model = "模型";
                    if (model.Equals("video", StringComparison.OrdinalIgnoreCase)) continue;

                    double total = J.D(e, "current_interval_total_count");
                    double remain = J.D(e, "current_interval_usage_count"); // 注意: 此字段为【剩余量】
                    var pool = new QuotaPool
                    {
                        Label = "5 小时窗口额度 · " + model,
                        Total = total,
                        Remaining = remain,
                        Used = Math.Max(0, total - remain)
                    };
                    pool.Percent = total > 0 ? (int)Math.Round(pool.Used / total * 100) : 0;

                    string[] timeKeys = { "current_interval_end_time", "end_time", "next_reset_time", "reset_time", "current_interval_reset_time" };
                    foreach (var tk in timeKeys)
                    {
                        if (J.HasKey(e, tk))
                        {
                            var t = ParseTimeFlexible(e[tk]);
                            if (t.HasValue) { pool.ResetLocal = t.Value; break; }
                        }
                    }
                    st.Pools.Add(pool);

                    // 周窗口（若存在）
                    double wTotal = 0, wRemain = 0; bool hasWeekly = false;
                    foreach (var kv in e)
                    {
                        string k = kv.Key.ToLowerInvariant();
                        if (k.StartsWith("weekly") && k.EndsWith("total_count")) { wTotal = J.D(e, kv.Key); hasWeekly = true; }
                        if (k.StartsWith("weekly") && k.EndsWith("usage_count")) { wRemain = J.D(e, kv.Key); }
                    }
                    if (hasWeekly && wTotal > 0)
                    {
                        var wp = new QuotaPool
                        {
                            Label = "周额度 · " + model,
                            Total = wTotal,
                            Remaining = wRemain,
                            Used = Math.Max(0, wTotal - wRemain)
                        };
                        wp.Percent = (int)Math.Round(wp.Used / wTotal * 100);
                        st.Pools.Add(wp);
                    }
                }
            }

            // 悬浮窗主显示：取用量最高的 5 小时窗口（忽略未启用）
            foreach (var pool in st.Pools)
            {
                if (!pool.Label.StartsWith("5 小时窗口")) continue;
                if (pool.Note == "未启用") continue;
                if (st.Primary5h == null || pool.Percent > st.Primary5h.Percent) st.Primary5h = pool;
            }
            if (st.Primary5h != null && !st.Primary5h.ResetLocal.HasValue)
            {
                st.Primary5h.ResetLocal = NextFiveHourBoundary(DateTime.Now);
                st.Primary5h.ResetEstimated = true;
            }
            st.UpdatedAt = DateTime.Now;
            return st;
        }

        // ---------- Kimi (Code Plan) ----------
        // 接口：GET https://api.kimi.com/coding/v1/usages
        // usage 为周额度；limits[] 为频率滚动窗口（duration 300 分钟 = 5 小时窗口）
        // limit/used/remaining 为 0-100 的百分制刻度
        private static string KimiLevelName(string lvl)
        {
            if (string.IsNullOrEmpty(lvl)) return "";
            switch (lvl)
            {
                case "LEVEL_ENTRY": return "Andante";
                case "LEVEL_INTERMEDIATE": return "Moderato";
                case "LEVEL_ADVANCED": return "Allegretto";
                case "LEVEL_ULTIMATE": return "Allegro";
                default: return lvl.Replace("LEVEL_", "").Replace("_", " ");
            }
        }

        private static QuotaPool KimiQuotaPool(string label, Dictionary<string, object> d)
        {
            double limit = J.D(d, "limit");
            double used = J.D(d, "used");
            double remaining = J.D(d, "remaining");
            if (limit <= 0 && used <= 0 && remaining <= 0) return null;
            var pool = new QuotaPool
            {
                Label = label,
                PercentOnly = limit > 0 && limit <= 100,
                Total = limit > 0 ? limit : 100,
                Used = used,
                Remaining = remaining
            };
            // Kimi 字段本身即 0-100 百分制：Percent 直接取 Used，与文字展示的 Remaining 同刻度；
            // 若按 used/limit 换算（used=30/limit=60 → 50%），进度条与"剩余 30%"文字会互相矛盾
            pool.Percent = pool.PercentOnly
                ? (int)Math.Round(pool.Used)
                : (pool.Total > 0 ? (int)Math.Round(pool.Used / pool.Total * 100) : 0);
            if (pool.Percent < 0) pool.Percent = 0;
            if (pool.Percent > 100) pool.Percent = 100;
            if (J.HasKey(d, "resetTime"))
            {
                var t = ParseTimeFlexible(d["resetTime"]);
                if (t.HasValue) pool.ResetLocal = t.Value;
            }
            return pool;
        }

        public static ProviderStatus FetchKimi(ProviderConfig p)
        {
            string json = HttpGet("https://api.kimi.com/coding/v1/usages", "Bearer " + p.Key);

            var ser = new JavaScriptSerializer();
            var root = ser.Deserialize<Dictionary<string, object>>(json);

            // 错误返回：{"error":{"message":...}}
            object errObj;
            if (root.TryGetValue("error", out errObj))
            {
                var err = errObj as Dictionary<string, object>;
                if (err != null) throw new Exception("Kimi: " + J.S(err, "message"));
            }

            var st = new ProviderStatus { Name = p.Name, Type = "kimi" };

            // 会员等级
            object userObj;
            if (root.TryGetValue("user", out userObj))
            {
                var user = userObj as Dictionary<string, object>;
                if (user != null)
                {
                    object mObj;
                    if (user.TryGetValue("membership", out mObj))
                    {
                        var m = mObj as Dictionary<string, object>;
                        if (m != null) st.Level = KimiLevelName(J.S(m, "level"));
                    }
                }
            }

            // 频率滚动窗口（5 小时）—— 置于周额度之上
            object limitsObj;
            if (root.TryGetValue("limits", out limitsObj))
            {
                foreach (var item in J.Each(limitsObj))
                {
                    var e = item as Dictionary<string, object>;
                    if (e == null) continue;
                    string label = "频率窗口额度";
                    object winObj;
                    if (e.TryGetValue("window", out winObj))
                    {
                        var win = winObj as Dictionary<string, object>;
                        if (win != null)
                        {
                            int minutes = (int)Math.Round(J.D(win, "duration"));
                            string unit = J.S(win, "timeUnit");
                            if (unit == "TIME_UNIT_HOUR") minutes *= 60;
                            if (minutes >= 60)
                                label = ((int)Math.Round(minutes / 60.0)) + " 小时窗口额度";
                        }
                    }
                    Dictionary<string, object> detail = null;
                    object dObj;
                    if (e.TryGetValue("detail", out dObj)) detail = dObj as Dictionary<string, object>;
                    if (detail == null) detail = e;
                    var pool = KimiQuotaPool(label, detail);
                    if (pool == null) continue;
                    st.Pools.Add(pool);
                    if (label.StartsWith("5 小时窗口") && st.Primary5h == null) st.Primary5h = pool;
                }
            }

            // 周额度（主 usage）
            object usageObj;
            if (root.TryGetValue("usage", out usageObj))
            {
                var u = usageObj as Dictionary<string, object>;
                if (u != null)
                {
                    var pool = KimiQuotaPool("周额度", u);
                    if (pool != null) st.Pools.Add(pool);
                }
            }

            // 悬浮窗主显示：优先 5 小时窗口，缺失则回退到周额度
            if (st.Primary5h == null && st.Pools.Count > 0)
            {
                foreach (var pool in st.Pools)
                    if (pool.Label == "周额度") { st.Primary5h = pool; break; }
            }
            if (st.Primary5h != null && !st.Primary5h.ResetLocal.HasValue)
            {
                st.Primary5h.ResetLocal = NextFiveHourBoundary(DateTime.Now);
                st.Primary5h.ResetEstimated = true;
            }
            st.UpdatedAt = DateTime.Now;
            return st;
        }

        // ---------- DeepSeek ----------
        // 官方仅公开余额查询接口：GET https://api.deepseek.com/user/balance
        // 用量统计无公开 API（platform.deepseek.com 的 /api/v0/usage/* 为网页内部接口，
        // 仅认浏览器登录 cookie，API Key 调不通）。故此处只展示账户余额。
        public static ProviderStatus FetchDeepSeek(ProviderConfig p)
        {
            string json = HttpGet("https://api.deepseek.com/user/balance", "Bearer " + p.Key);

            var ser = new JavaScriptSerializer();
            var root = ser.Deserialize<Dictionary<string, object>>(json);

            bool isAvailable = false;
            object avObj;
            if (root.TryGetValue("is_available", out avObj) && avObj is bool) isAvailable = (bool)avObj;

            object biObj;
            if (!root.TryGetValue("balance_infos", out biObj))
                throw new Exception("返回缺少 balance_infos 字段");

            string currency = "¥";
            double total = 0, granted = 0, topped = 0;
            foreach (var item in J.Each(biObj))
            {
                var info = item as Dictionary<string, object>;
                if (info == null) continue;
                string cur = J.S(info, "currency");
                if (cur == "CNY") currency = "¥";
                else if (cur == "USD") currency = "$";
                else if (!string.IsNullOrEmpty(cur)) currency = cur + " ";
                total = J.D(info, "total_balance");
                granted = J.D(info, "granted_balance");
                topped = J.D(info, "topped_up_balance");
                break; // 仅取第一个币种
            }

            var st = new ProviderStatus { Name = p.Name, Type = "ds" };
            st.Level = isAvailable ? "可用" : "不可用";

            // 占位百分比：可用→绿满条(已用0%)；不可用→红空条(已用100%)
            int placeholderPct = isAvailable ? 0 : 100;

            // 悬浮窗主显示：总余额（金额模式，右侧显示金额，画占位条）
            st.Primary5h = new QuotaPool
            {
                Label = "余额",
                IsMoney = true,
                Currency = currency,
                Remaining = total,
                Total = total,
                Used = 0,
                Percent = placeholderPct
            };

            // 详情明细
            st.Pools.Add(new QuotaPool { Label = "总余额", IsMoney = true, Currency = currency, Remaining = total, Total = total, Percent = placeholderPct });
            st.Pools.Add(new QuotaPool { Label = "充值余额", IsMoney = true, Currency = currency, Remaining = topped, Total = topped, Percent = topped > 0 ? placeholderPct : 100 });
            st.Pools.Add(new QuotaPool { Label = "赠送余额", IsMoney = true, Currency = currency, Remaining = granted, Total = granted, Percent = granted > 0 ? placeholderPct : 100 });

            st.UpdatedAt = DateTime.Now;
            return st;
        }
    }

    // ============================= 样式 =============================
    internal static class St
    {
        // 主题: 1=深色 2=浅色
        public static int Theme = 2;
        // mini 模式（仅主悬浮窗）：隐藏圆点与进度条，仅保留名称 + 数值，字号不变
        public static bool Mini = false;

        // ---- DPI 缩放基础设施 ----
        // Per-Monitor DPI 感知下，字体以 pt 为单位按屏幕 DPI 换算像素（物理大小恒定），
        // 控件尺寸以 DIP（96DPI 基准）乘以所在屏缩放因子。
        private static float _scale = 1f;
        /// <summary>主屏 DPI 缩放因子（1.0=100%, 1.25=125%, 1.5=150%）</summary>
        public static float Scale { get { return _scale; } }
        /// <summary>初始化缩放因子，在程序启动时调用（基于主屏 DPI）</summary>
        public static void InitScale(int dpi) { _scale = Math.Max(1f, dpi / 96f); }
        /// <summary>按指定缩放因子缩放 int 像素</summary>
        public static int SiF(int v, float s) { return (int)Math.Round(v * s); }

        // ---- 字体：按屏幕 DPI 缓存 ----
        // pt 单位字体在正确 DPI 下创建：px = pt * dpi / 72，物理大小恒定（如 9pt 在任何屏都约 3.2mm）
        private static readonly Dictionary<int, Font[]> _fontCache = new Dictionary<int, Font[]>();
        /// <summary>获取指定 DPI 的字体组 [0]=FTitle [1]=FNorm [2]=FSmall [3]=FPct</summary>
        public static Font[] FontsFor(int dpi)
        {
            dpi = Math.Max(96, dpi);
            Font[] f;
            if (!_fontCache.TryGetValue(dpi, out f))
            {
                f = new Font[4];
                f[0] = new Font("Microsoft YaHei UI", 9.5f * dpi / 72f, FontStyle.Bold, GraphicsUnit.Pixel);
                f[1] = new Font("Microsoft YaHei UI", 9f * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
                f[2] = new Font("Microsoft YaHei UI", 8f * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
                f[3] = new Font("Microsoft YaHei UI", 11f * dpi / 72f, FontStyle.Bold, GraphicsUnit.Pixel);
                _fontCache[dpi] = f;
            }
            return f;
        }
        /// <summary>按缩放因子取字体组（s=1.0/1.25/1.5）</summary>
        public static Font[] FontsForScale(float s) { return FontsFor((int)Math.Round(96f * s)); }

        // ---- 屏幕 DPI 查询 ----
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        /// <summary>获取屏幕的 DPI（无效时回退 96）</summary>
        public static int DpiOf(Screen s)
        {
            try
            {
                var pt = new Point(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2);
                var hmon = MonitorFromPoint(pt, 2 /*MONITOR_DEFAULTTONEAREST*/);
                uint dpiX, dpiY;
                if (GetDpiForMonitor(hmon, 0 /*MDT_EFFECTIVE_DPI*/, out dpiX, out dpiY) == 0 /*S_OK*/)
                    return (int)dpiX;
            }
            catch
            {
                // 兼容性设计：Win7/8 无 shcore.dll（DllNotFoundException）或 P/Invoke 失败时
                // 按 96 DPI（100%）处理，自绘窗口以 1.0 缩放显示，功能不受影响。
            }
            return 96;
        }

        public static Color Bg { get { return Theme == 1 ? Color.FromArgb(44, 47, 59) : Color.FromArgb(240, 241, 244); } }
        public static Color Card { get { return Theme == 1 ? Color.FromArgb(55, 59, 73) : Color.FromArgb(249, 250, 251); } }
        public static Color Text { get { return Theme == 1 ? Color.FromArgb(232, 234, 240) : Color.FromArgb(51, 65, 85); } }
        public static Color Dim { get { return Theme == 1 ? Color.FromArgb(158, 166, 186) : Color.FromArgb(100, 116, 139); } }
        public static Color BarBg { get { return Theme == 1 ? Color.FromArgb(74, 79, 98) : Color.FromArgb(218, 222, 229); } }
        // 窗口最外圈细线：深色主题用浅线，浅色主题用比底色深一点的线
        public static Color Border { get { return Theme == 1 ? Color.FromArgb(68, 73, 90) : Color.FromArgb(204, 208, 217); } }
        public static Color Green { get { return Theme == 1 ? Color.FromArgb(52, 211, 153) : Color.FromArgb(46, 139, 87); } }
        public static Color Amber { get { return Theme == 1 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(190, 120, 24); } }
        public static Color Red { get { return Theme == 1 ? Color.FromArgb(248, 113, 113) : Color.FromArgb(198, 64, 48); } }
        public static Color GlmAccent { get { return Theme == 1 ? Color.FromArgb(129, 140, 248) : Color.FromArgb(92, 105, 224); } }
        public static Color MxAccent { get { return Theme == 1 ? Color.FromArgb(56, 189, 248) : Color.FromArgb(2, 132, 199); } }
        public static Color KimiAccent { get { return Theme == 1 ? Color.FromArgb(167, 139, 250) : Color.FromArgb(109, 40, 217); } }
        public static Color DsAccent { get { return Theme == 1 ? Color.FromArgb(96, 165, 250) : Color.FromArgb(24, 119, 242); } }

        // 基于剩余百分比(0-100)返回颜色：100=绿 → 50=琥珀 → 0=红，中间线性插值
        public static Color RemainColor(int remainPct)
        {
            remainPct = Math.Max(0, Math.Min(100, remainPct));
            if (remainPct >= 50) return Lerp(Amber, Green, (remainPct - 50) / 50.0);
            return Lerp(Red, Amber, remainPct / 50.0);
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color AccentFor(string type)
        {
            if (type == "mx") return MxAccent;
            if (type == "kimi") return KimiAccent;
            if (type == "ds") return DsAccent;
            return GlmAccent;
        }

        public static string FmtNum(double n)
        {
            return n.ToString("#,0", CultureInfo.InvariantCulture);
        }

        // 金额格式化：保留两位小数 + 千分位
        public static string FmtMoney(double n)
        {
            return n.ToString("#,0.00", CultureInfo.InvariantCulture);
        }

        public static string FmtCountdown(DateTime? reset, bool estimated)
        {
            if (!reset.HasValue) return "--:--:--";
            var diff = reset.Value - DateTime.Now;
            if (diff.TotalSeconds <= 0) return "00:00:00";
            string core;
            if (diff.TotalDays >= 1)
                core = string.Format("{0}天 {1:D2}:{2:D2}:{3:D2}", (int)diff.TotalDays, diff.Hours, diff.Minutes, diff.Seconds);
            else
                core = string.Format("{0:D2}:{1:D2}:{2:D2}", (int)diff.TotalHours, diff.Minutes, diff.Seconds);
            return (estimated ? "≈" : "") + core;
        }

        // 校验坐标是否落在某个屏幕的可视工作区内（防止跨屏越界导致窗口消失）
        public static bool IsPointOnScreen(Point p)
        {
            foreach (var s in Screen.AllScreens)
            {
                if (s.WorkingArea.Contains(p)) return true;
            }
            return false;
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void DrawBar(Graphics g, Rectangle r, int pct, Color fill)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            using (var bgBrush = new SolidBrush(BarBg))
            using (var fgBrush = new SolidBrush(fill))
            {
                using (var bgPath = RoundRect(r, r.Height / 2)) g.FillPath(bgBrush, bgPath);
                int w = (int)Math.Round(r.Width * pct / 100.0);
                if (w > 0)
                {
                    var fr = new Rectangle(r.X, r.Y, Math.Max(w, r.Height), r.Height);
                    if (fr.Width > r.Width) fr.Width = r.Width;
                    using (var fgPath = RoundRect(fr, r.Height / 2)) g.FillPath(fgBrush, fgPath);
                }
            }
        }

        // 提取自 ProviderRow.OnPaint，供 FloatingForm 自绘使用；y 为该行左上角纵坐标，s 为所在屏缩放因子
        public static void DrawRow(Graphics g, ProviderStatus st, int y, int width, float s)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool err = st.Error != null;
            var pool = st.Primary5h;
            int pct = pool != null ? Math.Max(0, Math.Min(100, pool.Percent)) : 0;
            int remain = 100 - pct;
            Color dotColor = err ? Red : RemainColor(remain);
            var fs = FontsForScale(s); // [0]=FTitle [1]=FNorm [2]=FSmall [3]=FPct

            if (Mini)
            {
                // mini 模式：无圆点、无进度条，仅名称（左）+ 数值（右），单行
                int x0 = SiF(12, s);
                using (var b = new SolidBrush(Text))
                    g.DrawString(st.Name, fs[0], b, x0, y + SiF(3, s));
                if (err)
                {
                    using (var b = new SolidBrush(Dim))
                        g.DrawString("失败", fs[2], b, width - SiF(36, s), y + SiF(6, s));
                }
                else if (pool != null)
                {
                    string rightTxt = pool.IsMoney ? pool.Currency + FmtMoney(pool.Remaining) : (remain + "%");
                    using (var b = new SolidBrush(dotColor))
                    {
                        var psz = g.MeasureString(rightTxt, fs[0]);
                        g.DrawString(rightTxt, fs[0], b, width - psz.Width - SiF(12, s), y + SiF(3, s));
                    }
                }
                return;
            }

            using (var b = new SolidBrush(dotColor))
                g.FillEllipse(b, SiF(12, s), y + SiF(10, s), SiF(9, s), SiF(9, s));

            using (var b = new SolidBrush(Text))
                g.DrawString(st.Name, fs[0], b, SiF(28, s), y + SiF(5, s));

            if (err)
            {
                using (var b = new SolidBrush(Dim))
                    g.DrawString("连接失败", fs[2], b, SiF(28, s), y + SiF(20, s));
            }
            else if (pool != null)
            {
                // 金额模式（DeepSeek 余额）显示金额，否则显示剩余百分比
                string rightTxt = pool.IsMoney ? pool.Currency + FmtMoney(pool.Remaining) : (remain + "%");
                using (var b = new SolidBrush(dotColor))
                {
                    var psz = g.MeasureString(rightTxt, fs[0]);
                    g.DrawString(rightTxt, fs[0], b, width - psz.Width - SiF(12, s), y + SiF(5, s));
                }
                DrawBar(g, new Rectangle(SiF(28, s), y + SiF(29, s), width - SiF(40, s), SiF(4, s)), remain, dotColor);
            }
            else
            {
                using (var b = new SolidBrush(Dim))
                    g.DrawString("无 5 小时窗口数据", fs[2], b, SiF(28, s), y + SiF(20, s));
            }
        }
    }

    // ============================= 分层窗口基类（Per-Pixel Alpha，实现完美抗锯齿圆角+透明） =============================
    internal abstract class LayeredForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const byte AC_SRC_OVER = 0;
        private const byte AC_SRC_ALPHA = 1;
        private const int ULW_ALPHA = 2;

        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern int DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern int DeleteObject(IntPtr obj);

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        private Bitmap _bmp;
        private Graphics _g;

        // 窗口不透明度（0-255），由 UpdateLayeredWindow 的 SourceConstantAlpha 应用（Form.Opacity 对分层窗口无效）
        public byte LayeredAlpha = 255;

        public LayeredForm()
        {
            AutoScaleMode = AutoScaleMode.None; // 自绘窗口，禁用 PerMonitorV2 自动缩放
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        // 用 ARGB 位图驱动窗口显示；圆角外像素 alpha=0 → 真正透明，抗锯齿过渡由系统 alpha 混合，无任何色键残留
        public void UpdateLayered()
        {
            if (IsDisposed || Width <= 0 || Height <= 0) return;
            if (_bmp == null || _bmp.Size != ClientSize)
            {
                if (_g != null) { _g.Dispose(); _g = null; }
                if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
                _bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _g = Graphics.FromImage(_bmp);
            }
            _g.SmoothingMode = SmoothingMode.AntiAlias;
            _g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            _g.Clear(Color.FromArgb(0)); // 全透明清空
            try { OnLayeredPaint(_g); }
            catch { /* 避免绘制异常导致窗口消失 */ }

            // 句柄获取全部纳入 try/finally：GetHbitmap 在 GDI 紧张时可抛 OutOfMemoryException，
            // 若获取步骤在 try 外，异常路径会泄漏 screenDc/memDc；本方法每秒执行，泄漏持续累积
            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                memDc = CreateCompatibleDC(screenDc);
                hBmp = _bmp.GetHbitmap(Color.FromArgb(0));
                oldBmp = SelectObject(memDc, hBmp);
                var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = LayeredAlpha, AlphaFormat = AC_SRC_ALPHA };
                var size = new Size(Width, Height);
                var ptDst = new Point(Left, Top);
                var ptSrc = new Point(0, 0);
                UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBmp != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldBmp);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateLayered();
        }

        // 子类必须实现的绘制：传入的 Graphics 已清为完全透明
        protected abstract void OnLayeredPaint(Graphics g);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_g != null) { _g.Dispose(); _g = null; }
                if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
            }
            base.Dispose(disposing);
        }
    }

    // ============================= 应用上下文 =============================
    internal class AppContext : ApplicationContext
    {
        public Config Cfg;
        public List<ProviderStatus> Providers = new List<ProviderStatus>();
        public FloatingForm Floating;
        public NotifyIcon Tray;
        private SettingsForm _settings;
        private TopBarForm _topBar;
        private bool _exiting;
        internal bool IsExiting { get { return _exiting; } }

        // 懒加载置顶栏（仅在首次切换到 topbar 模式时创建）
        public TopBarForm TopBar
        {
            get
            {
                if (_topBar == null || _topBar.IsDisposed)
                {
                    _topBar = new TopBarForm(this);
                    _topBar.ContextMenuStrip = Floating.ContextMenuStrip;
                    _topBar._detail.ContextMenuStrip = Floating.ContextMenuStrip;
                }
                return _topBar;
            }
        }

        public AppContext()
        {
            Cfg = Config.Load();
            // 上次崩溃/强杀残留的工作区预留：启动时无条件清理（此前仅进入 topbar 模式才清，
            // float 模式启动会让"桌面变矮"的残留一直留着）
            try { TopBarForm.CleanupStartupResidue(); } catch { }
            Floating = new FloatingForm(this);

            Tray = new NotifyIcon
            {
                Icon = IconFactory.Create(),
                Visible = true,
                Text = "AI 额度悬浮窗 v" + AppInfo.Version
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("立即刷新", null, delegate { RefreshData(); });
            var miMini = new ToolStripMenuItem("mini 模式") { Checked = Cfg.MiniMode };
            miMini.Click += delegate
            {
                Cfg.MiniMode = !Cfg.MiniMode;
                miMini.Checked = Cfg.MiniMode;
                Cfg.Save();
                Floating.ApplyFromConfig();
                Floating.Rebuild();
                // 吸附态下切换 mini 会因高度变化导致窗口消失，直接取消吸附贴顶部展开
                Floating.RestoreFromEdge();
            };
            menu.Items.Add(miMini);
            var miLock = new ToolStripMenuItem("固定位置") { Checked = Cfg.LockPosition };
            miLock.Click += delegate
            {
                Cfg.LockPosition = !Cfg.LockPosition;
                miLock.Checked = Cfg.LockPosition;
                if (Cfg.LockPosition) Floating.RestoreFromEdge();
                else Floating.OnLockReleased();
                Cfg.Save();
            };
            menu.Items.Add(miLock);
            menu.Items.Add("设置…", null, delegate { ShowSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            // 打开菜单时同步勾选状态（兼容设置页等外部改动）
            menu.Opening += delegate
            {
                bool isTopbar = Cfg.DisplayMode == "topbar";
                miMini.Checked = Cfg.MiniMode;
                miLock.Checked = Cfg.LockPosition;
                // 置顶栏模式下 mini / 固定位置 无意义，隐藏
                miMini.Visible = !isTopbar;
                miLock.Visible = !isTopbar;
                // 菜单跟随鼠标所在屏的 DPI，保证各屏物理大小一致；
                // DPI 查询失败（旧系统无 shcore）时保持默认字体，不影响功能
                try { menu.Font = St.FontsForScale(St.DpiOf(Screen.FromPoint(Cursor.Position)) / 96f)[1]; }
                catch { }
            };
            Tray.ContextMenuStrip = menu;
            Floating.ContextMenuStrip = menu;
            Floating._detail.ContextMenuStrip = menu;
            Tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !_firstShow)
                {
                    if (Cfg.DisplayMode == "topbar")
                    {
                        if (TopBar.Visible)
                        {
                            TopBar.UnregisterAppBar();
                            TopBar.Hide();
                        }
                        else
                        {
                            TopBar.ApplyFromConfig();
                            MainForm = Floating;
                            TopBar.Show();
                        }
                    }
                    else
                        Floating.Visible = !Floating.Visible;
                }
            };

            // 不在此处设置 MainForm：设置后 Application.Run 会立即显示空白窗口；
            // 仅创建窗口句柄，使后台线程能通过 BeginInvoke 回到 UI 线程
            var h = Floating.Handle;
            RefreshData();
            // 任何退出路径（含未处理异常对话框选"退出"）都兜底恢复工作区；RestoreWorkArea 以
            // _originalLoaded 哨兵保证幂等，正常退出链路已恢复过时此处为空操作
            Application.ApplicationExit += delegate
            {
                if (_topBar != null) { try { _topBar.UnregisterAppBar(); } catch { } }
            };
        }

        public void ShowSettings()
        {
            if (_settings == null || _settings.IsDisposed) _settings = new SettingsForm(this);
            _settings.Show();
            _settings.Activate();
        }

        public void ApplyConfig()
        {
            Cfg.Save();
            Config.SetAutoStart(Cfg.AutoStart);
            ShowActiveForm();
            RefreshData();
        }

        // 根据当前 DisplayMode 显示对应窗口，隐藏另一个；
        // 隐藏的窗口同时停掉刷新/重绘定时器，避免两个模式的定时器同时常驻触发
        private void ShowActiveForm()
        {
            if (Cfg.DisplayMode == "topbar")
            {
                Floating.SuspendTimers();
                if (Floating.Visible) Floating.Hide();
                TopBar.ApplyFromConfig();
                if (!TopBar.Visible) { MainForm = Floating; TopBar.Show(); }
            }
            else
            {
                if (_topBar != null)
                {
                    if (_topBar.Visible) { _topBar.UnregisterAppBar(); _topBar.Hide(); }
                    _topBar.SuspendTimers();
                }
                Floating.ApplyFromConfig();
                if (!Floating.Visible) { MainForm = Floating; Floating.Show(); }
            }
        }

        private int _fetching;
        private bool _firstShow = true;
        private string _lastTrayTip = "";   // 缓存上次托盘文本，只在变化时更新（避免每秒调用 Shell_NotifyIcon）

        public void RefreshData()
        {
            if (Interlocked.CompareExchange(ref _fetching, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                // 并行请求各服务商，避免串行超时叠加（最差 4×15s → 单次 15s）
                var providers = Cfg.Providers;
                var results = new ProviderStatus[providers.Count];
                System.Threading.Tasks.Parallel.For(0, providers.Count, i =>
                {
                    var p = providers[i];
                    if (string.IsNullOrWhiteSpace(p.Key)) return;
                    try
                    {
                        ProviderStatus st;
                        if (p.Type == "mx") st = QuotaService.FetchMiniMax(p);
                        else if (p.Type == "kimi") st = QuotaService.FetchKimi(p);
                        else if (p.Type == "ds") st = QuotaService.FetchDeepSeek(p);
                        else st = QuotaService.FetchGlm(p);
                        results[i] = st;
                    }
                    catch (Exception ex) { results[i] = new ProviderStatus { Name = p.Name, Type = p.Type, Error = ex.Message }; }
                });
                var list = new List<ProviderStatus>();
                foreach (var st in results) if (st != null) list.Add(st);
                if (list.Count == 0)
                    list.Add(new ProviderStatus { Name = "未配置", Error = "右键 → 设置，添加供应商" });

                try
                {
                    Floating.BeginInvoke((Action)delegate
                    {
                        // 回调体自包 try/finally：外层 catch 只覆盖 BeginInvoke 分发本身，
                        // 回调内异常（如 GDI OOM / 退出竞态）若不复位 _fetching，此后所有
                        // 刷新在重入检查处直接 return，自动刷新静默永久失效
                        try
                        {
                            Providers = list;
                            Floating.Rebuild();
                            if (_topBar != null && _topBar.Visible) _topBar.Rebuild();
                            UpdateTrayTip();
                            if (_firstShow) { _firstShow = false; ShowActiveForm(); }
                        }
                        finally { Interlocked.Exchange(ref _fetching, 0); }
                    });
                }
                catch { Interlocked.Exchange(ref _fetching, 0); }
            });
        }

        public void UpdateTrayTip()
        {
            var sb = new StringBuilder();
            foreach (var p in Providers)
            {
                if (sb.Length > 0) sb.Append(" · ");
                if (p.Error != null) sb.Append(p.Name).Append("连接失败");
                else if (p.Primary5h != null)
                {
                    if (p.Primary5h.IsMoney) sb.Append(p.Name).Append(' ').Append(p.Primary5h.Currency).Append(St.FmtMoney(p.Primary5h.Remaining));
                    else sb.Append(p.Name).Append("剩").Append(100 - Math.Max(0, Math.Min(100, p.Primary5h.Percent))).Append('%');
                }
                else sb.Append(p.Name).Append(" --");
            }
            string tip = sb.ToString();
            // 压缩分隔符/措辞后仍超长时，优先保留末尾完整项（最后配置的供应商），
            // 从前往后舍去并加省略号，避免像旧逻辑那样把末尾余额直接截掉
            if (tip.Length > 63)
            {
                string prefix = "…";
                tip = prefix + tip.Substring(tip.Length - 63 + prefix.Length);
            }
            if (tip != _lastTrayTip) { _lastTrayTip = tip; Tray.Text = tip; }
        }

        public void ExitApp()
        {
            // 防重入：Floating.Close() 会再次触发 OnFormClosing 路由回此处
            if (_exiting) return;
            _exiting = true;
            if (_topBar != null) _topBar.UnregisterAppBar();
            Tray.Visible = false;
            Floating.Close();
            ExitThread();
        }
    }

    // ============================= 悬浮窗 =============================
    internal class FloatingForm : LayeredForm
    {
        private readonly AppContext _ctx;
        internal DetailForm _detail;
        private System.Windows.Forms.Timer _uiTimer;
        private System.Windows.Forms.Timer _refreshTimer;
        private System.Windows.Forms.Timer _hideTimer;
        private System.Windows.Forms.Timer _showTimer;
        private bool _dragging;
        private Point _dragOffset;

        // 贴边隐藏（拖拽到屏幕顶部收缩，类似 QQ）——始终启用
        private const int EdgeThreshold = 6;    // 鼠标距屏幕顶部多少像素内触发（DIP，运行时按 DPI 缩放）
        private const int EdgePeek = 3;         // 收缩时露出的像素（DIP，运行时按 DPI 缩放）
        private bool _edgeDocked;               // 吸附态：窗口固定在顶部，鼠标进出控制展开/收缩
        private bool _edgeCollapsed;            // 吸附态下是否当前收缩
        private bool _edgeArmed;                // 拖拽中鼠标已接近顶部，已显示虚影
        private bool _edgeJustUndocked;         // 刚从吸附态拖出，需鼠标离开顶部才能重新吸附
        private int _edgeTargetX;               // 吸附目标 X
        private EdgeGhostForm _ghost;           // 贴边虚影预览
        private System.Windows.Forms.Timer _animTimer;
        private Point _animTarget;

        // 行高与窗口宽度（DIP 基准值，运行时按所在屏 DPI 缩放）
        private const int RowHFull = 38;
        private const int RowHMini = 26;
        private const int PadY = 6;
        private const int WidthFull = 236;
        private const int WidthMini = 180;
        // 当前所在屏缩放因子（跨屏拖动时更新，保证各屏物理尺寸一致）
        private float _scale = St.Scale;
        private int RowH { get { return St.SiF(St.Mini ? RowHMini : RowHFull, _scale); } }
        private int WinWidth { get { return St.SiF(St.Mini ? WidthMini : WidthFull, _scale); } }
        private int PadYS { get { return St.SiF(PadY, _scale); } }

        // 更新为所在屏缩放（并同步详情面板 DPI）
        private void UpdateScale()
        {
            try
            {
                var s = Screen.FromControl(this);
                float n = St.DpiOf(s) / 96f;
                if (Math.Abs(n - _scale) > 0.01f)
                {
                    _scale = n;
                    _detail.DpiScale = n;
                    Width = WinWidth;
                    Rebuild();
                }
            }
            catch (Exception ex)
            {
                // DPI 查询/缩放失败时保持原尺寸，下次拖动或定时刷新会重试，不影响主功能
                System.Diagnostics.Debug.WriteLine("UpdateScale 失败: " + ex.Message);
            }
        }

        public FloatingForm(AppContext ctx)
        {
            _ctx = ctx;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Width = WinWidth;

            _detail = new DetailForm(ctx);
            _detail.DpiScale = St.Scale;
            _detail.RequestCancelHide = CancelHide;
            _detail.RequestArmHide = ArmHide;
            _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiTimer.Tick += delegate
            {
                UpdateLayered();
                if (_detail.Visible) _detail.UpdateLayered();
                _ctx.UpdateTrayTip();
            };
            _uiTimer.Start();

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Tick += delegate { _ctx.RefreshData(); };

            _hideTimer = new System.Windows.Forms.Timer { Interval = 350 };
            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                if (!Bounds.Contains(Cursor.Position) && !_detail.Bounds.Contains(Cursor.Position))
                    _detail.Hide();
            };

            _showTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _showTimer.Tick += delegate
            {
                _showTimer.Stop();
                if (Bounds.Contains(Cursor.Position)) ShowDetail();
            };

            ApplyFromConfig();
        }

        public void ApplyFromConfig()
        {
            var cfg = _ctx.Cfg;
            // mini 模式（仅主悬浮窗）：隐藏圆点/进度条，字号保持原样
            St.Mini = cfg.MiniMode;
            UpdateScale();
            Width = WinWidth;
            TopMost = cfg.TopMost;
            _detail.TopMost = cfg.TopMost;

            // 主题与不透明度应用到两个分层窗口并立即重绘
            St.Theme = cfg.Theme == "dark" ? 1 : 2;
            int pct = Math.Max(50, Math.Min(100, cfg.OpacityPct));
            LayeredAlpha = (byte)(255 * pct / 100);
            _detail.LayeredAlpha = LayeredAlpha;
            UpdateLayered();
            if (_detail.Visible) _detail.UpdateLayered();

            _refreshTimer.Stop();
            _refreshTimer.Interval = Math.Max(15, cfg.RefreshSec) * 1000;
            _refreshTimer.Start();
            // 从置顶栏模式切回时 _uiTimer 处于停止态（SuspendTimers 所停），必须在此重启；
            // 构造函数里的 Start() 只执行一次，缺了这行倒计时/托盘 tip 将永久冻结
            _uiTimer.Start();

            if (St.IsPointOnScreen(new Point(cfg.X, cfg.Y)))
            {
                Location = new Point(cfg.X, cfg.Y);
            }
            else
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 24, wa.Top + 24);
            }
        }

        // 切到置顶栏模式后本窗口隐藏，停止刷新/重绘定时器，避免两个模式的定时器
        // 同时常驻触发（此前仅靠 _fetching 防重入兜底）；托盘 tip 改由 TopBarForm 的 _uiTimer 接管
        public void SuspendTimers()
        {
            _uiTimer.Stop();
            _refreshTimer.Stop();
        }

        // Alt+F4 / WM_CLOSE 走正规退出流程（清理托盘图标与工作区），否则消息循环直接结束、
        // ExitApp 被跳过，托盘留下幽灵图标；ExitApp 内部通过 IsExiting 防止二次路由
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.CloseReason == CloseReason.UserClosing && !_ctx.IsExiting)
            {
                e.Cancel = true;
                _ctx.ExitApp();
            }
        }

        public void Rebuild()
        {
            Height = PadYS * 2 + _ctx.Providers.Count * RowH;
            UpdateLayered();
            if (_detail.Visible) ShowDetail();
        }

        protected override void OnLayeredPaint(Graphics g)
        {
            // 圆角背景：圆角外像素 alpha=0 → 完全透明；抗锯齿过渡由系统 alpha 混合，无色键残留
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), St.SiF(12, _scale)))
            {
                using (var brush = new SolidBrush(St.Bg))
                    g.FillPath(brush, path);
                // 最外圈 1px 细线，浅色主题用深线、深色主题用浅线，保证在任何壁纸上都有轮廓
                using (var pen = new Pen(St.Border, 1f))
                    g.DrawPath(pen, path);
            }

            int y = PadYS;
            foreach (var st in _ctx.Providers)
            {
                St.DrawRow(g, st, y, Width, _scale);
                y += RowH;
            }
        }

        // 拖动（窗口自绘后直接由窗口接收鼠标事件）
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _ctx.Cfg.LockPosition) return;
            // 停掉展开/收缩动画，否则 timer 会把窗口拉回原位
            if (_animTimer != null) _animTimer.Stop();
            _dragging = true; _dragOffset = e.Location;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            // 首次实际移动才退出吸附态：纯点击不动不会脱离吸附
            if (_edgeDocked) { _edgeDocked = false; _edgeJustUndocked = true; }
            Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
            // 贴边检测：以鼠标(屏幕坐标)是否接近当前屏幕顶部为准
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            bool nearTop = Cursor.Position.Y <= wa.Top + St.SiF(EdgeThreshold, _scale);
            // 从吸附态拖出后，鼠标须先离开顶部阈值一次，才能在新位置重新吸附
            if (_edgeJustUndocked)
            {
                if (nearTop) return;
                _edgeJustUndocked = false;
            }
            if (nearTop && !_edgeArmed)
            {
                _edgeArmed = true;
                _edgeTargetX = Math.Max(wa.Left, Math.Min(wa.Right - Width, Left));
                ShowGhost(wa);
            }
            else if (!nearTop && _edgeArmed)
            {
                _edgeArmed = false;
                HideGhost();
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            _edgeJustUndocked = false;
            if (_edgeArmed)
            {
                // 松手时仍在顶部 → 执行吸附收缩
                _edgeArmed = false;
                HideGhost();
                CommitEdgeHide();
            }
            else
            {
                _ctx.Cfg.X = Left; _ctx.Cfg.Y = Top; _ctx.Cfg.Save();
            }
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); _ctx.RefreshData(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); OnEnterAny(this, e); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); OnLeaveAny(this, e); }

        // 分层窗口移动后需重新提交位图，否则跨屏拖动时画面会"停留在旧位置"导致看不见；同时让详情弹窗跟随移动
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!IsHandleCreated) return;
            UpdateScale();
            UpdateLayered();
            if (_detail.Visible) ShowDetail();
        }

        private void OnEnterAny(object s, EventArgs e)
        {
            _hideTimer.Stop(); _showTimer.Stop();
            if (_edgeDocked)
            {
                EdgeExpand();
                // 展开后正常允许唤出详情面板（鼠标仍在窗口上时计时）
                _showTimer.Start();
                return;
            }
            _showTimer.Start();
        }
        private void OnLeaveAny(object s, EventArgs e)
        {
            _showTimer.Stop(); _hideTimer.Stop();
            if (_edgeDocked) { EdgeCollapse(); return; }  // 吸附态：滑回收起
            _hideTimer.Start();
        }

        // ---- 贴边隐藏逻辑 ----
        // 显示贴边虚影预览（松手将吸附到的展开位置）
        private void ShowGhost(Rectangle wa)
        {
            if (_ghost == null)
            {
                _ghost = new EdgeGhostForm();
                _ghost.TopMost = TopMost;
            }
            _ghost.Width = ClientSize.Width;
            _ghost.Height = ClientSize.Height;
            _ghost.Location = new Point(_edgeTargetX, wa.Top);
            // 必须无条件刷新：复用的 ghost 已有 handle，Show() 不会再触发 OnHandleCreated
            if (!_ghost.Visible) _ghost.Show();
            _ghost.UpdateLayered();
        }

        private void HideGhost()
        {
            if (_ghost != null && _ghost.Visible) _ghost.Hide();
        }

        // 松手吸附：记录展开位置(供下次启动/恢复)，滑入收缩态
        private void CommitEdgeHide()
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            _edgeTargetX = Math.Max(wa.Left, Math.Min(wa.Right - Width, _edgeTargetX));
            _ctx.Cfg.X = _edgeTargetX; _ctx.Cfg.Y = wa.Top; _ctx.Cfg.Save();
            _edgeDocked = true;
            _edgeCollapsed = true;
            AnimateTo(new Point(_edgeTargetX, wa.Top - Height + St.SiF(EdgePeek, _scale)));
        }

        private void EdgeExpand()
        {
            if (!_edgeCollapsed) return;
            var wa = Screen.FromControl(this).WorkingArea;
            _edgeCollapsed = false;
            AnimateTo(new Point(_edgeTargetX, wa.Top));
        }

        private void EdgeCollapse()
        {
            if (_edgeCollapsed) return;
            var wa = Screen.FromControl(this).WorkingArea;
            _edgeCollapsed = true;
            AnimateTo(new Point(_edgeTargetX, wa.Top - Height + St.SiF(EdgePeek, _scale)));
        }

        // 锁定位置开启时调用：停动画、清吸附态、窗口靠顶部展开固定
        public void RestoreFromEdge()
        {
            if (_animTimer != null) _animTimer.Stop();   // 防止收缩动画把位置改回去
            _edgeArmed = false;
            _edgeJustUndocked = false;
            HideGhost();
            if (_edgeDocked)
            {
                var wa = Screen.FromControl(this).WorkingArea;
                _edgeDocked = false;
                _edgeCollapsed = false;
                Location = new Point(_edgeTargetX, wa.Top);
                _ctx.Cfg.X = _edgeTargetX; _ctx.Cfg.Y = wa.Top; _ctx.Cfg.Save();
            }
        }

        // 取消固定位置时调用：若窗口仍停在顶部，给一次吸附豁免，避免一拖就吸回去
        public void OnLockReleased()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            if (Top <= wa.Top + St.SiF(EdgeThreshold, _scale)) _edgeJustUndocked = true;
        }

        // 简单线性滑动动画：每 tick 移动距目标的 1/3，直到贴近视为完成
        private void AnimateTo(Point target)
        {
            _animTarget = target;
            if (_animTimer == null)
            {
                _animTimer = new System.Windows.Forms.Timer { Interval = 15 };
                _animTimer.Tick += delegate
                {
                    int dx = _animTarget.X - Left;
                    int dy = _animTarget.Y - Top;
                    if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1)
                    {
                        Location = _animTarget;
                        _animTimer.Stop();
                        return;
                    }
                    Location = new Point(Left + AnimStep(dx), Top + AnimStep(dy));
                };
            }
            _animTimer.Start();
        }

        // 动画步长：每 tick 前进距目标的 1/3；整数除法在残距 <3px 时步长为 0（如 dx=2 时
        // 2/3=0），窗口将永远停在距目标 2px 处且定时器空转，此时按方向退化为 ±1 保证收敛
        private static int AnimStep(int d)
        {
            int s = d / 3;
            return s == 0 && d != 0 ? Math.Sign(d) : s;
        }

        private void ShowDetail()
        {
            if (_ctx.Providers.Count == 0) return;
            _detail.UpdateData();
            var wa = Screen.FromControl(this).WorkingArea;
            int x = Left;
            if (x + _detail.Width > wa.Right) x = wa.Right - _detail.Width - 8;
            if (x < wa.Left) x = wa.Left + 8;
            int y = Bottom + 8;
            if (y + _detail.Height > wa.Bottom) y = Top - _detail.Height - 8;
            _detail.Location = new Point(x, y);
            if (!_detail.Visible) _detail.Show();
        }

        public void ArmHide() { _hideTimer.Stop(); _hideTimer.Start(); }
        public void CancelHide() { _hideTimer.Stop(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_uiTimer != null) _uiTimer.Dispose();
                if (_refreshTimer != null) _refreshTimer.Dispose();
                if (_hideTimer != null) _hideTimer.Dispose();
                if (_showTimer != null) _showTimer.Dispose();
                if (_animTimer != null) _animTimer.Dispose();
                if (_detail != null) _detail.Dispose();
                if (_ghost != null) _ghost.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ============================= 贴边虚影预览 =============================
    internal class EdgeGhostForm : LayeredForm
    {
        public EdgeGhostForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }

        protected override void OnLayeredPaint(Graphics g)
        {
            // 半透明绿色描边 + 极淡绿色填充，提示「松手将吸附到这里」
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 12))
            {
                using (var brush = new SolidBrush(Color.FromArgb(36, St.Green.R, St.Green.G, St.Green.B)))
                    g.FillPath(brush, path);
                using (var pen = new Pen(St.Green, 2.5f))
                    g.DrawPath(pen, path);
            }
        }
    }

    // ============================= 置顶栏拖拽虚影预览 =============================
    internal class AppBarGhostForm : LayeredForm
    {
        public AppBarGhostForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override void OnLayeredPaint(Graphics g)
        {
            // 半透明绿色描边 + 极淡填充，提示「松手将停靠到此屏幕顶部」
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 6))
            {
                using (var brush = new SolidBrush(Color.FromArgb(36, St.Green.R, St.Green.G, St.Green.B)))
                    g.FillPath(brush, path);
                using (var pen = new Pen(St.Green, 2.5f))
                    g.DrawPath(pen, path);
            }
        }
    }

    // ============================= 详情面板 =============================
    internal class DetailForm : LayeredForm
    {
        private readonly AppContext _ctx;
        private const int BaseW = 380;

        // 由宿主窗口（FloatingForm / TopBarForm）设置，解耦对具体窗口类型的依赖
        public Action RequestCancelHide;
        public Action RequestArmHide;
        // DPI 缩放因子（由宿主窗口设置，默认跟主屏一致）
        public float DpiScale { get { return _dpi; } set { _dpi = value; Width = Si(BaseW); } }
        private float _dpi = 1f;
        private int Si(int v) { return (int)Math.Round(v * _dpi); }

        public DetailForm(AppContext ctx)
        {
            _ctx = ctx;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Width = Si(BaseW);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (RequestCancelHide != null) RequestCancelHide();
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (RequestArmHide != null) RequestArmHide();
        }
        // 与悬浮窗一致，重新定位后刷新分层位图
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (IsHandleCreated) UpdateLayered();
        }

        private int MeasureHeight()
        {
            int h = Si(14);
            foreach (var p in _ctx.Providers)
            {
                h += Si(26); // 标题
                if (p.Error != null) h += Si(34);
                else
                {
                    foreach (var pool in p.Pools)
                        h += pool.IsMoney ? Si(22) : Si(45);
                }
                h += Si(10);
            }
            return h;
        }

        public void UpdateData()
        {
            Height = MeasureHeight();
            UpdateLayered();
        }

        protected override void OnLayeredPaint(Graphics g)
        {
            var fs = St.FontsForScale(_dpi); // [0]=FTitle [1]=FNorm [2]=FSmall [3]=FPct
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Si(12)))
            {
                using (var brush = new SolidBrush(St.Card))
                    g.FillPath(brush, path);
                using (var pen = new Pen(St.Border, 1f))
                    g.DrawPath(pen, path);
            }

            int y = Si(14);
            foreach (var p in _ctx.Providers)
            {
                using (var b = new SolidBrush(St.AccentFor(p.Type)))
                    g.FillEllipse(b, Si(16), y + Si(6), Si(9), Si(9));
                using (var b = new SolidBrush(St.Text))
                    g.DrawString(p.Name, fs[0], b, Si(32), y + Si(2));
                string right = string.IsNullOrEmpty(p.Level) ? "" : p.Level;
                if (!string.IsNullOrEmpty(right))
                {
                    var sz = g.MeasureString(right, fs[2]);
                    using (var b = new SolidBrush(St.AccentFor(p.Type)))
                    using (var path = St.RoundRect(new Rectangle(Width - (int)sz.Width - Si(28), y + Si(2), (int)sz.Width + Si(12), Si(17)), Si(8)))
                        g.FillPath(b, path);
                    using (var b = new SolidBrush(Color.White))
                        g.DrawString(right, fs[2], b, Width - sz.Width - Si(22), y + Si(4));
                }
                y += Si(26);

                if (p.Error != null)
                {
                    string msg = p.Error;
                    if (msg.Length > 46) msg = msg.Substring(0, 46) + "…";
                    using (var b = new SolidBrush(St.Red))
                        g.DrawString("⚠ " + msg, fs[2], b, Si(32), y + Si(4));
                    y += Si(34);
                }
                else
                {
                    foreach (var pool in p.Pools)
                    {
                        if (pool.IsMoney)
                        {
                            using (var b = new SolidBrush(St.Text))
                                g.DrawString(pool.Label, fs[1], b, Si(32), y);
                            string moneyTxt = pool.Currency + St.FmtMoney(pool.Remaining);
                            var msz = g.MeasureString(moneyTxt, fs[1]);
                            using (var b = new SolidBrush(St.Text))
                                g.DrawString(moneyTxt, fs[1], b, Width - msz.Width - Si(18), y);
                            y += Si(22);
                            continue;
                        }
                        int pct = Math.Max(0, Math.Min(100, pool.Percent));
                        int remain = 100 - pct;
                        Color c = St.RemainColor(remain);
                        using (var b = new SolidBrush(St.Text))
                            g.DrawString(pool.Label, fs[1], b, Si(32), y);
                        string pctTxt = remain + "%";
                        var psz = g.MeasureString(pctTxt, fs[1]);
                        using (var b = new SolidBrush(c))
                            g.DrawString(pctTxt, fs[1], b, Width - psz.Width - Si(18), y);
                        y += Si(19);
                        St.DrawBar(g, new Rectangle(Si(32), y, Width - Si(50), Si(6)), remain, c);
                        y += Si(11);
                        string nums = pool.PercentOnly
                            ? "剩余 " + St.FmtNum(pool.Remaining) + "%" + (string.IsNullOrEmpty(pool.Note) ? "" : "　" + pool.Note)
                            : "已用 " + St.FmtNum(pool.Used) + " / " + St.FmtNum(pool.Total) + "　剩余 " + St.FmtNum(pool.Remaining);
                        using (var b = new SolidBrush(St.Dim))
                            g.DrawString(nums, fs[2], b, Si(32), y);
                        if (pool.ResetLocal.HasValue)
                        {
                            string cd = "重置 " + St.FmtCountdown(pool.ResetLocal, pool.ResetEstimated);
                            var csz = g.MeasureString(cd, fs[2]);
                            using (var b = new SolidBrush(St.Dim))
                                g.DrawString(cd, fs[2], b, Width - csz.Width - Si(18), y);
                        }
                        y += Si(15);
                    }
                }
                y += Si(10);
            }
        }
    }

    // ============================= 置顶栏（普通置顶窗口 + 手动工作区预留） =============================
    // 不使用 SHAppBarMessage（混合 DPI 下会导致系统疯狂广播 ABN_POSCHANGED → 卡顿）。
    // 改用普通 TopMost 窗口 + SystemParametersInfo(SPI_SETWORKAREA) 手动预留工作区。
    // SPI_SETWORKAREA 只修改系统工作区度量，不触发 AppBar 消息循环，不会卡顿。
    internal class TopBarForm : Form
    {
        private readonly AppContext _ctx;
        internal DetailForm _detail;
        private System.Windows.Forms.Timer _uiTimer;
        private System.Windows.Forms.Timer _refreshTimer;
        private System.Windows.Forms.Timer _hideTimer;
        private System.Windows.Forms.Timer _showTimer;

        // ---- 工作区预留 P/Invoke ----
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool SystemParametersInfo(int uAction, int uParam, ref RECT pvParam, int fuWinIni);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        // 窗口枚举与重排（工作区增大时最大化窗口不会自动恢复，需手动触发）
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const int SPI_SETWORKAREA = 47;
        private const int WM_SETTINGCHANGE = 0x001A;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // 任务栏位置查询（用于净化疑似残留的工作区：区分顶部留白来自任务栏还是本程序 bar 残留）
        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge;
            public RECT rc; public int lParam;
        }
        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);
        private const int ABM_GETTASKBARPOS = 5;

        private int _currentScreen = -1;
        // 系统原始工作区（不含本程序 bar 预留，但含任务栏预留）：用于计算预留后的真实可用区，避免最大化窗口被任务栏遮挡
        private static List<Rectangle> _originalWorkAreas;
        private static bool _originalLoaded;
        private bool _startupCleaned;               // 启动时是否已完成崩溃残留清理

        // ---- 拖拽 ----
        private bool _dragging;
        private AppBarGhostForm _ghost;
        private int _ghostScreen = -1;

        private const int BaseBarHeight = 38;   // DIP 基准高度（96DPI 下）
        private int _barHeight = BaseBarHeight;    // 当前屏幕缩放后的实际像素高度
        // 当前屏缩放因子（DPI/96）：直接保存，避免由 int 截断的 _barHeight 反推产生精度误差（如 125% 屏反推得 1.2368 而非 1.25）
        private float _scale = 1f;

        // 获取目标屏幕的 DPI（复用 St 公共实现）
        private static int GetScreenDpi(Screen screen) { return St.DpiOf(screen); }

        public TopBarForm(AppContext ctx)
        {
            _ctx = ctx;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Height = _barHeight;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;

            // 双缓冲自绘，避免闪烁
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();

            // 首次显示后强制修正高度（WinForms 首次 Show 时可能因非客户区/自动缩放导致高度偏大）
            Shown += delegate
            {
                if (Height != _barHeight)
                {
                    Height = _barHeight;
                    // 高度变了，重新预留工作区以匹配
                    if (_currentScreen >= 0) ReserveWorkArea(_currentScreen);
                }
            };

            _detail = new DetailForm(ctx);
            _detail.DpiScale = St.Scale;
            _detail.RequestCancelHide = CancelHide;
            _detail.RequestArmHide = ArmHide;

            _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiTimer.Tick += delegate
            {
                Invalidate();
                if (_detail.Visible) _detail.UpdateLayered();
                _ctx.UpdateTrayTip();
            };

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Tick += delegate { _ctx.RefreshData(); };

            _hideTimer = new System.Windows.Forms.Timer { Interval = 350 };
            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                if (!Bounds.Contains(Cursor.Position) && !_detail.Bounds.Contains(Cursor.Position))
                    _detail.Hide();
            };

            _showTimer = new System.Windows.Forms.Timer { Interval = 800 };
            _showTimer.Tick += delegate
            {
                _showTimer.Stop();
                if (Bounds.Contains(Cursor.Position)) ShowDetail();
            };
        }

        // ---- 辅助 ----
        private Screen GetScreen(int index)
        {
            var screens = Screen.AllScreens;
            if (screens.Length == 0) return Screen.PrimaryScreen;
            if (index < 0 || index >= screens.Length) return screens[0];
            return screens[index];
        }

        private static int FindScreenIndex(Point pt)
        {
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
                if (screens[i].Bounds.Contains(pt)) return i;
            return 0;
        }

        // 预留工作区：把目标屏的工作区顶部下移 BarHeight。
        // SPI_SETWORKAREA 是每屏独立的：传入的 RECT 必须位于目标屏幕区域内。
        // 关键发现（来自微软文档 + ExplorerPatcher/AutoIt 实践）：
        //   - 设小工作区时系统自动重排最大化窗口（缩小）；
        //   - 设大工作区时最大化窗口不会自动恢复（增大），必须手动 SetWindowPos 触发。
        //   - 多屏下传虚拟桌面并集是错误的，必须传目标屏自己的 Bounds。
        private static string WorkAreaFile { get { return Path.Combine(Path.GetTempPath(), "QuotaWidget-workarea.txt"); } }

        // 加载系统原始工作区（含任务栏预留、不含本程序 bar 预留）：
        // - 首次运行（无持久化文件）：以当前各屏 WorkingArea 作为原始值并持久化；
        // - 崩溃恢复（文件存在）：解析文件中保存的原始值，避免读到被本程序修改过的工作区。
        private static List<Rectangle> LoadOriginalWorkAreas()
        {
            if (_originalLoaded) return _originalWorkAreas;
            _originalLoaded = true;

            var screens = Screen.AllScreens;
            bool fromFile = false, fileExists = false;
            try
            {
                if (File.Exists(WorkAreaFile))
                {
                    fileExists = true;   // 标记疑似上次崩溃残留（正常退出会删除该文件）
                    var parts = File.ReadAllText(WorkAreaFile).Trim().Split('|');
                    var list = new List<Rectangle>();
                    foreach (var p in parts)
                    {
                        var c = p.Split(',');
                        int l, t, r, b;
                        if (c.Length == 4 && int.TryParse(c[0], out l) && int.TryParse(c[1], out t) && int.TryParse(c[2], out r) && int.TryParse(c[3], out b))
                            list.Add(Rectangle.FromLTRB(l, t, r, b));
                        else { list = null; break; }
                    }
                    if (list != null && list.Count == screens.Length)
                    {
                        // 校验存储的工作区是否仍落在对应屏幕的 Bounds 内（分辨率变更后旧记录无效）
                        bool valid = true;
                        for (int i = 0; i < screens.Length && i < list.Count; i++)
                        {
                            var b = screens[i].Bounds;
                            if (list[i].Left < b.Left || list[i].Top < b.Top ||
                                list[i].Right > b.Right || list[i].Bottom > b.Bottom)
                            { valid = false; break; }
                        }
                        if (valid) { _originalWorkAreas = list; fromFile = true; }
                    }
                }
            }
            catch { }

            if (!fromFile)
            {
                // 文件存在说明上次未正常退出，当前 WorkingArea 可能仍残留本程序的 bar 预留；
                // 屏数/分辨率变化导致文件整体校验失败时尤甚——直接采集会把污染值当"原始值"
                // 持久化，退出恢复后屏幕顶部将永久损失一条 bar 高度，采集前先净化
                bool sanitize = fileExists;
                _originalWorkAreas = new List<Rectangle>();
                foreach (var s in screens) _originalWorkAreas.Add(sanitize ? SanitizeOriginalArea(s) : s.WorkingArea);
                // 采集后持久化；从文件成功加载时不回写，消除冗余 I/O。
                SaveOriginalWorkAreas();
            }
            return _originalWorkAreas;
        }

        private static void SaveOriginalWorkAreas()
        {
            if (_originalWorkAreas == null) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var r in _originalWorkAreas)
                    sb.Append(r.Left).Append(',').Append(r.Top).Append(',').Append(r.Right).Append(',').Append(r.Bottom).Append('|');
                File.WriteAllText(WorkAreaFile, sb.ToString().TrimEnd('|'));
            }
            catch { }
        }

        // 运行中热插显示器：屏数变化时重采集快照。旧屏沿用已保存的原始值（按 Bounds 包含
        // 关系匹配，容忍屏幕顺序变化），新屏按当前 WorkingArea 入册（本程序从未预留过该屏，
        // 当前值即原始值）
        private static void RefreshOriginalWorkAreasForScreenChange()
        {
            if (!_originalLoaded) return;
            var screens = Screen.AllScreens;
            if (screens.Length == _originalWorkAreas.Count) return;
            var used = new bool[_originalWorkAreas.Count];
            var list = new List<Rectangle>();
            foreach (var s in screens)
            {
                int hit = -1;
                for (int i = 0; i < _originalWorkAreas.Count; i++)
                {
                    if (used[i]) continue;
                    var b = s.Bounds;
                    var r = _originalWorkAreas[i];
                    if (r.Left >= b.Left && r.Top >= b.Top && r.Right <= b.Right && r.Bottom <= b.Bottom) { hit = i; break; }
                }
                if (hit >= 0) { used[hit] = true; list.Add(_originalWorkAreas[hit]); }
                // 未命中兜底走净化而非直接取 WorkingArea：同屏重应用时该屏可能仍带着本程序
                // 的 bar 预留，直接采集会把预留态烙进"原始值"，退出后顶部永久缺一条 bar 高度
                else list.Add(SanitizeOriginalArea(s));
            }
            _originalWorkAreas = list;
            SaveOriginalWorkAreas();
        }

        // 启动时无条件清理上次崩溃/强杀残留的 bar 预留（残留文件存在才动作，正常退出无文件则空操作）；
        // 此前仅进入 topbar 模式才清，float 模式启动会永远留着"桌面变矮"的残留
        public static void CleanupStartupResidue()
        {
            if (!File.Exists(WorkAreaFile)) return;
            LoadOriginalWorkAreas();
            foreach (var s in Screen.AllScreens)
                ResetWorkAreaForScreen(s);
            try { File.Delete(WorkAreaFile); } catch { }
            _originalWorkAreas = null;
            _originalLoaded = false;
        }

        // 净化疑似残留的工作区：顶部留白超出任务栏高度的部分视为上次 bar 预留残留，
        // 将 Top 修回 Bounds.Top + 任务栏在该屏顶部的占位（任务栏不在该屏顶部时记 0）。
        // 仅在检测到上次异常退出（WorkAreaFile 存在）时调用，正常运行不修正，
        // 避免误伤其他 appbar 类工具（Dock 等）的合法预留。
        private static Rectangle SanitizeOriginalArea(Screen s)
        {
            var wa = s.WorkingArea;
            int taskbarTop = 0;
            try
            {
                var abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
                if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
                {
                    var tb = Rectangle.FromLTRB(abd.rc.Left, abd.rc.Top, abd.rc.Right, abd.rc.Bottom);
                    // 水平任务栏（宽>高）且贴住该屏顶边才算顶部占位
                    if (tb.Width > tb.Height && tb.Top <= s.Bounds.Top && tb.IntersectsWith(s.Bounds))
                        taskbarTop = tb.Bottom - s.Bounds.Top;
                }
            }
            catch { }
            int expectedTop = s.Bounds.Top + taskbarTop;
            if (wa.Top > expectedTop)
                wa = Rectangle.FromLTRB(wa.Left, expectedTop, wa.Right, wa.Bottom);
            return wa;
        }

        private int _reservedScreen = -1;

        // 重排指定屏幕上的所有最大化窗口到目标工作区（工作区变化后系统不自动调整已最大化窗口）
        // targetWorkArea: 窗口应适配的最终工作区矩形（恢复后的原始区 或 缩小后的预留区）
        private static void ReflowMaximizedWindows(Screen screen, Rectangle targetWorkArea)
        {
            EnumWindows(delegate(IntPtr hWnd, IntPtr lp)
            {
                if (!IsWindowVisible(hWnd) || !IsZoomed(hWnd)) return true;
                // 只处理目标屏上的窗口
                var hmon = MonitorFromWindow(hWnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
                var screenHmon = MonitorFromPoint(
                    new Point(screen.Bounds.Left + screen.Bounds.Width / 2, screen.Bounds.Top + 1), 2);
                if (hmon != screenHmon) return true;
                // 跳过自己（置顶栏窗口）
                if (hWnd == lp) return true;
                // 主动设置最大化窗口到目标工作区的完整区域（去掉 SWP_NOSIZE / SWP_NOMOVE）。
                // 仅用 SWP_FRAMECHANGED + 不变坐标，在资源管理器等部分窗口上无效——
                // 它们收到 WM_SIZE 时 RECT 未变就不会重查工作区，顶部会卡在旧偏移。
                // 直接设位强制更新最大化边界，所有窗口（含 Explorer）都能正确贴满。
                SetWindowPos(hWnd, IntPtr.Zero,
                    targetWorkArea.Left, targetWorkArea.Top,
                    targetWorkArea.Width, targetWorkArea.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                return true;
            }, IntPtr.Zero);
        }

        // 恢复目标屏的【原始】工作区（含任务栏预留），并手动重排最大化窗口。
        // Per-Monitor DPI 感知下坐标已统一为物理像素，广播 WM_SETTINGCHANGE 会让 Explorer
        // 正确地将桌面图标移入可用工作区（仅顶部图标下移，其余不动）。
        private static void ResetWorkAreaForScreen(Screen screen)
        {
            var orig = LoadOriginalWorkAreas();
            var screens = Screen.AllScreens;
            int idx = -1;
            for (int i = 0; i < screens.Length; i++)
                if (screens[i].Bounds == screen.Bounds) { idx = i; break; }
            // 索引未命中（分辨率/屏数变化导致快照错位）时跳过恢复：
            // 用全屏 Bounds 兜底会抹掉任务栏预留，直到 explorer 重算才恢复
            if (idx < 0 || idx >= orig.Count) return;
            var r = orig[idx];
            var full = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref full, 0);
            ReflowMaximizedWindows(screen, r);
            PostMessage(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
            var progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
                PostMessage(progman, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
        }

        private void ReserveWorkArea(int screenIndex)
        {
            var bounds = GetScreen(screenIndex).Bounds;
            var orig = LoadOriginalWorkAreas();
            var origRect = (screenIndex >= 0 && screenIndex < orig.Count) ? orig[screenIndex] : bounds;

            // 基于原始工作区（含任务栏预留）收缩而非全屏 Bounds：任务栏贴顶/贴左/贴右时，
            // Left/Top/Right 保持任务栏占位，仅顶部为 bar 下压，预留结果不吞任务栏区域
            var newRect = new RECT
            {
                Left = origRect.Left,
                Top = origRect.Top + _barHeight,
                Right = origRect.Right,
                Bottom = origRect.Bottom
            };

            SystemParametersInfo(SPI_SETWORKAREA, 0, ref newRect, 0);
            var shrunk = Rectangle.FromLTRB(newRect.Left, newRect.Top, newRect.Right, newRect.Bottom);
            ReflowMaximizedWindows(GetScreen(screenIndex), shrunk);
            // 广播工作区变更，让 Explorer 正确调整桌面图标（仅顶部图标下移）
            PostMessage(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
            var progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
                PostMessage(progman, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
            _reservedScreen = screenIndex;
        }

        // 切屏时清除旧屏预留
        private void ClearWorkArea(int screenIndex)
        {
            ResetWorkAreaForScreen(GetScreen(screenIndex));
        }

        // 退出时恢复所有屏到原始工作区
        private void RestoreWorkArea()
        {
            if (!_originalLoaded) return;
            foreach (var s in Screen.AllScreens)
                ResetWorkAreaForScreen(s);
            try { File.Delete(WorkAreaFile); } catch { }
            _originalWorkAreas = null;
            _originalLoaded = false;
            _reservedScreen = -1;
        }

        // 把置顶栏贴到目标屏顶部，并预留工作区
        private void ApplyToScreen(int screenIndex)
        {
            // 切屏时先清除旧屏的预留（恢复旧屏原始工作区）
            if (_reservedScreen >= 0 && _reservedScreen != screenIndex)
                ClearWorkArea(_reservedScreen);

            // 运行中热插显示器：屏数变化时重采集快照，新增屏按当前工作区入册，避免索引错位
            RefreshOriginalWorkAreasForScreenChange();

            // 首次应用：加载/持久化原始工作区；若检测到上次崩溃残留（文件已存在），先恢复所有屏到原始值
            if (!_startupCleaned)
            {
                _startupCleaned = true;
                bool crashed = File.Exists(WorkAreaFile);
                LoadOriginalWorkAreas();
                if (crashed)
                {
                    foreach (var s in Screen.AllScreens)
                        ResetWorkAreaForScreen(s);
                }
            }

            var screen = GetScreen(screenIndex);
            _scale = GetScreenDpi(screen) / 96f;
            _barHeight = (int)(BaseBarHeight * _scale); // 与原实现一致：int 截断（125% 屏为 47px）
            // 位置与宽度基于原始工作区（含任务栏预留）：任务栏贴顶/贴左时置于任务栏内侧，不遮挡任务栏
            var origAreas = LoadOriginalWorkAreas();
            var origRect = (screenIndex >= 0 && screenIndex < origAreas.Count) ? origAreas[screenIndex] : screen.Bounds;
            Width = origRect.Width;
            Height = _barHeight;
            Location = new Point(origRect.Left, origRect.Top);
            _currentScreen = screenIndex;
            // 详情面板跟随目标屏 DPI（跨缩放屏切换时尺寸一致）
            _detail.DpiScale = GetScreenDpi(screen) / 96f;
            ReserveWorkArea(screenIndex);
            Invalidate();
        }

        // 退出时恢复工作区
        public void UnregisterAppBar()
        {
            RestoreWorkArea();
        }

        // ---- 配置应用 ----
        public void ApplyFromConfig()
        {
            var cfg = _ctx.Cfg;
            St.Theme = cfg.Theme == "dark" ? 1 : 2;
            St.Mini = false; // 置顶栏不使用 mini
            int pct = Math.Max(50, Math.Min(100, cfg.OpacityPct));
            // 窗口半透明：置顶栏为普通窗口，用 Form.Opacity（悬浮窗/详情窗为分层窗口，走 LayeredAlpha）
            Opacity = pct / 100.0;
            _detail.LayeredAlpha = (byte)(255 * pct / 100);
            // 置顶栏同样消费"窗口置顶"配置（此前硬编码 true，取消勾选后设置静默失效）
            TopMost = cfg.TopMost;
            _detail.TopMost = cfg.TopMost;

            _refreshTimer.Stop();
            _refreshTimer.Interval = Math.Max(15, cfg.RefreshSec) * 1000;
            _refreshTimer.Start();

            int targetScreen = Math.Max(0, Math.Min(Screen.AllScreens.Length - 1, cfg.AppBarScreen));
            ApplyToScreen(targetScreen);

            _uiTimer.Start();
            Invalidate();
        }

        // 切回悬浮窗模式后置顶栏隐藏且工作区已恢复，停止其刷新/重绘定时器，
        // 避免与悬浮窗的定时器同时常驻触发
        public void SuspendTimers()
        {
            _uiTimer.Stop();
            _refreshTimer.Stop();
        }

        public void Rebuild()
        {
            Invalidate();
        }

        // ---- 绘制：全宽背景 + 居中横向徽标 ----
        private struct BadgeInfo
        {
            public string Name, Value;
            public Color DotColor;
            public float NameW, ValW;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 背景
            using (var brush = new SolidBrush(St.Bg))
                g.FillRectangle(brush, 0, 0, Width, Height);
            // 底部细线
            using (var pen = new Pen(St.Border, 1f))
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

            var providers = _ctx.Providers;
            if (providers.Count == 0) return;

            // 第一遍：测量每个徽标宽度
            float s = _scale; // 当前屏缩放因子（直接保存的 DPI/96，避免 int 截断反推的精度误差）
            float gap = 24f * s;
            float dot = 8f * s;
            float dx1 = 8f * s, dx2 = 6f * s, dx3 = 10f * s;
            var fs = St.FontsForScale(s); // [1]=FNorm
            var badges = new List<BadgeInfo>(providers.Count);
            float totalW = 0;

            foreach (var st in providers)
            {
                var bi = new BadgeInfo { Name = st.Name };
                bool err = st.Error != null;
                var pool = st.Primary5h;
                int usedPct = pool != null ? Math.Max(0, Math.Min(100, pool.Percent)) : 0;
                int remain = 100 - usedPct;
                bi.DotColor = err ? St.Red : St.RemainColor(remain);
                bi.Value = err ? "失败"
                    : (pool != null
                        ? (pool.IsMoney ? pool.Currency + St.FmtMoney(pool.Remaining) : remain + "%")
                        : "--");
                bi.NameW = g.MeasureString(bi.Name, fs[1]).Width;
                bi.ValW = g.MeasureString(bi.Value, fs[1]).Width;
                badges.Add(bi);
                totalW += dx1 + dx2 + bi.NameW + dx3 + bi.ValW + gap;
            }
            totalW -= gap;

            // 居中起始 X
            float x = (Width - totalW) / 2;
            if (x < 8) x = 8;

            float textH = g.MeasureString("Ay", fs[1]).Height;
            float textY = (Height - textH) / 2;

            // 第二遍：绘制
            for (int i = 0; i < badges.Count; i++)
            {
                var bi = badges[i];

                using (var b = new SolidBrush(bi.DotColor))
                    g.FillEllipse(b, x, Height / 2 - dot / 2, dot, dot);

                float tx = x + dx1 + dx2;
                using (var b = new SolidBrush(St.Text))
                    g.DrawString(bi.Name, fs[1], b, tx, textY);
                tx += bi.NameW + dx3;
                using (var b = new SolidBrush(bi.DotColor))
                    g.DrawString(bi.Value, fs[1], b, tx, textY);

                x += dx1 + dx2 + bi.NameW + dx3 + bi.ValW + gap;
            }
        }

        // ---- 鼠标交互 ----
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            _ctx.RefreshData();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) _dragging = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            int target = FindScreenIndex(Cursor.Position);
            if (target != _ghostScreen)
            {
                _ghostScreen = target;
                ShowGhost(target);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            HideGhost();

            int target = FindScreenIndex(Cursor.Position);
            if (target != _currentScreen)
            {
                ApplyToScreen(target);
                _ctx.Cfg.AppBarScreen = target;
                _ctx.Cfg.Save();
            }
            _ghostScreen = -1;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _showTimer.Stop(); _hideTimer.Stop();
            _showTimer.Start();
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _showTimer.Stop();
            _hideTimer.Start();
        }

        // ---- 拖拽虚影 ----
        private void ShowGhost(int screenIndex)
        {
            var screen = GetScreen(screenIndex);
            if (_ghost == null) _ghost = new AppBarGhostForm();
            _ghost.Width = screen.Bounds.Width;
            // 用目标屏的 DPI 计算高度（跨不同缩放屏拖拽时，虚影与实际置顶栏高度一致）
            int ghostH = (int)(BaseBarHeight * GetScreenDpi(screen) / 96f);
            _ghost.Height = ghostH;
            _ghost.Location = new Point(screen.Bounds.Left, screen.Bounds.Top);
            if (!_ghost.Visible)
            {
                _ghost.Show(this);
                // 首次 Show() 后 WinForms 可能撑大高度，强制修正
                if (_ghost.Height != ghostH) _ghost.Height = ghostH;
            }
            _ghost.UpdateLayered();
        }
        private void HideGhost()
        {
            if (_ghost != null && _ghost.Visible) _ghost.Hide();
        }

        // ---- 详情面板 ----
        private void ShowDetail()
        {
            if (_ctx.Providers.Count == 0) return;
            _detail.UpdateData();
            var screen = GetScreen(_currentScreen >= 0 ? _currentScreen : 0);
            int x = screen.Bounds.Left + (screen.Bounds.Width - _detail.Width) / 2;
            int y = Bottom + St.SiF(4, _scale);
            _detail.Location = new Point(x, y);
            if (!_detail.Visible) _detail.Show(this);
        }

        public void ArmHide() { _hideTimer.Stop(); _hideTimer.Start(); }
        public void CancelHide() { _hideTimer.Stop(); }

        // Alt+F4 / WM_CLOSE：恢复工作区并转为隐藏（与托盘左键切换行为一致），
        // 避免绕过 UnregisterAppBar 导致系统工作区顶部永久残留一条 bar 高度
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                UnregisterAppBar();
                Hide();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_uiTimer != null) _uiTimer.Dispose();
                if (_refreshTimer != null) _refreshTimer.Dispose();
                if (_hideTimer != null) _hideTimer.Dispose();
                if (_showTimer != null) _showTimer.Dispose();
                if (_detail != null) _detail.Dispose();
                if (_ghost != null) _ghost.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ============================= 托盘图标 =============================
    internal static class IconFactory
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(St.GlmAccent, 5f))
                    g.DrawArc(pen, 5, 5, 22, 22, -90, 270);
                using (var brush = new SolidBrush(St.MxAccent))
                    g.FillEllipse(brush, 13, 13, 6, 6);
            }
            IntPtr hIcon = bmp.GetHicon();
            bmp.Dispose();
            // Icon.FromHandle 不接管 HICON 所有权，需克隆后手动销毁原始句柄
            var tmp = Icon.FromHandle(hIcon);
            var icon = (Icon)tmp.Clone();
            tmp.Dispose();
            DestroyIcon(hIcon);
            return icon;
        }
    }

    // 禁用鼠标滚轮切换选中项的 ComboBox：吞掉作用于控件本体的 WM_MOUSEWHEEL，
    // 避免鼠标悬停时滚动滚轮误改下拉框选项；下拉展开后的列表滚轮浏览不受影响
    internal class NoWheelComboBox : ComboBox
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL) return;
            base.WndProc(ref m);
        }
    }

    // 禁用鼠标滚轮调整数值的 NumericUpDown：同 NoWheelComboBox，吞掉 WM_MOUSEWHEEL，
    // 避免鼠标悬停时滚动滚轮误改数值（仅能点击上下按钮或手动输入调整）
    internal class NoWheelNumericUpDown : NumericUpDown
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL) return;
            base.WndProc(ref m);
        }
    }

    // ============================= 添加供应商对话框 =============================
    internal class AddProviderDialog : Form
    {
        private ComboBox _cmbType;
        private TextBox _txtKey;
        private TextBox _txtName;
        public ProviderConfig Result { get; private set; }
        // 所在屏缩放（跟随鼠标所在屏，保证各屏物理大小一致）
        private float _scale = St.Scale;

        public AddProviderDialog() : this(null, null) { }

        public AddProviderDialog(ProviderConfig existing) : this(existing, null) { }

        public AddProviderDialog(ProviderConfig existing, Form owner)
        {
            Text = existing != null ? "编辑供应商" : "添加供应商";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            // 缩放基准取父窗体所在屏而非光标所在屏：键盘触发（如菜单快捷键）时两者可能不一致，
            // 导致与 CenterParent 定位屏算错 DPI；无 owner（对话框直接弹出）时退回光标所在屏
            var baseScreen = owner != null ? Screen.FromControl(owner) : Screen.FromPoint(Cursor.Position);
            _scale = St.DpiOf(baseScreen) / 96f;
            Func<int, int> S = v => St.SiF(v, _scale);
            AutoScaleMode = AutoScaleMode.None; // 手动布局，禁用 PerMonitorV2 自动缩放
            ClientSize = new Size(S(400), S(175));
            Font = St.FontsForScale(_scale)[1];
            BackColor = Color.FromArgb(248, 250, 252);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(S(16)), RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(90)));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // 接口选择
            layout.Controls.Add(new Label { Text = "接口", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            _cmbType = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _cmbType.Items.AddRange(new object[] { "GLM 国内版 open.bigmodel.cn", "GLM 国际版 api.z.ai", "MiniMax 国内版 minimaxi.com", "MiniMax 国际版 minimax.io", "Kimi Code Plan api.kimi.com", "DeepSeek api.deepseek.com" });
            _cmbType.SelectedIndex = 0;
            _cmbType.SelectedIndexChanged += delegate { UpdateDefaultName(); };
            layout.Controls.Add(_cmbType, 1, 0);

            // API Key
            layout.Controls.Add(new Label { Text = "API Key", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            _txtKey = new TextBox { Dock = DockStyle.Fill, PasswordChar = '●' };
            layout.Controls.Add(_txtKey, 1, 1);

            // 显示名称
            layout.Controls.Add(new Label { Text = "显示名称", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            _txtName = new TextBox { Dock = DockStyle.Fill, Text = "GLM" };
            layout.Controls.Add(_txtName, 1, 2);

            // 按钮
            var btnPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Height = S(36) };
            var btnOk = new Button { Text = "确认", Width = S(80), Height = S(28), BackColor = Color.FromArgb(51, 65, 85), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            var btnCancel = new Button { Text = "取消", Width = S(80), Height = S(28), FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
            btnOk.Click += delegate { OnOk(); };
            btnPanel.Controls.Add(btnOk);
            btnPanel.Controls.Add(btnCancel);
            layout.Controls.Add(btnPanel, 0, 3);
            layout.SetColumnSpan(btnPanel, 2);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Controls.Add(layout);

            // 编辑模式：用现有配置预填
            if (existing != null)
            {
                int i = existing.Type == "ds" ? 5 : (existing.Type == "kimi" ? 4 : (existing.Type == "mx" ? (existing.Region == "intl" ? 3 : 2) : (existing.Region == "intl" ? 1 : 0)));
                _cmbType.SelectedIndex = i;
                _txtKey.Text = existing.Key;
                _txtName.Text = existing.Name;
            }
        }

        private void UpdateDefaultName()
        {
            // 仅当名称为空或等于上一个默认值时自动更新
            int i = _cmbType.SelectedIndex;
            string defaultName = i == 5 ? "DeepSeek" : (i == 4 ? "Kimi" : ((i == 2 || i == 3) ? "MiniMax" : "GLM"));
            if (string.IsNullOrWhiteSpace(_txtName.Text) || _txtName.Text == "GLM" || _txtName.Text == "MiniMax" || _txtName.Text == "Kimi" || _txtName.Text == "DeepSeek")
                _txtName.Text = defaultName;
        }

        private void OnOk()
        {
            if (string.IsNullOrWhiteSpace(_txtKey.Text))
            {
                MessageBox.Show(this, "请填写 API Key", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int i = _cmbType.SelectedIndex;
            string type = i == 5 ? "ds" : (i == 4 ? "kimi" : ((i == 2 || i == 3) ? "mx" : "glm"));
            string region = (i == 1 || i == 3) ? "intl" : "cn";
            string fallbackName = i == 5 ? "DeepSeek" : (i == 4 ? "Kimi" : ((i == 2 || i == 3) ? "MiniMax" : "GLM"));
            Result = new ProviderConfig { Type = type, Region = region, Key = _txtKey.Text.Trim(), Name = string.IsNullOrWhiteSpace(_txtName.Text) ? fallbackName : _txtName.Text.Trim() };
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ============================= 设置窗口 =============================
    internal class SettingsForm : Form
    {
        private readonly AppContext _ctx;
        private DataGridView _grid;
        private NumericUpDown _numInterval;
        private TrackBar _trkOpacity;
        private Label _lblOpacity;
        private CheckBox _chkTopMost;
        private CheckBox _chkAutoStart;
        private ComboBox _cmbTheme;
        private ComboBox _cmbMode;
        private ComboBox _cmbScreen;
        private Panel _screenRow;
        private List<ProviderConfig> _editList; // 编辑副本
        // 所在屏缩放（跟随鼠标所在屏，保证各屏物理大小一致）
        private float _scale = St.Scale;
        private bool _built;
        private Button _btnSave;
        // 手动拖动状态（标题栏拖动改为程序控制，实现拖动中实时跨屏适配）
        private bool _drag;
        private Point _dragOffset;

        public SettingsForm(AppContext ctx)
        {
            _ctx = ctx;
            _editList = new List<ProviderConfig>();
            foreach (var p in _ctx.Cfg.Providers)
                _editList.Add(new ProviderConfig { Type = p.Type, Region = p.Region, Key = p.Key, Name = p.Name });

            _scale = St.DpiOf(Screen.FromPoint(Cursor.Position)) / 96f;
            AutoScaleMode = AutoScaleMode.None; // 手动布局，禁用 PerMonitorV2 自动缩放
            BuildLayout();
            Shown += delegate { if (_built) _btnSave.Focus(); };
            _built = true;
        }

        // 按当前 _scale 构建全部布局（跨屏拖拽时重建，保证各屏物理大小一致）
        private void BuildLayout()
        {
            Func<int, int> S = v => St.SiF(v, _scale);
            var fs = St.FontsForScale(_scale); // [0]=FTitle [1]=FNorm [2]=FSmall [3]=FPct

            Text = "设置 - AI 额度悬浮窗 v" + AppInfo.Version;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(S(560), S(660));
            Font = fs[1];
            BackColor = Color.FromArgb(248, 250, 252);

            // 用 FlowLayoutPanel 自上而下流式布局，每行一个固定高度的 Panel，避免坐标/行高计算错误
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(S(22), S(18), S(22), S(8)),
                AutoScroll = true
            };

            // 底部固定区域（保存按钮行 + 提示行，不随内容滚动，始终置底）
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(S(22), 0, S(22), S(18)),
                Height = S(106) // 42+16+22+8+18：按钮行+间距+提示行+间距+底部 padding
            };

            int contentWidth = S(560) - S(44); // 减去左右 padding

            // ===== 供应商标题 + 添加/删除/排序按钮 =====
            var header = new Panel { Width = contentWidth, Height = S(36) };
            var lblTitle = new Label { Text = "供应商配置", Font = fs[0], ForeColor = Color.FromArgb(51, 65, 85), AutoSize = false, Bounds = new Rectangle(0, 0, S(136), S(36)), TextAlign = ContentAlignment.MiddleLeft };
            var btnAdd = new Button { Text = "+ 添加", Width = S(84), Height = S(32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(51, 65, 85), ForeColor = Color.White };
            var btnEdit = new Button { Text = "编辑", Width = S(84), Height = S(32), FlatStyle = FlatStyle.Flat };
            var btnDel = new Button { Text = "- 删除", Width = S(84), Height = S(32), FlatStyle = FlatStyle.Flat };
            var btnUp = new Button { Text = "↑", Width = S(44), Height = S(32), FlatStyle = FlatStyle.Flat };
            var btnDown = new Button { Text = "↓", Width = S(44), Height = S(32), FlatStyle = FlatStyle.Flat };
            btnAdd.FlatAppearance.BorderSize = 0;
            btnEdit.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            btnDel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            btnUp.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            btnDown.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            // 从右往左依次排列，间距 8px
            int bx = contentWidth;
            int y2 = S(2);
            bx -= btnAdd.Width; btnAdd.SetBounds(bx, y2, btnAdd.Width, btnAdd.Height);
            bx -= S(8) + btnEdit.Width; btnEdit.SetBounds(bx, y2, btnEdit.Width, btnEdit.Height);
            bx -= S(8) + btnDel.Width; btnDel.SetBounds(bx, y2, btnDel.Width, btnDel.Height);
            bx -= S(8) + btnDown.Width; btnDown.SetBounds(bx, y2, btnDown.Width, btnDown.Height);
            bx -= S(8) + btnUp.Width; btnUp.SetBounds(bx, y2, btnUp.Width, btnUp.Height);
            btnAdd.Click += delegate { AddProvider(); };
            btnEdit.Click += delegate { EditSelected(); };
            btnDel.Click += delegate { DeleteSelected(); };
            btnUp.Click += delegate { MoveSelected(-1); };
            btnDown.Click += delegate { MoveSelected(1); };
            header.Controls.Add(btnUp);
            header.Controls.Add(btnDown);
            header.Controls.Add(btnDel);
            header.Controls.Add(btnEdit);
            header.Controls.Add(btnAdd);
            header.Controls.Add(lblTitle);
            flow.Controls.Add(header);

            // ===== 表格 =====
            BuildGrid();
            _grid.Width = contentWidth;
            _grid.Height = S(180);
            flow.Controls.Add(_grid);
            flow.SetFlowBreak(_grid, true);

            // ===== 通用设置 标题 =====
            flow.Controls.Add(new Label { Text = "通用设置", Font = fs[0], ForeColor = Color.FromArgb(51, 65, 85), AutoSize = false, Width = contentWidth, Height = S(28), TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(0, S(6), 0, 0) });

            // 模式行
            var modeRow = new Panel { Width = contentWidth, Height = S(36) };
            var lblMode = new Label { Text = "模式", AutoSize = false, Bounds = new Rectangle(0, 0, S(120), S(36)), TextAlign = ContentAlignment.MiddleLeft };
            // 右侧控件 y=S(8)、高=S(26)：使控件内文字（顶部对齐，距顶约 3px）的中心与
            // label 文字中心（行高 36 内居中）对齐，实现左右同一水平线
            _cmbMode = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(S(120), S(8), S(150), S(26)) };
            _cmbMode.Items.AddRange(new object[] { "悬浮窗", "置顶栏" });
            _cmbMode.SelectedIndex = _ctx.Cfg.DisplayMode == "topbar" ? 1 : 0;
            modeRow.Controls.Add(lblMode);
            modeRow.Controls.Add(_cmbMode);
            flow.Controls.Add(modeRow);

            // 显示屏幕行（仅置顶栏模式可见）
            _screenRow = new Panel { Width = contentWidth, Height = S(36) };
            var lblScreen = new Label { Text = "显示屏幕", AutoSize = false, Bounds = new Rectangle(0, 0, S(120), S(36)), TextAlign = ContentAlignment.MiddleLeft };
            _cmbScreen = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(S(120), S(8), S(280), S(26)) };
            var screens = Screen.AllScreens;
            for (int si = 0; si < screens.Length; si++)
            {
                var b = screens[si].Bounds;
                string label = (si + 1) + ". " + (screens[si].Primary ? "主显示器" : "显示器 " + (si + 1));
                label += string.Format(" ({0}×{1})", b.Width, b.Height);
                _cmbScreen.Items.Add(label);
            }
            _cmbScreen.SelectedIndex = Math.Max(0, Math.Min(_cmbScreen.Items.Count - 1, _ctx.Cfg.AppBarScreen));
            _screenRow.Controls.Add(lblScreen);
            _screenRow.Controls.Add(_cmbScreen);
            _screenRow.Visible = _cmbMode.SelectedIndex == 1;
            _cmbMode.SelectedIndexChanged += delegate { _screenRow.Visible = _cmbMode.SelectedIndex == 1; };
            flow.Controls.Add(_screenRow);

            // 刷新间隔行
            var secRow = new Panel { Width = contentWidth, Height = S(36) };
            var lblInterval = new Label { Text = "刷新间隔（秒）", AutoSize = false, Bounds = new Rectangle(0, 0, S(120), S(36)), TextAlign = ContentAlignment.MiddleLeft };
            _numInterval = new NoWheelNumericUpDown { Minimum = 15, Maximum = 3600, Value = Math.Max(15, Math.Min(3600, _ctx.Cfg.RefreshSec)), Increment = 15, Bounds = new Rectangle(S(120), S(8), S(100), S(26)) };
            secRow.Controls.Add(lblInterval);
            secRow.Controls.Add(_numInterval);
            flow.Controls.Add(secRow);

            // 不透明度行
            var opRow = new Panel { Width = contentWidth, Height = S(44) };
            var lblO = new Label { Text = "窗口不透明度", AutoSize = false, Bounds = new Rectangle(0, 0, S(120), S(44)), TextAlign = ContentAlignment.MiddleLeft };
            _trkOpacity = new TrackBar { Minimum = 50, Maximum = 100, Value = Math.Max(50, Math.Min(100, _ctx.Cfg.OpacityPct)), TickFrequency = 10, Bounds = new Rectangle(S(120), S(4), contentWidth - S(120) - S(50), S(36)) };
            _lblOpacity = new Label { Text = _ctx.Cfg.OpacityPct + "%", AutoSize = false, Bounds = new Rectangle(contentWidth - S(50), 0, S(50), S(44)), TextAlign = ContentAlignment.MiddleLeft };
            _trkOpacity.ValueChanged += delegate { _lblOpacity.Text = _trkOpacity.Value + "%"; };
            opRow.Controls.Add(_lblOpacity);
            opRow.Controls.Add(_trkOpacity);
            opRow.Controls.Add(lblO);
            flow.Controls.Add(opRow);

            // 主题行
            var themeRow = new Panel { Width = contentWidth, Height = S(36) };
            var lblTheme = new Label { Text = "主题", AutoSize = false, Bounds = new Rectangle(0, 0, S(120), S(36)), TextAlign = ContentAlignment.MiddleLeft };
            _cmbTheme = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(S(120), S(8), S(150), S(26)) };
            _cmbTheme.Items.AddRange(new object[] { "浅色", "深色" });
            _cmbTheme.SelectedIndex = _ctx.Cfg.Theme == "dark" ? 1 : 0;
            themeRow.Controls.Add(lblTheme);
            themeRow.Controls.Add(_cmbTheme);
            flow.Controls.Add(themeRow);

            // 选项复选框行（FlatStyle.System：勾选框由系统按窗口所在屏 DPI 绘制，跨屏自动适配）
            var optRow = new Panel { Width = contentWidth, Height = S(32) };
            _chkTopMost = new CheckBox { Text = "窗口置顶", Checked = _ctx.Cfg.TopMost, AutoSize = false, FlatStyle = FlatStyle.System, Bounds = new Rectangle(0, S(4), S(110), S(24)) };
            _chkAutoStart = new CheckBox { Text = "开机启动", Checked = _ctx.Cfg.AutoStart, AutoSize = false, FlatStyle = FlatStyle.System, Bounds = new Rectangle(S(130), S(4), S(110), S(24)) };
            optRow.Controls.Add(_chkTopMost);
            optRow.Controls.Add(_chkAutoStart);
            flow.Controls.Add(optRow);

            // 底部按钮行
            var btnRow = new Panel { Width = contentWidth, Height = S(42), Margin = new Padding(0, S(16), 0, 0) };
            _btnSave = new Button { Text = "保存", Width = S(100), Height = S(34), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(51, 65, 85), ForeColor = Color.White };
            var btnTest = new Button { Text = "测试全部", Width = S(100), Height = S(34), FlatStyle = FlatStyle.Flat };
            var btnCancel = new Button { Text = "取消", Width = S(100), Height = S(34), FlatStyle = FlatStyle.Flat };
            _btnSave.FlatAppearance.BorderSize = 0;
            btnTest.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            btnCancel.SetBounds(contentWidth - S(100), 0, S(100), S(34));
            btnTest.SetBounds(contentWidth - S(100) * 2 - S(10), 0, S(100), S(34));
            _btnSave.SetBounds(contentWidth - S(100) * 3 - S(20), 0, S(100), S(34));
            _btnSave.Click += delegate { SaveAndClose(); };
            btnCancel.Click += delegate { Close(); };
            btnTest.Click += delegate { TestConnection(btnTest); };
            btnRow.Controls.Add(btnCancel);
            btnRow.Controls.Add(btnTest);
            btnRow.Controls.Add(_btnSave);
            bottom.Controls.Add(btnRow);

            // 提示行
            bottom.Controls.Add(new Label
            {
                Text = "配置保存在 exe 同目录 config.json（明文，请勿分享此文件）",
                ForeColor = Color.Gray, AutoSize = false, Width = contentWidth, Height = S(22), Font = fs[2], TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, S(8), 0, 0)
            });

            Controls.Add(bottom);
            Controls.Add(flow);
        }

        // 跨屏实时重建布局：拖动中窗口每次移动都会检测所在屏，
        // 与手动拖动循环完全同步（与悬浮窗行为一致，无系统拖动竞态）。
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!_built || !IsHandleCreated) return;
            try
            {
                float n = St.DpiOf(Screen.FromControl(this)) / 96f;
                if (Math.Abs(n - _scale) > 0.01f)
                {
                    _scale = n;
                    // 重建前备份未保存的编辑状态（控件树重建会从配置重读，不备份会静默丢失用户改动）
                    int interval = _numInterval != null ? (int)_numInterval.Value : Math.Max(15, _ctx.Cfg.RefreshSec);
                    int opacity = _trkOpacity != null ? _trkOpacity.Value : _ctx.Cfg.OpacityPct;
                    int modeIdx = _cmbMode != null ? _cmbMode.SelectedIndex : (_ctx.Cfg.DisplayMode == "topbar" ? 1 : 0);
                    int themeIdx = _cmbTheme != null ? _cmbTheme.SelectedIndex : (_ctx.Cfg.Theme == "dark" ? 1 : 0);
                    int screenIdx = _cmbScreen != null ? _cmbScreen.SelectedIndex : Math.Max(0, _ctx.Cfg.AppBarScreen);
                    bool topMost = _chkTopMost != null ? _chkTopMost.Checked : _ctx.Cfg.TopMost;
                    bool autoStart = _chkAutoStart != null ? _chkAutoStart.Checked : _ctx.Cfg.AutoStart;
                    int gridRow = _grid != null && _grid.CurrentRow != null ? _grid.CurrentRow.Index : -1;

                    SuspendLayout();
                    try
                    {
                        // 先快照再 Dispose：Dispose 会把控件从 Controls 移除，直接 foreach 枚举会因
                        // 集合被修改抛 InvalidOperationException（被外层 catch 吞掉后重建中断、按钮
                        // 区永久丢失）。快照后逐个 Dispose 仍是为避免仅 Clear() 泄漏 HWND/GDI 资源
                        var snapshot = new Control[Controls.Count];
                        Controls.CopyTo(snapshot, 0);
                        foreach (Control c in snapshot) c.Dispose();
                        Controls.Clear();
                        BuildLayout();
                    }
                    finally { ResumeLayout(true); }

                    // 回填重建前的编辑状态
                    _numInterval.Value = Math.Max(15, Math.Min(3600, interval));
                    _trkOpacity.Value = Math.Max(50, Math.Min(100, opacity));
                    if (modeIdx >= 0 && modeIdx < _cmbMode.Items.Count) _cmbMode.SelectedIndex = modeIdx;
                    if (themeIdx >= 0 && themeIdx < _cmbTheme.Items.Count) _cmbTheme.SelectedIndex = themeIdx;
                    if (screenIdx >= 0 && screenIdx < _cmbScreen.Items.Count) _cmbScreen.SelectedIndex = screenIdx;
                    _chkTopMost.Checked = topMost;
                    _chkAutoStart.Checked = autoStart;
                    if (gridRow >= 0 && gridRow < _grid.Rows.Count) _grid.CurrentCell = _grid.Rows[gridRow].Cells[0];
                    // 拖动中让窗口贴住鼠标抓点；非拖动保持左上角位置
                    if (_drag) Location = new Point(MousePosition.X - _dragOffset.X, MousePosition.Y - _dragOffset.Y);
                }
            }
            catch (Exception ex)
            {
                // 重建失败时保留原布局（缩放不更新），下次移动窗口会重试，不阻塞拖动
                System.Diagnostics.Debug.WriteLine("SettingsForm 跨屏重建失败: " + ex.Message);
            }
        }

        // 手动拖动标题栏（WM_NCHITTEST 已把标题栏转为客户区，事件在此接收）
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // 偏移取屏幕坐标系（MousePosition - Location）：标题栏被 WM_NCHITTEST 转为客户区后
            // 点击标题栏时 e.Location.Y 为负，若用客户区偏移对屏幕坐标运算，首次移动窗口跳变
            if (e.Button == MouseButtons.Left) { _drag = true; _dragOffset = new Point(MousePosition.X - Left, MousePosition.Y - Top); }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_drag) return;
            // 捕获丢失（窗口失活/ESC 中断等）时复位，避免窗口失控持续跟随鼠标
            if (!Capture) { _drag = false; return; }
            Location = new Point(MousePosition.X - _dragOffset.X, MousePosition.Y - _dragOffset.Y);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) _drag = false;
        }

        // 拦截 WM_NCHITTEST：标题栏区域（HT_CAPTION）转为客户区（HT_CLIENT），
        // 由程序自己拖动，从而支持拖动过程中实时跨屏适配。
        // WM_DPICHANGED 交给 base 处理：WinForms 更新 DeviceDpi（勾选框等按所在屏 DPI 绘制），
        // AutoScaleMode=None 时不会自动缩放布局，尺寸由 BuildLayout 手动控制。
        private const int WM_NCHITTEST = 0x0084;
        private const int HT_CAPTION = 2;
        private const int HT_CLIENT = 1;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if (m.Result.ToInt32() == HT_CAPTION) m.Result = new IntPtr(HT_CLIENT);
                return;
            }
            base.WndProc(ref m);
        }

        private void BuildGrid()
        {
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToResizeColumns = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                GridColor = Color.FromArgb(226, 232, 240),
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersHeight = St.SiF(34, _scale),
                RowTemplate = { Height = St.SiF(32, _scale) },
                ColumnHeadersDefaultCellStyle = { Font = St.FontsForScale(_scale)[0], BackColor = Color.FromArgb(241, 245, 249), ForeColor = Color.FromArgb(51, 65, 85), Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(St.SiF(8, _scale), 0, 0, 0) },
                DefaultCellStyle = { SelectionBackColor = Color.FromArgb(51, 65, 85), SelectionForeColor = Color.White, ForeColor = Color.FromArgb(51, 65, 85), Padding = new Padding(St.SiF(8, _scale), 0, 0, 0) },
                RowHeadersVisible = false,
                EnableHeadersVisualStyles = false
            };
            _grid.Columns.Add("colName", "显示名称");
            _grid.Columns.Add("colType", "接口类型");
            _grid.Columns.Add("colKey", "API Key");
            ((DataGridViewColumn)_grid.Columns["colKey"]).FillWeight = 180;
            _grid.CellFormatting += delegate(object s, DataGridViewCellFormattingEventArgs e)
            {
                if (e.ColumnIndex == _grid.Columns["colKey"].Index && e.Value != null)
                {
                    string k = e.Value.ToString();
                    e.Value = k.Length > 8 ? k.Substring(0, 4) + "******" + k.Substring(k.Length - 4) : "****";
                    e.FormattingApplied = true;
                }
            };
            RefreshGrid();
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            foreach (var p in _editList)
                _grid.Rows.Add(p.Name, p.TypeName, p.Key);
        }

        private void AddProvider()
        {
            using (var dlg = new AddProviderDialog(null, this))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    _editList.Add(dlg.Result);
                    RefreshGrid();
                }
            }
        }

        private void EditSelected()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= _editList.Count)
            {
                MessageBox.Show(this, "请先选择一个供应商", "编辑", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int idx = _grid.CurrentRow.Index;
            using (var dlg = new AddProviderDialog(_editList[idx], this))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    _editList[idx] = dlg.Result;
                    RefreshGrid();
                }
            }
        }

        private void DeleteSelected()
        {
            if (_grid.CurrentRow != null && _grid.CurrentRow.Index < _editList.Count)
            {
                _editList.RemoveAt(_grid.CurrentRow.Index);
                RefreshGrid();
            }
        }

        private void MoveSelected(int delta)
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= _editList.Count)
            {
                MessageBox.Show(this, "请先选择一个供应商", "移动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int idx = _grid.CurrentRow.Index;
            int newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= _editList.Count) return;
            var item = _editList[idx];
            _editList.RemoveAt(idx);
            _editList.Insert(newIdx, item);
            RefreshGrid();
            _grid.CurrentCell = _grid.Rows[newIdx].Cells[0];
        }

        private void SaveAndClose()
        {
            // 先快照旧配置：保存失败时回滚，防止"未保存的新配置"留在内存里被定时刷新
            // 使用（运行配置 ≠ 磁盘配置），或被后续偶发 Save（拖窗/贴边/开关等）静默落盘
            var oldProviders = _ctx.Cfg.Providers;
            int oldRefreshSec = _ctx.Cfg.RefreshSec;
            int oldOpacityPct = _ctx.Cfg.OpacityPct;
            bool oldTopMost = _ctx.Cfg.TopMost;
            bool oldAutoStart = _ctx.Cfg.AutoStart;
            string oldTheme = _ctx.Cfg.Theme;
            string oldDisplayMode = _ctx.Cfg.DisplayMode;
            int oldAppBarScreen = _ctx.Cfg.AppBarScreen;

            // 拷贝而非直接赋值：保存失败时窗口保持打开，用户继续增删/移动供应商会突变
            // _editList；若与 Cfg.Providers 共享同一 List，刷新线程池并行遍历会因列表收缩
            // 抛 ArgumentOutOfRangeException 并终止进程（详见 RefreshData 的 providers[i]）
            _ctx.Cfg.Providers = new List<ProviderConfig>(_editList);
            _ctx.Cfg.RefreshSec = (int)_numInterval.Value;
            _ctx.Cfg.OpacityPct = _trkOpacity.Value;
            _ctx.Cfg.TopMost = _chkTopMost.Checked;
            _ctx.Cfg.AutoStart = _chkAutoStart.Checked;
            _ctx.Cfg.Theme = _cmbTheme.SelectedIndex == 1 ? "dark" : "light";
            _ctx.Cfg.DisplayMode = _cmbMode.SelectedIndex == 1 ? "topbar" : "float";
            _ctx.Cfg.AppBarScreen = _cmbScreen.SelectedIndex;
            // 保存失败（磁盘满/文件被杀软锁定等）不静默关闭窗口：
            // 否则用户以为已保存，重启后 API Key 全部丢失且无任何自救提示
            if (!_ctx.Cfg.Save())
            {
                // 回滚到快照：窗口保持打开期间内存配置与磁盘一致，用户取消/关闭不会留下半提交状态
                _ctx.Cfg.Providers = oldProviders;
                _ctx.Cfg.RefreshSec = oldRefreshSec;
                _ctx.Cfg.OpacityPct = oldOpacityPct;
                _ctx.Cfg.TopMost = oldTopMost;
                _ctx.Cfg.AutoStart = oldAutoStart;
                _ctx.Cfg.Theme = oldTheme;
                _ctx.Cfg.DisplayMode = oldDisplayMode;
                _ctx.Cfg.AppBarScreen = oldAppBarScreen;
                MessageBox.Show(this, "配置保存失败（config.json 写入失败，可能磁盘已满或被其他程序占用）。\n窗口保持打开，请先记录本次填写的 API Key，再重试保存。", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _ctx.ApplyConfig();
            Close();
        }

        private void TestConnection(Button btn)
        {
            if (_editList.Count == 0)
            {
                MessageBox.Show(this, "请先添加供应商", "测试连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            btn.Enabled = false; btn.Text = "测试中…";
            var snapshot = new List<ProviderConfig>(_editList);
            ThreadPool.QueueUserWorkItem(delegate
            {
                var sb = new StringBuilder();
                foreach (var p in snapshot)
                {
                    if (string.IsNullOrWhiteSpace(p.Key)) continue;
                    try
                    {
                        ProviderStatus st;
                        if (p.Type == "mx") st = QuotaService.FetchMiniMax(p);
                        else if (p.Type == "kimi") st = QuotaService.FetchKimi(p);
                        else if (p.Type == "ds") st = QuotaService.FetchDeepSeek(p);
                        else st = QuotaService.FetchGlm(p);
                        sb.AppendLine("【" + p.Name + "】连接成功 ✓  " + st.Pools.Count + " 个额度池");
                        foreach (var q in st.Pools)
                            sb.AppendLine("   · " + q.Label + "：" + (q.IsMoney ? q.Currency + St.FmtMoney(q.Remaining) : "已用 " + q.Percent + "%"));
                    }
                    catch (Exception ex) { sb.AppendLine("【" + p.Name + "】失败 ✗  " + ex.Message); }
                }
                string report = sb.ToString();
                // 测试耗时可达数分钟，期间窗口可能已被用户关闭（句柄销毁）；无 catch 的线程池
                // 线程上抛异常会终止整个进程，故与 AppContext.RefreshData 相同：BeginInvoke 外包
                // try-catch，回调内再判 IsDisposed 跳过弹窗
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        btn.Enabled = true; btn.Text = "测试全部";
                        if (IsDisposed) return;
                        MessageBox.Show(this, report, "测试连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch { /* 窗口已销毁，放弃回调 */ }
            });
        }
    }

    // ============================= 入口 =============================
    internal static class Program
    {
        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [STAThread]
        private static void Main()
        {
            // 单实例保护：置顶栏模式会修改系统工作区，多实例会导致工作区记录混乱
            bool createdNew;
            var mutex = new Mutex(true, "Global\\QuotaWidget_SingleInstance", out createdNew);
            if (!createdNew) return;

            // Per-Monitor V2 感知：窗口 DPI（含标题栏文字、系统绘制控件）跟随所在屏，跨屏拖动自动切换
            try
            {
                // 返回值 false（如与其他 DPI 感知声明冲突）时主动回退 SetProcessDpiAwareness，不只靠 catch 兜底
                if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) // PER_MONITOR_AWARE_V2
                    SetProcessDpiAwareness(2); // 回退 PROCESS_PER_MONITOR_DPI_AWARE
            }
            catch { try { SetProcessDpiAwareness(2); } catch { } } // 旧系统回退（Win8.1 起 shcore 支持 DPI 感知 API）
            try
            {
                // TLS1.2 必须；TLS1.3 仅在 .NET 4.8+ / Win11 上可用（动态检测避免旧框架抛异常）
                var protocols = SecurityProtocolType.Tls12;
                if (Enum.IsDefined(typeof(SecurityProtocolType), 12288))
                    protocols |= (SecurityProtocolType)12288;
                ServicePointManager.SecurityProtocol = protocols;
                ServicePointManager.DefaultConnectionLimit = 8;
            }
            catch { }
            // 同步 WinForms 的 HighDpiMode（.NET 4.7+）：保证 MessageBox/ToolTip 等系统控件与自绘窗口
            // 一致按 PerMonitorV2 缩放。旧框架无此 API，用反射调用避免直接引用导致编译/运行失败
            try
            {
                var setHighDpi = typeof(Application).GetMethod("SetHighDpiMode");
                if (setHighDpi != null)
                {
                    var t = typeof(Application).Assembly.GetType("System.Windows.Forms.HighDpiMode");
                    if (t != null) setHighDpi.Invoke(null, new object[] { Enum.Parse(t, "PerMonitorV2") });
                }
            }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 初始化 DPI 缩放（基于主屏 DPI）；查询失败按 96（100%）处理
            int mainDpi = 96;
            try { using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) mainDpi = (int)g.DpiX; }
            catch { /* 主屏 DPI 查询失败时按 96（100%）处理，缩放因子为 1.0 */ }
            St.InitScale(mainDpi);
            Application.Run(new AppContext());
            GC.KeepAlive(mutex); // 确保 Mutex 在应用程序整个生命周期内存活
        }
    }
}
