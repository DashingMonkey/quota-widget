// QuotaWidget - AI 额度工具 (GLM Coding Plan / MiniMax Token Plan / Kimi Code Plan / DeepSeek 余额)
// 编译: csc.exe /nologo /utf8output /target:winexe /optimize+ /win32icon:QuotaWidget.ico /out:QuotaWidget.exe QuotaWidget.cs /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

    // ============================= 崩溃日志 =============================
    // winexe 无控制台、无调试器时 Debug.WriteLine 不可见，未处理异常只闪一下系统
    // WER 对话框就消失，用户侧表现为"双击没反应"且无从排查。启动失败/未处理异常
    // 统一落盘到这里，让任何机器上的错误都能事后取证。
    internal static class CrashLog
    {
        // 崩溃路径上的日志 IO 任何情况下不得再抛异常（二次异常会把根因彻底吞掉）
        public static void Write(string context, Exception ex)
        {
            try
            {
                string path = ResolvePath();
                if (string.IsNullOrEmpty(path)) return;
                // 超 1MB 轮转：改名保留上一份（error.old.log），避免长期运行无限增长
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    {
                        string old = path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                }
                catch { }
                string entry = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] v" + AppInfo.Version
                    + " " + context + Environment.NewLine
                    + (ex == null ? "(无异常对象)" :
                        ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace)
                    + Environment.NewLine + Environment.NewLine;
                File.AppendAllText(path, entry, Encoding.UTF8);
            }
            catch { }
        }

        // 优先 exe 同目录 error.log（与 config.json 约定一致，用户最容易找到）；
        // exe 位于不可写目录（如 Program Files）时回退 %LOCALAPPDATA%\QuotaWidget\
        private static string ResolvePath()
        {
            try
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                try
                {
                    // 用打开方式探测可写性：AppendAllText 到只读目录会抛 UnauthorizedAccessException
                    using (File.Open(local, FileMode.Append, FileAccess.Write, FileShare.Read)) { }
                    return local;
                }
                catch { }
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaWidget");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "error.log");
            }
            catch { return null; }
        }
    }

    // ============================= 数据模型 =============================
    internal class ProviderConfig
    {
        public string Type { get; set; }     // "glm" / "mx" / "kimi" / "ds" / "claude" / "codex"
        public string Region { get; set; }   // "cn" / "intl"
        // 旧明文字段：仅用于读取旧 config.json 完成迁移，序列化时忽略（迁移后恒为空）
        [ScriptIgnore]
        public string Key { get; set; }
        // DPAPI(CurrentUser) 加密的 Base64 密文：绑定本机当前 Windows 用户，
        // config.json 被其他软件扫描/拷走到别的电脑均无法还原
        public string KeyEnc { get; set; }
        public string Name { get; set; }     // 显示名称
        // 悬浮窗/置顶栏展示方式：false(默认)=图标模式（品牌图标+外圈环形进度+百分比）；
        // true=名称模式（显示自定义名称）。旧配置无此字段，加载时按是否自定义过名称推断
        public bool ShowName { get; set; }

        // 各接口默认显示名（与设置对话框 TypeItems 顺序一致）；编辑对话框与旧配置迁移共用
        internal static readonly string[] TypeDefaultNames = { "GLM", "GLM", "MiniMax", "MiniMax", "Kimi", "DeepSeek", "Claude", "Codex" };

        /// type+region → 接口下拉项索引（编辑对话框预填/保存与旧配置迁移共用）
        internal static int TypeIndex(string type, string region)
        {
            if (type == "codex") return 7;
            if (type == "claude") return 6;
            if (type == "ds") return 5;
            if (type == "kimi") return 4;
            if (type == "mx") return region == "intl" ? 3 : 2;
            return region == "intl" ? 1 : 0;
        }

        internal static string TypeDisplay(string type, string region)
        {
            if (type == "glm") return region == "intl" ? "GLM 国际版" : "GLM 国内版";
            if (type == "mx") return region == "intl" ? "MiniMax 国际版" : "MiniMax 国内版";
            if (type == "kimi") return "Kimi Code Plan";
            if (type == "ds") return "DeepSeek";
            if (type == "claude") return "Claude Pro/Max";
            if (type == "codex") return "Codex (ChatGPT 套餐)";
            return type;
        }

        /// 悬浮窗/托盘等处的展示文本：图标模式用接口类型名（自定义名仅在名称模式下展示）
        [ScriptIgnore]
        public string DisplayName { get { return ShowName ? Name : TypeDisplay(Type, Region); } }

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

        public ProviderConfig Clone() { return new ProviderConfig { Type = Type, Region = Region, Key = Key, KeyEnc = KeyEnc, Name = Name, ShowName = ShowName }; }

        [ScriptIgnore]
        public string TypeName
        {
            get { return TypeDisplay(Type, Region); }
        }

        // claude/codex 无自建"余额查询 API Key"：Key 留空表示自动读取本机 CLI 登录凭证
        // （Claude Code / Codex 登录后落盘的 OAuth 令牌，由 CLI 负责刷新），
        // 未配置 key 不等于不可查询，刷新循环不应跳过
        [ScriptIgnore]
        public bool CanUseLocalCreds { get { return (Type == "claude" || Type == "codex") && !HasKey; } }
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
        public string Type = "";        // "glm" / "mx" / "kimi" / "ds" / "claude" / "codex" 用于决定主题色
        public string Region = "";
        public bool ShowName = true;    // false=悬浮窗/置顶栏以品牌图标+环形进度展示（随 ProviderConfig 同步）
        public string Level = "";
        public string Error = null;
        public List<QuotaPool> Pools = new List<QuotaPool>();
        public DateTime UpdatedAt = DateTime.Now;
        public QuotaPool Primary5h;

        // 悬浮窗/托盘等文本场景的展示名：图标模式显示接口类型名（如"GLM 国内版"）
        [ScriptIgnore]
        public string DisplayName { get { return ShowName ? Name : ProviderConfig.TypeDisplay(Type, Region); } }
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
        public bool LockPosition { get; set; }   // 固定位置：禁止拖拽悬浮窗
        public string DisplayMode { get; set; }  // "float"(悬浮窗) / "topbar"(置顶栏)
        public int AppBarScreen { get; set; }    // 置顶栏模式：显示在哪个屏幕（索引，默认0=主屏）
        public string FloatLayout { get; set; }  // 悬浮窗排列："island"(条形灵动岛，默认) / "card"(矩形卡片)
        public int DockSide { get; set; }        // 灵动岛吸附边（重启恢复用）：-1 未吸附 / 0 顶 / 1 左 / 2 右
        public bool Vertical { get; set; }       // 竖向岛形态（左右吸附或竖向固定时为 true，重启恢复用）

        public Config()
        {
            Providers = new List<ProviderConfig>();
            GlmKey = ""; GlmRegion = "cn"; MxKey = ""; MxRegion = "cn";
            RefreshSec = 60; OpacityPct = 95; TopMost = true; Theme = "light"; X = -1; Y = -1; AutoStart = false; LockPosition = false; DockSide = -1;
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
                    // 旧配置无 ShowName 字段：自定义过名称的供应商保持名称模式，其余进入图标模式
                    if (!p.ShowName)
                    {
                        string def = ProviderConfig.TypeDefaultNames[ProviderConfig.TypeIndex(p.Type, p.Region)];
                        if (!string.IsNullOrEmpty(p.Name) && p.Name != def) p.ShowName = true;
                    }
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
            return HttpGet(url, authorization, null, null, null);
        }

        // ua/额外头：Claude 的 OAuth 用量接口必须带 Claude Code 的 UA 与 anthropic-beta 头，
        // 否则会被网关持续 429（anthropics/claude-code #31021）；Codex 需带 CLI 风格 UA 与账号头
        // 非 2xx 响应体的降级显示：把 JSON 错误结构提炼成一句可读文案，提炼不出再退回
        // 「状态码短语 + 截断原文」，避免把 {"error":{...}} 整段原样甩进悬浮窗报错明细。
        // 各端错误形态不一，按优先级逐层提取：
        //   Anthropic  {"error":{"type":"forbidden","message":"..."}}
        //   OpenAI     {"error":{"message":"...","type":"...","code":"..."}}
        //   ChatGPT 后端 {"detail":{"message":"...","code":...}} 或 {"detail":"..."}
        //   通用       {"message":"..."} / {"msg":"..."} / {"content":"..."}
        private static string DescribeHttpError(int status, string body, string fallback)
        {
            string detail = null, kind = null;
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                    if (root != null)
                    {
                        object errObj;
                        if (root.TryGetValue("error", out errObj))
                        {
                            var err = errObj as Dictionary<string, object>;
                            if (err != null)
                            {
                                detail = J.S(err, "message");
                                kind = J.S(err, "type");
                                if (string.IsNullOrEmpty(kind) && J.HasKey(err, "code"))
                                    kind = J.S(err, "code");   // code 可能是数字，S 的 ToString 足矣
                            }
                            else detail = errObj as string;    // error 直接是字符串的形态
                        }
                        if (string.IsNullOrEmpty(detail))
                        {
                            object dObj;
                            if (root.TryGetValue("detail", out dObj))
                            {
                                var d = dObj as Dictionary<string, object>;
                                if (d != null)
                                {
                                    detail = J.S(d, "message");
                                    if (string.IsNullOrEmpty(detail)) detail = J.S(d, "content");
                                    if (string.IsNullOrEmpty(kind)) kind = J.S(d, "code");
                                }
                                else detail = dObj as string;
                            }
                        }
                        if (string.IsNullOrEmpty(detail)) detail = J.S(root, "message");
                        if (string.IsNullOrEmpty(detail)) detail = J.S(root, "msg");
                        if (string.IsNullOrEmpty(detail)) detail = J.S(root, "content");
                    }
                }
                catch { }   // 非 JSON 响应体（如 HTML 错误页）：走下方原文截断
            }

            detail = detail == null ? null : detail.Trim();
            if (!string.IsNullOrEmpty(detail))
            {
                if (detail.Length > 160) detail = detail.Substring(0, 160) + "…";
                // 保留 type/code（如 forbidden / insufficient_quota）作为定位线索
                return string.IsNullOrEmpty(kind) ? detail : detail + "（" + kind + "）";
            }

            string reason;
            switch (status)
            {
                case 0: return string.IsNullOrEmpty(fallback) ? "网络请求失败" : fallback;   // 无响应（连接失败/超时），we.Message 本身可读
                case 401: reason = "请求未授权（401）：登录凭证可能已失效"; break;
                case 403: reason = "请求被拒绝（403）"; break;
                case 404: reason = "接口不存在（404）"; break;
                case 429: reason = "请求过于频繁（429）"; break;
                default: reason = status >= 500 ? "服务端错误（" + status + "）" : "请求失败（HTTP " + status + "）"; break;
            }
            if (string.IsNullOrEmpty(body)) return reason;
            string raw = body.Trim();
            if (raw.Length > 80) raw = raw.Substring(0, 80) + "…";
            return reason + "：" + raw;
        }

        private static string HttpGet(string url, string authorization, string ua, string extraName, string extraValue)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = string.IsNullOrEmpty(ua) ? "QuotaWidget/" + AppInfo.Version : ua;
            req.Accept = "application/json";
            if (authorization != null) req.Headers["Authorization"] = authorization;
            if (!string.IsNullOrEmpty(extraName) && !string.IsNullOrEmpty(extraValue)) req.Headers[extraName] = extraValue;
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
                throw new HttpException(status, DescribeHttpError(status, body, we.Message));
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

            var st = new ProviderStatus { Name = p.Name, Type = "glm", Region = p.Region, ShowName = p.ShowName };
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

            var st = new ProviderStatus { Name = p.Name, Type = "mx", Region = p.Region, ShowName = p.ShowName };

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

            var st = new ProviderStatus { Name = p.Name, Type = "kimi", Region = p.Region, ShowName = p.ShowName };

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

            var st = new ProviderStatus { Name = p.Name, Type = "ds", Region = p.Region, ShowName = p.ShowName };
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

        // ---------- Claude Pro/Max ----------
        // 无公开 API：Claude 订阅套餐走 Claude Code 自身的 OAuth 用量接口
        //   GET https://api.anthropic.com/api/oauth/usage（Anthropic 未公开文档，官方 CLI 在用，
        //   ccusage 等社区工具长期依赖）。必须携带 Claude Code 风格 UA + anthropic-beta 头，
        //   否则网关持续 429。响应（utilization 为已用百分比 0-100，resets_at 为 ISO 8601）：
        //   {"five_hour":{"utilization":42.0,"resets_at":"..."},"seven_day":{...}}
        // 鉴权：OAuth 访问令牌（sk-ant-oat01-...）。Key 留空 = 自动读取
        //   %USERPROFILE%\.claude\.credentials.json（Claude Code 登录后落盘，由其自行刷新）。
        static readonly object _claudeLock = new object();
        static DateTime _claudeLastTry = DateTime.MinValue;
        static ProviderStatus _claudeCache;
        static string _claudeCacheErr;

        public static ProviderStatus FetchClaude(ProviderConfig p, bool force)
        {
            // 该接口按账户限速，安全轮询间隔约 180s；悬浮窗刷新周期小于此值时复用上次结果
            // （成功与失败都节流，避免 60s 轮询把偶发 429 放大成持续封禁）。
            // 主动"测试连接"传 force 绕过。多个 Claude 供应商共享同一节流窗口（同账户场景）。
            if (!force)
            {
                lock (_claudeLock)
                {
                    if ((DateTime.Now - _claudeLastTry).TotalSeconds < 170)
                    {
                        if (_claudeCache != null) return _claudeCache;
                        if (_claudeCacheErr != null) throw new Exception(_claudeCacheErr);
                    }
                }
            }
            try
            {
                var st = FetchClaudeCore(p);
                lock (_claudeLock) { _claudeCache = st; _claudeCacheErr = null; _claudeLastTry = DateTime.Now; }
                return st;
            }
            catch (Exception ex)
            {
                lock (_claudeLock) { _claudeCacheErr = ex.Message; _claudeLastTry = DateTime.Now; }
                throw;
            }
        }

        static ProviderStatus FetchClaudeCore(ProviderConfig p)
        {
            string tok = p.HasKey ? RequireKey(p) : ReadLocalCreds(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"),
                "claudeAiOauth", "accessToken",
                "未找到 Claude 登录凭证（%USERPROFILE%\\.claude\\.credentials.json）：请先在 Claude Code 登录，或在设置中粘贴访问令牌");

            string json;
            try
            {
                json = HttpGet("https://api.anthropic.com/api/oauth/usage", "Bearer " + tok,
                    "claude-code/2.0.14 (external, cli)", "anthropic-beta", "oauth-2025-04-20");
            }
            catch (HttpException he)
            {
                // 网关层 401/403 在 HttpGet 就已抛出（走不到下方 error JSON 解析），在此补充本机修复动作
                if (he.StatusCode == 401 || he.StatusCode == 403)
                    throw new Exception("Claude: " + he.Message );
                throw;
            }

            var ser = new JavaScriptSerializer();
            var root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null) throw new Exception("Claude: 响应为空");

            // 错误返回：{"type":"error","error":{"type":"authentication_error","message":"..."}}
            object errObj;
            if (root.TryGetValue("error", out errObj))
            {
                var err = errObj as Dictionary<string, object>;
                if (err != null)
                {
                    string msg = J.S(err, "message");
                    if (string.IsNullOrEmpty(msg)) msg = "请求被拒绝";
                    throw new Exception("Claude: " + msg );
                }
            }

            var st = new ProviderStatus { Name = p.Name, Type = "claude", Region = p.Region, ShowName = p.ShowName };
            AddUtilWindow(st, root, "five_hour", "5 小时窗口额度", true);
            AddUtilWindow(st, root, "seven_day", "周额度", false);
            if (st.Primary5h == null && st.Pools.Count > 0) st.Primary5h = st.Pools[0];
            if (st.Pools.Count == 0) throw new Exception("Claude: 响应缺少用量窗口字段（接口可能已变更）");
            st.UpdatedAt = DateTime.Now;
            return st;
        }

        // utilization 窗口 → 池（仅百分比，无绝对量）
        static void AddUtilWindow(ProviderStatus st, Dictionary<string, object> root, string key, string label, bool primary)
        {
            object wo;
            if (!root.TryGetValue(key, out wo)) return;
            var w = wo as Dictionary<string, object>;
            if (w == null) return;
            double util = J.D(w, "utilization");       // 已用百分比 0-100
            if (util <= 0 && !J.HasKey(w, "utilization")) return;
            int pct = Math.Max(0, Math.Min(100, (int)Math.Round(util)));
            var pool = new QuotaPool
            {
                Label = label,
                PercentOnly = true,                    // 接口只给百分比，无绝对额度
                Used = util, Total = 100, Remaining = 100 - util, Percent = pct,
                ResetLocal = ParseTimeFlexible(J.S(w, "resets_at"))
            };
            st.Pools.Add(pool);
            if (primary) st.Primary5h = pool;
        }

        // ---------- Codex (ChatGPT 套餐) ----------
        // 无公开 API：走 Codex CLI 每 60s 轮询的用量接口
        //   GET https://chatgpt.com/backend-api/wham/usage（openai/codex 后端客户端同款，未公开文档）。
        // 鉴权：ChatGPT OAuth 访问令牌（Bearer）。Key 留空 = 自动读取
        //   %USERPROFILE%\.codex\auth.json 的 tokens.access_token（Codex 登录后落盘，由其自行刷新）。
        // 响应字段为 snake_case：
        //   {"plan_type":"plus","rate_limit":{"allowed":true,"limit_reached":false,
        //    "primary_window":{"used_percent":18,"limit_window_seconds":18000,"reset_at":1730000000},
        //    "secondary_window":{...周窗口...}}}
        public static ProviderStatus FetchCodex(ProviderConfig p)
        {
            string accountId = null;
            string tok = p.HasKey ? RequireKey(p) : CodexLocalToken(ref accountId);
            if (string.IsNullOrEmpty(accountId)) accountId = null;

            string json;
            try
            {
                json = HttpGet("https://chatgpt.com/backend-api/wham/usage", "Bearer " + tok,
                    "codex_cli_rs", "chatgpt-account-id", accountId);
            }
            catch (HttpException he)
            {
                // 令牌被拒时补充本机修复动作，与"响应缺少 rate_limit"的提示口径一致
                if (he.StatusCode == 401 || he.StatusCode == 403)
                    throw new Exception("Codex: " + he.Message + "（访问令牌可能已过期，请运行一次 codex 刷新登录）");
                throw;
            }

            var ser = new JavaScriptSerializer();
            var root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null) throw new Exception("Codex: 响应为空");

            var st = new ProviderStatus { Name = p.Name, Type = "codex", Region = p.Region, ShowName = p.ShowName };
            string plan = J.S(root, "plan_type");
            if (!string.IsNullOrEmpty(plan)) st.Level = char.ToUpper(plan[0]) + plan.Substring(1); // Plus / Pro / Team…

            object rlObj;
            if (!root.TryGetValue("rate_limit", out rlObj) || !(rlObj is Dictionary<string, object>))
                throw new Exception("Codex: 响应缺少 rate_limit（访问令牌可能已过期，请运行一次 codex 刷新登录）");
            var rl = (Dictionary<string, object>)rlObj;

            // 主窗口按窗口长度命名（典型 5 小时），次窗口为周额度
            AddCodexWindow(st, rl, "primary_window", true);
            AddCodexWindow(st, rl, "secondary_window", false);
            if (st.Primary5h == null && st.Pools.Count > 0) st.Primary5h = st.Pools[0];
            if (st.Pools.Count == 0) throw new Exception("Codex: 响应中无用量窗口（接口可能已变更）");
            st.UpdatedAt = DateTime.Now;
            return st;
        }

        static void AddCodexWindow(ProviderStatus st, Dictionary<string, object> rl, string key, bool primary)
        {
            object wo;
            if (!rl.TryGetValue(key, out wo)) return;
            var w = wo as Dictionary<string, object>;
            if (w == null) return;

            int pct = Math.Max(0, Math.Min(100, (int)Math.Round(J.D(w, "used_percent"))));
            int winSec = (int)J.D(w, "limit_window_seconds");
            string label = winSec >= 7 * 86400 ? "周额度" : (winSec >= 3600 ? (winSec / 3600) + " 小时窗口额度" : "频率窗口额度");

            var pool = new QuotaPool
            {
                Label = label,
                PercentOnly = true,                    // 仅百分比
                Used = pct, Total = 100, Remaining = 100 - pct, Percent = pct
            };
            object ro;
            if (w.TryGetValue("reset_at", out ro) && ro != null)
            {
                long t;
                if (long.TryParse(ro.ToString(), out t) && t > 0) pool.ResetLocal = DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;
            }
            object reached;
            if (rl.TryGetValue("limit_reached", out reached) && reached is bool && (bool)reached)
                pool.Note = "已达上限";
            st.Pools.Add(pool);
            if (primary) st.Primary5h = pool;
        }

        static string CodexLocalToken(ref string accountId)
        {
            accountId = null;
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
            if (!File.Exists(path))
                throw new Exception("未找到 Codex 登录凭证（%USERPROFILE%\\.codex\\auth.json）：请先运行 codex 登录，或在设置中粘贴 ChatGPT 访问令牌");
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            object toks;
            if (root != null && root.TryGetValue("tokens", out toks))
            {
                var d = toks as Dictionary<string, object>;
                if (d != null)
                {
                    accountId = J.S(d, "account_id");
                    string t = J.S(d, "access_token");
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            throw new Exception("auth.json 中无 ChatGPT 访问令牌（可能为 API Key 模式）：请用 ChatGPT 套餐账号运行 codex 登录");
        }

        // 从本机 CLI 凭证 JSON 读取 OAuth 字段（结构不符时抛可读错误）
        static string ReadLocalCreds(string path, string section, string field, string missingMsg)
        {
            if (!File.Exists(path)) throw new Exception(missingMsg);
            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path)); }
            catch { throw new Exception("凭证文件解析失败：" + path); }
            object sec;
            if (root != null && root.TryGetValue(section, out sec))
            {
                var d = sec as Dictionary<string, object>;
                if (d != null)
                {
                    string v = J.S(d, field);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            throw new Exception("凭证文件缺少 " + section + "." + field + "：" + path);
        }
    }

    // ============================= 样式 =============================
    internal static class St
    {
        // 主题: 1=深色 2=浅色
        public static int Theme = 2;

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
        public static Color Bg { get { return Theme == 1 ? Color.FromArgb(20, 20, 25) : Color.FromArgb(237, 238, 242); } }
        public static Color Card { get { return Theme == 1 ? Color.FromArgb(20, 20, 25) : Color.FromArgb(255, 255, 255); } }
        public static Color Text { get { return Theme == 1 ? Color.FromArgb(232, 234, 240) : Color.FromArgb(51, 65, 85); } }
        public static Color Dim { get { return Theme == 1 ? Color.FromArgb(158, 166, 186) : Color.FromArgb(100, 116, 139); } }
        public static Color BarBg { get { return Theme == 1 ? Color.FromArgb(86, 92, 112) : Color.FromArgb(206, 211, 221); } }
        // 窗口最外圈细线：深色主题用浅线，浅色主题用深线，保证在任何壁纸上都有轮廓
        public static Color Border { get { return Theme == 1 ? Color.FromArgb(88, 94, 114) : Color.FromArgb(185, 191, 203); } }
        public static Color Green { get { return Theme == 1 ? Color.FromArgb(52, 211, 153) : Color.FromArgb(46, 139, 87); } }
        public static Color Amber { get { return Theme == 1 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(190, 120, 24); } }
        public static Color Red { get { return Theme == 1 ? Color.FromArgb(248, 113, 113) : Color.FromArgb(198, 64, 48); } }
        // 各服务商 accent 的唯一定义：t=1 深色（为深底对比提亮的变体）/ t=2 浅色（高饱和原色）
        static Color Acc(string type, int t)
        {
            if (type == "mx") return t == 1 ? Color.FromArgb(56, 189, 248) : Color.FromArgb(2, 132, 199);
            if (type == "kimi") return t == 1 ? Color.FromArgb(167, 139, 250) : Color.FromArgb(109, 40, 217);
            if (type == "ds") return t == 1 ? Color.FromArgb(96, 165, 250) : Color.FromArgb(24, 119, 242);
            if (type == "claude") return t == 1 ? Color.FromArgb(232, 149, 111) : Color.FromArgb(201, 100, 66);   // Claude 珊瑚色
            if (type == "codex") return t == 1 ? Color.FromArgb(52, 211, 153) : Color.FromArgb(16, 163, 127);    // OpenAI 绿
            return t == 1 ? Color.FromArgb(129, 140, 248) : Color.FromArgb(92, 105, 224);
        }
        public static Color GlmAccent { get { return Acc("glm", Theme); } }
        public static Color MxAccent { get { return Acc("mx", Theme); } }
        public static Color KimiAccent { get { return Acc("kimi", Theme); } }
        public static Color DsAccent { get { return Acc("ds", Theme); } }
        public static Color ClaudeAccent { get { return Acc("claude", Theme); } }
        public static Color CodexAccent { get { return Acc("codex", Theme); } }

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

        public static Color AccentFor(string type) { return Acc(type, Theme); }
        // 不随主题切换的 accent（恒用浅色高饱和原色）：深色模式下详情面板徽标保持与浅色一致的浓度
        public static Color AccentForFixed(string type) { return Acc(type, 2); }

        // ---- 横向徽标（置顶栏 / 悬浮窗条形灵动岛共用）----
        public struct BadgeInfo
        {
            public string Name, Value;
            public Color DotColor;
            public float NameW, ValW;
            public string IconType;   // 非空 = 图标徽标：品牌图标 + 外圈环形进度，右侧数值（名称模式此字段为 null）
            public int RingPct;       // 图标徽标的环形弧度 = 剩余百分比 0-100
        }

        // 单个徽标的内容宽度（不含徽标间 gap）；岛宽计算与绘制必须同源，否则按此定宽后徽标会被裁切
        public static float BadgeWidth(BadgeInfo bi, float s, int ringD)
        {
            if (bi.IconType != null) return SiF(ringD > 0 ? ringD : 18, s) + SiF(8, s) + bi.ValW;
            return 8f * s + 6f * s + bi.NameW + 10f * s + bi.ValW;
        }

        // 品牌图标 + 外圈环形进度（图标模式的核心绘制）：底环常显，弧长=剩余百分比，0 时只画底环。
        // 几何：内圈（图标）恒 18 DIP，ringD 为环外径 DIP（灵动岛 38 / 置顶栏 0 无环），penW 环粗 DIP。
        // ringD / penW 必须与图标一样按 s 缩放绘制——此前漏乘 s，高 DPI 下环外径与环粗不随面板/图标放大
        public static void DrawRingIcon(Graphics g, string type, int ringPct, Color ringColor, float cx, float cy, float s, float ringD, float penW, bool dark)
        {
            int isz = SiF(18, s);
            float d = ringD * s, pw = penW * s;
            var rc = new RectangleF(cx - d / 2 + pw / 2, cy - d / 2 + pw / 2, d - pw, d - pw);
            using (var pen = new Pen(BarBg, pw))
                g.DrawEllipse(pen, rc);
            if (ringPct > 0)
                using (var pen = new Pen(ringColor, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, rc, -90, Math.Max(1f, ringPct * 3.6f));   // 满环留 1° 露出端点圆帽
            BrandIcons.Draw(g, type, dark, (int)Math.Round(cx - isz / 2f), (int)Math.Round(cy - isz / 2f), isz);
        }

        // 绘制单个徽标（centerY 为垂直中心线，textY 为文本行顶），返回徽标内容宽度；调用方自行叠加徽标间 gap
        public static float DrawBadge(Graphics g, BadgeInfo bi, float x, float centerY, float textY, float s, Font f, int ringD, float penW, bool dark)
        {
            if (bi.IconType != null)
            {
                if (ringD <= 0)
                {
                    // 无环模式（置顶栏）：空间紧凑，只显示品牌图标本身
                    int isz = SiF(18, s);
                    BrandIcons.Draw(g, bi.IconType, dark, (int)Math.Round(x), (int)Math.Round(centerY - isz / 2f), isz);
                    using (var b = new SolidBrush(bi.DotColor))
                        g.DrawString(bi.Value, f, b, x + isz + SiF(8, s), textY);
                    return BadgeWidth(bi, s, ringD);
                }
                float outer = SiF(ringD, s);
                DrawRingIcon(g, bi.IconType, bi.RingPct, bi.DotColor, x + outer / 2, centerY, s, ringD, penW, dark);
                using (var b = new SolidBrush(bi.DotColor))
                    g.DrawString(bi.Value, f, b, x + outer + SiF(8, s), textY);
                return BadgeWidth(bi, s, ringD);
            }
            float dot = 8f * s;
            using (var b = new SolidBrush(bi.DotColor))
                g.FillEllipse(b, x, centerY - dot / 2, dot, dot);
            float tx = x + 8f * s + 6f * s;
            using (var b = new SolidBrush(Text))
                g.DrawString(bi.Name, f, b, tx, textY);
            tx += bi.NameW + 10f * s;
            using (var b = new SolidBrush(bi.DotColor))
                g.DrawString(bi.Value, f, b, tx, textY);
            return BadgeWidth(bi, s, ringD);
        }

        // 按当前数据与字体测量所有徽标；totalW 为去掉末尾间距后的内容总宽（岛宽计算与绘制共用，
        // 两端必须同源测量，否则灵动岛按此定宽后徽标会被裁切）
        public static List<BadgeInfo> BuildBadges(List<ProviderStatus> providers, Graphics g, float s, out float totalW, int ringD, float penW, bool dark)
        {
            float gap = 12f * s;   // 徽标间距（灵动岛 / 置顶栏绘制处与此必须同源）
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
                // 图标模式（无可用品牌图标时回退名称模式，如"未配置"占位行）
                if (!st.ShowName && BrandIcons.Get(st.Type, dark) != null)
                {
                    bi.IconType = st.Type;
                    bi.RingPct = err ? 0 : remain;
                }
                bi.NameW = g.MeasureString(bi.Name, fs[1]).Width;
                bi.ValW = g.MeasureString(bi.Value, fs[1]).Width;
                badges.Add(bi);
                totalW += BadgeWidth(bi, s, ringD) + gap;
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

        // 提取自 ProviderRow.OnPaint，供 FloatingForm 自绘使用；y 为该行左上角纵坐标，s 为所在屏缩放因子。
        // 卡片行固定紧凑单行样式：左 = 名称或品牌图标（图标模式），右 = 剩余百分比/金额/失败
        public static void DrawRow(Graphics g, ProviderStatus st, int y, int width, float s, bool dark)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool err = st.Error != null;
            var pool = st.Primary5h;
            int pct = pool != null ? Math.Max(0, Math.Min(100, pool.Percent)) : 0;
            int remain = 100 - pct;
            Color dotColor = err ? Red : RemainColor(remain);
            var fs = FontsForScale(s); // [0]=FTitle [1]=FNorm [2]=FSmall [3]=FPct

            if (!st.ShowName && BrandIcons.Get(st.Type, dark) != null)
            {
                int isz = SiF(16, s);
                BrandIcons.Draw(g, st.Type, dark, SiF(12, s), y + (SiF(24, s) - isz) / 2, isz);
            }
            else
            {
                using (var b = new SolidBrush(Text))
                    g.DrawString(st.Name, fs[0], b, SiF(12, s), y + SiF(3, s));
            }

            if (err)
            {
                using (var b = new SolidBrush(Dim))
                    g.DrawString("失败", fs[2], b, width - SiF(36, s), y + SiF(6, s));
            }
            else if (pool != null)
            {
                // 金额模式（DeepSeek 余额）显示金额，否则显示剩余百分比
                string rightTxt = pool.IsMoney ? pool.Currency + FmtMoney(pool.Remaining) : (remain + "%");
                using (var b = new SolidBrush(dotColor))
                {
                    var psz = g.MeasureString(rightTxt, fs[0]);
                    g.DrawString(rightTxt, fs[0], b, width - psz.Width - SiF(12, s), y + SiF(3, s));
                }
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

    // ============================= 品牌图标 =============================
    // 内嵌 64px 透明底 PNG（LobeHub Icons 官方 SVG 经 Edge 无头渲染转换）。
    // 样式随主题自动切换：浅色用品牌彩色源（Codex 无彩源用黑色单色原样），
    // 深色统一白色——有深色彩变体的用变体（Kimi 白 K+蓝点），其余单色源染白。
    internal static class BrandIcons
    {
        class Def { public string Mono, Color, ColorDark; }   // ColorDark：彩色版深色变体（可选，如 Kimi 白 K）
        static readonly Dictionary<string, Def> _defs = new Dictionary<string, Def>
        {
            { "glm",    new Def { Mono = IC_GLM,      Color = IC_GLM_COLOR } },
            { "mx",     new Def { Mono = IC_MINIMAX,  Color = IC_MINIMAX_COLOR } },
            { "kimi",   new Def { Mono = IC_KIMI,     Color = IC_KIMI_COLOR, ColorDark = IC_KIMI_COLOR_DARK } },
            { "ds",     new Def { Mono = IC_DEEPSEEK, Color = IC_DEEPSEEK_COLOR } },
            { "claude", new Def { Mono = IC_CLAUDE,   Color = IC_CLAUDE_COLOR } },
            { "codex",  new Def { Mono = IC_CODEX } },
        };
        static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();

        /// 取指定主题的源图（缓存按主题区分）；未定义的类型返回 null
        public static Image Get(string type, bool dark)
        {
            string key = type + "#" + (dark ? 1 : 0);
            Bitmap b;
            if (_cache.TryGetValue(key, out b)) return b;
            Def d;
            if (!_defs.TryGetValue(type, out d)) return null;
            string b64 = dark
                ? (!string.IsNullOrEmpty(d.ColorDark) ? d.ColorDark : d.Mono)
                : (!string.IsNullOrEmpty(d.Color) ? d.Color : d.Mono);
            b = (Bitmap)new ImageConverter().ConvertFrom(Convert.FromBase64String(b64));
            _cache[key] = b;
            return b;
        }

        /// 在 (x,y) 处绘制 size×size 品牌图标（未定义的类型静默跳过）
        public static void Draw(Graphics g, string type, bool dark, int x, int y, int size)
        {
            var img = Get(type, dark) as Bitmap;
            if (img == null) return;
            Def d = _defs[type];
            bool usedColor = dark ? !string.IsNullOrEmpty(d.ColorDark) : !string.IsNullOrEmpty(d.Color);
            if (usedColor)
            {
                g.DrawImage(img, x, y, size, size);   // 品牌彩源原样（含渐变/多色）
                return;
            }
            // 单色源随主题染色：深色白、浅色近黑（近黑近白，边缘更柔和）
            var c = Color.FromArgb((int)(0xFF000000u | (uint)(dark ? 0xF2F2F4 : 0x18181B)));
            using (var attrs = new ImageAttributes())
            {
                // 重染色：输出 RGB 恒为目标色、保留原图 alpha（形状由透明度决定，边缘抗锯齿平滑）
                attrs.SetColorMatrix(new ColorMatrix { Matrix40 = c.R / 255f, Matrix41 = c.G / 255f, Matrix42 = c.B / 255f });
                g.DrawImage(img, new Rectangle(x, y, size, size), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
            }
        }

        const string IC_GLM =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAGeElEQVR4nOybd4wVVRTGP8VEwU6wYZAIokjQoK6oYFDBgg1L" +
            "lERFxYhGEo0lsZclFlCxRFEJ1j9s6CpRgyYKGmzYkKBGiqyIsWAjNuwaPN/eebuzs+e2ebNvSN7+kpN9c+/c92bO3Dnt3u2G" +
            "crlVZJjIPJTEeiiX/iL/iaxESZStgNJZH3VOlwJQ5xShgN4ir4g0ixyMOPYTGYQ4jhD5VORFkV6okiIUcLLIQTAW/arAMVuK" +
            "vCwyX+Tj5O8WgWOvFukncmjy21VRhAI+S33+KGxIi6JGpo45Ey4JHLsix+9ZKcoN7i3yt8gHgefPFjky0zYH5qmGcABM7PA5" +
            "qmQDFMN7iINTPquAdxHOqyiIbigHKozGc4/k+E2RiTCzqKaUHQn2SWQ+SqIrFEbncRjMu/o7zPt9dsTYRpGlImthvMz9Ipui" +
            "E7DNAPrZFcjPDOg3TOt/tGfs8zDBTpZlIkNE/kQ+dkR7l92CNgMGJReRlzNhf9pHiYy3D8U46DdPdoEJgvLyrMjO2cbOsAHL" +
            "tB9KQYM33NI3S+Q4x9jlnu+OJsYGbCxyj8hqmHfzcuWczeG/wJ0cff3gZoCl/Ry02YznYK41iBgFXAnjq3smx5PRMZj5B35W" +
            "OfqWwk2z0ra/yHSYV4TQxtyMQGIUoBmvAzPHtPhvwM1sR9/TcPOU0jZGaRuJQGIU8LXStlppmwD9SVV4wdHXBLuCODuuVdq/" +
            "Qth1qcQo4IbMMV3KQ8p5NIIMcR+Dzl9ww5k2SeST5HgljFsdKvKHcv4D6JgUaYpSifUCfWGmHKc6n9S3AWNo1Y+HcXHkIpHb" +
            "USw9kutifjFX5MPQgbUMhWmYLhb5CSadDb7IzqSWCqCLpAfonhzzfV8iskZkAUyGuAY1ptbJED3EcEf/WTBxf82otiAyAqYe" +
            "SGbCGEAXO3j674MJdi71nLeXSAPMrFoMt2t1kncGMOhgJbh3pv1G6BEioTGchTDoCWw3xWh0YqZtkcgJMNXiKPIqgO9rg6Xv" +
            "CpEpSvtCtFWAfNhi/sss301oT2JL7B3iAGr2CZjw0gazvQZHPxXQPdPGqR9682QA9Lh/imPMrmhztRqsPDM2OSPdmFbAYJjp" +
            "NVbkSccXDYObTUQGZtryZHC7ZY739A/Bvo6+R0ROEnkQqQeYNoLphYkf7d/TEgT5yM4AX/SnsRHicRn1dEXp18qH9Aygi+Ir" +
            "8LjI6bDjK2BSQdkgpxnxLMkcLwwY40rEjoWJPehqW71VXiPIG+pv6eMKz1SlnfXBEQjjC+gukyn59ZYxXGIbjEjyFkVHoy1Z" +
            "SXM39JsndyAc2zIZE7IZSjtn3BjkoNpI8DSYKg63ubwk8o7n/O/hX9FldjfBcw4t+iEwD5Au81HkpNahMFeAbF6EhpdVpltQ" +
            "Q4paGwwlbTdokFjkYErNdYPY9cVCqKUCLhTZBiYjZDq8HOsAsYujjP3Hi4yCibtD0leOofG6Jjm+AGZzRAgMbFjsCClxnZuc" +
            "TwX/jEBibAALjekLp6tiRPWdci4rRyyAjFX6toW7krQ1TLSWrjgzieINrrJ8H11sOtqkgZyLAGLc4PTMMVd1NWvNaf4W9Jsn" +
            "vqiQq1LZcjtLarzJrZTzT0HHUNuXTrcSo4A+Spu2r4fv+nawM9nRdyfsiRaTo0aEXZdvgaWVGAVoVV4tv3ctbRGG29PQPl/g" +
            "tOe+4fM8Y09U2mYqbc8gkBgbwOUmGrNTRb6B8ddaWXwtwmDBg+86w1emsaEZI68jm5CNSq6Hq8cPwwRoQXRGIOTKEyrQPjSl" +
            "jmnpGf42esYxWOqJAtFeARYuFiM/0zz9LI03Zdr4RCchU6xQmIr8MF/YJ9uoxQHMld8W+RL5YD7AqThQ6bsJ7lWbRck4Laub" +
            "B2Mj/kU+3odJsdttxNIUwBXevDdfgWU1BiM0bvQI9N90mSEZIdcOuXjSC23ehC6Yr03emydcQ+ywC63uN0mVtU+wAkNX1hB/" +
            "QEmUtV2e9TmWrxgx8r18TWQzlEBZM+A6mAptBeYOv4m8jhpT1AxgrW9cxPna+uBQhMN6YQ8UQBEK4D9JMFFhBHZv4BitwrsA" +
            "YTBg4oYIWvXoImiWIhQwJPW5b+AYrvCkd3Vw6t8WOPaY5C8TsdGokiLc4PYwiRL/MtGZEziOIe3uML45ZrM0N1veBZNzHA7/" +
            "zjInXZulUed0KQB1TtmhMA3hhsi/Bb5qyjaCrCj9InI+SuJ/AAAA//+vKs5WAAAABklEQVQDAOpQHGzBb5SsAAAAAElFTkSu" +
            "QmCC";

        const string IC_MINIMAX =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAE6UlEQVR4nOxbWYgdRRQ9Mwb3Ne4BEcRkjBqi4o7LfLjjEhcQ" +
            "QXTEgPihYvwRFEFxCX6IGPFD3EU0JqAo+VCjJlEwakjiblwQxzUrmewfYZJ7qG5yc191Vfd093uT9Dtw4N6quq9f31fLvVX1" +
            "etFw9KLh6DoADUfXAWgP9hHeK5yXcJbwzPzmuEU4N7F9X3gpKkIP6scNwhnCozx17wqnCldn2F4gfFF4vKdugXBA+AdKoG4H" +
            "nCVcGGnDX/RqT/kxwh+F+wdslwpPRQnsgXrBbnuY0jcLfxEeocr64F5kmbF9U3iSKftWeKTS2auGEHdyJuqcAyYIT1D6TOFY" +
            "4ckJNa7y2F+h5EXCg4WT4XrG2ohtbtTpgOOM/rxwSyL/IPxA1Z1i2vYZnfPAUCL/DefMFKehBMagPuxp9BVG/0/JB0Vs/wno" +
            "B6IEunEAGo6uA9BwdB2AhmMkDhiH1gjNh22R+uEM2Wcb0vO8A5OxM3wVRRxwj3AQbg3+Hi6BeQKjG5PgMshNwq/gHPehcHza" +
            "IK8DmLA8DReGpmBYe79wCVyYOtpwE1zucKEpvxguH7mGSh4HPCy8MlDPMPZlT3ks0+zNkPPY6vphTz3D8JcQxmvCcTEHMJN7" +
            "SOnM2LixcavwS1U+Ba1jrMo5AIF63zs8Kdxb6c8IrxO+oMoYQj8SywXsrs19wjmJPE/4p6o7T/i10jvZA3S3fwdu/kplbq70" +
            "J/q5sR4w3ujzlTyYMMUE07ZTPWA/7LwHMd/Uf6LkibEe0BN4MKG/+JiIrUVdPSBmO5z1JRqJrgPQcNABd8BFd+8JD4m0Hw7o" +
            "RSeyOpfBrOe06DR+Ci6+5+bijWgYOHPvq/Sxkfa9Ab3oTF5nHJD1nBa9zk3RKhEbEiNG4ydB2wPsdvReRt9o9G0Zch6EbKvc" +
            "Dwh+Fo0XK/1y03iKkn9C+MNjX7qIbahtURRyAJMfntowe+LB5NmqbhF2Q3AITBdeKzw0KTsxoQbP4h712LdrFQjVl1oFqPwO" +
            "lz7+nPEBPH/vh9tFsWhXIBSqLxUIpZMgu/1EuC0u/vo8dl4ON+65FbYJuynsKjAdxbDLD4GygVCZIbBByesjtlUOga0oYFwn" +
            "ZmPHfYFX0T4sUPLcTobC3KriVRlmoINoH74QngN3S2Vmp3OB9Wjt/nkwZPTDhSsT2d5GW+exX5hwl80Flhr9briexAtUd5m6" +
            "xYHP6bgDLhLejOL4DO7UJ8WDwjXC/+GckeJz4XehDyrrgJ4M2adbTBN+JHxd+AbyPyfF7YhjaqxBWQfo8WUvK9mLT3bc6hPm" +
            "0xGGb0llbnIJWi9fEf/CHdQsQwRlJ0GOr8mJzC01RpOMHvnyd6p2q4R/GdsqskH2IDqyX3gs3JrP06qP0epwL8o64DnhbYnM" +
            "rTVmkBxzk0y7Zz22oeETamtB587GCFF2CLAbzjBl9uV/FT6G8LNjoXBtd5qrWAU4mT0Of0zOrsgb31sxSlFFIMSXe0D4Ntw8" +
            "wE2V3+Befk7ATt/3tecRVh9JsJQLVUaC3yTMiyVK5mbMgPCVRJ9m2gaDmTJoxx8msnAA3DJ1tCpjnM6jbX0sz0RpADWhkw4g" +
            "+G+SWYF6XsTqQ/Y/Skqj7j9MxMBlk1tyvLhkt+AZT1yGmjPFTveAFLx9dr3wfLh1/VPhW2gDRosDOobu/QA0HF0HoOHYDgAA" +
            "//+4CBVLAAAABklEQVQDAFkl/a6unb5SAAAAAElFTkSuQmCC";

        const string IC_KIMI =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAADf0lEQVR4nOybTYiNURjH/4YiC2ZhfDRJiBEl8lEYxFhQyoJS" +
            "QhELH4WFrGzYsCDZmA1SWAhTShbCIF8RI4NEIslHg3wUpmb4Px3T3C5z55z3Puec13vvr36l+56J/zPPe95zz3lVINvU0sO0" +
            "jX6kDXRx7oAeyC4r6ZEurp2nC2lrT2STGpiQXf2CR1LJfjGrHXCQru5mTDut7IVsUmsxpoJOyGoBeluOq8pqAWxpLuUC3KXP" +
            "SrUAH2DWA+02BVhLq7u49oPugj5VdD3s1inf6e68z9oKjL9Jl9DXsPwLfnVzfTR9Cj0G0st0jOX4d3Rw3mcTaf+8z6RQL+nb" +
            "3A81bgHN22gQTPgay/EttO4fn9+DJWmaAyT8dTrCcryEl+f9ExRBBdKBa3hp+6LDC2noANfwMnnNos+hQOwCJAk/A2YyUyHm" +
            "LeAaXkKrhhdiFUDWFS7hpd3VwwsxbgEJf40Osxwv4afDTHzqhO4A1/Ayy3sLL4TsgCTh5VHXAo+E6gDX8A8RILwQogCu4Zvo" +
            "TAQIL/gugIR2DT+bfkIgfBbANfxtmPBfEBBfk2BH+GrL8RJ+Lv2GwPjoANfwsiCKEl7QLoBr+EY6D5HCC5q3QJLw8+lPRESj" +
            "A2TLTI6absA+/CW6AJHDCxoF2Eqv0iEOP3MSZkM1OhqbokmQR90qehqRifV1uB89RQ/RvohIrA7IRb7uyj699U6uJmnYFJVN" +
            "kVt0GyK8sJGGDsjlCl2KvMMLn6RlW7wD2e19DLM+CILPAuyFOTZrcvsxVNJztJ72gWd83QL76JY/f5aXFfbQDXBHzhwXwXSF" +
            "F3x0wHZ0hhdktbcR5q2sz3BjFMzTYTM8od0BcqR9oMD1oTDv6k2COxfoMvoeimh2wDoUDi+8opPx93m+DXIK3AzlCVKjA+T6" +
            "CnoMbsyhJ+gAuHMG5jYr+nxQowDym69HMuR4TApXh2RIIe7DzBMNSIBGAcbRRygOmTh3oDikC4/CkbQshHbSafQNkpNozZCm" +
            "laC8vCTddBYBSdtSWM4DZL2wibYiAGkrQAf76VT6Ap7R2hP0gczu4+H+eHVCowA+v8N/pcthCuFlbkjrLZDPA5i5YQrMklgN" +
            "jQK0IRx3YA5ShtM19Dg6X55I9O+waV95Phd6/74R8RkLs4ZwPlXO8n+asuJ/mQO8US4ASpxyAVDilAuAEqdcAJQ4vwEAAP//" +
            "lKu2ygAAAAZJREFUAwC5W6To0ku4iAAAAABJRU5ErkJggg==";

        const string IC_DEEPSEEK =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAGZklEQVR4nOybCYxdUxjH//qqhmJoa61lKhHaiiiqHbU0CLVW" +
            "LS2CDo29BBH7FmLXWIJEbEklYqmlEUvUMhWqtauxFplkilYtVVptkfj+890n15lz7z3n3jPvPXnvl/wzM+eeO/Pud7/zne98" +
            "50wf1Dl9UOc0DIA6p2EA1DkNA6DO+b8ZYGvRItG+CERfVJ6xoqGibUTDRP1F74iuFP2Rce+Zok1Ek0WvIABroDJMFJ0oOjjh" +
            "+hWi20W/I53Foo1FK0QtoiUoSB4DHCZqFY0SjRStG7vGN7hM9Fvs6/bQt5bExaKbkM1wUUfs5znRZ/kJ+RgkWu1qAD7kGaIL" +
            "oxtD8atoA8e+40QvGG0LRZeKHoYfW4k+EM13CYJnib4T3YywD+/LRpa2LUTToZ6xE9zYVPSsaIBoVN+MP/gQksdtCJpFU0QP" +
            "OPRNG+8cHnyjO4o+jrXzZwbcbaEBn/HjuOjvko4kAzBCvwm1Vm/D4LeW6J6Mfp3I5nrRoaIJojuhHpJGhy0G0DXmQq1WSZ6G" +
            "zhRJM0ETsqdJwgc/B260mQboJ5otGo3q8LnoQCS/7SdFRyAMK0WDzCBIF6rWwxNOmW8j2fumIxwzRMvjHsCA8RFqA6a7e4kW" +
            "WK69jOKp8I+inUVdcQ+4F7UDg+9b0A9pciw0IywCA2UXvykbYHdU1/VtDITGozFGO6dDesfX8GcpdC0yt9xQNsApqE2Ygb4o" +
            "2s1o/1K0C3Q4uMLhtCvUqP/CGLAe1KXWRu3CqfF40UyjnUHzs5T7lovaoanyY7YO9AC6v+3hmf7Og1q72tATnhFdbbRPSOjP" +
            "6XR0dN8hSHh4QgOYY+xu0fqiwdEv2Q7qKezHhcdLUMtWg6ugqW5r9PP4hH58afPgAB9slmi/WBsfdI7DvUdD8/gDEAYGplXQ" +
            "vN5l0cXcf0TCNa4aD4ID9IAtjTbXFd8T0CXqENFtKAYXKHyrY6GLMBp2WcY9I1KufQVHaIABRttm8KNTdD50Ofo+8rEn9OHL" +
            "i5cHoSnxSuTjQ9eONIC5zh6MfDCL5NR0CfxhseU1aHJSDmwchhORj/dcO9IAppWHohg3QktlCz3u4fTG4iin41Nj7SxcXAB/" +
            "nFN6GsCM6HugOO9Ch8Qsx/5MdMoVX3M5PA1qCFdme/TtNsAio415+BAUh8XK/UX3O/Rl3Lkl+n6V5bpPpupjrG4DdFraxyMc" +
            "/PAzPPrbVnqLPX7H8/CABphvaZ+CsDBncI3M9EAmMeZs9IvDvZ8gPTXuARMhrqxs44YRPe+0ZoNFDiYv/R37MzjTnX+AemRW" +
            "fY9MhWayztAALIwutXywp0RHIiwnQef43oC7RfSarATqP3AI/AWdg004Hw9DWFhmn4ne4T54Pjwp1wNskZPeMQ3hORnq1qFx" +
            "2V7rQdkAXC/bNh6Y6x+FsPwsOg1huVX0PXJQir5yGDAW2KYgBknu3OTNy21wvb45NNAWhZF/kuhv5CBeFWZliLl4s6VfbwRE" +
            "bnQwZy8SZ7hRwowzd9GmFPt+NdQL9rH04/qA8/NzyIZZJHd5mN7yAZOKJ/Q6HnLgzNAP+eC97ShAyfj5ddHhsO8JsqC4DrIL" +
            "kXxwHnjggoj5PadXFjv+tPRluszhMAn+XA7POd+GbW+Qb/vTlHsehdbmk+B+v5m1cWi1iV5NuOcaqNFc4YbqeQhAydLGXRMm" +
            "RuMS7tkBWgbjcLBtZDJYdu+9x9oYV3iuh0VK2wqRecjwSFl8Ad3YCELaCRHuw52Qcp1z+TGwJ1H0gm+hQ8aEw4GFji7LNS54" +
            "XIJtOzTOPAJ9YbkppVzjH+Cba024zrE9OfqeOX58GcuaQtKCijl9G7RoYdbuHocehUur95EWaMnsdGguwwXQCuTA5YwQDzHd" +
            "4NCPEZ9DgkNkINzgdvdlULeOw2M5d8GPc0V3wJOSQ583oEGRxY2mlH5MbFpgd/skmANMjb5uGIkesCTSGI/fxQNXPnWHbnyO" +
            "ybF8zpR5b1QGLmwYY5igcZociZ4lfMI9Px7nuVb0DTzJc07wIugWVRN6DxZEGF/MocHgSm9ZE7p1twAFyXtSlK56NnQudj3n" +
            "5wKPu10HzTUqQtGjstxUbYPu7BSpJnOtwcNNXhXdEIQ8K8zkh7U/7slxqmuOFN9q43TF/IHrAI5Xzhx8+MJnfvNSqcPSNUvj" +
            "HyZQ5zQMgDqnYQDUOQ0DoM75BwAA//9m6Z/LAAAABklEQVQDAHQvG1yqxYs9AAAAAElFTkSuQmCC";

        const string IC_CLAUDE =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAIvUlEQVR4nNSbB8xcxRWFTwgo2EACCQESEtKd4BQwIU5CAjhK" +
            "QkdUAaII07voXSB6EwgQvZtuOpgOAkw11ab3Ykyx6b0XcT/mrXn/7JRXdg0c6Wj/nZn373t3Z+6ce+fudGqP9Y2vGd8yPmY8" +
            "xziLviX4rtrhT8YrjYOM3zP+yPhH43zGs9QMmxpHGwcbb1Gf0cYAMxqvN84e6Pud8SLjS6qHUcZdjLMZ/1d8xnXqI76j5hhu" +
            "vCPRf4pxXVXHnMYpgfbfGx9XnzCdmuOpTP86xh+rOkZG2t9XH9HGADi+hzNjNld1zB9o+8j4vPqINgYA12T6N5Nbx1Xw00Db" +
            "I+oz2hrgQON7iX52hTVVDXMG2h5VNWDkQ4xjCx5X8brWBsBpbZ8Zs62qYY5AWxXnN6vxLuM2xkULbmhcq8K1rQ0AjjHenej/" +
            "g3FJ5TFboC03A9AKV8npER9HKbysBqAXBgCbZPq3zvTHbvQ+pXGu8e+RPtToqsqgVwZgBpyU6P+/8W+J/tD6/0DpGTDKuLTS" +
            "yIqolAGQoSg51heqbg6lsZPx3UT/vom+kF4Yb/w8Mn5H49pK41nj/ZkxUQOcb/yX3EMvaFy+uKH54v9Krxp3TvQzCxaO9IWM" +
            "e09k7DLGA5TH4RXGRA2wUqBtbuOtxtUVx5HGBxL9B0ba5wq0hRzrEOOZyuMy46EVxkUN8GSkfabiBtD5MYGzqeL4p8I7wjyB" +
            "tvHe+5mNY4zfVxovqOIWCGIGuFFpjJTz0H8O9OE7zklcG/IFvgHQ/w95bWfLBUY5rGB8UxURMwCiIidCmI44mY0DfTskrhsm" +
            "d5Nl/Nx77zuv3eXWfg7MvrtUAzEDvC3n/C5THgih8zQwCzRJTprGsI/33p8B5f1/KeOeyuOs4l5qIbUNvmNcVm425LCycYIG" +
            "RnR7G1+PjB+qgSLFT6pMKF4xTBWnx4xZXw1QRQjhTbnhXGT2G+Pt+mpJkCPcOzF+N7mETEgFspNML7cdz6o0+KKWkxNOtVFV" +
            "CfLwGOGwzDjygkxDcnqzFOOfjowld7i4XMTogyXAXp9Sjx2QdZqohqgrhdH0/1Y+G8T0Zhv7i3G7xDicoW8A1CSBTJUo8mi5" +
            "WdIYTWIBxBDR1yGZcb+V+yZ/lRizhLoNwH6fk7kAP7GZWqJNUhQgbE6XW/9NcYPxP6oPMs9PqiXaGgCgCNmnd9a0Aw50nxrj" +
            "8TeIKLQL+YkZ5Jz73b0wQAc4rNOKD+gncKqxGUdQtYCc2ML/DC1eYziglwbo4CDl02RtgN+42vgz41/lHhbRxoP/RPUwuh8G" +
            "AAvJKbNfqPe4WG4nml3tsWjHAExfIr1P5QTMmwXfUXNwXkj4u4W+WeC5SOdfajwDAyBaUrkzMr+vBPh2QQz2hvFlufXpn+Tg" +
            "4YkO65wS9RrjjNfKJVDHlTuQmwtlLp5L4YRFDBhlUsHn5Az4jKadARBgSPJ75bJK41ODmQEcchLDz6BvHybKPSzkoJaH/bjG" +
            "9VN1AKllDhPmlfMFcHDxmsvATCsQ7HQeFDXKVH5NLVFnFyAq6xhlcMFBpdcOZyy9drCGnDRuA3KEhL0sp+cKhrLGtJGgJaOd" +
            "NVC/tsEyUF8UUsytrwecLk8pEQOSvMVX9dUAOD3k6ob65oEZspXxzH4YgKlPFonYYGb1DvgnlhFKkEzVPGqPxXptANLRiJ+Y" +
            "JL1N7nBlJtXHZLmT3yeK978u3hORjpCLDutiUq8MwI2Q/Zk/0o84ovgJp3S6mgMB9l+FD1/QKovIFVdxqjVUedza1gA4OIKf" +
            "5RJjUGAkLIkSr/b6OL+rGy+gPEmR50roSLSMkPtyMMgCpT6+CPzTYU0NgIMjJk/pfDQ35wMnFB9+s9yW2QEHH/sbz1AzcHx3" +
            "YY3xfDaql7wAYfuXcU6TOkEcHBHZwokxnCbjrHjoH8qVrZSjtw/lDks5Zyx/M8QUg1QNxC98k3dWHP+JXKzCwclUtVgnJ8jp" +
            "DUdm5AJj3p0b4oxgRbngCIxW98nPlnIVZuV1Oqm4LgQMFjpSO8J4sFqgqgHWk5uyiyTGMK3wxBeU2vaQ+6bLuMJ4fPF32XMj" +
            "UsbKzR4fHXVJQYQfbZI9plKkUSyT8wGs9VOUrsTgNJbcvF8yt5i6nR7ylRQV/gFp/Uapj5NfnOkv5dRaCCRCWLuXy2WEyqAa" +
            "BH1Q64AkNQPYqwk8Ug/PIQje3X94ZO/owHjWfOfkdojX1ymTnah4NQmzjENb/MY4r4/tcazyJ0kDkDIANxjL6SNGcIKcxobK" +
            "Yjis8Ku+9tDArStmALCf8UV1A/Gzl5wewKOf6vUT2uN4c+U8U5EyQKzmnyMrbj62D+OY/uG1MdY/4R3mvS8bgHUeK7Ml4Tq8" +
            "+HukutPjHNoQNlfSFykD3CS3T+PZ8f5Md7Kwqfz/auq+caZ8qKxmuPferxTHGZ6rMEaV/kaP+FXpzFyMMK8y6GUswIcRsw/2" +
            "2lFtl3ttGJ6lU97zUWu3eePQEE8Urz5YJruW3lOtQj1DOUjiy2MXmqAIelUniMO8RN0Pf6y6Hx5ws77gmRwYR31BTG0SW5SX" +
            "EfEBO0y5qAM5fIMS6JUBRqk7GkPoxCpEQ8feUyJjOV+IVaqc5r0nTmAr3KnU9gOFa5m+RC8MgKpb2WtDua1SvIbgG4C9PbV/" +
            "b6Aig+MBh7dboJ2QHNFGWgxfFi3da+sDWLeh3QBBMyZxHSnrctEl63yI0qCWIBb8oEUeUwO0mQHs82d7bZ8p//CsfX9KVvlx" +
            "FbtC7HcAI9QQbQyAF/aDHAobxmSuGxb43MmqBvJ4DwXaG6fH2hjAP3ZmL65S0bVgoG2KqqHjW3w0/qFmGwNQHt8JeTeSC5qq" +
            "YHigraoBALsLM60jwXFwR6kh2jpB9n1yAy/XuIayFr/AgXD7ZNUHvuQBtcD0aof3Ve93fezJoeqOOjOgjFYPD3olhKoCoRIq" +
            "uKz7E9ueYVobAOzlvWdJVP15XM/R9tfjTfCg3AMTMp8oV1rb+pS3Kb4AAAD//7f/jqwAAAAGSURBVAMAwmejDdwlA9EAAAAA" +
            "SUVORK5CYII=";

        const string IC_CODEX =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAKqklEQVR4nNSbCdRVVRXH/2qtNINEQ4FExDJxyNQsSa2wNFEj" +
            "KwobrL4sKy2KrNXEKjGzcmUDpEWDYrayNEPLxGiQzAZtEjOsNO0TLCIlw8SypNw/zrt63n7n3Om9D5f/tfZa755z7v3u2WfP" +
            "+36P0MhhH6PpRocaTTTaweixRquNbje62egio4uN/qWHCJtpsBht9HqjNxjtVvOee4y+avQxoz9oE2OQDHiN0XyFU26LxUYn" +
            "GP1NmwiDYMBjjC4wOrJkzR0Koo+o72L0uJK1rHu+0a+jsW2NJigwd23neXdoAOiXAWx+qdGBiblLFU4UHV/n5sYq2IdZCptN" +
            "4UyjPY32U1qq1htdaPQNo8vUEv0y4MdGB7mxRUYfNvqj6uGJCvr/QrXHn43eZ3SeGqIfBnza6C1ujBP9uprjqQqSMlH9YbnR" +
            "69StPqVoywA2eoEbO9joJ2qGcUYfMRrKzP/V6EdGNxptUPAyuFPswTTl8VaFA6pEWwbgx2ND9goFV9YEiOxco0cn5v5k9A4F" +
            "qciB+55n9DalmXGu0WtVgc3VHLi7ePNIQpPNH6VgH05T7+bvUtg4nuLiiucQP1xidIjRSxWkJcaQ0QcqntFKAr5rdFh0Pdlo" +
            "uMZ9uxqd5e6NcY7Ru9XevaEW3zba142/WCXMbMqArRQ4XwAX9JKKe3BhpyiIagp4EnT2WvUPJOpKo/2jsVVGO+VuaKoCL3DX" +
            "36pYf7yCuKc2f5vRy42eqcFsHnA4M9StDniWt+duqCsBRXz/NDeeE38CGOzCk5XGPKPTjf6tahygwKQbjJaoHjDKX4muCa13" +
            "SC2sYsCrjD6kvAiNMro7MU64um1i/HwFPb9N1ZikkFscHY390uhEo1/UuP9Wdb930k3nVGB7o+8rRFZZ/VF687urd/PXdl7g" +
            "lare/NYKHmJY3ZsH6PbPjT5vtF3Fcz7jro9OLUoxgDz+N0bPTcytz/yO8Sh3jZEjnq8TJCFxNynECGU4vrPuTSVrLnLXh6YW" +
            "eQaMV9Azry+4EfL766KxnPr48Z+qGoTC1yhI3PjE/O8SY2OMPqsgXQck5im4rIyuJyXW9DDgMvcCwwqGD19KOBpvri4DyuwM" +
            "qna2gm4/PTFPgYRobw8F1fpeYg0Se3XnOWPdXKxuqGVP1Bkz4CR1BxGEu4d1Xq7A/zO/VTKeW0fER8h7XGLuTqPZRlP04KZ/" +
            "r8CMmQqn63FcZ3x2NObtzQR/U8EAkpLTonHydzZfN6VtAvz0LUZnKJ0HEC2SIp+ZuX9xZ/496g7KAF5pgYINI03/r5v36x9g" +
            "AIHKltE4MfR1GiwIhS9XCJ4mV6zdoGoQR5AznJOYI/7A+M5w43/xC2EA4e2bo7F/Gn1BaWzm7q0DUthPKNiQ6TXW8y6I8gk1" +
            "1q5RyP+xA9dk/naBG1MPYBNkZ6OiMfLoOmXqnG57o4erSoWiGLjpHfLVYHw8fnyFyvP+AkjrVKNj1ZsVFkjFLBsZMM2NXa56" +
            "aFtLIErEUBEuL+3Qnp2xv7u1WP9lClWmHasfvTH8fYLRqeo9RGKRHrsCA57hxoaVR3zq/6uxxuNTnRfkRWI939AZQ6fnJ+4j" +
            "4ySrm6duW5UChg4bRtziDxP1mhsPwIBx0fV9CgXGHOrYgJRkUCF+koIqrFMezM3prE1Vek9W0OWXqRowDCP4HTdObrNfccEm" +
            "xkSTJBBlJ9hGAhYqpNE3qT5YS7k8ZYxJb8k0qRU+peI5SBZB3DI3vqD4AQPuiyb+o3K0kYC71R53lcyRIlP9JTEqa7RgC2a6" +
            "ZxEjvIgfbCIW+e1Vjvh0t6yxBuQkpQ18XsH7kxgRUc4puY/I8oNubE7xgFXRIO5nfMmD/OZm1lhTplJNgRF7tkL9PwYdqk8a" +
            "XW+0c+bez6nb/jzLaAIM8JnWNOXxD3dNykmsPiUaa5IMtQG6T85CAHS7m9tLoSuVAqroexmzYMA33eBRyuPCxBh5NkzEjZFx" +
            "jaQExCAEJqRe6Mb3KrlnsbueCgOuUAh/C1C1mZR5APk6Fv3WxBziSfI05MYHLQExKMr4kHmLkvVXu+udCkv+NTdxuvLApyPy" +
            "qaYDLtVXgEeSAQXW1Px769zaiQUD2Ezsro5RyL1zoJpLuIlPPl8PL6yNfu9YMIAEYq5bSBnsIJWDggMqg2W+IbMGm3KwmgM/" +
            "f6Sao8rmxInfmjiYITqKS04UKwhH91E1sMwkNPQOUgnNVQptqz1qPIs1SzrP3F2DR9yCX+OjOaKjuOZOW4uiY2Hhq0DoSkJz" +
            "VmIOSSC9XaT0dwATO3OsOULtUWYD/JcsKzwDsKrk5yvdOBae+LxOkQJDw4cTuKMfJuaHOs8n69uuQ/M7Y0PqH2Uq4Bm7LMct" +
            "Yux9M3PoOs3MH6geSGWp/6VcK8ziHUYn5n6lEOjEVSTeaXliLTasKOUjQblYgFpkXI6bnEtocpsH6ChdIwKoXVQNokXc5smJ" +
            "OVTMbx43RYWXLtAKNUfuUOlvxpvHzgynGOBzAcrilyTWERBRuyP03FrlwG2SjBxYsY7aIbWARWqPlAogfT62QSqTKe027pow" +
            "F+NIyJtyde9VyMaGlAceZZ7yXSKCKypF9ArKUuA68BKA8V6ibiNOpWhjjSDFAF+xKb7RQ+dxdXRn73Rr6MhwakiLb1NRvaHo" +
            "mVIBDCs6jjTdosGD9J4PJmL3izS+sbhIMcDXzse5a/pxNCYWJu6lx0e8/SWjwxUaolRvfEGTU36Xgrgv1ciAaBbp9QaRzT9Q" +
            "AsgZwdXR710T8wQ7uEQaEFcm5l+tUItL6TxZXPFxZBVGqzk4dcrk5Dc+duGTvPPigRwD4n4gCU4ulP2tQv0AMV+pciANRJWp" +
            "PD4Fii3Hqh5ivac8tndiDbaqp+2eY4C3+seoHBQaKEOnMkR89JACE+u027Az2Bvc51ZuLsfkslIe91D6/2hqMucz4aI/Jb4U" +
            "WaVqoO8YPFSHeAHXdk+N+4gIcZUnZuYJx2er3rsW+LjR+1XS6SqLm7+sbhGs80lcW1BDgGljEnPDCq373Ld+SNZV0TVJFO+O" +
            "a12jCpQxgJPENT0yGkPET9XgwGc4nOyUxBx5CUEWp3hvyTOYPym6frwSXeAcyjq85PoL3BgiWlZ+rguCHtrkqEhq85wgKgQD" +
            "7q14VlyZJn+ovXmwecX8O9Xr5ig/f1HV4W8KRauc2uGMxDwboMuLG11d43nYizjJOlcNUadeh17+TL3/BEXEiITQman69I3w" +
            "GnsyT+nP29BVKlJnqz6IPlfowe+CKHVRU2j0H2h1C5akmlR09s/MU0TB6JBGk65i9QmSiMIol80qeTY6jAFcr/qgK4Wxi79c" +
            "PUWBwY3QpGKLT0Y3Z2owgGF0i29ueB9dIGKEw6Mxcg3C8CZM3IgtGqyliUpjhK+14Pw2ag9Uhpy/6f8JYjAxnFOjMaQNb9LI" +
            "+BWoMoIpEPXtrOC7r6h5j//aDBdLHw+d37vG/eQUMJ/kJi6UklQdofSHlLUwqKYFwQg6v1uHEEVOhBOmcsSJU1fMffrGBgh/" +
            "17pxojy+8U19HsPzSaWvVx/YFF2bGBhEvvcZq/5ARkddcp36RBsV6AfEFKTCZ6gdiBMOUfi/pb43Dza1BMQguSK3eI5Cr35U" +
            "Zh0ZJL0JOruXasB4KBngQXiMgcOG8I9TbHy5Rhj3AwAA//9U1hhvAAAABklEQVQDAFqzHWP+HZgfAAAAAElFTkSuQmCC";
        const string IC_GLM_COLOR =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAJGklEQVR4nOxbD5CVVRX/3fe9ZXdZHI0pJRrQMMFp1OHP/gPK" +
            "yLIcdUms/fMWMEtspGzCZiRUHBknMAUto3AoJWdAd5dNspBqSiomWXi7bNNQogKV1ZhpGSmwiPu+d/zdx+7n99735933Z99z" +
            "ZvnNvHnvu98999x77rnnnnPufRGUEY0xub+xTVahjIiijBhMYkOFgo0yQmGUI4JRjtMCwChHwUZwRptMHKOwRQkmI4mb9m5V" +
            "T5vS1sdktkrg9Xi3OmBK09AuV0Kwnp/DCYWF/R3qvygABWvAGKCdlvTjNKfnw8JKE5qL2+U9DTHZSeY9Kopn+btn+jVylgkt" +
            "hXwn+U1RCp+qELSjQBS+BBT+5vwW/MmEpEawkoO47J0mMLuyCstNaFn3rw67pBm/MBQsgHiHesJOoj6ZxPS9neqrJjQimOYp" +
            "VKiFAchjIYUwL6JwXnyr+i0KRFEcob4u1ZdLfdGqD1zlLuNzryn9ng61C0VCWXaBV6vwALVgk1Mg2G0dx70oA8rqCda1ySR+" +
            "TerrVD0oE0a9KzxiwVB9u3yavsHt/NTx8c9U+YfjXeoHJrSMEO8ShRhnZxqXx4ss2mkN4JbdP1NHUWT42oDadpmCAtDQJhsj" +
            "gl9yAJdSx6r5qVMRbOTAtmeljckO1l+VGrwGrT0/NyRq0Hfe9VKFPFHfLB/0K/cIoKFZPhwV7EC+jNrkBjopX/J9qXA1hXB9" +
            "EC0Hv4gDv9KfFNMmvIk7kSciUfy0vlWm+rRbXHD2X6AApgZWEPRwL5/r94rC2cYeLQgmxSH6HcFt5wFjG3DJYqkZO4i1/NnK" +
            "To5nZ25nZ+5x15nVLGeGDl5D4UNBr0S7uAgjxQV+5dS6m8h32dCy2T4QRWz/ZnUcBjD2A6oHcQd7sFQPfqgza8g4zZnBmxjM" +
            "1g6N4cshr59HKDEOZxZx2XyEXuFDjs0AmqoTuA+GMBYAGTT5lM1zP/dvVwOcxWdC21F4KugdhfNECCm4M/zYp3i+h4crzsgG" +
            "YwFwYP+Cl9NrmUV2Akv8Zsp5L/h50LveLtVN2iABPR9J4G5PaRIvwdvZ12AIYwEw2FmdxkNSUeCPMuvt61YvvGFjBjvxuF87" +
            "kQhOIgQ0kE2kXcX2D55ihBcp/I10lev3dKsTmfWPJvEIv/6e0be7YYicdoE57XJuUjCfnwGtyjSCr2SjoZ1YwLrXktEi/Uza" +
            "r/d2qm+jiJjVJGOjYzGfPCZKBE+zX/tNaUvmCtNY3Udmt3I2/8/Hj+XSyZFEyQSgt8gKizuA9gw1BN00as9RIMeUjX1HBX0H" +
            "utUxlBglDYbo6DxDjnNDqty4t0M9jBKioGCorkUutSLMBxIJG53aAIbV52xPziLxHza2ygV7u9Q3wioxMTqLhq6WFvxMGucD" +
            "3D2eQp7ISwNqm2Va1MJvSD3RXc4Bfotr+zY/Gm0M6bBsgwE4qKagQVGLNqQcsnS+f0wk8bn+LvUX5Ii8MkKWhS2Zg9egNFfQ" +
            "2N0WwMg4kGHk+IBfOdtekTn4Ib7TecaYNdL05eV+oPotZU/nUaLrOZO+Hp2O9jiTwetUUkbtbPeeTQM4uSKavldnAw3k1Pjj" +
            "6lBa/2JU/DAaYDH7vcXvXeoMAtBJ21+zjuO/OBpQ2yoX8WkDf7bwszWICRuZgzAojBu0cKG7qCJbgOTXjI2L3c+cnJlZiQSN" +
            "ge2BhzdIJVk2cRKdDLQjABoz98HEkZCGBpAFrFPtfk5m8f78kFTIPfmhgo06M1NnOG3bcDJLjgC0ytNLW0o16qCifR7BHQtN" +
            "YJJ24LiNNCfHjgbHBoF8gOfcz9wZ/mBAFhiI2cA12vfgzxvdu1VeuwAt8eHUUZgP2PHlvR1qbWY5DdiuVIrMABTiP+OdarKH" +
            "b0zu4Nc3fWmAZzmJFyFH5LULcH1e4QQr7k4Ivu83+BRNEg/CFOJ/TEYnabUOjLzVsd9W3rDYBAV5gsz8XhdJYgo7QP74FSO5" +
            "eFh9asF/WO+9oY0KHmE7S8KqDFn0y7muI6x/iMvjMeSJUrvCu8kxaBc5Qg1aQ9VfhxKipJekuLef70hcB0OnUmCv0Pj25nq+" +
            "WCyUTADce2/h4M/hoHVEqMPhQ3gXICcB6NsglQqf5SDGJSw82r9FvWxIswKnvDA98ytNB9/QKo3k9T/GBQez1WXO/2Z6qJFB" +
            "G0/2d6t/wBDGNoCG5zJuGTuHn/VWZZ1Ebc9P1KuZdYcyRzoz25L5jgOaEJZJmrNAzrYrsYlZpKtcRNsGo7jZT+B1zTIhYmFX" +
            "WjrexuWmV3XMs8KCh9KeFSYlKuGx1rT05zDxuQc+g9c4eSLcK+Tgd6QNPsUM10Zt7JrRLO/LrM8Tn4WeswgLoeF0Gr1pRarK" +
            "JE+ZgudeDzVDr/X3B7VTVYU1Qe/o6HxXBdwU0YciYyzc5fPK0y99wAJD5OIIebO8SW98z44GHm0NVVjK7XD97GZx4gWt9vre" +
            "MIbtRBCpQrNPHzq9LPAkDGFsA/TRGE9cVnMpLOZ29m8WrXOHlcPIFrI6YP6fbvMmdkC7r4uUYcQ4eAw1+gDGXVbXJp+gAVyn" +
            "8wLkvpm+xHUwRNEdobA4YRjM+LSkDkGGoNPaFTV0f5WvirtxhO7weBQRniVQ2yYzOAjji4uZEH2JMfz9re7Ba+gZpfu7ilrx" +
            "hVBaYC3yBDVzP8fVkFnuEcBbFTjITn4ReSLepR6UgDXIrfHeMFeXQniUtJ2+LwW/49HYd5AnuMSWWAPwTOyIxQLcDpdpe0EO" +
            "M7X3R0bLqL5bs9Fp45i08BWdvdG0qUJuwRTOlzECOP1/AZQR2tWdFZMLUUaURQPmzpcz7LH4xfApEW3O7+m9XR1/TL2BEqMs" +
            "GmBXY6X7iIwG6qPMGBndMy42iiIAfUSmb3gZE/icD1IL6mEIfc6gfQcUAQULoLFFPmnpaAzY3NAmRhchKQC/DO8+mPCLyXJ9" +
            "yBIdh5dSZxkFomABSATTXY/nmtDw4ETfLnNOirQNOFHhfxzmZYjP6C8K/CxL4QoUiIIzQhEbHckomrjnf4C9Msrn6bie+/1M" +
            "sXAJ8/Vv5XJZms7UPfT7v0dBSCLkwpUpTvsBGOU4LQCMcpRVADSE443/LjdCKOu/x7kL3F9pQbu/X0OZ8DYAAAD//6puvfcA" +
            "AAAGSURBVAMAhtIN1bNKO8EAAAAASUVORK5CYII=";
        const string IC_MINIMAX_COLOR =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAKt0lEQVR4nOxaDZBWZRU+573fLrDAAstP2w4byYDE34haNCB/" +
            "jkhjtEEoZqBIhmaWIJCNjg1jpQ3TDxk4TcOkAqaJMJAwRA5sAWYUECYOIpo5QvwvP7urBLvffU/nvb/v/fnu9613P5ZmPczy" +
            "fvee973vOeee9zk/3yegnZOAdk4fGwDaObV7A2TgEtDhUfM7lTU33ItIU/gPAOUpIein3Xau2lXI+g++cMssQJoFIDMoqJGQ" +
            "nuyy6aWXoRUIoch0+uo5t4ABy1j4SlT+hhIQ3ZF+L7I4p3zXqtNxaxsn3ToOSD4lEAaQmm/5qzfuYGPM7rRx43uQgorqAXXX" +
            "3vN5IFrDfyy00ppHsEfb9jSVSsngD18Orz0/6bZq0zQ3sbJdyFqnppOtPI98eMcR0Tq+uhpSUFExgL19hRJeSU9KaID/8rjf" +
            "5qr7qO7XNIydMSW8NpuVv0bELvYyBHvAfeTZTl2KEedrJj8AKahoBjh11d1XsrCfsd64JSyuri+TFRW7VgyTJg1z7ysjSBI1" +
            "4fU8/4vWm7eUpT1NptG98+Z1VyGIal52Dm2XUPNqIAUVzQBEor/9omwlpCmXX7FtxQXF67l75X4J9LL9Ji3+CH1tw/XTBymP" +
            "UUZSb5zd/qmKrWvqFa9s87r/sNCrbc+wbHgNpKCiGQANWWp7q62EEOJkgI94zNbOskK3gFAGlKJzbGw2HNH5UuIRF775+eWQ" +
            "goqLAWh7QGyske4E91CHyD424L5pnYSw+R4YpKAiGiBjv2D3rMft7GAAhSc0+fcxxjZSOo4DOYzXAiqiAbKgY0CELA8AGwPC" +
            "SpQ67g+5PcC/f9l6gPt+4pUAN55j/BsMYECIpGs8d2IKajsM8CbkeIOFYgD9v2KAolbBgHTUYgOcGvS9qmMD5g/NO9GUlIgB" +
            "RFLDABlgcaUUxAAMaCrQTSwL04Gmj+rUNPOGz8XxCq4FjvZfOI+Ll4WmbK7mIgSOD5p3huVaXvnW0ochx5NdD8h1lu0EIT6U" +
            "eRgQq54sCPwvTp84nHOKZVmg8WxSyM64nvejLYaU38bnd7yj5hTkAceueHAjD0+wVNWe5QkqWLmHTgz+zmtnRzzQPbIoWzwM" +
            "sMT28oB4unjrjV8TQtUONB70dIPoRmng29lZY6c4T0qmI59+8AesxJfcaswXzvkAOKKpKftMZKEhMBEDuJLRMCAgB3Kql4gB" +
            "Vgng7S/D/AvTbuiPhE/r+2MgapBSfBXddV1VogGOVi3sxXMXoVuNAR7kz/P5KNzJD/+7rwtMrRt6X/CMtSoGBCmIARTRAY3M" +
            "T3hCR+1wLeVTMw0If+O6Av9fLk38YSIGmCVipOG4sW1wXFj57s83WcYZMnebIeF9WwbFgjH8abe32JBoq5ADAywPkC4GBD0g" +
            "wx5AmBMDLA8Q3nMiHsAbj/c9iNaXvFA7z2Gtz94+YQDvN8HxjdGJHmCgGKh5PRgdz293eVVvLj3ETz+kCXVlYLEpKBEDLA/w" +
            "MCDoAVlBiXmAFzlVmRk0Ht0xqTMPvSyu/fjtOp+X/snTCGFwMgawF6NXdamOXBlFlAA3nGPQmy4VBmDQeHChyYuZtvFCKqre" +
            "mtuZonwgKLRQFjsBHV3ieJeqFoiXyi+TZOwM1zPzeIAfymK9GLTzEcsvtBZIyAPy1QIxkmldM8itYgs8AHJ4AObJRtqqHxCI" +
            "eLEe4G7A+dr7fRZ9k5FykTDkP8yMeWe/Q4vP6kLaaGt7QPMZM/Q0HwNEeCd0QpmrZCQK8FprIXpY4j1VgaChnitzYAAjn3B3" +
            "jr5EP+eBaJQQpGEAWYC6hDep4ts1hml8NTi5cAyI2DkLyRgAmpRxDpCEAfwvLwZQ/LG1BdUxALHMS5WkrAhP1jGgpMIIhhwX" +
            "vePwxMoDEjDAigJeihbJAxIxQMfNmDzAiUpu8hbyEPfa8QAtSXYOl0YikP1GdUiDAd6EAmoBDFWDefoBhWMAKg8AJ6+M8WPp" +
            "hzjM9RDIFQRasR9gmkGx8vQDEvMAZ4afBziWVrM57SkNPaiDjgGV+372YdxezpsOSZMvD3AOqX3WA2sjtQC2rB8QyAMYMINM" +
            "WyMtD8C97iHmyTeFnjTVwwDEA+GNvFdnmyG0kaBkDHBsluNVBjAgZIB8/QDtUEfXenhue0CGN9nLQl5juYXAkYerHt7PZ6wW" +
            "hJzIUwa7oYxD0h6IkzKXf6sokMkXBdCOpPFZlvWFajwQCmdd/jwgwpXg+IyTB7CBFvPVV/iqJ9j1yRAWeojt9m4cpnPssY9B" +
            "VEovGkejgKoFzIQ8wIkCVmUjI7UAGcLfXwb5Kgr4eUCOKABeHhATBaSfB3zq5GPv8uV4vnrLj+voxWF+2nsS5IS+/17yNkTI" +
            "3tyxdFCQ1uwHCBHg5+0HgJ4HUEzy5mOAVcH1O/4j9ZX14MNVjzzE94bw50pmnuDxADR3fqLv0UfPA8Qq4UfQsJJt2BP084DY" +
            "LMrdwbJFoIStPvr4YmgJWe7l4WBQVFLJTO6S1lprA6+SJZIIkaqIXQwQITNYDA8Dog0RbwstWXNJ8rMEgZ8HpCKtHxBbCyTk" +
            "AQQf+HkANAZYqhbQjReqFVQtoAWP2FrAGu0wEJJLwXOh/YC8lKYWkGv5/gXn9wErw9zUtQDEnxJp0g4PAwC2pjNAUp6cgcR+" +
            "QI+dz25vgkwfRsJ+3Xb89pdhflIt0KI8IEQlv9u+U0gcxby70ZDT0v1IKmUe0PvVp5XrN8YyE/IAQlGv5wGN02/q3XXN5lOK" +
            "dxE6VPr9UsuIDRGxn9v2Nx7UX9pfifmJYMSV3CiQKw/IR6jnISEW0D/BxwDINONcmjx5yYeGUSpk0/2EXpbNfyrTzU1tiAEA" +
            "58bMmlg/eubtcbwkDOi06aVX+PY+Xwr8/sUSOlOSaT7O8+dqecBfOqzZ+gYkUNoo4OQalhhBNfP0A86OnrWA4XwLJz3P1o+Z" +
            "+VyYH8AAoogJ+QR8w79wBufwu3mAScYcyENpvx73zhcH7sCPlTj4dNP7AdyNrg+sJBjq9QMQPxt5ckI/QFHnP2zYw/xJzPB+" +
            "fKW1F47ykjEd1/zxIOShVAbgvfY6o8roauqG3TdYfTxz7T3dGMG+peUBdT13P3NYX2uVwAT+qw6TlgcQmrFI23HDhi1ZwqGM" +
            "FNN51nf5MQs4zbm5FM0hJS9ueRUKoFQgaJDxK4nZrztgXMY515snh9/7hszCcPX9g924sZR4MrzW6j64SCWiKEFWw9KOAoKM" +
            "nBBavnFjHQ9r4SNSKg/offAX7Ia0TD+DrNVw7+xbd/Gdii59H48sDvQEKbknGIMBrUWpfyLTu0+vBSzhj0Fl2Qigd2P5ulYa" +
            "zeNw26PZyMIU3wu0JqX+tbij3CMnht7/Ise+GhZ6JLv3v1iD2l57l2/KvRDO2QmE9Yp76CwuhHroeYApsBGKRK32c/lP7F/2" +
            "Og+vt2DJa162gtCzYeyM2eWvPL/C4pBYoJR3MaArddoLRaKina18dOq6u7pmqPkgI8Enrda+1fuTO1nnXjwORKdk5XFlly1r" +
            "Z0ORKDUGfFRSdQAD5Vz9ewm+5iKFBqLTYuLhtJTmQigitZkBFHX/66q13Di8g23Q6JaobhrL2h/gADi2vHb9aSgitdkR0On0" +
            "uNuqM2TczO4+lsNDHWboz11rV78Al4AuCwO0JbXpEbgc6GMDQDundm+A/wEAAP//lRtVWQAAAAZJREFUAwB9s4wkQxpg8AAA" +
            "AABJRU5ErkJggg==";
        const string IC_KIMI_COLOR =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAFCklEQVR4nOybW4gbVRjH/2ea3RUftC2bzGwiiopWKpSKFqy9" +
            "YK1SL8UXRRBq8frgBbQP4lPtsvXBPijiU19UhLYvXhEEQcR2hXrFVmulWqlWmmwyO9vNuqzbZGfO8X/cLkZpujOZW0z2B2Em" +
            "c2Yy5/+d73znm3MmGXQw2RfUWiHxCHe3QmCC2wOQ2GPvEO/MnSPQoeSG1FYh8Oa5ypTCx7bEZgyKuoEOpH+nWsbNG83KaZjb" +
            "TAM7/t5HB2IOqdeo7OHznUMvkLIPizvSAxSwdr5z6AWGqGFlBh0IxfX5PC/bkQbwi/TwQ9cagN3kWwf4pVsNMFb3cA+HQTmv" +
            "AUxz4DEhROFcZYykZyqV0ouIGMuysoDxBPyNUtPlcmkX/l0vTzS5ki3/hSdwb3VQFOHnBpZVUOcr9zxx9ejoqeOICNM0c0Is" +
            "OsCqXePnfKVUhY1gNR7LDqnrlMDFjccWKUxLiZOjg6LceDx0F5DSi6wbnRU/TPHL/F2hHCHkLf89Ovq8OASftE0M0OJZnc+5" +
            "e4W/K5TjumKN45R/RgjaIhGaE89+60u8dvtZ8cVQ4jWpe0Bw8WDw8tY5TuVXRECqBmhFfL2ubhofr/yOiEitCwQVT/knZ8WX" +
            "IhOvScUD+vv787z1ZwFa/oTn1deNjzslREziBtDiM5neg9y9zM/5WjzgrnYcx0YMJNoF/hEvfImn/J+0eGY6sYjXJOYBrYif" +
            "mhJrJicrY4iRRDwgqHiO80dnxRdjFa+J3QNaEH+4Vvtzw+RktYoEiNUAS5bkL81kMBxEPKP9+mq1OomEiM0AWnxvr9DRvuDz" +
            "kq8pfiOjfWLiNbEYYE48x3nf4pVyN1D8FBIm8iAYVDzd/qAWz6EucfGaSD2ghZbfz8mMO7g9g5SIzACtiC+Xi5u4rSNFIjCA" +
            "ULlc7krOG+rcfsDPFUxvP61UinciZfGa0DGAw9yzQvT4Fq8RQr2NFN2+kdCToq3AwPcHPw/a9sh7SJlU5gPYXS4yDONd08y/" +
            "ns/nL0SKpDonSEM8xGn1I9lsYSVSIvVJUT0pYhj4yrLyzyGF5fpUYkBz1LCU7n22bVeQEG32foBYzxHlWC43sAkJEaMB5Mt6" +
            "2Uw/4QW5il1iMQPkR6ZZ2M2vFyBmYukCTHReYaKz7ezXvlwu/5JhiCcREP7OcddVd4+NlY4hJiL3AFZ6e4N4Tc22S09xMeMu" +
            "lk0E+S16w1U9PeIwV6ifQUxE6gFcfX3ctou7m5UvXVq4pLdXvc/bXo+AsCt9woXQ+8vl8igiJDIPmE+85vTp4imu5d8gpdqF" +
            "gDBn2MjqHs1mB25HhIT2ALaMLt/Cx9p9CIBlWTdT0FusQj8Cwlt+wDmEbRwuTyAkoQ3gp+WbwadIU4jM3tnWDY42BOPEd/So" +
            "Q60+V0QQA7xr2S9/RAhyucJ2ZoNDCIGU8gEaYQ8C0haJED1oJ136RnamEbQIPaGlnKFtMkHOCX5Zq00tpxE+RIK0VSrM9YAq" +
            "c4jN7NtPI6HZorZ8V5gjyqtSzqximPsNMRPaADMzIpanRQ5x37tufQW9YS9iJLQBenpUbM/wepWI3rCFAXJFXLHhf/G6PAPk" +
            "ER0bXFeu0ikxIiS0AVzX8JAQjjPyDT3iVuYel9MQj/KzT78yp8uYTLVUj3ndl0nKasPwmr5/zyRoP1KGafXy6enpkYmJiXEE" +
            "pGP/NOWXjvzLTBAWDIAuZ8EA6HIWDIAuZ8EA6HL+AgAA//9Itpo0AAAABklEQVQDAE3dKOnexrmbAAAAAElFTkSuQmCC";
        const string IC_KIMI_COLOR_DARK =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAD/ElEQVR4nOybW0gUURzGv7HA6KF88FIZRUUaBlFUkGVWWFgk" +
            "vRhBYBJdHrpA+RA9iaI95EMRPflSEVgvXQmCICQrKLtQdrHsgmUUpiZpEV5gZ/pOu8IGuc6cPWdmmt0ffJxhZ87M+b79z2Vn" +
            "ZscjwGQcsQoMEzs5WQ4D/Wxvw0RDd5VxaWQZAwEls8YqNwyc/dc8y8LNbhMlqDaGUxBA0mutXDZnRpvPYNZlpaDqzzQCSFaN" +
            "dYrOdsRahlVgmqlIC2QFWEDBWMuwClKMISwcjwBCc6k2l8sIZAB2MUN4mbABcDd58g14n6gB9A6HUMrToDlmAJZl7WaTPcrs" +
            "QcMwjkIx3GYGm72wd5Ya4Bjq/u6PkDFKT37zzSEDm/uqjS+wswEOxhpjkRwO4B0Uwc1lQlyxAfNsduni9qdEf5BRYy2yDEyO" +
            "/mychQHTREdPtfE1+nMVAeRxAK+hgIj5O1SuzS7cjbGK238FSXxzDIiYv0/NttlFmF9B828RB764EJIw3wUF5gWeV4CEeXHw" +
            "WknzH6AATwOQNL+c5j9BEZ7tAhLmO6DYvMCTAGh+GpyZb4cG8wLXd4GI+XvUTJtdhPl8mu+GBlytAAnzb6DRvMC1CpA0L051" +
            "vdCIKxUgYb4VLpgXaA9AwnwLVeCGeYHWAGh+BpybL6T5PriEtgAkzD9C2PxPuIiWg2CU+WybXYT5NTT/Cy6jvAIkzItlPTEv" +
            "UBqAhPkmqsgr8wJlu4Ck+WKaH4aHqKgAcdNoDttm2Dd/i9rgtXmBigAOUXepqQ76XKT5QfgAFfcEZfhBbWcIV+AxXt0PmERd" +
            "ZranqYnwEK8qIBrxc7eU1dACD/DDTVFxU+Qhcz5Muf643g8VEI14JrCF1dAFl/Db+wGFVBszL4ZL6AzgOJWD8C88J6RRNxhC" +
            "PTUBmtG1C5xgGVdE+ouXFY5R++Ac8cxxE9fVBk3oqIDKEfMCTg9R+zm5keqHM+ZSLQzxIDShugL20Gx9jHVNZ3OVWgznNFJb" +
            "uf4eKERlBcQ0L+D8z9QSTtbBOUVUK0NcD4WoqAAxv4zGzsMBXO1qNheodDjnGlXBbbYjTlQEMOY3H2PdWWzOIfztyiCCeEY9" +
            "lf1doSKA+fG8oBDZRiWbGsTHNo6jAQ7xxYUQB17LZhnVCXmkrhl8cyXIEB6wyaOuw0V8dSksngdQJZw8QLlyt8iX7wozhJNs" +
            "llIfoRkl9wShAYbwnM0ChM8S2lARgLbf8OIpEVWGcBBajg3/xevyDOFF5NggdotGKERFACG4BEN4TK3l5CxqFyWuPrviGYed" +
            "C6F8NqkxBtUEj+EYxemzk2P5DocE9k9TdgnkX2ackAwACU4yACQ4yQCQ4CQDQILzGwAA///YFKu2AAAABklEQVQDABFJXSuC" +
            "JTeCAAAAAElFTkSuQmCC";
        const string IC_DEEPSEEK_COLOR =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAI5ElEQVR4nOxbC3BUVxn+/rMb3oilRNoRfHSmCrTTqY9SLWoZ" +
            "7cgj2SBgELFSBBvILn1ZWwvtKIpUq608s2mppTNltLZpobDhUcEpOFMerYhWWvrQDDNVLFCt8gp57P37nU2T7m52s/fuXhKY" +
            "5JvZ3HvP/c+59//Pf/7XuTHo5jDo5ugRALo5egSAbo4eAaCbI4jzCBMj+tEAdK9CvlVbJX+AD+h0AZTO17GqGGnUuURFRomi" +
            "P6Av9ultflizVOo76ss+YYgMFTg38PL8EUAootNUdaYISkDuxTbyouVI9mG2H2tAINc47DHLHlVl6oSb9PYtK+UYCoR4pEfJ" +
            "fC0Tx/k8u17NzldxhAFJt+0MHidHJ1RwnLN7gvdHsG1oB69wV6xK7kMOUIiXkfUDrdeU464ikbL1VfIf5IFQhQ5p7o1GVwIY" +
            "G9YBAwSVfOqdnLgh8Alk4v+1UfNBN7QTwzo+ILolpT/wT2rFQgpwLTygtEI/giLdz9OXcnqB0ohGBkIPC/QXfjLvFQFBcXob" +
            "Z28YxfBYKOIcoICudDGMFeRFEtQY+w7m7+qsGsA1VhyI66OJdXs2ofLdWFQeyUWWSQPSERe5YvMq+VvrdVmlXuEIRlJSl0Kd" +
            "II8f4vNmkKdBLc/GixmNYEmlXiJxfZ6EF+GsQ5eFwtqbQoh2RBUwOGR1vkMa1Xt5CFFrJ3PcFQod1jbD8t5Z0pSr6IF2GjBu" +
            "jg4u6q17xEqtE0He1p9UmbkjKicz3R87S/sM7K/1ucfRFbQLN8MFHHqVFAGUl2uvM8W6k1L6HLoAFMKrzXGZsPVBOZTpfijs" +
            "PM13mwIfQAN8prGfDEkxgvXFzr1dxbwFZ2NEMKAvlM7VzNon8hh8Ao36U7+/X061CcAaDBG5HV0MCqGYi/mPmYRAd7eBM1dw" +
            "BMgx3jYwC+15mwBoEB7COQJrfOmqdtOYfTr9Xjwg3+ThCAqAqIQ2ROVNe54QQElYr+lK1c+CC0V1Z9l8HZPcaMPfOORLtBf/" +
            "gHf8D46MjVXLntYG0/LHuRHnIhhmq6NbS+fp6OTmzVXy+pkm+QxVebvboUj7RrORz5L5nSmPKJutA7WvWpXqi3MVipNcF9db" +
            "G5DcHJqvI8jZwezdcIp/dpDNtbVReSITTTDeD9cYzcj8YQ5g18kFNEyfQFcikXDpM6Xh+I9ro4FFrc0ax2TJEMxbd2p9PDVl" +
            "b66hg8zsxrRFSYnOWmXqzYKNa+REMqG1E8zurqUFGcsXGsMe/dHJoJf6UWnYmcqTChZEdrNAMCkL6etumE+MyQG3kf/rWhsc" +
            "lTGborIrV0eGy+VidA4FMQ5+QGENUwNn7zJXSZdiPyfiU1nubYlFzUS4QJAMDE9pMe4yvk3VUsNDzfh5+rEi49zMWbkN+YIJ" +
            "CnOBx1svSyI6m4HKUr7bB7L2ycZ84p7+HS5hWLgYnNLg4GJ4gA1bY9HA99TIlZy9PyMPqDhftKUyZqDD7PWmKlnDqs8EG64i" +
            "Dzgwf3FLaxKRV/LLwPkw8kDtSvlrbZWha5IF8AgmL5X0+c8FHX2zJZOjEBLLUKYhD9Aw7nNLa9pLWUaiANDd/Jx5+VW2WuO6" +
            "k2IDmsQa1SMUREXSWDHapO/DI+xkuKU1NDin0tq+gALBosSfiiC2QrPNDT2FNZrl2TBPh/I8JR2mJjzASYrBLRQ74QHWi76V" +
            "3GDjcAYYH0eBsMXKWJX5Kt3qr3PR8pkX03D9suVKG9LvmyJxHanS/7sXFlqWwKF2gygmwSfUVgVu5DOect9DvpLesnG5HPEw" +
            "xmZ4gKHkX0pv5DqcAx/Bym85GXBlma0GlkacveMqNM0b6Tsuur/MJXMQHhCky9jK7aYFaS9xuU1FGW3l5dYyIi7TNKj73USQ" +
            "pBldFNQ6BmkxzsZRtkxqqQB3DG65VMMjzOli7EokDekvoXo3fETtQ/IGU9Gb3NJzEvrwV04XGXHFvOJ03MDT/oCF2bFImvmA" +
            "59oNCEyeMFdHwUfUVsujCZd3VqAPs1ZwHB6RyKUyWU67dcf63APwGY0NMptCOAqf0dRscm6vZUJCAP16YS1nvP1Go2B8aVi/" +
            "Dh/x7CPyX8a+c+EnVO9/drX8G3kgIYDEtrQjS7OMvvJrt6qr/Tu3YFXmGY67Gv7g5T7HTN72qq2cYBqwym5WphNYtxRv1Jxb" +
            "V15x4pS5hYdXUBjqWR+cUlMjjcgTKRsjdH33MA1dnImQ+/sPshpTiRywUSRp19CLvCJF5ic2iMlGy1L8Jx2j+/ItrjDxup75" +
            "wm9QAFIKSvT7P80WsLAaM48lqZyGRh1cSoZYNZKw06R1oUh8Cffi+2Wi3Vgtr5GJmcgHKvcUyrxFu4oag4kZ2YgphDtLI/HH" +
            "0QGCvfDC+/Qg47IQQX2VmvHlTPSM3NZRYxbDE3QZCyhL4AMybo/T8t8iosuydaLH2M00dcrmqLyVsX8kvjzjBiWtNYsnd2R+" +
            "pvOkDXyQC4rXWO4aAZ+Q9fsAqi734eTbyP4iRxk/TOcMtguirNdobtR/tWhAu357mIBMa92ZSX2mYxOeqcgBTsAOvtt6acJv" +
            "Y6vlbRSADj+RCYXjv8pV6+MaXhQPYGlyFMaC6XXG6LbsffAOhTeDGrQ1/R61Zw215ztwAQriOJfsfaYJq/MVRM5vhLgc7uJy" +
            "+FkuOjJlrTk3MHA5Ly+EOzwtjtxtjWHKMyPK+F9XwQM4EbfSKC6HR7j6SCpRAhd9uO3TEt+hT6qa7XybOv7iLU3ORGrCHa5H" +
            "UH2Cbno6PML1Z3KTwjrcga5lj2vRCbDqTe2YTj81kNx9Ay2f5A1vR8c9P2rL846axSzV18EjPH8nGIroDyjtRTZdxVkCmd9r" +
            "HLkhfWlY49rUiFHcyitivHE4kWIXCM8CsOCSuMAYMLdXayB9yxM4mwdUZAn3BX6HTkJeAmhF+W3a93QjZjHstZ+e5V9NVqxj" +
            "ZLcifeu6M1CQAJJhvy7r1RvlKmr35GwFZxAZG5S8z8cZPsgHHqWKN/OqTmD2NQewzo9vfvOFbwI4X9HzHyPo5ugRALo5egSA" +
            "bo4eAaCb410AAAD//01WZeQAAAAGSURBVAMAwihc1bzPbrgAAAAASUVORK5CYII=";
        const string IC_CLAUDE_COLOR =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAANEUlEQVR4nNRbCXRU1Rn+752ZQELArVWrYg/CJIMilKVapAoe" +
            "q0crVAEnQ7UeqSJarEdFsqCHNipKJkCxR62t1oookmQArdtxOa0bKB4VUI+QDVBwieLCOkOSeffvd2dMmOW9N29mEqXfOZn3" +
            "3t3vf//733+5cVOeaCoPzGDBQSGEm4naBPFG5gMzfLVP7aX/A7gpD7TOCQwzJD0oSMS+8TsAvyWC+hbj80LKAU0VgVlo6Ga8" +
            "PlgarK+hXoagHLGtenrf9nB4I1a+1CyfjegI36JV71MWaKooW0pCXJGQVAMizKVehKQc0RHeP9xq8vGW3TdSFth6y2+PSZm8" +
            "RtWWuYES6kXkTADlim6xyxeCft8y1/9jcoiOqDHdtB+jM0y9iJwJMHTBE1+D0TfZlVFK/pEcAtz0s7RE5nZv7epPqBeRMwE0" +
            "BNOLdvlMfJ2WFeQIfJxJ4mbqZeR1CniYg+0krga79zPLx+lwVMf+yO/w+s9MbTHTMSJVJAvRSA6gidwRidzJzKO/67eptLb+" +
            "Gid18+KAQQtDbeitnOx7uJmcQIijTVKbM9SibTdefHh7OPI2XmdjG43XfxjTzKYK/+XkAHkRQMMXrL8fq/eOXZGmysAFlAFY" +
            "/CPS0hTbcsBn1ZOKOvr0eR6cMyw1j0nc11gx7TjKgLwJEIf4g20280122VYDBQHes6u3N1LYgMfppnWF6C9YBSgDeoQAvtq6" +
            "d0DyhywLCHFuU1Xg51bZLtl5TFoic2RI/1MsOSCmNJGw1TaZjf9QBlgKwcaKsjVgSy9YaTv0+09cqvOaIYue+NKyJUNWsdsI" +
            "QAAVm+YrvhO/55lXdqfpC7Ar1ovqakXmY6s0UZqS6zN97EQTNeWAxsqylWChcVowYX+NwfNiw+VZj708wqqh0sUrvhIsrNVW" +
            "zQXl0840yzKYzQTgu2Zlm8sDEzG2jDaCYP4rOYApAbCKU01SjwdV1zZX+i8lC+DouRdn/wdW+SxUkMwHcWz6GDhNsGq1mCUt" +
            "p0xgeqZ0YcMScgALGcCtZqn6vGeSy8EJD1spOBj4LLIAVm6s2YkAdj8xNU0KtT7x+8tqf3GnwU9RzOK0A39a0NHu6AiM9WPe" +
            "hniV7DG9PRJ5b3Ol/9TUjNJgaA1mVG9T9870JJFEAHBR2Btc9WFi2rdhucLW+OpuSkwedPeTu8ghTAkgI+7ZnEEJgYAskSTf" +
            "b64ouzY1zy1UhU3VkU1V/smJCdhaA5PaZkoSXuCaP6HDiZQBGPOs0pr6tykLmBLAe8/yPcSRMXovUaZOhbgfUjnUWPGb/l1p" +
            "g4Oh7ZjVYptK8xM/RcoWYEHd539Tedmv8biNMo2D6HGtlFGWsNQDtEsLQm0Sjq/ZlAFgzUsEFW5oLvd3W3RuwXdAN//GosbJ" +
            "TeX+g0qKoB8lD0ps0M8tlf4TkedE6L3fr0PNoByQURHS0hQseTI6sbfMBA0GN6zr2hLggt1IvMO6ZzkPqybMtEAQ4AOunuCO" +
            "klgJ6h5u1y2IvFdJcdHAJaEI5YCsXGKY3BJMMrOnB0KQKXK15iLsX+04Ocm0mFIX4Fj7VMuSxPTDjILi3bL9Nkw+oyElWfm9" +
            "taGVlCOy9gk2lpeNA8s/olfcviS3smFMFW45mFiuNi9CD0gpVijmlw8m8T5w3KpMml68MP+ttLbhOsoDWdsCvoUNawuKCofZ" +
            "CrkYxBDhcr+HyQ+yKgF3+vlM6qikWlqVdjJ5og35Tj7eXx5omeMfq6R8NDM3WAN7+GVw1NmUJVxCeYfUhFopT+RFAI2YNyYc" +
            "xjktetV9nQhsk3m+YMN8p+VbKqeewuwuxfFaAor7IMc8YL8l2orNmwBdiJm7TMvw6qPexVbECkw5rnXO5KOjwj0KHDUSCzKc" +
            "9OklaLhNWzU9RoAuNFYEakUmN1kekMTne4MNL7RUTDnBEJ7RsPowWRqDI3UU5MdPKDvU9TgBNForp50RZX4chPgp9TSYn8TO" +
            "/WWq8pQLYLiNjxFAsy+Uj34K8Sx4IHZ7Ol27ou7wrnwCnDtu8heGCwTMX3E9HUpg3oWZvwiOeRpy5DGBYGQdEgLW5Vl7fneC" +
            "UjshOHaSfuIbBsweaCF7cI7uVsTfuqPyy6Li8Nbjqp9OiuTATjgbnADrUDiOEvU0MIc3IRdekoZ63rso9GZinibAdkxoIPUc" +
            "9oA42o22HVJ3B5SaNqRdAKF0Gn0PQN/r0fc6zGmjVOJd78L69Xbl4RNUlzDLNVglD/UMBsTd1GJYbH/1ipSJA2z8EZpfh+c6" +
            "F9Nb7UVq/bDqUEc2bcSG1zx32ulKqZlocSgkqY7y9IOWVvTd+wA6FAAvsZ4oWPktoWit4e54Mx6fzA+O1ycWgSnoW+Thzn5R" +
            "6S7Cfioy3FQoDVkEwVkoJP7wxHtf/cSA+yb0cplWjSkPxIIvgrXRtA3bagdL3uEil0ovp5SI8lfRgugXTgjUiwwah3ZkRhX/" +
            "VztV6YcA8ydw7bdholoWQaDzNhn23Btz+lAvEkDfDVCGnK/jdHSogekrbKcbfbX1y3ucAPFIbVj7FOdaBklygYwOFVH3ELA+" +
            "LEiaJFIcqbkAobPzepQAOiILQyNopZJi4G/gZ4RVON0OMIA+9yhj/OCFq1r0d3PVlJNIecZDWI9lFhPQppeyBNrc3iMEaCn3" +
            "j1dC3E1mtzziHUE5Erfg7WsQ51HKGbwTStc5Q4OhtODLtnL/se1SnIUJ/Qqf47TfMWNrzGvzIkBcwFEtXi+y6eUlOEhnKCF9" +
            "iumF5Cz6OHt7geFr5Imx+IMNNs+dfJSLPRPQx3hwCLxYNKq7BSyE9kxDBtydEwFiAk6JebZ6flznrigNNjzYUh4YZUh+Hatf" +
            "lFDiQwxuAQb2GOUAHIVTS2rrVzstr+8S7DtQeAb6LGU+sKzLzsmaAHByQsDxbfYCjp9wGZ3X6mgyjKIjwx65MUXdPiCjarRy" +
            "ydlIv6q7FgJAZhclrLuh63U8kvKAY59g6+xpA5sqy3TIbLHV5DVrCcGXYNWndIXSwx5Rl2ZrKLrBuzi0CeVPTqi73SXEFIvu" +
            "D2Dl0kNqgu5pqihbRHnAEQEaK/xXRd0KsTpxlk2xZYXC7S2paVjVlYDBVeuweFIp5udKF9Y/oF9ByG7JrY0mb03dK5p7TNru" +
            "i61SiCDNhXGBmgC4zuGEaeCZM3OyZWxviX231x+2v4nBn0ohrvTW1CddmWupCpwHoffn5KK0o6Cj4zL9qlXrjgSnBsc1NYKw" +
            "nC2ZJ5t0pHWL1WQYY0m6nsXET+jKAHH8zYfvPhLbbVK2ARJLDmibc3k/ZYi37CaPAd1/RCH7UiffdKv/eIOpLq2CUJd2RW47" +
            "CjxJV2DBDTECDK2p+8iU3XUZKZb1Ke7fLF08Stv4yZl0DrbbK5qwlAUsCbDXHS4BaU19+hgglBF1JoKRs46uDu1LK9ApVqYJ" +
            "M+bqxKMLClNJygTaul4HFIXvQh+fmXR9Uns4crt3QWinr7bhDLT5SHIb4rT2gj6va+coOYQlAZhd/S2yanB+llidw3Cw3IOB" +
            "/CKltTUIYiRFeLHiI5NK0EECaK+SkMr0mq12uELjjDlX0OZ0yIT5KfnDDOlZt/XmKY70C0sClAQbXgMVFmjJDnZ7VbO7VDTa" +
            "7vo6jshpmFnywKEPuDqlybUaPi154Kot8bu0JgRhyA1kPuylXW/w682DAL2Skhsb1OFyI1AbGEoZ0GO2gO5MCX4nRdlBB2pi" +
            "STD0bGIaV1fL5vCmfRhoYVeai8S4IcG6NxLLaR1iv0e0wAlyZHqPfBeO21u7vvRtFfT9TKKRpBfPJYxzvTWrNpAFeuSeYExg" +
            "Ev07dfLgnL+nTl6jObL51MTJa0QN4/PUcpDo30jBFtqmuKWlamr3NtL2AcI9wxMvdei7yopdL5MNeoQAu2T70nRrjDf1KSoy" +
            "vyGqKO3SZD+D2syKgoCPW91UMZR7WeK3vpOgL3WA8FUHU8VhZneZupA3ARorAzfoGyIpyQckGWWDqpceMK0kkgmgLznYnd8F" +
            "rK7GY09aMxB4cLvPS03HCRGEQqGVti/Q+mtm1uPBoeSB2F0BKdJOA/jlLvLVhp6yqgdhuRGPEQfLU4s+WcgGsYtVFvcM0J8P" +
            "/TVRDsiZAz6uuvQIrPwKSh6JkWnyOmKERxJLwn74gjJAnwoQav8wy5NCTqAckTMBIip6V6qRA+fjFXaT12gvcI1M7Rf2+ufk" +
            "AH0Ki/T1nA9T00GYnN1jucsAHX5O/MRZrJ2MmaopVmPSm+I2cgAtU2BGl5lk9acckQcB+CEsXfz2uKJr4Jx42Ek1RaYhMkcE" +
            "0NBmNB5X6LtE+lvfTYYQvY9yRF5CUHtZwvvcxbbX6FMAVbk19UoNHJtX+Woa/kVZQh9vdhLeCXo9MJKILZX+w6Ik0+/xws4v" +
            "XdjwHP0A6KF/mXGG2OVJkwuXLlfmU6C38L0SIAZBtycncGtxtK+jf4/rDXzvBIA1WUdKTdMxO3z+RXL07GMXPbqffiD8DwAA" +
            "//+JCaoQAAAABklEQVQDANuwQuYJT8EsAAAAAElFTkSuQmCC";
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
            // 首屏即刻显示：此前首次 Show 押在全部供应商查询完成之后（_firstShow 分支），
            // 任一端点超时（15s，GLM/MiniMax 双端点回退最坏 30s）窗口就迟迟不出现。
            // 先塞一条占位状态（各布局渲染为"正在获取 --"）显示窗口，数据到达后由
            // RefreshData 的回调原地替换并 Rebuild
            Providers.Add(new ProviderStatus { Name = "正在获取", Type = "glm" });
            _firstShow = false;
            ShowActiveForm();
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
        // 每次弹出按当前配置构建：勾选态天然实时；置顶栏模式下固定位置无意义不显示（与旧 ContextMenuStrip 行为一致）
        public void ShowMenu()
        {
            var menu = new WidgetMenu();
            menu.AddItem("立即刷新", false, false, delegate { RefreshData(); });
            if (Cfg.DisplayMode != "topbar")
            {
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
                    if (!p.HasKey && !p.CanUseLocalCreds) return;   // 未配置 key 且无本地凭证可用：跳过（行内显示 "--"）
                    try
                    {
                        ProviderStatus st;
                        if (p.Type == "mx") st = QuotaService.FetchMiniMax(p);
                        else if (p.Type == "kimi") st = QuotaService.FetchKimi(p);
                        else if (p.Type == "ds") st = QuotaService.FetchDeepSeek(p);
                        else if (p.Type == "claude") st = QuotaService.FetchClaude(p, false);
                        else if (p.Type == "codex") st = QuotaService.FetchCodex(p);
                        else st = QuotaService.FetchGlm(p);
                        results[i] = st;
                    }
                    catch (Exception ex) { results[i] = new ProviderStatus { Name = p.Name, Type = p.Type, Region = p.Region, ShowName = p.ShowName, Error = ex.Message }; }
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
                if (p.Error != null) sb.Append(p.DisplayName).Append("连接失败");
                else if (p.Primary5h != null)
                {
                    if (p.Primary5h.IsMoney) sb.Append(p.DisplayName).Append(' ').Append(p.Primary5h.Currency).Append(St.FmtMoney(p.Primary5h.Remaining));
                    else sb.Append(p.DisplayName).Append("剩").Append(100 - Math.Max(0, Math.Min(100, p.Primary5h.Percent))).Append('%');
                }
                else sb.Append(p.DisplayName).Append(" --");
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
        private bool _edgeDocked;               // 吸附态：窗口固定在某一边缘，鼠标进出控制展开/收缩
        private bool _edgeCollapsed;            // 吸附态下是否当前收缩
        private bool _edgeArmed;                // 拖拽中鼠标已接近某边缘，已显示虚影
        private int _edgeSide;                  // 吸附边：0=顶 1=左 2=右（仅顶边保留原收起/展开机制）
        private int _edgeTargetY;               // 左右吸附时的纵向展开位置
        private bool _vertical;                 // 竖向岛形态（吸附左右时置位；"固定位置"后保留形态仅解除吸附）
        private bool _edgeJustUndocked;         // 刚从吸附态拖出，需鼠标离开顶部才能重新吸附
        private int _edgeTargetX;               // 吸附目标 X
        private EdgeGhostForm _ghost;           // 贴边虚影预览
        private System.Windows.Forms.Timer _animTimer;
        private Point _animTarget;

        // 行高与窗口宽度（DIP 基准值，运行时按所在屏 DPI 缩放）；卡片行为紧凑单行样式
        private const int RowH = 26;
        private const int PadY = 6;
        private const int WinWidth = 180;
        // 当前所在屏缩放因子（跨屏拖动时更新，保证各屏物理尺寸一致）
        private float _scale = St.Scale;
        private int RowHS { get { return St.SiF(RowH, _scale); } }
        private int WinWidthS { get { return St.SiF(WinWidth, _scale); } }
        private int PadYS { get { return St.SiF(PadY, _scale); } }

        // ---- 条形灵动岛排列 ----
        // island=true：内容自适应宽的胶囊徽标条，完全复用悬浮窗的贴边吸附机制——
        // 吸附顶边即自动隐藏（收起留缝、移入滑出展开），拖离顶边则退化为普通悬浮条
        private bool _island;
        private const int IslandH = 54;    // 岛高（DIP）：环外径 38（图标 18 + 内隙 6×2 + 环粗 4×2）+ 上下各 8px 边距
        private const int IslandPeek = 5;  // 收起时露出像素（DIP，比卡片贴边的 3px 略宽便于悬停命中）
        private const int IslandPad = 12;  // 岛左右内边距（DIP，兼吸收徽标测量误差）
        private const int VIslandW = 54;   // 竖向岛宽（DIP，左右吸附时）：环 38 居中
        private const int VRowH = 70;      // 竖向岛每供应商高度（DIP）：环行 38 + 百分比行 22 + 行间呼吸 10

        // 当前是否竖向岛形态（左右吸附，或竖向固定后自由放置）
        private bool VerticalIsland { get { return _island && _vertical; } }
        private bool VerticalDocked { get { return VerticalIsland && _edgeDocked && _edgeSide != 0; } }

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
                    if (!_island) Width = WinWidthS;
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
            Width = WinWidthS;

            _detail = new DetailForm(ctx);
            _detail.DpiScale = St.Scale;
            _detail.RequestCancelHide = CancelHide;
            _detail.RequestArmHide = ArmHide;
            _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiTimer.Tick += delegate
            {
                // 灵动岛吸附兜底：初始显示/切换排列后鼠标不在窗口上时贴顶收起
                //（MouseLeave 覆盖不到"从未进入过窗口"的场景）
                if (VerticalDocked && !_edgeCollapsed && !_dragging && !AnimRunning
                    && !Bounds.Contains(Cursor.Position))
                    EdgeCollapse();
                else if (_island && _edgeDocked && !_edgeCollapsed && !_dragging && !AnimRunning
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
            UpdateScale();
            Width = _island ? IslandWidth() : WinWidthS;
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
                    _edgeSide = 0;
                    _vertical = false;
                    _edgeTargetX = Left;
                    cfg.X = Left; cfg.Y = Top; cfg.DockSide = 0; cfg.Vertical = false; cfg.Save();
                }
                else
                {
                    Location = new Point(cfg.X, cfg.Y);
                    // 恢复竖向岛（重启前为左右吸附或竖向固定）
                    if (cfg.Vertical && (cfg.DockSide == 1 || cfg.DockSide == 2))
                    {
                        _vertical = true;
                        _edgeSide = cfg.DockSide;
                        _edgeDocked = true;
                        _edgeCollapsed = false;
                        _edgeJustUndocked = false;
                        _edgeTargetY = cfg.Y;
                        Rebuild();      // 竖向尺寸 + 贴边展开落位；鼠标不在窗口上时由 _uiTimer 兜底收起
                        return;
                    }
                    if (cfg.Vertical)
                    {
                        // 竖向固定：按保存的位置以竖向形态恢复（无吸附、不收缩）
                        _vertical = true;
                        _edgeSide = 0;
                        _edgeDocked = false;
                        _edgeCollapsed = false;
                        _edgeJustUndocked = false;
                        Rebuild();
                        return;
                    }
                    _vertical = false;
                    _edgeSide = 0;
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
                // 矩形卡片：清掉可能残留的吸附/竖向状态
                _edgeDocked = false;
                _edgeCollapsed = false;
                _edgeSide = 0;
                _vertical = false;
                cfg.DockSide = -1;
                cfg.Vertical = false;
                Location = new Point(cfg.X, cfg.Y);
            }
            else
            {
                // 矩形卡片无吸附概念：清掉可能残留的左右吸附态（含竖向形态）
                _edgeDocked = false;
                _edgeCollapsed = false;
                _edgeSide = 0;
                _vertical = false;
                cfg.DockSide = -1;
                cfg.Vertical = false;
                if (St.IsPointOnScreen(new Point(cfg.X, cfg.Y)))
                    Location = new Point(cfg.X, cfg.Y);
                else
                {
                    var wa = Screen.PrimaryScreen.WorkingArea;
                    Location = new Point(wa.Right - Width - 24, wa.Top + 24);
                }
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
            if (VerticalIsland)
            {
                // 竖向岛：宽固定，高随供应商数。吸附态贴边（收起时滑出只露 PeekPx）；固定态保持用户所放位置
                var wa = Screen.FromControl(this).WorkingArea;
                int oldH = Height;
                Width = St.SiF(VIslandW, _scale);
                Height = St.SiF(16 + _ctx.Providers.Count * VRowH, _scale);
                if (VerticalDocked)
                {
                    int top = Math.Max(wa.Top, Math.Min(wa.Bottom - Height, _edgeTargetY));
                    _edgeTargetY = top;
                    Location = _edgeCollapsed
                        ? (_edgeSide == 1
                            ? new Point(wa.Left - Width + PeekPx, top)
                            : new Point(wa.Right - PeekPx, top))
                        : (_edgeSide == 1
                            ? new Point(wa.Left, top)
                            : new Point(wa.Right - Width, top));
                }
                else if (!St.IsPointOnScreen(new Point(Left, Top)))
                {
                    Location = new Point(Math.Max(wa.Left, Math.Min(wa.Right - Width, Left)),
                                         Math.Max(wa.Top, Math.Min(wa.Bottom - Height, Top)));
                }
            }
            else if (_island)
            {
                // 数据刷新导致徽标宽度变化：重算岛宽并保持中心点不变（不打断收起/展开状态）
                int oldW = Width;
                Width = IslandWidth();
                Height = St.SiF(IslandH, _scale);
                if (Width != oldW) Left += (oldW - Width) / 2;
            }
            else
            {
                Height = PadYS * 2 + _ctx.Providers.Count * RowHS;
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
                St.BuildBadges(_ctx.Providers, g, _scale, out totalW, 38, 4f, St.Theme == 1);
            return Math.Max(St.SiF(IslandH, _scale), (int)Math.Ceiling(totalW) + pad * 2);
        }

        // 收起时露出的高度：灵动岛 5px（便于悬停命中），矩形卡片维持原 3px
        private int PeekPx { get { return St.SiF(_island ? IslandPeek : EdgePeek, _scale); } }
        private bool AnimRunning { get { return _animTimer != null && _animTimer.Enabled; } }

        protected override void OnLayeredPaint(Graphics g)
        {
            if (VerticalIsland)
            {
                PaintVerticalIsland(g);
                return;
            }
            if (_island)
            {
                // 条形灵动岛：圆角胶囊底 + 全周 1px 细边（曲度与悬浮窗卡片一致，参考系统圆角）+ 横向徽标
                bool dark = St.Theme == 1;
                using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), St.SiF(12, _scale)))
                {
                    using (var brush = new SolidBrush(St.Bg))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(St.Border, 1f))
                        g.DrawPath(pen, path);
                }
                if (_ctx.Providers.Count == 0) return;

                float s = _scale;
                float gap = 12f * s;
                float totalW;
                var badges = St.BuildBadges(_ctx.Providers, g, s, out totalW, 38, 4f, dark);
                float x = St.SiF(IslandPad, _scale);
                if (x < 8) x = 8;

                var fs = St.FontsForScale(s);
                float textH = g.MeasureString("Ay", fs[1]).Height;
                float textY = (Height - textH) / 2;
                for (int i = 0; i < badges.Count; i++)
                {
                    x += St.DrawBadge(g, badges[i], x, Height / 2f, textY, s, fs[1], 38, 4f, dark) + gap;
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
                St.DrawRow(g, st, y, Width, _scale, St.Theme == 1);
                y += RowHS;
            }
        }

        // 左右吸附的竖向岛：每供应商两行——环形进度（内嵌厂商图标）+ 百分比/余额
        private void PaintVerticalIsland(Graphics g)
        {
            bool dark = St.Theme == 1;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), St.SiF(16, _scale)))
            {
                using (var brush = new SolidBrush(St.Bg)) g.FillPath(brush, path);
                using (var pen = new Pen(St.Border, 1f)) g.DrawPath(pen, path);
            }
            var fs = St.FontsForScale(_scale);
            int pad = St.SiF(8, _scale);
            float ringD = St.SiF(38, _scale);
            int y = pad;
            foreach (var st in _ctx.Providers)
            {
                bool err = st.Error != null;
                var pool = st.Primary5h;
                int remain = pool == null ? 0 : Math.Max(0, Math.Min(100, 100 - pool.Percent));
                Color c = err ? St.Red : St.RemainColor(remain);
                // 第一行：环形进度 + 厂商图标（与横向岛同款大环）
                St.DrawRingIcon(g, st.Type, err ? 0 : remain, c, Width / 2f, y + ringD / 2f, _scale, 38, 4f, dark);
                // 第二行：百分比 / 余额 / 失败
                string txt = err ? "失败" : (pool == null ? "--" : (pool.IsMoney ? pool.Currency + St.FmtMoney(pool.Remaining) : remain + "%"));
                using (var b = new SolidBrush(err ? St.Dim : c))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(txt, fs[1], b, new RectangleF(0, y + ringD + St.SiF(2, _scale), Width, St.SiF(20, _scale)), sf);   // 与横向徽标同款 FNorm 9pt
                y += St.SiF(VRowH, _scale);
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
            if (_edgeDocked)
            {
                _edgeDocked = false;
                _edgeJustUndocked = true;
                if (_edgeSide != 0) ExitVerticalDock();   // 从左右侧竖向吸附拖出：恢复横向形态
                _ctx.Cfg.DockSide = -1;
                _ctx.Cfg.Vertical = _vertical;
            }
            Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
            // 贴边检测：以鼠标(屏幕坐标)相对光标所在屏工作区三边的距离为准（跨屏拖动各自独立判定）
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int th = St.SiF(EdgeThreshold, _scale);
            bool nearLeft, nearRight, nearTop;
            if (_vertical)
            {
                // 竖向形态只吸附左右（竖条贴顶是矛盾形态）
                nearLeft = Cursor.Position.X <= wa.Left + th;
                nearRight = Cursor.Position.X >= wa.Right - th;
                nearTop = false;
            }
            else
            {
                nearTop = Cursor.Position.Y <= wa.Top + th;
                nearLeft = !nearTop && Cursor.Position.X <= wa.Left + th;
                nearRight = !nearTop && !nearLeft && Cursor.Position.X >= wa.Right - th;
            }
            int side = nearTop ? 0 : nearLeft ? 1 : nearRight ? 2 : -1;
            // 从吸附态拖出后，鼠标须先离开所有边缘阈值一次，才能在新位置重新吸附
            if (_edgeJustUndocked)
            {
                if (side >= 0) return;
                _edgeJustUndocked = false;
            }
            if (side >= 0 && (!_edgeArmed || side != _edgeSide))
            {
                _edgeArmed = true;
                _edgeSide = side;
                _edgeTargetX = Math.Max(wa.Left, Math.Min(wa.Right - Width, Left));
                _edgeTargetY = Math.Max(wa.Top, Math.Min(wa.Bottom - Height, Top));
                ShowGhost(wa);
            }
            else if (side < 0 && _edgeArmed)
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
                _ctx.Cfg.X = Left; _ctx.Cfg.Y = Top; _ctx.Cfg.DockSide = -1; _ctx.Cfg.Vertical = _vertical; _ctx.Cfg.Save();
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
            if (_edgeDocked) { EdgeCollapse(); return; }  // 吸附态：滑回收起（左右竖向岛常驻，见 EdgeCollapse）
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
            // 左右吸附预览竖向形态（宽高互换）；顶边保持横向
            bool vert = _island && _edgeSide != 0;
            _ghost.Width = vert ? St.SiF(VIslandW, _scale) : ClientSize.Width;
            _ghost.Height = vert ? St.SiF(VRowH * _ctx.Providers.Count + 16, _scale) : ClientSize.Height;
            if (_edgeSide == 1) _ghost.Location = new Point(wa.Left, _edgeTargetY);
            else if (_edgeSide == 2) _ghost.Location = new Point(wa.Right - _ghost.Width, _edgeTargetY);
            else _ghost.Location = new Point(_edgeTargetX, wa.Top);
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
            _edgeTargetY = Math.Max(wa.Top, Math.Min(wa.Bottom - Height, _edgeTargetY));
            if (_island && _edgeSide != 0)
            {
                // 左右侧：切换为竖向形态并贴边，初始收缩（移入展开），保存展开位置供重启恢复
                _vertical = true;
                _edgeDocked = true;
                _edgeCollapsed = false;
                Rebuild();
                _ctx.Cfg.X = Left; _ctx.Cfg.Y = Top;
                _ctx.Cfg.DockSide = _edgeSide; _ctx.Cfg.Vertical = true; _ctx.Cfg.Save();
                EdgeCollapse();
                return;
            }
            _vertical = false;
            _ctx.Cfg.X = _edgeTargetX; _ctx.Cfg.Y = wa.Top; _ctx.Cfg.DockSide = 0; _ctx.Cfg.Vertical = false; _ctx.Cfg.Save();
            _edgeDocked = true;
            _edgeCollapsed = true;
            AnimateTo(new Point(_edgeTargetX, wa.Top - Height + PeekPx));
        }

        private void EdgeExpand()
        {
            if (!_edgeCollapsed) return;
            var wa = Screen.FromControl(this).WorkingArea;
            _edgeCollapsed = false;
            // 左右竖向吸附：向屏幕内侧滑入展开
            if (VerticalDocked)
            {
                AnimateTo(_edgeSide == 1 ? new Point(wa.Left, _edgeTargetY) : new Point(wa.Right - Width, _edgeTargetY));
                return;
            }
            AnimateTo(new Point(_edgeTargetX, wa.Top));
        }

        private void EdgeCollapse()
        {
            if (_edgeCollapsed) return;
            var wa = Screen.FromControl(this).WorkingArea;
            // 左右竖向吸附：向屏幕外侧滑出，只露 PeekPx 一条便于再次移入展开
            if (VerticalDocked)
            {
                _edgeCollapsed = true;
                AnimateTo(_edgeSide == 1 ? new Point(wa.Left - Width + PeekPx, _edgeTargetY) : new Point(wa.Right - PeekPx, _edgeTargetY));
                return;
            }
            _edgeCollapsed = true;
            AnimateTo(new Point(_edgeTargetX, wa.Top - Height + PeekPx));
        }

        // 退出左右竖向吸附：恢复横向岛形态（拖出/解锁/切换排列时调用）
        private void ExitVerticalDock()
        {
            _edgeSide = 0;
            _vertical = false;
            if (_island) Rebuild();
            // 竖向（窄）恢复横向（宽）后保持中心可能越出屏幕，钳制回工作区
            var wa = Screen.FromControl(this).WorkingArea;
            Location = new Point(Math.Max(wa.Left, Math.Min(wa.Right - Width, Left)),
                                 Math.Max(wa.Top, Math.Min(wa.Bottom - Height, Top)));
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
                if (VerticalIsland)
                {
                    // 竖向岛"固定位置"：保持竖向形态，钉在当前侧边的展开位置（不再随鼠标进出收缩）
                    Location = _edgeSide == 1 ? new Point(wa.Left, _edgeTargetY) : new Point(wa.Right - Width, _edgeTargetY);
                    _ctx.Cfg.X = Left; _ctx.Cfg.Y = Top;
                    _ctx.Cfg.DockSide = -1; _ctx.Cfg.Vertical = true; _ctx.Cfg.Save();
                    return;
                }
                Location = new Point(_edgeTargetX, wa.Top);
                _ctx.Cfg.X = _edgeTargetX; _ctx.Cfg.Y = wa.Top; _ctx.Cfg.DockSide = -1; _ctx.Cfg.Save();
            }
        }

        // 取消固定位置时调用：若窗口仍停在顶部，给一次吸附豁免，避免一拖就吸回去
        public void OnLockReleased()
        {
            // 竖向岛取消固定：恢复横向自由形态（拖动中可重新吸附任意边）
            if (VerticalIsland)
            {
                _edgeJustUndocked = true;   // 原地贴边时先豁免一次，离开边缘阈值后才能重新吸附
                ExitVerticalDock();
                return;
            }
            var wa = Screen.FromControl(this).WorkingArea;
            int th = St.SiF(EdgeThreshold, _scale);
            if (Top <= wa.Top + th) _edgeJustUndocked = true;
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
            // 左右侧竖向岛：面板弹向屏幕内侧，纵向跟随岛体
            if (VerticalIsland)
            {
                x = _edgeSide == 1 ? Right + 8 : Left - _detail.Width - 8;
                if (x + _detail.Width > wa.Right) x = wa.Right - _detail.Width - 8;
                if (x < wa.Left) x = wa.Left + 8;
                y = Math.Max(wa.Top + 8, Math.Min(wa.Bottom - _detail.Height - 8, Top));
            }
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

        // 测量文字的「墨迹」边界（实际像素覆盖范围，相对绘制原点的偏移含在 X/Y 中）。
        // MeasureString 返回的是行盒加内置边距的排版宽度，用它做徽标尺寸会偏宽、文字偏左；
        // 配合 CharacterRange 的区域边界可实现任意文字（pro / Moderato / 中文）的视觉精确居中
        private static RectangleF MeasureInk(Graphics g, string text, Font f)
        {
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                sf.SetMeasurableCharacterRanges(new[] { new CharacterRange(0, text.Length) });
                using (var r = g.MeasureCharacterRanges(text, f, new RectangleF(0, 0, 4096, 512), sf)[0])
                    return r.GetBounds(g);
            }
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
                // 标题行左侧品牌图标：浅色主题用彩色源、深色主题用白色变体，随主题自动切换；
                // 图标垂直对齐标题行文字中心；未定义图标的类型回退为原来的彩色圆点
                bool dark = St.Theme == 1;
                int isz = Si(16);
                if (BrandIcons.Get(p.Type, dark) != null)
                    BrandIcons.Draw(g, p.Type, dark, Si(16), (int)Math.Round(y + Si(2) + (fs[0].GetHeight(g) - isz) / 2f), isz);
                else
                    using (var b = new SolidBrush(St.AccentFor(p.Type)))
                        g.FillEllipse(b, Si(16), y + Si(6), Si(9), Si(9));
                using (var b = new SolidBrush(St.Text))
                    g.DrawString(p.DisplayName, fs[0], b, Si(36), y + Si(2));
                string right = string.IsNullOrEmpty(p.Level) ? "" : p.Level;
                if (!string.IsNullOrEmpty(right))
                {
                    // 徽标文字按「墨迹边界」精确居中：MeasureString 自带 GDI+ 内置边距且基线偏上，
                    // 直接按行盒排版文字会视觉偏左偏下（如 pro / Moderato）
                    var ink = MeasureInk(g, right, fs[2]);
                    if (ink.Width > 0 && ink.Height > 0)
                    {
                        int pad = Si(8), ph = Si(17);
                        int pw = (int)Math.Ceiling(ink.Width) + pad * 2;
                        var pill = new Rectangle(Width - Si(16) - pw, y + Si(2), pw, ph);
                        using (var b = new SolidBrush(St.AccentForFixed(p.Type)))
                        using (var path = St.RoundRect(pill, Si(8)))
                            g.FillPath(b, path);
                        using (var b = new SolidBrush(Color.White))
                        using (var sf = new StringFormat(StringFormat.GenericTypographic))
                            g.DrawString(right, fs[2], b,
                                pill.Left + pill.Width / 2f - ink.X - ink.Width / 2f,
                                pill.Top + pill.Height / 2f - ink.Y - ink.Height / 2f, sf);
                    }
                }
                y += Si(26);

                if (p.Error != null)
                {
                    string msg = p.Error;
                    if (msg.Length > 46) msg = msg.Substring(0, 46) + "…";
                    using (var b = new SolidBrush(St.Red))
                        g.DrawString("⚠ " + msg, fs[2], b, Si(36), y + Si(4));
                    y += Si(34);
                }
                else
                {
                    foreach (var pool in p.Pools)
                    {
                        if (pool.IsMoney)
                        {
                            using (var b = new SolidBrush(St.Text))
                                g.DrawString(pool.Label, fs[1], b, Si(36), y);
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
                            g.DrawString(pool.Label, fs[1], b, Si(36), y);
                        string pctTxt = remain + "%";
                        var psz = g.MeasureString(pctTxt, fs[1]);
                        using (var b = new SolidBrush(c))
                            g.DrawString(pctTxt, fs[1], b, Width - psz.Width - Si(18), y);
                        y += Si(19);
                        St.DrawBar(g, new Rectangle(Si(36), y, Width - Si(54), Si(6)), remain, c);
                        y += Si(11);
                        string nums = pool.PercentOnly
                            ? "剩余 " + St.FmtNum(pool.Remaining) + "%" + (string.IsNullOrEmpty(pool.Note) ? "" : "　" + pool.Note)
                            : "已用 " + St.FmtNum(pool.Used) + " / " + St.FmtNum(pool.Total) + "　剩余 " + St.FmtNum(pool.Remaining);
                        using (var b = new SolidBrush(St.Dim))
                            g.DrawString(nums, fs[2], b, Si(36), y);
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
            float gap = 12f * s;
            float totalW;
            var badges = St.BuildBadges(providers, g, s, out totalW, 0, 0, St.Theme == 1);   // 置顶栏：无环纯图标

            // 居中起始 X
            float x = (Width - totalW) / 2;
            if (x < 8) x = 8;

            var fs = St.FontsForScale(s);
            float textH = g.MeasureString("Ay", fs[1]).Height;
            float textY = (Height - textH) / 2;

            for (int i = 0; i < badges.Count; i++)
            {
                x += St.DrawBadge(g, badges[i], x, Height / 2f, textY, s, fs[1], 0, 0, St.Theme == 1) + gap;
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
            "Kimi Code Plan api.kimi.com", "DeepSeek api.deepseek.com",
            "Claude Pro/Max api.anthropic.com", "Codex (ChatGPT 套餐) chatgpt.com"
        };
        // 下拉项对应的品牌图标类型（与 TypeItems 一一对应，供 BrandIcons 染色绘制）
        private static readonly string[] TypeIconTypes = new string[] { "glm", "glm", "mx", "mx", "kimi", "ds", "claude", "codex" };

        private readonly TextBox _txtKey, _txtName;
        private readonly Rectangle _rcSelect, _rcKeyBox;
        private Rectangle _rcSeg, _rcNameBox, _rcOk, _rcCancel;
        private int _typeIndex;
        private bool _showName;                    // 显示方式：false=图标(默认，悬浮窗展示图标+环形进度) true=显示名称
        private bool _hotOk, _hotCancel, _hotSelect, _hotSeg;
        private SettingsPopupList _popup;
        public ProviderConfig Result { get; private set; }

        public ProviderEditDialog(ProviderConfig existing, SettingsPal pal, float scale)
            : base(existing != null ? "编辑供应商" : "添加供应商", pal, scale, new Size(St.SiF(400, scale), St.SiF(310, scale)))
        {
            int x = S(20), w = ClientSize.Width - S(40);

            // 前两组「标签 + 输入区」固定；显示方式分段控件与名称组（可选）在 ApplyLayout 里排布
            _rcSelect = new Rectangle(x, S(46) + S(38), w, S(34));
            _rcKeyBox = new Rectangle(x, S(46) + S(100), w, S(34));

            _txtKey = new TextBox { BorderStyle = BorderStyle.None, BackColor = pal.Win, ForeColor = pal.Text, Font = Fs[1], PasswordChar = '●' };
            _txtName = new TextBox { BorderStyle = BorderStyle.None, BackColor = pal.Win, ForeColor = pal.Text, Font = Fs[1] };
            Controls.Add(_txtKey);
            Controls.Add(_txtName);
            // 单行 TextBox 高度跟随字体，垂直居中在输入框内；边框由 OnPaint 画
            _txtKey.Bounds = new Rectangle(_rcKeyBox.X + S(10), _rcKeyBox.Y + (S(34) - _txtKey.PreferredHeight) / 2, _rcKeyBox.Width - S(20), _txtKey.PreferredHeight);
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
                _typeIndex = existing.Type == "codex" ? 7 : existing.Type == "claude" ? 6 :
                    (existing.Type == "ds" ? 5 : (existing.Type == "kimi" ? 4 : (existing.Type == "mx" ? (existing.Region == "intl" ? 3 : 2) : (existing.Region == "intl" ? 1 : 0))));
                _showName = existing.ShowName;
                // 预填解密后的真实 key（输入框本身以 ● 掩码显示）；密文解不开时留空待重填
                _txtKey.Text = existing.GetKey() ?? "";
                _txtName.Text = existing.Name;
            }
            ApplyLayout();
        }

        // 显示方式/图标样式切换后重排：图标模式在分段控件下追加黑白/彩色次级选择（加高 S(40)），
        // 名称模式追加名称输入组（加高 S(62)）
        private void ApplyLayout()
        {
            int x = S(20), w = S(400) - S(40);
            ClientSize = new Size(S(400), S(310) + (_showName ? S(62) : 0));
            _rcSeg = new Rectangle(x, S(46) + S(162), w, S(34));
            _rcNameBox = _showName ? new Rectangle(x, S(46) + S(224), w, S(34)) : Rectangle.Empty;
            _rcOk = new Rectangle(S(400) - S(20) - S(88), ClientSize.Height - S(18) - S(34), S(88), S(34));
            _rcCancel = new Rectangle(_rcOk.X - S(10) - S(88), _rcOk.Y, S(88), S(34));
            _txtName.Visible = _showName;
            _txtName.Bounds = new Rectangle(_rcNameBox.X + S(10), _rcNameBox.Y + (S(34) - _txtName.PreferredHeight) / 2, _rcNameBox.Width - S(20), _txtName.PreferredHeight);
            Invalidate();
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
                    _txtName.Text = ProviderConfig.TypeDefaultNames[idx];
                Invalidate();
            }, TypeIconTypes);
            _popup.Show(this);
        }

        private static bool IsDefaultName(string n)
        {
            foreach (string d in ProviderConfig.TypeDefaultNames)
                if (d == n) return true;
            return false;
        }

        private void OnOk()
        {
            // claude/codex 的 Key 可留空：自动读取本机 CLI 登录凭证（.claude/.credentials.json、.codex/auth.json）
            bool localCreds = TypeIconTypes[_typeIndex] == "claude" || TypeIconTypes[_typeIndex] == "codex";
            if (string.IsNullOrWhiteSpace(_txtKey.Text) && !localCreds)
            {
                StyledMsg.Show(this, "提示", "请填写 API Key（Claude / Codex 可留空自动读取本机登录凭证）", Pal, Sf);
                return;
            }
            int i = _typeIndex;
            string type = i == 7 ? "codex" : i == 6 ? "claude" : (i == 5 ? "ds" : (i == 4 ? "kimi" : ((i == 2 || i == 3) ? "mx" : "glm")));
            string region = (i == 1 || i == 3) ? "intl" : "cn";
            // 图标模式不展示自定义名，名称固定回默认名；名称模式取输入（空则回默认）
            string name = _showName && !string.IsNullOrWhiteSpace(_txtName.Text) ? _txtName.Text.Trim() : ProviderConfig.TypeDefaultNames[i];
            var r = new ProviderConfig { Type = type, Region = region, Name = name, ShowName = _showName };
            r.SetKey(_txtKey.Text.Trim());   // 明文仅存在于输入框与本次加密调用，配置文件里只落 DPAPI 密文
            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            DrawFieldLabel(g, "接口", S(46) + S(10));
            PaintSelectBox(g, _rcSelect, TypeItems[_typeIndex], _hotSelect, TypeIconTypes[_typeIndex]);
            DrawFieldLabel(g, "API Key", S(46) + S(72));
            DrawFieldBox(g, _rcKeyBox, _txtKey.Focused);
            DrawFieldLabel(g, "显示方式", S(46) + S(134));
            PaintSegmented(g, _rcSeg);
            if (_showName)
            {
                DrawFieldLabel(g, "显示名称", S(46) + S(196));
                DrawFieldBox(g, _rcNameBox, _txtName.Focused);
            }

            PaintBtn(g, _rcOk, "确认", true, _hotOk);
            PaintBtn(g, _rcCancel, "取消", false, _hotCancel);
        }

        // 显示方式分段控件：左=显示图标（默认） 右=显示名称，选中半 Sel 底 + 高亮字
        private void PaintSegmented(Graphics g, Rectangle r)
        {
            int half = r.Width / 2;
            var rl = new Rectangle(r.X, r.Y, half, r.Height);
            var rr = new Rectangle(r.X + half, r.Y, r.Width - half, r.Height);
            using (var path = St.RoundRect(_showName ? rr : rl, S(8)))
            using (var b = new SolidBrush(Pal.Sel))
                g.FillPath(b, path);
            using (var path = St.RoundRect(r, S(8)))
            using (var pen = new Pen(_hotSeg ? Pal.CardHover : Pal.Line))
                g.DrawPath(pen, path);
            TextRenderer.DrawText(g, "显示图标", Fs[1], rl, !_showName ? Pal.Text : Pal.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, "显示名称", Fs[1], rr, _showName ? Pal.Text : Pal.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
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

        private void PaintSelectBox(Graphics g, Rectangle r, string text, bool hot, string iconType)
        {
            using (var path = St.RoundRect(r, S(8)))
            using (var pen = new Pen(hot ? Pal.CardHover : Pal.Line))
                g.DrawPath(pen, path);
            // 品牌图标：主题色染色绘制在文本前（图标不可用时文本仍从原位开始）
            int textX = S(12);
            var icon = BrandIcons.Get(iconType, Pal.Dark);
            if (icon != null)
            {
                int sz = S(18);
                BrandIcons.Draw(g, iconType, Pal.Dark, r.X + S(10), r.Y + (r.Height - sz) / 2, sz);
                textX = S(10) + sz + S(8);
            }
            int maxW = r.Width - textX - S(34);
            string s = text;
            while (s.Length > 1 && TextRenderer.MeasureText(s + "…", Fs[1]).Width > maxW) s = s.Substring(0, s.Length - 1);
            if (s != text) s += "…";
            TextRenderer.DrawText(g, s, Fs[1], new Rectangle(r.X + textX, r.Y, maxW + S(20), r.Height), Pal.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
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
            if (_rcSeg.Contains(e.Location))
            {
                bool toName = e.X >= _rcSeg.X + _rcSeg.Width / 2;
                if (toName != _showName)
                {
                    _showName = toName;
                    // 切到名称模式时名称为空则预填当前接口默认名
                    if (_showName && string.IsNullOrWhiteSpace(_txtName.Text))
                        _txtName.Text = ProviderConfig.TypeDefaultNames[_typeIndex];
                    ActiveControl = null;
                    ApplyLayout();
                }
                return;
            }
            if (_rcOk.Contains(e.Location)) { OnOk(); return; }
            if (_rcCancel.Contains(e.Location)) { Close(); return; }
            // 点击输入框以外区域让文本框失焦，边框恢复常态
            if (!_rcKeyBox.Contains(e.Location) && !_rcNameBox.Contains(e.Location))
                ActiveControl = null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hs = _rcSelect.Contains(e.Location), ho = _rcOk.Contains(e.Location), hc = _rcCancel.Contains(e.Location), hg = _rcSeg.Contains(e.Location);
            if (hs != _hotSelect || ho != _hotOk || hc != _hotCancel || hg != _hotSeg)
            {
                _hotSelect = hs; _hotOk = ho; _hotCancel = hc; _hotSeg = hg;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotSelect || _hotOk || _hotCancel || _hotSeg)
            {
                _hotSelect = _hotOk = _hotCancel = _hotSeg = false;
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
        public bool Dark;   // 本调色板是否深色（品牌图标等主题资源跟随此值而非全局 St.Theme，保证设置页预览即时切换）

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
            SettingsPal p = new SettingsPal { Dark = dark };
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
        private readonly string[] _iconTypes; // 可选：每项前置品牌图标类型名（BrandIcons 染色绘制，null 跳过）
        private readonly SettingsPal _pal;
        private readonly bool _dark;          // 主题深浅（随调用方调色板，设置页预览切换即时生效）
        private readonly Font _fReg, _fBold;
        private readonly float _scale;
        private readonly Action<int> _onPick;
        private readonly int _sel;
        private int _hover = -1;
        private readonly int _itemH, _padV;

        public SettingsPopupList(Rectangle anchorScreen, string[] items, int selected,
            SettingsPal pal, Font fReg, Font fBold, float scale, Action<int> onPick, string[] iconTypes = null)
        {
            _items = items;
            _iconTypes = iconTypes;
            _dark = pal.Dark;
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

            // 宽度取最长项文本 + 左右留白，但不小于触发框宽（视觉对齐）；带图标时文本右移让出图标位
            bool hasIcons = iconTypes != null && Array.Exists(iconTypes, t => BrandIcons.Get(t, _dark) != null);
            int iconW = hasIcons ? St.SiF(26, scale) : 0;
            int maxW = anchorScreen.Width;
            foreach (string it in items)
                maxW = Math.Max(maxW, TextRenderer.MeasureText(it, fReg).Width + St.SiF(46, scale) + iconW);
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
                float tx = r.X + St.SiF(10, _scale);
                // 前置品牌图标（与选择框一致的主题色染色），文本右移让位
                if (_iconTypes != null && i < _iconTypes.Length && BrandIcons.Get(_iconTypes[i], _dark) != null)
                {
                    int sz = St.SiF(18, _scale);
                    BrandIcons.Draw(g, _iconTypes[i], _dark, r.X + St.SiF(10, _scale), r.Y + (_itemH - sz) / 2, sz);
                    tx = r.X + St.SiF(10, _scale) + sz + St.SiF(8, _scale);
                }
                using (var b = new SolidBrush(_pal.Text))
                    g.DrawString(_items[i], i == _sel ? _fBold : _fReg, b, new RectangleF(tx, r.Y, r.Right - tx, r.Height), WidgetMenu.SFLeft);
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

            // 品牌图标（悬浮窗同款：黑白随主题/彩色原样），无图标时回退标识色圆点
            if (BrandIcons.Get(prov.Type, p.Dark) != null)
                BrandIcons.Draw(g, prov.Type, p.Dark, card.X + S(30), card.Y + cardH / 2 - S(9), S(18));
            else
                using (var b = new SolidBrush(St.AccentFor(prov.Type)))
                    g.FillEllipse(b, card.X + S(34), card.Y + cardH / 2 - S(4), S(8), S(8));

            // 名称（超长省略）；图标模式显示接口类型名（与悬浮窗/托盘一致）
            string name = Elide(g, prov.DisplayName, fs[1], S(110));
            float nameW = g.MeasureString(name, fs[1]).Width;
            using (var b = new SolidBrush(p.Text))
                g.DrawString(name, fs[1], b, new RectangleF(card.X + S(56), card.Y, nameW + S(16), cardH), SFLeftV);

            // 接口类型徽标
            string badge = prov.TypeName;
            int bw = MeasureW(badge, fs[5]) + S(16);
            var br = new Rectangle(card.X + S(56) + (int)nameW + S(10), card.Y + cardH / 2 - S(9), bw, S(18));
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
                case R_LAYOUT: return "条形灵动岛为内容自适应宽的圆角胶囊，贴顶自动隐藏、可拖拽定位，拖离顶边变为普通悬浮条；矩形卡片为紧凑单行卡片视图。";
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
                g.DrawString("在桌面悬浮展示 GLM / Kimi / MiniMax / DeepSeek / Claude / Codex 的额度与用量。", fs[3], b, new RectangleF(_vp.X, iconY + S(128), _vp.Width, S(18)), sf);
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
                g.DrawString("更改在点击「保存」后生效", fs[4], b, new RectangleF(sideW + S(30), top, W - sideW - S(30), footH), SFLeftV);
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
                    if (!pr.HasKey && !pr.CanUseLocalCreds) continue;   // 未配置 key 且无本地凭证：跳过（密文解不开的由 RequireKey 报可读错误）
                    try
                    {
                        ProviderStatus st;
                        if (pr.Type == "mx") st = QuotaService.FetchMiniMax(pr);
                        else if (pr.Type == "kimi") st = QuotaService.FetchKimi(pr);
                        else if (pr.Type == "ds") st = QuotaService.FetchDeepSeek(pr);
                        else if (pr.Type == "claude") st = QuotaService.FetchClaude(pr, true);   // 主动测试绕过节流
                        else if (pr.Type == "codex") st = QuotaService.FetchCodex(pr);
                        else st = QuotaService.FetchGlm(pr);
                        sb.AppendLine("【" + pr.DisplayName + "】连接成功 ✓  " + st.Pools.Count + " 个额度池");
                        foreach (var q in st.Pools)
                            sb.AppendLine("   · " + q.Label + "：" + (q.IsMoney ? q.Currency + St.FmtMoney(q.Remaining) : "已用 " + q.Percent + "%"));
                    }
                    catch (Exception ex) { sb.AppendLine("【" + pr.DisplayName + "】失败 ✗  " + ex.Message); }
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
            if (!createdNew)
            {
                // 已有实例（含卡死残留的僵尸进程、其他用户会话）时直接 return 会让用户以为
                // "双击没反应"；弹窗说明情况。用系统 MessageBox：此时 DPI/缩放初始化尚未执行，
                // 自绘 StyledMsg 不可用，且崩溃路径要求零依赖的最大健壮性
                MessageBox.Show("QuotaWidget 已在运行。\n\n请检查任务栏托盘区（Win11 可能收纳在 ^ 溢出区），\n或任务管理器中是否有残留的 QuotaWidget.exe 进程。",
                    "AI 额度工具", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

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
            // UI 线程事件中的未处理异常：记录并弹窗，替代默认的 ThreadExceptionDialog
            //（默认对话框只有堆栈无日志，用户关掉就丢根因）
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                CrashLog.Write("UI 线程异常", e.Exception);
                MessageBox.Show("发生错误，已记录到 error.log：\n\n" + e.Exception.Message,
                    "AI 额度工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            // UI 线程之外的未处理异常（线程池/自建线程，进程即将终止）：尽力落盘；
            // 终止中的进程弹窗可能来不及展示，日志优先
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                CrashLog.Write("未处理异常（进程终止）", e.ExceptionObject as Exception
                    ?? new Exception(Convert.ToString(e.ExceptionObject)));
            };
            try
            {
                Application.Run(new AppContext());
            }
            catch (Exception ex)
            {
                // AppContext 构造（悬浮窗/托盘创建等）抛异常发生在消息循环开始之前，
                // WinForms 默认 ThreadException 对话框不生效，此前会静默崩溃只闪一下系统
                // WER 对话框；落盘 + 弹窗把"双击没反应"变成可读的错误
                CrashLog.Write("启动失败", ex);
                MessageBox.Show("启动失败，已记录到 error.log：\n\n" + ex.Message,
                    "AI 额度工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            GC.KeepAlive(mutex); // 确保 Mutex 在应用程序整个生命周期内存活
        }
    }
}
