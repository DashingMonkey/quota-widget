// QuotaWidget - AI 额度工具 (GLM Coding Plan / MiniMax Token Plan / Kimi Code Plan / DeepSeek 余额)
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
using System.Security.Cryptography;

namespace QuotaWidget
{
    // ============================= 应用信息 =============================
    internal static class AppInfo
    {
        public const string Version = "1.1.7";
    }

    // ============================= 数据模型 =============================
    internal class ProviderConfig
    {
        public string Type { get; set; }     // "glm" / "mx"
        public string Region { get; set; }   // "cn" / "intl"
        // 旧明文字段：仅用于读取旧 config.json 完成迁移，序列化时忽略（迁移后恒为空）
        [ScriptIgnore]
        public string Key { get; set; }
        // DPAPI(CurrentUser) 加密的 Base64 密文：绑定本机当前 Windows 用户，
        // config.json 被其他软件扫描/拷走到别的电脑均无法还原
        public string KeyEnc { get; set; }
        public string Name { get; set; }     // 显示名称

        // 可选熵：恒定值（改动会让历史密文全部失效），提高离线盲解门槛
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuotaWidget.ApiKey.v1");
        // 解密结果缓存（仅进程内存，不序列化）：设置页每次重绘都要画掩码，避免反复走 DPAPI
        private string _plainCache;
        private bool _plainLoaded;

        public ProviderConfig() { Type = "glm"; Region = "cn"; Key = ""; KeyEnc = ""; Name = "GLM"; }

        /// 是否配置了 key（旧明文字段或密文任一存在）
        [ScriptIgnore]
        public bool HasKey { get { return !string.IsNullOrWhiteSpace(Key) || !string.IsNullOrWhiteSpace(KeyEnc); } }

        /// <summary>解出明文 key：未配置返回 ""；密文解不开（拷贝到其他电脑/Windows 账户）返回 null，由调用方提示重填</summary>
        public string GetKey()
        {
            if (_plainLoaded) return _plainCache;
            _plainLoaded = true;
            // 尚未走完迁移的旧明文直接返回（Config.Load 迁移后即清空该字段）
            if (!string.IsNullOrWhiteSpace(Key)) { _plainCache = Key; return _plainCache; }
            if (string.IsNullOrWhiteSpace(KeyEnc)) { _plainCache = ""; return _plainCache; }
            try
            {
                _plainCache = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(KeyEnc), Entropy, DataProtectionScope.CurrentUser));
            }
            catch { _plainCache = null; }   // 密文损坏或环境变化：保持 null，查询时报"无法解密"
            return _plainCache;
        }

        /// 加密保存明文 key（空串视为清除）
        public void SetKey(string plain)
        {
            _plainLoaded = true;
            _plainCache = plain ?? "";
            if (string.IsNullOrEmpty(plain)) { KeyEnc = ""; Key = ""; return; }
            KeyEnc = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
            Key = "";
        }

        public ProviderConfig Clone() { return new ProviderConfig { Type = Type, Region = Region, Key = Key, KeyEnc = KeyEnc, Name = Name }; }

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
        public string FloatLayout { get; set; }  // 悬浮窗排列："island"(条形灵动岛，默认) / "card"(矩形卡片)

        public Config()
        {
            Providers = new List<ProviderConfig>();
            GlmKey = ""; GlmRegion = "cn"; MxKey = ""; MxRegion = "cn";
            RefreshSec = 60; OpacityPct = 95; TopMost = true; Theme = "light"; X = -1; Y = -1; AutoStart = false; MiniMode = false; LockPosition = false;
            DisplayMode = "float"; AppBarScreen = 0; FloatLayout = "island";
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
            // 悬浮窗排列字段为空时默认条形灵动岛
            if (string.IsNullOrEmpty(cfg.FloatLayout)) cfg.FloatLayout = "island";
            // 旧明文 key 就地迁移为 DPAPI 密文并立即落盘；迁移成功后顺手清掉可能残留明文的损坏备份
            bool keyMigrated = false;
            if (cfg.Providers != null)
                foreach (var p in cfg.Providers)
                {
                    if (string.IsNullOrWhiteSpace(p.Key)) continue;
                    try { p.SetKey(p.Key); keyMigrated = true; }
                    catch { /* DPAPI 异常：保留明文字段下次启动重试，避免迁移失败丢 key */ }
                }
            if (keyMigrated)
            {
                cfg.Save();
                try { File.Delete(FilePath + ".bak"); } catch { }
            }
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

        // 解出明文 key；密文解不开（换电脑/换 Windows 账户）时抛出可读异常，
        // 由刷新/测试的 catch 展示；未配置 key 的供应商由调用方先行跳过
        private static string RequireKey(ProviderConfig p)
        {
            string k = p.GetKey();
            if (k == null) throw new Exception("密钥无法解密（可能已更换电脑或 Windows 账户），请在设置中重新填写");
            return k;
        }

        public static ProviderStatus FetchGlm(ProviderConfig p)
        {
            string baseUrl = p.Region == "intl" ? "https://api.z.ai" : "https://open.bigmodel.cn";
            string url = baseUrl + "/api/monitor/usage/quota/limit";
            string key = RequireKey(p);
            // 鉴权格式：先 Bearer 前缀（现行官方口径，与智谱控制台/主流工具一致），
            // 失败（401/403 或 200+鉴权失败 JSON）或空响应时回退旧式裸 Key（历史上 GLM 曾要求无 Bearer 前缀）。
            // 2026-08 起智谱监控接口故障：对有效 Key 返回 200+空 body，空体经
            // JavaScriptSerializer 反序列化得 null，后续 root.TryGetValue 会抛
            // NullReferenceException（"未将对象引用设置到对象的实例"），必须在此拦截
            string json = null;            // 有效（非空、非鉴权失败）响应体
            Exception firstErr = null;     // 首个鉴权相关失败（401/403 或 200+鉴权失败 JSON）；双失败时抛出，保留真实根因
            var ser = new JavaScriptSerializer();
            foreach (var auth in new[] { "Bearer " + key, key })
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
            string key = RequireKey(p);
            // 优先官方文档接口，失败则回退到 coding_plan 接口
            string json = null;
            try { json = HttpGet(baseUrl + "/v1/token_plan/remains", "Bearer " + key); }
            catch (Exception first)
            {
                // 两端点鉴权范围是否一致未经服务端验证，401/403 也保留回退（不缩小既有覆盖面）；
                // 双失败时抛第一个异常，保留更接近真实根因的 token_plan 错误
                try { json = HttpGet(baseUrl + "/v1/api/openplatform/coding_plan/remains", "Bearer " + key); }
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
            string json = HttpGet("https://api.kimi.com/coding/v1/usages", "Bearer " + RequireKey(p));

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
            string json = HttpGet("https://api.deepseek.com/user/balance", "Bearer " + RequireKey(p));

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

        // 悬浮窗 / 置顶栏 / 明细面板配色。对比度与设置页（contrast-proposal 方案 A）同方向：
        // 边框 / 分割线提到可见强度，浅色明细面板提纯白与悬浮窗底拉开，深色反向加深底层。
        public static Color Bg { get { return Theme == 1 ? Color.FromArgb(40, 43, 54) : Color.FromArgb(237, 238, 242); } }
        public static Color Card { get { return Theme == 1 ? Color.FromArgb(62, 67, 84) : Color.FromArgb(255, 255, 255); } }
        public static Color Text { get { return Theme == 1 ? Color.FromArgb(232, 234, 240) : Color.FromArgb(51, 65, 85); } }
        public static Color Dim { get { return Theme == 1 ? Color.FromArgb(158, 166, 186) : Color.FromArgb(100, 116, 139); } }
        public static Color BarBg { get { return Theme == 1 ? Color.FromArgb(86, 92, 112) : Color.FromArgb(206, 211, 221); } }
        // 窗口最外圈细线：深色主题用浅线，浅色主题用深线，保证在任何壁纸上都有轮廓
        public static Color Border { get { return Theme == 1 ? Color.FromArgb(88, 94, 114) : Color.FromArgb(185, 191, 203); } }
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

        // ---- 横向徽标（置顶栏 / 悬浮窗条形灵动岛共用）----
        public struct BadgeInfo
        {
            public string Name, Value;
            public Color DotColor;
            public float NameW, ValW;
        }

        // 按当前数据与字体测量所有徽标；totalW 为去掉末尾间距后的内容总宽（岛宽计算与绘制共用，
        // 两端必须同源测量，否则灵动岛按此定宽后徽标会被裁切）
        public static List<BadgeInfo> BuildBadges(List<ProviderStatus> providers, Graphics g, float s, out float totalW)
        {
            float gap = 24f * s;
            float dx1 = 8f * s, dx2 = 6f * s, dx3 = 10f * s;
            var fs = FontsForScale(s); // [1]=FNorm
            var badges = new List<BadgeInfo>(providers.Count);
            totalW = 0;
            foreach (var st in providers)
            {
                var bi = new BadgeInfo { Name = st.Name };
                bool err = st.Error != null;
                var pool = st.Primary5h;
                int usedPct = pool != null ? Math.Max(0, Math.Min(100, pool.Percent)) : 0;
                int remain = 100 - usedPct;
                bi.DotColor = err ? Red : RemainColor(remain);
                bi.Value = err ? "失败"
                    : (pool != null
                        ? (pool.IsMoney ? pool.Currency + FmtMoney(pool.Remaining) : remain + "%")
                        : "--");
                bi.NameW = g.MeasureString(bi.Name, fs[1]).Width;
                bi.ValW = g.MeasureString(bi.Value, fs[1]).Width;
                badges.Add(bi);
                totalW += dx1 + dx2 + bi.NameW + dx3 + bi.ValW + gap;
            }
            if (badges.Count > 0) totalW -= gap;
            return badges;
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

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Win11 圆角：DWMWA_WINDOW_CORNER_PREFERENCE(33) / DWMWCP_ROUND(2)，DWM 抗锯齿裁剪窗口四角；
        // 旧系统无此 API 调用失败保持直角（兼容策略与 DpiOf 一致）。设置窗 / 下拉弹层 / 右键菜单共用。
        public static void RoundCorners(IntPtr hwnd)
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
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
                Text = "AI 额度工具 v" + AppInfo.Version
            };
            Tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) { ShowMenu(); return; }
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

        // 悬浮窗 / 置顶栏 / 托盘图标共用的右键菜单（自绘 WidgetMenu，样式与设置页统一）。
        // 每次弹出按当前配置构建：勾选态天然实时；置顶栏模式下 mini / 固定位置无意义不显示
        //（与旧 ContextMenuStrip 行为一致）
        public void ShowMenu()
        {
            var menu = new WidgetMenu();
            menu.AddItem("立即刷新", false, false, delegate { RefreshData(); });
            if (Cfg.DisplayMode != "topbar")
            {
                // mini 模式仅矩形卡片有意义（条形灵动岛本身已是极简单行）
                if (Cfg.FloatLayout != "island")
                    menu.AddItem("mini 模式", true, Cfg.MiniMode, delegate
                    {
                        Cfg.MiniMode = !Cfg.MiniMode;
                        Cfg.Save();
                        Floating.ApplyFromConfig();
                        Floating.Rebuild();
                        // 吸附态下切换 mini 会因高度变化导致窗口消失，直接取消吸附贴顶部展开
                        Floating.RestoreFromEdge();
                    });
                menu.AddItem("固定位置", true, Cfg.LockPosition, delegate
                {
                    Cfg.LockPosition = !Cfg.LockPosition;
                    if (Cfg.LockPosition) Floating.RestoreFromEdge();
                    else Floating.OnLockReleased();
                    Cfg.Save();
                });
            }
            menu.AddItem("设置…", false, false, delegate { ShowSettings(); });
            menu.AddSeparator();
            menu.AddItem("退出", false, false, delegate { ExitApp(); });
            menu.ShowAt(Cursor.Position);
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
                    if (!p.HasKey) return;   // 未配置 key：跳过（行内显示 "--"）
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

        // ---- 条形灵动岛排列 ----
        // island=true：内容自适应宽的胶囊徽标条，完全复用悬浮窗的贴边吸附机制——
        // 吸附顶边即自动隐藏（收起留缝、移入滑出展开），拖离顶边则退化为普通悬浮条
        private bool _island;
        private const int IslandH = 38;    // 岛高（DIP，与置顶栏同高）
        private const int IslandPeek = 5;  // 收起时露出像素（DIP，比卡片贴边的 3px 略宽便于悬停命中）
        private const int IslandPad = 16;  // 岛左右内边距（DIP，兼吸收徽标测量误差）

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
                    if (!_island) Width = WinWidth;
                    Rebuild(); // 窗口尺寸（岛宽高 / 卡片高）统一由 Rebuild 按当前排列重算
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
                // 灵动岛吸附兜底：初始显示/切换排列后鼠标不在窗口上时贴顶收起
                //（MouseLeave 覆盖不到"从未进入过窗口"的场景）
                if (_island && _edgeDocked && !_edgeCollapsed && !_dragging && !AnimRunning
                    && !Bounds.Contains(Cursor.Position))
                    EdgeCollapse();
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

            _island = _ctx.Cfg.FloatLayout == "island"; // 排列启动即生效（ApplyFromConfig 仅在切换排列时重置位置）
            ApplyFromConfig();
        }

        public void ApplyFromConfig()
        {
            var cfg = _ctx.Cfg;
            // 排列：矩形卡片 / 条形灵动岛（岛复用贴边吸附机制，仅外观与默认停靠不同）
            bool wasIsland = _island;
            _island = cfg.FloatLayout == "island";
            bool layoutChanged = _island != wasIsland;
            // mini 模式（仅矩形卡片消费）：隐藏圆点/进度条，字号保持原样
            St.Mini = cfg.MiniMode;
            UpdateScale();
            Width = _island ? IslandWidth() : WinWidth;
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

            if (_island)
            {
                if (layoutChanged || !St.IsPointOnScreen(new Point(cfg.X, cfg.Y)))
                {
                    // 进入条形排列（或无有效位置）：贴主屏工作区顶边居中，不继承矩形卡片的位置；
                    // 先展开显示，鼠标不在窗口上时由 _uiTimer 兜底收起
                    var wa = Screen.PrimaryScreen.WorkingArea;
                    Height = St.SiF(IslandH, _scale);
                    Location = new Point(wa.Left + Math.Max(0, (wa.Width - Width) / 2), wa.Top);
                    _edgeDocked = true;
                    _edgeCollapsed = false;
                    _edgeJustUndocked = false;
                    _edgeTargetX = Left;
                    cfg.X = Left; cfg.Y = Top; cfg.Save();
                }
                else
                {
                    Height = St.SiF(IslandH, _scale);
                    Location = new Point(cfg.X, cfg.Y);
                    // 位置贴住顶边视为吸附态（与手动拖到顶边等效）；否则为普通悬浮条不自动隐藏
                    var wa = Screen.FromControl(this).WorkingArea;
                    _edgeDocked = Top <= wa.Top + St.SiF(EdgeThreshold, _scale);
                    if (_edgeDocked) _edgeTargetX = Left;
                    _edgeCollapsed = false;
                    _edgeJustUndocked = false;
                }
            }
            else if (St.IsPointOnScreen(new Point(cfg.X, cfg.Y)))
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
            if (_island)
            {
                // 数据刷新导致徽标宽度变化：重算岛宽并保持中心点不变（不打断收起/展开状态）
                int oldW = Width;
                Width = IslandWidth();
                Height = St.SiF(IslandH, _scale);
                if (Width != oldW) Left += (oldW - Width) / 2;
            }
            else
            {
                Height = PadYS * 2 + _ctx.Providers.Count * RowH;
            }
            UpdateLayered();
            if (_detail.Visible) ShowDetail();
        }

        // 灵动岛宽度 = 徽标内容宽 + 左右内边距（与绘制同源测量，见 St.BuildBadges）
        private int IslandWidth()
        {
            int pad = St.SiF(IslandPad, _scale);
            float totalW = 0;
            using (var g = CreateGraphics())
                St.BuildBadges(_ctx.Providers, g, _scale, out totalW);
            return Math.Max(St.SiF(IslandH, _scale), (int)Math.Ceiling(totalW) + pad * 2);
        }

        // 收起时露出的高度：灵动岛 5px（便于悬停命中），矩形卡片维持原 3px
        private int PeekPx { get { return St.SiF(_island ? IslandPeek : EdgePeek, _scale); } }
        private bool AnimRunning { get { return _animTimer != null && _animTimer.Enabled; } }

        protected override void OnLayeredPaint(Graphics g)
        {
            if (_island)
            {
                // 条形灵动岛：圆角胶囊底 + 全周 1px 细边（曲度与悬浮窗卡片一致，参考系统圆角）+ 横向徽标
                using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), St.SiF(12, _scale)))
                {
                    using (var brush = new SolidBrush(St.Bg))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(St.Border, 1f))
                        g.DrawPath(pen, path);
                }
                if (_ctx.Providers.Count == 0) return;

                float s = _scale;
                float gap = 24f * s;
                float dot = 8f * s;
                float dx1 = 8f * s, dx2 = 6f * s, dx3 = 10f * s;
                float totalW;
                var badges = St.BuildBadges(_ctx.Providers, g, s, out totalW);
                float x = St.SiF(IslandPad, _scale);
                if (x < 8) x = 8;

                var fs = St.FontsForScale(s);
                float textH = g.MeasureString("Ay", fs[1]).Height;
                float textY = (Height - textH) / 2;
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
                return;
            }

            // 矩形卡片（原样）：圆角背景，圆角外像素 alpha=0 → 完全透明
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
            if (e.Button == MouseButtons.Right) { _ctx.ShowMenu(); return; }
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
            AnimateTo(new Point(_edgeTargetX, wa.Top - Height + PeekPx));
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
            AnimateTo(new Point(_edgeTargetX, wa.Top - Height + PeekPx));
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
            // 灵动岛吸附态按展开后的底边定位：收起瞬间弹出面板也不与岛体重叠
            int y = (_island && _edgeDocked ? wa.Top + Height : Bottom) + 8;
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
        // HUD 详情面板不抢焦点：弹出 / 点击都不激活（WS_EX_NOACTIVATE），
        // 避免悬停展开时打断其他窗口的前台状态（含刚弹出的右键菜单）
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        // 右键菜单与悬浮窗共用（自绘 WidgetMenu）
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) _ctx.ShowMenu();
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
    internal class TopBarForm : LayeredForm
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

            // 首次显示后强制修正高度（WinForms 首次 Show 时可能因非客户区/自动缩放导致高度偏大）
            Shown += delegate
            {
                if (Height != _barHeight)
                {
                    Height = _barHeight;
                    // 高度变了，重新预留工作区以匹配
                    if (_currentScreen >= 0) ReserveWorkArea(_currentScreen);
                }
                UpdateLayered();
            };

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

        // 把置顶栏贴到目标屏顶部（全宽常驻），并预留工作区
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
            UpdateLayered();
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
            // 置顶栏与悬浮窗同为分层窗口：用 LayeredAlpha 控制不透明度（Form.Opacity 对 ULW 窗口无效）
            LayeredAlpha = (byte)(255 * pct / 100);
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
            UpdateLayered();
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
            UpdateLayered();
        }

        // ---- 绘制：全宽背景 + 居中横向徽标 ----
        protected override void OnLayeredPaint(Graphics g)
        {
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

            // 徽标绘制（数据与测量均来自 St.BuildBadges）
            float s = _scale; // 当前屏缩放因子（直接保存的 DPI/96，避免 int 截断反推的精度误差）
            float gap = 24f * s;
            float dot = 8f * s;
            float dx1 = 8f * s, dx2 = 6f * s, dx3 = 10f * s;
            float totalW;
            var badges = St.BuildBadges(providers, g, s, out totalW);

            // 居中起始 X
            float x = (Width - totalW) / 2;
            if (x < 8) x = 8;

            var fs = St.FontsForScale(s);
            float textH = g.MeasureString("Ay", fs[1]).Height;
            float textY = (Height - textH) / 2;

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
            if (e.Button == MouseButtons.Right) { _ctx.ShowMenu(); return; }
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

        // 分层窗口移动后需重新提交位图，否则跨屏切换时画面停留在旧位置
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (IsHandleCreated) UpdateLayered();
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
                DrawGlyph(g, new Rectangle(0, 0, 32, 32));
            IntPtr hIcon = bmp.GetHicon();
            bmp.Dispose();
            // Icon.FromHandle 不接管 HICON 所有权，需克隆后手动销毁原始句柄
            var tmp = Icon.FromHandle(hIcon);
            var icon = (Icon)tmp.Clone();
            tmp.Dispose();
            DestroyIcon(hIcon);
            return icon;
        }

        // 仪表盘图标本体（270° 弧 + 圆点），按目标矩形等比绘制：
        // 托盘位图 / 设置窗标题栏 / 关于页共用同一造型，保证全应用图标一致
        public static void DrawGlyph(Graphics g, Rectangle r)
        {
            float k = r.Width / 32f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(St.GlmAccent, 5f * k))
                g.DrawArc(pen, r.X + 5f * k, r.Y + 5f * k, 22f * k, 22f * k, -90, 270);
            using (var brush = new SolidBrush(St.MxAccent))
                g.FillEllipse(brush, r.X + 13f * k, r.Y + 13f * k, 6f * k, 6f * k);
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

    // ============================= 设置风格对话框基类 =============================
    // 无边框 + 自绘标题栏（应用图标 / 标题 / 关闭）+ DWM 圆角 + 失焦不画系统框，
    // 添加/编辑供应商、测试结果、消息提示等弹窗共用，观感与设置窗口完全一致。
    internal class StyledDialogBase : Form
    {
        protected readonly SettingsPal Pal;
        protected readonly float Sf;      // 缩放因子（打开时所在屏，对话框生命周期内固定）
        protected readonly Font[] Fs;     // St 字体组 [0..3]
        private Rectangle _rcClose;
        private bool _hoverClose, _dragWin;
        private Point _dragOff;

        protected StyledDialogBase(string title, SettingsPal pal, float scale, Size client)
        {
            Text = title;
            Pal = pal; Sf = scale; Fs = St.FontsForScale(scale);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            // 跟随设置窗口的普通层级（非置顶）
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = client;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();
        }

        protected int S(int v) { return St.SiF(v, Sf); }

        protected int TitleH { get { return S(46); } }

        // 圆角统一刻度（DIP）：容器 12 > 卡片 10 > 控件 8 > 控件内小件 6；胶囊（徽标/开关）取高度一半
        protected int R(int v) { return S(v); }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.Style |= 0x00040000; return cp; } // WS_THICKFRAME → DWM 阴影 + Win11 圆角
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            St.RoundCorners(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCACTIVATE = 0x0086;
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)1; return; }
            if (m.Msg == WM_NCACTIVATE) { m.Result = (IntPtr)1; return; } // 失焦不让 DWM 重绘系统边框（同设置窗口）
            base.WndProc(ref m);
        }

        // 标题栏 + 关闭按钮 + 外框；子类先调 base.OnPaint 再画自身内容
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Pal.Win))
                g.FillRectangle(b, ClientRectangle);

            var ic = new Rectangle(S(14), (TitleH - S(20)) / 2, S(20), S(20));
            IconFactory.DrawGlyph(g, ic);
            TextRenderer.DrawText(g, Text, Fs[1], new Rectangle(ic.Right + S(9), 0, Width - ic.Right - S(60), TitleH), Pal.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            int bs = S(32);
            _rcClose = new Rectangle(Width - S(10) - bs, S(7), bs, bs);
            if (_hoverClose)
                using (var path = St.RoundRect(_rcClose, S(8)))
                using (var b = new SolidBrush(Pal.Hover))
                    g.FillPath(b, path);
            using (var pen = new Pen(Pal.Text2, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = _rcClose.X + bs / 2f, cy = _rcClose.Y + bs / 2f, d = S(5);
                g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
            }
            using (var pen = new Pen(Pal.Line2))
                g.DrawLine(pen, 0, TitleH - 1, Width, TitleH - 1);
            using (var pen = new Pen(Pal.Line))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_rcClose.Contains(e.Location)) { Close(); return; }
            if (e.Y < TitleH)
            {
                _dragWin = true;
                _dragOff = new Point(MousePosition.X - Left, MousePosition.Y - Top);
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragWin) { Location = new Point(MousePosition.X - _dragOff.X, MousePosition.Y - _dragOff.Y); return; }
            bool h = _rcClose.Contains(e.Location);
            if (h != _hoverClose) { _hoverClose = h; Invalidate(_rcClose); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragWin = false;
            Capture = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverClose) { _hoverClose = false; Invalidate(_rcClose); }
        }

        // 主/次按钮（与设置窗口同款：TextRenderer 垂直居中 + 悬停态）
        protected void PaintBtn(Graphics g, Rectangle r, string text, bool primary, bool hot)
        {
            using (var path = St.RoundRect(r, S(8)))
            {
                if (primary)
                {
                    using (var b = new SolidBrush(hot ? SettingsPal.Mix(Pal.Primary, Pal.Win, 0.12) : Pal.Primary))
                        g.FillPath(b, path);
                }
                else
                {
                    using (var b = new SolidBrush(hot ? Pal.Hover : Pal.Win))
                        g.FillPath(b, path);
                    using (var pen = new Pen(Pal.Line))
                        g.DrawPath(pen, path);
                }
            }
            Color c = primary ? Pal.PrimaryText : (hot ? Pal.Text : Pal.Text2);
            TextRenderer.DrawText(g, text, Fs[1], r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // ============================= 添加 / 编辑供应商（设置页同款风格） =============================
    internal class ProviderEditDialog : StyledDialogBase
    {
        private static readonly string[] TypeItems = new string[]
        {
            "GLM 国内版 open.bigmodel.cn", "GLM 国际版 api.z.ai",
            "MiniMax 国内版 minimaxi.com", "MiniMax 国际版 minimax.io",
            "Kimi Code Plan api.kimi.com", "DeepSeek api.deepseek.com"
        };
        private static readonly string[] TypeDefaultNames = new string[] { "GLM", "GLM", "MiniMax", "MiniMax", "Kimi", "DeepSeek" };

        private readonly TextBox _txtKey, _txtName;
        private readonly Rectangle _rcSelect, _rcKeyBox, _rcNameBox, _rcOk, _rcCancel;
        private int _typeIndex;
        private bool _hotOk, _hotCancel, _hotSelect;
        private SettingsPopupList _popup;
        public ProviderConfig Result { get; private set; }

        public ProviderEditDialog(ProviderConfig existing, SettingsPal pal, float scale)
            : base(existing != null ? "编辑供应商" : "添加供应商", pal, scale, new Size(St.SiF(400, scale), St.SiF(310, scale)))
        {
            int x = S(20), w = ClientSize.Width - S(40);

            // 三组「标签 + 输入区」，自上而下
            _rcSelect = new Rectangle(x, S(46) + S(38), w, S(34));
            _rcKeyBox = new Rectangle(x, S(46) + S(100), w, S(34));
            _rcNameBox = new Rectangle(x, S(46) + S(162), w, S(34));
            _rcOk = new Rectangle(ClientSize.Width - S(20) - S(88), ClientSize.Height - S(18) - S(34), S(88), S(34));
            _rcCancel = new Rectangle(_rcOk.X - S(10) - S(88), _rcOk.Y, S(88), S(34));

            _txtKey = new TextBox { BorderStyle = BorderStyle.None, BackColor = pal.Win, ForeColor = pal.Text, Font = Fs[1], PasswordChar = '●' };
            _txtName = new TextBox { BorderStyle = BorderStyle.None, BackColor = pal.Win, ForeColor = pal.Text, Font = Fs[1] };
            Controls.Add(_txtKey);
            Controls.Add(_txtName);
            // 单行 TextBox 高度跟随字体，垂直居中在输入框内；边框由 OnPaint 画
            _txtKey.Bounds = new Rectangle(_rcKeyBox.X + S(10), _rcKeyBox.Y + (S(34) - _txtKey.PreferredHeight) / 2, _rcKeyBox.Width - S(20), _txtKey.PreferredHeight);
            _txtName.Bounds = new Rectangle(_rcNameBox.X + S(10), _rcNameBox.Y + (S(34) - _txtName.PreferredHeight) / 2, _rcNameBox.Width - S(20), _txtName.PreferredHeight);
            _txtKey.GotFocus += delegate { Invalidate(); };
            _txtKey.LostFocus += delegate { Invalidate(); };
            _txtName.GotFocus += delegate { Invalidate(); };
            _txtName.LostFocus += delegate { Invalidate(); };

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnOk(); }
            };
            FormClosing += delegate { ClosePopup(); };

            // 编辑模式：用现有配置预填
            if (existing != null)
            {
                _typeIndex = existing.Type == "ds" ? 5 : (existing.Type == "kimi" ? 4 : (existing.Type == "mx" ? (existing.Region == "intl" ? 3 : 2) : (existing.Region == "intl" ? 1 : 0)));
                // 预填解密后的真实 key（输入框本身以 ● 掩码显示）；密文解不开时留空待重填
                _txtKey.Text = existing.GetKey() ?? "";
                _txtName.Text = existing.Name;
            }
        }

        private void ClosePopup()
        {
            if (_popup != null && !_popup.IsDisposed) _popup.Close();
            _popup = null;
        }

        private void OpenTypePopup()
        {
            ClosePopup();
            _popup = new SettingsPopupList(RectangleToScreen(_rcSelect), TypeItems, _typeIndex, Pal, Fs[1], Fs[1], Sf, delegate(int idx)
            {
                _typeIndex = idx;
                // 名称仍为默认名时跟随接口切换（与旧对话框行为一致，手动改过则不动）
                if (string.IsNullOrWhiteSpace(_txtName.Text) || IsDefaultName(_txtName.Text))
                    _txtName.Text = TypeDefaultNames[idx];
                Invalidate();
            });
            _popup.Show(this);
        }

        private static bool IsDefaultName(string n)
        {
            foreach (string d in TypeDefaultNames)
                if (d == n) return true;
            return false;
        }

        private void OnOk()
        {
            if (string.IsNullOrWhiteSpace(_txtKey.Text))
            {
                StyledMsg.Show(this, "提示", "请填写 API Key", Pal, Sf);
                return;
            }
            int i = _typeIndex;
            string type = i == 5 ? "ds" : (i == 4 ? "kimi" : ((i == 2 || i == 3) ? "mx" : "glm"));
            string region = (i == 1 || i == 3) ? "intl" : "cn";
            string fallback = TypeDefaultNames[i];
            var r = new ProviderConfig { Type = type, Region = region, Name = string.IsNullOrWhiteSpace(_txtName.Text) ? fallback : _txtName.Text.Trim() };
            r.SetKey(_txtKey.Text.Trim());   // 明文仅存在于输入框与本次加密调用，配置文件里只落 DPAPI 密文
            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }

        // 标签 + 输入框边框（聚焦高亮）+ 选择框 + 按钮
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            DrawFieldLabel(g, "接口", S(46) + S(10));
            PaintSelectBox(g, _rcSelect, TypeItems[_typeIndex], _hotSelect);
            DrawFieldLabel(g, "API Key", S(46) + S(72));
            DrawFieldBox(g, _rcKeyBox, _txtKey.Focused);
            DrawFieldLabel(g, "显示名称", S(46) + S(134));
            DrawFieldBox(g, _rcNameBox, _txtName.Focused);

            PaintBtn(g, _rcOk, "确认", true, _hotOk);
            PaintBtn(g, _rcCancel, "取消", false, _hotCancel);
        }

        private void DrawFieldLabel(Graphics g, string label, int y)
        {
            TextRenderer.DrawText(g, label, Fs[1], new Rectangle(S(20), y, ClientSize.Width - S(40), S(20)), Pal.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private void DrawFieldBox(Graphics g, Rectangle r, bool focused)
        {
            using (var path = St.RoundRect(r, S(8)))
            using (var pen = new Pen(focused ? Pal.Primary : Pal.Line))
                g.DrawPath(pen, path);
        }

        private void PaintSelectBox(Graphics g, Rectangle r, string text, bool hot)
        {
            using (var path = St.RoundRect(r, S(8)))
            using (var pen = new Pen(hot ? Pal.CardHover : Pal.Line))
                g.DrawPath(pen, path);
            int maxW = r.Width - S(34);
            string s = text;
            while (s.Length > 1 && TextRenderer.MeasureText(s + "…", Fs[1]).Width > maxW) s = s.Substring(0, s.Length - 1);
            if (s != text) s += "…";
            TextRenderer.DrawText(g, s, Fs[1], new Rectangle(r.X + S(12), r.Y, maxW + S(20), r.Height), Pal.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            using (var pen = new Pen(Pal.Text2, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = r.Right - S(16), cyy = r.Y + r.Height / 2f;
                g.DrawLine(pen, cx - S(4), cyy - S(2), cx, cyy + S(2));
                g.DrawLine(pen, cx, cyy + S(2), cx + S(4), cyy - S(2));
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_rcSelect.Contains(e.Location)) { OpenTypePopup(); return; }
            if (_rcOk.Contains(e.Location)) { OnOk(); return; }
            if (_rcCancel.Contains(e.Location)) { Close(); return; }
            // 点击输入框以外区域让文本框失焦，边框恢复常态
            if (!_rcKeyBox.Contains(e.Location) && !_rcNameBox.Contains(e.Location))
                ActiveControl = null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hs = _rcSelect.Contains(e.Location), ho = _rcOk.Contains(e.Location), hc = _rcCancel.Contains(e.Location);
            if (hs != _hotSelect || ho != _hotOk || hc != _hotCancel)
            {
                _hotSelect = hs; _hotOk = ho; _hotCancel = hc;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotSelect || _hotOk || _hotCancel)
            {
                _hotSelect = _hotOk = _hotCancel = false;
                Invalidate();
            }
        }
    }

    // ============================= 测试连接结果（设置页同款风格） =============================
    internal class TestResultDialog : StyledDialogBase
    {
        private readonly Rectangle _rcOk;
        private bool _hotOk;

        public TestResultDialog(string report, SettingsPal pal, float scale)
            : base("测试连接", pal, scale, new Size(St.SiF(560, scale), St.SiF(400, scale)))
        {
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Close(); }
            };

            // 结果有多行明细（可能含长行）：只读多行框，纵向 + 横向滚动
            var txt = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                BackColor = pal.Win,
                ForeColor = pal.Text,
                Font = new Font("Consolas", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel),
                Text = report
            };
            int x = S(20), y = TitleH + S(14), w = ClientSize.Width - S(40);
            int h = ClientSize.Height - y - S(12) - S(34) - S(16);
            txt.Bounds = new Rectangle(x, y, w, h);
            Controls.Add(txt);
            // 只读框获得焦点会默认全选文本（显示成大蓝块），显示后取消选区
            Shown += delegate { txt.SelectionStart = txt.Text.Length; txt.SelectionLength = 0; };
            _rcOk = new Rectangle(ClientSize.Width - S(20) - S(96), ClientSize.Height - S(16) - S(34), S(96), S(34));
        }

        public static void Show(Form owner, string report, SettingsPal pal, float scale)
        {
            using (var d = new TestResultDialog(report, pal, scale))
                d.ShowDialog(owner);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            var box = new Rectangle(S(20), TitleH + S(14), ClientSize.Width - S(40), ClientSize.Height - TitleH - S(14) - S(12) - S(34) - S(16));
            using (var pen = new Pen(Pal.Line))
                g.DrawRectangle(pen, box.X - 1, box.Y - 1, box.Width + 1, box.Height + 1);
            PaintBtn(g, _rcOk, "确定", true, _hotOk);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_rcOk.Contains(e.Location)) Close();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = _rcOk.Contains(e.Location);
            if (h != _hotOk) { _hotOk = h; Invalidate(_rcOk); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotOk) { _hotOk = false; Invalidate(_rcOk); }
        }
    }

    // ============================= 消息提示（替代系统 MessageBox） =============================
    internal class StyledMsg : StyledDialogBase
    {
        private readonly string _msg;
        private readonly Rectangle _rcText;
        private readonly Rectangle _rcOk;
        private bool _hotOk;

        private StyledMsg(string title, string text, SettingsPal pal, float scale)
            : base(title, pal, scale, CalcSize(text, scale))
        {
            _msg = text;
            _rcText = new Rectangle(S(20), TitleH + S(14), ClientSize.Width - S(40), ClientSize.Height - TitleH - S(14) - S(34) - S(28));
            _rcOk = new Rectangle(ClientSize.Width - S(20) - S(96), ClientSize.Height - S(16) - S(34), S(96), S(34));
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Close(); }
            };
        }

        private static Size CalcSize(string text, float scale)
        {
            Font f = St.FontsForScale(scale)[1];
            int w = St.SiF(420, scale);
            // 按宽度折行后的文本高度 + 标题栏 + 按钮 + 边距
            Size sz = TextRenderer.MeasureText(text, f, new Size(w - St.SiF(40, scale), 100000), TextFormatFlags.WordBreak);
            int h = St.SiF(46, scale) + St.SiF(14, scale) + sz.Height + St.SiF(28, scale) + St.SiF(34, scale) + St.SiF(16, scale);
            return new Size(w, Math.Max(St.SiF(150, scale), h));
        }

        public static void Show(Form owner, string title, string text, SettingsPal pal, float scale)
        {
            using (var m = new StyledMsg(title, text, pal, scale))
                m.ShowDialog(owner);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            TextRenderer.DrawText(g, _msg, Fs[1], _rcText, Pal.Text, TextFormatFlags.WordBreak | TextFormatFlags.Top);
            PaintBtn(g, _rcOk, "确定", true, _hotOk);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_rcOk.Contains(e.Location)) Close();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = _rcOk.Contains(e.Location);
            if (h != _hotOk) { _hotOk = h; Invalidate(_rcOk); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotOk) { _hotOk = false; Invalidate(_rcOk); }
        }
    }

    // ============================= 设置窗口配色（局部预览主题） =============================
    // 设置窗口自带一套明暗配色（Codex 风格中性色）。与 St.Theme 全局主题分离：
    // 标题栏 🌙 / 「主题」分段控件实时预览时只改本窗口配色，点「保存」后经 ApplyConfig
    // 才写入 St.Theme，否则未保存的预览会立刻波及悬浮窗 / 置顶栏。
    internal struct SettingsPal
    {
        public Color Win, Side, Text, Text2, Text3, Line, Line2, Hover, Sel, Pill, PillText, Primary, PrimaryText, Track, ToggleOff, CardHover, Danger;

        internal static Color C(int rgb) { return Color.FromArgb((int)(0xFF000000u | (uint)rgb)); }

        // 向背景色靠拢的混合色（主按钮悬停 = 主色向窗底色提亮 12%）
        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // 对比度方案 A（对照 design/contrast-proposal.html）：
        // 侧栏与窗口拉开一档 / 分割线提到 ≈13% 黑（GitHub Primer 强度）/ 选中项与悬停分离
        public static SettingsPal For(bool dark)
        {
            SettingsPal p = new SettingsPal();
            if (dark)
            {
                p.Win = C(0x1C1C21); p.Side = C(0x141419); p.Text = C(0xF2F2F4); p.Text2 = C(0xA6A6AF);
                p.Text3 = C(0x6E6E78); p.Line = C(0x3A3A42); p.Line2 = C(0x2E2E36); p.Hover = C(0x26262E);
                p.Sel = C(0x32323B); p.Pill = C(0x2C2C33); p.PillText = C(0xC9C9D1); p.Primary = C(0xF2F2F4); p.PrimaryText = C(0x18181B);
                p.Track = C(0x3A3A42); p.ToggleOff = C(0x3F3F46); p.CardHover = C(0x4A4A52); p.Danger = C(0xF87171);
            }
            else
            {
                p.Win = Color.White; p.Side = C(0xF2F2F4); p.Text = C(0x18181B); p.Text2 = C(0x71717A);
                p.Text3 = C(0xA1A1AA); p.Line = C(0xD6D6DB); p.Line2 = C(0xD9D9DE); p.Hover = C(0xEBEBEE);
                p.Sel = C(0xE3E3E8); p.Pill = C(0xF4F4F5); p.PillText = C(0x52525B); p.Primary = C(0x18181B); p.PrimaryText = Color.White;
                p.Track = C(0xE4E4E7); p.ToggleOff = C(0xD4D4D8); p.CardHover = C(0xC9C9CF); p.Danger = C(0xDC2626);
            }
            return p;
        }
    }

    // ============================= 设置窗口下拉弹层 =============================
    // 自绘下拉列表（替代系统 ComboBox 下拉，保证主题与圆角一致）：锚定在触发框下方，
    // 底部放不下时向上翻。点击外部（Deactivate）/ Esc 关闭，↑↓ 移动、Enter 确认。
    internal class SettingsPopupList : Form
    {
        private readonly string[] _items;
        private readonly SettingsPal _pal;
        private readonly Font _fReg, _fBold;
        private readonly float _scale;
        private readonly Action<int> _onPick;
        private readonly int _sel;
        private int _hover = -1;
        private readonly int _itemH, _padV;

        public SettingsPopupList(Rectangle anchorScreen, string[] items, int selected,
            SettingsPal pal, Font fReg, Font fBold, float scale, Action<int> onPick)
        {
            _items = items;
            _sel = Math.Max(0, Math.Min(items.Length - 1, selected));
            _pal = pal; _fReg = fReg; _fBold = fBold; _scale = scale; _onPick = onPick;

            int itemH = St.SiF(30, scale), padV = St.SiF(5, scale);
            _itemH = itemH; _padV = padV;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();

            // 宽度取最长项文本 + 左右留白，但不小于触发框宽（视觉对齐）
            int maxW = anchorScreen.Width;
            foreach (string it in items)
                maxW = Math.Max(maxW, TextRenderer.MeasureText(it, fReg).Width + St.SiF(46, scale));
            Width = maxW;
            Height = items.Length * _itemH + _padV * 2 + 2;

            var wa = Screen.FromPoint(anchorScreen.Location).WorkingArea;
            int x = Math.Max(wa.Left + 8, Math.Min(wa.Right - Width - 8, anchorScreen.Left));
            int y = anchorScreen.Bottom + St.SiF(4, scale);
            if (y + Height > wa.Bottom - 8) y = anchorScreen.Top - Height - St.SiF(4, scale); // 底部放不下向上翻
            Location = new Point(x, y);

            Deactivate += delegate { Close(); };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Up) { _hover = Math.Max(0, (_hover < 0 ? _sel : _hover) - 1); Invalidate(); e.Handled = true; }
                else if (e.KeyCode == Keys.Down) { _hover = Math.Min(_items.Length - 1, (_hover < 0 ? _sel : _hover) + 1); Invalidate(); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { Pick(_hover < 0 ? _sel : _hover); e.Handled = true; }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            St.RoundCorners(Handle);
        }

        private void Pick(int idx)
        {
            var cb = _onPick;
            Close();
            if (cb != null) cb(idx);
        }

        private int ItemAt(Point p)
        {
            int i = (p.Y - _padV) / _itemH;
            return (i >= 0 && i < _items.Length) ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(_pal.Win))
                g.FillRectangle(b, ClientRectangle);
            using (var pen = new Pen(_pal.Line))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            for (int i = 0; i < _items.Length; i++)
            {
                var r = new Rectangle(1 + _padV, _padV + i * _itemH, Width - 2 - _padV * 2, _itemH);
                if (i == _hover || i == _sel)
                    using (var path = St.RoundRect(r, St.SiF(6, _scale)))
                    using (var b = new SolidBrush(_pal.Hover))
                        g.FillPath(b, path);
                using (var b = new SolidBrush(_pal.Text))
                    g.DrawString(_items[i], i == _sel ? _fBold : _fReg, b, new RectangleF(r.X + St.SiF(10, _scale), r.Y, r.Width, r.Height), WidgetMenu.SFLeft);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = ItemAt(e.Location);
            if (i != _hover) { _hover = i; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = ItemAt(e.Location);
            if (i >= 0) Pick(i);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }
    }

    // ============================= 悬浮窗 / 托盘右键菜单 =============================
    // 与设置页同一套自绘风格（Codex 中性色 + DWM 圆角 + 悬停高亮），替代系统 ContextMenuStrip。
    // 每次弹出按当前配置构建（勾选态 / 项可见性天然实时），DPI 跟随光标所在屏。
    internal class WidgetMenu : Form
    {
        private class Item
        {
            public string Label;   // null = 分隔线
            public bool Checked;
            public Action OnClick;
        }

        private readonly List<Item> _items = new List<Item>();
        private readonly List<int> _ys = new List<int>(); // 每项在窗内的 y 偏移（含顶部留白）
        private int _hover = -1;
        private float _scale = St.Scale;
        private int _itemH, _padV;
        private Font _fItem;
        private SettingsPal _pal;

        public void AddItem(string label, bool checkable, bool isChecked, Action onClick)
        {
            var it = new Item(); it.Label = label; it.Checked = checkable && isChecked; it.OnClick = onClick;
            _items.Add(it);
        }

        public void AddSeparator()
        {
            var it = new Item();
            _items.Add(it);
        }

        public void ShowAt(Point screenPos)
        {
            _pal = SettingsPal.For(St.Theme == 1);
            _scale = St.DpiOf(Screen.FromPoint(screenPos)) / 96f;
            Func<int, int> S = delegate(int v) { return St.SiF(v, _scale); };
            _fItem = St.FontsForScale(_scale)[1];
            _itemH = S(32);
            _padV = S(6);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();

            // 尺寸：宽度取最长项 + 勾选槽与左右留白；分隔线占独立矮行
            int w = S(180);
            _ys.Clear();
            int y = _padV;
            foreach (Item it in _items)
            {
                _ys.Add(y);
                if (it.Label == null) { y += S(9); continue; }
                w = Math.Max(w, TextRenderer.MeasureText(it.Label, _fItem).Width + S(58));
                y += _itemH;
            }
            Width = w;
            Height = y + _padV + 2;

            // 定位：光标右下优先，越界翻转 / 收敛到工作区
            var wa = Screen.FromPoint(screenPos).WorkingArea;
            int x = Math.Max(wa.Left + 8, Math.Min(wa.Right - Width - 8, screenPos.X));
            int py = screenPos.Y + S(2);
            if (py + Height > wa.Bottom - 8) py = screenPos.Y - Height - S(2);
            if (py < wa.Top + 8) py = Math.Min(wa.Bottom - 8 - Height, wa.Top + 8);
            Location = new Point(x, py);

            Deactivate += delegate { Close(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };

            // 无焦点菜单（WS_EX_NOACTIVATE）：不抢前台，其他窗口激活不会波及菜单；
            // 配合鼠标捕获实现"点击菜单外任意位置关闭"，托盘弹出也无需前台焦点
            Show();
            Capture = true;
        }

        // 无焦点窗口
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            St.RoundCorners(Handle); // 菜单四周同样走 DWM 抗锯齿圆角
        }

        private int ItemAt(Point p)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
                if (p.Y >= _ys[i] && p.Y < _ys[i] + (_items[i].Label == null ? St.SiF(9, _scale) : _itemH))
                    return _items[i].Label == null ? -1 : i;
            return -1;
        }

        private void Run(int i)
        {
            var a = _items[i].OnClick;
            Close();
            if (a != null) a();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(_pal.Win))
                g.FillRectangle(b, ClientRectangle);
            using (var pen = new Pen(_pal.Line))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            Func<int, int> S = delegate(int v) { return St.SiF(v, _scale); };
            for (int i = 0; i < _items.Count; i++)
            {
                Item it = _items[i];
                if (it.Label == null)
                { // 分隔线
                    using (var p2 = new Pen(_pal.Line2))
                        g.DrawLine(p2, S(12), _ys[i] + S(4), Width - S(12), _ys[i] + S(4));
                    continue;
                }
                if (i == _hover)
                    using (var path = St.RoundRect(new Rectangle(S(4), _ys[i], Width - S(8), _itemH), S(6)))
                    using (var b = new SolidBrush(_pal.Hover))
                        g.FillPath(b, path);
                if (it.Checked)
                { // 勾选槽：勾选项与普通项文字左缘对齐
                    using (var p2 = new Pen(_pal.Text, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        float cx = S(15), cy = _ys[i] + _itemH / 2f;
                        g.DrawLine(p2, cx - S(4), cy, cx - S(1), cy + S(3));
                        g.DrawLine(p2, cx - S(1), cy + S(3), cx + 4.5f * _scale, cy - 3.5f * _scale);
                    }
                }
                using (var b = new SolidBrush(_pal.Text))
                    g.DrawString(it.Label, _fItem, b, new RectangleF(S(32), _ys[i], Width - S(40), _itemH), SFLeft);
            }
        }

        internal static readonly StringFormat SFLeft = new StringFormat { LineAlignment = StringAlignment.Center };

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // Capture 生效期间菜单外点击（坐标越界）→ 关闭；右键同样视为关闭
            if (e.Button != MouseButtons.Left ||
                e.X < 0 || e.Y < 0 || e.X >= Width || e.Y >= Height)
                Close();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = ItemAt(e.Location);
            if (i != _hover) { _hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = ItemAt(e.Location);
            if (i >= 0) Run(i);
        }
    }

    // ============================= 行说明气泡 =============================
    // 悬停设置页行标题后浮现的说明气泡：无焦点 + 点击穿透（WS_EX_TRANSPARENT），
    // 鼠标事件全部落到下层设置窗口，不打断交互；样式与设置页一致，DWM 圆角。
    internal class HoverTip : Form
    {
        private SettingsPal _pal;
        private float _scale = St.Scale;
        private Font _fTip;
        private string _text;

        public HoverTip()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000020; // WS_EX_NOACTIVATE | WS_EX_TRANSPARENT
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            St.RoundCorners(Handle);
        }

        public void ShowAt(Point screenPos, string text, SettingsPal pal, float scale)
        {
            _pal = pal; _scale = scale; _text = text;
            _fTip = St.FontsForScale(scale)[1]; // St 字体组仅 [0..3]：[1]=FNorm 9pt
            Width = TextRenderer.MeasureText(text, _fTip).Width + St.SiF(20, scale);
            Height = St.SiF(26, scale);

            // 跟随光标右下，越界翻转 / 收敛到工作区
            var wa = Screen.FromPoint(screenPos).WorkingArea;
            int x = screenPos.X + St.SiF(14, scale);
            int y = screenPos.Y + St.SiF(22, scale);
            if (x + Width > wa.Right - 8) x = wa.Right - 8 - Width;
            if (y + Height > wa.Bottom - 8) y = screenPos.Y - Height - St.SiF(10, scale);
            Location = new Point(Math.Max(wa.Left + 8, x), Math.Max(wa.Top + 8, y));
            if (!Visible) Show();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(_pal.Win))
                g.FillRectangle(b, ClientRectangle);
            // 边框用 CardHover：气泡底色与窗口同为 Win，普通 Line 在深色主题下几乎不可见
            using (var pen = new Pen(_pal.CardHover))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, _text, _fTip, ClientRectangle, _pal.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // ============================= 设置窗口 =============================
    // Codex / ChatGPT 桌面端风格：左侧分类导航（供应商 / 通用 / 关于）+ 卡片式供应商列表 +
    // 行内开关 / 分段控件 / 滑杆，整窗自绘（与悬浮窗同一工艺），支持明暗主题实时预览。
    internal class SettingsForm : Form
    {
        private readonly AppContext _ctx;
        private readonly List<ProviderConfig> _editList = new List<ProviderConfig>(); // 编辑副本，保存时才写回

        // ---- 待保存设置（纯字段而非控件：跨屏 DPI 重建布局时状态天然保留，无需备份回填） ----
        private int _pendMode;      // 0=悬浮窗 1=置顶栏
        private int _pendScreen;    // 置顶栏停靠屏幕索引
        private bool _pendIsland;   // 悬浮窗排列：true=条形灵动岛 / false=矩形卡片
        private int _pendInterval;  // 刷新间隔（秒）
        private int _pendOpacity;   // 不透明度 50-100
        private bool _pendTopMost;
        private bool _pendAutoStart;
        private bool _pendDark;     // 主题预览：仅本窗口配色，保存后经 ApplyConfig 才影响悬浮窗

        // ---- 窗口状态 ----
        private int _page;                    // 0=供应商 1=通用 2=关于
        private float _scrollP, _scrollG;     // 供应商页 / 通用页滚动偏移
        private string _hover;                // 悬停命中区 id
        private bool _testing;                // 「测试全部」进行中
        private SettingsPopupList _popup;
        private bool _built;

        // ---- 拖拽状态 ----
        private bool _dragWindow;
        private Point _dragOffset;            // 屏幕坐标系（与旧版一致，跨屏重建后仍能贴住鼠标抓点）
        private int _dragCard = -1;           // 正在拖拽排序的供应商索引
        private bool _dragSlider;
        private int _grabDY;                  // 抓点相对卡片顶部的偏移（拖拽跟手用）
        private float _dragDrawY;             // 拖拽卡片当前绘制 Y（跟随鼠标）

        // ---- 行说明气泡（悬停通用页行标题后浮现） ----
        private System.Windows.Forms.Timer _tipTimer;
        private HoverTip _tip;
        private int _tipRow = -1;
        private string _tipText;

        private float _scale = St.Scale;
        private SettingsPal P { get { return SettingsPal.For(_pendDark); } }

        // ---- 布局结果（LayoutAll 重建；矩形为客户区坐标、已含滚动偏移，命中与绘制共用） ----
        private class Hot { public Rectangle R; public string Id; }
        private readonly List<Hot> _hots = new List<Hot>();
        private Rectangle _vp;                // 当前页内容视口
        private int _contentH;                // 当前页内容总高（与滚动无关，>视口高时出现滚动）
        private Rectangle _rcMoon, _rcClose, _rcAdd, _rcTest, _rcCancel, _rcSave;
        private readonly List<Rectangle> _cardRects = new List<Rectangle>();
        private Rectangle _grp1, _grp2;
        private readonly List<int> _visRows = new List<int>();        // 通用页可见行（置顶栏模式才含“显示屏幕”）
        private readonly List<Rectangle> _rowRects = new List<Rectangle>();
        private readonly List<Rectangle> _segModeBtns = new List<Rectangle>();
        private readonly List<Rectangle> _segLayoutBtns = new List<Rectangle>();
        private readonly List<Rectangle> _segThemeBtns = new List<Rectangle>();
        private Rectangle _rcSegMode, _rcSegLayout, _rcSegTheme, _rcSelScreen, _rcSelInt, _rcSlider, _rcTglTop, _rcTglAuto;

        // 通用页行号（R_LAYOUT 仅悬浮窗模式显示，R_SCREEN 仅置顶栏模式显示）
        private const int R_MODE = 0, R_LAYOUT = 1, R_SCREEN = 2, R_INT = 3, R_OP = 4, R_THEME = 5, R_TOP = 6, R_AUTO = 7;
        // 刷新间隔预设（秒）；用户已有自定义值不在预设中时插入列表，保证不丢配置
        private static readonly int[] IntervalPresets = new int[] { 15, 30, 60, 120, 300, 600, 1800, 3600 };

        // ---- 本窗字体缓存（像素字号随缩放，跨实例共享，随进程存活） ----
        // [0]=页面标题17 [1]=按钮/行标题13B [2]=导航13 [3]=正文12.5 [4]=说明11.5 [5]=徽标11
        // [6]=等宽11.5 [7]=图标角标10.5B [8]=正文12.5B
        private static readonly Dictionary<int, Font[]> _fontCache = new Dictionary<int, Font[]>();
        private static Font[] Fonts(float s)
        {
            int dpi = (int)Math.Round(96f * s);
            lock (_fontCache)
            {
                Font[] f;
                if (!_fontCache.TryGetValue(dpi, out f))
                {
                    f = new Font[9];
                    f[0] = new Font("Microsoft YaHei UI", 17f * s, FontStyle.Bold, GraphicsUnit.Pixel);
                    f[1] = new Font("Microsoft YaHei UI", 13f * s, FontStyle.Bold, GraphicsUnit.Pixel);
                    f[2] = new Font("Microsoft YaHei UI", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
                    f[3] = new Font("Microsoft YaHei UI", 12.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
                    f[4] = new Font("Microsoft YaHei UI", 11.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
                    f[5] = new Font("Microsoft YaHei UI", 11f * s, FontStyle.Regular, GraphicsUnit.Pixel);
                    f[6] = new Font("Consolas", 11.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
                    f[7] = new Font("Microsoft YaHei UI", 10.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
                    f[8] = new Font("Microsoft YaHei UI", 12.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
                    _fontCache[dpi] = f;
                }
                return f;
            }
        }

        public SettingsForm(AppContext ctx)
        {
            _ctx = ctx;
            foreach (var p in _ctx.Cfg.Providers)
                _editList.Add(p.Clone());   // 克隆保留 KeyEnc 密文，未编辑的 key 原样往返

            // 与旧版一致：缩放跟随光标所在屏，PerMonitorV2 下手动布局
            var curScreen = Screen.FromPoint(Cursor.Position);
            _scale = ClampScale(St.DpiOf(curScreen) / 96f, curScreen);

            Text = "设置 - AI 额度工具 v" + AppInfo.Version; // 任务栏 / Alt-Tab 标题
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            // 非置顶：与常规软件一致，可被其他窗口遮挡
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();
            KeyPreview = true;

            _pendMode = _ctx.Cfg.DisplayMode == "topbar" ? 1 : 0;
            _pendScreen = Math.Max(0, Math.Min(Screen.AllScreens.Length - 1, _ctx.Cfg.AppBarScreen));
            _pendIsland = _ctx.Cfg.FloatLayout == "island";
            _pendInterval = Math.Max(15, Math.Min(3600, _ctx.Cfg.RefreshSec));
            _pendOpacity = Math.Max(50, Math.Min(100, _ctx.Cfg.OpacityPct));
            _pendTopMost = _ctx.Cfg.TopMost;
            _pendAutoStart = _ctx.Cfg.AutoStart;
            _pendDark = _ctx.Cfg.Theme == "dark";

            ApplyScaleToSize();
            LayoutAll();

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Enter || (e.Control && e.KeyCode == Keys.S)) { e.SuppressKeyPress = true; SaveAndClose(); }
            };
            FormClosing += delegate { ClosePopup(); HideTip(); };
            // 行说明气泡：悬停行标题 450ms 后浮现
            _tipTimer = new System.Windows.Forms.Timer { Interval = 450 };
            _tipTimer.Tick += delegate
            {
                _tipTimer.Stop();
                if (_tipRow >= 0 && _tipText != null && _hover == "tip:" + _tipRow) ShowTip();
            };
            Shown += delegate
            {
                // 打开期间插拔显示器后索引可能越界，收敛到有效范围
                _pendScreen = Math.Max(0, Math.Min(Screen.AllScreens.Length - 1, _pendScreen));
                Focus();
            };
            _built = true;
        }

        // 物理尺寸不超过所在屏工作区（小屏 + 高缩放屏防溢出；旧窗口等比 DIP 布局存在同样问题）
        private float ClampScale(float s, Screen screen)
        {
            var wa = screen.WorkingArea;
            return Math.Min(s, Math.Min((wa.Height - 80f) / 660f, (wa.Width - 80f) / 880f));
        }

        private int S(int v) { return St.SiF(v, _scale); }

        // 垂直居中（水平左对齐）：行标题/说明、卡片文字、导航等一切行内文字统一用它画，
        // 避免手算 y 偏移与实际字体行高不一致导致的不居中
        private static readonly StringFormat SFLeftV = new StringFormat { LineAlignment = StringAlignment.Center };

        private void ApplyScaleToSize()
        {
            ClientSize = new Size(S(880), S(660));
        }

        // ============================= 布局 =============================
        // 命中区与绘制共用同一批矩形；页面内容总高与滚动无关，先布局再收敛滚动，
        // 超界时清空重排一次，保证命中区与最终绘制一致
        private void LayoutAll()
        {
            ClearPageLayout();
            int W = ClientSize.Width, H = ClientSize.Height;
            int tbH = S(46), footH = S(58), sideW = S(208);
            _vp = new Rectangle(sideW + S(30), tbH + S(26), W - sideW - S(60), H - tbH - footH - S(26) - S(20));
            AddChromeHots(tbH);

            for (int pass = 0; pass < 2; pass++)
            {
                if (_page == 0) LayoutProviders();
                else if (_page == 1) LayoutGeneral();
                else { _contentH = S(420); break; }
                float c = ClampScroll(_page == 0 ? _scrollP : _scrollG);
                bool changed = (_page == 0 ? _scrollP : _scrollG) != c;
                if (_page == 0) _scrollP = c; else _scrollG = c;
                if (!changed) break;
                ClearPageLayout();
                AddChromeHots(tbH);
            }

            // 底部操作栏按钮（取消 / 保存，右对齐）
            int btnH = S(34);
            Font[] fs = Fonts(_scale);
            int saveW = MeasureW("保存", fs[1]) + S(40);
            int cancelW = MeasureW("取消", fs[1]) + S(36);
            _rcSave = new Rectangle(W - S(30) - saveW, H - footH + (footH - btnH) / 2, saveW, btnH);
            _rcCancel = new Rectangle(_rcSave.X - S(10) - cancelW, _rcSave.Y, cancelW, btnH);
            AddHot(_rcCancel, "btn:cancel");
            AddHot(_rcSave, "btn:save");
        }

        private void ClearPageLayout()
        {
            _hots.Clear();
            _cardRects.Clear();
            _visRows.Clear();
            _rowRects.Clear();
            _segModeBtns.Clear();
            _segLayoutBtns.Clear();
            _segThemeBtns.Clear();
        }

        // 标题栏 + 侧栏命中区（滚动收敛重排时需补回）
        private void AddChromeHots(int tbH)
        {
            int bs = S(32);
            _rcMoon = new Rectangle(ClientSize.Width - S(8) - bs * 2 - S(4), S(7), bs, bs);
            _rcClose = new Rectangle(ClientSize.Width - S(8) - bs, S(7), bs, bs);
            AddHot(_rcMoon, "moon");
            AddHot(_rcClose, "close");
            int navW = S(208) - S(20), navH = S(34);
            for (int i = 0; i < 3; i++)
                AddHot(new Rectangle(S(10), tbH + S(14) + i * (navH + S(2)), navW, navH), "nav:" + i);
        }

        private float ClampScroll(float v)
        {
            int max = Math.Max(0, _contentH - _vp.Height);
            if (v < 0) v = 0;
            if (v > max) v = max;
            return v;
        }

        private void AddHot(Rectangle r, string id)
        {
            var h = new Hot(); h.R = r; h.Id = id;
            _hots.Add(h);
        }

        // 命中测试从尾部向前：后加入的命中区（卡片内按钮）盖在先加入的（整卡）之上
        private string Hit(Point p)
        {
            for (int i = _hots.Count - 1; i >= 0; i--)
                if (_hots[i].R.Contains(p)) return _hots[i].Id;
            return null;
        }

        private int HoverIdxOf(string prefix)
        {
            if (_hover == null || !_hover.StartsWith(prefix + ":")) return -1;
            int i;
            return int.TryParse(_hover.Substring(prefix.Length + 1), out i) ? i : -1;
        }

        private static int MeasureW(string s, Font f)
        {
            return TextRenderer.MeasureText(s, f).Width;
        }

        private void LayoutProviders()
        {
            Font[] fs = Fonts(_scale);
            int y = _vp.Y - (int)_scrollP + S(2);

            int btnH = S(34);
            int addW = MeasureW("＋ 添加供应商", fs[1]) + S(28);
            int testW = MeasureW(_testing ? "测试中…" : "测试全部", fs[1]) + S(24);
            _rcAdd = new Rectangle(_vp.X, y, addW, btnH);
            _rcTest = new Rectangle(_vp.X + addW + S(10), y, testW, btnH);
            AddHot(_rcAdd, "add");
            AddHot(_rcTest, "test");

            int cardH = S(54), gap = S(10);
            int listY = y + btnH + S(16);
            for (int i = 0; i < _editList.Count; i++)
            {
                var card = new Rectangle(_vp.X, listY + i * (cardH + gap), _vp.Width, cardH);
                _cardRects.Add(card);
                AddHot(card, "card:" + i); // 整卡先加（悬停高亮边框），行内按钮后加保证优先命中
                int cy = card.Y + cardH / 2;
                AddHot(new Rectangle(card.X + S(10), cy - S(9), S(20), S(18)), "grip:" + i);
                AddHot(new Rectangle(card.Right - S(14) - S(30) * 2 - S(6), cy - S(15), S(30), S(30)), "edit:" + i);
                AddHot(new Rectangle(card.Right - S(14) - S(30), cy - S(15), S(30), S(30)), "del:" + i);
            }
            _contentH = listY + _editList.Count * (cardH + gap) + S(8) - _vp.Y;
        }

        private void LayoutGeneral()
        {
            int y = _vp.Y - (int)_scrollP;

            int pad = S(16), rowH = S(52);
            _visRows.Add(R_MODE);
            if (_pendMode == 0) _visRows.Add(R_LAYOUT); // 仅悬浮窗模式需要选择排列（条形灵动岛 / 矩形卡片）
            if (_pendMode == 1) _visRows.Add(R_SCREEN); // 仅置顶栏模式需要选择停靠屏幕
            _visRows.Add(R_INT);
            _visRows.Add(R_OP);

            _grp1 = new Rectangle(_vp.X, y + S(18), _vp.Width, _visRows.Count * rowH);
            for (int i = 0; i < _visRows.Count; i++)
            {
                var rr = new Rectangle(_grp1.X, _grp1.Y + i * rowH, _grp1.Width, rowH);
                _rowRects.Add(rr);
                AddHot(new Rectangle(rr.X + pad, rr.Y, S(150), rr.Height), "tip:" + _visRows[i]); // 悬停行标题出说明气泡
                int cy = rr.Y + rowH / 2;
                switch (_visRows[i])
                {
                    case R_MODE:
                        _rcSegMode = SegRect(rr, new string[] { "悬浮窗", "置顶栏" });
                        SegBtns(_rcSegMode, _segModeBtns);
                        AddHot(_segModeBtns[0], "segmode:0");
                        AddHot(_segModeBtns[1], "segmode:1");
                        break;
                    case R_LAYOUT:
                        _rcSegLayout = SegRect(rr, new string[] { "矩形卡片", "条形灵动岛" });
                        SegBtns(_rcSegLayout, _segLayoutBtns);
                        AddHot(_segLayoutBtns[0], "seglayout:0");
                        AddHot(_segLayoutBtns[1], "seglayout:1");
                        break;
                    case R_SCREEN:
                        _rcSelScreen = new Rectangle(rr.Right - pad - S(210), cy - S(17), S(210), S(34));
                        AddHot(_rcSelScreen, "sel:screen");
                        break;
                    case R_INT:
                        _rcSelInt = new Rectangle(rr.Right - pad - S(120), cy - S(17), S(120), S(34));
                        AddHot(_rcSelInt, "sel:int");
                        break;
                    case R_OP:
                        _rcSlider = new Rectangle(rr.Right - pad - S(264), cy - S(2), S(200), S(4));
                        AddHot(new Rectangle(_rcSlider.X - S(6), cy - S(13), S(212), S(26)), "sld:op");
                        break;
                }
            }

            _grp2 = new Rectangle(_vp.X, _grp1.Bottom + S(16), _vp.Width, 3 * rowH);
            int[] rows2 = new int[] { R_THEME, R_TOP, R_AUTO };
            for (int i = 0; i < rows2.Length; i++)
            {
                var rr = new Rectangle(_grp2.X, _grp2.Y + i * rowH, _grp2.Width, rowH);
                _rowRects.Add(rr);
                AddHot(new Rectangle(rr.X + pad, rr.Y, S(150), rr.Height), "tip:" + rows2[i]);
                int cy = rr.Y + rowH / 2;
                switch (rows2[i])
                {
                    case R_THEME:
                        _rcSegTheme = SegRect(rr, new string[] { "☀ 浅色", "☾ 深色" });
                        SegBtns(_rcSegTheme, _segThemeBtns);
                        AddHot(_segThemeBtns[0], "segtheme:0");
                        AddHot(_segThemeBtns[1], "segtheme:1");
                        break;
                    case R_TOP:
                        _rcTglTop = new Rectangle(rr.Right - pad - S(42), cy - S(12), S(42), S(24));
                        AddHot(_rcTglTop, "tgl:top");
                        break;
                    case R_AUTO:
                        _rcTglAuto = new Rectangle(rr.Right - pad - S(42), cy - S(12), S(42), S(24));
                        AddHot(_rcTglAuto, "tgl:auto");
                        break;
                }
            }

            _contentH = _grp2.Bottom + S(8) - _vp.Y;
        }

        // 分段控件容器：右对齐于行，宽度按按钮文本自适应
        private Rectangle SegRect(Rectangle row, string[] labels)
        {
            Font[] fs = Fonts(_scale);
            int w = S(4);
            foreach (string l in labels) w += MeasureW(l, fs[3]) + S(24);
            return new Rectangle(row.Right - S(16) - w, row.Y + (row.Height - S(30)) / 2, w, S(30));
        }

        private void SegBtns(Rectangle box, List<Rectangle> into)
        {
            int bw = (box.Width - S(4)) / 2;
            into.Add(new Rectangle(box.X + S(2), box.Y + S(2), bw, box.Height - S(4)));
            into.Add(new Rectangle(box.X + S(2) + bw, box.Y + S(2), box.Width - S(4) - bw, box.Height - S(4)));
        }

        // ============================= 绘制 =============================
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            SettingsPal p = P;
            Font[] fs = Fonts(_scale);
            int W = ClientSize.Width, H = ClientSize.Height;
            int tbH = S(46), footH = S(58), sideW = S(208);

            using (var b = new SolidBrush(p.Win))
                g.FillRectangle(b, ClientRectangle);

            PaintTitlebar(g, p, fs, W, tbH);
            PaintSidebar(g, p, fs, tbH, sideW, H);

            // 内容区裁剪：滚动时页面内容不侵入标题栏 / 侧栏 / 底栏
            g.SetClip(new Rectangle(sideW, tbH, W - sideW, H - tbH - footH));
            if (_page == 0) PaintProviders(g, p, fs);
            else if (_page == 1) PaintGeneral(g, p, fs);
            else PaintAbout(g, p, fs);
            g.ResetClip();

            PaintFooter(g, p, fs, sideW, footH, H, W);
            PaintScrollbar(g, p);

            // 窗口最外圈细边（DWM 阴影之外的补充轮廓，浅色桌面上勾出边界）
            using (var pen = new Pen(p.Line))
                g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
        }

        private void PaintTitlebar(Graphics g, SettingsPal p, Font[] fs, int W, int tbH)
        {
            // 应用图标（与托盘 / 任务栏同一枚仪表盘造型）
            var ic = new Rectangle(S(14), (tbH - S(20)) / 2, S(20), S(20));
            IconFactory.DrawGlyph(g, ic);
            using (var b = new SolidBrush(p.Text))
                TextRenderer.DrawText(g, "设置", fs[1], new Rectangle(ic.Right + S(9), 0, S(80), tbH), p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            // 主题预览按钮：浅色显示月亮、深色显示太阳
            if (_hover == "moon")
                using (var path = St.RoundRect(_rcMoon, S(8)))
                using (var b = new SolidBrush(p.Hover))
                    g.FillPath(b, path);
            float mcx = _rcMoon.X + _rcMoon.Width / 2f, mcy = _rcMoon.Y + _rcMoon.Height / 2f;
            if (_pendDark) PaintSun(g, p.Text, mcx, mcy, _scale);
            else PaintMoon(g, p, mcx, mcy, _scale);

            // 关闭按钮
            if (_hover == "close")
                using (var path = St.RoundRect(_rcClose, S(8)))
                using (var b = new SolidBrush(p.Hover))
                    g.FillPath(b, path);
            using (var pen = new Pen(p.Text2, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = _rcClose.X + _rcClose.Width / 2f, cy = _rcClose.Y + _rcClose.Height / 2f, d = S(5);
                g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
            }

            using (var pen = new Pen(p.Line2))
                g.DrawLine(pen, 0, tbH - 1, W, tbH - 1);
        }

        private static void PaintMoon(Graphics g, SettingsPal p, float cx, float cy, float s)
        {
            float d = 15f * s;
            using (var b = new SolidBrush(p.Text))
            {
                g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                using (var b2 = new SolidBrush(p.Win)) // 偏移圆抠出月牙
                    g.FillEllipse(b2, cx - d / 2 + d * 0.34f, cy - d / 2 - d * 0.16f, d * 0.86f, d * 0.86f);
            }
        }

        private static void PaintSun(Graphics g, Color c, float cx, float cy, float s)
        {
            float r = 3.6f * s;
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (var pen = new Pen(c, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float r1 = 6f * s, r2 = 8.4f * s;
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI * i / 4.0;
                    g.DrawLine(pen, cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1, cx + (float)Math.Cos(a) * r2, cy + (float)Math.Sin(a) * r2);
                }
            }
        }

        private void PaintSidebar(Graphics g, SettingsPal p, Font[] fs, int tbH, int sideW, int H)
        {
            using (var b = new SolidBrush(p.Side))
                g.FillRectangle(b, 0, tbH, sideW, H - tbH);

            string[] names = new string[] { "供应商", "通用", "关于" };
            int navH = S(34), navW = sideW - S(20);
            for (int i = 0; i < 3; i++)
            {
                var r = new Rectangle(S(10), tbH + S(14) + i * (navH + S(2)), navW, navH);
                bool active = _page == i;
                bool hot = _hover == "nav:" + i && !active;
                if (active || hot)
                    using (var path = St.RoundRect(r, S(8)))
                    // 选中用 Sel（比 Hover 深两档），悬停仍用 Hover：两个状态不再同色
                    using (var b = new SolidBrush(active ? p.Sel : p.Hover))
                        g.FillPath(b, path);
                Color c = (active || hot) ? p.Text : p.Text2;
                PaintNavIcon(g, i, r.X + S(10), r.Y + (r.Height - S(15)) / 2, c, p.Side);
                using (var b = new SolidBrush(c))
                    g.DrawString(names[i], active ? fs[1] : fs[2], b, new RectangleF(r.X + S(35), r.Y, navW - S(35), navH), SFLeftV);
            }

            // 侧栏底部：应用名 + 版本
            using (var b = new SolidBrush(p.Text3))
            {
                g.DrawString("AI 额度工具", fs[4], b, S(20), H - S(48));
                g.DrawString("v" + AppInfo.Version, fs[4], b, S(20), H - S(30));
            }
        }

        private void PaintNavIcon(Graphics g, int idx, float x, float y, Color c, Color bgFill)
        {
            float sc = _scale;
            using (var pen = new Pen(c, 1.3f))
            {
                if (idx == 0)
                { // 供应商：上下两栏
                    using (var p1 = St.RoundRect(new Rectangle((int)x, (int)y, S(15), S(6)), S(2)))
                    using (var p2 = St.RoundRect(new Rectangle((int)x, (int)y + S(9), S(15), S(6)), S(2)))
                    {
                        g.DrawPath(pen, p1);
                        g.DrawPath(pen, p2);
                    }
                    using (var b = new SolidBrush(c))
                    {
                        g.FillEllipse(b, x + 2.8f * sc, y + 2.2f * sc, 1.6f * sc, 1.6f * sc);
                        g.FillEllipse(b, x + 2.8f * sc, y + 11.2f * sc, 1.6f * sc, 1.6f * sc);
                    }
                }
                else if (idx == 1)
                { // 通用：两条调节杆 + 旋钮
                    g.DrawLine(pen, x + 1 * sc, y + 4.5f * sc, x + 14 * sc, y + 4.5f * sc);
                    g.DrawLine(pen, x + 1 * sc, y + 11.5f * sc, x + 14 * sc, y + 11.5f * sc);
                    float kr = 2.2f * sc;
                    using (var b = new SolidBrush(bgFill))
                    {
                        g.FillEllipse(b, x + 10.5f * sc - kr, y + 4.5f * sc - kr, kr * 2, kr * 2);
                        g.FillEllipse(b, x + 4.7f * sc - kr, y + 11.5f * sc - kr, kr * 2, kr * 2);
                    }
                    g.DrawEllipse(pen, x + 10.5f * sc - kr, y + 4.5f * sc - kr, kr * 2, kr * 2);
                    g.DrawEllipse(pen, x + 4.7f * sc - kr, y + 11.5f * sc - kr, kr * 2, kr * 2);
                }
                else
                { // 关于：圆 + i
                    g.DrawEllipse(pen, x + 1.5f * sc, y + 1.5f * sc, 12 * sc, 12 * sc);
                    using (var b = new SolidBrush(c))
                    {
                        g.FillEllipse(b, x + 7.1f * sc, y + 4.2f * sc, 1.8f * sc, 1.8f * sc);
                        g.FillEllipse(b, x + 7.1f * sc, y + 7.2f * sc, 1.8f * sc, 4.4f * sc);
                    }
                }
            }
        }

        private void PaintProviders(Graphics g, SettingsPal p, Font[] fs)
        {
            // 工具栏：＋ 添加供应商（主） / 测试全部（次）
            PaintButton(g, p, fs, _rcAdd, "＋ 添加供应商", true, _hover == "add");
            PaintButton(g, p, fs, _rcTest, _testing ? "测试中…" : "测试全部", false, _hover == "test" && !_testing);

            if (_editList.Count == 0)
            {
                int ey = _rcAdd.Bottom + S(40);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                using (var b = new SolidBrush(p.Text2))
                    g.DrawString("还没有供应商", fs[1], b, new RectangleF(_vp.X, ey, _vp.Width, S(24)), sf);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                using (var b = new SolidBrush(p.Text3))
                    g.DrawString("点击「＋ 添加供应商」开始监控额度", fs[3], b, new RectangleF(_vp.X, ey + S(28), _vp.Width, S(20)), sf);
                return;
            }

            int hovCard = HoverCardIndex();
            for (int i = 0; i < _cardRects.Count; i++)
            {
                if (i == _dragCard) continue; // 拖拽中的卡片最后浮层绘制
                PaintProviderCard(g, p, fs, _cardRects[i], i, i == hovCard, false);
            }

            // 拖拽中的卡片：跟随鼠标浮起（投影 + 高亮边框），盖在其他卡片之上
            if (_dragCard >= 0 && _dragCard < _cardRects.Count)
            {
                var src = _cardRects[_dragCard];
                var dragRect = new Rectangle(src.X, (int)_dragDrawY, src.Width, src.Height);
                using (var path = St.RoundRect(new Rectangle(dragRect.X + S(2), dragRect.Y + S(5), dragRect.Width, dragRect.Height), S(10)))
                using (var sb = new SolidBrush(Color.FromArgb(30, 0, 0, 0)))
                    g.FillPath(sb, path);
                PaintProviderCard(g, p, fs, dragRect, _dragCard, true, true);
            }
        }

        private void PaintProviderCard(Graphics g, SettingsPal p, Font[] fs, Rectangle card, int i, bool hot, bool elevated)
        {
            var prov = _editList[i];
            int cardH = card.Height;
            using (var path = St.RoundRect(card, S(10)))
            {
                using (var b = new SolidBrush(p.Win))
                    g.FillPath(b, path);
                using (var pen = new Pen(hot || elevated ? p.CardHover : p.Line))
                    g.DrawPath(pen, path);
            }

            // 拖拽手柄（2×3 圆点）
            using (var b = new SolidBrush(_hover == "grip:" + i ? p.Text2 : p.Text3))
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 2; c++)
                        g.FillEllipse(b, card.X + S(13) + c * S(5), card.Y + cardH / 2 - S(6) + r * S(5), S(2), S(2));

            // 服务商标识色圆点（与悬浮窗一致取 AccentFor）
            using (var b = new SolidBrush(St.AccentFor(prov.Type)))
                g.FillEllipse(b, card.X + S(34), card.Y + cardH / 2 - S(4), S(8), S(8));

            // 名称（超长省略）
            string name = Elide(g, prov.Name, fs[1], S(110));
            float nameW = g.MeasureString(name, fs[1]).Width;
            using (var b = new SolidBrush(p.Text))
                g.DrawString(name, fs[1], b, new RectangleF(card.X + S(50), card.Y, nameW + S(16), cardH), SFLeftV);

            // 接口类型徽标
            string badge = prov.TypeName;
            int bw = MeasureW(badge, fs[5]) + S(16);
            var br = new Rectangle(card.X + S(50) + (int)nameW + S(10), card.Y + cardH / 2 - S(9), bw, S(18));
            using (var path = St.RoundRect(br, S(9)))
            using (var b = new SolidBrush(p.Pill))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, badge, fs[5], br, p.PillText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            // 打码 Key（与旧表格同规则：前 4 + ****** + 后 4）：内存解密后仅取首尾字符画掩码，
            // 完整明文不出进程内存、不落盘；解不开或未配置时统一显示 ****
            string key = prov.GetKey() ?? "";
            string masked = key.Length > 8 ? key.Substring(0, 4) + "******" + key.Substring(key.Length - 4) : "****";
            int keyMax = (card.Right - S(14) - S(30) * 2 - S(6)) - S(12) - (br.Right + S(12));
            if (keyMax > S(40))
                using (var b = new SolidBrush(p.Text3))
                    g.DrawString(Elide(g, masked, fs[6], keyMax), fs[6], b, new RectangleF(br.Right + S(12), card.Y, keyMax, cardH), SFLeftV);

            // 行内操作：编辑（铅笔） / 删除（垃圾桶）
            bool eHot = _hover == "edit:" + i, dHot = _hover == "del:" + i;
            var er = new Rectangle(card.Right - S(14) - S(30) * 2 - S(6), card.Y + cardH / 2 - S(15), S(30), S(30));
            var dr = new Rectangle(card.Right - S(14) - S(30), card.Y + cardH / 2 - S(15), S(30), S(30));
            if (eHot)
                using (var path = St.RoundRect(er, S(6)))
                using (var b = new SolidBrush(p.Hover))
                    g.FillPath(b, path);
            PaintPencil(g, eHot ? p.Text : p.Text2, er.X + S(7), er.Y + S(7), _scale);
            if (dHot)
                using (var path = St.RoundRect(dr, S(6)))
                using (var b = new SolidBrush(Color.FromArgb(22, p.Danger)))
                    g.FillPath(b, path);
            PaintTrash(g, dHot ? p.Danger : p.Text2, dr.X + S(7), dr.Y + S(7), _scale);
        }

        private int HoverCardIndex()
        {
            if (_hover == null) return -1;
            int k = _hover.IndexOf(':');
            if (k < 0) return -1;
            string head = _hover.Substring(0, k);
            if (head != "card" && head != "grip" && head != "edit" && head != "del") return -1;
            int i;
            return int.TryParse(_hover.Substring(k + 1), out i) ? i : -1;
        }

        private void PaintGeneral(Graphics g, SettingsPal p, Font[] fs)
        {
            PaintGroupBg(g, p, _grp1);
            PaintGroupBg(g, p, _grp2);

            for (int i = 0; i < _visRows.Count; i++)
                PaintRow(g, p, fs, _visRows[i], _rowRects[i], i > 0);
            int baseIdx = _visRows.Count;
            PaintRow(g, p, fs, R_THEME, _rowRects[baseIdx], true);
            PaintRow(g, p, fs, R_TOP, _rowRects[baseIdx + 1], true);
            PaintRow(g, p, fs, R_AUTO, _rowRects[baseIdx + 2], true);
        }

        private void PaintGroupBg(Graphics g, SettingsPal p, Rectangle grp)
        {
            using (var path = St.RoundRect(grp, S(12)))
            {
                using (var b = new SolidBrush(p.Win))
                    g.FillPath(b, path);
                using (var pen = new Pen(p.Line))
                    g.DrawPath(pen, path);
            }
        }

        // 行标题文案
        private static string RowTitle(int rowId)
        {
            switch (rowId)
            {
                case R_MODE: return "显示模式";
                case R_LAYOUT: return "排列";
                case R_SCREEN: return "显示屏幕";
                case R_INT: return "刷新间隔";
                case R_OP: return "窗口不透明度";
                case R_THEME: return "主题";
                case R_TOP: return "窗口置顶";
                default: return "开机启动";
            }
        }

        // 行说明文案（PaintRow 不再画小字，改为悬停行标题后的气泡提示）
        private static string RowDesc(int rowId)
        {
            switch (rowId)
            {
                case R_MODE: return "悬浮窗为独立小窗，可拖动，有矩形/条形两种排列；置顶栏全宽贴屏幕顶边常驻并预留工作区。";
                case R_LAYOUT: return "条形灵动岛为内容自适应宽的圆角胶囊，贴顶自动隐藏、可拖拽定位，拖离顶边变为普通悬浮条；矩形卡片为多行卡片视图，支持 mini 模式。";
                case R_SCREEN: return "置顶栏停靠的显示器。";
                case R_INT: return "自动查询额度的时间间隔。";
                case R_OP: return "数值越低悬浮窗越透明。";
                case R_THEME: return "跟随此设置，悬浮窗与置顶栏同步切换明暗。";
                case R_TOP: return "悬浮窗始终显示在其他窗口之上。";
                default: return "登录 Windows 后自动运行。";
            }
        }

        private void PaintRow(Graphics g, SettingsPal p, Font[] fs, int rowId, Rectangle r, bool withSeparator)
        {
            if (withSeparator)
                using (var pen = new Pen(p.Line2))
                    g.DrawLine(pen, r.X + S(16), r.Y, r.Right - S(16), r.Y);

            // 单行布局：标题整行垂直居中，说明文字改为悬停气泡（见 UpdateTip / HoverTip）
            using (var b = new SolidBrush(p.Text))
                g.DrawString(RowTitle(rowId), fs[1], b, new RectangleF(r.X + S(16), r.Y, r.Width - S(32), r.Height), SFLeftV);

            int cy = r.Y + r.Height / 2;
            switch (rowId)
            {
                case R_MODE:
                    // 分段顺序：悬浮窗在左（默认模式在前）。段位 0=悬浮窗 / 1=置顶栏，与 _pendMode 一致
                    PaintSeg(g, p, fs, _rcSegMode, _segModeBtns, new string[] { "悬浮窗", "置顶栏" }, _pendMode, "segmode");
                    break;
                case R_LAYOUT:
                    // 分段顺序：条形灵动岛在左（默认排列在前）。段位 0=条形灵动岛 / 1=矩形卡片，与 _pendIsland 一致
                    PaintSeg(g, p, fs, _rcSegLayout, _segLayoutBtns, new string[] { "条形灵动岛", "矩形卡片" }, _pendIsland ? 0 : 1, "seglayout");
                    break;
                case R_SCREEN:
                    string[] items = ScreenItems();
                    PaintSelect(g, p, fs, _rcSelScreen, items[Math.Min(_pendScreen, items.Length - 1)], _hover == "sel:screen");
                    break;
                case R_INT:
                    PaintSelect(g, p, fs, _rcSelInt, IntervalText(_pendInterval), _hover == "sel:int");
                    break;
                case R_OP:
                    PaintSlider(g, p, fs, cy);
                    break;
                case R_THEME:
                    PaintSeg(g, p, fs, _rcSegTheme, _segThemeBtns, new string[] { "☀ 浅色", "☾ 深色" }, _pendDark ? 1 : 0, "segtheme");
                    break;
                case R_TOP:
                    PaintToggle(g, p, _rcTglTop, _pendTopMost);
                    break;
                default:
                    PaintToggle(g, p, _rcTglAuto, _pendAutoStart);
                    break;
            }
        }

        private void PaintSeg(Graphics g, SettingsPal p, Font[] fs, Rectangle box, List<Rectangle> btns, string[] labels, int active, string hoverPrefix)
        {
            using (var path = St.RoundRect(box, S(8)))
            using (var b = new SolidBrush(p.Hover))
                g.FillPath(b, path);
            int hoverIdx = HoverIdxOf(hoverPrefix);
            for (int i = 0; i < btns.Count; i++)
            {
                var r = btns[i];
                bool on = i == active;
                bool hot = !on && i == hoverIdx;
                if (on)
                    using (var path = St.RoundRect(r, S(6)))
                    {
                        using (var b = new SolidBrush(p.Win))
                            g.FillPath(b, path);
                        using (var pen = new Pen(p.Line))
                            g.DrawPath(pen, path);
                    }
                TextRenderer.DrawText(g, labels[i], on ? fs[8] : fs[3], r, on || hot ? p.Text : p.Text2,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        private void PaintSelect(Graphics g, SettingsPal p, Font[] fs, Rectangle r, string text, bool hot)
        {
            using (var path = St.RoundRect(r, S(8)))
            {
                using (var b = new SolidBrush(p.Win))
                    g.FillPath(b, path);
                using (var pen = new Pen(hot ? p.CardHover : p.Line))
                    g.DrawPath(pen, path);
            }
            int maxW = r.Width - S(34);
            string s = text;
            while (s.Length > 1 && MeasureW(s + "…", fs[3]) > maxW) s = s.Substring(0, s.Length - 1);
            if (s != text) s += "…";
            using (var b = new SolidBrush(p.Text))
                g.DrawString(s, fs[3], b, new RectangleF(r.X + S(12), r.Y, maxW + S(20), r.Height), SFLeftV);
            using (var pen = new Pen(p.Text2, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = r.Right - S(16), cyy = r.Y + r.Height / 2f;
                g.DrawLine(pen, cx - S(4), cyy - S(2), cx, cyy + S(2));
                g.DrawLine(pen, cx, cyy + S(2), cx + S(4), cyy - S(2));
            }
        }

        private void PaintSlider(Graphics g, SettingsPal p, Font[] fs, int cy)
        {
            var tr = _rcSlider;
            using (var path = St.RoundRect(tr, S(2)))
            using (var b = new SolidBrush(p.Track))
                g.FillPath(b, path);
            int fillW = (int)Math.Round(tr.Width * (_pendOpacity - 50) / 50.0);
            if (fillW > 0)
            {
                var fr = new Rectangle(tr.X, tr.Y, Math.Max(fillW, tr.Height), tr.Height);
                using (var path = St.RoundRect(fr, S(2)))
                using (var b = new SolidBrush(p.Text))
                    g.FillPath(b, path);
            }
            var thumb = new Rectangle(tr.X + fillW - S(8), cy - S(8), S(16), S(16));
            using (var b = new SolidBrush(p.Win))
                g.FillEllipse(b, thumb);
            using (var pen = new Pen(p.Text, 1.6f))
                g.DrawEllipse(pen, thumb);

            using (var b = new SolidBrush(p.Text2))
                g.DrawString(_pendOpacity + "%", fs[3], b, new RectangleF(tr.Right + S(12), cy - S(14), S(48), S(28)), SFLeftV);
        }

        private void PaintToggle(Graphics g, SettingsPal p, Rectangle r, bool on)
        {
            using (var path = St.RoundRect(r, r.Height / 2))
            using (var b = new SolidBrush(on ? p.Text : p.ToggleOff))
                g.FillPath(b, path);
            int d = r.Height - S(4);
            var knob = new Rectangle(on ? r.Right - S(2) - d : r.X + S(2), r.Y + S(2), d, d);
            using (var b = new SolidBrush(p.Win))
                g.FillEllipse(b, knob);
        }

        private void PaintAbout(Graphics g, SettingsPal p, Font[] fs)
        {
            int y = _vp.Y - (int)_scrollP + S(24);

            int cx = _vp.X + _vp.Width / 2;
            int iconY = y + S(16);
            IconFactory.DrawGlyph(g, new Rectangle(cx - S(28), iconY, S(56), S(56)));

            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            using (var b = new SolidBrush(p.Text))
                g.DrawString("AI 额度工具", fs[0], b, new RectangleF(_vp.X, iconY + S(70), _vp.Width, S(26)), sf);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            using (var b = new SolidBrush(p.Text2))
            {
                g.DrawString("v" + AppInfo.Version, fs[3], b, new RectangleF(_vp.X, iconY + S(100), _vp.Width, S(18)), sf);
                g.DrawString("在桌面悬浮展示 GLM / Kimi / MiniMax / DeepSeek 的额度与用量。", fs[3], b, new RectangleF(_vp.X, iconY + S(128), _vp.Width, S(18)), sf);
                g.DrawString("配置明文保存在 exe 同目录 config.json，请勿分享此文件。", fs[3], b, new RectangleF(_vp.X, iconY + S(150), _vp.Width, S(18)), sf);
            }
        }

        private void PaintButton(Graphics g, SettingsPal p, Font[] fs, Rectangle r, string text, bool primary, bool hot)
        {
            using (var path = St.RoundRect(r, S(8)))
            {
                if (primary)
                {
                    using (var b = new SolidBrush(hot ? SettingsPal.Mix(p.Primary, p.Win, 0.12) : p.Primary))
                        g.FillPath(b, path);
                }
                else
                {
                    using (var b = new SolidBrush(hot ? p.Hover : p.Win))
                        g.FillPath(b, path);
                    using (var pen = new Pen(p.Line))
                        g.DrawPath(pen, path);
                }
            }
            Color c = primary ? p.PrimaryText : (hot ? p.Text : p.Text2);
            // GDI TextRenderer 垂直居中：GDI+ DrawString 的行盒对中文字体含大量内边距，
            // 居中后字形明显偏上；与系统按钮控件观感保持一致
            TextRenderer.DrawText(g, text, fs[1], r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private void PaintFooter(Graphics g, SettingsPal p, Font[] fs, int sideW, int footH, int H, int W)
        {
            int top = H - footH;
            using (var pen = new Pen(p.Line2))
                g.DrawLine(pen, sideW, top, W, top);
            using (var b = new SolidBrush(p.Text3))
                g.DrawString("更改在点击「保存」后生效，配置明文保存于 config.json", fs[4], b, new RectangleF(sideW + S(30), top, W - sideW - S(30), footH), SFLeftV);
            PaintButton(g, p, fs, _rcCancel, "取消", false, _hover == "btn:cancel");
            PaintButton(g, p, fs, _rcSave, "保存", true, _hover == "btn:save");
        }

        private void PaintScrollbar(Graphics g, SettingsPal p)
        {
            int max = Math.Max(0, _contentH - _vp.Height);
            if (max <= 0) return;
            float scroll = _page == 0 ? _scrollP : _scrollG;
            int thumbH = Math.Max(S(32), (int)((float)_vp.Height * _vp.Height / _contentH));
            int thumbY = _vp.Y + (int)((_vp.Height - thumbH) * (scroll / max));
            var r = new Rectangle(_vp.Right + S(8), thumbY, S(4), thumbH);
            using (var path = St.RoundRect(r, S(2)))
            using (var b = new SolidBrush(Color.FromArgb(70, p.Text3)))
                g.FillPath(b, path);
        }

        private static void PaintPencil(Graphics g, Color c, float x, float y, float s)
        {
            // 16 盒内铅笔：沿对角的笔杆 + 左下笔尖
            var pts = new PointF[] {
                new PointF(x + 11.3f * s, y + 2.2f * s),
                new PointF(x + 13.8f * s, y + 4.7f * s),
                new PointF(x + 5.4f * s,  y + 13f * s),
                new PointF(x + 2.2f * s,  y + 13.7f * s),
                new PointF(x + 2.9f * s,  y + 10.5f * s)
            };
            using (var pen = new Pen(c, 1.3f) { LineJoin = LineJoin.Round })
                g.DrawPolygon(pen, pts);
        }

        private static void PaintTrash(Graphics g, Color c, float x, float y, float s)
        {
            using (var pen = new Pen(c, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, x + 2.5f * s, y + 4f * s, x + 13.5f * s, y + 4f * s);      // 顶盖
                g.DrawLine(pen, x + 6.5f * s, y + 4f * s, x + 6.5f * s, y + 2.2f * s);     // 提手
                g.DrawLine(pen, x + 9.5f * s, y + 4f * s, x + 9.5f * s, y + 2.2f * s);
                g.DrawLine(pen, x + 6.5f * s, y + 2.2f * s, x + 9.5f * s, y + 2.2f * s);
                g.DrawLines(pen, new PointF[] {                                            // 桶身
                    new PointF(x + 4f * s,    y + 4f * s),
                    new PointF(x + 4.7f * s,  y + 13.2f * s),
                    new PointF(x + 11.3f * s, y + 13.2f * s),
                    new PointF(x + 12f * s,   y + 4f * s)
                });
            }
        }

        private string Elide(Graphics g, string s, Font f, double maxW)
        {
            if (g.MeasureString(s, f).Width <= maxW) return s;
            while (s.Length > 1 && g.MeasureString(s + "…", f).Width > maxW) s = s.Substring(0, s.Length - 1);
            return s + "…";
        }

        // ============================= 数据源 =============================
        private string[] ScreenItems()
        {
            var screens = Screen.AllScreens;
            var items = new string[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                items[i] = (i + 1) + ". " + (screens[i].Primary ? "主显示器" : "显示器 " + (i + 1)) + string.Format(" ({0}×{1})", b.Width, b.Height);
            }
            return items;
        }

        // 刷新间隔候选（秒）：预设 + 已有自定义值（保证旧配置里的任意值可见可选）
        private int[] IntervalVals()
        {
            var vals = new List<int>(IntervalPresets);
            if (!vals.Contains(_pendInterval))
            {
                vals.Add(_pendInterval);
                vals.Sort();
            }
            return vals.ToArray();
        }

        private static string IntervalText(int v)
        {
            return v >= 60 && v % 60 == 0 ? (v / 60) + " 分钟" : v + " 秒";
        }

        // ============================= 交互 =============================
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            HideTip();
            string id = Hit(e.Location);
            Point mp = MousePosition;

            if (id == "moon") { _pendDark = !_pendDark; _hover = null; Invalidate(); return; }
            if (id == "close") { Close(); return; }
            if (id != null && id.StartsWith("nav:")) { SwitchPage(int.Parse(id.Substring(4))); return; }

            // 标题栏空白区拖动窗口（沿用旧版手动拖动：跨屏时实时重建缩放）
            if (id == null && e.Y < S(46))
            {
                _dragWindow = true;
                _dragOffset = new Point(mp.X - Left, mp.Y - Top);
                Capture = true;
                return;
            }

            if (_page == 0)
            {
                if (id == "add") { AddProvider(); return; }
                if (id == "test") { TestAll(); return; }
                if (id != null && id.StartsWith("grip:") && int.Parse(id.Substring(5)) < _cardRects.Count)
                {
                    _dragCard = int.Parse(id.Substring(5));
                    _grabDY = Math.Max(0, Math.Min(S(54), e.Y - _cardRects[_dragCard].Y)); // 抓点在卡片内的相对位置
                    _dragDrawY = _cardRects[_dragCard].Y;
                    Capture = true;
                    return;
                }
                if (id != null && id.StartsWith("edit:")) { EditCard(int.Parse(id.Substring(5))); return; }
                if (id != null && id.StartsWith("del:")) { DeleteCard(int.Parse(id.Substring(4))); return; }
            }
            else if (_page == 1)
            {
                if (id != null && id.StartsWith("segmode:")) { SetMode(int.Parse(id.Substring(8))); return; } // 段位 0=悬浮窗 / 1=置顶栏，与 _pendMode 一致
                if (id != null && id.StartsWith("seglayout:")) { _pendIsland = int.Parse(id.Substring(10)) == 0; Invalidate(); return; } // 段位 0=条形灵动岛 / 1=矩形卡片
                if (id != null && id.StartsWith("segtheme:")) { _pendDark = int.Parse(id.Substring(9)) == 1; Invalidate(); return; }
                if (id == "tgl:top") { _pendTopMost = !_pendTopMost; Invalidate(); return; }
                if (id == "tgl:auto") { _pendAutoStart = !_pendAutoStart; Invalidate(); return; }
                if (id == "sel:screen") { OpenScreenPopup(); return; }
                if (id == "sel:int") { OpenIntervalPopup(); return; }
                if (id == "sld:op") { _dragSlider = true; Capture = true; SliderSet(e.X); return; }
            }

            if (id == "btn:cancel") { Close(); return; }
            if (id == "btn:save") { SaveAndClose(); return; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragWindow)
            {
                Location = new Point(MousePosition.X - _dragOffset.X, MousePosition.Y - _dragOffset.Y);
                return;
            }
            if (_dragCard >= 0) { CardDragMove(e.Y); return; }
            if (_dragSlider) { SliderSet(e.X); return; }

            string h = Hit(e.Location);
            if (h != _hover)
            {
                _hover = h;
                Cursor = h == null ? Cursors.Default
                    : (h.StartsWith("grip:") ? Cursors.SizeAll
                    : (h.StartsWith("tip:") ? Cursors.Default : Cursors.Hand));
                UpdateTip(h);
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragWindow = false;
            _dragSlider = false;
            _dragCard = -1;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_dragWindow || _dragCard >= 0 || _dragSlider) return; // 拖拽中的 MouseLeave（含 Capture 边界）不当作真离开
            if (_hover != null) { _hover = null; Cursor = Cursors.Default; Invalidate(); }
            HideTip();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_page == 2 || _dragCard >= 0) return; // 拖拽排序中禁滚动，避免拖拽卡与列表错位
            HideTip();
            if (_page == 0) _scrollP -= e.Delta * S(52) / 120f; else _scrollG -= e.Delta * S(52) / 120f;
            LayoutAll();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_built) LayoutAll();
        }

        private void SwitchPage(int idx)
        {
            if (_page == idx) return;
            _page = idx;
            _hover = null;
            ClosePopup();
            HideTip();
            LayoutAll();
            Invalidate();
        }

        private void SetMode(int m)
        {
            if (_pendMode == m) return;
            _pendMode = m;
            ClosePopup();
            HideTip();
            LayoutAll(); // 屏幕行显隐改变内容高度，滚动随之收敛
            Invalidate();
        }

        private void SliderSet(int mouseX)
        {
            int v = 50 + (int)Math.Round((mouseX - _rcSlider.X) * 50.0 / Math.Max(1, _rcSlider.Width));
            v = Math.Max(50, Math.Min(100, v));
            if (v != _pendOpacity) { _pendOpacity = v; Invalidate(); }
        }

        // 拖拽跟手：被拖卡片以浮层形式跟随鼠标（画在 _dragDrawY），其余卡片让位；
        // 拖拽卡中心线越过相邻卡中点时实时换位（列表顺序即最终顺序，松手无需再处理）
        private void CardDragMove(int mouseY)
        {
            if (_dragCard < 0 || _dragCard >= _cardRects.Count || _cardRects.Count == 0) return;
            int cardH = S(54), gap = S(10);
            int listTop = _cardRects[0].Y;
            int listBottom = listTop + _editList.Count * (cardH + gap) - gap;
            _dragDrawY = Math.Max(listTop, Math.Min(listBottom - cardH, mouseY - _grabDY));

            int idx = (int)Math.Floor((_dragDrawY + cardH / 2.0 - listTop) / (cardH + gap));
            idx = Math.Max(0, Math.Min(_editList.Count - 1, idx));
            if (idx != _dragCard)
            {
                var item = _editList[_dragCard];
                _editList.RemoveAt(_dragCard);
                _editList.Insert(idx, item);
                _dragCard = idx;
                LayoutAll();
            }
            Invalidate();
        }

        // ---- 下拉弹层 ----
        private void OpenPopup(Rectangle anchorClient, string[] items, int selected, Action<int> pick)
        {
            ClosePopup();
            Font[] fs = Fonts(_scale);
            _popup = new SettingsPopupList(RectangleToScreen(anchorClient), items, selected, P, fs[3], fs[8], _scale, pick);
            _popup.Show(this);
        }

        private void ClosePopup()
        {
            if (_popup != null && !_popup.IsDisposed)
                _popup.Close();
            _popup = null;
        }

        private void OpenScreenPopup()
        {
            _pendScreen = Math.Max(0, Math.Min(Screen.AllScreens.Length - 1, _pendScreen));
            string[] items = ScreenItems();
            int sel = _pendScreen;
            OpenPopup(_rcSelScreen, items, sel, delegate(int idx)
            {
                _pendScreen = idx;
                Invalidate();
            });
        }

        private void OpenIntervalPopup()
        {
            int[] vals = IntervalVals();
            var items = new string[vals.Length];
            for (int i = 0; i < vals.Length; i++) items[i] = IntervalText(vals[i]);
            int sel = 0;
            for (int i = 0; i < vals.Length; i++) if (vals[i] == _pendInterval) sel = i;
            OpenPopup(_rcSelInt, items, sel, delegate(int idx)
            {
                _pendInterval = vals[idx];
                Invalidate();
            });
        }

        // ============================= 行说明气泡 =============================
        private void UpdateTip(string hoverId)
        {
            int row = -1;
            if (_page == 1 && hoverId != null && hoverId.StartsWith("tip:"))
                row = int.Parse(hoverId.Substring(4));
            if (row == _tipRow) return;
            HideTip();
            if (row >= 0 && row <= R_AUTO)
            {
                _tipRow = row;
                _tipText = RowDesc(row);
                _tipTimer.Stop();
                _tipTimer.Start(); // 悬停 450ms 后浮现（见 ctor 的 Tick）
            }
        }

        private void ShowTip()
        {
            if (_tipText == null) return;
            if (_tip == null || _tip.IsDisposed) _tip = new HoverTip();
            _tip.ShowAt(Cursor.Position, _tipText, P, _scale);
        }

        private void HideTip()
        {
            _tipTimer.Stop();
            _tipRow = -1;
            _tipText = null;
            if (_tip != null && !_tip.IsDisposed) _tip.Hide();
        }

        // ---- 供应商增删改 ----
        private void AddProvider()
        {
            using (var dlg = new ProviderEditDialog(null, P, _scale))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    _editList.Add(dlg.Result);
                    LayoutAll();
                    Invalidate();
                }
            }
        }

        private void EditCard(int idx)
        {
            if (idx < 0 || idx >= _editList.Count) return;
            using (var dlg = new ProviderEditDialog(_editList[idx], P, _scale))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    _editList[idx] = dlg.Result;
                    LayoutAll();
                    Invalidate();
                }
            }
        }

        private void DeleteCard(int idx)
        {
            if (idx < 0 || idx >= _editList.Count) return;
            _editList.RemoveAt(idx);
            LayoutAll();
            Invalidate();
        }

        // ---- 跨屏 DPI：拖动换屏时按新缩放重建尺寸与布局（状态在字段里，无需备份回填） ----
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!_built || !IsHandleCreated) return;
            try
            {
                var screen = Screen.FromControl(this);
                float n = ClampScale(St.DpiOf(screen) / 96f, screen);
                if (Math.Abs(n - _scale) > 0.01f)
                {
                    _scale = n;
                    ClosePopup();
                    ApplyScaleToSize();
                    LayoutAll();
                    // 拖动中让窗口贴住鼠标抓点（尺寸变了，位置按屏幕坐标偏移重算）
                    if (_dragWindow) Location = new Point(MousePosition.X - _dragOffset.X, MousePosition.Y - _dragOffset.Y);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SettingsForm 跨屏重建失败: " + ex.Message);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            St.RoundCorners(Handle); // 四角交给 DWM 做抗锯齿圆角（与设计稿 10px 圆角观感一致）
            EnsureBorderlessStyle();
        }

        // ---- 无边框样式自愈 ----
        // WinForms 在最小化恢复 / 句柄重建时可能把 WS_CAPTION 加回窗口（表现为顶部突然出现
        // 原生标题栏和最小化/关闭按钮）。命中即剥掉并触发一次框架重算；样式正常时是空操作。
        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint flags);

        private void EnsureBorderlessStyle()
        {
            if (!IsHandleCreated) return;
            int style = GetWindowLong(Handle, GWL_STYLE);
            if ((style & WS_CAPTION) != 0)
            {
                SetWindowLong(Handle, GWL_STYLE, style & ~WS_CAPTION);
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            EnsureBorderlessStyle();
        }

        // 无边框窗口保留 DWM 阴影与 Win11 自动圆角：加 WS_THICKFRAME 样式，
        // 再用 WM_NCCALCSIZE 吞掉非客户区，使客户区 = 整窗（Win7/8 无圆角仅剩细边，可接受）
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00040000; // WS_THICKFRAME
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCCALCSIZE = 0x0083;
            const int WM_NCHITTEST = 0x0084;
            const int WM_NCACTIVATE = 0x0086;
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero; // 移除标准边框，标题栏由自绘负责
                return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)1; // HT_CLIENT：窗口不可 resize，命中全部交给自绘
                return;
            }
            if (m.Msg == WM_NCACTIVATE)
            {
                // 失焦时 DefWindowProc 会让 DWM 重绘系统边框（带标题文字的失焦框盖在自绘
                // 标题栏上）。窗口全自绘、无真实非客户区，直接吞掉：不调用默认过程，返回 TRUE
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        // ============================= 保存 / 测试 =============================
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
            string oldFloatLayout = _ctx.Cfg.FloatLayout;

            // 拷贝而非直接赋值：保存失败时窗口保持打开，用户继续增删/移动供应商会突变
            // _editList；若与 Cfg.Providers 共享同一 List，刷新线程池并行遍历会因列表收缩
            // 抛 ArgumentOutOfRangeException 并终止进程（详见 RefreshData 的 providers[i]）
            _ctx.Cfg.Providers = new List<ProviderConfig>(_editList);
            _ctx.Cfg.RefreshSec = _pendInterval;
            _ctx.Cfg.OpacityPct = _pendOpacity;
            _ctx.Cfg.TopMost = _pendTopMost;
            _ctx.Cfg.AutoStart = _pendAutoStart;
            _ctx.Cfg.Theme = _pendDark ? "dark" : "light";
            _ctx.Cfg.DisplayMode = _pendMode == 1 ? "topbar" : "float";
            _ctx.Cfg.AppBarScreen = _pendScreen;
            _ctx.Cfg.FloatLayout = _pendIsland ? "island" : "card";
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
                _ctx.Cfg.FloatLayout = oldFloatLayout;
                StyledMsg.Show(this, "保存失败", "配置保存失败（config.json 写入失败，可能磁盘已满或被其他程序占用）。\n窗口保持打开，请先记录本次填写的 API Key，再重试保存。", P, _scale);
                return;
            }
            _ctx.ApplyConfig();
            Close();
        }

        private void TestAll()
        {
            if (_testing) return;
            if (_editList.Count == 0)
            {
                StyledMsg.Show(this, "测试连接", "请先添加供应商", P, _scale);
                return;
            }
            _testing = true;
            _hover = null;
            Cursor = Cursors.Default;
            Invalidate();
            var snapshot = new List<ProviderConfig>(_editList);
            ThreadPool.QueueUserWorkItem(delegate
            {
                var sb = new StringBuilder();
                foreach (var pr in snapshot)
                {
                    if (!pr.HasKey) continue;   // 未配置 key 的供应商跳过（密文解不开的由 RequireKey 报可读错误）
                    try
                    {
                        ProviderStatus st;
                        if (pr.Type == "mx") st = QuotaService.FetchMiniMax(pr);
                        else if (pr.Type == "kimi") st = QuotaService.FetchKimi(pr);
                        else if (pr.Type == "ds") st = QuotaService.FetchDeepSeek(pr);
                        else st = QuotaService.FetchGlm(pr);
                        sb.AppendLine("【" + pr.Name + "】连接成功 ✓  " + st.Pools.Count + " 个额度池");
                        foreach (var q in st.Pools)
                            sb.AppendLine("   · " + q.Label + "：" + (q.IsMoney ? q.Currency + St.FmtMoney(q.Remaining) : "已用 " + q.Percent + "%"));
                    }
                    catch (Exception ex) { sb.AppendLine("【" + pr.Name + "】失败 ✗  " + ex.Message); }
                }
                string report = sb.ToString();
                // 测试耗时可达数分钟，期间窗口可能已被用户关闭（句柄销毁）；无 catch 的线程池
                // 线程上抛异常会终止整个进程，故与 AppContext.RefreshData 相同：BeginInvoke 外包
                // try-catch，回调内再判 IsDisposed 跳过弹窗
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        _testing = false;
                        if (IsDisposed) return;
                        LayoutAll(); // 按钮文本宽度随文案变化（测试全部 / 测试中…）
                        Invalidate();
                        TestResultDialog.Show(this, report, P, _scale);
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
