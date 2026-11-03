// QuotaWidget - AI 额度工具 (GLM Coding Plan + 钱包余额 / MiniMax Token Plan / Kimi Code Plan / DeepSeek 余额 / 小米 MiMo)
// 编译: csc.exe /nologo /utf8output /target:winexe /optimize+ /win32icon:QuotaWidget.ico /out:QuotaWidget.exe QuotaWidget.cs /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        public const string Version = "1.2.0";

        // 更新检查双源：Gitee 主源（国内可达性最好、海外也能访问），GitHub 备源，两边同 tag 发版。
        // Gitee API v5 仿 GitHub API 设计，核心字段（tag_name 等）两边一致，解析代码一套通吃
        public const string GiteeApi = "https://gitee.com/api/v5/repos/dashingmonkey/quota-widget/releases/latest";
        public const string GiteePage = "https://gitee.com/dashingmonkey/quota-widget/releases/latest";
        public const string GitHubApi = "https://api.github.com/repos/DashingMonkey/quota-widget/releases/latest";
        public const string GitHubPage = "https://github.com/DashingMonkey/quota-widget/releases/latest";
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

        // 日志与 config.json 同放 %LOCALAPPDATA%\QuotaWidget\（见 Config.DataDir）：
        // 不再写 exe 同目录，避免随仓库/便携目录被误删或在多个 exe 副本旁各留一份
        private static string ResolvePath()
        {
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                return Path.Combine(Config.DataDir, "error.log");
            }
            catch { return null; }
        }
    }

    // ============================= 数据模型 =============================
    internal class ProviderConfig
    {
        public string Type { get; set; }     // "glm" / "mx" / "kimi" / "ds" / "claude" / "codex" / "mimo" / "qwen" / "bailian"
        public string Region { get; set; }   // "cn" / "intl"
        // 旧明文字段：仅用于读取旧 config.json 完成迁移，序列化时忽略（迁移后恒为空）
        [ScriptIgnore]
        public string Key { get; set; }
        // DPAPI(CurrentUser) 加密的 Base64 密文：绑定本机当前 Windows 用户，
        // config.json 被其他软件扫描/拷走到别的电脑均无法还原
        public string KeyEnc { get; set; }
        public string Name { get; set; }     // 显示名称
        // 悬浮窗展示方式：false(默认)=图标模式（品牌图标+外圈环形进度+百分比）；
        // true=名称模式（显示自定义名称）。旧配置无此字段，加载时按是否自定义过名称推断
        public bool ShowName { get; set; }
        public bool Enabled { get; set; } // 关闭后保留配置，跳过查询与展示；旧配置默认启用。
        public int RefreshSec { get; set; } // 0=跟随通用设置；其他值为供应商独立间隔。
        // 仅 GLM 使用：查询内容 "quota"(默认，Coding Plan 套餐额度) / "wallet"(标准 API 钱包余额)。
        // 官方已将套餐 Key 与标准 API Key 分开管理，同一个 Key 不再同时属于两种产品
        public string GlmMode { get; set; }
        // 仅 GLM 钱包模式使用：币种 "CNY"/"USD"。账户余额不随接口域名换算，
        // 国内账户用人民币、国际账户用美元，需与账户实际币种一致
        public string GlmCurrency { get; set; }
        // 仅 GLM Coding Plan 使用：团队版查询需要 ?type=2 与组织/项目上下文头；
        // 个人版保持 "personal"（默认），不带这两个头，也不带 type 参数
        public string GlmPlanType { get; set; }
        public string GlmOrganization { get; set; }
        public string GlmProject { get; set; }
        // 仅小米 MiMo 使用：查询内容 "plan"(默认，Token Plan 套餐额度) / "balance"(账号余额)。
        // 官方只有网页控制台接口，凭证是登录 Cookie（仍加密保存在 KeyEnc 里），不是 API Key
        public string MimoMode { get; set; }
        // 控制台 Cookie 型供应商（MiMo / 千问 / 百炼）共用：拿到 Cookie 的本机时间（ISO 8601）。
        // 只用于显示「登录于 …」，不据此作废——是否还有效只由接口返回决定
        public string MimoLoginAt { get; set; }
        public CustomApiConfig Custom { get; set; }
        internal Guid RuntimeId = Guid.NewGuid(); // 编辑、排序后仍能找到同一接口的查询状态，不写入配置。
        internal DateTime NextRefreshUtc;
        internal DateTime CooldownUntilUtc;
        internal DateTime LastFetchUtc;
        internal List<ProviderStatus> LastResults;

        public int EffectiveRefreshSec(int globalSeconds) { return Math.Max(15, Math.Min(86400, RefreshSec > 0 ? RefreshSec : globalSeconds)); }
        public bool IsRefreshDue(DateTime now) { return now >= NextRefreshUtc; }
        // 旧配置没有该字段，按默认值"套餐额度"处理；未知值（含旧版临时写入的 "both"）也回退到套餐
        [ScriptIgnore]
        public string EffectiveGlmMode { get { return GlmMode == "wallet" ? "wallet" : "quota"; } }
        // 钱包币种：未配置时默认人民币（旧配置），美元需显式选择
        [ScriptIgnore]
        public string EffectiveGlmCurrency { get { return GlmCurrency == "USD" ? "USD" : "CNY"; } }
        // 套餐类型：旧配置未记录时按个人版处理
        [ScriptIgnore]
        public string EffectiveGlmPlanType { get { return GlmPlanType == "team" ? "team" : "personal"; } }
        // MiMo 查询内容：旧配置未记录时按 Token Plan 套餐处理
        [ScriptIgnore]
        public string EffectiveMimoMode { get { return MimoMode == "balance" ? "balance" : "plan"; } }
        [ScriptIgnore]
        public bool HasGlmTeamContext { get { return !string.IsNullOrWhiteSpace(GlmOrganization) && !string.IsNullOrWhiteSpace(GlmProject); } }
        [ScriptIgnore]
        public string IconType { get { return Type == "custom" ? (Custom == null || string.IsNullOrEmpty(Custom.IconType) ? "custom" : Custom.IconType) : Type; } }

        // 各接口默认显示名（与设置对话框 TypeItems 顺序一致，末尾"自定义接口"不在下拉里）；编辑对话框与旧配置迁移共用
        internal static readonly string[] TypeDefaultNames = { "GLM", "GLM", "MiniMax", "MiniMax", "Kimi", "DeepSeek", "Claude", "Codex", "MiMo", "千问", "百炼", "自定义接口" };

        /// type+region → 接口下拉项索引（编辑对话框预填/保存与旧配置迁移共用）
        internal static int TypeIndex(string type, string region)
        {
            if (type == "custom") return 11;
            if (type == "bailian") return 10;
            if (type == "qwen") return 9;
            if (type == "mimo") return 8;
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
            if (type == "mimo") return "小米 MiMo";
            if (type == "qwen") return "千问 Token Plan";
            if (type == "bailian") return "百炼 Token Plan";
            if (type == "custom") return "自定义接口";
            return type;
        }

        /// 悬浮窗/托盘等处的展示文本：图标模式用接口类型名（自定义名仅在名称模式下展示）
        [ScriptIgnore]
        public string DisplayName { get { return ShowName || Type == "custom" ? Name : TypeDisplay(Type, Region); } }

        // 可选熵：恒定值（改动会让历史密文全部失效），提高离线盲解门槛
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuotaWidget.ApiKey.v1");
        // 解密结果缓存（仅进程内存，不序列化）：设置页每次重绘都要画掩码，避免反复走 DPAPI
        private string _plainCache;
        private bool _plainLoaded;

        public ProviderConfig() { Type = "glm"; Region = "cn"; Key = ""; KeyEnc = ""; Name = "GLM"; Enabled = true; GlmMode = "quota"; GlmPlanType = "personal"; MimoMode = "plan"; }

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

        public ProviderConfig Clone() { return new ProviderConfig { RuntimeId = RuntimeId, Type = Type, Region = Region, Key = Key, KeyEnc = KeyEnc, Name = Name, ShowName = ShowName, Enabled = Enabled, RefreshSec = RefreshSec, GlmMode = GlmMode, GlmCurrency = GlmCurrency, GlmPlanType = GlmPlanType, GlmOrganization = GlmOrganization, GlmProject = GlmProject, MimoMode = MimoMode, MimoLoginAt = MimoLoginAt, Custom = Custom == null ? null : Custom.Clone() }; }

        internal bool HasSameQuery(ProviderConfig other)
        {
            if (other == null || Type != other.Type || Region != other.Region) return false;
            if ((KeyEnc != other.KeyEnc || Key != other.Key) && GetKey() != other.GetKey()) return false;
            // 查询内容、钱包币种、团队上下文变化后不能复用旧额度
            if (Type == "glm")
            {
                if (EffectiveGlmMode != other.EffectiveGlmMode) return false;
                if (EffectiveGlmMode == "wallet" && EffectiveGlmCurrency != other.EffectiveGlmCurrency) return false;
                if (EffectiveGlmMode == "quota")
                {
                    if (EffectiveGlmPlanType != other.EffectiveGlmPlanType) return false;
                    if (EffectiveGlmPlanType == "team" &&
                        (GlmOrganization != other.GlmOrganization || GlmProject != other.GlmProject)) return false;
                }
            }
            // MiMo 套餐与余额是两个接口，切换后不能套用旧结果
            if (Type == "mimo" && EffectiveMimoMode != other.EffectiveMimoMode) return false;
            if (Type != "custom") return true;
            if (Custom == null || other.Custom == null) return Custom == other.Custom;
            if (Custom.HttpMethod != other.Custom.HttpMethod || (Custom.HttpMethod == "POST" && Custom.GetBody() != other.Custom.GetBody())) return false;
            // 名称和图标只影响展示；请求与取值规则变化后不能继续套用旧额度。
            var a = Custom.Clone(); var b = other.Custom.Clone();
            a.IconType = b.IconType = a.DisplayName = b.DisplayName = a.BodyEnc = b.BodyEnc = "";
            a.Method = b.Method = Custom.HttpMethod;
            var ser = new JavaScriptSerializer();
            return ser.Serialize(a) == ser.Serialize(b);
        }

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

        // 首屏、自动刷新和连接测试统一使用此条件，禁用时不读取本机登录凭证。
        [ScriptIgnore]
        public bool CanQuery { get { return Enabled && (Type == "custom" ? Custom != null && !string.IsNullOrWhiteSpace(Custom.Url) : HasKey || CanUseLocalCreds); } }
    }

    internal class CustomQuotaRule
    {
        public string Name { get; set; }
        public string ValuePath { get; set; }
        public string TotalPath { get; set; }
        public string ResetPath { get; set; }
        public string Mode { get; set; }
        public bool IsRemaining { get; set; }
        public bool Fraction { get; set; }
        public double Multiplier { get; set; }
        public string Currency { get; set; }
        public CustomQuotaRule()
        { Name = ValuePath = TotalPath = ResetPath = ""; Mode = "percent"; IsRemaining = true; Multiplier = 1; Currency = "$"; }
        public CustomQuotaRule Clone() { return (CustomQuotaRule)MemberwiseClone(); }
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) throw new Exception("请先填写额度名称");
            if (Mode != "percent" && Mode != "balance" && Mode != "usedTotal") throw new Exception("请选择额度类型");
            if (string.IsNullOrWhiteSpace(ValuePath)) throw new Exception("请拖入或填写数值路径");
            if (Mode == "usedTotal" && string.IsNullOrWhiteSpace(TotalPath)) throw new Exception("请拖入或填写总量路径");
            if (Mode == "balance" && (double.IsNaN(Multiplier) || double.IsInfinity(Multiplier) || Multiplier <= 0)) throw new Exception("倍率必须大于 0");
            JsonFieldPath.Tokens(ValuePath); JsonFieldPath.Tokens(ResetPath);
            if (Mode == "usedTotal") JsonFieldPath.Tokens(TotalPath);
        }
    }

    // 自定义接口采用字段路径映射，不执行用户代码。请求头整体沿用 ProviderConfig.KeyEnc 加密保存。
    internal class CustomApiConfig
    {
        public string Url { get; set; }
        public string Method { get; set; }
        public string BodyEnc { get; set; }
        [ScriptIgnore]
        public string HttpMethod { get { return string.IsNullOrWhiteSpace(Method) ? "GET" : Method.Trim().ToUpperInvariant(); } }
        public string GetBody() { return new ProviderConfig { KeyEnc = BodyEnc }.GetKey(); }
        public void SetBody(string body)
        {
            var encrypted = new ProviderConfig(); encrypted.SetKey(body); BodyEnc = encrypted.KeyEnc;
        }
        public string IconType { get; set; }
        public string DisplayName { get; set; } // 留空时跟随接口名称；填写后独立保存。
        public List<CustomQuotaRule> Rules { get; set; } // 每套规则使用相对响应根的固定路径。

        public CustomApiConfig()
        {
            Url = ""; Method = "GET"; BodyEnc = ""; IconType = "custom";
            Rules = new List<CustomQuotaRule>();
        }
        public CustomApiConfig Clone()
        {
            var copy = (CustomApiConfig)MemberwiseClone();
            if (Rules != null) copy.Rules = Rules.ConvertAll(rule => rule == null ? null : rule.Clone());
            return copy;
        }
        public void ValidateRequest()
        {
            if (HttpMethod != "GET" && HttpMethod != "POST") throw new Exception("请求方式请选择 GET 或 POST");
            Uri uri;
            if (!Uri.TryCreate(Url, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new Exception("请输入完整的 HTTP / HTTPS 数据接口地址");
        }
        public void Validate()
        {
            ValidateRequest();
            if (Rules != null)
            {
                if (Rules.Count == 0 || Rules.Count > 100) throw new Exception("请保留 1 到 100 套额度规则");
                for (int i = 0; i < Rules.Count; i++)
                {
                    if (Rules[i] == null) throw new Exception("额度规则不能为空");
                    try { Rules[i].Validate(); }
                    catch (Exception ex) { throw new Exception("第 " + (i + 1) + " 套规则：" + ex.Message); }
                }
                return;
            }
            throw new Exception("请添加额度规则");
        }
    }

    internal static class JsonFieldPath
    {
        public static string Append(string parent, string key)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(key, @"^[\p{L}_$][\p{L}\p{N}_$]*$")) return parent + "." + key;
            return parent + "[" + new JavaScriptSerializer().Serialize(key) + "]";
        }
        private static string DecodeKey(string key)
        {
            var result = new StringBuilder();
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c != '\\') { result.Append(c); continue; }
                if (++i >= key.Length) throw new Exception("字段名称的转义不完整");
                c = key[i];
                if (c == '\\' || c == '\'' || c == '"' || c == '/') result.Append(c);
                else if (c == 'n') result.Append('\n');
                else if (c == 'r') result.Append('\r');
                else if (c == 't') result.Append('\t');
                else if (c == 'b') result.Append('\b');
                else if (c == 'f') result.Append('\f');
                else if (c == 'u')
                {
                    ushort code;
                    if (i + 4 >= key.Length || !ushort.TryParse(key.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        throw new Exception("字段名称的 Unicode 转义不完整");
                    result.Append((char)code); i += 4;
                }
                else throw new Exception("字段名称包含无效转义");
            }
            return result.ToString();
        }
        // 兼容 result.data.value、data.items[0].value、result["a-b"]；不求值 JS 表达式。
        public static List<object> Tokens(string path)
        {
            string s = (path ?? "").Trim();
            if (s == "result" || s == "$") s = "";
            else if (s.StartsWith("result.") || s.StartsWith("result[")) s = s.Substring(6);
            else if (s.StartsWith("$.") || s.StartsWith("$[")) s = s.Substring(1);
            if (s.StartsWith(".")) s = s.Substring(1);
            var tokens = new List<object>();
            int pos = 0;
            while (pos < s.Length)
            {
                var match = System.Text.RegularExpressions.Regex.Match(s.Substring(pos),
                    pos == 0 ? @"^(?:([\p{L}_$][\p{L}\p{N}_$]*)|\[(\d+)\]|\['((?:[^'\\\r\n]|\\.)*)'\]|\[""((?:[^""\\\r\n]|\\.)*)""\])"
                             : @"^(?:\.([\p{L}_$][\p{L}\p{N}_$]*)|\[(\d+)\]|\['((?:[^'\\\r\n]|\\.)*)'\]|\[""((?:[^""\\\r\n]|\\.)*)""\])");
                if (!match.Success) throw new Exception("字段路径格式不正确：" + path + "（例如 result.data.value 或 items[0].value）");
                int index;
                if (match.Groups[2].Success)
                {
                    if (!int.TryParse(match.Groups[2].Value, out index)) throw new Exception("数组下标过大");
                    tokens.Add(index);
                }
                else tokens.Add(match.Groups[1].Success ? match.Groups[1].Value : DecodeKey(match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value));
                pos += match.Length;
            }
            return tokens;
        }
        public static object Get(object root, string path)
        {
            object value;
            return TryGet(root, path, out value) ? value : null;
        }
        public static bool TryGet(object root, string path, out object value)
        {
            value = root;
            foreach (object token in Tokens(path))
            {
                if (token is int)
                {
                    var list = value as IList;
                    int n = (int)token;
                    if (list == null || n >= list.Count) { value = null; return false; }
                    value = list[n];
                }
                else
                {
                    var dict = value as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue((string)token, out value)) { value = null; return false; }
                }
            }
            return true;
        }
        public static object Optional(object root, string path) { return string.IsNullOrWhiteSpace(path) ? null : Get(root, path); }
        public static string Text(object root, string path)
        {
            object value = Optional(root, path);
            return value is string ? (string)value : value == null || value is IDictionary || value is IList ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        public static double Number(object value, string path)
        {
            double number;
            if (value == null || value is bool || !double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                || double.IsNaN(number) || double.IsInfinity(number)) throw new Exception("字段 " + path + " 缺失或不是有效数字");
            return number;
        }
    }

    internal class CustomFetchResult
    {
        public List<ProviderStatus> Statuses;
        public string PreviewJson;
        public bool Cached;
        public int WaitSeconds;
        internal DateTime FetchedUtc;
        internal string Json; // 原响应只保留在进程内，供本地规则预览使用。
        internal object PreviewRoot; // 树与字段旁的值只显示脱敏后的数据。
    }

    internal static class CustomApiService
    {
        private sealed class Entry { public string Json, CooldownError; public DateTime FetchedUtc, NextUtc; }
        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>();
        private const int MaxBody = 2 * 1024 * 1024;

        public static Dictionary<string, string> ParseHeaders(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if ((text ?? "").Length > 16384) throw new Exception("请求头过长");
            foreach (string line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) throw new Exception("请求头请每行填写 名称: 值");
                string name = line.Substring(0, colon).Trim(), value = line.Substring(colon + 1).Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$") || value.IndexOf('\r') >= 0 || value.IndexOf('\0') >= 0)
                    throw new Exception("请求头包含无效字符");
                if (result.ContainsKey(name)) throw new Exception("请求头重复：" + name);
                if (WebHeaderCollection.IsRestricted(name) && !name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && !name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("此请求头由程序自动管理：" + name);
                result.Add(name, value);
            }
            return result;
        }

        private static object Deserialize(string json)
        {
            try { return new JavaScriptSerializer { MaxJsonLength = MaxBody }.DeserializeObject(json); }
            catch { throw new Exception("接口没有返回有效 JSON，请检查是否填成了网页首页"); }
        }
        private static string ReadBody(WebResponse response)
        {
            if (response.ContentLength > MaxBody) throw new Exception("响应超过 2 MB");
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var buffer = new char[4096];
                var text = new StringBuilder();
                int n;
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (text.Length + n > MaxBody) throw new Exception("响应超过 2 MB");
                    text.Append(buffer, 0, n);
                }
                return text.ToString();
            }
        }
        private static int Delay(object root, string key)
        {
            double n;
            object v = JsonFieldPath.Optional(root, key);
            if (v == null || !double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n) || double.IsNaN(n) || n <= 0) return 0;
            return (int)Math.Min(604800, Math.Ceiling(n));
        }
        private static int RetryAfter(string value)
        {
            int seconds;
            if (int.TryParse(value, out seconds)) return Math.Max(0, Math.Min(604800, seconds));
            DateTimeOffset time;
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)
                ? (int)Math.Max(0, Math.Min(604800, Math.Ceiling((time.UtcDateTime - DateTime.UtcNow).TotalSeconds))) : 0;
        }
        public static CustomFetchResult Fetch(ProviderConfig p, bool requestOnly = false)
        {
            if (!p.Enabled) return new CustomFetchResult { Statuses = new List<ProviderStatus>(), PreviewJson = "" };
            if (p.Custom == null) throw new Exception("缺少自定义接口配置");
            if (requestOnly) p.Custom.ValidateRequest(); else p.Custom.Validate();
            string headerText = p.GetKey();
            if (headerText == null) throw new Exception("无法解密请求头，请重新填写");
            var headers = ParseHeaders(headerText);
            string method = p.Custom.HttpMethod;
            string body = method == "POST" ? p.Custom.GetBody() : "";
            if (body == null) throw new Exception("无法解密请求体，请重新填写");
            if (body.Length > 1048576) throw new Exception("请求体超过 1 MB");
            string cacheKey;
            var headerNames = new List<string>(headers.Keys); headerNames.Sort(StringComparer.OrdinalIgnoreCase);
            var identity = new StringBuilder(new Uri(p.Custom.Url).AbsoluteUri);
            identity.Append('\n').Append(method).Append('\n').Append(body.Length).Append(':').Append(body);
            foreach (string name in headerNames) identity.Append('\n').Append(name.ToLowerInvariant()).Append(':').Append(headers[name]);
            using (var hash = SHA256.Create()) cacheKey = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(identity.ToString())));
            Entry entry;
            lock (Cache)
            {
                if (!Cache.TryGetValue(cacheKey, out entry)) Cache.Add(cacheKey, entry = new Entry());
            }
            lock (entry)
            {
                bool cached = DateTime.UtcNow < entry.NextUtc;
                if (!cached)
                {
                    var req = (HttpWebRequest)WebRequest.Create(p.Custom.Url);
                    req.Method = method; req.Timeout = 15000; req.ReadWriteTimeout = 15000;
                    req.AllowAutoRedirect = false; // 自定义认证头不得随重定向发到其他地址。
                    req.Accept = "application/json"; req.UserAgent = "QuotaWidget/" + AppInfo.Version;
                    if (method == "POST") req.ContentType = "application/json; charset=utf-8";
                    foreach (var h in headers)
                    {
                        if (h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = h.Value;
                        else if (h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = h.Value;
                        else if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) req.ContentType = h.Value;
                        else req.Headers[h.Key] = h.Value;
                    }
                    try
                    {
                        if (method == "POST")
                        {
                            byte[] data = Encoding.UTF8.GetBytes(body);
                            req.ContentLength = data.Length;
                            using (var stream = req.GetRequestStream()) stream.Write(data, 0, data.Length);
                        }
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        {
                            if ((int)resp.StatusCode >= 300) throw new Exception("接口发生重定向，请填写最终数据接口地址");
                            string json = ReadBody(resp);
                            object root = Deserialize(json);
                            entry.Json = json; entry.FetchedUtc = DateTime.UtcNow;
                            entry.CooldownError = null;
                            entry.NextUtc = entry.FetchedUtc.AddSeconds(Math.Max(Delay(root, "next_allowed_in"), RetryAfter(resp.Headers["Retry-After"])));
                        }
                    }
                    catch (WebException ex)
                    {
                        using (var resp = ex.Response as HttpWebResponse)
                        {
                            if (resp == null) throw new Exception("连接失败或超时（" + ex.Status + "）");
                            int status = (int)resp.StatusCode;
                            if (status == 429 || status == 503)
                            {
                                int wait = Math.Max(60, RetryAfter(resp.Headers["Retry-After"]));
                                try { wait = Math.Max(wait, Delay(Deserialize(ReadBody(resp)), "retry_after")); } catch { }
                                entry.NextUtc = DateTime.UtcNow.AddSeconds(wait);
                                if (status == 429 && entry.Json != null) cached = true;
                                else { entry.CooldownError = "HTTP " + status + "：接口暂不可用"; throw new Exception(entry.CooldownError + "，请等待 " + wait + " 秒后重试"); }
                            }
                            else throw new Exception("HTTP " + status + (status == 401 || status == 403 ? "：请检查认证请求头" : "：接口请求失败"));
                        }
                    }
                }
                int left = (int)Math.Max(0, Math.Ceiling((entry.NextUtc - DateTime.UtcNow).TotalSeconds));
                if (entry.CooldownError != null) throw new Exception(entry.CooldownError + "，请等待 " + left + " 秒后重试");
                if (entry.Json == null) throw new Exception("接口冷却中，请等待 " + left + " 秒后重试");
                var statuses = new List<ProviderStatus>();
                if (!requestOnly)
                {
                    try { statuses = Parse(p, entry.Json); }
                    catch (Exception ex) { statuses = new List<ProviderStatus> { QuotaService.EmptyStatus(p, ex.Message) }; }
                }
                foreach (var st in statuses) st.UpdatedAt = entry.FetchedUtc.ToLocalTime();
                var secrets = new List<string>(headers.Values);
                if (body.Length > 0) secrets.Add(body);
                object previewRoot = Redact(Deserialize(entry.Json), secrets);
                string preview = new JavaScriptSerializer { MaxJsonLength = MaxBody }.Serialize(previewRoot);
                return new CustomFetchResult { Statuses = statuses, PreviewJson = PrettyJson(preview), Cached = cached,
                    WaitSeconds = left, FetchedUtc = entry.FetchedUtc, Json = entry.Json, PreviewRoot = previewRoot };
            }
        }
        private static object Redact(object value, ICollection<string> secrets)
        {
            var obj = value as Dictionary<string, object>;
            if (obj != null)
            {
                var copy = new Dictionary<string, object>();
                foreach (var pair in obj) copy[pair.Key] = System.Text.RegularExpressions.Regex.IsMatch(pair.Key, "(?i)token|secret|password|authorization|cookie|api.?key|management.?key") ? "[已隐藏]" : Redact(pair.Value, secrets);
                return copy;
            }
            var list = value as IList;
            if (list != null) { var copy = new List<object>(); foreach (object item in list) copy.Add(Redact(item, secrets)); return copy; }
            if (value is string)
            {
                string text = (string)value;
                foreach (string secret in secrets) if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "[已隐藏]");
                return text;
            }
            return value;
        }
        private static string PrettyJson(string json)
        {
            var output = new StringBuilder(); int depth = 0; bool quoted = false, escaped = false;
            foreach (char c in json)
            {
                if (quoted)
                {
                    output.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') { quoted = true; output.Append(c); }
                else if (c == '{' || c == '[') { output.Append(c).AppendLine(); depth++; output.Append(' ', depth * 2); }
                else if (c == '}' || c == ']') { depth--; output.AppendLine().Append(' ', depth * 2).Append(c); }
                else if (c == ',') output.Append(c).AppendLine().Append(' ', depth * 2);
                else if (c == ':') output.Append(": ");
                else output.Append(c);
            }
            return output.ToString();
        }

        private static bool MissingRuleValue(object value)
        {
            return value == null || (value is string && string.IsNullOrWhiteSpace((string)value));
        }

        // 本次响应缺少规则所需的数值时返回 null；规则保留，下次取到值会自动恢复。
        public static QuotaPool ParseRule(CustomQuotaRule rule, object root)
        {
            rule.Validate();
            object rawValue = JsonFieldPath.Get(root, rule.ValuePath);
            object rawTotal = rule.Mode == "usedTotal" ? JsonFieldPath.Get(root, rule.TotalPath) : null;
            if (MissingRuleValue(rawValue) || (rule.Mode == "usedTotal" && MissingRuleValue(rawTotal))) return null;
            double value = JsonFieldPath.Number(rawValue, rule.ValuePath);
            var pool = new QuotaPool { Label = rule.Name };
            if (rule.Mode == "balance")
            {
                value *= rule.Multiplier;
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new Exception("余额换算结果超出范围");
                pool.IsMoney = true; pool.Currency = rule.Currency ?? ""; pool.Remaining = value; pool.Total = Math.Max(0, value); pool.Percent = value > 0 ? 0 : 100;
            }
            else
            {
                double used;
                if (rule.Mode == "usedTotal")
                {
                    double total = JsonFieldPath.Number(rawTotal, rule.TotalPath);
                    if (total <= 0 || value < 0 || value > total) throw new Exception("总量必须大于 0，已用量须在 0 到总量之间");
                    pool.Total = total; pool.Used = value; pool.Remaining = total - value; used = value / total * 100;
                }
                else
                {
                    if (rule.Fraction) value *= 100;
                    if (value < 0 || value > 100) throw new Exception("百分比超出范围，请检查已用/剩余及数值范围");
                    used = rule.IsRemaining ? 100 - value : value;
                    pool.PercentOnly = true; pool.Total = 100; pool.Used = used; pool.Remaining = 100 - used;
                }
                pool.Percent = (int)Math.Round(used);
            }
            object reset = JsonFieldPath.Optional(root, rule.ResetPath);
            if (reset != null && !string.IsNullOrWhiteSpace(Convert.ToString(reset, CultureInfo.InvariantCulture)))
            {
                string text = Convert.ToString(reset, CultureInfo.InvariantCulture); double epoch; DateTimeOffset time;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out epoch))
                    pool.ResetLocal = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(epoch >= 100000000000 ? epoch / 1000 : epoch).LocalDateTime;
                else if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)) pool.ResetLocal = time.LocalDateTime;
                else throw new Exception("重置时间格式无效");
            }
            return pool;
        }
        internal static string FormatPool(QuotaPool pool)
        {
            return pool.Label + ": " + (pool.IsMoney ? pool.Currency + St.FmtMoney(pool.Remaining) : "剩余" + (100 - pool.Percent) + "%");
        }
        private static void SelectPrimary(ProviderStatus status)
        {
            status.Primary5h = status.Pools.Count == 0 ? null : status.Pools[0];
            foreach (var pool in status.Pools) if (!pool.IsMoney && pool.Percent >= 100) { status.Primary5h = pool; break; }
        }
        internal static object ParseJson(string json) { return Deserialize(json); }
        public static List<ProviderStatus> Parse(ProviderConfig p, string json)
        {
            p.Custom.Validate();
            object root = Deserialize(json);
            var status = QuotaService.EmptyStatus(p, null);
            foreach (var rule in p.Custom.Rules)
            {
                try
                {
                    var pool = ParseRule(rule, root);
                    if (pool != null) status.Pools.Add(pool);
                }
                catch (Exception ex) { status.Error = rule.Name + "：" + ex.Message; status.Pools.Clear(); break; }
            }
            SelectPrimary(status);
            return new List<ProviderStatus> { status };
        }
    }
    internal class QuotaPool
    {
        public string Label = "";
        public double Total;
        public double Used;
        public double Remaining;
        public int Percent;            // 已用百分比 0-100
        public DateTime? ResetLocal;   // 本地时间（接口未提供则不显示，不做本地估算）
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
        public bool ShowName = true;    // false=悬浮窗以品牌图标+环形进度展示（随 ProviderConfig 同步）
        public string CustomIcon;
        public string CustomDisplayName;
        public string IconType { get { return Type == "custom" ? (CustomIcon ?? "custom") : Type; } }
        public string WidgetName { get { return Type == "custom" && ShowName && !string.IsNullOrWhiteSpace(CustomDisplayName) ? CustomDisplayName : Name; } }
        public string Level = "";
        public string Error = null;
        public List<QuotaPool> Pools = new List<QuotaPool>();
        public DateTime UpdatedAt = DateTime.Now;
        // 紧凑位（悬浮窗/灵动岛/托盘）主显池：平时为 5 小时窗口；
        // 周额度耗尽时由 PromoteExhaustedWeekly 切换为周额度（见该方法注释）
        public QuotaPool Primary5h;

        // 悬浮窗/托盘等文本场景的展示名：图标模式显示接口类型名（如"GLM 国内版"）
        [ScriptIgnore]
        public string DisplayName { get { return ShowName || Type == "custom" ? WidgetName : ProviderConfig.TypeDisplay(Type, Region); } }

        internal ProviderStatus WithConfig(ProviderConfig p)
        {
            var st = (ProviderStatus)MemberwiseClone();
            st.Name = p.Name ?? ""; st.Type = p.Type; st.Region = p.Region; st.ShowName = p.ShowName; st.CustomIcon = p.IconType;
            if (p.Type == "custom")
            {
                st.CustomDisplayName = p.Custom == null || string.IsNullOrWhiteSpace(p.Custom.DisplayName) ? st.Name : p.Custom.DisplayName;
            }
            return st;
        }
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
        public bool FloatingEnabled { get; set; } // 仅右键菜单切换；关闭后仍保留托盘与后台刷新
        public string FloatLayout { get; set; }  // 悬浮窗排列："island"(条形灵动岛，默认) / "card"(矩形卡片)
        public int DockSide { get; set; }        // 灵动岛吸附边（重启恢复用）：-1 未吸附 / 0 顶 / 1 左 / 2 右
        public bool Vertical { get; set; }       // 竖向岛形态（左右吸附或竖向固定时为 true，重启恢复用）

        public Config()
        {
            Providers = new List<ProviderConfig>();
            GlmKey = ""; GlmRegion = "cn"; MxKey = ""; MxRegion = "cn";
            RefreshSec = 60; OpacityPct = 95; TopMost = true; Theme = "light"; X = -1; Y = -1; AutoStart = false; LockPosition = false; DockSide = -1;
            FloatingEnabled = true; FloatLayout = "island";
        }

        // 配置与日志统一放 %LOCALAPPDATA%\QuotaWidget\。exe 常随源码仓库或便携目录放置，
        // 同目录的 config.json 极易被 git clean -fdx、删库重建等连带误删；而 DPAPI 密文
        // 绑定本机当前用户，丢文件等于丢全部 API Key。AppData 是 Windows 用户数据的标准位置，
        // 且与 DPAPI 的"本机本用户"语义一致（Local 而非 Roaming，不参与漫游同步）
        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaWidget"); }
        }

        public static string FilePath
        {
            get { return Path.Combine(DataDir, "config.json"); }
        }

        /// 是否存在已启用且可查询的供应商，与首屏、刷新、连接测试保持一致。
        [ScriptIgnore]
        public bool IsConfigured
        {
            get
            {
                if (Providers == null) return false;
                foreach (var p in Providers)
                    if (p != null && p.CanQuery) return true;
                return false;
            }
        }

        [ScriptIgnore]
        public bool AllProvidersDisabled
        {
            get
            {
                if (Providers == null || Providers.Count == 0) return false;
                foreach (var p in Providers)
                    if (p != null && p.Enabled) return false;
                return true;
            }
        }

        public static Config Load()
        {
            MigrateFromExeDir();
            Config cfg = new Config();
            try
            {
                if (File.Exists(FilePath))
                {
                    var ser = new JavaScriptSerializer();
                    // 旧 DisplayMode / AppBarScreen 字段由反序列化器忽略，沿用原悬浮窗位置与排列。
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
                    if (p.Type != "custom" && !p.ShowName)
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

        // 一次性迁移：旧版把 config.json 放 exe 同目录，首启发现旧文件且新位置尚无配置时搬走。
        // 同机同用户移动 DPAPI 密文原样可解，四个 Key 无感迁移；复制成功后才删旧文件，
        // 任一步失败保留旧文件下次启动重试，绝不出现两边都没有的情况
        private static void MigrateFromExeDir()
        {
            try
            {
                string oldPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (!File.Exists(oldPath) || File.Exists(FilePath)) return;
                Directory.CreateDirectory(DataDir);
                File.Copy(oldPath, FilePath, true);
                if (!File.Exists(FilePath)) return;
                File.Delete(oldPath);
                // 旧版在 exe 同目录留下的损坏备份可能残留明文 key，一并清掉
                try { if (File.Exists(oldPath + ".bak")) File.Delete(oldPath + ".bak"); } catch { }
            }
            catch { }
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
                Directory.CreateDirectory(DataDir); // 首次保存时 AppData 目录可能尚不存在
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
        public static Dictionary<string, object> Dict(object o) { return o as Dictionary<string, object>; }
        public static bool HasAny(Dictionary<string, object> d, string[] keys)
        {
            foreach (var k in keys) if (HasKey(d, k)) return true;
            return false;
        }
        // 深度优先找第一个「含指定键之一」的对象：控制台网关的信封层级会变（data / DataV2 / data.data），
        // 与其猜层数，不如按业务字段出现的位置定位，改版时也只影响一处
        public static Dictionary<string, object> FindFirstWithAny(object node, string[] keys)
        {
            var d = node as Dictionary<string, object>;
            if (d != null)
            {
                if (HasAny(d, keys)) return d;
                foreach (var v in d.Values)
                {
                    var hit = FindFirstWithAny(v, keys);
                    if (hit != null) return hit;
                }
                return null;
            }
            foreach (var item in Each(node))
            {
                var hit = FindFirstWithAny(item, keys);
                if (hit != null) return hit;
            }
            return null;
        }

        public static IEnumerable<object> Each(object o)
        {
            if (o is ArrayList) { foreach (var i in (ArrayList)o) yield return i; }
            else if (o is object[]) { foreach (var i in (object[])o) yield return i; }
        }
    }

    // ============================= 查询服务 =============================
    internal static class QuotaService
    {
        public static List<ProviderStatus> FetchProvider(ProviderConfig p, bool force)
        {
            if (!p.Enabled) return new List<ProviderStatus>();
            if (p.Type == "custom")
            {
                var result = CustomApiService.Fetch(p);
                p.CooldownUntilUtc = DateTime.UtcNow.AddSeconds(result.WaitSeconds);
                return result.Statuses;
            }
            ProviderStatus st;
            if (p.Type == "mx") st = FetchMiniMax(p);
            else if (p.Type == "kimi") st = FetchKimi(p);
            else if (p.Type == "ds") st = FetchDeepSeek(p);
            else if (p.Type == "claude") st = FetchClaude(p, force);
            else if (p.Type == "codex") st = FetchCodex(p);
            else if (p.Type == "mimo") st = FetchMimo(p);
            else if (p.Type == "qwen" || p.Type == "bailian") st = FetchAliyunConsole(p);
            else st = FetchGlm(p);
            return new List<ProviderStatus> { st };
        }

        public static ProviderStatus EmptyStatus(ProviderConfig p, string error)
        {
            return new ProviderStatus { Error = error }.WithConfig(p);
        }
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
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(extraName) && !string.IsNullOrEmpty(extraValue)) headers[extraName] = extraValue;
            return HttpGet(url, authorization, ua, headers);
        }

        private static string HttpGet(string url, string authorization, string ua, IDictionary<string, string> extraHeaders)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = string.IsNullOrEmpty(ua) ? "QuotaWidget/" + AppInfo.Version : ua;
            req.Accept = "application/json";
            if (authorization != null) req.Headers["Authorization"] = authorization;
            if (extraHeaders != null)
                foreach (var header in extraHeaders)
                {
                    if (string.IsNullOrEmpty(header.Value)) continue;
                    // Referer / User-Agent 是受限头，走 Headers 索引器会抛 ArgumentException，只能经属性赋值
                    if (string.Equals(header.Key, "Referer", StringComparison.OrdinalIgnoreCase)) req.Referer = header.Value;
                    else if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = header.Value;
                    else req.Headers[header.Key] = header.Value;
                }
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

        // 表单 POST：千问 / 百炼的控制台「数据网关」是 POST form + params 内嵌 JSON，
        // HttpGet 覆盖不到这个形态。网关把业务错误也放在 HTTP 200 的 JSON 里，所以状态码只用于
        // 网络层判断；3xx 不跟随（会被重定向到登录页，跟过去只会拿到 HTML）。
        private static string HttpPostForm(string url, string ua, IDictionary<string, string> extraHeaders, string form, int timeoutMs,
            CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = string.IsNullOrEmpty(ua) ? "QuotaWidget/" + AppInfo.Version : ua;
            req.Accept = "application/json";
            req.ContentType = "application/x-www-form-urlencoded";
            req.AllowAutoRedirect = false;
            if (extraHeaders != null)
                foreach (var header in extraHeaders)
                {
                    if (string.IsNullOrEmpty(header.Value)) continue;
                    if (string.Equals(header.Key, "Referer", StringComparison.OrdinalIgnoreCase)) req.Referer = header.Value;
                    else if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = header.Value;
                    else req.Headers[header.Key] = header.Value;
                }
            byte[] payload = Encoding.UTF8.GetBytes(form ?? "");
            req.ContentLength = payload.Length;
            using (cancellation.Register(req.Abort))
            try
            {
                using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
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
        // 200+success=false+code 1000/1001 = 鉴权失败（Key 无效/被删/填错，或鉴权格式被拒）；
        // 财务接口也可能用 code 401 表达同一含义。回退循环与最终错误分类共用此判据，
        // 避免两处判定不一致
        private static bool IsAuthFailure(Dictionary<string, object> root)
        {
            bool success = false;
            object sv;
            if (root.TryGetValue("success", out sv) && sv is bool) success = (bool)sv;
            if (success) return false;
            int code = (int)Math.Round(J.D(root, "code"));
            // 1000–1004 都是智谱系的鉴权失败码；401 是 Z.ai 在 HTTP 200 里返回的拒绝码
            return (code >= 1000 && code <= 1004) || code == 401;
        }

        // 解出明文 key；密文解不开（换电脑/换 Windows 账户）时抛出可读异常，
        // 由刷新/测试的 catch 展示；未配置 key 的供应商由调用方先行跳过
        private static string RequireKey(ProviderConfig p)
        {
            string k = p.GetKey();
            if (k == null) throw new Exception("密钥无法解密（可能已更换电脑或 Windows 账户），请在设置中重新填写");
            return k;
        }

        // 周额度耗尽时把主显切到周额度。紧凑位（悬浮窗/灵动岛/竖向岛/托盘）平时固定展示
        // 5 小时窗口，不按剩余比例中途换池——两个窗口消耗速率不同，切换会让数字看起来
        // 忽快忽慢；只有周窗口真正用完（此时拦住使用的是周额度，5 小时余量已无意义）
        // 才切换为周额度的红色 0%，数字变化只表达"被硬顶挡住"这一种含义。
        // 与 CodexBar 的菜单栏策略一致：固定主窗口，仅耗尽时切换。
        static void PromoteExhaustedWeekly(ProviderStatus st)
        {
            if (st.Primary5h != null && st.Primary5h.Percent >= 100) return;   // 5h 自身已耗尽：同为红 0%，保持稳定
            foreach (var pool in st.Pools)
            {
                if (pool.IsMoney || pool.Note == "未启用") continue;           // 余额无窗口概念；未启用≠耗尽
                if (!pool.Label.StartsWith("周额度")) continue;
                // 耗尽要有正向证据：百分比到顶，或计数确实用完（Used>=Total 且 Remaining 不为正）。
                // 字段缺失时 J.D 返回 0，单凭 Remaining<=0 会把"接口没给 remaining"误判成耗尽
                bool exhausted = pool.Percent >= 100
                    || (pool.Total > 0 && pool.Remaining <= 0 && pool.Used >= pool.Total);
                if (exhausted) { st.Primary5h = pool; return; }
            }
        }

        // 额度接口与钱包接口共用鉴权回退：官方文档要求额度接口使用裸 Key（Bearer 会被拒），
        // 财务接口则接受 Bearer，因此由调用方指定先试哪种格式。失败（401/403 或 200+鉴权失败 JSON）
        // 或空响应时回退另一种格式。
        // 2026-08 起智谱监控接口故障：对有效 Key 返回 200+空 body，空体经
        // JavaScriptSerializer 反序列化得 null，后续 root.TryGetValue 会抛
        // NullReferenceException（"未将对象引用设置到对象的实例"），必须在此拦截
        private static string RequestGlmJson(string url, string key, string emptyMessage, IDictionary<string, string> headers, bool rawFirst)
        {
            string json = null;            // 有效（非空、非鉴权失败）响应体
            Exception firstErr = null;     // 首个鉴权相关失败（401/403 或 200+鉴权失败 JSON）；双失败时抛出，保留真实根因
            var ser = new JavaScriptSerializer();
            var auths = rawFirst ? new[] { key, "Bearer " + key } : new[] { "Bearer " + key, key };
            foreach (var auth in auths)
            {
                try
                {
                    string body = HttpGet(url, auth, null, headers);
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
                throw new Exception(emptyMessage);
            }
            return json;
        }

        // 解析 Coding Plan 额度响应（GET /api/monitor/usage/quota/limit）
        internal static ProviderStatus ParseGlmQuota(ProviderConfig p, string json)
        {
            var ser = new JavaScriptSerializer();
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
            int limitCount = 0;
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
                    if (is5h && st.Primary5h == null) st.Primary5h = pool;
                    st.Pools.Add(pool);
                    limitCount++;
                }
            }
            if (limitCount == 0)
                throw new Exception(p.EffectiveGlmPlanType == "team"
                    ? "额度接口返回空数据：请检查团队组织 ID / 项目 ID 是否正确"
                    : "额度接口未返回任何额度数据");
            PromoteExhaustedWeekly(st);
            st.UpdatedAt = DateTime.Now;
            return st;
        }

        // 现金钱包（GET /api/biz/account/query-customer-account-report，与 Coding Plan 额度接口共用
        // API Key）。availableBalance 是可用余额；rechargeAmount / giveAmount / totalSpendAmount
        // 都是累计值（充值总额 / 赠送总额 / 消费总额），故分别标注"累计…"，不当作余额。
        // 余额为 0 也保留，用于确认钱包已用尽；字段缺失或为 0 的累计行直接省略，避免明细过长。
        // currency 由账户币种决定（不随接口域名换算），传 "¥" 或 "$"。
        internal static List<QuotaPool> ParseGlmWallet(string json, string currency)
        {
            var ser = new JavaScriptSerializer();
            var root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null) throw new Exception("钱包接口返回无法解析的响应");
            bool success = true;
            object sv;
            if (root.TryGetValue("success", out sv) && sv is bool) success = (bool)sv;
            if (!success)
            {
                int code = (int)Math.Round(J.D(root, "code"));
                string msg = J.S(root, "msg");
                if (IsAuthFailure(root)) throw new Exception("GLM API Key 无效或已失效（" + msg + "）");
                throw new Exception("钱包接口返回失败" + (string.IsNullOrEmpty(msg) ? "（code " + code + "）" : ": " + msg));
            }
            object dataObj;
            if (!root.TryGetValue("data", out dataObj)) throw new Exception("钱包接口返回缺少 data 字段");
            var data = dataObj as Dictionary<string, object>;
            if (data == null) throw new Exception("钱包接口返回缺少 data 字段");

            double available = J.HasKey(data, "availableBalance") ? J.D(data, "availableBalance") : J.D(data, "balance");
            var pools = new List<QuotaPool>();
            pools.Add(new QuotaPool
            {
                Label = "钱包余额",
                IsMoney = true,
                Currency = currency,
                Remaining = available,
                Total = available,
                // 余额为 0 时用红环表达"无可扣余额"；有余额时保持绿色满环（与 DeepSeek 余额一致）
                Percent = available > 0 ? 0 : 100
            });
            double recharge = J.D(data, "rechargeAmount");
            if (recharge > 0)
                pools.Add(new QuotaPool { Label = "累计充值", IsMoney = true, Currency = currency, Remaining = recharge, Total = recharge, Percent = 0 });
            double gift = J.D(data, "giveAmount");
            if (gift > 0)
                pools.Add(new QuotaPool { Label = "累计赠送", IsMoney = true, Currency = currency, Remaining = gift, Total = gift, Percent = 0 });
            double spent = J.D(data, "totalSpendAmount");
            if (spent > 0)
                pools.Add(new QuotaPool { Label = "累计消费", IsMoney = true, Currency = currency, Remaining = spent, Total = spent, Percent = 0 });
            return pools;
        }

        // 钱包查询结果 → 状态：主显第一个金额池（钱包余额），详情按 钱包余额 / 累计… 排列
        internal static ProviderStatus GlmWalletStatus(ProviderConfig p, List<QuotaPool> wallet)
        {
            var st = new ProviderStatus { Name = p.Name, Type = "glm", Region = p.Region, ShowName = p.ShowName, UpdatedAt = DateTime.Now };
            st.Pools.AddRange(wallet);
            st.Primary5h = wallet.Count > 0 ? wallet[0] : null;
            return st;
        }

        // 钱包按币种选择站点：人民币走国内站，美元走国际站（与 QuotaTray 等项目的
        // zhipu_api=CNY / zai_api=USD 划分一致）。套餐额度仍按用户选择的接口区域。
        // 这样国内账户即使把接口切到国际版，也不会拿人民币金额去请求国际站再套 $。
        internal static string GlmBaseUrl(ProviderConfig p)
        {
            if (p.EffectiveGlmMode == "wallet")
                return p.EffectiveGlmCurrency == "USD" ? "https://api.z.ai" : "https://open.bigmodel.cn";
            return p.Region == "intl" ? "https://api.z.ai" : "https://open.bigmodel.cn";
        }

        // 套餐额度按官方新版协议组装：个人版无参数；团队版需要 ?type=2 与组织/项目上下文头。
        // 缺少上下文时明确报错，避免接口返回空 data 被误显示成 0 额度。
        internal static string GlmQuotaUrl(ProviderConfig p)
        {
            string url = GlmBaseUrl(p) + "/api/monitor/usage/quota/limit";
            return p.EffectiveGlmPlanType == "team" ? url + "?type=2" : url;
        }

        internal static Dictionary<string, string> GlmQuotaHeaders(ProviderConfig p)
        {
            var headers = new Dictionary<string, string>();
            if (p.EffectiveGlmPlanType != "team") return headers;
            if (!p.HasGlmTeamContext)
                throw new Exception("GLM 团队套餐需要填写组织 ID 和项目 ID（bigmodel-organization / bigmodel-project）");
            headers["bigmodel-organization"] = p.GlmOrganization.Trim();
            headers["bigmodel-project"] = p.GlmProject.Trim();
            return headers;
        }

        // 查询内容二选一：Coding Plan 套餐额度 / 标准 API 钱包余额。
        // 官方已把套餐 Key 与标准 API Key 分开管理，同一个 Key 不再同时属于两种产品，
        // 因此按 Key 所属产品只请求对应接口，不再同时查两条。
        public static ProviderStatus FetchGlm(ProviderConfig p)
        {
            string baseUrl = GlmBaseUrl(p);
            string key = RequireKey(p);
            if (p.EffectiveGlmMode == "wallet")
                return GlmWalletStatus(p, ParseGlmWallet(
                    RequestGlmJson(baseUrl + "/api/biz/account/query-customer-account-report", key, "智谱钱包接口返回空响应", null, false),
                    p.EffectiveGlmCurrency == "USD" ? "$" : "¥"));
            // 官方文档要求额度接口用裸 Key（Bearer 会被拒），所以先试裸 Key
            return ParseGlmQuota(p, RequestGlmJson(
                GlmQuotaUrl(p), key, "智谱接口返回空响应（服务端故障中，非 Key 或网络问题），请稍后重试", GlmQuotaHeaders(p), true));
        }

        // ---------- 小米 MiMo ----------
        // 官方只开放了网页控制台的私有接口（platform.xiaomimimo.com/api/v1）：
        //   GET /balance         账号余额（data.balance 是字符串，data.currency 明确给币种）
        //   GET /tokenPlan/usage Token Plan 额度（data.monthUsage.items[] 每项 used/limit/percent）
        //   GET /tokenPlan/detail 套餐详情（currentPeriodEnd 是 UTC 的 "yyyy-MM-dd HH:mm:ss"）
        // 鉴权是控制台登录 Cookie（api-platform_serviceToken + userId），API Key 调不通：
        // 带着推理 Key 请求同样返回 HTTP 401 + {"code":401,"loginUrl":...}。
        internal const string MimoApiBase = "https://platform.xiaomimimo.com/api/v1";
        internal const string MimoConsoleUrl = "https://platform.xiaomimimo.com/console/balance";
        internal const string MimoExpiredMessage = "MiMo 登录已过期，请重新点击「自动获取」获取 Cookie";
        // 控制台是普通网页，接口按浏览器上下文校验：UA/Origin/Referer 全部按浏览器补齐
        private const string MimoUserAgent = ConsoleUserAgent;

        // 官方会话 Cookie 自签发起 24 小时有效；这里只做展示，不参与请求判断
        // 只显示「什么时候拿到的」：官方会话 Cookie 的官方说法是自签发起 24 小时有效，
        // 但也有账户靠小米账号登录态长期免登录，实测可能远长于 24 小时。
        // 所以这里不做倒计时、也不按时间主动作废，过期与否只由接口是否返回 401 决定。
        internal static string MimoLoginText(string loginAt)
        {
            if (string.IsNullOrEmpty(loginAt)) return "";
            DateTime parsed;
            if (!DateTime.TryParse(loginAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)) return "";
            return "登录于 " + parsed.ToLocalTime().ToString("MM-dd HH:mm");
        }
        internal static string MimoUrl(ProviderConfig p)
        {
            return MimoApiBase + (p.EffectiveMimoMode == "balance" ? "/balance" : "/tokenPlan/usage");
        }

        // 用户可能整段粘贴 "Cookie: a=b; c=d"（DevTools 复制出来的就是这种），也可能只粘键值对
        internal static string MimoCookie(string raw)
        {
            return ConsoleCookie(raw);
        }

        // 两个必需 Cookie 缺一不可：缺了请求会落到登录页或返回 401 loginUrl
        internal static bool IsMimoCookie(string cookie)
        {
            return !string.IsNullOrEmpty(cookie)
                && cookie.IndexOf("api-platform_serviceToken", StringComparison.OrdinalIgnoreCase) >= 0
                && cookie.IndexOf("userId", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string MimoCurrencySymbol(string code)
        {
            string c = (code ?? "").Trim();
            if (c.Length == 0) return "¥";
            string u = c.ToUpperInvariant();
            if (u == "USD" || u == "$" || u == "US$") return "$";
            if (u == "CNY" || u == "RMB" || u == "￥" || u == "¥") return "¥";
            return c + " ";
        }

        // MiMo 的成功信封是 code == 0（不是 success:true）；code 401 也会以 HTTP 200 返回
        private static Dictionary<string, object> MimoData(string json, string what)
        {
            var ser = new JavaScriptSerializer();
            Dictionary<string, object> root = null;
            try { root = ser.Deserialize<Dictionary<string, object>>(json); }
            catch { }
            if (root == null)
                throw new Exception("MiMo " + what + "接口返回无法解析的响应" +
                    (string.IsNullOrEmpty(json) ? "" : ": " + json.Substring(0, Math.Min(120, json.Length))));
            int code = (int)Math.Round(J.D(root, "code"));
            if (code == 401 || code == 403) throw new Exception(MimoExpiredMessage);
            if (code != 0)
            {
                string msg = J.S(root, "message");
                if (string.IsNullOrEmpty(msg)) msg = J.S(root, "msg");
                throw new Exception("接口返回失败" + (string.IsNullOrEmpty(msg) ? "（code " + code + "）" : ": " + msg));
            }
            var data = root.ContainsKey("data") ? root["data"] as Dictionary<string, object> : null;
            if (data == null) throw new Exception("MiMo " + what + "接口返回缺少 data 字段");
            return data;
        }

        private static string RequestMimoJson(string url, string cookie, string what)
        {
            var headers = new Dictionary<string, string>();
            headers["Cookie"] = cookie;
            headers["Origin"] = "https://platform.xiaomimimo.com";
            headers["Referer"] = MimoConsoleUrl;
            string body;
            try { body = HttpGet(url, null, MimoUserAgent, headers); }
            catch (HttpException he)
            {
                // 401/403：Cookie 过期或被拒；其余（超时/5xx）保持原始描述
                if (he.StatusCode == 401 || he.StatusCode == 403) throw new Exception(MimoExpiredMessage);
                throw;
            }
            if (string.IsNullOrWhiteSpace(body)) throw new Exception("MiMo " + what + "接口返回空响应");
            return body;
        }

        // 从 items[] 里挑额度项：优先指定名称，其次第一条 limit > 0 的记录
        private static Dictionary<string, object> MimoUsageItem(object itemsObj, string preferred)
        {
            Dictionary<string, object> fallback = null;
            foreach (var raw in J.Each(itemsObj))
            {
                var item = raw as Dictionary<string, object>;
                if (item == null) continue;
                if (fallback == null && J.D(item, "limit") > 0) fallback = item;
                string name = J.S(item, "name");
                if (name == preferred) return item;
            }
            return fallback;
        }

        internal static List<QuotaPool> ParseMimoBalance(string json)
        {
            var data = MimoData(json, "余额");
            string symbol = MimoCurrencySymbol(J.S(data, "currency"));
            double balance = J.D(data, "balance");
            var pools = new List<QuotaPool>();
            pools.Add(new QuotaPool
            {
                Label = "账户余额",
                IsMoney = true,
                Currency = symbol,
                Remaining = balance,
                Total = balance,
                // 与 GLM / DeepSeek 一致：有余额画满环，0 余额画红环
                Percent = balance > 0 ? 0 : 100
            });
            double cash = J.D(data, "cashBalance");
            if (cash > 0)
                pools.Add(new QuotaPool { Label = "现金余额", IsMoney = true, Currency = symbol, Remaining = cash, Total = cash, Percent = 0 });
            double gift = J.D(data, "giftBalance");
            if (gift > 0)
                pools.Add(new QuotaPool { Label = "赠送余额", IsMoney = true, Currency = symbol, Remaining = gift, Total = gift, Percent = 0 });
            return pools;
        }

        // 套餐额度以 Credits 计（Lite = 41 亿），直接显示原始值会撑爆详情行，
        // 因此按量级折算成"亿/万"并写进标签，百分比仍用接口给出的口径
        internal static List<QuotaPool> ParseMimoPlan(string json, string detailJson)
        {
            var data = MimoData(json, "Token Plan");
            object monthObj;
            var month = data.TryGetValue("monthUsage", out monthObj) ? monthObj as Dictionary<string, object> : null;
            object itemsObj = null;
            if (month != null) month.TryGetValue("items", out itemsObj);
            string label = "本月额度";
            var plan = MimoUsageItem(itemsObj, "month_total_token");
            if (plan == null)
            {
                // 没有 monthUsage（或 items 为空）时回退到 usage.items：部分账号只回本期用量
                label = "本期额度";
                object usageObj;
                var usage = data.TryGetValue("usage", out usageObj) ? usageObj as Dictionary<string, object> : null;
                itemsObj = null;
                if (usage != null) usage.TryGetValue("items", out itemsObj);
                plan = MimoUsageItem(itemsObj, "plan_total_token");
            }
            if (plan == null) throw new Exception("该账号未开通 Token Plan");

            double used = J.D(plan, "used"), limit = J.D(plan, "limit");
            if (limit <= 0) throw new Exception("该账号未开通 Token Plan");
            double percent = J.D(plan, "percent");
            // 百分比以 used/limit 为准：接口 percent 可能是 0-1 的小数或未返回
            double pct = used / limit * 100;
            if (pct <= 0 && percent > 0) pct = percent <= 1 ? percent * 100 : percent;

            double scale = 1;
            string unit = "Credits";
            if (limit >= 1e8) { scale = 1e8; unit = "亿 Credits"; }
            else if (limit >= 1e4) { scale = 1e4; unit = "万 Credits"; }

            var pool = new QuotaPool
            {
                Label = label + "（" + unit + "）",
                Total = limit / scale,
                Used = used / scale,
                Remaining = Math.Max(0, limit - used) / scale,
                Percent = (int)Math.Round(Math.Max(0, Math.Min(100, pct)))
            };
            var reset = MimoPeriodEnd(detailJson);
            if (reset.HasValue) pool.ResetLocal = reset.Value;

            var pools = new List<QuotaPool> { pool };

            // 补偿额度（官方活动补发）单独一行，和主额度分开看
            foreach (var raw in J.Each(itemsObj))
            {
                var item = raw as Dictionary<string, object>;
                if (item == null || J.S(item, "name") != "compensation_total_token") continue;
                double cUsed = J.D(item, "used"), cLimit = J.D(item, "limit");
                if (cLimit <= 0) continue;
                pools.Add(new QuotaPool
                {
                    Label = "补偿额度（" + unit + "）",
                    Total = cLimit / scale,
                    Used = cUsed / scale,
                    Remaining = Math.Max(0, cLimit - cUsed) / scale,
                    Percent = (int)Math.Round(Math.Max(0, Math.Min(100, cUsed / cLimit * 100))),
                    ResetLocal = reset
                });
            }
            return pools;
        }

        internal static DateTime? MimoPeriodEnd(string detailJson)
        {
            if (string.IsNullOrWhiteSpace(detailJson)) return null;
            try
            {
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<Dictionary<string, object>>(detailJson);
                if (root == null) return null;
                var data = root.ContainsKey("data") ? root["data"] as Dictionary<string, object> : null;
                if (data == null) return null;
                string text = J.S(data, "currentPeriodEnd");
                if (string.IsNullOrEmpty(text)) return null;
                // 官方按 UTC 输出 "yyyy-MM-dd HH:mm:ss"，不能按本地时间解析
                DateTime utc;
                if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
                    return utc.ToLocalTime();
                return ParseTimeFlexible(text);
            }
            catch { return null; }
        }

        internal static ProviderStatus MimoStatus(ProviderConfig p, List<QuotaPool> pools)
        {
            var st = new ProviderStatus { Name = p.Name, Type = "mimo", Region = p.Region, ShowName = p.ShowName, UpdatedAt = DateTime.Now };
            st.Pools.AddRange(pools);
            st.Primary5h = pools.Count > 0 ? pools[0] : null;
            return st;
        }

        // 查询内容二选一：Token Plan 套餐额度 / 账号余额。两者共用同一份控制台 Cookie
        public static ProviderStatus FetchMimo(ProviderConfig p)
        {
            string cookie = MimoCookie(RequireKey(p));
            if (!IsMimoCookie(cookie))
                throw new Exception("MiMo 需要控制台登录 Cookie（含 api-platform_serviceToken 与 userId），请点击「自动获取」");
            if (p.EffectiveMimoMode == "balance")
                return MimoStatus(p, ParseMimoBalance(RequestMimoJson(MimoApiBase + "/balance", cookie, "余额")));
            string usage = RequestMimoJson(MimoApiBase + "/tokenPlan/usage", cookie, "Token Plan");
            string detail = null;
            try { detail = RequestMimoJson(MimoApiBase + "/tokenPlan/detail", cookie, "Token Plan 详情"); }
            catch (Exception) { }   // 详情只提供重置时间，失败不影响额度展示
            return MimoStatus(p, ParseMimoPlan(usage, detail));
        }

        // ---------- 阿里云控制台：千问 / 百炼 Token Plan ----------
        // 两家控制台是同一套「数据网关」，只有域名与 consoleSite 不同：
        //   GET  {infoUrl}  → data.secToken（CSRF 令牌，登录后自动取；手配只作兜底）
        //   POST {gateway}?action=BroadScopeAspnGateway&product=sfm_bailian&api=<inner>&_v=undefined
        //        表单 product / action / region / language / params(JSON) [/ sec_token]
        //        inner = zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/{usage|quota-config|subscription}
        // 本机实测（2026-11-03，未登录）：只传 Api 时回 data.errorCode="Bad Request"，
        // 补上 cornerstoneParam 后回 "BailianGateway.Login.NotLogined" —— 请求骨架正确，缺的只有登录态；
        // 两家返回同一个错误码，因此共用一份实现，只切换站点常量。
        // 字段口径（per1MonthPercentage / quota-config[specCode].monthly / subscription.specCode 等）
        // 参考 dsh-token-plan-quota（MIT）的逆向核对结果，其在真实账号上验证过。
        internal const string AliyunGatewayAction = "BroadScopeAspnGateway";
        internal const string AliyunGatewayProduct = "sfm_bailian";
        internal const string AliyunApiPrefix = "zeldaHttp.apikeyMgr./tokenplan/personal/api/v2";
        internal const string AliyunRegion = "cn-beijing";

        internal const string QwenConsoleOrigin = "https://platform-home.qianwenai.com";
        internal const string QwenConsoleUrl = "https://platform-home.qianwenai.com/analytics/token-plan/individual";
        // platform-home 是工作台子应用；独立打开会空白，浏览器必须走官网外层入口。
        internal const string QwenLoginUrl = "https://platform.qianwenai.com/home/analytics/token-plan";
        internal const string QwenGatewayUrl = "https://cs-data.qianwenai.com/data/api.json";
        internal const string QwenInfoUrl = "https://platform-home.qianwenai.com/tool/user/info.json";
        internal const string QwenConsoleSite = "QIANWENAI";

        internal const string BailianConsoleOrigin = "https://bailian.console.aliyun.com";
        internal const string BailianConsoleUrl = "https://bailian.console.aliyun.com/";
        internal const string BailianGatewayUrl = "https://bailian-cs.console.aliyun.com/data/api.json";
        internal const string BailianInfoUrl = "https://bailian.console.aliyun.com/tool/user/info.json";
        internal const string BailianConsoleSite = "BAILIAN_ALIYUN";

        internal static bool IsBailian(string type) { return type == "bailian"; }
        internal static string AliyunBrand(string type) { return IsBailian(type) ? "百炼" : "千问"; }
        internal static string AliyunConsoleOrigin(string type) { return IsBailian(type) ? BailianConsoleOrigin : QwenConsoleOrigin; }
        internal static string AliyunConsoleUrl(string type) { return IsBailian(type) ? BailianConsoleUrl : QwenConsoleUrl; }
        internal static string AliyunGatewayUrl(string type) { return IsBailian(type) ? BailianGatewayUrl : QwenGatewayUrl; }
        internal static string AliyunInfoUrl(string type) { return IsBailian(type) ? BailianInfoUrl : QwenInfoUrl; }
        internal static string AliyunConsoleSite(string type) { return IsBailian(type) ? BailianConsoleSite : QwenConsoleSite; }

        // 控制台数据网关会挑自定义 UA 的毛病（报 PostonlyOrTokenError），必须用浏览器 UA
        internal const string ConsoleUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";

        // 粘贴痕迹清理：DevTools 整行复制会带 "Cookie: " 前缀，X11/终端粘贴还可能夹换行；
        // 换行留在头里会让请求直接失败，所以在入口统一抹掉
        internal static string ConsoleCookie(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7).Trim();
            return s.Replace("\r", "").Replace("\n", "");
        }

        // 阿里云控制台的登录态是 passport 的 login_aliyunid* 系列 Cookie（HttpOnly，域为 .aliyun.com /
        // .qianwenai.com）。这里只做结构预检，真正是否还有效由网关返回决定
        internal static bool IsAliyunCookie(string cookie)
        {
            if (string.IsNullOrEmpty(cookie)) return false;
            return cookie.IndexOf("login_aliyunid", StringComparison.OrdinalIgnoreCase) >= 0
                || cookie.IndexOf("aliyun_site", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string AliyunExpiredMessage(string type)
        {
            return AliyunBrand(type) + " 登录已过期，请重新点击「自动获取」获取 Cookie";
        }

        // 网关失败文案：errorCode 是机器可读的分类，errorMsg 往往只是重复它，
        // 所以把两者拼在一起，避免只看到 "Bad Request" 不知道问题在哪
        private static string AliyunFailText(string errorCode, string errorMsg)
        {
            bool login = errorCode.IndexOf("NotLogined", StringComparison.OrdinalIgnoreCase) >= 0
                || errorCode.IndexOf("NotAuthorised", StringComparison.OrdinalIgnoreCase) >= 0
                || errorCode.IndexOf("ConsoleNeedLogin", StringComparison.OrdinalIgnoreCase) >= 0;
            if (login) return null;   // 交给调用方按站点拼「登录已过期」
            string detail = string.IsNullOrWhiteSpace(errorMsg) || errorMsg == errorCode ? "" : "：" + errorMsg;
            return "控制台返回失败（" + errorCode + "）" + detail;
        }

        // 网关信封只做两层判断：登录态错误 / 其它业务错误。成功时把业务数据体交给调用方按字段定位
        private static Dictionary<string, object> AliyunEnvelope(string json, string type, string what)
        {
            var ser = new JavaScriptSerializer();
            Dictionary<string, object> root = null;
            try { root = ser.Deserialize<Dictionary<string, object>>(json); }
            catch { }
            if (root == null)
                throw new Exception(AliyunBrand(type) + " " + what + "接口返回无法解析的响应" +
                    (string.IsNullOrEmpty(json) ? "" : "：" + json.Substring(0, Math.Min(120, json.Length))));

            var data = root.ContainsKey("data") ? J.Dict(root["data"]) : null;
            string errorCode = data == null ? "" : J.S(data, "errorCode");
            if (string.IsNullOrEmpty(errorCode)) errorCode = J.S(root, "errorCode");
            string errorMsg = data == null ? "" : J.S(data, "errorMsg");
            if (string.IsNullOrEmpty(errorMsg)) errorMsg = J.S(root, "errorMsg");
            if (!string.IsNullOrEmpty(errorCode))
            {
                string text = AliyunFailText(errorCode, errorMsg);
                if (text == null) throw new Exception(AliyunExpiredMessage(type));
                throw new Exception(text);
            }

            string code = J.S(root, "code");
            object okObj;
            bool successResponse = root.TryGetValue("successResponse", out okObj) && okObj is bool && (bool)okObj;
            if (code != "" && code != "200" && !successResponse)
            {
                string msg = J.S(root, "message");
                if (string.IsNullOrEmpty(msg)) msg = J.S(root, "msg");
                throw new Exception("控制台返回失败（" + code + "）" + (string.IsNullOrEmpty(msg) ? "" : "：" + msg));
            }
            if (data != null)
            {
                object flag;
                if (data.TryGetValue("success", out flag) && flag is bool && !(bool)flag)
                {
                    string msg = J.S(data, "message");
                    if (string.IsNullOrEmpty(msg)) msg = J.S(data, "errorMsg");
                    throw new Exception("控制台返回失败" + (string.IsNullOrEmpty(msg) ? "" : "：" + msg));
                }
            }
            return root;
        }

        internal static Dictionary<string, object> AliyunData(string json, string type, string what, string[] markers)
        {
            var root = AliyunEnvelope(json, type, what);
            var found = J.FindFirstWithAny(root, markers);
            if (found == null)
                throw new Exception(AliyunBrand(type) + " " + what + "接口返回里没有预期字段（接口可能改版）");
            return found;
        }

        // 档位额度与订阅信息只用来补"分母"和套餐名，拿不到也要照常报官方比例，所以单独容忍失败
        private static Dictionary<string, object> AliyunOptionalData(string json, string type, string what, string[] markers)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return AliyunData(json, type, what, markers); }
            catch { return null; }
        }

        private static readonly string[] AliyunUsageMarkers =
            { "per1MonthPercentage", "per1WeekPercentage", "per5HourPercentage" };
        private static readonly string[] AliyunQuotaMarkers =
            { "addon_quota", "lite", "standard", "pro", "essential" };
        private static readonly string[] AliyunSubscriptionMarkers =
            { "specCode", "SpecCode", "remainingDays", "endTime" };

        // sec_token 是网关的 CSRF 令牌：登录后由 /tool/user/info.json 下发，不需要用户手抄
        internal static string AliyunSecToken(string type, string cookie)
        {
            try
            {
                var headers = new Dictionary<string, string>();
                headers["Cookie"] = cookie;
                headers["Origin"] = AliyunConsoleOrigin(type);
                headers["Referer"] = AliyunConsoleUrl(type);
                string body = HttpGet(AliyunInfoUrl(type), null, ConsoleUserAgent, headers);
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                if (root == null) return null;
                var data = root.ContainsKey("data") ? J.Dict(root["data"]) : null;
                string token = data == null ? "" : J.S(data, "secToken");
                if (string.IsNullOrEmpty(token)) token = J.S(root, "secToken");
                return string.IsNullOrEmpty(token) ? null : token;
            }
            catch { return null; }   // 拿不到就退回"只用 Cookie"，网关仍可能接受
        }

        internal static string AliyunGatewayRequest(string type, string cookie, string secToken, string apiName,
            CancellationToken cancellation = default(CancellationToken))
        {
            string apiPath = AliyunApiPrefix + "/" + apiName;
            var cornerstone = new Dictionary<string, object>
            {
                { "feTraceId", Guid.NewGuid().ToString() },
                { "feURL", AliyunConsoleUrl(type) },
                { "protocol", "V2" },
                { "console", "ONE_CONSOLE" },
                { "productCode", "p_efm" },
                { "switchUserType", 3 },
                { "domain", AliyunConsoleOrigin(type).Replace("https://", "").Replace("http://", "") },
                { "consoleSite", AliyunConsoleSite(type) },
                { "userNickName", "" },
                { "userPrincipalName", "" },
                { "xsp_lang", "zh-CN" }
            };
            // 匿名设备号：控制台前端会把它一并带上，缺了可能被风控当成脚本
            string anonymous = CookieValue(cookie, "cna");
            if (!string.IsNullOrEmpty(anonymous)) cornerstone["X-Anonymous-Id"] = anonymous;

            var parameters = new Dictionary<string, object>
            {
                { "Api", apiPath },
                { "V", "1.0" },
                { "Data", new Dictionary<string, object> { { "cornerstoneParam", cornerstone } } }
            };
            string paramsJson = new JavaScriptSerializer().Serialize(parameters);

            var form = new StringBuilder();
            form.Append("product=").Append(Uri.EscapeDataString(AliyunGatewayProduct));
            form.Append("&action=").Append(Uri.EscapeDataString(AliyunGatewayAction));
            form.Append("&region=").Append(Uri.EscapeDataString(AliyunRegion));
            form.Append("&language=zh-CN");
            form.Append("&params=").Append(Uri.EscapeDataString(paramsJson));
            if (!string.IsNullOrEmpty(secToken)) form.Append("&sec_token=").Append(Uri.EscapeDataString(secToken));

            string url = AliyunGatewayUrl(type)
                + "?action=" + AliyunGatewayAction
                + "&product=" + AliyunGatewayProduct
                + "&api=" + Uri.EscapeDataString(apiPath)
                + "&_v=undefined";
            var headers = new Dictionary<string, string>();
            headers["Cookie"] = cookie;
            headers["Origin"] = AliyunConsoleOrigin(type);
            headers["Referer"] = AliyunConsoleUrl(type);
            return HttpPostForm(url, ConsoleUserAgent, headers, form.ToString(), 15000, cancellation);
        }

        private static string AliyunOptionalRequest(string type, string cookie, string secToken, string apiName)
        {
            try { return AliyunGatewayRequest(type, cookie, secToken, apiName); }
            catch { return null; }
        }

        // 从 Cookie 串里取单个键（cna 等）；找不到返回 ""
        internal static string CookieValue(string cookie, string name)
        {
            if (string.IsNullOrEmpty(cookie)) return "";
            foreach (var part in cookie.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (part.Substring(0, eq).Trim() != name) continue;
                return part.Substring(eq + 1).Trim();
            }
            return "";
        }

        // 接口给的是「已用」比例，且是 0–1 的小数（0.1186 = 11.86%）；>1.5 视为已经是百分数，
        // 不做 0–100 的启发式猜测（两家语义不同，猜错就是把额度显示反）
        private static double? AliyunRatio(Dictionary<string, object> usage, string key)
        {
            if (usage == null || !J.HasKey(usage, key)) return null;
            double v = J.D(usage, key);
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;
            double ratio = v > 1.5 ? v / 100.0 : v;
            return Math.Max(0, Math.Min(1, ratio));
        }

        private static void AddAliyunWindow(List<QuotaPool> pools, Dictionary<string, object> usage,
            Dictionary<string, object> quota, string ratioKey, string resetKey, string[] totalKeys,
            string label)
        {
            double? ratio = AliyunRatio(usage, ratioKey);
            if (!ratio.HasValue) return;   // 这个账期没有读数就不是一条计量
            double total = 0;
            if (quota != null)
                foreach (var k in totalKeys)
                    if (J.D(quota, k) > 0) { total = J.D(quota, k); break; }
            int usedPct = (int)Math.Round(ratio.Value * 100);
            var pool = new QuotaPool
            {
                Label = label,
                Percent = Math.Max(0, Math.Min(100, usedPct))
            };
            if (total > 0)
            {
                pool.Total = total;
                pool.Used = total * ratio.Value;
                pool.Remaining = Math.Max(0, total - total * ratio.Value);
            }
            else
            {
                // 只有官方比例、没有分母：只报比例，不派生绝对余量
                pool.PercentOnly = true;
                pool.Total = 100;
                pool.Used = usedPct;
                pool.Remaining = Math.Max(0, 100 - usedPct);
            }
            if (usage != null && J.HasKey(usage, resetKey))
            {
                var reset = ParseTimeFlexible(usage[resetKey]);
                if (reset.HasValue) pool.ResetLocal = reset;
            }
            pools.Add(pool);
        }

        internal static List<QuotaPool> ParseAliyunPlan(string usageJson, string quotaJson, string subscriptionJson,
            string type = "qwen")
        {
            // 两家的字段完全一致，type 只影响失败文案里的品牌名。
            // 信封错误（登录失效 / 网关业务失败）必须先抛，不能被"没开通"吞掉；
            // 但"字段里没有 per1* 读数"是正常情况（没订阅），所以这里不做 markers 强制校验
            var root = AliyunEnvelope(usageJson, type, "Token Plan");
            var usage = J.FindFirstWithAny(root, AliyunUsageMarkers);
            var quotaRoot = AliyunOptionalData(quotaJson, type, "档位额度", AliyunQuotaMarkers);
            var subscription = AliyunOptionalData(subscriptionJson, type, "订阅", AliyunSubscriptionMarkers);

            string specCode = subscription == null ? "" : J.S(subscription, "specCode");
            if (string.IsNullOrEmpty(specCode) && subscription != null) specCode = J.S(subscription, "SpecCode");
            var quota = quotaRoot;
            if (quotaRoot != null)
            {
                var specQuota = string.IsNullOrEmpty(specCode)
                    ? null
                    : J.Dict(quotaRoot.ContainsKey(specCode) ? quotaRoot[specCode] : null);
                if (specQuota == null && string.IsNullOrEmpty(specCode))
                {
                    // 订阅接口没回 specCode：档位表里恰好只有一个可识别档位时才取它。
                    // 两个以上绝不猜——猜错就是把别的档位的额度当成你的
                    string only = null;
                    int count = 0;
                    foreach (var key in new[] { "lite", "standard", "pro", "essential" })
                    {
                        if (J.Dict(quotaRoot.ContainsKey(key) ? quotaRoot[key] : null) == null) continue;
                        count++;
                        only = key;
                    }
                    if (count == 1) specQuota = J.Dict(quotaRoot[only]);
                }
                if (specQuota != null) quota = specQuota;
            }

            var pools = new List<QuotaPool>();
            // 顺序即主次：当前账期必须排最前，否则悬浮窗报一个窗口、详情报另一个
            AddAliyunWindow(pools, usage, quota, "per1MonthPercentage", "per1MonthResetTime",
                new[] { "monthly" }, "本月额度");
            AddAliyunWindow(pools, usage, quota, "per1WeekPercentage", "per1WeekResetTime",
                new[] { "weekly" }, "7 天额度");
            AddAliyunWindow(pools, usage, quota, "per5HourPercentage", "per5HourResetTime",
                new[] { "fiveHour", "five_hour" }, "5 小时额度");
            if (pools.Count == 0)
                throw new Exception("该账号未开通 Token Plan");

            // 档位只作为备注挂到主计量上，不当成第二个窗口（配置值不是额度）
            if (!string.IsNullOrEmpty(specCode)) pools[0].Note = specCode;
            return pools;
        }

        internal static ProviderStatus FetchAliyunConsole(ProviderConfig p)
        {
            string type = p.Type;
            string brand = AliyunBrand(type);
            string cookie = ConsoleCookie(RequireKey(p));
            if (!IsAliyunCookie(cookie))
                throw new Exception(brand + " 需要控制台登录 Cookie（含 login_aliyunid），请点击「自动获取」");
            string secToken = AliyunSecToken(type, cookie);
            string usage;
            try { usage = AliyunGatewayRequest(type, cookie, secToken, "usage"); }
            catch (HttpException he)
            {
                if (he.StatusCode >= 300 && he.StatusCode < 400) throw new Exception(AliyunExpiredMessage(type));
                throw;
            }
            string quota = AliyunOptionalRequest(type, cookie, secToken, "quota-config");
            string subscription = AliyunOptionalRequest(type, cookie, secToken, "subscription");
            return AliyunStatus(p, ParseAliyunPlan(usage, quota, subscription, type));
        }

        internal static ProviderStatus AliyunStatus(ProviderConfig p, List<QuotaPool> pools)
        {
            var st = new ProviderStatus { Name = p.Name, Type = p.Type, Region = p.Region, ShowName = p.ShowName, UpdatedAt = DateTime.Now };
            st.Pools.AddRange(pools);
            st.Primary5h = pools.Count > 0 ? pools[0] : null;
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
            PromoteExhaustedWeekly(st);
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
            PromoteExhaustedWeekly(st);
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
            PromoteExhaustedWeekly(st);
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
            PromoteExhaustedWeekly(st);
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

        // ============================= 更新检查 =============================
        // 检查结果：远端版本号（已去 v 前缀）、是否比本地新、实际应答源的发布页地址
        internal class UpdateInfo
        {
            public string Tag = "";
            public bool Newer;
            public string PageUrl = "";
        }

        // 同时问 Gitee 与 GitHub：各 15s 超时并行跑，谁有结果算谁的；都有结果以 Gitee 为准。
        // 单边失败静默（另一边成功时不暴露失败细节），两边都失败抛主源的错误。
        // 内部再排线程池并行两路请求后阻塞汇总，调用方需在后台线程执行（勿占 UI 线程）
        internal static UpdateInfo CheckUpdate()
        {
            // GitHub 强制 TLS1.2+，而 ServicePointManager 默认值在部分机器上不含 1.2（握手直接
            // 失败）。Program.Main 已全局设置过，这里幂等补设：不依赖调用方初始化顺序
            try
            {
                if ((ServicePointManager.SecurityProtocol & SecurityProtocolType.Tls12) == 0)
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }
            var answers = new UpdateInfo[2];
            var errors = new string[2];
            int pending = 2;
            using (var done = new ManualResetEvent(false))
            {
                for (int i = 0; i < 2; i++)
                {
                    int idx = i;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            answers[idx] = CheckUpdateFrom(
                                idx == 0 ? AppInfo.GiteeApi : AppInfo.GitHubApi,
                                idx == 0 ? AppInfo.GiteePage : AppInfo.GitHubPage);
                        }
                        catch (Exception ex) { errors[idx] = ex.Message; }
                        if (Interlocked.Decrement(ref pending) == 0) done.Set();
                    });
                }
                done.WaitOne();
            }
            if (answers[0] != null) return answers[0];
            if (answers[1] != null) return answers[1];
            throw new Exception(errors[0] != null ? errors[0] : (errors[1] ?? "网络请求失败"));
        }

        // 单源检查：请求 releases/latest 并解析 tag_name。404 / 网络失败 / 解析失败均以
        // 异常抛出，由 CheckUpdate 视为该源失败
        private static UpdateInfo CheckUpdateFrom(string api, string pageUrl)
        {
            string body;
            try { body = HttpGet(api, null); }
            catch (HttpException he) { throw new Exception(FriendlyUpdateError(he)); }

            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body); }
            catch { root = null; }
            if (root == null) throw new Exception("接口响应解析失败");

            string tag = J.S(root, "tag_name").Trim();
            if (tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V')) tag = tag.Substring(1);
            int[] remote = ParseVer(tag);
            if (remote == null) throw new Exception("远端版本号无法解析：" + tag);

            int[] local = ParseVer(AppInfo.Version);
            return new UpdateInfo { Tag = tag, Newer = local != null && CompareVer(remote, local) > 0, PageUrl = pageUrl };
        }

        // 更新检查场景的专属错误翻译：限流（401/403 + rate limit，两平台匿名配额均为 60 次/时）
        // 走 DescribeHttpError 会得到「登录凭证失效」的误导文案；404 在此场景是「没发过版本」
        private static string FriendlyUpdateError(HttpException he)
        {
            if ((he.StatusCode == 401 || he.StatusCode == 403) && he.Message.ToLowerInvariant().Contains("rate limit"))
                return "请求过于频繁，请稍后再试";
            if (he.StatusCode == 404) return "仓库尚未发布任何版本";
            return he.Message;
        }

        // "1.2.10" → {1,2,10}；任何段非数字、为空或超 4 段返回 null（解析失败）
        private static int[] ParseVer(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] parts = s.Trim().Split('.');
            if (parts.Length > 4) return null;
            var v = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], out v[i])) return null;
            return v;
        }

        // 逐段数值比较（长度不齐补 0）。版本号必须数值比较：字符串序会把 "1.1.10" 判成小于 "1.1.9"
        private static int CompareVer(int[] a, int[] b)
        {
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
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
                f[0] = new Font("Microsoft YaHei UI", 9f * dpi / 72f, FontStyle.Bold, GraphicsUnit.Pixel);
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

        // 悬浮窗背景与详情面板共用主题色，其余颜色由各自的调色板管理。
        public static Color Bg { get { return SettingsPal.For(Theme == 1).Win; } }
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
            if (type == "mimo") return t == 1 ? Color.FromArgb(251, 146, 60) : Color.FromArgb(234, 88, 12);     // 小米橙
            if (type == "qwen") return t == 1 ? Color.FromArgb(192, 132, 252) : Color.FromArgb(124, 58, 237);   // 千问紫
            if (type == "bailian") return t == 1 ? Color.FromArgb(45, 212, 191) : Color.FromArgb(13, 148, 136); // 百炼青
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

        // ---- 横向徽标（条形灵动岛）----
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
        // 几何：内圈（图标）恒 18 DIP，ringD 为环外径 DIP（灵动岛 38），penW 环粗 DIP。
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

        // 绘制单个徽标（centerY 为垂直中心线，textY 为文本行顶），返回徽标内容宽度；调用方自行叠加徽标间 gap。
        // fName 名称粗体 / fVal 数值常规，与卡片行同款层级
        public static float DrawBadge(Graphics g, BadgeInfo bi, float x, float centerY, float textY, float s, Font fName, Font fVal, int ringD, float penW, bool dark)
        {
            if (bi.IconType != null)
            {
                if (ringD <= 0)
                {
                    // 无环模式：空间紧凑，只显示品牌图标本身
                    int isz = SiF(18, s);
                    BrandIcons.Draw(g, bi.IconType, dark, (int)Math.Round(x), (int)Math.Round(centerY - isz / 2f), isz);
                    using (var b = new SolidBrush(bi.DotColor))
                        g.DrawString(bi.Value, fVal, b, x + isz + SiF(8, s), textY);
                    return BadgeWidth(bi, s, ringD);
                }
                float outer = SiF(ringD, s);
                DrawRingIcon(g, bi.IconType, bi.RingPct, bi.DotColor, x + outer / 2, centerY, s, ringD, penW, dark);
                using (var b = new SolidBrush(bi.DotColor))
                    g.DrawString(bi.Value, fVal, b, x + outer + SiF(8, s), textY);
                return BadgeWidth(bi, s, ringD);
            }
            float dot = 8f * s;
            using (var b = new SolidBrush(bi.DotColor))
                g.FillEllipse(b, x, centerY - dot / 2, dot, dot);
            float tx = x + 8f * s + 6f * s;
            using (var b = new SolidBrush(Text))
                g.DrawString(bi.Name, fName, b, tx, textY);
            tx += bi.NameW + 10f * s;
            using (var b = new SolidBrush(bi.DotColor))
                g.DrawString(bi.Value, fVal, b, tx, textY);
            return BadgeWidth(bi, s, ringD);
        }

        // 按当前数据与字体测量所有徽标；totalW 为去掉末尾间距后的内容总宽（岛宽计算与绘制共用，
        // 两端必须同源测量，否则灵动岛按此定宽后徽标会被裁切）
        public static List<BadgeInfo> BuildBadges(List<ProviderStatus> providers, Graphics g, float s, out float totalW, int ringD, float penW, bool dark)
        {
            float gap = 12f * s;   // 徽标间距（灵动岛绘制处与此必须同源）
            var fs = FontsForScale(s); // [1]=FNorm
            var badges = new List<BadgeInfo>(providers.Count);
            totalW = 0;
            foreach (var st in providers)
            {
                var bi = new BadgeInfo { Name = st.WidgetName };
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
                if (!st.ShowName && BrandIcons.Get(st.IconType, dark) != null)
                {
                    bi.IconType = st.IconType;
                    bi.RingPct = err ? 0 : remain;
                }
                bi.NameW = g.MeasureString(bi.Name, fs[0]).Width;   // 名称粗体，与绘制同源
                bi.ValW = g.MeasureString(bi.Value, fs[1]).Width;
                badges.Add(bi);
                totalW += BadgeWidth(bi, s, ringD) + gap;
            }
            if (badges.Count > 0) totalW -= gap;
            return badges;
        }

        public static string FmtNum(double n)
        {
            // 最多两位小数：MiMo 套餐按亿折算后会出现 10.25 这类值，整数额度（GLM/Kimi 计数）显示不变
            return n.ToString("#,0.##", CultureInfo.InvariantCulture);
        }

        // 金额格式化：保留两位小数 + 千分位
        public static string FmtMoney(double n)
        {
            return n.ToString("#,0.00", CultureInfo.InvariantCulture);
        }

        public static string FmtCountdown(DateTime? reset)
        {
            if (!reset.HasValue) return "--:--:--";
            var diff = reset.Value - DateTime.Now;
            if (diff.TotalSeconds <= 0) return "00:00:00";
            string core;
            if (diff.TotalDays >= 1)
                core = string.Format("{0}天 {1:D2}:{2:D2}:{3:D2}", (int)diff.TotalDays, diff.Hours, diff.Minutes, diff.Seconds);
            else
                core = string.Format("{0:D2}:{1:D2}:{2:D2}", (int)diff.TotalHours, diff.Minutes, diff.Seconds);
            return core;
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
            DrawBar(g, r, pct, fill, BarBg);
        }

        public static void DrawBar(Graphics g, Rectangle r, int pct, Color fill, Color background)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            using (var bgBrush = new SolidBrush(background))
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

            if (!st.ShowName && BrandIcons.Get(st.IconType, dark) != null)
            {
                int isz = SiF(16, s);
                BrandIcons.Draw(g, st.IconType, dark, SiF(12, s), y + (SiF(24, s) - isz) / 2, isz);
            }
            else
            {
                using (var b = new SolidBrush(Text))
                    g.DrawString(st.WidgetName, fs[0], b, SiF(12, s), y + SiF(3, s));
            }

            if (err)
            {
                using (var b = new SolidBrush(Dim))
                    g.DrawString("失败", fs[2], b, width - SiF(36, s), y + SiF(6, s));
            }
            else if (pool != null)
            {
                // 金额模式（DeepSeek 余额）显示金额，否则显示剩余百分比；与名称同字号常规体，基线天然对齐
                string rightTxt = pool.IsMoney ? pool.Currency + FmtMoney(pool.Remaining) : (remain + "%");
                using (var b = new SolidBrush(dotColor))
                {
                    var psz = g.MeasureString(rightTxt, fs[1]);
                    g.DrawString(rightTxt, fs[1], b, width - psz.Width - SiF(12, s), y + SiF(3, s));
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
    // 内嵌透明底 PNG（既有图标为 64px；千问 / 百炼从官网页头 SVG 提取图形，以 128px 内嵌）。
    // 千问 / 百炼矢量原件、黑白变体及来源见 assets/brands/；运行时仍为单文件、零依赖。
    // 样式随主题自动切换：浅色用品牌彩色源（Codex、GLM/Z.ai 等无彩单色源随主题染色），
    // 深色优先用专用变体（Kimi 白 K+蓝点、百炼灰白分面），其余单色源染白。
    internal static class BrandIcons
    {
        class Def { public string Mono, Color, ColorDark; }   // ColorDark：保留明暗/配色的深色变体
        static readonly Dictionary<string, Def> _defs = new Dictionary<string, Def>
        {
            { "glm",    new Def { Mono = IC_ZAI } },
            { "mx",     new Def { Mono = IC_MINIMAX,  Color = IC_MINIMAX_COLOR } },
            { "kimi",   new Def { Mono = IC_KIMI,     Color = IC_KIMI_COLOR, ColorDark = IC_KIMI_COLOR_DARK } },
            { "ds",     new Def { Mono = IC_DEEPSEEK, Color = IC_DEEPSEEK_COLOR } },
            { "claude", new Def { Mono = IC_CLAUDE,   Color = IC_CLAUDE_COLOR } },
            { "codex",  new Def { Mono = IC_CODEX } },
            { "mimo",   new Def { Mono = IC_MIMO } },
            { "qwen",   new Def { Mono = IC_QWEN, Color = IC_QWEN_COLOR } },
            { "bailian",new Def { Mono = IC_BAILIAN, Color = IC_BAILIAN_COLOR, ColorDark = IC_BAILIAN_DARK } },
        };
        static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();

        /// 取指定主题的源图（缓存按主题区分）；未定义的类型返回 null
        public static Image Get(string type, bool dark)
        {
            string key = type + "#" + (dark ? 1 : 0);
            Bitmap b;
            if (_cache.TryGetValue(key, out b)) return b;
            if (type == "custom")
            {
                b = new Bitmap(64, 64);
                using (var g = Graphics.FromImage(b)) IconFactory.DrawServer(g, 4, 4, 56, dark ? Color.White : Color.FromArgb(24, 24, 27));
                _cache[key] = b;
                return b;
            }
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
            if (type == "custom") { g.DrawImage(img, x, y, size, size); return; }
            Def d;
            if (_defs.TryGetValue(type, out d))
            {
                bool usedColor = dark ? !string.IsNullOrEmpty(d.ColorDark) : !string.IsNullOrEmpty(d.Color);
                if (usedColor)
                {
                    g.DrawImage(img, x, y, size, size);   // 品牌彩源原样（含渐变/多色）
                    return;
                }
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

        // qwen: official wordmark symbol; SVG sources and attribution in assets/brands/.
        const string IC_QWEN =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAAQAElEQVR4AeydBawGORHHH3DoocEJ7u5w6MGhB8HdXYJLCO4uwTVw" +
            "uLsHDnc5IEEvuLu7JNj/d/eat2+/drftN+3ufl9fZl53u+102p2tzEz7HXOn/W11CzQB2OrXv7PTBKAJwJa3wJZXv/UATQC2vAW2" +
            "vPqb1gOcRe/zFsJ7Cy8hbDDSApsiAGdTPT8s/L7wtcJnC48Q/kh4PWGDXgu426ULwPFUkScIjxReSdiHMyri7cL3C88sbNBrgSUL" +
            "wCGqy3eEDxUeRzgEV9fDHwgfIWzQaYGlCsDNVIePCE8vTIHHKvGrhQ12W2CJAkC3f9gu/znBrZTpGsIGaoElCgBj/YHifR241jqZ" +
            "NynvEgXg1AYvgOWiAZnlk1iiAFjwfKzlvzqbGlg0pg0njUqVFugX0gSg3yJbdt8EYMteeL+6TQD6LbJl900AtuyF96vbBKDfIlt2" +
            "v0QB+I/BO/qfAQ1rEoeK4JOE7xY+UYj9QkFZWKIAWKzhj1G2WZOoX0ypvyh8n/DBQrSUD1GIBfPTCs8rLAZLFIBijVGZ8KlU3iuF" +
            "XxAiBApW4DKK+YrwGcITCrMhlHGJAvDfUGUS4qceAjBhY56+jXge640OUJr7CTF9IxC6tIMlCoBd7etTuq6K/J4QJ5YTKEyB0ygx" +
            "Xk+XVmgGSxSA2l/vudXaDxS+Tfgi4e2EJxamwHmU+APCdwjPKswFTOHMD3Lzr+RbogDUmgTykp+rFvu68CnC6wvvKny58FtC/AoU" +
            "DMJJ9PRZwq8Kryq0ANPVwRIFwKIRx2jcVgkYc++p0CdwdMd4FoVm6bQrwvJd5b+PkHFcgQng/nYiE0oiAqMKFgUlJ4EnV0swzr5C" +
            "IbN0BYPApOwbSvFwoYODdfFlIcPFKRSWALNhcIkCYFb53puhy/+U4vA4UpAEj1Nqhos3Kvy48ALCRcCSBABN2QfVqi8UloBbiygT" +
            "PgVZwHBxk6ycBTONkV6CAFxKleDLRFN2FV0zE1ZgDlc0p1iOoFkvOGcBOKfa763CzwovK7QEn/LlZJYFFKT1D9H+m9AE5ioAzMJZ" +
            "at3ApJarRMy+oFXSxWMwFpkVMkcBuKZqxyxcQYNeC6A+Ri3ci86/naMA3Cu/Ohud8xeq3dWEPxeawRwF4MJmtQsT8s0BwqmnffJP" +
            "FY/t4BwKUSwpsIM5CkARs6ddk1WlhF6BF4+iyWzi163BHAWgy9+2Xn9JFWf5yybYn+o6GWIz5AoAa/EHqZCPCv8sxGnheQqRVgVr" +
            "gYWqd4yBua4CfinGWQFdXOHnhcUhRwA4ceOb4uzJQpQnGCYuqOt7CNGLP1MhcQqyIIen1ILmNgdgbc/WdU46eVVqZdZJn9LY2LEP" +
            "V2GcuHEmhT44tiLvK+SoljsrzAELp8+xcufUAzDOo/R6lJj+u7AqxAoAtvCviTOWIQpGASvYi5UKO/jlFKbAb1MSLzgtjqBVxvmh" +
            "NooRgMuLAN4wqS5MyraDVeyTukDKY0/zwHNGWTYWfqaa4UzCKWZVxnmVF4QYAWBsDxKIfICV7CdKyzg3JkgseZhLKPlGAeP8o1Wj" +
            "sws5yUzB9BAjABc1ZJNDmnCKvPkAzd/r2UFCXKYVbATgPcQE7zGqDYodBfOAGAE4rjGruFO9TjSx8l1EoQ9QeuB8Waqb9E0CfXE+" +
            "3lLiPqfE1AH3b1S5ui0LqdRjBCAmTWq5pGcChMLjpboJuV+5iRKnfzKEKKkJ/NpD5TeeuNyoHysjvRwu3NRBt/OEmJdbUjHDevwO" +
            "ahocMO+vMASv1wO3VGIs1e1a8B5Pbpa4nuikKHhjmDuXcr1BGAIE/kKhhzXjYwSgBj/44z1dBaE/uLZCHzB2MolkLF1HWULeN3sK" +
            "YM6BjsPzKCoKuvD2eKWGVwUrgIaPPX+/0hMcR9H8mZp3RTcJYgSghmLGMc3pXe/SDUvBkFqZsdSpSz+jtLHAS2GFQd5QHhxQUMiQ" +
            "NpSmH4+7Gi8WuvDWf879afUPAWMfYNevnxPP2PfH8HZ7pakOMQLAurU2Y2yi+LYKfY7wpEIfMH/AVYyxlgb0pXFxKKUQLsyqLi4U" +
            "0sug9XxZKMFuPAdR31jX6EngRZcrwJKXpR8rHyaCKwl2I9CRUB42FdTru9HlgxgBYAlTnhN/CTiHMCywycKfYmeHsdbND/7aS/QW" +
            "3bPuJj/drW6jgC/5jkrJWN4fFv6ieHoSDp+Gvm69gJcxL54e5fjeFKuR2FQwsLGZZPVpgZgYAeDrYTtUgeKjSOKsySYLVNGhr4Mu" +
            "my+XL+mWokoDIhR8obwERWUBvRDDAu7i0KS3OZ0oDfUkzPzp6pkTsORV8mRgOxmbTpIzpmaIEQBoMlNn4wPXU+H5VTBfB2rlkDHq" +
            "T0qDjoGhg5WFbk0AB1Vo0tv0exlXAMKHho95CXMCF58b3jQlY27aWAGAPr/CgW6fU7q5nwpRK/9QhbMbhzFWl5MCPND7MA9BX2HF" +
            "DDuKrWgF6aQIAETYKXtlXXCMCd2jLicDxmG+crr5qZhguMFPj7W/NQ8IljXNFXqpAuAIvFcXTJCYpP1B11MB4/GbVDg/EaOgKjxf" +
            "pb1GyBJPwTIhVwBcbXEDY8nEpMXFTREyPPE11ir7Tiro7sLFw7oCQAP8Uf/QZqEF6y+Z9Kga4EBZqzDLsb4Wz95yLATAEWa9zpKJ" +
            "pRqeQC6+VsgEtVZZIS2lZfn/tiQWomUpAK4M9sdj6GDpiELFxZcO8UcsXYajX6LdHG0Xoml018XCkhVBeYQWjiVSsQpsMGGMRqPV" +
            "WzdBSQGAN7xcUYUO6cFJ13C1BfCTKK7rKC0ArlrtV7pcS8SH2A/QdaBnwNIYnzMhZS0BQE2awFZW0ppm65ploWdguzyWwitktcxA" +
            "ploC4DtqbYCtrEeoh7MyZmSawkSOpfBj4vWdQpbcCtaHWgKwPqfjFPhKxlPZpJjSRH4dVYFhAUeSkK+EksRBLQHA9y+Oo7xUT1M2" +
            "HCoUVIEXqBTM5AomA5Rv6F44nSybiVoCUMLlmkrj5IGNnrN8ua+JOJmwwwceapbbLQtfCcz0mKvpGbrPoq5rCQDeslEMJSRikwVj" +
            "ITb6hGymSbH/w8OQg4hpgY5YL8T5hbkBvpTn6z0bvK0lAJhtBxlJeIinzRmUHl879Ay6nBTggeUaLmJYJqdkBl9KTPZYKjnDYZSX" +
            "WgKA1XDdXoCdROyyYU0cOjWD07kx1GAd5KsYbYDIBJi+oYnBKXSEDapbvHg4apaXEEm6SDIslR8S5VGfgloCwGZPDEXiKRlYcvFS" +
            "8ZEL7bJB2h8pyggG3TL+AYyL+P+vIwjk5bBKDsSAJhtUOKWLsihTRa4AbmsYpthUO6WvBB7TzFNWGOxG1BIAykS3zddBY3I/hvQY" +
            "eNpgT6DhQ+nxA8A7iTlB/+u8kTIhCDiVnlLXsYC/PrN88vYFl9NPKIsyKTtEk5UC8wO641Ca0vGcuThYRk0BgBG+DnzdsBTytRLn" +
            "Q8Z5Gm9slw3n9eOVw5zAR8fF8SWgKIr5tQ3Gc9KOnXBCmZTNxpDQjz7RA7BMo84cdO34qRWyF2KwrNoC4JjBUohN/QGK6G7UxNUM" +
            "j1rG+ZApGdUoAoLrNcOCSEQB4yG/xzekxMHjGANMqHv3FURXy9DEzh9486Wh1+N0Fc5X4rRPX5oScaMm8qkEgMriy89+QLpbDock" +
            "xNk0tMuGl4JlET9/Nl1AIwdZu/uMUzcUMTyOFWQBFk94G5ofsFTDhY4T1lg9ZBVkmWlKAejWA0NHtyfoPuOasZalJEs/rGTErYMI" +
            "Wj9/aFNqP93QPbzFzA+eKiIMccnaS+UzhbkIQKhSnCFwhB4y1lpaFPkKRXYfhLrvfYkib2LmB2gQ2X7GkMcSN5K0bbK5CgAvmx0+" +
            "NAxrf9ta7+zUqrebHzDnYQ7iqwdDHnMZhhCEwpcmN46V1GDeWg0xyETnIY3EJIxdNuj4O48WfclxN5yWxhIyVBEmpwwLli50R4YK" +
            "c/FzEgBm/kyiWIY5/kqFx/AQ9sV5kmVHcdgWq54hAkwMmehaqZWxkg6VV60rHGKC7o9lFPb83N20Q/R9z3zWSV+cL+86cb7Jp4+e" +
            "UysfooejX7HS9IGuH+sgQ2j/2b77KXsAxnk0fChzQoqUfcxuwI1v8jlULTyAsO7dTYlQKikYBX5ci53UUT8tM4UAHKgqoOFjWYdx" +
            "RbfVwdfd12iLXNc4VNlo9diiHtowgvYSRRPqXxxFohq1RqW7jLDsQRP2MEWi2FEwG6gxBGRXVhk5+4BDKjA0cXSvoo4Cjutn3oC6" +
            "GUXTUZGx/2oJAMshTsU6TIylGGWUvBr4eoVqhScUhFoZrSX8gpjAWTmgWU0gc3TS0gLASR44SWAwYbvY0aW2/7NpgVICwDiP4QVz" +
            "6pQHOMymoXcZYSf17uU8ghICcLCqxnoe06v1OcMibQK+8b7kiaiO6dFlmUtYK7QWALaGszsYy16tOiylHJZxOLjMil9rAcBaN6sK" +
            "Bphh8tR/ZN0WXfpM0A5VBF5ECuYDlpWmUZntz6d28+CEwyRRzGDVnAdHHS4sBQB/vAM6tOd8SXfc5896DsBQeEkVwiSYOZEu06F0" +
            "DksBKM2rJX1+yMGSnqNFV487NgdCMx/Cbc09m2W4RAHATwDdQm6DcuTsS3Izj+TDH5/NGezQGUk6j8dLEgA0ifwEHe5hqJRx205t" +
            "RU45ZZkaOu41ld7i01sKgCWtbsPyUy53UQT2dCyHutzhBeLqjaWMs3mJG0I8bXAk5ZTTkDLGgn8mwkN8zO6ZRaVdpTBK+BQs7nlq" +
            "iNWLAyjxkqHL9tHGVs7KA48bn1MpJ3mwo4etXfgVpvKw8ektBYAXhH+bRaMxkeLrZg885/OP0cQnn5eMUYTZN4ivAadq8FO2COcY" +
            "DYtVAG0wVs6snlsKABVbd58+pmI8WZhIpSpN6NoxizL7BtlPSA8BXw0DLWAtAGz9YpLFjyIFivRG4wvHZgk8ZqI8WbxUNiiyVlWs" +
            "BQC+8X5lVy3eK4zBxIWQLhNfQLxd2CwRSlcj3qIttnoS2H1JjLl4rzBzD6lAmS/g88+vZfkmcF167bpQC1hI/RBrHBp9kBJwcALK" +
            "ESZnhOzBY0cMQqDHs4A2CSz4GtDcOfUoIQc3FCwui7TFx9CGgKymb5kmawELqZ+M+Vbw+i3QBGD9Nlw0hSYAM3t9tdlpAlC7xWdW" +
            "XhOAmb2Q2uw0Adhr8TGt5V7K8JWFLiFMvcCTJgB7jfq7vcvsK3wXsjNPkbEJwF6ro6Hcu0u/wmz9ifRs0+ZoArDX/vgU4MK9F5N2" +
            "xQknCEFarolTNwHY/wJw4canYH/s8B0nnvJbPlP+auowhwNPmwCsNg5eRZxPjAPp6tO9mH/pkoMuOPF07a5ftCaBJgD+ZmcjBw6k" +
            "HDbNiWX9VIcrApc19vqxF0C3y4QmAMPvjaPicW7h4GhOAAU5uIl9fgjJcO4FPG0CMP6S+MI52YSNryAHN43nWkiKJgALeVGl2GwC" +
            "UKplF0K3CcBCXlQpZT6vnAAAABZJREFUNpsAlGrZhdBtAjDxi5q6+P8DAAD//+frU9gAAAAGSURBVAMARkUxHzUZKeMAAAAASUVO" +
            "RK5CYII=";

        // bailian: official wordmark symbol; SVG sources and attribution in assets/brands/.
        const string IC_BAILIAN =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAAK00lEQVR4AeydB6wuRRXHP7uJxog9xooNsMdegoogalQUERVbQAHF" +
            "hmIFa0RUVKyoICoWQEUsqBFQFAvYu1HBhljQiC1WbPj7vbzNe+++e+/3fbOzuzO75+Z/7pltM7Pn/L8tszNnLj6Lv0lbIAgwaffP" +
            "ZkGAIMDELTDx048rQBBg4haY+OlP9Qpwbfy+C/KkjWLadSxOC1MiwK1x7RHIRch5yGnIGzaKade57TmsuwoyajQnNwUCbMPJHoqc" +
            "hTwNmYeXsYP77ocePcZOgMfiwTORQ5DLIoviRux4FHIK4u0BNU6MlQA74a6PI8cg2yOp2JUDvT28Cb0tMjqMjQA66Ui8dDpyXyQX" +
            "nkBG3haeiR4VxkKAi+GVZyBe7g9Ad4Grk+nhiETYHT0KjIEAD8YTOv6V6GsgXeNOFHASchxyS6Rq1EyA22D545EPIDoF1Sv2orQv" +
            "IS9BLodUiRoJcCUs/VLEX/3D0UPCN4vnUYGvII9BisfKCtZGgMdxAjr+YPRlkFKwAxU5FvkochekGtRCgJ2x6CeQtyLbIaXiflTs" +
            "C8hrkD6eRyimHUonwA04Pd/BP4m+N1ILDqSiX0X81oAqF6USwHr5zu3l3nfwci24ds2uxSa/NZyBfhFSJDR0aRXbgwr5ru07t+/e" +
            "LFaNu1H7FyK/QR6EFIWSCHBbLHMCciJyB2RskMwf5KRsWr4xugiUQIArY4nDEC/3D0OPHX5cOpuTfBXiayRqOAxNgH05dS/3z0Vf" +
            "GpkSDuJkL0AejXSOtQoYigD+CvzUejQVK+ZySF36hi2I76RQfwQ7ontH3wS4IWf4ZsT7oJ9aSQawgE3Zn0W/Bbku0hv6IsAlOKNn" +
            "Id7nH48OrG6B/Vl9LuLtwS+cJLtFHwTYk1PwEvcK9NWQwHwL+ID4bXZ7CNIpuiTA7aj5e5H3IbdHAstZ4Obs/n5E+3X2WtwFAexR" +
            "a8dKL/cP5QQC7SzgFdTPzp30d8hNAO9hXu7tWn2pducdR6+wgD2evsi6rN8XchFgbyp2KuJTrD1qSQY6sMD1yLP5vvAA0nMxb4cc" +
            "BPA+9XYKuhcS6McCfl/4CEX5w0Oloy0B7Dvf+ZNq+umN/sgXtD3DNgTYh8InMXqG8ywV3hLu3qZyqQS4BYW+DQkMa4FzKN7+Bqg0" +
            "pBLgUWnFxVGZLWCH1FZZphDA17sgQCuztz7Y7wa7kYt9J1DpSCGAzbl2bkgvNY5MtYDfCZ7Mwd73T0a3RgoBrtq61MhgWQv8jwP8" +
            "PnBn9BuRuVh0hxQCXHHRzGO/LBawnUXH20n2/Cw5bpZJCgE2OzySHVrA9n+/A/g95ctdlRME6Mqy6fn6K/fXbieR1g9586oRBJhn" +
            "oX63287vp1/v972UHAToxcxzC7Fd3/b9p7DnL5DeEATozdSrFvQt1tor+IHozyG9IwjQu8k3FPgH/tuKZ+i6d5MeDEGA/k1vV3hH" +
            "QRnjIHvpy2YYBFjWYun722Hmnhxur6mfoYtAEKB7N/yAIoxX6PD2T5MuCkGA7tzxd7I2fpCfzu0xxWJ5CAJ045N3ka0RxOyx8x/S" +
            "xSIIkNc1ds6wb6QBo36cN+tucgsC5LHrT8nGh7t7oA1ng6oDQYB2fvovh78cMUqYr3ck60IQIN1fDtm6GYcb2+BC9KBILTwIsLzl" +
            "HJ1jIGqjmfxw+cPLOiIIsLg/fs2uT0TsnGHMQpL1IwiwmA8dmGkkE2MWLnZEJXsFAdZ3lIGoHaZtcIu/rb9rnVuDAKv77eusvj/i" +
            "sLfvoUeLIMCWrv09i3bK8Gvdx0iPHkGATS42wLOxie2WtWntyFNBgNnsw7PZ7Faz2ezpyJ+RqtC2skGAthas/PggwGxmfzz75r16" +
            "NptdHpkUggCb3O0twJ46WWPwbMo+e8rxgYahd+RQ8mDdIMCWfjHCmQ+BzgFkc++WW8tY8g3lPVTlM4hh6H1Vtf+BZGDVcggCrG4v" +
            "Yxw686hxDn0zWH2vftc6KNcWSb9FPGKVoiWDEUNW2bT2qiDA2rZxi+Py7Nhh3EOXhxInvf4ahRsq7pLotRAEWMsyLdcb9/BX5OGs" +
            "ZajeIAH9xR9BiddBsiOuAIub9Jrs6qxln0d3PTWc7RLOLuIt6I6UtxVyrQgCLG/Ju3KIU8P54LUN6ZxwBhEDQHyTTHuZXygIgKUT" +
            "4atXM8QrMYstDvP+7sBQ+xxssaHLhSBAe+va99/2g90Ts3KySUPD+4Tva2hiNmmHBQHS7LbyKJ++T2Ll6ch2yCJw9hR7FjndrINH" +
            "Fjkm+z5BgLwm3YnsHApmYxLJNfFatvwIcbgYajgEAbqxvc3J/yLrlbOeOl3OX1n/VKQIBAG6c4MBNe1DaI8i2+2/S1FOmOVMYSTL" +
            "QBCgez/clCJst3cMAcl2yH10ECC3RSvLLwhQmcNyVzcIkNuileUXBKjMYbmrGwTIbdHK8gsCVOaw3NUNAuS2aGX5BQEqcVhX1Uwh" +
            "gJ9Au6pP5NvOAkv7JoUAv2tXxzi6Qwss7ZsUAlzACfwWCZRlAX2ib5aqVQoB/k0JgwY4pvzA1hbQJ/pm6y3rrEkhgNlZmDqkHAsk" +
            "+SSVAN/hvIfuK08VAhst8GK0PkEth1QCWMrB/PsUEhjWAo5gShoWZrXbEMDjd+HfAUigIwvMyfbZbLdTKSoNbQlgqfZy6aUPu4WF" +
            "bLDA9/m/F3I40go5CGAFjLJxBRJHIoHuLNCEoHdk0gk5islFAOvyF/7ZGXJ79IeQQF4L+JRvkEpD0P8pV9Y5CdDUyfCpDpLYmRWO" +
            "s0cFWljA2cR243hnF3MACcl86IIATe0cJOEkiMbOd2Rtsz70Yhb4ObvZfdz5BE8m3Qm6JEBTYQdRbsuCr42owAIWcDi49/nXL7Bv" +
            "q136IIAVdJCEDUfXZyEeFDHCGnB4mY4/iO29XDX7IgDnswHn8t8HRYdYx4MixtgIQ9P6WrcHy2chs76kbwI053UmCR8U90RP+UHR" +
            "r3feGn26z/Jahz2XwlAEaCp5IgkfFA9E93LJo5xScBQV0fHeGr1Fstg/hiZAc8avI2FkLsfIkxw1TuHsnFnMgaKOEGZxOJRCAC1w" +
            "Pv+My28cvONIjw22j+zLSd0HKWZmsZIIgF02wAeiR5Ky8cOATCSrxj+o/aGIl/tj0EWhRAI0BrLxY0cWjJH3S3SNMKKnjn8+lf8j" +
            "UhxKJkBjLKNp+G48L+pGs38J2iuXX0gNJGUg6hLqtGodaiCAFT+Pf87k4cycJc/kYT19o/HK5RdSqr0c+t67FgI0djmDhHP57IP2" +
            "mziqGDjjiFcq32iKqdS8itRGgOZ83kFCY/tw9U/SQ8IWTVs2DTdf3bNKrQTQ4X4T9+HKh6zjXdGzfIPyjNpti6YtmyzWh5oJ0Fjb" +
            "sKo6wnZ0Ays367vSzix2CJkPRTyKzocxEKCxRvMlzcYkR8k063Pqo8nMW89h6AuR6jEmAuiMi/hnc7JOsrMqi1lwKrnsiuyPnI2M" +
            "BmMjQOOYn5Cwu7pt7jqPxSTo7P040oiep6FHh7ESoHGUbe46z1/uMh9efLPwMu993jkCmvw600NlPHYCNHb13q0zfXgzlm+zfqU+" +
            "hxWGbW/2XXq8PcdXhakQQKfY+cJf9Q4s2KJoDxyd7UPj3qxzlrCboJ0/0DcLkuPHlAiwuTdtUbQHjs72ofFYNhq6HTUtTJUA0/Ly" +
            "OmcbBFjHOFPYFASYgpfXOccgwDrGmcKmIMDAXh66+P8DAAD//xvHY7wAAAAGSURBVAMAafhXEKPWKngAAAAASUVORK5CYII=";

        const string IC_QWEN_COLOR =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAAQAElEQVR4Aex9CZhdVZX1Wve9qoRJwAkZhEBCwtjaMgiiEFREFAds" +
            "RQURsFtRRAY1zIQEBBtBBIRuxO4GQZrBVpr2txv9VeRvQQTSIhIkk4RRxLadIaTee+dfa99735RXqSS8VFJNne+su/beZ599hnv2" +
            "va/qpSDDeHle78D4AXhe335g/ACMH4Dn+Q48z5c//gQYPwDP8x14ni//f9UTYMPd09brvTYd8oLXpmNfsE/a7Xl+b1do+f8rDsCG" +
            "r0mT139N+l5jAL/Qgq4FcTES7tpwn/TwBtPTO1doJ55nTuVytV+lOAZ5epq4/h7pnHpKDwDp9WDSIpLufRNbEummDfdt3LLR9DRJ" +
            "jeO1awfG7AFY/9Vp3/WfxQL9IHsqMw4KIAlqRczamFpxhv1RSQ9ttF86Q9p4bdsBbVebNkZE3fz3IUvfV8ZvocyHGOYUTwCJegb4" +
            "aht8AKSk4HTWRm+qXyN1vBY7MPYOgB77zPAPIFUFZ7vlgvVEAC1rZcFErjeZH9j4gHQAxkvsgLYpeMxc1n0Gr1emr5dnd9K8m+/7" +
            "kHVRtd0eatONt4R2ZuNAOY1X7cCYOwCVCjYhiTy7zchlrSRsYvgJwMIeLFkMt4kJbo3xEjvgLQlhzFyo2xjvemV5MFQk6xqZXjIt" +
            "yB4suZMrsoxX7cDYOwCaNEk420FCxwGWqZXQmV9w0051sE3MNpb1eVm7F60t6Tat/bre7Pqcr+zWNZ+tZee/W2zJdVugGx+Wgv0T" +
            "Q/4Tga3jGHsHQA9vtmU6fWO1CjO7OG6+2qOtyYT9xm99vgPaslwYW9flZXixEop7ouyr9vHqN+XY2oVMR5ZdT4DQdbNL9qoYOhBs" +
            "WUCAUHeMl3wHtJ25MFaujZhonsV+4+fvc+m+uf5MEKy3f8HNd75/YghbQh4jAj3vL2PuAMSElcKR2U0GrMONusmWW2DeJoMq/JTI" +
            "9DkCa1mZNGPozZNOqn9265Nr39zm5Pq5k04d2n80pugtG41x+jdG3LxlMx4shjB3oPBtewIUnmsFbfmppbtsPaN+D8j/0DeXJ2tS" +
            "B+o3nacQvGWb02q3Tznl2R1kW2117B0AbYWz2NluJmXQKswsWTbrEEOCqogCQD01QKzxMvnT6aWTZtS/kmWVu7WWXUjCcyMBkkLw" +
            "a1K18tMpM+sX7jArrY/nUIbrqi0brmlttdc1MWW1rnrTA0SU/LOARGV6Lssn2syCPh8os3yV05qtW32qfmoN9Yc0/w/qM4pnDHHM" +
            "LeYui9eVzxdV8QnPpvqCybOGXoM+l7F3APRlADVrJQm8SYblFgjLUKabGSxbB2ONlEmfrr1DN38RM5yjua3LmBM0X+qiKqOqdMsF" +
            "irUyw8uY8XtTzhraE30sCt/HaKMZSnuGnlC2h71g55V1cfibV2LVmx2RttvsyPqJm32o9o3NP1y/fPMP14544SfSC1ZmqVsdn7bf" +
            "8pO176SEf1Wmb6PMR3MunluR8WrL7WEDch+gjSfqYJ+CPpaV2Io+jvocQlU8YyEypWRtmPXYKAmqICkAyppgbVywdRAjlhceml6g" +
            "G/9FZI37keE89TtInY4CeeW6Q415W3ys9gHpy63bnJQ23PKE+kWoNO7LyP0UQ91ZzIlARsCVhKqQMwgYYSvXWDLR158OFBZjqtRR" +
            "j/nGu9KSsidksTctzy5lvzJd705f5ZXr0SaLDMutLzuydviEgcYCxTtGqAjIx3C3BEV7mS7XbHF07fYtj0rLfko/M2VbHlc/qra0" +
            "sZBMx2ncquciRmS5OrcYCFk33T6KLR15kc19bItRw5oGp52XNgixD5cxdwAq0P3QxkR2BBOWfTF3Z1noyrQWA8zQs2z+wfSizY5o" +
            "fC8Dr5LPS0G5CWyHY0l39pJ8TRpozH35J+qnyzPqy48b2nvLPzTuVf/LhRc7Bos+JYMK4CpWzdXQAWaC5SYztzmGbeLKMz5B6EtR" +
            "yL7EGb0gFT0BqOGaUH6EXLD2JrJVDNvNBZxF8kJuR0fxI7+RNX6oLHx9tLtvL7iX7Y5ZMnD2y4+rfXHL42s3kLxNP8/vHFnd7lP6" +
            "msunVZMVNOxiV8vtbLmJWEFTe67CmDkAL3xLevML35r+L5FdSm2Qkd8owjJZsDJEIvIMBWi9G36uorNMHGx8UL7bkbIL5m5AdmjH" +
            "wu6Y0tUHDBuPIXkwbW8HibCxZICUzBZDsiEzGLHUJrYtxpMc9mCivk60YEXKSD4KOZLLmm3f8C1p140PUGYi/QeQ3qgMnbhMhjuT" +
            "lG1q81UTdpa0kGd+m85lN1A++wiIFmokqIgtebxmm0awzbDNsNwO24ywaW4hF+xY8XRQ7Hy+5Tglh7dG8XxlkxRxxG4xJmT9+zpj" +
            "rT0AL35LmvrCN6evZyndTWIvATkYDGcZAQazk20T7AOWPi3OeqyazDamfCGYKZ+ciW52XGayG2oMWWw72Bont0t3m+1iVXSgOY79" +
            "BMe0rWQ7Wxbn8fjMfTP4Z/SpaKg+RepjmI3elA6vN9I8Zci7oI3rhDIjbAU7M6yLw8/cju620MOhc8ZsIPq7vR12td7OcNH4JmV2" +
            "ZGiTbVRbex+3WW+yfEIXu1oWx9PB40iOmGb3EWsvdFVl+paufatr3QHY+M3pLaykq3TgQc2O2pwS+Q0icr3gyA7tR8E0dwAIG1sM" +
            "xUVXITPQPm2AZDiWmO2cqbN1se25D2GZbLHtjkHKRoCkAICAIRV0DOklu0/IbfFDJyF+uJKqn0Afi4bvY7R+hEopFphngwJSbz3m" +
            "7E2LzJDN7KxQqxpTB2wz7KMGVbUrhm3NuLK21+TXqnzcJ/fJ+1jP/VLkZnnN7fIprcWc4v1uW1NXb8uKnc/XuuAqm+PkUXy1scWW" +
            "muMRv6xktTc+cCKftFe/sNYdAJ3yV1Ib0wJh2RczM+tCO1tuAsh9WhxZxZbODNLQUagPBpSVjiNGwdaRAWY2mSAJyAcs2toYbrMu" +
            "Vs3V0O0rWHasYKIVV22OGW2FnVwi0zkTUNl27icnLkSfi4bqc8TnHm59UEGaUB6EXLCya9kMVZvtAfUVR4w27uxjny6UnwFs9nht" +
            "fW1yprZY4zmr233a+7jNepPVM3Sxq+V2ttyEYlt2X+CGeqps+/MZA6f384Ofw5dY+w5ABaA2yIAYElRFFNSmdLDu7DPT+vLAVp/w" +
            "lQ4DnYXUVthuEtMxS5YtxhOH3W0lNImwNRkgKbQYBAyZQceQbrYN1jtA+XBOI0t7zDux+r6FJ/ExrEJZ0S4aekVd2/ymp4nr7Z5O" +
            "Wn+PdOv6ezT+sP6ejZ+uv1f90hfsnbZt81pFUW89uqsyocmSlW35OzSX84zMZfVwqzrletkW9mYMNcsrNt3ve6ttSLaVvvYTUoEy" +
            "XskddmWqdb/7zfGkkc3j5PPVIBG3ZHs5kucqm8awZsjyeEo4fN6MbLcFMwZ/7NbVjZU+AOvunt653jN4UCf4b7XI6ci4ATP+BZl9" +
            "XJOfq0PwhRfvlVb5ywqSUIXiBiu2mOjgIvvsA6Joa3HTTkK1CRAwMr3v0VWonQhfxXZ/igMylmy7++c60GQqsKtYFR1oxpW/fSKu" +
            "5JLtnPEZEGdtsKQydf7J1auhb5AwSkXTW7GRJu6RJinrb9HcbtKct/KJD/gE+8TnPKCTfPzSalq04fR0FPSt2IpFb/Oivu7TRkVs" +
            "s+JGVolR6LBcorT1Ys/L9i400FCytY1ZivaLuDYULo5hW5OLttLX7LYOlk/oYlfL4uY6JMca9KWBRvnnBn3jB86cM4tPR9MoXlbo" +
            "AGywezqo0sBc3fj9Sa1GFWKSyLOgxVBE2V6CLF2+0Q9x78ZvTK9dmfUQ+A11IekhoFjBcMYQuW65icJWtvVg2NYGPQGkobPIQiPi" +
            "qqlgj5+PTVgmW2x7Hls2AiQFAAQMqaD3Q3rJ7hNyls1JqbHnglOrh67u9zyWUzS95bSqaeLu6XUNpG8oI9fN32k6sz7xbVnht5oR" +
            "p1p9SpZtZ/X9z433q9/wwgPSFtE0wkVjfEdjyUu9NY6uIecxNbbGtc3IbWqWzRtuW9Jmh72dJYddHG1+37tbO7QTZfTcJ9dKWfOS" +
            "t2yaU9iaLLNlxbaPPOC5wEU2+9rmudkkPNYgPjD/5Gy3BaeOznteYw5btexh26KhQnycJMgCzgzLTQZoWZGCCcQpJ3K7WPrBqZEe" +
            "1SE4a7O3pXWxnDI0mJ2mwebmsZjHiPiljNzGFiu+urR0uk1AgVIPVixkme8J2ovNLNcghVRnsWNQbLVkhAIRBXMbHIPSm0wwZD7N" +
            "CmcNLa1su/CU6rXquMwcsAaKpjbSqOlVoHyKU+4TXWZozmpzBprM9l2G1Si7suCMJfU0f+O3pvfJ0rP+4Wv8n8p6fLXGuSrGdawC" +
            "6h+S2tBsU1zrqeCwWx4OcNHv/U094fsieL0ezXG62W22N1mBQhe7Wi5Y80okvlplZcr8UyqzF8/mEjetLRj5AJD5t89ahSoY2cBg" +
            "kIAihK2Lm3ZqqW4TUwxi84zpuhe9vXHnSw9Mr1DrMvVX1/DPv/565chU4W7M+GMBwyLiEiQQPmII7AHb83kt+wSQPVHzo9fXDgUK" +
            "W5M1juWIT0iUAQBVDccoWLY7K2jsPv+06mE/P42/xCiUlR1C0x2pS3s2SFY25JmYy3lvy5GHarUl123RJtiApE0JQVkTckqvrmfp" +
            "Jy96Z/0fNzkovTTaui7/fT3veeqGbA+wcYhiPSrIw7FzxDwirvR2lhxjiN3Hsn3zudi38ZQCdVS5/tq+hn0Ny/B84T62CHK0zbGW" +
            "885/RN3ev+D06p7zTh+8p2OgtUwZ8QCQBCk4K4KByAj1DCag7JEPZBcouE2MAhS3QFiG42USyQ/VkRa8+K/qM/CRpB8jsUz51XXV" +
            "69aZkE1lhpnq+2f3BQGSgjkHCBgyowmPIzvbGMC/Cx2VGb7TjNvmm8dxACgmBXMbskLO+c+s4IxavTJt/szq9RimTD4/vXTq55f2" +
            "fPoN02W1mTXtEWLrKMeJVxb4xOfeyogQxAwBzrKQCh3mnij6RFz3kJ7hBXL93Et+mx548cG1t9najcVXccmTX62cXavpICB9NY+v" +
            "vuqYy+rRS0bh0+KrH720eoO8O+riC6tXKeP1047NXX3KuTZZPuVYEovxv1qtVLZdcPrAZ4Z7z293wdJdpp0/dMsAa7/KwHu3v7D2" +
            "5HZfGPq0Q6wpZCMNzIxDJCEGSUA9Qi6ZQOhtHD6hq81cAGKQrkLBRbYh5ykZs3976fsa33nJIannr5X1Wnjil1dXDiOyXUneQQIQ" +
            "zN2wvYgLUh++Mpyum384hikPf776V/I7U+tZIob7goBltjEkG7bpl4o/TGzsunDm8O/57b+QNp12fu0aILub5P6OK5YK/xfPzt/h" +
            "4trD219cOwypmUYYraLbONJQ6XG9+ZQ/ygpdc2/Lyhfp+ZSl0y0lqy102ZQ14SP2pqkFsBx988hhk97G+zE15m9yaP2SjY5IG6FH" +
            "+eVXOOeJK7O9FOv9LPT+hAAAEABJREFUivtIPoYdFTPGTpBdSDXx5dVqNunRSyrnYISy+PzKWahkkzSXL+uG1NUXGkOQRfNW1JAB" +
            "LNaU37vgzOrrFs0cnIMeZZdZad2p5w3NagzVF6n3BwRGLHVMiiUd1iVvySxdveNltXt3+uLQdIxiyUYaS5/Er1FGgPJknqXITzBk" +
            "I0izIIZ8IA5bkwnrvpjzGLJFrIItN6FYlikmPzGx1li46WG1D2OY8sQ/Vq8frGXTFHsmiD/l8eWsGBn59YTKjo9eWvnYQxfzV7Ku" +
            "UH3oXP7qofOqH0G1sqOW9G+KjRIZ8UeCpy2aXd164ezqjcMFnHpe7fA/rVtfRFJPFKwjBtTZgSy3AJmoCyDhL1IFt+50+dLjMEpF" +
            "61v+SH+6jVdoelfmXjr/UvKTa0upSw57wZabKHxYcJx++YlhH3MB5W5IXfFflMgrXnZ4fe7Ljkg9s8OfDx6/onL2hJRtoYw6FBmO" +
            "Qz2b+shl2bsfv4zzPdqq4KHPcN6ic6vvaKCyHcDjFPf9g1lls4WzK+dimDLl3KE9p503dDeR9JkCL9N85JnQwSz0YCDavBfW4cKL" +
            "drqi/38J7MjdGPEAuMPvb+OHsoxfjOxSDzMKpiduWcySLQtxg+WgCpICQGUBCaDg0C0PBwLhQ+ygX+DdutmH6jds+tdpK/Qov7iC" +
            "v3/s76v//NhllUse+xIX9HBZJVMchLMrlyyaVb3+gVn8U68gU85LW0z729q1lQrv0EJ3jfXFfmgBXhuJfB1i5pAfQNXQC1YfSEfC" +
            "e7ESZVVdPdwK9f3d93ls1sBOOrvfiwyNXtK0AIupYJ/mXC7bzIJWpHedr3LP9TxOnve+lnrJti0bCweTjQc3/XD97M0+svxfK2ug" +
            "1V4303t+23OGzsoatUc130MEjdm2vshqWQvO90ft1g1522bK9yckgNgeo1BW+AB4Lv9zK+f+/rvZG3WSD0SG+ZokDB/YFgjLiFOv" +
            "5mDZ2tmyYB+w9Glx065AqigB+RrSJ6r76VnWWLD5x2rvwRoq0z5bO3T9wfpCVngGSWhfQLZYCkBV27q4adcdUDPYzpKzDMv9zgR9" +
            "Khpq5SP99tv81u9uyaaB/ASI3wpowacbiFOtRTubl8liyKdE4RP9bWvXnSHWhwOwmVxu3OLo+sUY5TL1nNplKUG/j0ibemhnrznW" +
            "HevQGjW5WNfyWJ3y/ZHgdYpcG76MAlbpAJTz+u1/8NI0yG2UsRfRkw8QlsmClaoSIZ/cbr0DAK1zeIbbesD2Iu6xWxxdOxSjVKZ+" +
            "tvY3yHA0tXv03M0kWDBso3XBXABiECApFBx9ABQcMSQrhIyrv2qo5zbI7/6Vv/ufb/KEWoOTQdzkjIdPPPL3nq4aQNkgPdrEthm5" +
            "rmbZ1FdXWdnSo926EFkiti1kebuPdffQBg77DWO09/OS0iEeN19Vfi3XXHL+RFCb9iJkcd6miVgW2S4PSe1Ve9Curmb5OR+Acn6/" +
            "/xZ/8Zubs3cxy6aTvI86wmIEW24CuU03M9rEcJu41GlZiBssLvXgwre9DzIgI3fGaJWM25IENS5pFsSaBCAmc50020RdVK1HH8nt" +
            "bLkJwnGRoYZRKBq2v6P890287dff4CuRGkeCeMKZUsJnOz/xutLjtrgjq902HNzNbd1PAGLQTaOErDObvQ4h5iR2hjehGYVcsEj7" +
            "4iuajLyopyPkCtPDubB6r9nqCc/0629UrxpY4kzB2cz4jKCTzWVB5LY29sbQehdsV2Yg2jK22KswMDqFHsvji0kClsUs2bKQTxAi" +
            "CgWrTyhiUnZzDwDZLViB8lxdNPRzDTF8/ye+yaefurEyM6FxVIqzXZ7xnG1LdH/p7SxZFkDsp4d97Fvqthm2GZaF6ILRKdo3Dyc4" +
            "u7U2v881B/jJELLtBlQKtl09ZMiv4e++YWm3AWT6h1dcPTR7ly+l1frjoBaC1V5IHkDdzDxDNJwyBdZlVEUJyGaErpnlTHRzZJxi" +
            "5PHUXsiKPCo15pNfAKpKVgVJIdfBnNlch3XBehvgPtbbGNKFdVnBzPr6tQWvvG7pEUhOA/S9eKi+B+0OyIz6Hb2tOuXamDj5ZmdG" +
            "sNqGY2UI3NbOco8YZsdQm54EdaujAd2KIWd6Do3oOXiOvVjN8tdV1T6ismo3kK8NiFhQcQxRm76Zul35l9cP3fuXNw7t46Z+YlQO" +
            "gDagogMOHQRQq+lm2NYDtsPZ7bZ29qytix3LPlmGxRiloqEfJwmPTYoLQAwCJIWCPUcCKJjmXnAf23uwg9F/fQX84FVfG7p5l5uW" +
            "TFbEvlQN2Zc4Iwehc1Z5KraEdpasQ4LSFrKyutQRRX3F5TVi2KeA+vjbN3mMQmXjGr/PYw7K2JDFbVkbk7A9sjy08lKuoGwpWP0t" +
            "uU8Zt8luE/L46e2pkS181TeWXvjKm1LPfytRjrQiPCoHgBlI3WQqdYItCyjQbit9UPqKkQFhbzJBEnAbASnnP3x+9Z8wSuXnnxz8" +
            "uwy8gqSGhkBdVK1nZqGdLTdB0DJbjC6dVJugwAqUy2TBpS9xQoW1X+x689Jj8ByKwj2H3ivYlTrKylB5J0DKcgEV+zizS5ZJIeKa" +
            "s+MIwJMk3v/wBZUTi8ZRowdOqB6ldXxAeDIy03N1lnoGltvZsuAZGxKhfohS9MkzXxbry4NcYjyxflLYWPTF3b65dN6u/2fJ2yWv" +
            "dB2dA0A8oxsFKmPpzRHM3YhN0YzCXvhGH9lgXZzrhHj20AbZ5MUXDP+vb1d6N1aywwPHVq9dZ7A6GRnOJfmsAGqOICGbiLC+XLDw" +
            "aWN1VJXdtjbIiDJuxHRbPt5UMrt5938f+u5utzy748osw91Xxn+VfHW6F4DuqvNvFiShtPnpEG9G2ZsZrieAbUaX7eqhSvbyxRdU" +
            "Zj0xi6P+17ToKnOO4tMPHD1wWqMxNA1o3Bjr0txzN2vlCizb2mJL2hsZJTnr3c8shF0M2SxHlNDlLlt+DatE9ddVvm+Qy/2v/vbS" +
            "y6bfmiaGaYTL6ByAlMV/3TNuuG6yDi7yEwwxQXYyItsJmllwhjtTVtn1kQurhz/+OT6GHmWbk9KG25xaO2TyGfVjtzkzTe3hskqm" +
            "aRctmbbDRUuP3f7i2vt2uCyt3yvIz49Z5+H7Pzb4XhD+T80+QO2sgZg/RF5HzlCbDHA7KXsB2wyyZWv6qA8pu9j9wy5ZJoRMwHb3" +
            "B3H0M0ND312RQ6AQ6ria65Nf4dzExgGaGJaBTzOholNcsjSd5rjqffc4s3TIwxdW93zk8+z5r28nnZkmTjplaCaq9ce0Cdeq78VM" +
            "9XlTzqx9bZszl6zyQdju80umbveFoa9nqDyYyIv1zr0O9doTO146NHPSlb0zbO5RA7fe/3h1ZyJ9PBG/ifV6JUpNk9YT1MFuGw72" +
            "dls7Wy4ReyZlWd5rSb12tFqWW0flAHgGv/zHgVuYZa8n8aAAA550ZDnAdvas4vsDnJFtWJmy+PPV6zBMmXxq7X3ZUGNelnE2yfXp" +
            "mC28O8uqc7c9q/b3U85NLxkmxDLmnS5Km2x3Ye1LrFQfIPkuAYYuQIYNxLPXe6b+4E6X1967TGcbZrNx398M/h0mVLfVvC6j1uP+" +
            "w7HiqXIZyACNJyI6+mqvwu64QshE4dNiZOkAjFDcfQSX/jU/fgVvfXzTbMfEdKTy/VFo0srWYoD8faarmtLVRDb54S9Uhv0rm8kn" +
            "L91lm5NrtzeA65RNWzpWUqaoMyzDJY9fle2jqNcXb3vO0Gk2Lw/bXTg0s1avLVbMj2huFTEc1xxwZ42jtq001+t3vHzohzt/eeku" +
            "NnfjZ4fytz89onpMo9HYXk+D72oeeSxIymMgYkpujqG2kMXRBhf5izSerSHp0lHzPjIplq5wXz2FtsEIZVQPQMxF2fH45f6mMJuq" +
            "Uz0D5FNxgnWqmfFbRGW3Ry6uHv7wRez517RbnZo23frk2tUNZvcg42uom9wBrSj0JhO0nGFdkp+Z+ln/hQ56lu0vrN0AcDYzTiTV" +
            "LyCLGMyZjmW5xXup5Z6dvlz7yvZfSpuiR/nZkRMevO+DA/4/hrxT/ReRBEnoEiApsUDEJVCw/HPZehfsAyqE7Y5RsG0InQMYoajL" +
            "CB6rqXnxVVzy6KWVCx67rLJJA9krBwazTR65ODvwkUs4Bz3KJL/nT6rPZKO+EMRhAnzKcwaaDBVtiq4IG7pL+sDUv60t82icdkHt" +
            "3cqwg/OYCS0GchkIhkqP+Pp88MGBgdqCnf9p6PRJV/b+fHDvodWbK38c2F7zOjkRf4x4zth2KHzY29nycOieS8SSs7l4Xkgbtq6x" +
            "A9A+o8e/yJ8uuoBPtdva5W1OrB3KJY0FWYbZelA4k0EtnGQwKG9BKpwxtOyViZ0lYZOeM6H2A9Wjo7KCt1LBSbfnsCMIkBQKVpxQ" +
            "xKTs5gIg1mOGszdkff4rr6kdih5FPzYO/eT9A+c1BqtT1P0q6uQIMHRBzJeE4kgtWPMKe0aEvRgvZKLwLZnqJ5n0BSMVhRrJZc21" +
            "Tzpx6R6TZgzd1WD869stlKFQ5iAyROtLOuXKVenIi2x6N8M2++bGXMvttkpn6vFuTJvZx7Ed1xxwEI1jsl29JebX8Ndo1hxZDarS" +
            "mF6eUuOrr7hGnw+u7f354L538an/OnjwSKS0m/r+WEBzPMWEim2KFpIuqtZs9ciWZdLcQirYc4w40oPlsryaLa9xTbVNOTFtMWlG" +
            "7Vqicgcz7kbqzrqKVYsTjpy1grA1maBlZU0wCVgWs8UVdBU3k8xjikGAJOhYlNzOlgvIqfBhk6G2wr5XBbxH3+dfucs3U89/2KFD" +
            "MOe/3j24h55uHyT5pMdzf3OArbFtL+KKiLxdTEAGV4FNSBjxF2WeKtaWstmstO6kGfXZtVT3H50cohOs1Wh2cZrb2XIBEeiLsiI4" +
            "8kGGgt3XWsGKaU3oqvrIHG2OYd+S5RZPHXE5jkUjRrCfFfcZhuVyROPPQ/+5183D/wc07zlo8JpUqU7WND7jME2oc8jDsmbhNo/f" +
            "ZNsEpJ9H3+Vc1pIDkLj1p2qHDf6pMV9zncmMbX9NC5AUWgwChsygVkDpJcNZbls3yyn3IUiiu8iUqQFQE0moCuJMJgIoOGJY7oY6" +
            "RFsPViBVvurpZ2ufVqRh65y38em73zHhjHqlPknj3RjxNI5k9QdCZ4ttdwNt00VVaihyEmfZ5zBCcfgRXFZv86RPL331Vp9q3K33" +
            "/NVg2lzQgDq9PtF+F2odYRP7/aYWQDJcgrveie7jNrF9y9aIIZs59fhvRMm34bboGmP7iZL3Dlvzktvyq43qaVIfS56jeiKPJYvs" +
            "lm1Xdi/z4RM9yk/eus7Dd7918L0Ncl/FesAu7m92rGW4bQz5Q/vzTJYab79jr4EfYYSyxg7A5p/Qe/5T9etTyu7Uyd6Fupl01ooR" +
            "CkQUzG3QjKO5yfKx7L4FOzPIwh4sWQz5OCBJdJe8iaBjEJ1sWxMs2loMt7Glk5IFj2WQhZ6xx4dPDFvuOWDgB3fdObiz4hdcSiMA" +
            "AAqpSURBVH+MGf+bxTigukRMgGQHQHy7Uanv8MPXTfwmVqAo5Ap49dFlk0+n9bb6ZP0z1YH6AmXRezVhRXemiCJDxXGi29lyAVHe" +
            "RwKFZapi2eYYTZYt9DZ2WxtSpm0OHxnLuCXL5KrenqFFtOZgq/Ku2Ve65R7QvapgZctsNu560+DlNQxMUddLNG5NQDwJPL9iHO3l" +
            "Yv3q9513vG7Cm+/cc53FWMEyigcgcdLxtSMm1hsLtBGnMeNEASTRychtbDEkGxRTMy7ZNt02hM32AEOHUjrsJEIW0zYxDHQWNclM" +
            "UDEkAGKSsL5csPBpY/cnZV8GnWOujDZnP/7+zjcOHqcvxnZmxm+Qjg8NxT8Q2ZkT6oPb37HXhJuxkkXLXMkeq+C+5fFDe211fOMn" +
            "+jR9pU7upjqtyiRliq7SdZXMwkoNIBliv/fUAstwkU25Jv/yaqM9chQRZMx1ezm+2bHMpS6njtqQZh/3DD/pOTuqpbzFUjRpFra4" +
            "T9g8Z9kivuSwi91m2VHMed9Vv/5o3wkP/mj6wF/9aPog79hH2Htww9v3HjjrB/tyyapEXa0HYOuPp622PK52I5n9EMQrEGkGUEzd" +
            "zCaHIrtYFR3QDENvMkHLjmEm4bgkQdvYYtvB9riEmqGfudFd1DXa6JhtsDFs6lgy1N5hd5tgm0F6nALyJSUX3D3umtY1rf5Pwe/5" +
            "LY+tn1uvNuaBeI+zIDIDyhl6vC52ptjeZPmELna1LNYTxFfBVTFM7iNuZlfoamuyGi07RpNl667eCfu02RUFmj+iuK+FXmzbcOjs" +
            "8zuraxO87L7O5+XHDe09caixSFlyijJmAp1aAklYJlsM2b3BpGwESAoACBhSQc2Q0kt2n5DVt4PlFLo4fMS0j7kAxFAs6Pfv6C5E" +
            "I5o9Xi+okbb3YMVT5TKQAejogxF/LOue1urWNb3+DbHFMUPTkXib3nebRNbDOZQjz1DJzhTbm6zxLVM9xPIAJMMlWNEk51cJ7msS" +
            "2ze3S1JfRUD5pGkfL2S3G1ApWeKytYyomNFYsPpYilgauxwn2G1CyGrr9HEQ98TvG0xnWlub0NcDUMk4i846Q5kCsW9m2AiUDFqR" +
            "LlbNVZsMzShsTSZoWbFKhnUW9mDJYsjHwchcJ835OGDOEYPW0FHkmkWbYgTLUDK6x3ObgAKkApYofDv6ZPqAluFNc9424cGOQdcC" +
            "RdPt1yx07jPuBWVAwBlhmY6vDGhnt1lvsnxCF7tabmfLTSiWZfdtsmyhF2y5CTmFXLAIZXzLJbQT6u0Z55bSx31l0ep0VbW+PMgF" +
            "bm8y/6VRr+90z9sG77JpbYOW3Z8pTTsR/vd4VUYGEU1mITcZIGVjiyHZkBnUjCjdbFtkkm1NENEW4+QxYJm2C+YCEIO5T4i6RF/w" +
            "t+gqaoLblgGJsLHFsNwDtsPzdFvG2yXuds87Bt4z56CJi7qGW2F1dTtqjv0cIs8hv0WN8kkQMn0VqPEkQ+yscg/LsiJn+cA9oyUk" +
            "a4ZbDMtNOJby1rHCJj1kcWcmqlU2R01s3KnAHVU/YfhXAbLZQ+SYoqQ+6ok8ltqsC2EXu81yMa8l8vhuSthfN/61d71j7f5/BUCl" +
            "vwdAmeibSLGSAE2m7rarWBUd0AxCbzJBy45hJgHLYub8zwqg3y0QtoMASaGTwUKPGJJb/LNUG/gyuopC5J8BJEC+EFNMUmIOCTDI" +
            "XCfFpY+5go//10GD+805aOA7GCNF0+7nTHX+6Xhd7EyxvcnyCV3salmsLPRVcFUMk/uIlWX3KtteO+/E6qGVdSt/rZt0hXTAfe0z" +
            "HENebhPrfn2/OlDd+4GPs+d/7lUuyDMd6OCIr/n0YqjYLhqLtW8HYIhQBhGkkGcqZAGoalsb22bIjDzL5KOZUD7uEzbHkA3kr7MK" +
            "PjL/xOqr5p04cDtUfAMf/GT1qEq1saP63EFSboQugmroBSsGySelHfazjwy84d4j2fuXMZRH7osYn0TJjktK74LtaOuDMVg0/f7M" +
            "+hfn4Q/KtZRnTkKLgZCpVmWKWqQjL7LJqrdtebU5PCSkWgPpIrIy+cEZ1S+D6ixre5177IQHfn78wF56eR+hMZ4S8ubSlakuw8VL" +
            "nq1Mu/+j1a9KHr5qLkmtetL4ijyWLI4lhF0cMy0494GK/HTFyn/X515rFH07ANBuKEHmCBK1m65SVHXvCpNtGjFsTSYi04qMpxgZ" +
            "vpul6o4LTh44Yd5J/CNGKA8eV/3KYK06jcBZyHAbiduQ8bqExl/c/9GB4xceSx3O5Qfx9wOMORFkDgkwyFwnxYUPCnafkKVjDJa+" +
            "TlsZeyKoXXCGdLBtBUTh086WczzUIN4+/+Tqfg+eSv/zsNy6Atd7T+Dv5h4zcOYDRw9Mv/9jA9PnfrR6yNyPToh/TbMC3VsuMXdl" +
            "9HBsT7e1s+Uxir4egIc/N3CrHgR7k/wVdQBIQhUgYFh2xpRsm7NHeDojT5p/anWbhSdXV+hfsqDfRXPM52aBIJeFjNBcRUTpG5wR" +
            "tlf6uJv9Xt5w8fo+5Yc+O/Cf2WA2NSFdItSVSwCRl2BZpcWVcPNVjYHK1vNOrYz4DxjVbTVWT0Vwdgvd7/x8vmqPGeRsWymFeQxe" +
            "+n4AvAcLZ/EPD50zcFyqVF6VZbiLuvGRKcGE5SzL5lSyxm4LTqseuWgGn3K/NQlWCDAHSYkFtEOk5DZ2tnsNAapbtAHP8w+B2oCu" +
            "+tBs3rdwdvXVynP/27/vKFv8TaH54PmnVXZ98JTBnn8H2BVmlFTNTpmvVxg6ABXbRWE3t4OFUnKhjhXS2V39U9UhuHHhzOr+wvSF" +
            "Zwzsv/D06tdW/6grOYKzvAt6DCCy3XbtFElE1hfv/FzWOG4TJI25OkanvTr2uXgC6HEV739xK+PVpiGX9853mw+L3MZUHT8Axe0i" +
            "ld2GdoQkfDNJwlkesu1dsB1E+JASMPaKljT2Jr1aZuy/Fup+15f68gYs73vJy/NdC9vGD0B5U5zBhnaEJJz5ZMHNd36pA9EevoVM" +
            "QN9Z4LmW0e6vJYz2kGvreHrPNzNesqbp93opSVW1Zmt8IyBdVX1sbX1ekG0M1fEDUNysTFlOEvSOCOYAETaK83c+kdvbmACoy/P7" +
            "yyCM6ZLYqPfMYt3XWNiwrPx3m58EDeiLyfAeMxed9TEz19U60Qz8DUlEdutp4GzPZQ2bobCLKVgX28eZT8kkkVWyX2OMFS1ljM14" +
            "NU1X30LqN5T5+91v+O5hypbmU0IZHz7mAP7IrPL/wjaGLuMHoLhZc94x+BV9DvgXakdKQDKc3WKSCDsJ2yBWFTGALB1++2s54r9d" +
            "wFpWtLS1bEZrcDp3HTjwHqR0ZscUWGhNbr3zEbb0GMB9fvS6CTdhDJbxA9B10+4+cMJZdTSmKNu/LyCyXLvUYoYti/8/QPrMhDS4" +
            "7R37DDznR3/XNEZN1dJGbawxM9Cct0xc9OP9B9+g9/27gfSoch6S/e0Akt/3SN9GvbHjHftMOGNV/y4fa0kZPwDLuRE/3m/C19cZ" +
            "GJyq7P+w3GZnTLOBbN8f7TvhzXe8Ye39ax/NdYXr+AEYYauc4Xe+YfAf7nzDhFl37Dth1p37DvxghC5jqnn8AIyp29X/yY4fgP7v" +
            "6ZiKOH4AxtTt6v9kxw9A//d0TEUcPwBr+Hat6eH/PwAAAP//gCpxsQAAAAZJREFUAwCgcQvE0cevbgAAAABJRU5ErkJggg==";

        const string IC_BAILIAN_COLOR =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAAQAElEQVR4AexdCXwURdb/1wRIgESuEJAzkMACwYRMjkmAcOwiICvg" +
            "LteHfp+uLhB1XVeFRVER8VhRf+quNyi7XuyHgrrK71N/68cdDiUSghyBkIQrIZOEHOSaq7v2dUirxCSTmenu6c7Qv6qp6equV+/V" +
            "+3e9qlc1PSZcPQK6Ba4CIKDVD1wFwFUABHgLBLj4V3uAqwAI8BYIcPEDsgdYOoMPXjaNT1k6ld9P6T3LpvJfLr+BDwhELAQMAJZf" +
            "z/uRsjcvncY5BJzmwNdgeJHSVzjDVkHEOena0un8gz/ewHu3dzDI8rV7AJAyg0mxDwsmnCRlz5EFbzHluKWTiFwCy32rJvEOaOdH" +
            "uwbAsun8N504jpEOn6bYlWJbQzcCy0s1wTh2/w18YlsLGfG+dgkAsu0jyK7v4ByfgGOoD4oZZhKxg3qQT6Vxgw90dFu0XQHgwSm8" +
            "2wPT+Ctk24+QXVfyyb2Jxg05BKwnH5/Ju+hWm14w1i4AsGoVN9Hg7W5XEPIYcA+1QxBFpUMIAevRagdyHpjKFyhN3F/0DA8AyUbX" +
            "7MdhcLxGjdiLotphIGPY+MB0nnH/NB6jdmVq0zcsACSbTCP1zZKNpkbSXBGMYxw1Xjbx8DoBoSfxYMhAMhiLb8kGS7YYAnJopO5+" +
            "WqeueEHEw13UiLmSCVpFpkjd6nyn3pQC8d40S7/n9LTdXG3HSckWE5chFPUSeoJMkGSK/nw9H6cXptrChyEAQCP7OJqK7aenbQMp" +
            "v39bBPPTPTGiCRnE60ajuJZ1DYClM3k4NeZ6GtkfJIVaKBolLHCJOEHAXSl5IvXMtG4BQKPsB+BAHjXeHRR1yyfx1mwg0Hah+AS5" +
            "lU+8/ih/8KO1vFuzN/o5U3cNS4qfRU99Po2yX6C2uYaiYYPJhIrIeHQKH4Q1ZLpOffQWX8I5uah0JJFuAECDp2Gk+H+T4j+j9hlC" +
            "0dCh5wAUmGeiR5+huLZRkHAaKK7d9BayNq/lujFnfgfA8lk8jBZtnqfB00lqqOspGjp07Ymc2GlwDLNgSFDza4lxIsP+Tev4Pz9e" +
            "y2Vw+E1mvwKAnvg7RTvyOMcyv7WAQhWHhCIrdipKR0/GiM6h6OSOLM1oFgoMuTQ2eOSLl3mwu/t9vd5Seb8AgJwm40n5WcTUG9QQ" +
            "ht58wUwoi7bgVNw0xHcOg6eydKWxwVPSsvOHa/lN1B6aB00BIM2NaWq0gWzhbpJ0DEVDhx79cTxhJnr1GoBonwRhGMoYPv1oHd+x" +
            "+U0+zCdaHhbWBACrJvEQUvxKadsVTY1uhsGP0HB8G/9r1AxPwUiy8ySSYgJNpLHQMeoNXvpsPQ9TjGorhFQHAPnt51YH4wS10hOt" +
            "8GGISx2DkTN6Es7FTERypxCEqsR0B+oN7rMLyNu0li9Se9qoGgBohSyG5vQZZOM2kfIHqdRYmpAlO38+0ozvzTdiRNdeGKhJpUBv" +
            "8hi8pfa0UXEAkOJ70gDvDSKcTXN6Qy2MNKNYoecAZCbORt8+Q3BdM9e1yGqYNn64jn+gxrSR9KScDNTd308EC4jinRTV2JVDZLUJ" +
            "1MWfHv1LVNN8PpE8es3P6LVhpaEW6kVvkaaN5D9YoeS0kfTVQN+nj2XT+SR66nOou3+RCF1D0bDB1AFno5NxmgZ5kV17oDv0dXTl" +
            "wF8apo1v85ltYc3dPT4DgBT/KufYThX9gqKhQ58oHE2ajUG9BiISej4YTRtFfE5OpId8ZdMnACybxr8kBv5A0dAhtCeOjJkORI6B" +
            "5lvLfGo4hmf+8Q/u08YYrwFAT/5S6o6o2XwSwa+Fyc4fve56WGMmY3RwV7+y4nXlITb09bowFfQOAEPzum06f252vSiUEg3DBZpn" +
            "l9PTnkN2PqbLNehjOAF+ZLhk4V3s9I+nnn/zCgAdmXBrkc2Wti4/P2Rnaelez6v1TwlSfF34YGQlzEJPsvcj/MOFcrUy4AFfqXkF" +
            "AHJQPN5Ycdihqsqxr+fl5Z6ouXS2MU+XCY3oD5AjxxmViHhy3+qSxzYzxRs8q+PmLWEb2lymhRu9AgA4rtgH7+TisK+KrYPeO3vm" +
            "uzKH42ILdfklm6Z1hcPHoZDm9EkdOqGbX5hQrtIKmnH9CT0QQ8pXpOf1GADBQ/JbnO5VOBwJG86e6fVFcfFBp0hDROUE95gSYyjv" +
            "PxLf07Suf4++0PNO4rbIJpBX9TWYEL0gnb08fz4T3BVq63WPASCYHOHuiOfWVJvXFpwqyywvz3F3rwrXbeS+3Z84C6EDRvnNfauc" +
            "WAxbTRyj56Wze+YvYuXKEb5MyWMAXC7m/lPgCN9TfnHEuoKC4/k1tYXuS/h+R0gYvo2b3uC+TaGu3+2uHN9rVJVCLjdh1vzFbMrc" +
            "dKbag6QaAOSmqRdcI7cUF/X/37Nn9lc5nBVyvpIp64CTw8fiZNxUJId09XhXjpKsKEGrirr7ZRUcMQsWsS1KEGyNhuoAkCsvcThS" +
            "3jl7useX1uJdcp7PKcPpIWOwPXk2hvW4FsN9pudfAiI41rk4oqm7fyE9nTm1YEczAMjCnKyunvDKqVNnDlSWfyPneZN2i8BuUnx4" +
            "RBQmU3lG0chhDykidn46S785nZVpKQjVq2V1l+siqA/eW3bR8tbp/O/O19Wfu5zbts+OnZF93RScG5GGNBak2q6ctjHj+10FNFea" +
            "O38JGz93CTvqOznPKfgFADKbdS4h4eOi8wM3FZ7fV+MSKuX85tKOHbGblH7QPANxXbpBq105zbGiRF4NAx4OtWHkgiXsYyUIekvD" +
            "rwCQmS6qr09dfzo/mNzKmXLeDynD+aFm/Ms8C6nU7Zt/yDfmF3rg8U4HAVHkyHlmxr3MrrQYntIzeVpAxfs7k1s58fX8/IJjly41" +
            "LHD06IfPyM537j0E0p75DjD28Y0oIp66+9t/excr0YsoegJAQ5s4RWHI1yXWyC954ZEoC7eQndfivT8Ndav0UUjd/UJSfMp/3cmy" +
            "VarDa7K6A4AsyYVaR+WKbUV9vymsy6B+s1bON1BaR7yuqu1I07olbCN912XQLQCk1nLResKHR8vHr95eZD9/ySFNGwkL0hVdR1qv" +
            "wYYgms/TU//E7bczm5651TUA5Ia75BR7vri/xPLagZJjtU7xhJyvw/Q78tun0sj+v+eksws65O9nLBkCADLXeRWOmJXbi36x5WTV" +
            "PkFEsZyvg7SIMdw2bzGSyG8v9VQ6YKltLBgKALJI209Xp67YVtg3q7he+pFpvZzvh9QGhqc7cwybt5i9xxh58f3AhC9VGhIAksDS" +
            "+OD9wxfTnsqwVhTXOL+V8jSNDJuCTBhBq3WPzkxn0oBP0+qbVubtuWEBIAtcXufs99xea/KbmaVZNoFLL5WSL6mVZtMy7XhS/Pw5" +
            "i9gZtSrRiq7hASA31Mlye/wj2wqjvsqrzqDJQ5mcr2Bqpe5+0bzFiKdl2j0K0vUrqXYDAKkVpfnXv/Oqxj+8rbDHsTKbND5wSPk+" +
            "RgfNPZ+zhSBq/mK2nhnQzrcmf7sCgCyoQ+BBbx8sS1uzp7joYp3r5+sL8o3u03+ZghoWbB689VZmRGeUWwnbJQBkqUtqXZFPZxQn" +
            "/v1Q2QEChSf2WlqanUSOnN/M/T3Ll+m1x7RdA0BW2JESWxKZhcE0fdwtgre2La0MHHcfLUQsKX+nXL49pwEBAEmBNDAEOZDSVm4r" +
            "6pRXbsugPBdFOTg5x19J+dHz09kbq1czEQFyBAwAZH3Wu3jX1zLLxj+/tziv0u76nvK/MImIWZDO7iflV9G5oYKvzAYcAOQGO9fZ" +
            "7rr7lycq5990GHN/e0jODrg04ADg6ilWWx+pOlS8uirG1VtII43PAEwnUZL9Ekpzwug8oEJAAaByYe3e4qcqw5z9heZeUnkfuD0f" +
            "1kN3GgIBJVljCLRbYM3+DKWHvX73YkAAwD7CuafwrxU5NWn2sW6UGw7G3oD18FGUZk1yc69/LhdnR6Dk8HrqtbKIgRvBMAucbyAw" +
            "PE3nHod2DQCxp3jKurIqo/Te6nG8E2/7+wAYHwVu2k6NuhkXsiI9blU1CuTmBqPk0EMw4RTApT/RaFrLw6jI6t400915uwQADxGd" +
            "ZfdU7yp6qrK/81phvLtGaOX6HASZclCS/RcUZ/vvJTIlhxagW10OwJ4BEEax+WA3tfjL7eYLUD/S0gWj5lfPsO0sfKGq2jbKOYFk" +
            "6EzR1xBMBFbAxHIJCLdRd8voXJtgPRJHdWYATNpTGAl3BxM93jndbnoAZ6TzQNGzFQerbqybCMaveIGFu3Zr2/WGP3d4hwZcmTRG" +
            "SG1bGS/vku08E6Q/yxrXHBWl8gwPAGdfsci6qirTurw6SQzjWvxwxEwA20tP5kaUHuynlCIa6PzczquuH9UraBBMhQ8hjIMGdwet" +
            "j1VGOPsIiSpU4Y7kAvCgXJQeWomCAp/e1ddQkTV7Dtn543Bn56HsYTgAcLJyFQvrDl9YU15vH+E0U3NQDn36J3QBZ0+gy6UTkAZq" +
            "3vBgbbTzDJup+BCKmgZDAaDe7MgpeqGipDbNFgvGlBjgKdPYDIMgDdRKsndBUijacGho51vjxhAAEMPEcuuKqsyLi2pG8I48ojWB" +
            "/HwtDUw41OCoKcoMb5YXP9j5ZvlozNQ9ACrn1W0vWlPZyTnQL3a+sZk8Tfgd6NCxgNy0f76ipJ/s/BU8NDnRLQAckWL3wr9V5tZM" +
            "tk0Gg1p/z9KkORQ9DSW+n0NJ9ikUZN5E6W46V9nOB3m8j0F/AAhGrvCQ80jtS7WjeUdxmKIq0ZoY52WoEgpRIXyMQlsYHNzn7WWt" +
            "iyB4rE+PC7TOgA9XGer5HNe3rh31w/hs12gfKOmjqF3YhQv2YFQ3eCRN4CCvnn0wrPZd5E1U/H1/3gqtCwDwWHGf63ObS1jmTPZW" +
            "EN2UE/hBFNtPo5QUL/7Mbx8EJ5+AQocJlS4CAlz+5tu/AAjHQeFV+3lhrT0V4bzlRQ5/t1Jb6heRh0rnUXrqzXBxN3573h01rgm4" +
            "YDuLejGrLeTVuscvACBnzlnhXmeWa0u9mSeIA9QSThO6HFZ6mv8PRbbBqBE8+8cREUNx0RGPC/Zv4RQ92baumGiaA4BPFfYKu+sH" +
            "8YWueMWk8Bchu7iTFN8JNa5fEwveeyQFngyr41qUOHZCRDXR0ixoB4BocZ+wqb5SWO1wtytHM+G9rsgpfk6KP4VSx0Rw9PCazpUF" +
            "O8EhTkSR3YZLTloCJihceV2VM/UBEMq/F5515Lnet6fyAfB4xwr0dIjIRIl9Jz2ts0g90eqwxnvjkjCepo2nYBOPqlPHj1TVA4CJ" +
            "l/I7XJmur23X8QlC1I9VGvEbL0CVS3rqY+HgEzWRgGM4yhwxNKPYC4FfaFudGjiCqIoyd8zw8XyPa6stVFjs9McyrTv2PLnugl38" +
            "gqZtoah2zaKCnShqG1x8LA0Su6HcuYPMTX2rlXPu8fjB4x7AXjCi5Zc0DRQPCO/aS4TnbeMQAv2s1rXaai1cFNkxlNrzyc7PIMdN" +
            "7xbu0iq7C+qESSiyqNWgxwAABcpJREFUlZN52NdipaLTY0+jxwC4XDm/eDlt/OyMHGGV44TrI3sSHy7qebWukeFWEk7z+QrnHhTV" +
            "j4KdD2/lTu0vcfSnAWIqiuzfwymebMLAcfRL9PhVNV4BgCHocUgHQzVfIHzj2lY/nE8XPN6RKpHQUbShzrUdF2z9USuoug/PZ5lF" +
            "fh0NRKPJrbyHBqOyW/lNb+h6BQAnZ+/z0eJ21xc2CPc5LFSxV3SonD6Ck++nwdYllLsmU4P6vr1LG6lMcPJxBNiOqHB9RXyv96Za" +
            "7xSXH1UlrLO9iu7c2O5bjv00rTtKT1IKuW+Nabo4wlDrXI++cV69wcQ7AEhQ6zPmE0pepmjEUEq2NIPm2ik0rfPMfas3aRl/GOYU" +
            "aZ+BV5x5DwCpuoi4P9HUZDl9FSgaIVSjTtxOg6hQGk378oshzWRttSKO1YhPeabVe9xc9A0AEvE+cc8DfBIBQR6MSLn6i04uKb4K" +
            "5Q6y89zYU1TgOGC6HgmWx31taN8BIHEQMSYDwYxco+wV6VRXkeMsrbgdJjsvKd7oK4/V9KD9EWbLKJiT/l+JdlYGABIn3WMrEBF7" +
            "LyBKc+ctUpafI9l5114U2QbRmnusn3lRoHq2ER1c0fTUv6oAsR9IKAcAmWREfC4i4maBsUlgUH0xQ672J6mNHDhbSfEhuOQaS0/M" +
            "Ty4Z8ut3EMUUmJMXIm6c4n81ozwA5DbuHbsTveNGg7F0yiqlqH5w8t2w2qzkwv0VzYuNPUUFLgD8VpgtiUhMVe0V9OoBQFZ379h1" +
            "pIwhAFsDtQ4ROWTnD8JqT4MTg9WqRiO6NQBbiaq6oTS9ex8qH+oDQBJAclJExK6AS4gE2EdQ7iho3I4VTXZe+p2gcpS1p8SpyncR" +
            "5IqCOfkpTJ5so3PVgzYAkMXoZz6DiNgF1CNIv6/PlLO9Su3CVpy3SZsrfduO5VXlChdi+IZWHJNgtvxOtvMK19AiOW0BILPRN24/" +
            "IuKSSOhbKes8xbYHgR9AiYOWaZ2/okJKbcciUn4JueBsAeItKUhI+c4fHPgHALKkfca8j9prhoHxxyjL3WaHLBrcST+2SIJDHEr3" +
            "GzlYaXD8B7Lzo5CQrKRJ9LhN/AsAid0hQ2zoPeZJMGcUwP6OpgdDOS3P7kahPYamdxOaXjbYuQMMzyEoJArxya+TnQ/wH4b8VHu9" +
            "Ey8gIvb34EHSSxx3NlyyiZ/inF1AhTONzrXfjkWVKhi2QjCNRLzlQcTFebVypyAvP5Dyfw/wAyuNX/qMzkZE3CRctM9BmSOW5sL+" +
            "3o7VyJiXCUM+2flZNMCbgqSkfC+pqFZMfwCQRR2Z/Ak1WjR1mYsp68otaJRhgCCNaR6iJz6K7LweXOPNNpl+ASCzG295G2IQjQ/w" +
            "tpyl/5RtbODZbHlW77zqHwBSCyYmVlFvsLjBJw7o7h+4JRYbYx7AJkLy2yfSmAaeH1qXMAYA5FaRfOJmyxgw/A7AOYp6CfXgeBRm" +
            "SzTMybv0wlRb+DAWAGSJ4i3v0hx6OA0QX6Asj1+LQmWUDP9s6O4TLE8rSVQrWsYEgNQ6kq/cnLIMJlMiwHZA+6NxmdZyCwzS3TfX" +
            "RMYFgCzNmKQs6nYnkzdxKmVpMT7QZJmWZNEkGB8AcjPFp3wNaXzA2f9QVjFFpcNlO6/RMq3SzLdEr/0AQJYwIfmDBpvM8bycpUD6" +
            "XgNNyc5LpkcBgnoh0f4AILVsYmIdEizLadqYQKf7KHobjtOKZSr1LLcZ2c63Jnz7BIAscWLqQVLeWECcTVlWim0Nl2hMcR+VHUXL" +
            "tPvbWsiX+/xVtn0DQG5Vc+rncCIGYCsAtLL/gBUB7DFInsf4lL8hAI7AAICkSIvlIszJa2C2DATYFAAPkfNmdUOUvks/tDAn94c5" +
            "+Unq7svoekAEU0BI2VRIc/JWSH566Zc1UpS+K/RDi6ZV6f08MAGgd61oyN9VAGjY2Hqs6ioA9KgVDXm6CgANG1uPVV0FgJ+14u/q" +
            "/wMAAP//lnnoxwAAAAZJREFUAwD3MpVqK22ClwAAAABJRU5ErkJggg==";

        const string IC_BAILIAN_DARK =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAAQAElEQVR4AexdCXiTx5mekXzIh2zZGF/4lGwMMUc4DDYYbFMoljlC" +
            "Fh540mySbrbddtvsNulmm2OTbpu0TZo8aXabtNmwsG0gsDQnSVO62WziAMYy4KM5AF+SbMn3Lcu3Jf37jsP/PGCwrVv/L6NnxuN/" +
            "/jm+b7535vvm0EhCbn3mdQvcAsC8Fj8htwBwCwDzvAXmOfu3RoBbAJjnLTDP2Z+XI8C5c9WpGs3FrfAPlZdffOD8+aotFRUVSfMR" +
            "C/MGAGVl1YkaTdVbGk0lJ5HYmgihH8H/ilL6ks3GfcxxAUb2Dv71M2eqFxI///Ds+T0ATp1qCNZoqh6XSm31hHB7ecZnCe8ODLQ1" +
            "lJdXPVhaWhowSzq/eOXXAIAQ71QoTJch+J9DWmHw9rpISrkXZTL55fLyCwX2ZhJjOr8EQHl55RL0+k8hxHcoJUoXBJNJqeTTiorK" +
            "d5nd4EI5gs3qVwCorKyMhOBfgtC/RK93W8/lOLIHdkOtRlP5NOoIFaw0nSDMLwDAcZwEvfR7k5NEC8E/gHaQwrvbyVDgE6gDQKg6" +
            "gP/9wokeAExHazRVn6OX/gYSWQDvaZcMkJ3QaCrLLly4kO3pyjxdvmgBwHSyBtM6pqMx5PtCEButVslnGk3lb8vLy6M9LShPlS86" +
            "ADAdjEZ/mulk9ER7pnWeajtWLlM1f09pUANTQUwVsUgh++m0iQoAsO6/AR2M+Tx5AowwnYxAEC6aqSANVBFWFzcKgiI7iRAFAM6f" +
            "r16p0VRWYKg/Br4WwQvSgT6oIloGEJwQy9KyoAGA4T6moqLqsM1mq4bE18OLxNEDWFquq6iofJKtRAqZaMECAD3+hxjutdCr96MB" +
            "BUsnaJvJhUItPBUVZaozGtsf6evri5wpoS/jBdewGD53Q/g6NMoL8BHwonWU0v6EhLigiIjwZ6XSwMaBAfPfAdBUSAwJBgAVFTWZ" +
            "EPz/EkLfI4Skw4vaRUTI9VlZmVFRUYqEq4zEUEpeNZuHakwmk2DUmc8BUFZWJod1/zzHWZl1v+1qY4k2CAmR1WZkpE8kJSWmSyQ3" +
            "dnaohZWESCpMJvPx4eFhHhw+49enAECP/65UKtOiZzzssxZwU8VBQYE1SmVad3p66pIgfOwo9i6LhWuAWvgXqIVgO9K7lGSmzD4B" +
            "AKzjfPgaEPUKvKgPX1BKe5KS4hszMpSrZLJgB3nhwiglPxscHLo8ODi4B23hdedVALC5MXr9MQyDZ+Fv9zq3bq5QLpdfycrKWBAR" +
            "EZnhYtFKjqPvYjT4FPZBpotlOZTdKwAoLS2Vocc/ibmxEdR9A17ULiQk5EJWlmooOTlxqeRmit5J7jAaYAtbchn2wYvd3d1yJ4tx" +
            "KJvHAVBRcXGfTCav4zjylEOUCTBxYGBAbVpaqjE9PWWdVBoQ7iES2TG0B4OCZFqohW/BPqAeqmeqWI8BgG2VYrgv4zj6JmpKgRet" +
            "o1TSkpgY90VmpmpJaKgs2UuMLETb/aenp41uBwDbGoXgX2FbpWgoUW2MgN7pzgo9Xwk9H69QKJZPf+mNZ44jK8lX08bXPTFtdCsA" +
            "sIr3EKVBejTMd+HZVikCcToM901KZao5OTlxLdQ8G5Z9zcjdX00bBx+DWnDbtNEtAIDgC2Hk1RJCf0UIEfXyrVQqMSQmJjRhuE+T" +
            "yWQK8CMgx6aN9BdXp4277CFsrjQuA6C8/OLLhNBSDFVZROSfBQsUl7KyMlMUiog0gbPCpo3vDwwMPuoqnS4BAD3/z5TS77tKhK/z" +
            "h4aGfImFHBIXF4f9fF9TY3/9aPtnoA5k9ue4MaXTAMCQ/0+E0GIi4g/0/CWVKq0zLS1lGZZyRcnJwMBAvCuEOwWAN954I9Jo7LjD" +
            "arV1u1K5r/JSSvri4+Nqoeezg4OD43xFhxvq7YqKimpypRynADAxQe8dGxvbpNcbZF1dveWuEODlvCORkZE10PPR0dGKJV6u2wPV" +
            "0R+6WqhTALDZ6E+uViwfHDRv0OmaG8zmYcPVOEEGsOgvZmVlTC5aFL8Ky7eCpNEBouoo5TZGRoazM5IOZLsxqVMAwBB63Tl4m43L" +
            "7OzsTjEYWqrGxyd7b6zGdzEQdmtyclIr5vQ5UqlUkMeyHGidfqT9QUREeHZERIRbRl6HAXD06HszTvcmJixrjMbWBR0dPdUABWj1" +
            "nQNI+2JjF36xZEnmIrk8TLAnie1sISvS/YYQW0ZkpPzXlFL2jKiZnb1vHAYApZaYuQofGhpardMZegYGTFgcmiu129+PRUTIK7Ky" +
            "MsNjYqJ9snzrZo4+xnC/DIJ/APZLn5vLJg4DwH4CuJienv4lMBSvYA271f58zqeERX9BpVKak5ISczH0BzlfkiByNkDwuyH4rRju" +
            "PdaRPAiArxoRU8Wl7e3diwyGtorJyUmmw7564ca/EHZ9Ssqieszp1wUHBzp4KseNhLinKBOG+IcjvtLzf3RPkTOX4nEA8FVPTEzk" +
            "Nje3RnV29pzh41wN0VBNCQlxpdDzmeHh4YtdLc/H+W2UkoPo9RkQ/guU0klv0OM1APDMmM1Dm7Xa5ua+PtN5Ps6ZMCws9CwEHxMV" +
            "pShCfgovZndOIiErIiLk38Fw3+NNRrwOAMYc1q9T+/r618M+qBodHWXHxFi0XR7Lt59hqDempiZvQi/x1Kkcu2hxPRHVYxNtH/R8" +
            "vlwuv+R6eY6X4BMA8GTCPljT2tqZ3NLSrrFYrAN8/M1CqVRyNjU1pRrLtyth7HnrVM7NSHFH3BA6weMREWFLFQr52+4o0NkyfAoA" +
            "nuixsfG8piZjcHd3XyUfx4eUkhbo+ZNZWZl5YWEhq/l4kYYcIfT3GO5VCkXEM5TSceLmj6PFCQIAV4kOMZkG12q1Rv3g4NDUBgeG" +
            "xfcg+BDoeXZmXgincq6S6kxAz2MJfVVkZPjfgK8uZ0rwRB4hAWCKP46zpnd19aQ1NRm+TEyMW48pnjfu/Zmq2xN/oOPZGshdEHxu" +
            "VFT4Z56ow5UyBQcAnpne3v6BgwePxF++XFcGfTnMx4soHAGt/wrBs+XbE/hfkE6wAGCtZbVaySefnM3/3e/+exzrB2zaCB3K3gja" +
            "MxqPBQRImOCfopSOCZlaQQOAb7iRkZHoN988uf6ddz64DIOxjo8XYFiFDZu8yEj5X4eFhbULkL4bSBIFAHiq29o6sg8dOpp17twF" +
            "DcfZOvh4AYRthND7IiLCc7Bhw0YqIpaPqADAN2pNzed5r756JL6hQce+ZDrKx/sgZMP7z4eHwzMjI8OPYLhnw78PyHC+SlECgLFr" +
            "sVjIhx9+sunIkT/0Y1XxAovzsn/TZgtYEhkpfyIxkTKDz8vVX1+ds0+iBQDPsNlsTjx+/O11J0/+uWZiYlLLx3sqpJR8hg2bfAh+" +
            "f1RUSLOn6vFWuaIHAN9QLS2tqw4efE118WJNmc3GeWJDpROC/5ZcHr4KGzbn+HrFHvoNAHhBnD9flQ8gRDU1Gc8ibgLeVTfBcfS5" +
            "0dFhFQR/mAIFrhYopPx+BwDWuLAPpB988OGmY8febDOZzDfsL7A09niOIyexMokNm/BH4uPjxbgYNSebfgkAnuv+flPa0aN/WHvq" +
            "1EcXAQpH9PUlbNgUYqfuToVCwe4s5Iv0u9CvAcBLS6drznn11ddSMX3EtJGb7VhaD8eR72E+v0Iul5/m8/tzOC8AwATIQbJYQNp0" +
            "6NCRoJaWtjLEWeB5x45f/ZvVOpmBXv8K9LyNf+Hv4bwBAC/I8fHJsJMnT+UfP/6Wdmho+AtCuFOE2LIxrXsoOjraxKcTS+gqnfMO" +
            "AHyDAQiWnp6Bgb4+E+npGeSj51047wAQGhpmLinZ9pfdu9XZcnnYJmiGEkpJvVZreLG2ttsrV7MJCWXzCgA5OavL77hDLceGzQ2X" +
            "VFJKHwwKGtPp9S3sfiMhyeimtIDO23U64x91upb3tFqj03cvSm5aup9FxsbGntu3b09tRoZywxysxcBYfAUNekmvNxbOkdYnrxsb" +
            "O2JB22HQya7a3UkIt5tSckynM7BfR3WYJr8GAPbkG3fs+HrZ1762eWNgYIDd9wGgQW+DaijV6Yxv6fV6QdwX1NDQEAxgPiqRTDaC" +
            "tvtvlDR9HLQ6fKmVXwIAwp4sLMw/Az2/CMu3+Tc2lt0xezkuoFavN/yio6MjzO5cbk6I+g9IpSG1AOYzKHoWOyVgxm9uI99Nnd8B" +
            "YPny7NP79t1hTkiI3wyOQ+BddcEcRx8bGZlsgN69D0Ov176FBP2+UqczlKH+Exjq5xyJrFaJwyen/QYAMTGKi3v37qpetmxpASH0" +
            "ugssiHs+CRD+73U6Y2VTU0uee4q8eSm8nofQqwmhN71tlbjpI3oAYMm2befO7ZXbtm3NCQoK9vgXRyilq7HdXI7eecJgMCS6SQ5T" +
            "xdxEz3tcPh6vYIozD/yRyYLJli2bqyH8WIBgrQeqmKNI7oDFQht0OsOTML5cuquPVQQDb69UKrtCKZlDz7PU7vOiA4AE23Tr1q3+" +
            "fM+enaNxcbGsxzus99zXfCSUEPoUxwXUMUONOPHBSDKl5yH4t5A9Hd6rTlQASE1Nqt23b3eXSqVcQSl1h4HnrsZOYYaaTmc8wwRq" +
            "T6He1POz0SMKAAQHy/rU6q2VGzbkLpFKA2JnY8jH7zbBcPuLXm88XFfXdtO7lHyh52drE8EDYM2a20vvvHNHkEKh8IGen63pZn7H" +
            "ceT+wECrHiPCP1+byld6/loapv8vWABA4Ir9+/+qYfHijCIM92K8CILR/BxGg8aamro9UA1nKSUe1fMBAZzD5xgEBwAYeQ0qVfqX" +
            "GzasWyaVSrz6C1rTe4erz1g36Onu7mvt6Gh/u7FRL5+YGHf5eNlsNGFW4rA8Hc4wGwEuvhtNSIi7kJeXkxkfH7vMxbJ8nn10dPQM" +
            "hB7c29vPViQlVqt1JVYSUw2G1jNWq83t9/05y7AgACCXh2uwVWtRKtPWOcuIUPJZLJZqvd7QZDC0bcaC0fR1e+no6NhmrVYv6erq" +
            "PoMR4tpjaT5hwacACAoKrF6+fGnLihXZefh/emP5pEGcrRTC1nZ09FzSaptXT0xMzrpuDyNR0d8/uLm+vslgNpvZtq6z1bqczycA" +
            "oJQaUlNTatDrV2O3LsllLnxYAHpxZ1dX758aGnSpJpMp2zFSbMq2tq5VOp3hAkDjyLF1x6qZJbXXAbBwYUw5DLyUpKSEVbPQJYpX" +
            "IyOjpxsamoL6+wd2gGCnVyQnJyfXQW0ktLS0n7bZbGaU5TXnNQCEhoZqVq9eObB4sWquUzleY97ZitBb34eB12g0thVwnC3K2XKm" +
            "5QsaHh4pAKDGMHNgx9YdntJNK8+uR48DQCqVsivbtatWLc8LCZE5fGLFLi68lIjjuMrm5pbTer1hNyx5V38wegaquYV9ff359fX6" +
            "xqGhUY9fHukxAMDQ6U5OTqrMzV27fMGCaNUM3IokmtN3dfW9X1+vWzE2Nl7gDaI5zra4tbUtG2Arn5y0tNtTp1cWgjjOOudXr6Oj" +
            "o89t2JATnpKySDTLSc822AAABqtJREFUtzM0sAXD8qm6Ol14f3//bqTx+hX0UDfsJ3ki29o6P+U4btbbUCwWx+0Hh0eAe+7ZP+Ml" +
            "TSEhwRdXrlzetXRp5kas6Alptw6yc8zBGLuMubwOhlkJci6E96ULNZuHCjEC9fX2mjQzERIaGuDwSqPDALha+XW/CySVSthPsNWt" +
            "Xn17Tnh4qJB3666SP3Ngs3Hazs7ucw0N+tuwmrd45pQ+ebOop6cnDwboF1BF9ddTwF1JTEx0+KoaZwHA/2qYOTEx/nxubs7i2NgY" +
            "h0+kXs+Az5/GTKah0sZG3aKBgUGPnsNzlVMYoMthjGbAn8OwP7WsjLWV/3CmXKcAEBxsOyqXy0vXr19D0tNT16Nip8pBPkG48fHJ" +
            "Chhbgx0dnUUwXmWCIGpuIiQYBTZiWTmwu7v3f0JCAg/PneXGFE4Jbv/+/ably297OSAgQNTLt1YrV9HcbLzU1GTIhbElVtUlx4bT" +
            "YWdvMHEKAAxHKlXyOwh/DS9CR7uxfFuG4T53bGzCweVbwbH7eElJETtn4BRhTgOA1aZUJv8A4Y/grfBicObBwSk9j2ndQL4YCJ6d" +
            "Ru6nanUhO0U8e7JZ3roEAFYuQPA8x9nYFymnjBEWJ0Q/Pj4BA09vam/vLLJabaKeolJKrlDKbVOri3hj3OkmdxkArGaVKrVMKrWx" +
            "pdGX2LOQPMdxhtbWjs+bmoxM8KLeeUS7miH4fyguLrytuLjo//DssnMLABgVqamp/RgN/hFWNObOnMd/747VOZsHHd0wjsrr6/Up" +
            "Q0PDK2ZLK5J3JySSwAwI/mV30us2APBEwThsUCpT2HfWCzFUeXwzg6/3mnBsZGTkYxh4sp6ePuw8iu7+5mtYIQRArrLZbLlqdeFd" +
            "27dvdPtPzbgdADz16enJp+HZ2b7vIK4b3uMO++pndTpDp9HY/jWbjRP1FBWN1Y4OdG9JSeHaHTu2eOwKeo8BAAxMOaiFg6Ghgelg" +
            "5tmpCA/8gVFXCz1fDeFvAghSPVCFN4scQr9/UibjlMXFhUc9XbHHAcAYYIsUGA0eI0SaBubeYHHu8AAVtml7/9TYqM+AnmffE3RH" +
            "sb4qA7qKew16XqVWF/2sqKiI/RaBx2nxCgB4LpTKxGbYBwcopXmIc/oOX+QlTM/X1WkVrh7HYmX52qM9zkskthwI/pu8nvcWTV4F" +
            "AM9UenpSBVRDDiH0XkJIC7zdzmKxXMSUTsf0PAwkdx3Hsrt+dyYE/Q0Q/oHi4oLc7du3VLmzbHvL8gkAeOKUyqSjlFoyoRZ+jLhZ" +
            "DztAz9cYjW1ntNrmHCzqKJFetA6C7wTx3w8J4TCfL3gD//vM+RQAjOv09PQxqIWnJRKrilLyXyxumu8bHDSfhZ7PHhkZZd+ymfZa" +
            "VI8TEP5zUuk49Hzhb6Hn5/cXQ64VXVpaWnt6evLfEkJvJ4RM3dQ9NDT0bn29ztre3rUJcV4/joU63ejox1ardCmmdY9s375dML89" +
            "4PMRYHoLQy18plQmF7a1te9tbe1cgaVcXx/Hmk6io8869PrdanXB1p07Nzl8ZMvRyhxNLzgA8Azk5697R60uzMDa97cRd90RNDyL" +
            "wY1yHH0UPKjQ632+ND5TgwkWADzBxcVFhwICJlV4PgQvFneCUgkEX/BLoRMseACwBty2bZsJPenbNqyJ41lwv8ANmnintdloAWi9" +
            "q7h4s1M/HcsX5K1QFADgG4OtiaNxYSTSbyLOCC8Ux4b7J0Bbxo4dBWeEQpQ9dIgKADxDMKhew1r5YhiILyDOBu8zx3Hk+NXh3qnb" +
            "un1G+NWKRQkARnsR1spLSooe5jjKvn30KYvzpuc4MrVNW1JSeLdYhvubtY9oAcAzU1JSUIOht0giIV9HnDfsA69s04IXrzjRA4Bv" +
            "pe3bCz8CEGAfcPcgrgPe3W5Kz0P1eGWb1t3Ez1Se3wCAZ1CtLnrdYgln08bn+ThXQyxRH+H1PFM9rpYnpPx+BwDWuLt2rR1Rqwt/" +
            "BKGtwfOMX6bEu1kdBH8FhmZecXHhfWLW87Mx6ZcA4BmG0KoBhA2E0DtgtLEdOGLnZ5BS+iAEf1tJSVGFnXlcSuarzH4NAL5RMW18" +
            "PyjIko1t58cQN9v5gzZKyY8tFk5VXFzw70jr925eAIBJcevWrb1qddGzanVhMiG2rRzW6Qnhfso8+x97DtvwbhF6/dO7dhXNeQkG" +
            "K9Mf/LwBwLXCUqu3fIzp4y/V6qKfMM/+L3bTFy2urUcM/89LAIhBMN6i8RYAvNXSAq3nFgAEKhhvkXULAN5qaYHWcwsAPhaMr6v/" +
            "fwAAAP//IJE+vAAAAAZJREFUAwCzcFB5fjZ2hgAAAABJRU5ErkJggg==";

        const string IC_ZAI =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMA" +
            "AA7DAcdvqGQAAAMASURBVHhe7Zs5zE1RFIUXibEhEUNlTFAIGnNpjCjQiAQ1ekOhQiISiaGgMUWFgoixM1WGRkJnqIgCEQoKQpac" +
            "l5ys7Pca+2w7cb9k5S/+s9/a/333nnPv+s8FOjo6Ojr6sgPAeQBnGuhs+TlOTSsOAzhn1HroAoB9alizGsCvxjoCYKgaF/Yb4721" +
            "Tk17TATwzijw1B01rVhhjPfWLjWtuWUUeOo9gElqWpgA4K1R46kbalqzxyjw1ho1rWBzOt5TPLjj1bTHMgA/jSJPHVLTCp6WOt5b" +
            "vLxMxgJ4YxR46uGASW8JgB9Gjac4sZqMAHDbKPDUJwDT1LgwBsBLo8ZTd9W0ZjSAtQCWN9QsNa24aDTsqQ8AJqtpFrYbDXtrvZpm" +
            "YS6A70bDnjqqplkYBeC50bCnHgMYpsZZOG007KmvAGaqaRa2GA17ix4p4bfyxWjYUzy7UsLrkdelNuypF2VZTwlnZG3YU98AzFPT" +
            "LHAt1oa9xQAnJbwL492YNuypS2qahSEA7hkNe+pVeZ5ISetoi0+QS9U0CxHR1m41zUJEtHVTTTMREW3xIKeEp6U27K2VapoFTkit" +
            "o60DapqFiGiLSyqX1pS0jrY+Zo62eBuqDXtrg5pmgQ8graOtY2qahYho68n/Hm0NitT/KRHR1lY1zUJEtMWNDCnpoi2jYU9xRZmv" +
            "plmIiLZ2qmkWIqKty2qahYho63XZp5CS1tEWd6Zwh0pKIqKtvWqahYhoi7vS0tI62uJ+RO5LTElEtLVKTbMQEW0dVNMsRERb9wdE" +
            "W9MBLAawoIEWlc/ut13vD62jLW6Zm6KmBT768vda4yneyvc7+CHR1kY1LQwH8NQY76lHxcckIto6rqYVJ4zxnvoMYIaa9oiItvjt" +
            "9jv6PCt0vLc2qWlNRLQ1W00LUwOu+1NqWhMRbW1T0wJn4wfGeE89AzBSjXssNAq8xfd+LPjHnzTGe4pz2hw1ruGLQ/w385UGulZe" +
            "uuoXbXEpvA7gqlH7t+Jn0n+zmmai71rc0dHREcFvfnykuaHXaTsAAAAASUVORK5CYII=";

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
        // 小米 MiMo：官方 wordmark 里的"M"字形（24 网格两段描边，与 zai.svg 同一条渲染管线转 64px）。
        // 单行 wordmark 在 18px 下无法辨认，故只保留最具识别度的首字母
        const string IC_MIMO =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAC80lEQVR4nOyav2sUQRTHv3faCBErkTQWojY2iv+EqIWFoiYa" +
            "zwSE+Ot/sLGxFZsoYhIlhRgIWIgIVgqKhY0WgoiNoKJYiSThfI+dxeNu9+W93blZk5kPfLnk7czsvO/N7OzsXhuR00bkJAMQ" +
            "OckARE4yAJGTDEDkWA0YIV0nLZE6+H+YQtYn7tuIpWLLUHYb6RVpb0/sHpo34j5prOf/96SDpN+aypug5wbpcF9sP2k3aRHN" +
            "ME8a74ttJ30hvVbUx2bo2VUSP+M+zyIscxhMPmcflFiuAQ+EY2zCLMIxh3/GF6HOy2rAknCcR0AIE9ZK3oR1FThOeiocZxPm" +
            "MZzlte3a1iSvvrhbO7pMOgLZBJ6Xs7CtMGvRdm2OwzNVvqnchMdCGe7obfgxYWjJ541XgU04BtmESdQ3gesOLXmmzlxdIR0l" +
            "PRLKsAkzqA7XlZL/UBLvQomPi9VJyDdCfJt6B7aR0HJ1poQyfM7LqIkPA3gknIBsgmU6tFzZSaHMojtnV2hDha/lahV6EyQs" +
            "ya/CAz7Xa60Jt0qOaZJfgMfkGd83LLkJC0KZaQyaoE1+DB6TZyybIWYr6RBpJ+kN6XlBGe5gvj09VdLOtPu8CFvy6qu7FosB" +
            "vM18SxrtifEafa6gLHf0NOlPyXGGTVh2fZCSvysc7xrjA1imAHditC82Qdoh1DkPeTpcRTYKymCDpaWwZYwPYDHggDHO8DfB" +
            "Q7fKLpHrdDCEYd+LZQr8KomvQIYT6Li/J6AjSPJMqKfCuQmakTCDQMkz1lWgDpqRwMlfQEBCvxfITSjaIAVPnmnixQibwIle" +
            "Qfb09jPpEqolX3sZDDkF+rnpVIegy+CGJBmAyEkGIHKSAYicZAAiJxmAyIneAMtm6GdJ/Dua40dJXN0nywh4VhD7RnqH5uBz" +
            "fy2IP4ESy6/EPiJ7LsjvBLaQXiLbw39Cc/DzyBekPcge2/Pb4mukh9oGfP6KY12SVgFETjIAkZMMQOQkAxA5yQBETjIAkfMX" +
            "AAD//xiW8p4AAAAGSURBVAMAxtuHICs0LiEAAAAASUVORK5CYII=";
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
        private bool _exiting;
        internal bool IsExiting { get { return _exiting; } }

        // ---- 首启自动打开设置 ----
        // 一个可用供应商都没有时（config.json 不存在、被清空、供应商被删光，或文件损坏后回退默认），
        // 悬浮窗只会显示"正在获取"再变成一行错误提示，用户不知道下一步该做什么，只能自己摸到右键菜单；
        // 此处直接把设置页推到最前。配置成功保存后 SettingsForm 会调 ApplyConfig → RefreshDue，
        // 一切照旧；若用户没配置就关掉设置页，悬浮窗仍以它原有的未配置样式留在屏幕上（托盘也可再唤出）。
        private bool _autoOpenedSettings;
        internal bool IsConfigured { get { return Cfg.IsConfigured; } }

        // 延后到消息循环启动后再打开设置，确保悬浮窗已完成首屏布局。
        private void AutoOpenSettingsOnce()
        {
            if (_autoOpenedSettings || _exiting) return;   // 回调出队前用户可能已从托盘退出
            _autoOpenedSettings = true;
            if (IsConfigured || Cfg.AllProvidersDisabled) return;
            ShowSettings();
        }

        public AppContext()
        {
            Cfg = Config.Load();
            // 升级时恢复旧置顶栏留下的工作区预留；新版本不再预留工作区。
            try { LegacyWorkAreaCleanup.CleanupStartupResidue(); } catch { }
            Floating = new FloatingForm(this);

            Tray = new NotifyIcon
            {
                Icon = IconFactory.Create(),
                Visible = true,
                Text = "AI 额度工具 v" + AppInfo.Version
            };
            // 左键仅定位已开启的悬浮窗；开关统一放在右键菜单，双击也不切换可见性。
            Tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) { ShowMenu(); return; }
                if (e.Button == MouseButtons.Left)
                    Floating.RevealFromTray();
            };

            // 不设置 MainForm，避免消息循环启动时把用户已关闭的悬浮窗自动显示。
            // 句柄供后台线程 BeginInvoke 使用；应用退出统一走 ExitApp。
            var h = Floating.Handle;
            // 先按启用的供应商铺占位行，首屏无需等待请求，也不会短暂显示已停用项。
            ResetProviderDisplay();
            ApplyFloatingConfig();
            // 没有可查询项时跳过请求；主动全部停用属于正常暂停，不自动打开设置。
            if (IsConfigured) RefreshData();
            else
            {
                UpdateTrayTip();
                // 延后到消息循环跑起来之后再显示，避开启动期各窗口的显示顺序（见 AutoOpenSettingsOnce）
                try { Floating.BeginInvoke((Action)AutoOpenSettingsOnce); }
                catch { AutoOpenSettingsOnce(); }   // 句柄异常时直接打开，别把唯一入口整个吃掉
            }
        }

        public void ShowSettings()
        {
            // 关闭即解除引用：否则 ApplyConfig 等处读已释放实例的 Visible 会抛 ObjectDisposedException
            if (_settings == null || _settings.IsDisposed)
            {
                _settings = new SettingsForm(this);
                _settings.FormClosed += delegate { _settings = null; };
            }
            _settings.Show();
            _settings.Activate();
        }

        // 悬浮窗与托盘共用菜单，每次弹出按当前可见性与配置构建。
        private WidgetMenu _menu;
        public void ShowMenu()
        {
            if (_menu != null && !_menu.IsDisposed) _menu.Close();
            var menu = new WidgetMenu();
            _menu = menu;
            Floating.SetContextMenuOpen(true);
            menu.FormClosed += delegate
            {
                if (!object.ReferenceEquals(_menu, menu)) return;
                _menu = null;
                if (!Floating.IsDisposed) Floating.SetContextMenuOpen(false);
            };
            menu.AddItem(Floating.Visible ? "关闭悬浮窗" : "开启悬浮窗", false, false, delegate
            {
                Cfg.FloatingEnabled = !Floating.Visible;
                Floating.Visible = Cfg.FloatingEnabled;
                if (Floating.Visible) Floating.RevealFromTray();
                Cfg.Save();
            });
            menu.AddItem("立即刷新", false, false, delegate { RefreshData(); });
            menu.AddItem("固定位置", true, Cfg.LockPosition, delegate
            {
                Cfg.LockPosition = !Cfg.LockPosition;
                if (Cfg.LockPosition) Floating.RestoreFromEdge();
                else Floating.OnLockReleased();
                Cfg.Save();
            });
            menu.AddItem("设置…", false, false, delegate { ShowSettings(); });
            menu.AddSeparator();
            menu.AddItem("退出", false, false, delegate { ExitApp(); });
            try { menu.ShowAt(Cursor.Position); }
            catch { _menu = null; Floating.SetContextMenuOpen(false); menu.Dispose(); throw; }
        }

        public void ApplyConfig(List<ProviderConfig> previousProviders = null, int previousRefreshSec = 0, bool persist = true)
        {
            if (persist)
            {
                Cfg.Save();
                Config.SetAutoStart(Cfg.AutoStart);
            }
            if (previousProviders != null) PreserveRefreshSchedule(previousProviders, previousRefreshSec);
            ResetProviderDisplay(); // 保存后立即移除停用项，不等待旧请求完成。
            ApplyFloatingConfig();
            UpdateTrayTip();
            RefreshDue(false);
            // 删除最后一个供应商后，若设置页不在显示则重新打开，方便继续配置。
            if (!IsConfigured && !Cfg.AllProvidersDisabled && (_settings == null || _settings.IsDisposed || !_settings.Visible))
            {
                UpdateTrayTip();
                ShowSettings();
            }
        }

        private static List<ProviderStatus> CopyResults(ProviderConfig source, ProviderConfig target)
        {
            return source.LastResults == null ? null : source.LastResults.ConvertAll(st => st.WithConfig(target));
        }

        private void PreserveRefreshSchedule(List<ProviderConfig> previousProviders, int previousRefreshSec)
        {
            var previous = new Dictionary<Guid, ProviderConfig>();
            foreach (var p in previousProviders) if (p != null) previous[p.RuntimeId] = p;
            DateTime now = DateTime.UtcNow;
            foreach (var p in Cfg.Providers)
            {
                ProviderConfig old;
                if (p == null || !p.CanQuery) continue;
                if (!previous.TryGetValue(p.RuntimeId, out old))
                {
                    // 新接口已经在编辑器里测试过，保存时计满当前实际间隔，复用测试结果。
                    if (p.LastResults != null)
                    {
                        p.NextRefreshUtc = now.AddSeconds(p.EffectiveRefreshSec(Cfg.RefreshSec));
                        if (p.CooldownUntilUtc > p.NextRefreshUtc) p.NextRefreshUtc = p.CooldownUntilUtc;
                    }
                    continue;
                }
                bool sameQuery = old.CanQuery && p.HasSameQuery(old);
                bool sampled = p.LastResults != null && (!sameQuery || p.LastFetchUtc > old.LastFetchUtc);
                bool intervalChanged = p.RefreshSec != old.RefreshSec || p.EffectiveRefreshSec(Cfg.RefreshSec) != old.EffectiveRefreshSec(previousRefreshSec);
                if (sameQuery)
                {
                    // 请求可能刚好完成，结果、完成时间和下次时间必须作为同一快照读取。
                    lock (old)
                    {
                        if (p.LastResults == null || old.LastFetchUtc > p.LastFetchUtc)
                        {
                            p.LastResults = CopyResults(old, p); p.LastFetchUtc = old.LastFetchUtc;
                            p.NextRefreshUtc = old.NextRefreshUtc;
                        }
                        if (old.CooldownUntilUtc > p.CooldownUntilUtc) p.CooldownUntilUtc = old.CooldownUntilUtc;
                    }
                }
                // 从保存时重新计满新间隔；即使尚无结果，也不能绕过截止时间立即请求。
                if (intervalChanged || sampled) p.NextRefreshUtc = now.AddSeconds(p.EffectiveRefreshSec(Cfg.RefreshSec));
                if (p.CooldownUntilUtc > p.NextRefreshUtc) p.NextRefreshUtc = p.CooldownUntilUtc;
            }
        }

        private void MergeCompletedRefresh(List<ProviderConfig> source)
        {
            var current = new Dictionary<Guid, ProviderConfig>();
            foreach (var p in Cfg.Providers) if (p != null) current[p.RuntimeId] = p;
            foreach (var old in source)
            {
                ProviderConfig p;
                if (old == null || !current.TryGetValue(old.RuntimeId, out p) || !p.CanQuery || old.LastResults == null
                    || old.LastFetchUtc <= p.LastFetchUtc || !p.HasSameQuery(old)) continue;
                p.LastResults = CopyResults(old, p); p.LastFetchUtc = old.LastFetchUtc;
                if (old.CooldownUntilUtc > p.CooldownUntilUtc) p.CooldownUntilUtc = old.CooldownUntilUtc;
                DateTime next = old.LastFetchUtc.AddSeconds(p.EffectiveRefreshSec(Cfg.RefreshSec));
                if (next > p.NextRefreshUtc) p.NextRefreshUtc = next;
                if (p.CooldownUntilUtc > p.NextRefreshUtc) p.NextRefreshUtc = p.CooldownUntilUtc;
            }
        }

        internal void ApplyFloatingConfig()
        {
            Floating.ApplyFromConfig();
            if (Cfg.LockPosition) Floating.RestoreFromEdge();
            Floating.Visible = Cfg.FloatingEnabled;
        }

        private int _fetching;
        private string _lastTrayTip = "";   // 缓存上次托盘文本，只在变化时更新（避免每秒调用 Shell_NotifyIcon）

        private ProviderStatus EmptyProviderStatus()
        {
            return Cfg.AllProvidersDisabled
                ? new ProviderStatus { Name = "已暂停" }
                : new ProviderStatus { Name = "未配置", Error = "右键 → 设置，添加或开启供应商" };
        }

        private void ResetProviderDisplay()
        {
            var list = new List<ProviderStatus>();
            foreach (var p in Cfg.Providers)
            {
                if (p == null || !p.CanQuery) continue;
                if (p.LastResults != null) list.AddRange(p.LastResults);
                else list.Add(QuotaService.EmptyStatus(p, null));
            }
            if (list.Count == 0) list.Add(EmptyProviderStatus());
            Providers = list;
        }

        public void RefreshData()
        {
            RefreshDue(true);
        }

        public void RefreshDue(bool force)
        {
            if (_exiting) return;
            if (!IsConfigured)
            {
                ResetProviderDisplay();
                Floating.Rebuild();
                UpdateTrayTip();
                return;
            }
            bool due = force;
            foreach (var provider in Cfg.Providers)
                if (provider != null && provider.CanQuery && provider.IsRefreshDue(DateTime.UtcNow)) due = true;
            if (!due) return;
            if (Interlocked.CompareExchange(ref _fetching, 1, 0) != 0) return;
            // 保存设置会换成新列表；请求与回调始终绑定发起时的配置快照。
            var providers = Cfg.Providers;
            int refreshSec = Cfg.RefreshSec;
            ThreadPool.QueueUserWorkItem(delegate
            {
                // 并行请求各服务商，避免串行超时叠加（最差 4×15s → 单次 15s）
                var results = new List<ProviderStatus>[providers.Count];
                System.Threading.Tasks.Parallel.For(0, providers.Count, i =>
                {
                    var p = providers[i];
                    if (!object.ReferenceEquals(providers, Cfg.Providers) || p == null || !p.CanQuery) return;
                    if (!force && !p.IsRefreshDue(DateTime.UtcNow))
                    { results[i] = p.LastResults ?? new List<ProviderStatus> { QuotaService.EmptyStatus(p, null) }; return; }
                    try
                    {
                        results[i] = QuotaService.FetchProvider(p, false);
                    }
                    catch (Exception ex) { results[i] = new List<ProviderStatus> { QuotaService.EmptyStatus(p, ex.Message) }; }
                    lock (p)
                    {
                        p.LastResults = results[i];
                        p.LastFetchUtc = DateTime.UtcNow;
                        DateTime next = p.LastFetchUtc.AddSeconds(p.EffectiveRefreshSec(refreshSec));
                        p.NextRefreshUtc = p.CooldownUntilUtc > next ? p.CooldownUntilUtc : next;
                    }
                });
                var list = new List<ProviderStatus>();
                foreach (var group in results) if (group != null) list.AddRange(group);
                try
                {
                    Floating.BeginInvoke((Action)delegate { CompleteRefresh(providers, list); });
                }
                catch { Interlocked.Exchange(ref _fetching, 0); }
            });
        }

        private void CompleteRefresh(List<ProviderConfig> source, List<ProviderStatus> results)
        {
            bool stale = !object.ReferenceEquals(source, Cfg.Providers);
            try
            {
                if (_exiting) return;
                if (stale)
                {
                    // 只接收仍启用且请求未改变的接口结果，保留保存后设定的下一次查询时间。
                    MergeCompletedRefresh(source);
                    ResetProviderDisplay(); Floating.Rebuild(); UpdateTrayTip();
                    return;
                }
                if (results.Count == 0) results.Add(EmptyProviderStatus());
                Providers = results;
                Floating.Rebuild();
                UpdateTrayTip();
            }
            finally
            {
                Interlocked.Exchange(ref _fetching, 0);
                if (stale && !_exiting) RefreshDue(false); // 仅补上已到期的查询，不强制刷新新配置。
            }
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
            if (!IsConfigured)
                tip = "AI 额度工具 v" + AppInfo.Version + (Cfg.AllProvidersDisabled ? " · 已暂停所有供应商" : " · 未配置，右键打开设置");
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
            Tray.Visible = false;
            Floating.Close();
            Tray.Dispose();
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
        private bool _contextMenuOpen;
        private System.Windows.Forms.Timer _showTimer;
        private System.Windows.Forms.Timer _trayRevealTimer;
        private const int TrayRevealMs = 2000;
        // 悬停看守只补上透明区域漏掉的进出事件；所有移出路径共用一个收起计时器。
        private System.Windows.Forms.Timer _watchdogTimer;
        private const int WatchdogIntervalMs = 50;
        private const int HoverHideDelayMs = 500;
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

        // 行高（DIP 基准值，运行时按所在屏 DPI 缩放）；卡片行为紧凑单行样式，宽度按内容自适应（见 CardWidth）
        private const int RowH = 26;
        private const int PadY = 6;
        // 当前所在屏缩放因子（跨屏拖动时更新，保证各屏物理尺寸一致）
        private float _scale = St.Scale;
        // 缩放所依据的屏（拖拽中按鼠标所在屏切换，附迟滞；见 UpdateScale）
        private Screen _scaleScreen;
        private int RowHS { get { return St.SiF(RowH, _scale); } }
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
                Screen s;
                if (_dragging)
                {
                    // 拖拽中按“鼠标所在屏”判定：若按窗口面积多数判定（FromControl），窗口跨在
                    // 分界线上时切换缩放→Rebuild 改变窗口大小→面积占比翻转→判定翻回旧屏→
                    // 再重建……无限振荡，表现为两种缩放的窗口在交界处反复闪。
                    // 另加迟滞：鼠标须离开当前屏边界 16px 才切换，贴着分界线微抖不触发重建
                    var m = Screen.FromPoint(Cursor.Position);
                    if (_scaleScreen == null || m.DeviceName == _scaleScreen.DeviceName)
                    {
                        s = m;
                    }
                    else
                    {
                        var tol = _scaleScreen.Bounds;
                        tol.Inflate(16, 16);
                        s = tol.Contains(Cursor.Position) ? _scaleScreen : m;
                    }
                }
                else s = Screen.FromControl(this);
                _scaleScreen = s;
                float n = St.DpiOf(s) / 96f;
                if (Math.Abs(n - _scale) > 0.01f)
                {
                    _scale = n;
                    _detail.DpiScale = n;
                    if (!_island) Width = CardWidth();
                    Rebuild(); // 窗口尺寸（岛宽高 / 卡片宽高）统一由 Rebuild 按当前排列重算
                    // 拖动中尺寸变了：把窗口贴回鼠标抓点（沿用设置窗口做法），避免重建后跳位
                    if (_dragging)
                        Location = new Point(Cursor.Position.X - _dragOffset.X, Cursor.Position.Y - _dragOffset.Y);
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
            Width = CardWidth();

            _detail = new DetailForm(ctx);
            _detail.DpiScale = St.Scale;
            _detail.RequestCancelHide = CancelHide;
            _detail.RequestArmHide = ArmHide;
            _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiTimer.Tick += delegate
            {
                _ctx.UpdateTrayTip();
                if (!Visible) return; // 关闭悬浮窗后仍刷新托盘摘要，不再重绘隐藏窗口。
                UpdateLayered();
                if (_detail.Visible) _detail.UpdateLayered();
            };
            _uiTimer.Start();

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Tick += delegate { _ctx.RefreshDue(false); };

            // 从首次离开悬浮窗和详情区域开始计时，重复移出事件和轮询不得重置倒计时。
            _hideTimer = new System.Windows.Forms.Timer { Interval = HoverHideDelayMs };
            _hideTimer.Tick += delegate
            {
                CancelHide();
                if (!KeepHoverOpen())
                {
                    HideDetail();
                    if (_edgeDocked) EdgeCollapse();
                }
            };

            _showTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _showTimer.Tick += delegate
            {
                _showTimer.Stop();
                if (Visible && !_contextMenuOpen && !_dragging && PointerOverWidget()) ShowDetail();
            };

            _trayRevealTimer = new System.Windows.Forms.Timer { Interval = TrayRevealMs };
            _trayRevealTimer.Tick += delegate
            {
                _trayRevealTimer.Stop();
                if (Visible && _edgeDocked && !KeepHoverOpen())
                {
                    HideDetail();
                    EdgeCollapse();
                }
            };

            _watchdogTimer = new System.Windows.Forms.Timer { Interval = WatchdogIntervalMs };
            _watchdogTimer.Tick += delegate { WatchdogTick(); };
            _watchdogTimer.Start();

            _island = _ctx.Cfg.FloatLayout == "island"; // 排列启动即生效（ApplyFromConfig 仅在切换排列时重置位置）
            ApplyFromConfig();
        }

        // 滑杆拖动时只更新透明度，不重建布局或重置查询、吸附计时器。
        public void ApplyOpacity(int percent)
        {
            int pct = Math.Max(50, Math.Min(100, percent));
            LayeredAlpha = (byte)(255 * pct / 100);
            _detail.LayeredAlpha = LayeredAlpha;
            UpdateLayered();
            if (_detail.Visible) _detail.UpdateLayered();
        }

        public void ApplyFromConfig()
        {
            _trayRevealTimer.Stop();
            if (_animTimer != null) _animTimer.Stop();
            var cfg = _ctx.Cfg;
            // 排列：矩形卡片 / 条形灵动岛（岛复用贴边吸附机制，仅外观与默认停靠不同）
            bool wasIsland = _island;
            _island = cfg.FloatLayout == "island";
            bool layoutChanged = _island != wasIsland;
            UpdateScale();
            Width = _island ? IslandWidth() : CardWidth();
            TopMost = cfg.TopMost;
            _detail.TopMost = cfg.TopMost;

            // 主题与不透明度应用到两个分层窗口并立即重绘
            St.Theme = cfg.Theme == "dark" ? 1 : 2;
            ApplyOpacity(cfg.OpacityPct);

            _refreshTimer.Stop();
            _refreshTimer.Interval = 1000; // 每秒检查独立到期时间，未到期的供应商不发请求。
            _refreshTimer.Start();
            _uiTimer.Start();
            _watchdogTimer.Start();

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
                    bool dockOuter = false;
                    if (cfg.Vertical && (cfg.DockSide == 1 || cfg.DockSide == 2))
                    {
                        // 旧版本可能把左右吸附保存在两屏内侧边：该边仍为外缘才恢复吸附，
                        // 否则退化为下面的竖向固定形态（不吸附、不收缩）
                        var dscr = Screen.FromPoint(new Point(cfg.X, cfg.Y));
                        dockOuter = EdgeIsOuterBoundary(dscr, dscr.WorkingArea, cfg.DockSide, new Point(cfg.X, cfg.Y));
                    }
                    if (dockOuter)
                    {
                        _vertical = true;
                        _edgeSide = cfg.DockSide;
                        _edgeDocked = true;
                        _edgeCollapsed = false;
                        _edgeJustUndocked = false;
                        _edgeTargetY = cfg.Y;
                        Rebuild();      // 竖向尺寸 + 贴边展开落位；鼠标不在窗口上时由悬停看守延迟收起
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
                    // 位置贴住顶边视为吸附态（与手动拖到顶边等效）；否则为普通悬浮条不自动隐藏；
                    // 上下排列双屏中下屏顶边属内侧边，不按吸附恢复
                    var tscr = Screen.FromControl(this);
                    var wa = tscr.WorkingArea;
                    _edgeDocked = Top <= wa.Top + St.SiF(EdgeThreshold, _scale)
                        && EdgeIsOuterBoundary(tscr, wa, 0, new Point(Left + Width / 2, Top));
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
            // 矩形卡片：上面只定了位置（宽度已在前面按排列算过），高度仍是窗体默认值，
            // 必须按当前供应商数量算一次尺寸。岛形与竖向分支内部已各自调用 Rebuild，
            // 这里补上后三种排列首屏形态都直接正确
            Rebuild();
        }

        // 托盘仅定位已开启的窗口。重复单击重新计时，普通悬浮/固定窗口保持常驻。
        public void RevealFromTray()
        {
            if (!Visible) return;
            _trayRevealTimer.Stop();
            if (_edgeDocked && !PointerOverWidget())
                _trayRevealTimer.Start(); // 先保护展开期，避免鼠标事件或悬停看守提前收起。
            if (_edgeDocked) EdgeExpand();
            BringToFront();
        }

        public void SetContextMenuOpen(bool open)
        {
            _contextMenuOpen = open;
            _trayRevealTimer.Stop(); _showTimer.Stop(); _hideTimer.Stop();
            if (open || !Visible) return;
            if (PointerOverWidget()) _showTimer.Start();
            else ArmHide(); // 菜单释放后留出正常的鼠标移动宽限期。
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) return;
            if (_trayRevealTimer != null) _trayRevealTimer.Stop();
            if (_showTimer != null) _showTimer.Stop();
            if (_hideTimer != null) _hideTimer.Stop();
            if (AnimRunning)
            {
                _animTimer.Stop();
                Location = _animTarget;
            }
            _dragging = false;
            _edgeArmed = false;
            HideGhost();
            if (_detail != null) HideDetail();
        }

        // Alt+F4 / WM_CLOSE 走正规退出流程（清理托盘图标），否则消息循环直接结束、
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
                if (_edgeDocked)
                {
                    // 内容宽度变化后，同步停靠目标与悬停区域，避免右侧圆角落在旧范围外。
                    _edgeTargetX = Left;
                    if (AnimRunning) _animTarget.X = Left;
                }
            }
            else
            {
                Width = CardWidth();   // 数据刷新后数字变化 → 重算卡片宽（左缘不动，右侧伸缩）
                Height = PadYS * 2 + _ctx.Providers.Count * RowHS;
            }
            UpdateLayered();
            if (_detail.Visible) ShowDetail();
        }

        // 卡片宽度 = 各行最大值：左列（图标或名称）+ 最小间隙 16 + 右列（失败/金额/百分比）+ 右边距 12，
        // 与 DrawRow 同源测量；向上取整到 8 DIP 台阶，百分比/金额数字变化不引起宽度抖动
        private int CardWidth()
        {
            float s = _scale;
            var fs = St.FontsForScale(s);
            bool dark = St.Theme == 1;
            float maxRow = 0;
            using (var g = CreateGraphics())
            {
                foreach (var st in _ctx.Providers)
                {
                    bool err = st.Error != null;
                    var pool = st.Primary5h;
                    float lw = (!st.ShowName && BrandIcons.Get(st.IconType, dark) != null)
                        ? St.SiF(12 + 16, s)
                        : g.MeasureString(st.WidgetName, fs[0]).Width + St.SiF(12, s);
                    float rw = 0;
                    if (err) rw = g.MeasureString("失败", fs[2]).Width;
                    else if (pool != null)
                    {
                        string t = pool.IsMoney ? pool.Currency + St.FmtMoney(pool.Remaining)
                            : ((100 - Math.Max(0, Math.Min(100, pool.Percent))) + "%");
                        rw = g.MeasureString(t, fs[1]).Width;
                    }
                    float row = lw + St.SiF(16, s) + rw + St.SiF(12, s);
                    if (row > maxRow) maxRow = row;
                }
            }
            int step = Math.Max(1, St.SiF(8, s));
            int w = ((int)Math.Ceiling(maxRow) + step - 1) / step * step;
            return Math.Max(w, St.SiF(80, s));   // 下限：空数据/极短内容时保持可点可拖
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
                    x += St.DrawBadge(g, badges[i], x, Height / 2f, textY, s, fs[0], fs[1], 38, 4f, dark) + gap;
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
                if (st.ShowName)
                {
                    using (var b = new SolidBrush(St.Text))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                        g.DrawString(st.WidgetName, fs[1], b, new RectangleF(St.SiF(4, _scale), y, Width - St.SiF(8, _scale), ringD), sf);
                }
                else St.DrawRingIcon(g, st.IconType, err ? 0 : remain, c, Width / 2f, y + ringD / 2f, _scale, 38, 4f, dark);
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
            // 拖拽属于移动意图：停掉悬停唤出计时并收起详情面板，拖拽中不再弹出
            _showTimer.Stop();
            if (_detail.Visible) HideDetail();
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
            var scr = Screen.FromPoint(Cursor.Position);
            var wa = scr.WorkingArea;
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
            // 多联屏：两屏共享的内侧边不吸附——虚影会在两屏边缘间来回闪；
            // 且收起后窗口大半在邻屏，Screen.FromControl 判到邻屏会把它吸附到
            // 邻屏外缘"飘走"。只认探测后仍属于本屏的桌面外缘
            if (side >= 0 && !EdgeIsOuterBoundary(scr, wa, side, Cursor.Position)) side = -1;
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
            // 松手后鼠标仍悬停时恢复悬停唤出计时（与纯悬停行为一致，1s 后弹出详情）
            _showTimer.Start();
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
            _trayRevealTimer.Stop(); // 鼠标接管后恢复正常悬停/移出行为。
            CancelHide();
            if (_edgeDocked) EdgeExpand();
            // 透明圆角或动画产生的重复进入事件不重置详情唤出时间。
            if (!_contextMenuOpen && !_dragging && !_detail.Visible && !_showTimer.Enabled) _showTimer.Start();
        }
        private void OnLeaveAny(object s, EventArgs e)
        {
            // 分层窗口的透明圆角、滑动动画都会产生 MouseLeave；仍在展开区域内时忽略。
            if (PointerOverWidget()) return;
            _showTimer.Stop(); ArmHide();
        }

        private bool PointerOverWidget()
        {
            if (!Visible) return false;
            // 展开时使用固定目标区域，不跟随动画中的窗口位置或圆角透明像素跳变。
            Rectangle area = _edgeDocked && !_edgeCollapsed ? EdgeExpandBounds() : Bounds;
            if (_edgeDocked && !_edgeCollapsed) area.Inflate(St.SiF(2, _scale), St.SiF(2, _scale));
            return area.Contains(Cursor.Position);
        }

        // ---- 贴边隐藏逻辑 ----
        // 工作区某条边是否为桌面外缘：向边外 2px 探测一点，若落到其他屏幕上则该边是
        // 两屏共享的内侧边（在此吸附窗口会盖到邻屏上、收起后还会被邻屏"抢走"）
        private static bool EdgeIsOuterBoundary(Screen s, Rectangle wa, int side, Point pt)
        {
            try
            {
                Point probe = side == 0 ? new Point(pt.X, wa.Top - 2)
                    : side == 1 ? new Point(wa.Left - 2, pt.Y)
                    : new Point(wa.Right + 2, pt.Y);
                return Screen.FromPoint(probe).DeviceName == s.DeviceName;
            }
            catch { return true; }   // 探测失败按外缘处理，不影响单屏正常吸附
        }

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
            if (_edgeCollapsed || _dragging || _contextMenuOpen || _trayRevealTimer.Enabled || _hideTimer.Enabled) return;
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
            _trayRevealTimer.Stop();
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
            if (!Visible || _ctx.Providers.Count == 0) return;
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

        private bool PointerOverHoverArea()
        {
            return PointerOverWidget() || (_detail.Visible && _detail.Bounds.Contains(Cursor.Position));
        }

        private bool KeepHoverOpen()
        {
            return !Visible || _contextMenuOpen || _dragging || _trayRevealTimer.Enabled || PointerOverHoverArea();
        }

        public void ArmHide()
        {
            if (KeepHoverOpen()) { CancelHide(); return; }
            _showTimer.Stop();
            if (!_hideTimer.Enabled && ((_edgeDocked && !_edgeCollapsed) || _detail.Visible)) _hideTimer.Start();
        }
        public void CancelHide() { _hideTimer.Stop(); }

        // ---- 悬停看守（兜底）----
        // 模态配置窗口会禁用同线程窗体的鼠标消息，透明圆角也可能漏掉进出事件。
        // 定时检查固定区域，补齐进入与离开；沿用原有计时，重复检查不重新计时。
        private void WatchdogTick()
        {
            if (!Visible) return;
            if (PointerOverWidget())
            {
                OnEnterAny(this, EventArgs.Empty);
                return;
            }
            if (_detail.Visible && _detail.Bounds.Contains(Cursor.Position)) _trayRevealTimer.Stop();
            ArmHide();
        }

        // 岛体展开后占用的固定矩形：圆角和动画期间都按同一范围判断悬停。
        private Rectangle EdgeExpandBounds()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            if (VerticalDocked)
                return new Rectangle(_edgeSide == 1 ? wa.Left : wa.Right - Width, _edgeTargetY, Width, Height);
            return new Rectangle(_edgeTargetX, wa.Top, Width, Height);
        }

        // 关面板统一入口：清掉共用的移出倒计时，避免下次进入时沿用旧计时。
        private void HideDetail()
        {
            _detail.Hide();
            // Hide 可能同步触发详情的 MouseLeave，在其回调之后清理，避免重新启动计时。
            CancelHide();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_uiTimer != null) _uiTimer.Dispose();
                if (_refreshTimer != null) _refreshTimer.Dispose();
                if (_hideTimer != null) _hideTimer.Dispose();
                if (_showTimer != null) _showTimer.Dispose();
                if (_trayRevealTimer != null) _trayRevealTimer.Dispose();
                if (_watchdogTimer != null) _watchdogTimer.Dispose();
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

    // ============================= 详情面板 =============================
    internal class DetailForm : LayeredForm
    {
        private readonly AppContext _ctx;
        private const int BaseW = 380;

        // 由宿主窗口 FloatingForm 设置，解耦对具体窗口类型的依赖
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
            var pal = SettingsPal.For(St.Theme == 1);
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Si(12)))
            {
                using (var brush = new SolidBrush(pal.Win))
                    g.FillPath(brush, path);
                using (var pen = new Pen(pal.Line, 1f))
                    g.DrawPath(pen, path);
            }

            int y = Si(14);
            for (int i = 0; i < _ctx.Providers.Count; i++)
            {
                // 分割线居中放入供应商之间的留白，首尾不额外画线。
                if (i > 0)
                    using (var pen = new Pen(pal.Line, 1f))
                        g.DrawLine(pen, Si(16), y - Si(5), Width - Si(16), y - Si(5));
                var p = _ctx.Providers[i];
                // 标题行左侧品牌图标：浅色主题用彩色源、深色主题用白色变体，随主题自动切换；
                // 图标垂直对齐标题行文字中心；未定义图标的类型回退为原来的彩色圆点
                bool dark = St.Theme == 1;
                int isz = Si(16);
                if (BrandIcons.Get(p.IconType, dark) != null)
                    BrandIcons.Draw(g, p.IconType, dark, Si(16), (int)Math.Round(y + Si(2) + (fs[0].GetHeight(g) - isz) / 2f), isz);
                else
                    using (var b = new SolidBrush(St.AccentFor(p.Type)))
                        g.FillEllipse(b, Si(16), y + Si(6), Si(9), Si(9));
                using (var b = new SolidBrush(pal.Text))
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
                        bool available = p.Type == "ds" && right == "可用";
                        using (var b = new SolidBrush(available ? pal.GreenBg : pal.Pill))
                        using (var path = St.RoundRect(pill, Si(8)))
                            g.FillPath(b, path);
                        using (var b = new SolidBrush(available ? pal.Green : pal.PillText))
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
                    using (var b = new SolidBrush(pal.Danger))
                        g.DrawString("⚠ " + msg, fs[2], b, Si(36), y + Si(4));
                    y += Si(34);
                }
                else
                {
                    foreach (var pool in p.Pools)
                    {
                        if (pool.IsMoney)
                        {
                            using (var b = new SolidBrush(pal.Text))
                                g.DrawString(pool.Label, fs[1], b, Si(36), y);
                            string moneyTxt = pool.Currency + St.FmtMoney(pool.Remaining);
                            var msz = g.MeasureString(moneyTxt, fs[1]);
                            using (var b = new SolidBrush(pal.Text))
                                g.DrawString(moneyTxt, fs[1], b, Width - msz.Width - Si(18), y);
                            y += Si(22);
                            continue;
                        }
                        int pct = Math.Max(0, Math.Min(100, pool.Percent));
                        int remain = 100 - pct;
                        // 额度进度和百分比固定使用浅色状态色，深色下也保持相同浓度。
                        Color c = SettingsPal.For(false).RemainColor(remain);
                        using (var b = new SolidBrush(pal.Text))
                            g.DrawString(pool.Label, fs[1], b, Si(36), y);
                        string pctTxt = remain + "%";
                        var psz = g.MeasureString(pctTxt, fs[1]);
                        using (var b = new SolidBrush(c))
                            g.DrawString(pctTxt, fs[1], b, Width - psz.Width - Si(18), y);
                        y += Si(19);
                        St.DrawBar(g, new Rectangle(Si(36), y, Width - Si(54), Si(6)), remain, c, pal.Track);
                        y += Si(11);
                        string nums = pool.PercentOnly
                            ? "剩余 " + St.FmtNum(pool.Remaining) + "%" + (string.IsNullOrEmpty(pool.Note) ? "" : "　" + pool.Note)
                            : "已用 " + St.FmtNum(pool.Used) + " / " + St.FmtNum(pool.Total) + "　剩余 " + St.FmtNum(pool.Remaining);
                        using (var b = new SolidBrush(pal.Text2))
                            g.DrawString(nums, fs[2], b, Si(36), y);
                        if (pool.ResetLocal.HasValue)
                        {
                            string cd = "重置 " + St.FmtCountdown(pool.ResetLocal);
                            var csz = g.MeasureString(cd, fs[2]);
                            using (var b = new SolidBrush(pal.Text2))
                                g.DrawString(cd, fs[2], b, Width - csz.Width - Si(18), y);
                        }
                        y += Si(15);
                    }
                }
                y += Si(10);
            }
        }
    }

    // ============================= 旧版置顶栏工作区清理 =============================
    // 升级兼容：仅检测到旧残留文件才恢复；不再创建快照或预留屏幕空间。
    internal static class LegacyWorkAreaCleanup
    {
        // 仅用于恢复旧版遗留的工作区与最大化窗口。
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

        private static List<Rectangle> _originalWorkAreas;
        private static bool _originalLoaded;
        private static string WorkAreaFile { get { return Path.Combine(Path.GetTempPath(), "QuotaWidget-workarea.txt"); } }

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
                            if (list[i].Width <= 0 || list[i].Height <= 0 || list[i].Left < b.Left || list[i].Top < b.Top ||
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
                // 屏数/分辨率变化导致旧快照无效时，先净化顶部残留再恢复。
                bool sanitize = fileExists;
                _originalWorkAreas = new List<Rectangle>();
                foreach (var s in screens) _originalWorkAreas.Add(sanitize ? SanitizeOriginalArea(s) : s.WorkingArea);
            }
            return _originalWorkAreas;
        }

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

        // ===== SVG 矢量图标：codicon（VS Code，MIT）/ lucide（ISC）官方 path 数据内嵌 =====
        // 网页参考页（icons-reference.html）里选中的图标即这套几何，运行时解析 SVG path
        // 为 GraphicsPath 后按官方原路径填充/描边，渲染结果与网页逐点一致，不再手绘近似。

        private const string DServer =
            "M12 11.5C12 11.5989 11.9707 11.6956 11.9157 11.7778C11.8608 11.86 11.7827 11.9241 11.6913 11.9619C11.6 11.9998 11.4994 12.0097 11.4025 11.9904C11.3055 11.9711 11.2164 11.9235 11.1464 11.8536C11.0765 11.7836 11.0289 11.6945 11.0096 11.5975C10.9903 11.5006 11.0002 11.4 11.0381 11.3087C11.0759 11.2173 11.14 11.1392 11.2222 11.0843C11.3044 11.0293 11.4011 11 11.5 11C11.6326 11 11.7598 11.0527 11.8536 11.1464C11.9473 11.2402 12 11.3674 12 11.5ZM11.5 8C11.5989 8 11.6956 7.97068 11.7778 7.91573C11.86 7.86079 11.9241 7.7827 11.9619 7.69134C11.9998 7.59998 12.0097 7.49945 11.9904 7.40245C11.9711 7.30546 11.9235 7.21637 11.8536 7.14645C11.7836 7.07652 11.6945 7.0289 11.5975 7.00961C11.5006 6.99031 11.4 7.00022 11.3087 7.03806C11.2173 7.0759 11.1392 7.13999 11.0843 7.22221C11.0293 7.30444 11 7.40111 11 7.5C11 7.63261 11.0527 7.75979 11.1464 7.85355C11.2402 7.94732 11.3674 8 11.5 8ZM14 4.5C13.999 4.87026 13.86 5.22685 13.61 5.5C13.86 5.77315 13.999 6.12974 14 6.5V8.5C13.999 8.87026 13.86 9.22685 13.61 9.5C13.86 9.77315 13.999 10.1297 14 10.5V12.5C14 12.8978 13.842 13.2794 13.5607 13.5607C13.2794 13.842 12.8978 14 12.5 14H3.5C3.10218 14 2.72064 13.842 2.43934 13.5607C2.15804 13.2794 2 12.8978 2 12.5V10.5C2.00097 10.1297 2.14003 9.77315 2.39 9.5C2.14003 9.22685 2.00097 8.87026 2 8.5V6.5C2.00097 6.12974 2.14003 5.77315 2.39 5.5C2.14003 5.22685 2.00097 4.87026 2 4.5V2.5C2 2.10218 2.15804 1.72064 2.43934 1.43934C2.72064 1.15804 3.10218 1 3.5 1H12.5C12.8978 1 13.2794 1.15804 13.5607 1.43934C13.842 1.72064 14 2.10218 14 2.5V4.5ZM3 4.5C3 4.63261 3.05268 4.75979 3.14645 4.85355C3.24021 4.94732 3.36739 5 3.5 5H12.5C12.6326 5 12.7598 4.94732 12.8536 4.85355C12.9473 4.75979 13 4.63261 13 4.5V2.5C13 2.36739 12.9473 2.24021 12.8536 2.14645C12.7598 2.05268 12.6326 2 12.5 2H3.5C3.36739 2 3.24021 2.05268 3.14645 2.14645C3.05268 2.24021 3 2.36739 3 2.5V4.5ZM12.5 6H3.5C3.36739 6 3.24021 6.05268 3.14645 6.14645C3.05268 6.24021 3 6.36739 3 6.5V8.5C3 8.63261 3.05268 8.75979 3.14645 8.85355C3.24021 8.94732 3.36739 9 3.5 9H12.5C12.6326 9 12.7598 8.94732 12.8536 8.85355C12.9473 8.75979 13 8.63261 13 8.5V6.5C13 6.36739 12.9473 6.24021 12.8536 6.14645C12.7598 6.05268 12.6326 6 12.5 6ZM13 10.5C13 10.3674 12.9473 10.2402 12.8536 10.1464C12.7598 10.0527 12.6326 10 12.5 10H3.5C3.36739 10 3.24021 10.0527 3.14645 10.1464C3.05268 10.2402 3 10.3674 3 10.5V12.5C3 12.6326 3.05268 12.7598 3.14645 12.8536C3.24021 12.9473 3.36739 13 3.5 13H12.5C12.6326 13 12.7598 12.9473 12.8536 12.8536C12.9473 12.7598 13 12.6326 13 12.5V10.5ZM11.5 4C11.5989 4 11.6956 3.97068 11.7778 3.91573C11.86 3.86079 11.9241 3.7827 11.9619 3.69134C11.9998 3.59998 12.0097 3.49945 11.9904 3.40245C11.9711 3.30546 11.9235 3.21637 11.8536 3.14645C11.7836 3.07652 11.6945 3.0289 11.5975 3.00961C11.5006 2.99031 11.4 3.00022 11.3087 3.03806C11.2173 3.0759 11.1392 3.13999 11.0843 3.22221C11.0293 3.30444 11 3.40111 11 3.5C11 3.63261 11.0527 3.75979 11.1464 3.85355C11.2402 3.94732 11.3674 4 11.5 4Z";
        private const string DGear =
            "M12 9C10.3425 9 9.00002 10.3425 9.00002 12C9.00002 13.6575 10.3425 15 12 15C13.6575 15 15 13.6575 15 12C15 10.3425 13.6575 9 12 9ZM12 13.5C11.172 13.5 10.5 12.828 10.5 12C10.5 11.172 11.172 10.5 12 10.5C12.828 10.5 13.5 11.172 13.5 12C13.5 12.828 12.828 13.5 12 13.5ZM21.8475 14.5725L19.9185 12.942C19.8675 12.8985 19.8195 12.8505 19.776 12.7995C19.332 12.279 19.3965 11.5005 19.9185 11.058L21.8475 9.4275C22.0395 9.2655 22.113 9.0045 22.0365 8.766C21.579 7.3545 20.823 6.06 19.8285 4.962C19.7085 4.83 19.5405 4.758 19.368 4.758C19.2975 4.758 19.227 4.77 19.1595 4.794L16.779 5.6415C16.716 5.664 16.65 5.682 16.584 5.694C16.509 5.7075 16.434 5.715 16.3605 5.715C15.7725 5.715 15.2505 5.298 15.141 4.701L14.6865 2.223C14.6415 1.977 14.451 1.782 14.205 1.7295C13.485 1.5765 12.7485 1.5 12.0015 1.5C11.2545 1.5 10.5165 1.578 9.79652 1.7295C9.55052 1.782 9.36002 1.977 9.31502 2.223L8.86202 4.701C8.85002 4.767 8.83202 4.8315 8.80952 4.8945C8.62802 5.4 8.15102 5.715 7.64102 5.715C7.50302 5.715 7.36202 5.691 7.22402 5.643L4.84352 4.7955C4.77602 4.7715 4.70402 4.7595 4.63502 4.7595C4.46252 4.7595 4.29452 4.8315 4.17452 4.9635C3.17852 6.0615 2.42402 7.356 1.96502 8.7675C1.88702 9.006 1.96202 9.267 2.15402 9.429L4.08302 11.0595C4.13402 11.103 4.18202 11.151 4.22552 11.202C4.66952 11.7225 4.60502 12.501 4.08302 12.9435L2.15402 14.574C1.96202 14.736 1.88852 14.997 1.96502 15.2355C2.42252 16.647 3.17852 17.9415 4.17452 19.0395C4.29452 19.1715 4.46252 19.2435 4.63502 19.2435C4.70552 19.2435 4.77602 19.2315 4.84352 19.2075L7.22402 18.36C7.28702 18.3375 7.35302 18.3195 7.41902 18.307C7.49402 18.294 7.56902 18.288 7.64252 18.288C8.23052 18.288 8.75252 18.705 8.86202 19.302L9.31502 21.78C9.36002 22.026 9.55052 22.221 9.79652 22.2735C10.5165 22.4265 11.2545 22.503 12.0015 22.503C12.7485 22.503 13.4865 22.425 14.205 22.2735C14.451 22.221 14.6415 22.026 14.6865 21.78L15.141 19.302C15.153 19.236 15.171 19.1715 15.1935 19.1085C15.375 18.603 15.852 18.288 16.362 18.288C16.5 18.288 16.641 18.312 16.779 18.36L19.158 19.2075C19.227 19.2315 19.2975 19.2435 19.3665 19.2435C19.539 19.2435 19.707 19.1715 19.827 19.0395C20.823 17.9415 21.5775 16.647 22.035 15.2355C22.113 14.997 22.038 14.736 21.846 14.574L21.8475 14.5725ZM19.092 17.589L17.2815 16.944C16.9845 16.839 16.6755 16.785 16.362 16.785C15.2085 16.785 14.1705 17.514 13.782 18.5985C13.731 18.738 13.6935 18.882 13.6665 19.029L13.3215 20.9055C12.8865 20.9685 12.444 21 12.0015 21C11.559 21 11.1165 20.9685 10.68 20.904L10.3365 19.0275C10.098 17.727 8.96552 16.7835 7.64252 16.7835C7.48052 16.7835 7.31552 16.7985 7.14902 16.8285C7.00352 16.8555 6.86102 16.893 6.72002 16.9425L4.90952 17.5875C4.35752 16.896 3.91652 16.1385 3.59102 15.321L5.05202 14.0865C5.61152 13.614 5.95202 12.951 6.01202 12.222C6.07202 11.493 5.84252 10.785 5.36702 10.227C5.27102 10.1145 5.16452 10.008 5.05202 9.912L3.59102 8.6775C3.91652 7.86 4.35752 7.101 4.90952 6.411L6.72002 7.056C7.01702 7.161 7.32602 7.215 7.64102 7.215C8.79452 7.215 9.83252 6.486 10.221 5.4015C10.272 5.2605 10.3095 5.1165 10.3365 4.971L10.68 3.0945C11.1165 3.0315 11.559 2.9985 12.0015 2.9985C12.444 2.9985 12.8865 3.03 13.3215 3.093L13.665 4.9695C13.9035 6.27 15.036 7.2135 16.359 7.2135C16.521 7.2135 16.686 7.1985 16.851 7.1685C16.9965 7.1415 17.1405 7.104 17.2815 7.0545L19.092 6.4095C19.644 7.0995 20.085 7.8585 20.4105 8.676L18.951 9.9105C18.3915 10.383 18.0495 11.046 17.991 11.775C17.931 12.504 18.1605 13.2135 18.636 13.77C18.7335 13.884 18.8385 13.989 18.9525 14.085L20.4135 15.3195C20.088 16.137 19.647 16.896 19.095 17.586L19.092 17.589Z";
        private const string DInfo =
            "M8.49902 7.49998C8.49902 7.22384 8.27517 6.99998 7.99902 6.99998C7.72288 6.99998 7.49902 7.22384 7.49902 7.49998V10.5C7.49902 10.7761 7.72288 11 7.99902 11C8.27517 11 8.49902 10.7761 8.49902 10.5V7.49998ZM8.74807 5.50001C8.74807 5.91369 8.41271 6.24905 7.99903 6.24905C7.58535 6.24905 7.25 5.91369 7.25 5.50001C7.25 5.08633 7.58535 4.75098 7.99903 4.75098C8.41271 4.75098 8.74807 5.08633 8.74807 5.50001ZM8 1C4.13401 1 1 4.13401 1 8C1 11.866 4.13401 15 8 15C11.866 15 15 11.866 15 8C15 4.13401 11.866 1 8 1ZM2 8C2 4.68629 4.68629 2 8 2C11.3137 2 14 4.68629 14 8C14 11.3137 11.3137 14 8 14C4.68629 14 2 11.3137 2 8Z";
        private const string DColorMode =
            "M8 1.00195C6.61553 1.00195 5.26216 1.4125 4.11101 2.18167C2.95987 2.95084 2.06266 4.04409 1.53285 5.32317C1.00303 6.60225 0.86441 8.00972 1.13451 9.36759C1.4046 10.7255 2.07129 11.9727 3.05026 12.9517C4.02922 13.9307 5.27651 14.5974 6.63437 14.8675C7.99224 15.1375 9.3997 14.9989 10.6788 14.4691C11.9579 13.9393 13.0511 13.0421 13.8203 11.8909C14.5895 10.7398 15 9.38642 15 8.00195C15 6.14544 14.2625 4.36496 12.9498 3.05221C11.637 1.73945 9.85652 1.00195 8 1.00195ZM8 14.002V2.00195C9.5913 2.00195 11.1174 2.63409 12.2426 3.75931C13.3679 4.88453 14 6.41065 14 8.00195C14 9.59325 13.3679 11.1194 12.2426 12.2446C11.1174 13.3698 9.5913 14.002 8 14.002Z";
        private const string DSun =
            "M8 12a4 4 0 1 0 8 0a4 4 0 1 0 -8 0ZM12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41";
        private const string DMoon =
            "M20.985 12.486a9 9 0 1 1-9.473-9.472c.405-.022.617.46.402.803a6 6 0 0 0 8.268 8.268c.344-.215.825-.004.803.401";

        private static GraphicsPath _pServer, _pGear, _pInfo, _pColor, _pSun, _pMoon;
        private static GraphicsPath P(ref GraphicsPath cache, string d)
        {
            if (cache == null) cache = ParseSvgPath(d);
            return cache;
        }

        // SVG path → GraphicsPath（M/L/H/V/C/A/Z 及相对命令；圆弧按 ≤90° 分段转三阶贝塞尔）
        private static GraphicsPath ParseSvgPath(string d)
        {
            var path = new GraphicsPath();
            float x = 0, y = 0, sx = 0, sy = 0;
            int i = 0;
            char cmd = ' ';
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            while (i < d.Length)
            {
                if (!char.IsLetter(d[i])) { i++; continue; }
                cmd = d[i]; i++;
                var n = new List<float>();
                while (i < d.Length)
                {
                    while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
                    if (i >= d.Length || (char.IsLetter(d[i]))) break;
                    int start = i;
                    if (d[i] == '-' || d[i] == '+') i++;
                    bool dot = false;
                    while (i < d.Length)
                    {
                        char c = d[i];
                        if (char.IsDigit(c)) i++;
                        else if (c == '.' && !dot) { dot = true; i++; }
                        else break;
                    }
                    n.Add(float.Parse(d.Substring(start, i - start), inv));
                }
                bool rel = char.IsLower(cmd);
                char C = char.ToUpper(cmd);
                int j = 0;
                while (j < n.Count)
                {
                    int take = (C == 'C' || C == 'A') ? 6 + (C == 'A' ? 1 : 0) : (C == 'M' || C == 'L') ? 2 : (C == 'H' || C == 'V') ? 1 : 0;
                    if (j + take > n.Count) break;
                    switch (C)
                    {
                        case 'M':
                            {
                                float nx = n[j] + (rel ? x : 0), ny = n[j + 1] + (rel ? y : 0); j += 2;
                                path.StartFigure();
                                x = sx = nx; y = sy = ny;
                                C = 'L';   // M 后隐式重复的坐标按 L 处理
                                break;
                            }
                        case 'L':
                            {
                                float nx = n[j] + (rel ? x : 0), ny = n[j + 1] + (rel ? y : 0); j += 2;
                                path.AddLine(x, y, nx, ny); x = nx; y = ny;
                                break;
                            }
                        case 'H':
                            {
                                float nx = n[j] + (rel ? x : 0); j += 1;
                                path.AddLine(x, y, nx, y); x = nx;
                                break;
                            }
                        case 'V':
                            {
                                float ny = n[j] + (rel ? y : 0); j += 1;
                                path.AddLine(x, y, x, ny); y = ny;
                                break;
                            }
                        case 'C':
                            {
                                float c1x = n[j] + (rel ? x : 0), c1y = n[j + 1] + (rel ? y : 0);
                                float c2x = n[j + 2] + (rel ? x : 0), c2y = n[j + 3] + (rel ? y : 0);
                                float ex = n[j + 4] + (rel ? x : 0), ey = n[j + 5] + (rel ? y : 0); j += 6;
                                path.AddBezier(x, y, c1x, c1y, c2x, c2y, ex, ey); x = ex; y = ey;
                                break;
                            }
                        case 'A':
                            {
                                float rx = n[j], ry = n[j + 1], rot = n[j + 2];
                                bool large = n[j + 3] != 0, sweep = n[j + 4] != 0;
                                float ex = n[j + 5] + (rel ? x : 0), ey = n[j + 6] + (rel ? y : 0); j += 7;
                                SvgArcToBeziers(path, x, y, rx, ry, rot, large, sweep, ex, ey, out x, out y);
                                break;
                            }
                        case 'Z':
                            path.CloseFigure(); x = sx; y = sy;
                            break;
                    }
                }
            }
            return path;
        }

        // W3C 端点参数化圆弧 → 贝塞尔分段（标准换算）
        private static void SvgArcToBeziers(GraphicsPath path, float x0, float y0, float rx, float ry, float rotDeg,
            bool large, bool sweep, float x1, float y1, out float ox, out float oy)
        {
            ox = x1; oy = y1;
            if (rx == 0 || ry == 0 || (x0 == x1 && y0 == y1)) { path.AddLine(x0, y0, x1, y1); return; }
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            double phi = rotDeg * Math.PI / 180.0;
            double cosP = Math.Cos(phi), sinP = Math.Sin(phi);
            double dx2 = (x0 - x1) / 2.0, dy2 = (y0 - y1) / 2.0;
            double x1p = cosP * dx2 + sinP * dy2;
            double y1p = -sinP * dx2 + cosP * dy2;
            double lam = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
            if (lam > 1) { double s = Math.Sqrt(lam); rx = (float)(rx * s); ry = (float)(ry * s); }
            double sign = (large != sweep) ? 1 : -1;
            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double co = sign * Math.Sqrt(Math.Max(0, num) / den);
            double cxp = co * rx * y1p / ry;
            double cyp = -co * ry * x1p / rx;
            double cx = cosP * cxp - sinP * cyp + (x0 + x1) / 2.0;
            double cy = sinP * cxp + cosP * cyp + (y0 + y1) / 2.0;
            double th1 = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
            double dth = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx) - th1;
            if (!sweep && dth > 0) dth -= 2 * Math.PI;
            else if (sweep && dth < 0) dth += 2 * Math.PI;
            int segs = (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 2));
            if (segs < 1) segs = 1;
            double delta = dth / segs;
            double t = 4.0 / 3 * Math.Tan(delta / 4);
            double th = th1, px = x0, py = y0;
            for (int s = 0; s < segs; s++)
            {
                double th2 = th + delta;
                double c1 = Math.Cos(th), s1 = Math.Sin(th), c2 = Math.Cos(th2), s2 = Math.Sin(th2);
                double ex = cx + rx * cosP * c2 - ry * sinP * s2;
                double ey = cy + rx * sinP * c2 + ry * cosP * s2;
                double d1x = t * (-rx * cosP * s1 - ry * sinP * c1);
                double d1y = t * (-rx * sinP * s1 + ry * cosP * c1);
                double d2x = t * (rx * cosP * s2 + ry * sinP * c2);
                double d2y = t * (rx * sinP * s2 - ry * cosP * c2);
                path.AddBezier((float)px, (float)py, (float)(px + d1x), (float)(py + d1y),
                    (float)(ex + d2x), (float)(ey + d2y), (float)ex, (float)ey);
                px = ex; py = ey; th = th2;
            }
        }

        // 按官方路径填充（codicon 系，viewBox 见各常量注释）到目标矩形
        private static void FillIcon(Graphics g, string d, ref GraphicsPath cache, float vb, float x, float y, float size, Color c)
        {
            using (var m = new Matrix())
            {
                m.Translate(x, y);
                m.Scale(size / vb, size / vb);
                using (var gp = (GraphicsPath)P(ref cache, d).Clone())
                {
                    gp.Transform(m);
                    using (var b = new SolidBrush(c))
                        g.FillPath(b, gp);
                }
            }
        }

        // 按官方路径描边（lucide 系，1.5 线宽圆头，与参考页一致）
        private static void StrokeIcon(Graphics g, string d, ref GraphicsPath cache, float vb, float x, float y, float size, Color c)
        {
            using (var m = new Matrix())
            {
                m.Translate(x, y);
                m.Scale(size / vb, size / vb);
                using (var gp = (GraphicsPath)P(ref cache, d).Clone())
                {
                    gp.Transform(m);
                    using (var pen = new Pen(c, 1.5f * size / vb) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawPath(pen, gp);
                }
            }
        }

        public static void DrawServer(Graphics g, float x, float y, float size, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillIcon(g, DServer, ref _pServer, 16, x, y, size, c);
        }

        public static void DrawGear(Graphics g, float x, float y, float size, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillIcon(g, DGear, ref _pGear, 24, x, y, size, c);
        }

        public static void DrawInfo(Graphics g, float x, float y, float size, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillIcon(g, DInfo, ref _pInfo, 16, x, y, size, c);
        }

        // codicon color-mode 官方几何为左半填充(偶奇规则),签名保持中心定位
        public static void DrawColorMode(Graphics g, float cx, float cy, float size, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillIcon(g, DColorMode, ref _pColor, 16, cx - size / 2, cy - size / 2, size, c);
        }

        public static void DrawSun(Graphics g, float cx, float cy, float size, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            StrokeIcon(g, DSun, ref _pSun, 24, cx - size / 2, cy - size / 2, size, c);
        }

        public static void DrawMoon(Graphics g, float cx, float cy, float size, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            StrokeIcon(g, DMoon, ref _pMoon, 24, cx - size / 2, cy - size / 2, size, c);
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
            // 子控件圆角外的底色必须与窗体实际绘制背景一致。
            BackColor = pal.Win;
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
            using (var b = new SolidBrush(Pal.Side))
                g.FillRectangle(b, 0, 0, Width, TitleH);

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
            using (var pen = new Pen(Pal.SectionLine))
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
                    using (var b = new SolidBrush(hot ? Pal.PrimaryHover : Pal.Primary))
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
    // GLM Coding Plan 团队版上下文：新版官方协议用 ?type=2 + bigmodel-organization/
    // bigmodel-project 区分团队额度，个人版不需要。IDs 来自 bigmodel.cn 控制台的请求头。
    internal class GlmTeamContextDialog : StyledDialogBase
    {
        private readonly TextBox _txtOrg, _txtProj;
        private Rectangle _rcSeg, _rcOrg, _rcProj, _rcOk, _rcCancel;
        private bool _team;
        private bool _hotSeg, _hotOk, _hotCancel;
        public string PlanType { get; private set; }
        public string Organization { get; private set; }
        public string Project { get; private set; }

        public GlmTeamContextDialog(string planType, string org, string project, SettingsPal pal, float scale)
            : base("Coding Plan 套餐上下文", pal, scale, new Size(St.SiF(420, scale), St.SiF(236, scale)))
        {
            _team = planType == "team";
            _txtOrg = new TextBox { BorderStyle = BorderStyle.None, BackColor = pal.Win, ForeColor = pal.Text, Font = Fs[1], Text = org ?? "" };
            _txtProj = new TextBox { BorderStyle = BorderStyle.None, BackColor = pal.Win, ForeColor = pal.Text, Font = Fs[1], Text = project ?? "" };
            Controls.Add(_txtOrg);
            Controls.Add(_txtProj);
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnOk(); }
            };
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            int x = S(20), w = S(420) - S(40);
            int h = _team ? S(340) : S(236);
            if (IsHandleCreated) Size = new Size(S(420), h); else ClientSize = new Size(S(420), h);
            _rcSeg = new Rectangle(x, S(46) + S(38), w, S(34));
            _rcOrg = _team ? new Rectangle(x, S(46) + S(112), w, S(34)) : Rectangle.Empty;
            _rcProj = _team ? new Rectangle(x, S(46) + S(186), w, S(34)) : Rectangle.Empty;
            _rcOk = new Rectangle(S(420) - S(20) - S(88), h - S(18) - S(34), S(88), S(34));
            _rcCancel = new Rectangle(_rcOk.X - S(10) - S(88), _rcOk.Y, S(88), S(34));
            _txtOrg.Visible = _team;
            _txtProj.Visible = _team;
            if (_team)
            {
                _txtOrg.Bounds = new Rectangle(_rcOrg.X + S(10), _rcOrg.Y + (S(34) - _txtOrg.PreferredHeight) / 2, _rcOrg.Width - S(20), _txtOrg.PreferredHeight);
                _txtProj.Bounds = new Rectangle(_rcProj.X + S(10), _rcProj.Y + (S(34) - _txtProj.PreferredHeight) / 2, _rcProj.Width - S(20), _txtProj.PreferredHeight);
            }
            Invalidate();
        }

        private void OnOk()
        {
            if (_team && (string.IsNullOrWhiteSpace(_txtOrg.Text) || string.IsNullOrWhiteSpace(_txtProj.Text)))
            {
                StyledMsg.Show(this, "提示", "团队版请填写组织 ID 和项目 ID", Pal, Sf);
                return;
            }
            PlanType = _team ? "team" : "personal";
            Organization = _team ? _txtOrg.Text.Trim() : "";
            Project = _team ? _txtProj.Text.Trim() : "";
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            DrawFieldLabel(g, "套餐类型", S(46) + S(10));
            PaintSegmented(g);
            if (_team)
            {
                DrawFieldLabel(g, "组织 ID（bigmodel-organization）", S(46) + S(84));
                DrawFieldBox(g, _rcOrg, _txtOrg.Focused);
                DrawFieldLabel(g, "项目 ID（bigmodel-project）", S(46) + S(158));
                DrawFieldBox(g, _rcProj, _txtProj.Focused);
            }
            else
            {
                TextRenderer.DrawText(g, "个人版无需填写；团队版请填写控制台请求头里的组织 / 项目 ID",
                    Fs[2], new Rectangle(S(20), S(140), ClientSize.Width - S(40), S(24)), Pal.Text2,
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            PaintBtn(g, _rcOk, "确认", true, _hotOk);
            PaintBtn(g, _rcCancel, "取消", false, _hotCancel);
        }

        private void PaintSegmented(Graphics g)
        {
            var r = _rcSeg;
            int half = r.Width / 2;
            var rl = new Rectangle(r.X, r.Y, half, r.Height);
            var rr = new Rectangle(r.X + half, r.Y, r.Width - half, r.Height);
            using (var path = St.RoundRect(_team ? rr : rl, S(8)))
            using (var b = new SolidBrush(Pal.Sel))
                g.FillPath(b, path);
            using (var path = St.RoundRect(r, S(8)))
            using (var pen = new Pen(_hotSeg ? Pal.CardHover : Pal.Line))
                g.DrawPath(pen, path);
            TextRenderer.DrawText(g, "个人版", Fs[1], rl, !_team ? Pal.Text : Pal.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, "团队版", Fs[1], rr, _team ? Pal.Text : Pal.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_rcSeg.Contains(e.Location))
            {
                bool team = e.X >= _rcSeg.X + _rcSeg.Width / 2;
                if (team != _team) { _team = team; ActiveControl = null; ApplyLayout(); }
                return;
            }
            if (_rcOk.Contains(e.Location)) { OnOk(); return; }
            if (_rcCancel.Contains(e.Location)) { Close(); return; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hs = _rcSeg.Contains(e.Location), ho = _rcOk.Contains(e.Location), hc = _rcCancel.Contains(e.Location);
            if (hs != _hotSeg || ho != _hotOk || hc != _hotCancel)
            {
                _hotSeg = hs; _hotOk = ho; _hotCancel = hc;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotSeg || _hotOk || _hotCancel)
            {
                _hotSeg = _hotOk = _hotCancel = false;
                Invalidate();
            }
        }
    }

    // ============================= 控制台 Cookie 获取 =============================
    // MiMo / 千问 / 百炼都没有能用 API Key 调通的额度接口，鉴权是网页控制台的登录 Cookie。
    // 「直接读本机浏览器里已登录的会话」这条路，本机实测过三条结论，三条都堵死：
    //   1) Edge / Chrome 运行期间，Cookies 数据库被独占锁住，任何其它进程都读不了（20 次重试 / 14 秒全失败）；
    //   2) Chrome 127+ 的 App-Bound 加密（v20）让外部解密不可行，且 136+ 禁止在默认配置目录上开调试端口
    //      （dsh-ego-local-browser 也记录了这两条，所以他们改用浏览器扩展桥）；
    //   3) MiMo 的登录态本身还是「会话 Cookie + HttpOnly」——实测把 655KB 的 Cookies 库复制出来，
    //      xiaomimimo.com 的记录数是 0：它只存在浏览器内存里，复制或重启都拿不到。
    // 所以只剩一条可靠路径，也是 dsh-subusage 的做法：开一个独立配置目录的窗口，让用户在里面登录一次，
    // 再从 CDP 读 Cookie。拿到的是快照，之后是否还有效只由接口返回决定（阿里云控制台 Cookie 一般
    // 能撑几周，MiMo 的会话 Cookie 更短——所以两者都不按时间主动作废）。
    internal sealed class ConsoleSite
    {
        internal readonly string Type;           // 与 ProviderConfig.Type 一致
        internal readonly string Brand;          // 界面文案里的品牌名
        internal readonly string HostSuffix;     // CDP 取 Cookie 时按域名后缀筛
        internal readonly string LoginUrl;       // 登录窗口打开的页面
        internal readonly string LoginDirName;   // 独立配置目录名（同一家共用，登录一次长期免登录）
        internal readonly string LogFileName;    // 诊断日志文件名
        internal readonly string[] CookieNames;  // null = 收该域下全部 Cookie

        internal ConsoleSite(string type, string brand, string hostSuffix, string loginUrl,
            string loginDirName, string logFileName, string[] cookieNames)
        {
            Type = type; Brand = brand; HostSuffix = hostSuffix; LoginUrl = loginUrl;
            LoginDirName = loginDirName; LogFileName = logFileName; CookieNames = cookieNames;
        }

        internal string LoginDir { get { return Path.Combine(Config.DataDir, LoginDirName); } }
        internal string LoginPrompt { get { return "请在浏览器窗口中登录" + Brand + "账号…"; } }
        internal string WaitingText { get { return "已打开浏览器窗口，" + LoginPrompt; } }
    }

    internal static class ConsoleBinder
    {
        private const int PollMs = 700;
        private const int LoginSeconds = 300;
        // 旧窗口收尾完成后才能启动新窗口，避免共用配置目录时误关下一次登录。
        private static readonly SemaphoreSlim LoginGate = new SemaphoreSlim(1, 1);

        // 千问与百炼共用同一个登录配置目录：两家控制台都是阿里云账号体系，
        // 在千问平台登录会顺带在 .aliyun.com 上留一份 passport 会话，第二家通常直接免登录
        internal static readonly ConsoleSite MiMo = new ConsoleSite("mimo", "小米",
            "xiaomimimo.com", QuotaService.MimoConsoleUrl, "mimo-login", "mimo.log",
            new string[] { "userId", "api-platform_serviceToken", "api-platform_slh", "api-platform_ph" });
        internal static readonly ConsoleSite Qwen = new ConsoleSite("qwen", "千问",
            "qianwenai.com", QuotaService.QwenLoginUrl, "aliyun-login", "aliyun.log", null);
        internal static readonly ConsoleSite Bailian = new ConsoleSite("bailian", "百炼",
            "aliyun.com", QuotaService.BailianConsoleUrl, "aliyun-login", "aliyun.log", null);

        internal static ConsoleSite SiteFor(string type)
        {
            if (type == "bailian") return Bailian;
            if (type == "mimo") return MiMo;
            return Qwen;
        }

        // worker 线程写、UI 线程读；字段用 volatile 保证可见性
        internal sealed class Progress
        {
            private volatile bool _cancel, _done;
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
            private volatile string _message = "", _cookie, _error, _summary;
            public string Message { get { return _message; } }
            public string Cookie { get { return _cookie; } }
            public string Error { get { return _error; } }
            public string Summary { get { return _summary; } }
            public bool Done { get { return _done; } }
            public bool Cancelled { get { return _cancel; } }
            internal CancellationToken Token { get { return _cancellation.Token; } }
            public void Cancel() { _cancel = true; _cancellation.Cancel(); }
            internal void SetMessage(string m) { if (!_cancel) _message = m; }
            internal void SetResult(string cookie, string error, string summary)
            { if (!_cancel) { _cookie = cookie; _error = error; _summary = summary; } }
            internal void Finish() { _done = true; }
        }

        internal static void AutoFetch(Progress state, ConsoleSite site, string existingCookie)
        {
            try
            {
                state.Token.ThrowIfCancellationRequested();
                string cookie = QuotaService.ConsoleCookie(existingCookie);
                if (ValidCookie(site, cookie))
                {
                    state.SetMessage("正在验证已有 Cookie…");
                    string summary;
                    if (Probe(site, cookie, out summary, state.Token))
                    {
                        state.SetResult(cookie, null, summary);
                        return;
                    }
                    state.SetMessage("已有 Cookie 已失效，正在打开登录窗口…");
                }
                state.Token.ThrowIfCancellationRequested();
                string found;
                if (OpenLoginWindow(state, site, out found))
                {
                    state.SetResult(found, null, "已登录并获取 Cookie");
                    return;
                }
                state.SetResult(null, state.Cancelled ? "已取消" : "没有拿到" + site.Brand + "登录 Cookie", null);
            }
            catch (Exception ex)
            {
                state.SetResult(null, Unwrap(ex).Message, null);
            }
            finally
            {
                state.Finish();
            }
        }

        // 结构预检：只判"看着像"，真正是否有效一律由 Probe 打接口决定
        internal static bool ValidCookie(ConsoleSite site, string cookie)
        {
            if (string.IsNullOrEmpty(cookie)) return false;
            if (site.Type == "mimo") return QuotaService.IsMimoCookie(cookie);
            return QuotaService.IsAliyunCookie(cookie);
        }

        // 轻量诊断日志：只记步骤和 Cookie 名字，不记 Cookie 值
        internal static void Log(ConsoleSite site, string message)
        {
            try
            {
                string path = Path.Combine(Config.DataDir, site.LogFileName);
                if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        // 可见窗口登录：拿到可用的 Cookie 之前，只有这一条路径
        private static bool OpenLoginWindow(Progress state, ConsoleSite site, out string cookie)
        {
            state.SetMessage("正在准备登录窗口…");
            LoginGate.Wait(state.Token);
            try
            {
                state.Token.ThrowIfCancellationRequested();
                return ReadFromProfile(site, site.LoginDir, true, LoginSeconds, state, site.LoginPrompt, out cookie);
            }
            finally { LoginGate.Release(); }
        }

        // 在指定配置目录上起一个浏览器窗口，轮询 CDP 直到拿到目标站点的 Cookie
        private static bool ReadFromProfile(ConsoleSite site, string dir, bool visible, int seconds,
            Progress state, string message, out string cookie)
        {
            cookie = null;
            state.Token.ThrowIfCancellationRequested();
            string cookies = Path.Combine(dir, "Default", "Network", "Cookies");
            Log(site, "read: dir=" + dir + " visible=" + visible + " db=" + File.Exists(cookies));
            string exe = FindBrowser();
            if (exe == null) throw new Exception("未找到 Edge 或 Chrome，无法读取或打开登录页");
            try { Directory.CreateDirectory(dir); }
            catch { return false; }

            Process proc = null;
            int port = 0;
            try
            {
                port = FreePort();
                state.Token.ThrowIfCancellationRequested();
                proc = Launch(exe, dir, port, visible, site.LoginUrl);
                if (!WaitForCdp(port, 25000, state.Token))
                {
                    if (!visible) return false;
                    throw new Exception("浏览器调试端口没有就绪，请关闭残留的登录窗口后重试");
                }
                state.SetMessage(site.WaitingText);
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                var started = DateTime.UtcNow;
                while (DateTime.UtcNow < deadline)
                {
                    if (state.Cancelled) return false;
                    int elapsed = (int)(DateTime.UtcNow - started).TotalSeconds;
                    state.SetMessage(message + (visible ? "（已等待 " + elapsed + " 秒）" : ""));
                    string found;
                    if (TryReadCookie(site, port, out found, state.Token))
                    {
                        cookie = found;
                        return true;
                    }
                    Task.Delay(PollMs, state.Token).GetAwaiter().GetResult();
                }
                if (visible) throw new Exception("等待登录超时（" + (seconds / 60) + " 分钟）");
                return false;
            }
            finally
            {
                CloseBrowser(proc, port);
            }
        }

        private static bool TryReadCookie(ConsoleSite site, int port, out string cookie, CancellationToken cancellation)
        {
            cookie = null;
            cancellation.ThrowIfCancellationRequested();
            var targets = PageTargets(port, cancellation);
            if (targets.Count == 0) return false;
            string ws = null;
            // 尽量连在目标站点那个标签上（getAllCookies 本身与页面无关，这里只是选一个可用上下文）
            foreach (var t in targets)
                if (t[0].IndexOf(site.HostSuffix, StringComparison.OrdinalIgnoreCase) >= 0) { ws = t[1]; break; }
            if (ws == null) ws = targets[0][1];

            string json;
            try
            {
                using (var cdp = new CdpClient(ws, 5000, cancellation))
                {
                    cdp.Call("Network.enable", "{}", 4000);
                    // 返回整个配置目录的 Cookie，与当前标签停在哪个页面无关
                    json = cdp.Call("Network.getAllCookies", "{}", 6000);
                }
            }
            catch { return false; }

            string header = CookieHeader(site, json);
            Log(site, "cookies: bytes=" + json.Length + " kept=" + (header.Length == 0 ? 0 : header.Split(';').Length)
                + " valid=" + ValidCookie(site, header));
            if (!ValidCookie(site, header)) return false;
            string summary;
            if (!Probe(site, header, out summary, cancellation)) return false;   // 有 Cookie 不等于已登录，打一次接口验活
            cookie = header;
            return true;
        }

        // 验活 + 顺手拿到给界面显示的摘要：
        //   MiMo：/balance → 「余额 ¥x」
        //   千问 / 百炼：/tool/user/info.json → secToken 存在即已登录，再读一次 usage 拼「本月已用 x%」
        internal static bool Probe(ConsoleSite site, string cookie, out string summary,
            CancellationToken cancellation = default(CancellationToken))
        {
            summary = null;
            cancellation.ThrowIfCancellationRequested();
            if (site.Type == "mimo") return ProbeMimo(cookie, out summary, cancellation);
            return ProbeAliyun(site, cookie, out summary, cancellation);
        }

        private static bool ProbeMimo(string cookie, out string summary, CancellationToken cancellation)
        {
            summary = null;
            try
            {
                var headers = new Dictionary<string, string>();
                headers["Cookie"] = cookie;
                headers["Origin"] = "https://platform.xiaomimimo.com";
                headers["Referer"] = QuotaService.MimoConsoleUrl;
                string body = HttpGetText(QuotaService.MimoApiBase + "/balance", 8000, headers, cancellation);
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                if (root == null || (int)Math.Round(J.D(root, "code")) != 0) return false;
                Log(MiMo, "probe: code=" + (root == null ? "null" : J.S(root, "code")));
                var data = root.ContainsKey("data") ? J.Dict(root["data"]) : null;
                if (data == null) return false;
                double balance = J.D(data, "balance");
                summary = "余额 " + QuotaService.MimoCurrencySymbol(J.S(data, "currency")) + St.FmtMoney(balance);
                return true;
            }
            catch { return false; }
        }

        private static bool ProbeAliyun(ConsoleSite site, string cookie, out string summary, CancellationToken cancellation)
        {
            summary = null;
            try
            {
                var headers = new Dictionary<string, string>();
                headers["Cookie"] = cookie;
                headers["Origin"] = QuotaService.AliyunConsoleOrigin(site.Type);
                headers["Referer"] = QuotaService.AliyunConsoleUrl(site.Type);
                headers["User-Agent"] = QuotaService.ConsoleUserAgent;
                string body = HttpGetText(QuotaService.AliyunInfoUrl(site.Type), 8000, headers, cancellation);
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                if (root == null) return false;
                // 未登录时这家回 {"code":"ConsoleNeedLogin","message":"请登录"}，HTTP 仍是 200
                string code = J.S(root, "code");
                var data = root.ContainsKey("data") ? J.Dict(root["data"]) : null;
                if (data == null) { Log(site, "probe: code=" + code + "（无 data）"); return false; }
                Log(site, "probe: code=" + (code == "" ? "ok" : code));
                summary = "已登录控制台";
                // 再读一次 usage，把「本月已用 x%」显示出来；失败不影响验活结论
                try
                {
                    string secToken = J.S(data, "secToken");
                    string usage = QuotaService.AliyunGatewayRequest(site.Type, cookie, secToken, "usage", cancellation);
                    var pools = QuotaService.ParseAliyunPlan(usage, null, null, site.Type);
                    if (pools.Count > 0)
                        summary = pools[0].Label.Replace("额度", "") + "已用 " + pools[0].Percent + "%";
                }
                catch { }
                return true;
            }
            catch { return false; }
        }


        // ---------- 浏览器与配置目录 ----------
        private static string FindBrowser()
        {
            string progId = "";
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                    if (key != null) progId = (key.GetValue("ProgId") as string) ?? "";
            }
            catch { }
            var edge = EdgePath();
            var chrome = ChromePath();
            var list = new List<string>();
            if (progId.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0) { list.Add(chrome); list.Add(edge); }
            else { list.Add(edge); list.Add(chrome); }
            foreach (var p in list) if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            return null;
        }

        private static string EdgePath()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] paths = new string[]
            {
                Path.Combine(pf86 ?? "", @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf ?? "", @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(la ?? "", @"Microsoft\Edge\Application\msedge.exe")
            };
            foreach (var p in paths) if (File.Exists(p)) return p;
            return "";
        }

        private static string ChromePath()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] paths = new string[]
            {
                Path.Combine(pf ?? "", @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86 ?? "", @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(la ?? "", @"Google\Chrome\Application\chrome.exe")
            };
            foreach (var p in paths) if (File.Exists(p)) return p;
            return "";
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static Process Launch(string exe, string dir, int port, bool visible, string url)
        {
            // 新配置目录会被 Edge 用系统账号自动登录，同步气泡会挡住登录页，这里一并关掉；
            // 无头模式则连窗口都没有，用户完全无感
            string args =
                (visible ? "" : "--headless=new --disable-gpu ") +
                "--remote-debugging-port=" + port +
                " --user-data-dir=\"" + dir + "\"" +
                " --no-first-run --no-default-browser-check --no-service-autorun" +
                " --disable-sync --disable-background-networking --disable-component-update" +
                " --suppress-message-center-popups" +
                " --disable-features=msEdgeSyncPromo,msImplicitSignin,msEdgeIdentityFRE,EdgeSyncPromo" +
                " --new-window \"" + url + "\"";
            return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
        }

        private static bool WaitForCdp(int port, int timeoutMs, CancellationToken cancellation)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    string body = HttpGetText("http://127.0.0.1:" + port + "/json/version", 2000, null, cancellation);
                    if (!string.IsNullOrEmpty(body)) return true;
                }
                catch { }
                Task.Delay(300, cancellation).GetAwaiter().GetResult();
            }
            return false;
        }

        private static void CloseBrowser(Process proc, int port)
        {
            if (proc == null) return;
            // 无头实例直接结束；可见窗口走 Browser.close，保证会话/登录态写回配置目录
            try
            {
                if (port > 0)
                {
                    string version = HttpGetText("http://127.0.0.1:" + port + "/json/version", 1500);
                    var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(version);
                    string ws = root == null ? "" : J.S(root, "webSocketDebuggerUrl");
                    if (ws.Length > 0)
                        using (var cdp = new CdpClient(ws, 2000))
                            cdp.Call("Browser.close", "{}", 1500);
                }
            }
            catch { }
            try { if (!proc.HasExited && !proc.WaitForExit(4000)) proc.Kill(); }
            catch { }
        }

        private static Exception Unwrap(Exception ex)
        {
            var agg = ex as AggregateException;
            if (agg != null && agg.InnerExceptions.Count == 1) return Unwrap(agg.InnerExceptions[0]);
            return ex;
        }

        // 只收目标站点自己的 Cookie，避免把其它站点的内容写进配置：
        //   MiMo：只保留平台自己的四个（其它全是无关的埋点）；
        //   千问 / 百炼：控制台要带整串 passport 与风控 Cookie，只能按域名全收（同名去重）。
        // 同名取先出现的那个：CDP 按 domain 排序，先出现的通常是更具体的那一条
        private static string CookieHeader(ConsoleSite site, string cookiesJson)
        {
            var parts = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cookiesJson);
                if (root == null) return "";
                object resultObj;
                if (!root.TryGetValue("result", out resultObj)) return "";
                var result = resultObj as Dictionary<string, object>;
                if (result == null) return "";
                object cookiesObj;
                if (!result.TryGetValue("cookies", out cookiesObj)) return "";
                foreach (var raw in J.Each(cookiesObj))
                {
                    var item = raw as Dictionary<string, object>;
                    if (item == null) continue;
                    string domain = J.S(item, "domain");
                    if (domain.IndexOf(site.HostSuffix, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string name = J.S(item, "name"), value = J.S(item, "value");
                    if (name.Length == 0 || value.Length == 0) continue;
                    if (site.CookieNames != null)
                    {
                        bool wanted = false;
                        foreach (var n in site.CookieNames) if (n == name) { wanted = true; break; }
                        if (!wanted) continue;
                    }
                    if (!seen.Add(name)) continue;
                    parts.Add(name + "=" + value);
                }
            }
            catch { return ""; }
            return string.Join("; ", parts.ToArray());
        }

        private static List<string[]> PageTargets(int port, CancellationToken cancellation)
        {
            var list = new List<string[]>();
            string json;
            try { json = HttpGetText("http://127.0.0.1:" + port + "/json/list", 2500, null, cancellation); }
            catch { return list; }
            if (string.IsNullOrEmpty(json)) return list;
            try
            {
                var items = new JavaScriptSerializer().Deserialize<object[]>(json);
                if (items == null) return list;
                foreach (var item in items)
                {
                    var target = item as Dictionary<string, object>;
                    if (target == null || J.S(target, "type") != "page") continue;
                    string ws = J.S(target, "webSocketDebuggerUrl");
                    if (ws.Length == 0) continue;
                    list.Add(new string[] { J.S(target, "url"), ws });
                }
            }
            catch { }
            return list;
        }

        private static string HttpGetText(string url, int timeoutMs)
        {
            return HttpGetText(url, timeoutMs, null);
        }

        private static string HttpGetText(string url, int timeoutMs, IDictionary<string, string> headers,
            CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            if (req.RequestUri.IsLoopback) req.Proxy = null;   // 只对本机 CDP 绕过代理
            req.UserAgent = "QuotaWidget/" + AppInfo.Version;
            if (headers != null)
                foreach (var header in headers)
                {
                    if (string.IsNullOrEmpty(header.Value)) continue;
                    if (string.Equals(header.Key, "Referer", StringComparison.OrdinalIgnoreCase)) req.Referer = header.Value;
                    else if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = header.Value;
                    else req.Headers[header.Key] = header.Value;
                }
            using (cancellation.Register(req.Abort))
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        // 极简 CDP 客户端：一条命令一个 JSON 帧，按 id 匹配回包，忽略事件帧
        private sealed class CdpClient : IDisposable
        {
            private readonly ClientWebSocket _ws = new ClientWebSocket();
            private readonly CancellationTokenRegistration _cancelRegistration;
            private readonly byte[] _buf = new byte[1 << 16];
            private int _next;

            public CdpClient(string url, int timeoutMs, CancellationToken cancellation = default(CancellationToken))
            {
                cancellation.ThrowIfCancellationRequested();
                _cancelRegistration = cancellation.Register(_ws.Abort);
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        cts.CancelAfter(timeoutMs);
                        _ws.ConnectAsync(new Uri(url), cts.Token).GetAwaiter().GetResult();
                    }
                    if (_ws.State != WebSocketState.Open) throw new Exception("浏览器调试接口未就绪");
                }
                catch (Exception ex)
                {
                    Dispose();
                    throw new Exception("浏览器调试接口连接失败：" + Unwrap(ex).Message);
                }
            }

            public string Call(string method, string paramsJson, int timeoutMs)
            {
                int id = ++_next;
                string message = "{\"id\":" + id + ",\"method\":\"" + method + "\"" +
                    (paramsJson == null ? "" : ",\"params\":" + paramsJson) + "}";
                var payload = Encoding.UTF8.GetBytes(message);
                var cts = new CancellationTokenSource(timeoutMs);
                try { _ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cts.Token).Wait(timeoutMs); }
                catch (Exception ex) { throw new Exception("浏览器调试接口无响应：" + Unwrap(ex).Message); }
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    string text = Receive(timeoutMs);
                    var reply = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                    object idObj;
                    if (reply != null && reply.TryGetValue("id", out idObj) && idObj != null
                        && (int)Math.Round(Convert.ToDouble(idObj, CultureInfo.InvariantCulture)) == id)
                        return text;
                }
                throw new TimeoutException("浏览器调试接口无响应");
            }

            private string Receive(int timeoutMs)
            {
                var sb = new StringBuilder();
                var cts = new CancellationTokenSource(timeoutMs);
                while (true)
                {
                    var result = _ws.ReceiveAsync(new ArraySegment<byte>(_buf), cts.Token).Result;
                    sb.Append(Encoding.UTF8.GetString(_buf, 0, result.Count));
                    if (result.EndOfMessage) return sb.ToString();
                }
            }

            public void Dispose()
            {
                _cancelRegistration.Dispose();
                try { _ws.Dispose(); } catch { }
            }
        }
    }

    internal class ProviderEditDialog : StyledDialogBase
    {
        private static readonly string[] TypeItems = new string[]
        {
            "GLM 国内版 open.bigmodel.cn", "GLM 国际版 api.z.ai",
            "MiniMax 国内版 minimaxi.com", "MiniMax 国际版 minimax.io",
            "Kimi Code Plan api.kimi.com", "DeepSeek api.deepseek.com",
            "Claude Pro/Max api.anthropic.com", "Codex (ChatGPT 套餐) chatgpt.com",
            "小米 MiMo platform.xiaomimimo.com",
            "千问 Token Plan platform-home.qianwenai.com", "百炼 Token Plan bailian.console.aliyun.com"
        };
        // 下拉项对应的品牌图标类型（与 TypeItems 一一对应，供 BrandIcons 染色绘制）
        private static readonly string[] TypeIconTypes = new string[] { "glm", "glm", "mx", "mx", "kimi", "ds", "claude", "codex", "mimo", "qwen", "bailian" };
        // 下拉索引 ↔ 配置里的 type/region；不再用嵌套三元，避免新增接口时改错分支
        private static readonly string[] TypeKeys = new string[] { "glm", "glm", "mx", "mx", "kimi", "ds", "claude", "codex", "mimo", "qwen", "bailian" };
        private static readonly string[] TypeRegions = new string[] { "cn", "intl", "cn", "intl", "cn", "cn", "cn", "cn", "cn", "cn", "cn" };

        private readonly TextBox _txtKey, _txtName;
        private Rectangle _rcRefresh;
        private int _refreshSec;
        private Rectangle _rcSelect, _rcKeyBox;
        private Rectangle _rcSeg, _rcNameBox, _rcOk, _rcCancel, _rcGlmMode, _rcGlmContext, _rcAuto;
        private int _typeIndex;
        private bool _showName;                    // 显示方式：false=图标(默认，悬浮窗展示图标+环形进度) true=显示名称
        private int _glmMode;                      // 仅 GLM：查询内容 0=Coding Plan 套餐 1=钱包余额(¥) 2=钱包余额($)
        private int _mimoMode;                     // 仅 MiMo：查询内容 0=Token Plan 套餐 1=账号余额
        private string _glmPlanType = "personal";  // 仅 GLM 套餐：personal / team
        private string _glmOrg = "", _glmProj = "";
        private readonly bool _providerEnabled;
        private bool _hotOk, _hotCancel, _hotSelect, _hotSeg, _hotGlmMode, _hotGlmContext, _hotAuto;
        // 控制台 Cookie 型供应商的自动获取：worker 线程 + 定时器，状态直接写在 Cookie 输入框下方，不弹任何对话框
        private ConsoleBinder.Progress _mimoState;
        private System.Windows.Forms.Timer _mimoTimer;
        private string _mimoStatus;
        private string _mimoLoginAt;
        private bool _mimoOk, _mimoFail;
        private SettingsPopupList _popup;
        public ProviderConfig Result { get; private set; }

        public ProviderEditDialog(ProviderConfig existing, SettingsPal pal, float scale)
            : base(existing != null ? "编辑供应商" : "添加供应商", pal, scale, new Size(St.SiF(400, scale), St.SiF(334, scale)))
        {
            _providerEnabled = existing == null || existing.Enabled;
            int x = S(20), w = ClientSize.Width - S(40);

            // 前两组「标签 + 输入区」固定；显示方式分段控件与名称组（可选）在 ApplyLayout 里排布
            _rcSelect = new Rectangle(x, S(46) + S(38), w, S(34));
            _rcKeyBox = new Rectangle(x, S(46) + S(112), w, S(34));

            _txtKey = new TextBox { BorderStyle = BorderStyle.None, BackColor = pal.Win, ForeColor = pal.Text, Font = Fs[1], PasswordChar = '●' };
            _txtName = new TextBox { BorderStyle = BorderStyle.None, BackColor = pal.Win, ForeColor = pal.Text, Font = Fs[1] };
            Controls.Add(_txtKey);
            Controls.Add(_txtName);
            _refreshSec = existing == null ? 0 : existing.RefreshSec;
            // 单行 TextBox 高度跟随字体，垂直居中在输入框内；边框由 OnPaint 画
            _txtKey.Bounds = new Rectangle(_rcKeyBox.X + S(10), _rcKeyBox.Y + (S(34) - _txtKey.PreferredHeight) / 2, _rcKeyBox.Width - S(20), _txtKey.PreferredHeight);
            // 空 Key 框在未聚焦时是隐藏的（见 UpdateKeyBox），打开对话框自动聚焦会跳过它，占位提示得以显示
            _txtKey.GotFocus += delegate { UpdateKeyBox(); Invalidate(); };
            _txtKey.LostFocus += delegate { UpdateKeyBox(); Invalidate(); };
            _txtKey.TextChanged += delegate { UpdateKeyBox(); };
            _txtName.GotFocus += delegate { Invalidate(); };
            _txtName.LostFocus += delegate { Invalidate(); };

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnOk(); }
            };
            FormClosing += delegate
            {
                ClosePopup();
                CancelMimoFetch();
            };
            FormClosed += delegate { if (_mimoTimer != null) { _mimoTimer.Stop(); _mimoTimer.Dispose(); _mimoTimer = null; } };

            // 编辑模式：用现有配置预填
            if (existing != null)
            {
                _typeIndex = ProviderConfig.TypeIndex(existing.Type, existing.Region);
                _showName = existing.ShowName;
                _glmMode = existing.EffectiveGlmMode == "wallet" ? (existing.EffectiveGlmCurrency == "USD" ? 2 : 1) : 0;
                _mimoMode = existing.EffectiveMimoMode == "balance" ? 1 : 0;
                _mimoLoginAt = existing.MimoLoginAt;
                _glmPlanType = existing.EffectiveGlmPlanType;
                _glmOrg = existing.GlmOrganization ?? "";
                _glmProj = existing.GlmProject ?? "";
                // 预填解密后的真实 key（输入框本身以 ● 掩码显示）；密文解不开时留空待重填
                _txtKey.Text = existing.GetKey() ?? "";
                _txtName.Text = existing.Name;
            }
            ApplyLayout();
            UpdateKeyBox();
        }

        // Key 框占位提示的显隐：空且未聚焦时隐藏输入框，露出 OnPaint 画的占位文字。
        // .NET Framework 的 TextBox 无 PlaceholderText，且密码掩码下系统 cue banner 不显示，只能自绘
        private void UpdateKeyBox()
        {
            _txtKey.Visible = _txtKey.Text.Length > 0 || _txtKey.Focused;
        }

        private string KeyHint()
        {
            string t = TypeIconTypes[_typeIndex];
            if (t == "codex") return "留空自动读取本机 Codex 登录凭证";
            if (t == "claude") return "留空自动读取本机 Claude 登录凭证";
            if (t == "mimo" || t == "qwen" || t == "bailian") return "点「自动获取」或粘贴 Cookie";
            return "请输入 API Key";
        }

        // 字段纵向节奏：组间距 12（上组输入框→下组 Label），组内 Label→输入框 8，控件→按钮区 16。
        // 名称模式追加名称输入组（加高 S(74)）；GLM / MiMo 追加"查询内容"行（同样加高 S(74)）；
        // 控制台 Cookie 型（MiMo / 千问 / 百炼）还要在 Cookie 行下面留一行状态（加高 S(26)）。
        private void ApplyLayout()
        {
            int x = S(20), w = S(400) - S(40);
            bool console = ConsoleVisible;
            int modeExtra = ModeVisible ? S(74) : 0;
            int statusExtra = console ? S(26) : 0;
            int h = S(408) + statusExtra + modeExtra + (_showName ? S(74) : 0);
            // 本窗口吞掉了 WM_NCCALCSIZE,客户区即整个外框;句柄已创建后再设 ClientSize,
            // WinForms 会按 WS_THICKFRAME 用 AdjustWindowRectEx 补算边框,把窗口撑宽一圈,
            // 故运行期切换显示方式时直接设置外框尺寸(构造期仍走 ClientSize,创建句柄时正好抵消)
            if (IsHandleCreated) Size = new Size(S(400), h); else ClientSize = new Size(S(400), h);
            // 控制台 Cookie 型供应商的凭证是 Cookie：「自动获取」按钮放在输入框左边，点一下静默抓取
            _rcAuto = console ? new Rectangle(x, S(46) + S(112), S(88), S(34)) : Rectangle.Empty;
            _rcKeyBox = console
                ? new Rectangle(x + S(96), S(46) + S(112), w - S(96), S(34))
                : new Rectangle(x, S(46) + S(112), w, S(34));
            _txtKey.Bounds = new Rectangle(_rcKeyBox.X + S(10), _rcKeyBox.Y + (S(34) - _txtKey.PreferredHeight) / 2, _rcKeyBox.Width - S(20), _txtKey.PreferredHeight);
            _rcGlmMode = Rectangle.Empty;
            _rcGlmContext = Rectangle.Empty;
            if (ModeVisible)
            {
                int modeY = S(46) + S(186) + statusExtra;
                if (GlmVisible)
                {
                    // 套餐模式右侧留一个"个人版/团队版"按钮；钱包模式不需要上下文，下拉占满整行
                    bool quotaMode = _glmMode == 0;
                    _rcGlmMode = new Rectangle(x, modeY, quotaMode ? w - S(126) : w, S(34));
                    _rcGlmContext = quotaMode ? new Rectangle(x + w - S(116), modeY, S(116), S(34)) : Rectangle.Empty;
                }
                else _rcGlmMode = new Rectangle(x, modeY, w, S(34));
            }
            _rcSeg = new Rectangle(x, S(46) + S(186) + statusExtra + modeExtra, w, S(34));
            _rcNameBox = _showName ? new Rectangle(x, S(46) + S(260) + statusExtra + modeExtra, w, S(34)) : Rectangle.Empty;
            _rcOk = new Rectangle(S(400) - S(20) - S(88), h - S(18) - S(34), S(88), S(34));
            _rcCancel = new Rectangle(_rcOk.X - S(10) - S(88), _rcOk.Y, S(88), S(34));
            _txtName.Visible = _showName;
            _txtName.Bounds = new Rectangle(_rcNameBox.X + S(10), _rcNameBox.Y + (S(34) - _txtName.PreferredHeight) / 2, _rcNameBox.Width - S(20), _txtName.PreferredHeight);
            _rcRefresh = new Rectangle(x, S(306) + statusExtra + modeExtra + (_showName ? S(74) : 0), w, S(34));
            UpdateKeyBox();
            Invalidate();
        }

        // 控制台 Cookie 自动获取：按钮 → 后台线程跑 ConsoleBinder.AutoFetch → 定时器把进度写到输入框下方
        private void StartMimoFetch()
        {
            if (_mimoState != null || !ConsoleVisible) return;
            ClosePopup();
            ActiveControl = null;
            _mimoState = new ConsoleBinder.Progress();
            _mimoStatus = "正在检查已有 Cookie…";
            _mimoOk = _mimoFail = false;
            if (_mimoTimer == null)
            {
                _mimoTimer = new System.Windows.Forms.Timer { Interval = 200 };
                _mimoTimer.Tick += delegate { PumpMimoFetch(); };
            }
            _mimoTimer.Start();
            var state = _mimoState;
            var site = ConsoleBinder.SiteFor(TypeKeys[_typeIndex]);
            string existing = _txtKey.Text;
            var worker = new Thread(new ThreadStart(delegate { ConsoleBinder.AutoFetch(state, site, existing); }));
            worker.IsBackground = true;
            worker.Start();
            Invalidate();
        }

        // 先切断 UI 与旧任务的关联，再取消请求；旧线程即使晚到也不能回填新供应商。
        private void CancelMimoFetch()
        {
            var state = _mimoState;
            _mimoState = null;
            if (_mimoTimer != null) _mimoTimer.Stop();
            _mimoStatus = null;
            _mimoOk = _mimoFail = false;
            if (state != null) state.Cancel();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelMimoFetch();
                if (_mimoTimer != null) { _mimoTimer.Dispose(); _mimoTimer = null; }
            }
            base.Dispose(disposing);
        }

        private void PumpMimoFetch()
        {
            var state = _mimoState;
            if (state == null) return;
            if (state.Cancelled) { CancelMimoFetch(); Invalidate(); return; }
            if (!string.IsNullOrEmpty(state.Message)) _mimoStatus = state.Message;
            if (!state.Done) { Invalidate(); return; }

            _mimoTimer.Stop();
            _mimoState = null;
            if (!string.IsNullOrEmpty(state.Cookie))
            {
                _txtKey.Text = state.Cookie;
                UpdateKeyBox();
                _mimoOk = true; _mimoFail = false;
                _mimoLoginAt = DateTime.UtcNow.ToString("o");
                string left = QuotaService.MimoLoginText(_mimoLoginAt);
                _mimoStatus = "✓ " + (string.IsNullOrEmpty(state.Summary) ? "已获取 Cookie" : state.Summary)
                    + (string.IsNullOrEmpty(left) ? "" : " · " + left);
            }
            else
            {
                _mimoOk = false; _mimoFail = true;
                _mimoStatus = "✗ " + (string.IsNullOrEmpty(state.Error) ? "获取失败" : state.Error);
            }
            Invalidate();
        }

        // "查询内容"只对 GLM / MiMo 有意义：千问、百炼各自只有一个读数，其他接口仍按原布局显示
        private bool GlmVisible { get { return TypeIconTypes[_typeIndex] == "glm"; } }
        private bool MimoVisible { get { return TypeIconTypes[_typeIndex] == "mimo"; } }
        // 凭证是控制台 Cookie 的三家（MiMo / 千问 / 百炼）：共用「自动获取」按钮 + 状态行
        private bool ConsoleVisible
        {
            get
            {
                string t = TypeIconTypes[_typeIndex];
                return t == "mimo" || t == "qwen" || t == "bailian";
            }
        }
        private bool ModeVisible { get { return GlmVisible || MimoVisible; } }
        // GLM 有 3 档（套餐 / 钱包¥ / 钱包$），MiMo 有 2 档（套餐 / 余额），共用同一行的下拉
        private string[] ModeLabels { get { return GlmVisible ? GlmModeLabels : MimoModeLabels; } }
        private int ModeIndex { get { return GlmVisible ? _glmMode : _mimoMode; } }

        // 状态行文案里用的品牌名
        private string ConsoleBrand { get { return ConsoleBinder.SiteFor(TypeKeys[_typeIndex]).Brand; } }

        private void ClosePopup()
        {
            if (_popup != null && !_popup.IsDisposed) _popup.Close();
            _popup = null;
        }

        private void OpenTypePopup()
        {
            ClosePopup();
            _popup = new SettingsPopupList(RectangleToScreen(_rcSelect), TypeItems, _typeIndex, Pal, Fs[1], Fs[1], Sf, SelectType, TypeIconTypes);
            _popup.Show(this);
        }

        private void SelectType(int idx)
        {
            if (idx == _typeIndex) return;
            bool differentProvider = TypeKeys[idx] != TypeKeys[_typeIndex];
            CancelMimoFetch();
            _mimoLoginAt = null;
            // 不把上一家的 API Key / Cookie 带进另一家；同品牌切国内/国际仍保留输入。
            if (differentProvider) _txtKey.Clear();
            _typeIndex = idx;
            if (string.IsNullOrWhiteSpace(_txtName.Text) || IsDefaultName(_txtName.Text))
                _txtName.Text = ProviderConfig.TypeDefaultNames[idx];
            ApplyLayout();
            Invalidate();
        }

        private static bool IsDefaultName(string n)
        {
            foreach (string d in ProviderConfig.TypeDefaultNames)
                if (d == n) return true;
            return false;
        }

        private void OnOk()
        {
            int i = _typeIndex;
            string type = TypeKeys[i];
            // claude/codex 的 Key 可留空：自动读取本机 CLI 登录凭证（.claude/.credentials.json、.codex/auth.json）
            bool localCreds = type == "claude" || type == "codex";
            bool consoleType = type == "mimo" || type == "qwen" || type == "bailian";
            if (string.IsNullOrWhiteSpace(_txtKey.Text) && !localCreds)
            {
                StyledMsg.Show(this, "提示", consoleType
                    ? "请先点「自动获取」抓取 Cookie（也可手动粘贴控制台 Cookie）"
                    : "请填写 API Key（Claude / Codex 可留空自动读取本机登录凭证）", Pal, Sf);
                return;
            }
            string region = TypeRegions[i];
            if (type == "glm" && _glmMode == 0 && _glmPlanType == "team" &&
                (string.IsNullOrWhiteSpace(_glmOrg) || string.IsNullOrWhiteSpace(_glmProj)))
            {
                StyledMsg.Show(this, "提示", "团队版请填写组织 ID 和项目 ID", Pal, Sf);
                return;
            }
            // 图标模式不展示自定义名，名称固定回默认名；名称模式取输入（空则回默认）
            string name = _showName && !string.IsNullOrWhiteSpace(_txtName.Text) ? _txtName.Text.Trim() : ProviderConfig.TypeDefaultNames[i];
            var r = new ProviderConfig { Type = type, Region = region, Name = name, ShowName = _showName, Enabled = _providerEnabled, RefreshSec = _refreshSec,
                GlmMode = type == "glm" && _glmMode > 0 ? "wallet" : "quota",
                GlmCurrency = type == "glm" && _glmMode == 2 ? "USD" : (type == "glm" && _glmMode == 1 ? "CNY" : null),
                GlmPlanType = type == "glm" && _glmMode == 0 ? _glmPlanType : null,
                GlmOrganization = type == "glm" && _glmMode == 0 && _glmPlanType == "team" ? _glmOrg.Trim() : null,
                GlmProject = type == "glm" && _glmMode == 0 && _glmPlanType == "team" ? _glmProj.Trim() : null,
                MimoMode = type == "mimo" ? (_mimoMode == 1 ? "balance" : "plan") : null,
                MimoLoginAt = consoleType && !string.IsNullOrEmpty(_mimoLoginAt) ? _mimoLoginAt : null };
            // 明文仅存在于输入框与本次加密调用，配置文件里只落 DPAPI 密文；控制台 Cookie 型顺带
            // 去掉粘贴时带的 "Cookie:" 前缀与换行
            r.SetKey(consoleType ? QuotaService.ConsoleCookie(_txtKey.Text) : _txtKey.Text.Trim());
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
            bool console = ConsoleVisible;
            DrawFieldLabel(g, console ? "登录 Cookie" : "API Key", S(46) + S(84));
            DrawFieldBox(g, _rcKeyBox, _txtKey.Focused);
            // 占位提示：输入框隐藏（空且未聚焦）时才可见，聚焦后由真实输入框覆盖
            if (_txtKey.Text.Length == 0)
                TextRenderer.DrawText(g, KeyHint(), Fs[1],
                    new Rectangle(_rcKeyBox.X + S(10), _rcKeyBox.Y, _rcKeyBox.Width - S(20), _rcKeyBox.Height),
                    Pal.Text3, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (console)
            {
                PaintBtn(g, _rcAuto, _mimoState == null ? "自动获取" : "获取中…", false, _hotAuto);
                // 状态行：成功绿、失败红、进行中/提示灰；不弹对话框，进度都写在这里
                string status = _mimoStatus;
                if (string.IsNullOrEmpty(status))
                {
                    string left = QuotaService.MimoLoginText(_mimoLoginAt);
                    status = string.IsNullOrEmpty(left)
                        ? "点「自动获取」打开登录窗口获取 Cookie"
                        : "上次获取的 Cookie " + left;
                }
                Color color = _mimoOk ? Pal.Green : (_mimoFail ? Pal.Danger : (_mimoState != null ? Pal.Text2 : Pal.Text3));
                TextRenderer.DrawText(g, status, Fs[2],
                    new Rectangle(S(20), S(46) + S(154), ClientSize.Width - S(40), S(20)),
                    color, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            bool mode = ModeVisible;
            int statusExtra = console ? S(26) : 0;
            int modeExtra = mode ? S(74) : 0;
            if (mode)
            {
                DrawFieldLabel(g, "查询内容", S(46) + S(158) + statusExtra);
                PaintSelectBox(g, _rcGlmMode, ModeLabels[ModeIndex], _hotGlmMode, "");
                if (GlmVisible && _glmMode == 0)
                    PaintBtn(g, _rcGlmContext, _glmPlanType == "team" ? "团队版" : "个人版", false, _hotGlmContext);
            }
            DrawFieldLabel(g, "显示方式", S(46) + S(158) + statusExtra + modeExtra);
            PaintSegmented(g, _rcSeg);
            if (_showName)
            {
                DrawFieldLabel(g, "显示名称", S(46) + S(232) + statusExtra + modeExtra);
                DrawFieldBox(g, _rcNameBox, _txtName.Focused);
            }
            DrawFieldLabel(g, "刷新间隔", _rcRefresh.Y - S(28));
            PaintSelectBox(g, _rcRefresh, RefreshChoices.Label(_refreshSec), false, "");

            PaintBtn(g, _rcOk, "确认", true, _hotOk);
            PaintBtn(g, _rcCancel, "取消", false, _hotCancel);
        }

        // 显示方式分段控件：左=图标（默认） 右=名称，选中半 Sel 底 + 高亮字
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
            TextRenderer.DrawText(g, "图标", Fs[1], rl, !_showName ? Pal.Text : Pal.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, "名称", Fs[1], rr, _showName ? Pal.Text : Pal.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        // GLM 查询内容：套餐与钱包二选一；钱包金额不随接口域名换算，因此币种跟随账户显式选择
        private static readonly string[] GlmModeLabels = { "Coding Plan 套餐", "钱包余额（¥ 人民币）", "钱包余额（$ 美元）" };
        // MiMo 查询内容：Token Plan 与账号余额是两个接口，共用同一份控制台 Cookie
        private static readonly string[] MimoModeLabels = { "Token Plan 套餐", "账号余额" };

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
            if (_rcRefresh.Contains(e.Location))
            {
                ClosePopup();
                var values = RefreshChoices.Values(_refreshSec, true);
                var labels = values.ConvertAll(RefreshChoices.Label).ToArray();
                _popup = new SettingsPopupList(RectangleToScreen(_rcRefresh), labels, values.IndexOf(_refreshSec), Pal, Fs[1], Fs[1], Sf,
                    delegate(int idx) { _refreshSec = values[idx]; Invalidate(); });
                _popup.Show(this); return;
            }
            if (_rcGlmContext.Contains(e.Location))
            {
                using (var dlg = new GlmTeamContextDialog(_glmPlanType, _glmOrg, _glmProj, Pal, Sf))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _glmPlanType = dlg.PlanType;
                        _glmOrg = dlg.Organization;
                        _glmProj = dlg.Project;
                        Invalidate();
                    }
                }
                return;
            }
            if (_rcAuto.Contains(e.Location)) { StartMimoFetch(); return; }
            if (_rcGlmMode.Contains(e.Location))
            {
                ClosePopup();
                var labels = ModeLabels;
                _popup = new SettingsPopupList(RectangleToScreen(_rcGlmMode), labels, ModeIndex, Pal, Fs[1], Fs[1], Sf,
                    delegate(int idx) { if (GlmVisible) _glmMode = idx; else _mimoMode = idx; ApplyLayout(); });
                _popup.Show(this); return;
            }
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
            // Key 框空且未聚焦时输入框是隐藏的（露占位提示），点击需代为聚焦
            if (_rcKeyBox.Contains(e.Location)) { _txtKey.Visible = true; _txtKey.Focus(); return; }
            // 点击输入框以外区域让文本框失焦，边框恢复常态
            if (!_rcKeyBox.Contains(e.Location) && !_rcNameBox.Contains(e.Location))
                ActiveControl = null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hs = _rcSelect.Contains(e.Location), ho = _rcOk.Contains(e.Location), hc = _rcCancel.Contains(e.Location), hg = _rcSeg.Contains(e.Location);
            bool hm = _rcGlmMode.Contains(e.Location), hx = _rcGlmContext.Contains(e.Location), hb = _rcAuto.Contains(e.Location);
            if (hs != _hotSelect || ho != _hotOk || hc != _hotCancel || hg != _hotSeg || hm != _hotGlmMode || hx != _hotGlmContext || hb != _hotAuto)
            {
                _hotSelect = hs; _hotOk = ho; _hotCancel = hc; _hotSeg = hg; _hotGlmMode = hm; _hotGlmContext = hx; _hotAuto = hb;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotSelect || _hotOk || _hotCancel || _hotSeg || _hotGlmMode || _hotGlmContext || _hotAuto)
            {
                _hotSelect = _hotOk = _hotCancel = _hotSeg = _hotGlmMode = _hotGlmContext = _hotAuto = false;
                Invalidate();
            }
        }
    }

    internal static class RefreshChoices
    {
        internal static readonly int[] Presets = { 15, 30, 60, 120, 300, 600, 1800, 3600 };
        public static List<int> Values(int current, bool inherit)
        {
            var values = new List<int>(Presets);
            if (current > 0 && !values.Contains(current)) { values.Add(current); values.Sort(); }
            if (inherit) values.Insert(0, 0);
            return values;
        }
        public static string Label(int seconds)
        {
            if (seconds <= 0) return "跟随通用";
            return seconds % 60 == 0 ? (seconds / 60) + " 分钟" : seconds + " 秒";
        }
    }

    // 自定义接口面板的按钮、输入框和内容边框使用同一圆角刻度。
    internal static class ApiAppearance
    {
        public const int CornerRadius = 8;
    }

    internal sealed class ApiButton : Button
    {
        private readonly SettingsPal _pal;
        private readonly float _scale;
        private bool _hot, _pressed, _primary;
        public bool Primary { get { return _primary; } set { _primary = value; Invalidate(); } }
        public ApiButton(SettingsPal pal, float scale)
        {
            _pal = pal; _scale = scale;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            BackColor = pal.Win; ForeColor = pal.Text; Font = St.FontsForScale(scale)[1]; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? _pal.Win : Parent.BackColor);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Primary ? _pal.Primary : _pal.Win;
            if (!Enabled) fill = Primary ? SettingsPal.Mix(fill, _pal.Win, 0.7) : _pal.Win;
            else if (_pressed || _hot) fill = Primary ? (_pressed ? SettingsPal.Mix(_pal.PrimaryHover, _pal.Text, 0.12) : _pal.PrimaryHover) : (_pressed ? _pal.Sel : _pal.Hover);
            int radius = St.SiF(ApiAppearance.CornerRadius, _scale);
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), radius))
            {
                using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                if (!Primary)
                    using (var pen = new Pen(Enabled && Focused ? _pal.Primary : _pal.Line)) e.Graphics.DrawPath(pen, path);
            }
            if (Enabled && Focused && Primary && ShowFocusCues)
            {
                int inset = Math.Max(2, St.SiF(3, _scale));
                using (var path = St.RoundRect(new Rectangle(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1), Math.Max(1, radius - inset)))
                using (var pen = new Pen(_pal.PrimaryText)) e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, !Enabled ? _pal.Text3 : Primary ? _pal.PrimaryText : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { _pressed = false; Invalidate(); } }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); } }
        protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) _hot = _pressed = false; Invalidate(); }
    }

    internal class ApiRoundedPanel : Panel
    {
        private readonly SettingsPal _pal;
        private readonly float _scale;
        public ApiRoundedPanel(SettingsPal pal, float scale)
        {
            _pal = pal; _scale = scale; BackColor = pal.Win;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), St.SiF(ApiAppearance.CornerRadius, _scale)))
            using (var pen = new Pen(_pal.Line)) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class ApiValuePreview : Label
    {
        private readonly SettingsPal _pal;
        private readonly float _scale;
        public ApiValuePreview(SettingsPal pal, float scale)
        {
            _pal = pal; _scale = scale; BackColor = pal.Win; ForeColor = pal.Text3;
            Font = St.FontsForScale(scale)[1];
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_pal.Win);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), St.SiF(ApiAppearance.CornerRadius, _scale)))
            using (var brush = new SolidBrush(_pal.Pill)) e.Graphics.FillPath(brush, path);
            int inset = St.SiF(8, _scale);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(inset, 0, Math.Max(0, Width - inset * 2), Height), ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // 复用现有设置下拉浮层，供自定义接口各字段使用。
    internal class SettingsChoice : Control
    {
        private readonly SettingsPal _pal;
        private readonly float _scale;
        private int _index;
        private SettingsPopupList _popup;
        public string[] Items;
        public string[] IconTypes;
        public bool Segmented { get; set; }
        public event EventHandler SelectedIndexChanged;
        public int SelectedIndex
        {
            get { return _index; }
            set { _index = Math.Max(0, Math.Min(Items.Length - 1, value)); Invalidate(); if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty); }
        }
        public SettingsChoice(SettingsPal pal, float scale, string[] items)
        {
            _pal = pal; _scale = scale; Items = items; BackColor = pal.Win;
            Font = St.FontsForScale(scale)[1]; TabStop = true; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        private int S(int n) { return St.SiF(n, _scale); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_pal.Win); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), S(ApiAppearance.CornerRadius)))
            using (var brush = new SolidBrush(_pal.Field)) e.Graphics.FillPath(brush, path);
            if (Segmented)
            {
                int cell = Width / Items.Length;
                var selected = new Rectangle(_index * cell, 0, _index == Items.Length - 1 ? Width - _index * cell : cell, Height);
                using (var path = St.RoundRect(selected, S(ApiAppearance.CornerRadius)))
                using (var brush = new SolidBrush(_pal.Sel)) e.Graphics.FillPath(brush, path);
                using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), S(ApiAppearance.CornerRadius)))
                using (var pen = new Pen(Focused ? _pal.Primary : _pal.Line)) e.Graphics.DrawPath(pen, path);
                for (int i = 0; i < Items.Length; i++)
                    TextRenderer.DrawText(e.Graphics, Items[i], Font, new Rectangle(i * cell, 0, cell, Height), i == _index ? _pal.Text : _pal.Text2,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                return;
            }
            using (var path = St.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), S(ApiAppearance.CornerRadius)))
            using (var pen = new Pen(Focused ? _pal.Primary : _pal.Line)) e.Graphics.DrawPath(pen, path);
            int x = S(10);
            if (IconTypes != null && _index < IconTypes.Length)
            { BrandIcons.Draw(e.Graphics, IconTypes[_index], _pal.Dark, x, (Height - S(18)) / 2, S(18)); x += S(26); }
            TextRenderer.DrawText(e.Graphics, Items[_index], Font, new Rectangle(x, 0, Width - x - S(28), Height), Enabled ? _pal.Text : _pal.Text3,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            using (var pen = new Pen(_pal.Text2, 1.4f))
            { int cx = Width - S(16), cy = Height / 2; e.Graphics.DrawLines(pen, new[] { new Point(cx - S(4), cy - S(2)), new Point(cx, cy + S(2)), new Point(cx + S(4), cy - S(2)) }); }
        }
        private void OpenPopup()
        {
            if (_popup != null && !_popup.IsDisposed) { _popup.Close(); return; }
            _popup = new SettingsPopupList(RectangleToScreen(ClientRectangle), Items, _index, _pal, Font, Font, _scale,
                delegate(int index) { SelectedIndex = index; }, IconTypes);
            _popup.Show(FindForm());
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            if (Segmented) SelectedIndex = e.X / Math.Max(1, Width / Items.Length);
            else OpenPopup();
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Segmented)
            {
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
                { e.SuppressKeyPress = true; SelectedIndex = (_index + (e.KeyCode == Keys.Right ? 1 : Items.Length - 1)) % Items.Length; }
                return;
            }
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; OpenPopup(); }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing && _popup != null) _popup.Dispose(); base.Dispose(disposing); }
    }

    internal class SettingsTabs : Control
    {
        private readonly SettingsPal _pal;
        private readonly float _scale;
        private readonly string[] _items;
        private int _selected;
        public event EventHandler SelectedIndexChanged;
        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int next = Math.Max(0, Math.Min(_items.Length - 1, value));
                if (next == _selected) return;
                _selected = next; Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }
        public SettingsTabs(SettingsPal pal, float scale, string[] items)
        {
            _pal = pal; _scale = scale; _items = items; Font = St.FontsForScale(scale)[1];
            BackColor = pal.Win; TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.PageTabList;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        private int TabWidth { get { return St.SiF(124, _scale); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_pal.Win);
            using (var pen = new Pen(_pal.SectionLine)) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            for (int i = 0; i < _items.Length; i++)
            {
                var r = new Rectangle(i * TabWidth, 0, TabWidth, Height - St.SiF(4, _scale));
                TextRenderer.DrawText(e.Graphics, _items[i], Font, r, i == _selected ? _pal.Text : _pal.Text3,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                if (i == _selected)
                {
                    int inset = St.SiF(20, _scale), h = Math.Max(2, St.SiF(2, _scale));
                    using (var brush = new SolidBrush(_pal.Primary)) e.Graphics.FillRectangle(brush, r.X + inset, Height - h, r.Width - 2 * inset, h);
                }
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.X < _items.Length * TabWidth) { Focus(); SelectedIndex = e.X / TabWidth; }
        }
        protected override bool IsInputKey(Keys keyData)
        { return (keyData & Keys.KeyCode) == Keys.Left || (keyData & Keys.KeyCode) == Keys.Right || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
            { e.SuppressKeyPress = true; SelectedIndex = (_selected + (e.KeyCode == Keys.Right ? 1 : _items.Length - 1)) % _items.Length; }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    // 保留原生文本编辑与键盘操作，滚动位置供外层绘制主题滑块。
    internal sealed class ApiScrollTextBox : TextBox
    {
        private const int EM_GETRECT = 0x00B2, EM_GETLINECOUNT = 0x00BA,
            EM_LINESCROLL = 0x00B6, EM_GETFIRSTVISIBLELINE = 0x00CE, WM_MOUSEWHEEL = 0x020A;
        [StructLayout(LayoutKind.Sequential)]
        private struct TextRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, ref TextRect lParam);
        private int _wheelRemainder;
        public event EventHandler ViewportChanged;

        public void GetViewport(out int first, out int total, out int page)
        {
            first = 0; total = page = 1;
            if (!IsHandleCreated || IsDisposed) return;
            first = SendMessage(Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            total = Math.Max(1, SendMessage(Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32());
            var rect = new TextRect();
            SendMessage(Handle, EM_GETRECT, IntPtr.Zero, ref rect);
            int lineHeight = Font.Height;
            // 原生换行后的实际行高，避免 DPI 缩放时用估算行高漏掉最后一行。
            if (total > 1)
            {
                int line = Math.Min(first, total - 2);
                int a = GetFirstCharIndexFromLine(line), b = GetFirstCharIndexFromLine(line + 1);
                if (a >= 0 && b >= 0)
                {
                    int measured = GetPositionFromCharIndex(b).Y - GetPositionFromCharIndex(a).Y;
                    if (measured > 0) lineHeight = measured;
                }
            }
            page = Math.Max(1, (rect.Bottom - rect.Top) / Math.Max(1, lineHeight));
        }

        public void ScrollToLine(int line)
        {
            if (!IsHandleCreated || IsDisposed) return;
            int first, total, page;
            GetViewport(out first, out total, out page);
            int target = Math.Max(0, Math.Min(Math.Max(0, total - page), line));
            SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(target - first));
        }

        public void ScrollWheel(int delta)
        {
            _wheelRemainder += delta;
            int steps = _wheelRemainder / 120;
            _wheelRemainder %= 120;
            if (steps == 0) return;
            int first, total, page;
            GetViewport(out first, out total, out page);
            int lines = SystemInformation.MouseWheelScrollLines;
            ScrollToLine(first - steps * (lines < 0 ? page : lines));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (ViewportChanged != null) ViewportChanged(this, EventArgs.Empty);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && Multiline)
            {
                ScrollWheel(unchecked((short)(m.WParam.ToInt64() >> 16)));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
            // 绘制、尺寸、文本及光标滚动后同步；查询消息不触发回调，避免重入。
            if (ViewportChanged != null && (m.Msg == 0x000F || m.Msg == 0x0005 || m.Msg == 0x000C
                || m.Msg == 0x0030 || m.Msg == 0x0100 || m.Msg == 0x0102 || m.Msg == 0x0115
                || m.Msg == EM_LINESCROLL || m.Msg == 0x00B7))
                ViewportChanged(this, EventArgs.Empty);
        }
    }

    // 自定义接口编辑器：请求一次，树节点、字段预览和额度预览均在内存中计算。
    internal sealed class ApiTextField : Panel
    {
        private sealed class InputFrame : Panel
        {
            public InputFrame()
            {
                // 底色、焦点边框和滑块一次提交，避免背景擦除时闪掉边框。
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
        }

        private readonly SettingsPal _pal;
        private readonly float _scale;
        private readonly Panel _frame;
        private readonly Label _caption, _value;
        private readonly ToolTip _tip = new ToolTip();
        private bool _dropHot, _hasValue;
        private readonly bool _multiline;
        private readonly ApiScrollTextBox _scrollInput;
        private int _scrollFirst, _scrollTotal = 1, _scrollPage = 1, _scrollGrab;
        private bool _scrollHot, _scrollDragging;
        public readonly TextBox Input;
        public const string PathFormat = "QuotaWidget.JsonFieldPath";
        public ApiTextField(SettingsPal pal, float scale, string label, bool path, int inputHeight = 34)
        {
            _pal = pal; _scale = scale; _multiline = inputHeight > 34;
            BackColor = pal.Win; Height = S(28 + inputHeight); Width = S(468);
            _caption = new Label { Text = label, Font = St.FontsForScale(scale)[1], ForeColor = pal.Text, BackColor = pal.Win };
            _frame = new InputFrame { BackColor = pal.Win };
            _value = new ApiValuePreview(pal, scale) { Visible = false };
            Input = _multiline ? new ApiScrollTextBox() : new TextBox();
            Input.BorderStyle = BorderStyle.None; Input.Font = St.FontsForScale(scale)[1]; Input.ForeColor = pal.Text;
            Input.BackColor = pal.Field; Input.Multiline = Input.AcceptsReturn = _multiline;
            Input.ScrollBars = ScrollBars.None; Input.MaxLength = _multiline ? 1048576 : 4096;
            _scrollInput = Input as ApiScrollTextBox;
            Controls.Add(_caption); Controls.Add(_frame); Controls.Add(_value); _frame.Controls.Add(Input);
            _frame.Paint += delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var shape = St.RoundRect(new Rectangle(0, 0, _frame.Width - 1, _frame.Height - 1), S(ApiAppearance.CornerRadius)))
                {
                    using (var brush = new SolidBrush(pal.Field)) e.Graphics.FillPath(brush, shape);
                    using (var pen = new Pen(_dropHot || Input.Focused ? pal.Primary : pal.Line)) e.Graphics.DrawPath(pen, shape);
                }
                if (_scrollInput != null && _scrollTotal > _scrollPage)
                {
                    var thumb = ScrollThumb();
                    if (thumb.Width > 0 && thumb.Height > 0)
                        using (var brush = new SolidBrush(_scrollHot || _scrollDragging ? pal.Text2 : pal.Text3))
                        using (var shape = St.RoundRect(thumb, Math.Max(1, thumb.Width / 2)))
                            e.Graphics.FillPath(brush, shape);
                }
            };
            if (_scrollInput != null)
            {
                _scrollInput.ViewportChanged += delegate { SyncScrollbar(); };
                _frame.MouseDown += ScrollbarMouseDown;
                _frame.MouseMove += ScrollbarMouseMove;
                _frame.MouseUp += delegate(object sender, MouseEventArgs e)
                { if (e.Button == MouseButtons.Left) EndScrollbarDrag(); };
                _frame.MouseCaptureChanged += delegate
                { if (!_frame.Capture) EndScrollbarDrag(); };
                _frame.MouseLeave += delegate
                { if (_scrollHot) { _scrollHot = false; InvalidateScrollbar(); } };
                _frame.MouseWheel += delegate(object sender, MouseEventArgs e)
                {
                    _scrollInput.ScrollWheel(e.Delta);
                    var handled = e as HandledMouseEventArgs;
                    if (handled != null) handled.Handled = true;
                };
            }
            Input.GotFocus += delegate { _frame.Invalidate(); }; Input.LostFocus += delegate { _frame.Invalidate(); };
            if (path)
            {
                foreach (Control control in new Control[] { this, _frame, Input, _value })
                {
                    control.AllowDrop = true;
                    control.DragEnter += delegate(object sender, DragEventArgs e)
                    {
                        bool accepted = Enabled && e.Data.GetDataPresent(PathFormat);
                        e.Effect = accepted ? DragDropEffects.Copy : DragDropEffects.None;
                        _dropHot = accepted; _frame.Invalidate();
                    };
                    control.DragLeave += delegate { _dropHot = false; _frame.Invalidate(); };
                    control.DragDrop += delegate(object sender, DragEventArgs e)
                    {
                        _dropHot = false; _frame.Invalidate();
                        if (!Enabled || !e.Data.GetDataPresent(PathFormat)) return;
                        Input.Text = e.Data.GetData(PathFormat) as string ?? "";
                        Input.Focus(); Input.SelectionStart = Input.Text.Length;
                    };
                }
            }
            Arrange();
        }
        private int S(int n) { return St.SiF(n, _scale); }
        private Rectangle ScrollTrack()
        { return new Rectangle(_frame.Width - S(20), S(7), S(10), Math.Max(0, _frame.Height - S(14))); }
        private void InvalidateScrollbar()
        {
            // 悬停和滚动不涉及焦点状态，只重绘滑块所在的内侧区域。
            var track = ScrollTrack();
            if (track.Width > 0 && track.Height > 0) _frame.Invalidate(track);
        }
        private void EndScrollbarDrag()
        {
            if (!_scrollDragging) return;
            _scrollDragging = false;
            _frame.Capture = false;
            InvalidateScrollbar();
        }
        private Rectangle ScrollThumb()
        {
            var track = ScrollTrack();
            int height = Math.Min(track.Height, Math.Max(S(18), (int)((long)track.Height * _scrollPage / _scrollTotal)));
            int top = track.Top + (int)Math.Round((track.Height - height) * (double)_scrollFirst / Math.Max(1, _scrollTotal - _scrollPage));
            return new Rectangle(track.Left + S(2), top, Math.Max(1, track.Width - S(4)), height);
        }
        private void SyncScrollbar()
        {
            if (_scrollInput == null || IsDisposed) return;
            int first, total, page;
            _scrollInput.GetViewport(out first, out total, out page);
            first = Math.Max(0, Math.Min(Math.Max(0, total - page), first));
            if (_scrollFirst == first && _scrollTotal == total && _scrollPage == page) return;
            _scrollFirst = first; _scrollTotal = total; _scrollPage = page;
            if (total <= page) { _scrollHot = false; EndScrollbarDrag(); }
            InvalidateScrollbar();
        }
        private void ScrollbarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _scrollTotal <= _scrollPage || !ScrollTrack().Contains(e.Location)) return;
            var thumb = ScrollThumb();
            if (e.Y >= thumb.Top && e.Y < thumb.Bottom)
            { _scrollGrab = e.Y - thumb.Top; _scrollDragging = true; _frame.Capture = true; InvalidateScrollbar(); }
            else _scrollInput.ScrollToLine(_scrollFirst + (e.Y < thumb.Top ? -_scrollPage : _scrollPage));
        }
        private void ScrollbarMouseMove(object sender, MouseEventArgs e)
        {
            bool hot = _scrollTotal > _scrollPage && ScrollTrack().Contains(e.Location);
            if (hot != _scrollHot) { _scrollHot = hot; InvalidateScrollbar(); }
            if (!_scrollDragging) return;
            var track = ScrollTrack();
            int travel = track.Height - ScrollThumb().Height;
            if (travel > 0) _scrollInput.ScrollToLine((int)Math.Round((e.Y - track.Top - _scrollGrab) * (double)(_scrollTotal - _scrollPage) / travel));
        }
        public void SetPreview(bool exists, string text)
        {
            _hasValue = exists; _value.Visible = exists; _value.Text = exists ? text : "";
            _tip.SetToolTip(_value, exists ? text : ""); Arrange();
        }
        public void SetCaption(string text) { _caption.Text = text; }
        private void Arrange()
        {
            if (_frame == null) return;
            int valueWidth = _hasValue ? Math.Min(S(132), Width / 3) : 0;
            _caption.Bounds = new Rectangle(0, 0, Width, S(24));
            _frame.Bounds = new Rectangle(0, S(28), Math.Max(S(60), Width - (valueWidth > 0 ? valueWidth + S(8) : 0)), Height - S(28));
            _value.Bounds = new Rectangle(Width - valueWidth, S(28), valueWidth, Height - S(28));
            // 给细滑块留出固定空隙，显示/隐藏时不改变文本换行。
            Input.Bounds = _multiline ? new Rectangle(S(10), S(7), Math.Max(1, _frame.Width - S(34)), Math.Max(1, _frame.Height - S(14)))
                : new Rectangle(S(10), Math.Max(0, (_frame.Height - Input.PreferredHeight) / 2), _frame.Width - S(20), Input.PreferredHeight);
            SyncScrollbar();
            _frame.Invalidate();
        }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Arrange(); }
        protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class ApiJsonTree : TreeView
    {
        private sealed class NodeValue
        {
            public string Path;
            public object Value;
            public bool Loaded, IsRange;
            public int Start, Count;
            public string[] Keys;
        }
        private readonly SettingsPal _pal;
        private readonly float _scale;
        public ApiJsonTree(SettingsPal pal, float scale)
        {
            _pal = pal; _scale = scale; BackColor = pal.Win; ForeColor = pal.Text;
            BorderStyle = BorderStyle.None; ShowLines = false; ShowRootLines = false; ShowPlusMinus = true;
            HideSelection = false; FullRowSelect = false; ShowNodeToolTips = true; DrawMode = TreeViewDrawMode.OwnerDrawText;
            Font = St.FontsForScale(scale)[1]; ItemHeight = St.SiF(26, scale); Indent = St.SiF(18, scale);
            ItemDrag += delegate(object sender, ItemDragEventArgs e)
            {
                var node = e.Item as TreeNode;
                var data = node == null ? null : node.Tag as NodeValue;
                if (data == null || data.IsRange || e.Button != MouseButtons.Left) return;
                SelectedNode = node;
                var drag = new DataObject(); drag.SetData(ApiTextField.PathFormat, data.Path);
                DoDragDrop(drag, DragDropEffects.Copy);
            };
            BeforeExpand += delegate(object sender, TreeViewCancelEventArgs e) { LoadChildren(e.Node); };
        }
        internal static string ValueText(object value)
        {
            if (value == null) return "null";
            var obj = value as IDictionary;
            if (obj != null) return "{ " + obj.Count + " 个字段 }";
            var list = value as IList;
            if (list != null) return "[ " + list.Count + " 项 ]";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is string)
            {
                string text = (string)value;
                if (text.Length > 180) text = text.Substring(0, 180) + "…";
                return new JavaScriptSerializer().Serialize(text);
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        private TreeNode MakeNode(string key, string path, object value)
        {
            var node = new TreeNode(key + ": " + ValueText(value)) { Tag = new NodeValue { Path = path, Value = value }, ToolTipText = path };
            var obj = value as IDictionary; var list = value as IList;
            if ((obj != null && obj.Count > 0) || (list != null && list.Count > 0)) node.Nodes.Add(new TreeNode("…"));
            return node;
        }
        private void LoadChildren(TreeNode node)
        {
            var data = node.Tag as NodeValue;
            if (data == null || data.Loaded) return;
            data.Loaded = true; node.Nodes.Clear();
            var obj = data.Value as Dictionary<string, object>;
            var list = data.Value as IList;
            if (obj == null && list == null) return;
            if (obj != null && data.Keys == null) data.Keys = new List<string>(obj.Keys).ToArray();
            int start = data.IsRange ? data.Start : 0;
            int count = data.IsRange ? data.Count : obj != null ? obj.Count : list.Count;
            // 大数组分组加载；分组只影响树的展示，实际路径仍保留原始下标。
            if (count > 250)
            {
                int groupSize = 250;
                while ((count + groupSize - 1) / groupSize > 250) groupSize *= 250;
                for (int i = start; i < start + count; i += groupSize)
                {
                    int size = Math.Min(groupSize, start + count - i);
                    string label = obj == null ? "[" + i + " … " + (i + size - 1) + "]" : "字段 " + (i + 1) + "–" + (i + size);
                    var group = new TreeNode(label) { Tag = new NodeValue { Path = data.Path, Value = data.Value,
                        Keys = data.Keys, IsRange = true, Start = i, Count = size } };
                    group.Nodes.Add(new TreeNode("…")); node.Nodes.Add(group);
                }
                return;
            }
            for (int i = start; i < start + count; i++)
                if (obj != null) node.Nodes.Add(MakeNode(data.Keys[i], JsonFieldPath.Append(data.Path, data.Keys[i]), obj[data.Keys[i]]));
                else node.Nodes.Add(MakeNode("[" + i + "]", data.Path + "[" + i + "]", list[i]));
        }
        private void ExpandSample(TreeNode node, ref int budget, int depth)
        {
            if (--budget < 0 || depth > 24) return;
            var data = node.Tag as NodeValue;
            if (data != null && data.IsRange && !data.Loaded) return;
            LoadChildren(node); node.Expand();
            foreach (TreeNode child in node.Nodes)
            { if (budget <= 0) break; ExpandSample(child, ref budget, depth + 1); }
        }
        public void SetJson(object root)
        {
            BeginUpdate();
            try
            {
                Nodes.Clear(); var node = MakeNode("result", "result", root); Nodes.Add(node);
                int budget = 1500; ExpandSample(node, ref budget, 0); SelectedNode = node; node.EnsureVisible();
            }
            finally { EndUpdate(); }
        }
        public void ExpandVisibleData()
        {
            BeginUpdate();
            try { int budget = 5000; foreach (TreeNode node in Nodes) ExpandSample(node, ref budget, 0); }
            finally { EndUpdate(); }
        }
        protected override void OnDrawNode(DrawTreeNodeEventArgs e)
        {
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            var r = e.Bounds; r.Width = Math.Max(r.Width, TextRenderer.MeasureText(e.Node.Text, Font).Width + St.SiF(8, _scale));
            using (var b = new SolidBrush(selected ? _pal.Sel : _pal.Win)) e.Graphics.FillRectangle(b, r);
            TextRenderer.DrawText(e.Graphics, e.Node.Text, Font, r, selected ? _pal.Text : _pal.Text2,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class ApiRuleCard : ApiRoundedPanel
    {
        private readonly SettingsPal _pal;
        private readonly float _scale;
        private readonly ApiTextField _name, _value, _total, _reset, _currency, _multiplier;
        private readonly Panel _meaningGroup, _rangeGroup;
        private readonly SettingsChoice _mode, _meaning, _range;
        private readonly Label _title, _error;
        private readonly Button _remove;
        private bool _loading;
        private object _previewRoot, _rawRoot;
        private bool _hasSample;
        public event EventHandler Changed;
        public event EventHandler RemoveRequested;
        public ApiRuleCard(SettingsPal pal, float scale, CustomQuotaRule rule) : base(pal, scale)
        {
            _pal = pal; _scale = scale; BackColor = pal.Win; Width = S(500); _loading = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _title = LabelAt("额度规则", 16, 12, 320, 22, true);
            _remove = new ApiButton(pal, scale) { Text = "删除", Bounds = new Rectangle(S(416), S(8), S(68), S(28)), ForeColor = pal.Text2 };
            Controls.Add(_remove);
            _remove.Click += delegate { if (RemoveRequested != null) RemoveRequested(this, EventArgs.Empty); };
            _name = Field("额度名称", 16, 42, 468, false);
            _mode = Choice(this, "额度类型", 16, 114, 148, new[] { "百分比", "余额", "已用量 / 总量" });
            _meaningGroup = new Panel { Bounds = new Rectangle(S(176), S(114), S(148), S(62)), BackColor = pal.Win }; Controls.Add(_meaningGroup);
            _rangeGroup = new Panel { Bounds = new Rectangle(S(336), S(114), S(148), S(62)), BackColor = pal.Win }; Controls.Add(_rangeGroup);
            _meaning = Choice(_meaningGroup, "数值含义", 0, 0, 148, new[] { "已用", "剩余" });
            _range = Choice(_rangeGroup, "数值范围", 0, 0, 148, new[] { "0–100", "0–1" });
            _currency = Field("余额单位", 176, 114, 148, false);
            _multiplier = Field("余额倍率", 336, 114, 148, false);
            _value = Field("数值路径", 16, 186, 468, true);
            _total = Field("总量路径", 16, 258, 468, true);
            _reset = Field("重置时间路径（可选）", 16, 258, 468, true);
            _error = LabelAt("", 16, 330, 468, 36, true); _error.ForeColor = St.Red;
            _name.Input.Text = rule.Name; _value.Input.Text = rule.ValuePath; _total.Input.Text = rule.TotalPath; _reset.Input.Text = rule.ResetPath;
            _currency.Input.Text = rule.Currency; _multiplier.Input.Text = rule.Multiplier.ToString(CultureInfo.InvariantCulture);
            _mode.SelectedIndex = rule.Mode == "balance" ? 1 : rule.Mode == "usedTotal" ? 2 : 0;
            _meaning.SelectedIndex = rule.IsRemaining ? 1 : 0; _range.SelectedIndex = rule.Fraction ? 1 : 0;
            foreach (var field in new[] { _name, _value, _total, _reset, _currency, _multiplier }) field.Input.TextChanged += delegate { NotifyChange(); };
            foreach (var choice in new[] { _mode, _meaning, _range }) choice.SelectedIndexChanged += delegate { NotifyChange(); };
            _loading = false; UpdateFields();
        }
        private int S(int n) { return St.SiF(n, _scale); }
        private Label LabelAt(string text, int x, int y, int w, int h, bool muted)
        {
            var label = new Label { Text = text, Bounds = new Rectangle(S(x), S(y), S(w), S(h)), BackColor = _pal.Win,
                ForeColor = muted ? _pal.Text3 : _pal.Text, Font = St.FontsForScale(_scale)[1] };
            Controls.Add(label); return label;
        }
        private ApiTextField Field(string label, int x, int y, int width, bool path)
        {
            var field = new ApiTextField(_pal, _scale, label, path) { Location = new Point(S(x), S(y)), Width = S(width) };
            Controls.Add(field); return field;
        }
        private SettingsChoice Choice(Control parent, string label, int x, int y, int width, string[] items)
        {
            parent.Controls.Add(new Label { Text = label, Bounds = new Rectangle(S(x), S(y), S(width), S(24)),
                ForeColor = _pal.Text, BackColor = _pal.Win, Font = St.FontsForScale(_scale)[1] });
            var choice = new SettingsChoice(_pal, _scale, items) { Bounds = new Rectangle(S(x), S(y + 28), S(width), S(34)) };
            parent.Controls.Add(choice); return choice;
        }
        public void SetIndex(int index, bool canRemove) { _title.Text = "额度规则 " + index; _remove.Enabled = canRemove; }
        public void FocusName() { _name.Input.Focus(); }
        private void UpdateFields()
        {
            bool named = !string.IsNullOrWhiteSpace(_name.Input.Text), percent = _mode.SelectedIndex == 0, money = _mode.SelectedIndex == 1;
            _mode.Enabled = named; _meaningGroup.Visible = _rangeGroup.Visible = percent;
            _currency.Visible = _multiplier.Visible = money; _total.Visible = _mode.SelectedIndex == 2;
            _meaning.Enabled = _range.Enabled = named;
            foreach (var field in new[] { _value, _total, _reset, _currency, _multiplier }) field.Enabled = named;
            _value.SetCaption(_mode.SelectedIndex == 2 ? "已用量路径" : "数值路径");
            _reset.Top = S(_mode.SelectedIndex == 2 ? 330 : 258);
            _error.Top = _reset.Bottom + S(10); Height = _error.Bottom + S(12);
        }
        private void NotifyChange()
        {
            if (_loading) return;
            UpdateFields();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
        public CustomQuotaRule ReadRule()
        {
            return new CustomQuotaRule { Name = _name.Input.Text.Trim(), ValuePath = _value.Input.Text.Trim(), TotalPath = _total.Input.Text.Trim(),
                ResetPath = _reset.Input.Text.Trim(), Mode = _mode.SelectedIndex == 1 ? "balance" : _mode.SelectedIndex == 2 ? "usedTotal" : "percent",
                IsRemaining = _meaning.SelectedIndex == 1, Fraction = _range.SelectedIndex == 1, Currency = _currency.Input.Text.Trim(),
                Multiplier = _mode.SelectedIndex == 1 ? JsonFieldPath.Number(_multiplier.Input.Text, "倍率") : 1 };
        }
        private void UpdateValue(ApiTextField field)
        {
            object value = null; bool exists = false;
            if (_hasSample && !string.IsNullOrWhiteSpace(field.Input.Text))
                try { exists = JsonFieldPath.TryGet(_previewRoot, field.Input.Text.Trim(), out value); } catch { }
            field.SetPreview(exists, exists ? ApiJsonTree.ValueText(value) : "");
        }
        public string UpdatePreview(bool hasSample, object rawRoot, object previewRoot)
        {
            _hasSample = hasSample; _rawRoot = rawRoot; _previewRoot = previewRoot;
            UpdateValue(_value); UpdateValue(_total); UpdateValue(_reset);
            _error.Text = "";
            try
            {
                var rule = ReadRule();
                if (!hasSample) return null;
                var pool = CustomApiService.ParseRule(rule, _rawRoot);
                return pool == null ? null : CustomApiService.FormatPool(pool);
            }
            catch (Exception ex)
            {
                if (hasSample) _error.Text = ex.Message;
                return null;
            }
        }
    }

    internal class CustomApiDialog : StyledDialogBase
    {
        private readonly ProviderConfig _source;
        private readonly Panel[] _pages = new Panel[2];
        private readonly SettingsTabs _tabs;
        private readonly Dictionary<string, ApiTextField> _fields = new Dictionary<string, ApiTextField>();
        private readonly SettingsChoice _method, _display, _icon, _interval;
        private readonly Panel _requestContent, _bodyGroup, _displayGroup, _iconGroup, _nameGroup, _rulesHost, _previewPanel;
        private readonly List<int> _intervals;
        private readonly List<ApiRuleCard> _cards = new List<ApiRuleCard>();
        private readonly ApiJsonTree _tree;
        private readonly Label _responseStatus, _emptyTree;
        private readonly TextBox _previewLine;
        private readonly ApiButton _test, _ok, _add;
        private bool _loading, _testing, _displayNameEdited, _syncingDisplayName;
        private object _rawRoot, _previewRoot;
        private bool _hasSample;
        private DateTime _sampleFetchedUtc, _sampleCooldownUntilUtc;
        private readonly int _globalRefreshSec;
        private const string ExampleHeader = "Authorization: Bearer XXXXXXXXXX";
        private static readonly string[] IconTypes = { "custom", "codex", "claude", "glm", "mx", "kimi", "ds", "mimo", "qwen", "bailian" };
        public ProviderConfig Result { get; private set; }

        public CustomApiDialog(ProviderConfig existing, SettingsPal pal, float scale, int globalRefreshSec = 60)
            : base("自定义接口", pal, FitScale(scale), new Size(St.SiF(1100, FitScale(scale)), St.SiF(760, FitScale(scale))))
        {
            _loading = true; _globalRefreshSec = globalRefreshSec;
            _source = (existing ?? new ProviderConfig { Type = "custom", Name = "自定义接口", Custom = new CustomApiConfig() }).Clone();
            var config = _source.Custom ?? new CustomApiConfig();
            _tabs = new SettingsTabs(pal, Sf, new[] { "请求配置", "规则配置" }) { Bounds = new Rectangle(S(20), S(52), S(1060), S(40)) };
            Controls.Add(_tabs);
            for (int i = 0; i < 2; i++)
            {
                _pages[i] = new Panel { Bounds = new Rectangle(S(20), S(104), S(1060), S(530)), BackColor = pal.Win, AutoScroll = i == 0 };
                Controls.Add(_pages[i]);
            }
            _tabs.SelectedIndexChanged += delegate { SelectTab(_tabs.SelectedIndex); };
            _requestContent = new Panel { Bounds = new Rectangle(S(130), 0, S(800), S(470)), BackColor = pal.Win };
            _pages[0].Controls.Add(_requestContent);
            Field(_requestContent, "name", "接口名称", 0, 0, 800);
            _method = Choice(_requestContent, "请求方式", 0, 72, 108, new[] { "GET", "POST" });
            Field(_requestContent, "url", "接口地址", 124, 72, 676);
            Field(_requestContent, "headers", "请求头（每行 名称: 值）", 0, 144, 800, 80);
            LabelAt(_requestContent, "将 XXXXXXXXXX 替换为令牌，也可修改请求头；无需认证可清空。请求头与请求体加密保存。", 0, 260, 800, 34, true);
            _bodyGroup = new Panel { Bounds = new Rectangle(0, S(300), S(800), S(132)), BackColor = pal.Win };
            _requestContent.Controls.Add(_bodyGroup);
            Field(_bodyGroup, "body", "请求体（默认 JSON，Content-Type 可在请求头中修改）", 0, 0, 800, 96);
            _displayGroup = new Panel { Bounds = new Rectangle(0, S(300), S(800), S(136)), BackColor = pal.Win };
            _requestContent.Controls.Add(_displayGroup);
            _display = Choice(_displayGroup, "显示方式", 0, 0, 388, new[] { "图标", "名称" }); _display.Segmented = true;
            _intervals = RefreshChoices.Values(_source.RefreshSec, true);
            _interval = Choice(_displayGroup, "刷新间隔", 412, 0, 388, _intervals.ConvertAll(RefreshChoices.Label).ToArray());
            _iconGroup = new Panel { Bounds = new Rectangle(0, S(72), S(800), S(64)), BackColor = pal.Win }; _displayGroup.Controls.Add(_iconGroup);
            _icon = Choice(_iconGroup, "选择图标", 0, 0, 388, new[] { "通用接口", "Codex", "Claude", "GLM", "MiniMax", "Kimi", "DeepSeek", "小米 MiMo", "千问", "百炼" }); _icon.IconTypes = IconTypes;
            _nameGroup = new Panel { Bounds = _iconGroup.Bounds, BackColor = pal.Win }; _displayGroup.Controls.Add(_nameGroup);
            Field(_nameGroup, "displayName", "显示名称", 0, 0, 388);

            var rules = _pages[1];
            LabelAt(rules, "接口响应", 0, 0, 220, 26, false);
            ButtonAt(rules, "展开", 364, 0, 68, delegate { _tree.ExpandVisibleData(); });
            ButtonAt(rules, "折叠", 440, 0, 68, delegate { _tree.CollapseAll(); });
            _responseStatus = LabelAt(rules, "先点击“测试与配置”获取 JSON，再将节点拖到右侧字段。", 0, 34, 508, 36, true);
            var treeFrame = new ApiRoundedPanel(pal, Sf) { Bounds = new Rectangle(0, S(76), S(508), S(448)) };
            rules.Controls.Add(treeFrame);
            _tree = new ApiJsonTree(pal, Sf) { Bounds = new Rectangle(S(8), S(8), S(492), S(432)) }; treeFrame.Controls.Add(_tree);
            _emptyTree = LabelAt(treeFrame, "获取响应后，这里会显示可展开的 JSON。\n拖动节点即可填写右侧路径。", 24, 164, 454, 76, true);
            _emptyTree.TextAlign = ContentAlignment.MiddleCenter; _emptyTree.BringToFront();
            LabelAt(rules, "额度规则", 538, 0, 300, 26, false);
            _add = ButtonAt(rules, "+ 添加额度", 920, 0, 120, delegate { AddRule(new CustomQuotaRule(), true); });
            LabelAt(rules, "先填写额度名称，再拖入数值。数组保留具体下标。", 538, 34, 502, 36, true);
            _rulesHost = new Panel { Bounds = new Rectangle(S(538), S(76), S(522), S(448)), BackColor = pal.Win, AutoScroll = true };
            rules.Controls.Add(_rulesHost);
            _previewPanel = new ApiRoundedPanel(pal, Sf) { Bounds = new Rectangle(S(20), S(640), S(1060), S(62)) };
            Controls.Add(_previewPanel); LabelAt(_previewPanel, "显示预览", 16, 6, 1028, 22, true);
            _previewLine = new TextBox { Bounds = new Rectangle(S(16), S(30), S(1028), S(24)), ReadOnly = true, Multiline = false, AutoSize = false,
                ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None, BackColor = pal.Win, ForeColor = pal.Text, Font = Fs[1] };
            _previewPanel.Controls.Add(_previewLine);
            // 实际父控件承载底栏背景，按钮清除四角时沿用同一底色。
            var footer = new Panel { Bounds = new Rectangle(0, S(704), ClientSize.Width, S(56)), BackColor = pal.Side };
            footer.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(pal.SectionLine)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            Controls.Add(footer);
            _test = ButtonAt(footer, "测试与配置", 20, 6, 136, delegate { Test(); });
            ButtonAt(footer, "取消", 868, 6, 96, delegate { Close(); });
            _ok = ButtonAt(footer, "确认", 976, 6, 104, delegate { Confirm(); });
            _ok.Primary = true;
            _fields["name"].Input.Text = _source.Name;
            _displayNameEdited = !string.IsNullOrWhiteSpace(config.DisplayName);
            _fields["displayName"].Input.Text = _displayNameEdited ? config.DisplayName : InterfaceName();
            _fields["url"].Input.Text = config.Url;
            _fields["headers"].Input.Text = (existing == null ? ExampleHeader : _source.GetKey() ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            _fields["body"].Input.Text = config.GetBody() ?? "";
            _method.SelectedIndex = config.HttpMethod == "POST" ? 1 : 0;
            _display.SelectedIndex = _source.ShowName ? 1 : 0;
            _icon.SelectedIndex = Math.Max(0, Array.IndexOf(IconTypes, _source.IconType));
            _interval.SelectedIndex = Math.Max(0, _intervals.IndexOf(_source.RefreshSec));
            if (config.Rules != null) foreach (var rule in config.Rules) if (rule != null) AddRule(rule, false);
            if (_cards.Count == 0) AddRule(new CustomQuotaRule(), false);
            _fields["name"].Input.TextChanged += delegate { SyncDisplayName(); };
            _fields["displayName"].Input.TextChanged += delegate { if (!_loading && !_syncingDisplayName) _displayNameEdited = true; };
            foreach (string key in new[] { "url", "headers", "body" }) _fields[key].Input.TextChanged += delegate { ClearResponse(); };
            _method.SelectedIndexChanged += delegate { UpdateRequestLayout(); ClearResponse(); };
            _display.SelectedIndexChanged += delegate { UpdateRequestLayout(); };
            _loading = false; UpdateRequestLayout(); LayoutRules(); UpdatePreview(); SelectTab(0);
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); } };
        }
        private static float FitScale(float scale)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            return Math.Max(0.5f, Math.Min(scale, Math.Min((area.Width - 40) / 1100f, (area.Height - 40) / 760f)));
        }
        private Label LabelAt(Control parent, string text, int x, int y, int width, int height, bool muted)
        {
            var label = new Label { Text = text, Bounds = new Rectangle(S(x), S(y), S(width), S(height)), Font = Fs[1],
                ForeColor = muted ? Pal.Text3 : Pal.Text, BackColor = Pal.Win };
            parent.Controls.Add(label); return label;
        }
        private void Field(Control parent, string key, string label, int x, int y, int width, int height = 34)
        {
            var field = new ApiTextField(Pal, Sf, label, false, height) { Bounds = new Rectangle(S(x), S(y), S(width), S(28 + height)) };
            parent.Controls.Add(field); _fields.Add(key, field);
        }
        private SettingsChoice Choice(Control parent, string label, int x, int y, int width, string[] items)
        {
            LabelAt(parent, label, x, y, width, 24, false);
            var choice = new SettingsChoice(Pal, Sf, items) { Bounds = new Rectangle(S(x), S(y + 28), S(width), S(34)) };
            parent.Controls.Add(choice); return choice;
        }
        private ApiButton ButtonAt(Control parent, string text, int x, int y, int width, Action click)
        {
            var button = new ApiButton(Pal, Sf) { Text = text, Bounds = new Rectangle(S(x), S(y), S(width), S(34)) };
            button.Click += delegate { click(); }; parent.Controls.Add(button); return button;
        }
        internal void SelectTab(int index)
        {
            for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
            _tabs.SelectedIndex = index; _previewPanel.Visible = index == 1;
            _test.Text = index == 0 ? "测试与配置" : "重新获取响应";
        }
        private void UpdateRequestLayout()
        {
            Point scroll = _pages[0].AutoScrollPosition;
            _pages[0].SuspendLayout(); _pages[0].AutoScrollPosition = Point.Empty;
            _bodyGroup.Visible = _method.SelectedIndex == 1;
            _displayGroup.Top = S(_method.SelectedIndex == 1 ? 444 : 300);
            _requestContent.Height = _displayGroup.Bottom + S(12);
            _iconGroup.Visible = _display.SelectedIndex == 0; _nameGroup.Visible = _display.SelectedIndex == 1;
            _pages[0].AutoScrollMinSize = new Size(0, _requestContent.Height);
            _pages[0].ResumeLayout(true); _pages[0].AutoScrollPosition = new Point(0, -scroll.Y);
        }
        private string InterfaceName() { return string.IsNullOrWhiteSpace(_fields["name"].Input.Text) ? "自定义接口" : _fields["name"].Input.Text.Trim(); }
        private void SyncDisplayName()
        {
            if (_displayNameEdited) return;
            _syncingDisplayName = true;
            try { _fields["displayName"].Input.Text = InterfaceName(); } finally { _syncingDisplayName = false; }
        }
        private void AddRule(CustomQuotaRule rule, bool focus)
        {
            if (_cards.Count >= 100) return;
            var card = new ApiRuleCard(Pal, Sf, rule);
            card.SizeChanged += delegate { LayoutRules(); };
            card.Changed += delegate { UpdatePreview(); };
            card.RemoveRequested += delegate
            {
                if (_cards.Count <= 1) return;
                _cards.Remove(card); _rulesHost.Controls.Remove(card); card.Dispose(); LayoutRules(); UpdatePreview();
            };
            _cards.Add(card); _rulesHost.Controls.Add(card); LayoutRules();
            if (!_loading) UpdatePreview();
            if (focus) { _rulesHost.ScrollControlIntoView(card); card.FocusName(); }
        }
        private void LayoutRules()
        {
            Point scroll = _rulesHost.AutoScrollPosition;
            _rulesHost.SuspendLayout(); _rulesHost.AutoScrollPosition = Point.Empty;
            int y = 0;
            for (int i = 0; i < _cards.Count; i++)
            { _cards[i].Location = new Point(0, y); _cards[i].SetIndex(i + 1, _cards.Count > 1); y += _cards[i].Height + S(12); }
            _rulesHost.AutoScrollMinSize = new Size(0, y); _rulesHost.ResumeLayout(true);
            _rulesHost.AutoScrollPosition = new Point(0, -scroll.Y); _add.Enabled = _cards.Count < 100;
        }
        private void ClearResponse()
        {
            if (_loading) return;
            _hasSample = false; _rawRoot = _previewRoot = null; _tree.Nodes.Clear(); _emptyTree.Visible = true;
            _responseStatus.Text = "请求已修改，请点击“测试与配置”重新获取 JSON。"; UpdatePreview();
        }
        private void UpdatePreview()
        {
            if (_loading) return;
            var parts = new List<string>();
            foreach (var card in _cards)
            {
                string part = card.UpdatePreview(_hasSample, _rawRoot, _previewRoot);
                if (!string.IsNullOrEmpty(part)) parts.Add(part);
            }
            _previewLine.Text = parts.Count > 0 ? string.Join(" ; ", parts.ToArray())
                : _hasSample ? "当前响应没有可展示的额度。" : "获取响应后，配置结果会在这里实时显示。";
            _previewLine.SelectionStart = 0; _previewLine.SelectionLength = 0;
        }
        private ProviderConfig ReadRequest()
        {
            var p = _source.Clone(); p.Type = "custom"; p.Name = InterfaceName(); p.ShowName = _display.SelectedIndex == 1;
            p.RefreshSec = _intervals[_interval.SelectedIndex];
            p.Custom = new CustomApiConfig { Url = _fields["url"].Input.Text.Trim(), Method = _method.SelectedIndex == 1 ? "POST" : "GET",
                IconType = IconTypes[_icon.SelectedIndex], DisplayName = _displayNameEdited ? _fields["displayName"].Input.Text.Trim() : "" };
            p.Custom.ValidateRequest(); CustomApiService.ParseHeaders(_fields["headers"].Input.Text);
            p.SetKey(_fields["headers"].Input.Text.Trim()); p.Custom.SetBody(_fields["body"].Input.Text); return p;
        }
        internal ProviderConfig ReadDraft()
        {
            var p = ReadRequest();
            foreach (var card in _cards) p.Custom.Rules.Add(card.ReadRule());
            p.Custom.Validate(); return p;
        }
        private void Confirm()
        {
            try
            {
                Result = ReadDraft();
                if (_hasSample)
                {
                    var status = QuotaService.EmptyStatus(Result, null);
                    foreach (var rule in Result.Custom.Rules)
                    {
                        var pool = CustomApiService.ParseRule(rule, _rawRoot);
                        if (pool != null) status.Pools.Add(pool);
                    }
                    status.Primary5h = status.Pools.Count == 0 ? null : status.Pools[0];
                    foreach (var pool in status.Pools) if (!pool.IsMoney && pool.Percent >= 100) { status.Primary5h = pool; break; }
                    status.UpdatedAt = _sampleFetchedUtc.ToLocalTime();
                    Result.LastResults = new List<ProviderStatus> { status }; Result.LastFetchUtc = _sampleFetchedUtc;
                    Result.CooldownUntilUtc = _sampleCooldownUntilUtc;
                    Result.NextRefreshUtc = DateTime.UtcNow.AddSeconds(Result.EffectiveRefreshSec(_globalRefreshSec));
                    if (Result.CooldownUntilUtc > Result.NextRefreshUtc) Result.NextRefreshUtc = Result.CooldownUntilUtc;
                }
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { StyledMsg.Show(this, "检查配置", ex.Message, Pal, Sf); }
        }
        private void Test()
        {
            if (_testing) return;
            ProviderConfig draft;
            try { draft = ReadRequest(); draft.Enabled = true; }
            catch (Exception ex) { StyledMsg.Show(this, "检查请求", ex.Message, Pal, Sf); return; }
            SelectTab(1); _testing = true; _test.Enabled = _ok.Enabled = _pages[0].Enabled = false;
            _responseStatus.Text = "正在发送 " + draft.Custom.HttpMethod + " 请求…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                CustomFetchResult response = null; object root = null; string error = null;
                try { response = CustomApiService.Fetch(draft, true); root = CustomApiService.ParseJson(response.Json); }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed) return;
                        _testing = false; _test.Enabled = _ok.Enabled = _pages[0].Enabled = true;
                        if (error != null)
                        { _responseStatus.Text = error + (_hasSample ? "（仍显示上次响应）" : ""); return; }
                        _rawRoot = root; _previewRoot = response.PreviewRoot; _hasSample = true;
                        _sampleFetchedUtc = response.FetchedUtc; _sampleCooldownUntilUtc = DateTime.UtcNow.AddSeconds(response.WaitSeconds);
                        _tree.SetJson(_previewRoot); _emptyTree.Visible = false;
                        _responseStatus.Text = (response.Cached ? "已使用缓存响应" : "已获取响应") + " · 拖动节点填写路径"
                            + (response.WaitSeconds > 0 ? " · " + response.WaitSeconds + " 秒冷却" : "");
                        UpdatePreview();
                    });
                }
                catch (InvalidOperationException) { }
            });
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

    // ============================= 面板配色 =============================
    // 柔和蓝灰配色供设置、对话框和详情面板使用，悬浮窗背景也共用 Win。
    // 标题栏 / 「主题」分段控件切换后自动保存，并同步所有窗口的深浅主题。
    internal struct SettingsPal
    {
        public Color Win, Side, Field, Text, Text2, Text3, Line, Line2, SectionLine, Hover, Sel, Pill, PillText, Primary, PrimaryHover, PrimaryText, Track, ToggleOff, CardHover, Danger, Green, GreenBg, Amber;
        public bool Dark;   // 品牌图标等主题资源跟随当前面板配色。

        internal static Color C(int rgb) { return Color.FromArgb((int)(0xFF000000u | (uint)rgb)); }

        // 状态色与按下态共用的颜色插值。
        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public Color RemainColor(int remainPct)
        {
            remainPct = Math.Max(0, Math.Min(100, remainPct));
            return remainPct >= 50 ? Mix(Amber, Green, (remainPct - 50) / 50.0) : Mix(Danger, Amber, remainPct / 50.0);
        }

        // 对照 design/theme-proposal.html：灰白浅色 / 石墨深色，区域分割线独立加深。
        public static SettingsPal For(bool dark)
        {
            SettingsPal p = new SettingsPal { Dark = dark };
            if (dark)
            {
                p.Win = C(0x2D3032); p.Side = C(0x26292B); p.Field = C(0x333739); p.Text = C(0xD9DCD8); p.Text2 = C(0xA7AFAE);
                p.Text3 = C(0x929B9A); p.Line = C(0x4B5254); p.Line2 = C(0x3E4546); p.SectionLine = C(0x5A6368); p.Hover = C(0x3A4144);
                p.Sel = C(0x364853); p.Pill = C(0x344650); p.PillText = C(0x95BDD7); p.Primary = C(0x40739B); p.PrimaryHover = C(0x45789F); p.PrimaryText = C(0xFAFBF9);
                p.Track = C(0x495152); p.ToggleOff = C(0x4B5254); p.CardHover = C(0x73838B); p.Danger = C(0xD79C91);
                p.Green = C(0x96BBA2); p.GreenBg = C(0x35483D); p.Amber = C(0xCBA970);
            }
            else
            {
                p.Win = C(0xFAFAFA); p.Side = C(0xF0F1F2); p.Field = C(0xF4F5F6); p.Text = C(0x393E41); p.Text2 = C(0x6C7377);
                p.Text3 = C(0x858C8E); p.Line = C(0xCED3D8); p.Line2 = C(0xE0E3E6); p.SectionLine = C(0xB8BEC6); p.Hover = C(0xEAEDF0);
                p.Sel = C(0xE3EBEF); p.Pill = C(0xE5EEF2); p.PillText = C(0x356D95); p.Primary = C(0x3E7198); p.PrimaryHover = C(0x346487); p.PrimaryText = C(0xFAFBFC);
                p.Track = C(0xDCE0E4); p.ToggleOff = C(0xCED3D8); p.CardHover = C(0x9BA9B1); p.Danger = C(0xA5584E);
                p.Green = C(0x467B60); p.GreenBg = C(0xE8EFE7); p.Amber = C(0x986B2E);
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
            if (IsDisposed || e.Button != MouseButtons.Left || !ClientRectangle.Contains(e.Location)) return;
            int i = ItemAt(e.Location);
            if (i >= 0) Run(i);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // 鼠标抬起时先让 OnMouseClick 执行菜单动作，再处理意外丢失捕获。
            if (Visible && !Capture && IsHandleCreated)
                BeginInvoke((Action)delegate { if (!IsDisposed && Visible && !Capture) Close(); });
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
        private readonly List<ProviderConfig> _editList = new List<ProviderConfig>(); // 独立编辑副本，每次修改自动提交。
        private bool _providersChanged;
        private bool _saving;
        private bool _saveFailed;

        // ---- 设置状态（纯字段而非控件：跨屏 DPI 重建布局时状态天然保留，无需备份回填） ----
        private bool _pendIsland;   // 悬浮窗排列：true=条形灵动岛 / false=矩形卡片
        private int _pendInterval;  // 刷新间隔（秒）
        private int _pendOpacity;   // 不透明度 50-100
        private bool _pendTopMost;
        private bool _pendAutoStart;
        private bool _pendDark;     // 切换后自动保存并同步深浅主题。

        // ---- 窗口状态 ----
        private int _page;                    // 0=供应商 1=通用 2=关于
        private float _scrollP, _scrollG;     // 供应商页 / 通用页滚动偏移
        private string _hover;                // 悬停命中区 id
        private bool _testing;                // 「测试全部」进行中
        // ---- 关于页·检查更新状态（按钮文案与宽度随状态变，布局需重算） ----
        private bool _updateChecking;         // 检查请求进行中（按钮显示「检查中…」并忽略点击）
        private QuotaService.UpdateInfo _updateInfo; // 非 null 且 Newer=发现新版本（按钮变主色「下载新版本」）
        private string _updateMsg;            // 按钮下方状态行：已是最新 / 错误信息（发现新版时为引导文案）
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
        private Rectangle _rcMoon, _rcClose, _rcAdd, _rcCustom, _rcTest, _rcOpenDir, _rcUpdate, _rcRetrySave;
        private readonly List<Rectangle> _cardRects = new List<Rectangle>();
        private Rectangle _grp1, _grp2;
        private readonly List<int> _visRows = new List<int>();        // 通用页第一组设置行
        private readonly List<Rectangle> _rowRects = new List<Rectangle>();
        private readonly List<Rectangle> _segLayoutBtns = new List<Rectangle>();
        private readonly List<Rectangle> _segThemeBtns = new List<Rectangle>();
        private Rectangle _rcSegLayout, _rcSegTheme, _rcSelInt, _rcSlider, _rcTglTop, _rcTglAuto;

        // 通用页行号
        private const int R_LAYOUT = 0, R_INT = 1, R_OP = 2, R_THEME = 3, R_TOP = 4, R_AUTO = 5;
        // 刷新间隔预设（秒）；用户已有自定义值不在预设中时插入列表，保证不丢配置

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

            _pendIsland = _ctx.Cfg.FloatLayout == "island";
            _pendInterval = Math.Max(15, Math.Min(86400, _ctx.Cfg.RefreshSec));
            _pendOpacity = Math.Max(50, Math.Min(100, _ctx.Cfg.OpacityPct));
            _pendTopMost = _ctx.Cfg.TopMost;
            _pendAutoStart = _ctx.Cfg.AutoStart;
            _pendDark = _ctx.Cfg.Theme == "dark";

            ApplyScaleToSize();
            LayoutAll();

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; SaveChanges(); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                FinishDrag(false);
                ClosePopup();
                HideTip();
                if (!_ctx.IsExiting && !SaveChanges()) e.Cancel = true;
            };
            // 行说明气泡：悬停行标题 450ms 后浮现
            _tipTimer = new System.Windows.Forms.Timer { Interval = 450 };
            _tipTimer.Tick += delegate
            {
                _tipTimer.Stop();
                if (_tipRow >= 0 && _tipText != null && _hover == "tip:" + _tipRow) ShowTip();
            };
            Shown += delegate
            {
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
            int tbH = S(46), sideW = S(208);
            _vp = new Rectangle(sideW + S(30), tbH + S(26), W - sideW - S(60), H - tbH - S(26) - S(20));
            AddChromeHots(tbH);

            for (int pass = 0; pass < 2; pass++)
            {
                if (_page == 0) LayoutProviders();
                else if (_page == 1) LayoutGeneral();
                else { LayoutAbout(); break; }
                float c = ClampScroll(_page == 0 ? _scrollP : _scrollG);
                bool changed = (_page == 0 ? _scrollP : _scrollG) != c;
                if (_page == 0) _scrollP = c; else _scrollG = c;
                if (!changed) break;
                ClearPageLayout();
                AddChromeHots(tbH);
            }

        }

        private void ClearPageLayout()
        {
            _hots.Clear();
            _cardRects.Clear();
            _visRows.Clear();
            _rowRects.Clear();
            _segLayoutBtns.Clear();
            _segThemeBtns.Clear();
        }

        // 标题栏 + 侧栏命中区（滚动收敛重排时需补回）
        private void AddChromeHots(int tbH)
        {
            _rcRetrySave = new Rectangle(S(20), ClientSize.Height - S(88), S(168), S(28));
            if (_saveFailed) AddHot(_rcRetrySave, "retry:save");
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
            int customW = MeasureW("自定义接口", fs[1]) + S(24);
            int testW = MeasureW(_testing ? "测试中…" : "测试全部", fs[1]) + S(24);
            _rcAdd = new Rectangle(_vp.X, y, addW, btnH);
            _rcCustom = new Rectangle(_rcAdd.Right + S(10), y, customW, btnH);
            _rcTest = new Rectangle(_rcCustom.Right + S(10), y, testW, btnH);
            AddHot(_rcAdd, "add");
            AddHot(_rcCustom, "custom");
            AddHot(_rcTest, "test");
            _rcSelInt = new Rectangle(_vp.Right - S(120), y, S(120), btnH);
            AddHot(_rcSelInt, "sel:int");

            int cardH = S(54), gap = S(10);
            int listY = y + btnH + S(16);
            for (int i = 0; i < _editList.Count; i++)
            {
                var card = new Rectangle(_vp.X, listY + i * (cardH + gap), _vp.Width, cardH);
                _cardRects.Add(card);
                AddHot(card, "card:" + i); // 整卡先加（悬停高亮边框），行内按钮后加保证优先命中
                int cy = card.Y + cardH / 2;
                AddHot(new Rectangle(card.X + S(10), cy - S(9), S(20), S(18)), "grip:" + i);
                AddHot(ProviderToggleRect(card), "enabled:" + i);
                AddHot(new Rectangle(card.Right - S(14) - S(30) * 2 - S(6), cy - S(15), S(30), S(30)), "edit:" + i);
                AddHot(new Rectangle(card.Right - S(14) - S(30), cy - S(15), S(30), S(30)), "del:" + i);
            }
            _contentH = listY + _editList.Count * (cardH + gap) + S(8) - _vp.Y;
        }

        private Rectangle ProviderToggleRect(Rectangle card)
        {
            return new Rectangle(card.Right - S(14) - S(30) * 2 - S(6) - S(12) - S(40),
                card.Y + card.Height / 2 - S(11), S(40), S(22));
        }

        // 关于页固定布局（页内不滚动）：坐标与 PaintAbout 一致，命中与绘制共用 _rcOpenDir/_rcUpdate
        private void LayoutAbout()
        {
            _contentH = S(420);
            Font[] fs = Fonts(_scale);
            // 双按钮并排整体居中：更新按钮文案随检查状态变（检查中… / 下载新版本 vX），宽度须每次现测
            int w1 = MeasureW("打开配置目录", fs[1]) + S(36);
            int w2 = MeasureW(UpdateButtonText(), fs[1]) + S(36);
            int x = _vp.X + _vp.Width / 2 - (w1 + S(10) + w2) / 2;
            int iconY = _vp.Y + S(24) + S(16);
            _rcOpenDir = new Rectangle(x, iconY + S(142), w1, S(34));
            _rcUpdate = new Rectangle(x + w1 + S(10), iconY + S(142), w2, S(34));
            AddHot(_rcOpenDir, "opendir");
            AddHot(_rcUpdate, "update");
        }

        // 更新按钮文案随状态：检查中禁用 / 发现新版引导下载 / 默认
        private string UpdateButtonText()
        {
            if (_updateChecking) return "检查中…";
            if (_updateInfo != null && _updateInfo.Newer) return "下载新版本 v" + _updateInfo.Tag;
            return "检查更新";
        }

        private void LayoutGeneral()
        {
            int y = _vp.Y - (int)_scrollG;

            int pad = S(16), rowH = S(52);
            _visRows.Add(R_LAYOUT);
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
                    case R_LAYOUT:
                        _rcSegLayout = SegRect(rr, new string[] { "条形灵动岛", "矩形卡片" });
                        SegBtns(_rcSegLayout, _segLayoutBtns);
                        AddHot(_segLayoutBtns[0], "seglayout:0");
                        AddHot(_segLayoutBtns[1], "seglayout:1");
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
                        _rcSegTheme = SegRect(rr, new string[] { "浅色", "深色" });
                        // 钮内为「图标 + 文字」，比纯文字宽：每钮补图标位 S(20)
                        _rcSegTheme = new Rectangle(_rcSegTheme.X - S(40), _rcSegTheme.Y, _rcSegTheme.Width + S(40), _rcSegTheme.Height);
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
            int tbH = S(46), sideW = S(208);

            using (var b = new SolidBrush(p.Win))
                g.FillRectangle(b, ClientRectangle);

            PaintTitlebar(g, p, fs, W, tbH);
            PaintSidebar(g, p, fs, tbH, sideW, H);

            // 内容区裁剪：滚动时页面内容不侵入标题栏 / 侧栏。
            g.SetClip(new Rectangle(sideW, tbH, W - sideW, H - tbH));
            if (_page == 0) PaintProviders(g, p, fs);
            else if (_page == 1) PaintGeneral(g, p, fs);
            else PaintAbout(g, p, fs);
            g.ResetClip();

            PaintScrollbar(g, p);

            // 窗口最外圈细边（DWM 阴影之外的补充轮廓，浅色桌面上勾出边界）
            using (var pen = new Pen(p.Line))
                g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
        }

        private void PaintTitlebar(Graphics g, SettingsPal p, Font[] fs, int W, int tbH)
        {
            using (var b = new SolidBrush(p.Side))
                g.FillRectangle(b, 0, 0, W, tbH);
            // 应用图标（与托盘 / 任务栏同一枚仪表盘造型）
            var ic = new Rectangle(S(14), (tbH - S(20)) / 2, S(20), S(20));
            IconFactory.DrawGlyph(g, ic);
            using (var b = new SolidBrush(p.Text))
                TextRenderer.DrawText(g, "设置", fs[1], new Rectangle(ic.Right + S(9), 0, S(80), tbH), p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            // 主题按钮：VS Code 同款半圆对比图标（codicon color-mode），点击切换明暗主题。
            if (_hover == "moon")
                using (var path = St.RoundRect(_rcMoon, S(8)))
                using (var b = new SolidBrush(p.Hover))
                    g.FillPath(b, path);
            IconFactory.DrawColorMode(g, _rcMoon.X + _rcMoon.Width / 2f, _rcMoon.Y + _rcMoon.Height / 2f, S(16), p.Text);

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

            using (var pen = new Pen(p.SectionLine))
                g.DrawLine(pen, 0, tbH - 1, W, tbH - 1);
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
                Color c = active ? p.PillText : hot ? p.Text : p.Text2;
                PaintNavIcon(g, i, r.X + S(10), r.Y + (r.Height - S(16)) / 2, c);
                using (var b = new SolidBrush(c))
                    g.DrawString(names[i], active ? fs[1] : fs[2], b, new RectangleF(r.X + S(35), r.Y, navW - S(35), navH), SFLeftV);
            }

            // 仅写入失败时提供重试入口，保留当前填写内容。
            if (_saveFailed)
                TextRenderer.DrawText(g, "保存失败 · 点击重试", fs[4], _rcRetrySave, p.Danger,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            // 侧栏底部：应用名 + 版本
            using (var b = new SolidBrush(p.Text3))
            {
                g.DrawString("AI 额度工具", fs[4], b, S(20), H - S(48));
                g.DrawString("v" + AppInfo.Version, fs[4], b, S(20), H - S(30));
            }
        }

        // 导航图标统一用 codicon 风格（几何见 IconFactory）：server / settings-gear / info
        private void PaintNavIcon(Graphics g, int idx, float x, float y, Color c)
        {
            float size = S(16);
            if (idx == 0) IconFactory.DrawServer(g, x, y, size, c);
            else if (idx == 1) IconFactory.DrawGear(g, x, y, size, c);
            else IconFactory.DrawInfo(g, x, y, size, c);
        }

        private void PaintProviders(Graphics g, SettingsPal p, Font[] fs)
        {
            // 工具栏：＋ 添加供应商（主） / 测试全部（次）
            PaintButton(g, p, fs, _rcAdd, "＋ 添加供应商", true, _hover == "add");
            PaintButton(g, p, fs, _rcCustom, "自定义接口", false, _hover == "custom");
            // 「通用刷新间隔」右对齐贴住下拉框：文案变长后按实际字宽撑开，避免被裁切或压到「测试全部」
            const string intCap = "通用刷新间隔";
            int intCapW = MeasureW(intCap, fs[4]) + S(4);
            TextRenderer.DrawText(g, intCap, fs[4], new Rectangle(_rcSelInt.X - S(4) - intCapW, _rcSelInt.Y, intCapW, _rcSelInt.Height), p.Text2, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            PaintSelect(g, p, fs, _rcSelInt, IntervalText(_pendInterval), _hover == "sel:int");
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
                using (var b = new SolidBrush(p.Field))
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
            if (BrandIcons.Get(prov.IconType, p.Dark) != null)
                BrandIcons.Draw(g, prov.IconType, p.Dark, card.X + S(30), card.Y + cardH / 2 - S(9), S(18));
            else
                using (var b = new SolidBrush(St.AccentFor(prov.Type)))
                    g.FillEllipse(b, card.X + S(34), card.Y + cardH / 2 - S(4), S(8), S(8));

            // 名称（超长省略）；图标模式显示接口类型名（与悬浮窗/托盘一致）
            string name = Elide(g, prov.DisplayName, fs[1], S(110));
            float nameW = g.MeasureString(name, fs[1]).Width;
            using (var b = new SolidBrush(prov.Enabled ? p.Text : p.Text3))
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
            string key = prov.Enabled && prov.Type != "custom" ? (prov.GetKey() ?? "") : "";
            string masked = !prov.Enabled ? "已停用" : prov.Type == "custom" ? RefreshChoices.Label(prov.RefreshSec) : key.Length > 8 ? key.Substring(0, 4) + "******" + key.Substring(key.Length - 4) : "****";
            var toggle = ProviderToggleRect(card);
            int keyMax = toggle.Left - S(12) - (br.Right + S(12));
            // 与接口类型共用字体、绘制方式和行框，避免等宽字体的中文回退造成上下错位。
            if (keyMax > S(40))
                TextRenderer.DrawText(g, masked, fs[5], new Rectangle(br.Right + S(12), br.Y, keyMax, br.Height), p.Text3,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // 行内操作：启用开关 / 编辑（铅笔） / 删除（垃圾桶）。
            PaintToggle(g, p, toggle, prov.Enabled);
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
            if (head != "card" && head != "grip" && head != "enabled" && head != "edit" && head != "del") return -1;
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
                case R_LAYOUT: return "排列";
                case R_INT: return "刷新间隔";
                case R_OP: return "窗口不透明度";
                case R_THEME: return "主题";
                case R_TOP: return "始终置顶";
                default: return "开机启动";
            }
        }

        // 行说明文案（PaintRow 不再画小字，改为悬停行标题后的气泡提示）
        private static string RowDesc(int rowId)
        {
            switch (rowId)
            {
                case R_LAYOUT: return "条形灵动岛为内容自适应宽的圆角胶囊，贴顶自动隐藏、可拖拽定位，拖离顶边变为普通悬浮条；矩形卡片为紧凑单行卡片视图。";
                case R_INT: return "自动查询额度的时间间隔。";
                case R_OP: return "数值越低悬浮窗越透明。";
                case R_THEME: return "切换悬浮窗与详情面板的明暗主题。";
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
                case R_LAYOUT:
                    // 分段顺序：条形灵动岛在左（默认排列在前）。段位 0=条形灵动岛 / 1=矩形卡片，与 _pendIsland 一致
                    PaintSeg(g, p, fs, _rcSegLayout, _segLayoutBtns, new string[] { "条形灵动岛", "矩形卡片" }, _pendIsland ? 0 : 1, "seglayout");
                    break;
                case R_INT:
                    PaintSelect(g, p, fs, _rcSelInt, IntervalText(_pendInterval), _hover == "sel:int");
                    break;
                case R_OP:
                    PaintSlider(g, p, fs, cy);
                    break;
                case R_THEME:
                    PaintThemeSeg(g, p, fs);
                    break;
                case R_TOP:
                    PaintToggle(g, p, _rcTglTop, _pendTopMost);
                    break;
                default:
                    PaintToggle(g, p, _rcTglAuto, _pendAutoStart);
                    break;
            }
        }

        // 主题分段：钮内绘制「lucide sun/moon 图标 + 文字」，替换原 unicode 字符（☀/☾ 随字体渲染效果差）
        private void PaintThemeSeg(Graphics g, SettingsPal p, Font[] fs)
        {
            int active = _pendDark ? 1 : 0;
            int hoverIdx = HoverIdxOf("segtheme");
            using (var path = St.RoundRect(_rcSegTheme, S(8)))
            using (var b = new SolidBrush(p.Hover))
                g.FillPath(b, path);
            string[] labels = new string[] { "浅色", "深色" };
            for (int i = 0; i < _segThemeBtns.Count; i++)
            {
                var r = _segThemeBtns[i];
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
                Color c = on || hot ? p.Text : p.Text2;
                var f = on ? fs[8] : fs[3];
                float tw = g.MeasureString(labels[i], f).Width;
                float isz = S(14);
                float x = r.X + (r.Width - (isz + S(6) + tw)) / 2f;
                float cy = r.Y + r.Height / 2f;
                if (i == 0) IconFactory.DrawSun(g, x + isz / 2f, cy, isz, c);
                else IconFactory.DrawMoon(g, x + isz / 2f, cy, isz, c);
                using (var b = new SolidBrush(c))
                    g.DrawString(labels[i], f, b, new RectangleF(x + isz + S(6), r.Y, tw + S(4), r.Height), SFLeftV);
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
                using (var b = new SolidBrush(p.Field))
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
            using (var b = new SolidBrush(on ? p.Primary : p.ToggleOff))
                g.FillPath(b, path);
            int d = r.Height - S(4);
            var knob = new Rectangle(on ? r.Right - S(2) - d : r.X + S(2), r.Y + S(2), d, d);
            using (var b = new SolidBrush(on ? p.PrimaryText : p.Win))
                g.FillEllipse(b, knob);
        }

        private void PaintAbout(Graphics g, SettingsPal p, Font[] fs)
        {
            // 本页不滚动（OnMouseWheel 对 _page==2 直接返回），坐标与 LayoutAbout 一致；
            // 不再减 _scrollP——旧代码减了但页面切换不重置它，从已滚动的供应商页切过来会整体上移
            int y = _vp.Y + S(24);

            int cx = _vp.X + _vp.Width / 2;
            int iconY = y + S(16);
            IconFactory.DrawGlyph(g, new Rectangle(cx - S(28), iconY, S(56), S(56)));

            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            using (var b = new SolidBrush(p.Text))
                g.DrawString("AI 额度工具", fs[0], b, new RectangleF(_vp.X, iconY + S(70), _vp.Width, S(26)), sf);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            using (var b = new SolidBrush(p.Text2))
                g.DrawString("v" + AppInfo.Version, fs[3], b, new RectangleF(_vp.X, iconY + S(100), _vp.Width, S(18)), sf);
            // 配置挪到 AppData 后用户不易找到，提供一键定位（资源管理器选中 config.json）
            PaintButton(g, p, fs, _rcOpenDir, "打开配置目录", false, _hover == "opendir");
            // 发现新版时换主色引导下载；「检查中…」悬停态照常、点击在 OnMouseDown 被忽略
            PaintButton(g, p, fs, _rcUpdate, UpdateButtonText(), _updateInfo != null && _updateInfo.Newer, _hover == "update");
            if (!string.IsNullOrEmpty(_updateMsg))
            {
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                using (var b = new SolidBrush(p.Text2))
                    g.DrawString(_updateMsg, fs[3], b, new RectangleF(_vp.X, _rcOpenDir.Bottom + S(12), _vp.Width, S(18)), sf);
            }
        }

        private void PaintButton(Graphics g, SettingsPal p, Font[] fs, Rectangle r, string text, bool primary, bool hot)
        {
            using (var path = St.RoundRect(r, S(8)))
            {
                if (primary)
                {
                    using (var b = new SolidBrush(hot ? p.PrimaryHover : p.Primary))
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

        private void PaintScrollbar(Graphics g, SettingsPal p)
        {
            if (_page == 2) return; // 关于页固定布局不滚动，_scrollG 在本页也不过钳制，画出来只是装饰
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
        // 刷新间隔候选（秒）：预设 + 已有自定义值（保证旧配置里的任意值可见可选）
        private int[] IntervalVals()
        {
            return RefreshChoices.Values(_pendInterval, false).ToArray();
        }

        private static string IntervalText(int v)
        {
            return RefreshChoices.Label(v);
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

            if (id == "moon") { _pendDark = !_pendDark; _hover = null; SaveChanges(); Invalidate(); return; }
            if (id == "close") { Close(); return; }
            if (id == "retry:save") { SaveChanges(); return; }
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
                if (id == "custom") { AddCustomProvider(); return; }
                if (id == "sel:int") { OpenIntervalPopup(); return; }
                if (id == "test") { TestAll(); return; }
                if (id != null && id.StartsWith("enabled:"))
                {
                    int idx = int.Parse(id.Substring(8));
                    _editList[idx].Enabled = !_editList[idx].Enabled;
                    SaveChanges(true);
                    Invalidate();
                    return;
                }
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
                if (id != null && id.StartsWith("seglayout:")) { _pendIsland = int.Parse(id.Substring(10)) == 0; SaveChanges(); Invalidate(); return; } // 段位 0=条形灵动岛 / 1=矩形卡片
                if (id != null && id.StartsWith("segtheme:")) { _pendDark = int.Parse(id.Substring(9)) == 1; SaveChanges(); Invalidate(); return; }
                if (id == "tgl:top") { _pendTopMost = !_pendTopMost; SaveChanges(); Invalidate(); return; }
                if (id == "tgl:auto") { _pendAutoStart = !_pendAutoStart; SaveChanges(); Invalidate(); return; }
                if (id == "sel:int") { OpenIntervalPopup(); return; }
                if (id == "sld:op") { _dragSlider = true; Capture = true; SliderSet(e.X); return; }
            }
            else if (_page == 2)
            {
                if (id == "opendir") { OpenConfigDir(); return; }
                if (id == "update") { OnUpdateClick(); return; }
            }
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
                // 光标样式由 WndProc 的 WM_SETCURSOR 统一设置，这里只更新悬停态
                UpdateTip(h);
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) FinishDrag(true);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) FinishDrag(true);
        }

        private void FinishDrag(bool save)
        {
            bool changed = _dragSlider || _dragCard >= 0;
            _dragWindow = false;
            _dragSlider = false;
            _dragCard = -1;
            Capture = false;
            if (save && changed) SaveChanges();
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_dragWindow || _dragCard >= 0 || _dragSlider) return; // 拖拽中的 MouseLeave（含 Capture 边界）不当作真离开
            if (_hover != null) { _hover = null; Invalidate(); }
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

        private void SliderSet(int mouseX)
        {
            int v = 50 + (int)Math.Round((mouseX - _rcSlider.X) * 50.0 / Math.Max(1, _rcSlider.Width));
            v = Math.Max(50, Math.Min(100, v));
            if (v != _pendOpacity)
            {
                _pendOpacity = v;
                _ctx.Floating.ApplyOpacity(v);
                Invalidate();
            }
        }

        // 拖拽跟手：被拖卡片以浮层形式跟随鼠标（画在 _dragDrawY），其余卡片让位；
        // 拖拽卡中心线越过相邻卡中点时实时换位，松手或失去鼠标捕获时自动提交。
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
                _providersChanged = true;
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
                SaveChanges();
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
        private void AddCustomProvider()
        {
            using (var dialog = new CustomApiDialog(null, P, _scale, _pendInterval))
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result != null)
                { _editList.Add(dialog.Result); SaveChanges(true); LayoutAll(); Invalidate(); }
        }

        private void AddProvider()
        {
            using (var dlg = new ProviderEditDialog(null, P, _scale))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    _editList.Add(dlg.Result);
                    SaveChanges(true);
                    LayoutAll();
                    Invalidate();
                }
            }
        }

        private void EditCard(int idx)
        {
            if (idx < 0 || idx >= _editList.Count) return;
            if (_editList[idx].Type == "custom")
            {
                using (var custom = new CustomApiDialog(_editList[idx], P, _scale, _pendInterval))
                    if (custom.ShowDialog(this) == DialogResult.OK && custom.Result != null)
                    { _editList[idx] = custom.Result; SaveChanges(true); LayoutAll(); Invalidate(); }
                return;
            }
            using (var dlg = new ProviderEditDialog(_editList[idx], P, _scale))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    dlg.Result.RuntimeId = _editList[idx].RuntimeId;
                    _editList[idx] = dlg.Result;
                    SaveChanges(true);
                    LayoutAll();
                    Invalidate();
                }
            }
        }

        private void DeleteCard(int idx)
        {
            if (idx < 0 || idx >= _editList.Count) return;
            _editList.RemoveAt(idx);
            SaveChanges(true);
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

        [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr name);

        // 悬停光标统一用现场加载的系统共享句柄:WinForms 缓存的 Cursors.Hand 实测是 32×32 单色句柄,
        // 混合 DPI 下不随所在屏正确缩放(1080P 上显小显糙);系统句柄由 Windows 按当前屏 DPI 渲染
        private void UpdateHoverCursor()
        {
            IntPtr want = _hover == null || _hover.StartsWith("tip:")
                ? LoadCursor(IntPtr.Zero, (IntPtr)32512)                          // IDC_ARROW
                : (_hover.StartsWith("grip:") ? LoadCursor(IntPtr.Zero, (IntPtr)32646) // IDC_SIZEALL
                : LoadCursor(IntPtr.Zero, (IntPtr)32649));                        // IDC_HAND
            SetCursor(want);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCCALCSIZE = 0x0083;
            const int WM_NCHITTEST = 0x0084;
            const int WM_NCACTIVATE = 0x0086;
            const int WM_SETCURSOR = 0x0020;
            if (m.Msg == WM_SETCURSOR && ((long)m.LParam & 0xFFFF) == 1) // 本窗口恒 HTCLIENT
            {
                UpdateHoverCursor();
                m.Result = (IntPtr)1;
                return;
            }
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

        // ============================= 自动保存 / 测试 =============================
        private bool SaveChanges(bool providersChanged = false)
        {
            _providersChanged |= providersChanged;
            if (_saving) return false;
            var cfg = _ctx.Cfg;
            string theme = _pendDark ? "dark" : "light";
            string layout = _pendIsland ? "island" : "card";
            bool refreshChanged = cfg.RefreshSec != _pendInterval;
            bool opacityChanged = cfg.OpacityPct != _pendOpacity;
            bool startupChanged = cfg.AutoStart != _pendAutoStart;
            bool appearanceChanged = cfg.TopMost != _pendTopMost || cfg.Theme != theme || cfg.FloatLayout != layout;
            if (!_providersChanged && !refreshChanged && !opacityChanged && !startupChanged && !appearanceChanged)
            {
                if (_saveFailed) { _saveFailed = false; LayoutAll(); Invalidate(); }
                return true;
            }

            _saving = true;
            try
            {
                // 保存失败时恢复运行配置；编辑内容留在当前窗口，可继续修改或重试。
                var oldProviders = cfg.Providers;
                int oldRefreshSec = cfg.RefreshSec, oldOpacityPct = cfg.OpacityPct;
                bool oldTopMost = cfg.TopMost, oldAutoStart = cfg.AutoStart;
                string oldTheme = cfg.Theme, oldFloatLayout = cfg.FloatLayout;

                if (_providersChanged)
                {
                    // 列表和元素都独立复制，防止后续编辑直接修改运行中的配置。
                    // 自定义接口刚测试过的响应一并提交，避免确认后立即重复查询。
                    var providers = new List<ProviderConfig>();
                    foreach (var source in _editList)
                    {
                        var copy = source.Clone();
                        copy.LastFetchUtc = source.LastFetchUtc;
                        copy.NextRefreshUtc = source.NextRefreshUtc;
                        copy.CooldownUntilUtc = source.CooldownUntilUtc;
                        if (source.LastResults != null)
                            copy.LastResults = source.LastResults.ConvertAll(st => st.WithConfig(copy));
                        providers.Add(copy);
                    }
                    cfg.Providers = providers;
                }
                cfg.RefreshSec = _pendInterval;
                cfg.OpacityPct = _pendOpacity;
                cfg.TopMost = _pendTopMost;
                cfg.AutoStart = _pendAutoStart;
                cfg.Theme = theme;
                cfg.FloatLayout = layout;
                if (!cfg.Save())
                {
                    cfg.Providers = oldProviders;
                    cfg.RefreshSec = oldRefreshSec;
                    cfg.OpacityPct = oldOpacityPct;
                    cfg.TopMost = oldTopMost;
                    cfg.AutoStart = oldAutoStart;
                    cfg.Theme = oldTheme;
                    cfg.FloatLayout = oldFloatLayout;
                    _ctx.Floating.ApplyOpacity(oldOpacityPct);
                    _saveFailed = true;
                    LayoutAll();
                    Invalidate();
                    StyledMsg.Show(this, "自动保存失败", "config.json 写入失败，可能磁盘已满或被其他程序占用。\n本次修改尚未生效，填写内容已保留在当前窗口。\n处理后点击侧栏「保存失败 · 点击重试」或按 Ctrl+S；关闭窗口时也会重试。", P, _scale);
                    return false;
                }

                if (startupChanged) Config.SetAutoStart(cfg.AutoStart);
                if (_providersChanged || refreshChanged)
                    _ctx.ApplyConfig(oldProviders, oldRefreshSec, false);
                else if (appearanceChanged)
                    _ctx.ApplyFloatingConfig();
                else if (opacityChanged)
                    _ctx.Floating.ApplyOpacity(_pendOpacity);

                if (_providersChanged)
                {
                    // 已提交的采样结果归运行配置管理，后续提交从最新运行状态继承。
                    _editList.Clear();
                    foreach (var provider in cfg.Providers) _editList.Add(provider.Clone());
                }
                _providersChanged = false;
                _saveFailed = false;
                LayoutAll();
                Invalidate();
                return true;
            }
            finally { _saving = false; }
        }

        // 资源管理器打开 %LOCALAPPDATA%\QuotaWidget 并选中 config.json：
        // 配置不再放 exe 同目录，用户反馈/备份时需要入口找到它（error.log 也在该目录）
        private void OpenConfigDir()
        {
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Config.FilePath + "\"");
            }
            catch { }
        }

        // 检查更新按钮：检查中忽略点击；已发现新版则跳实际应答源的发布页；否则发起检查
        private void OnUpdateClick()
        {
            if (_updateChecking) return;
            if (_updateInfo != null && _updateInfo.Newer)
            {
                try { System.Diagnostics.Process.Start(_updateInfo.PageUrl); }
                catch { }
                return;
            }
            StartUpdateCheck();
        }

        // 后台双源检查（Gitee 主、GitHub 备，单边失败静默，见 QuotaService.CheckUpdate）。
        // 与 TestAll 相同的线程池套路：BeginInvoke 外包 try-catch、回调内判 IsDisposed，
        // 用户在请求期间关掉设置窗不会把线程池异常升级成进程崩溃
        private void StartUpdateCheck()
        {
            _updateChecking = true;
            _updateMsg = null;
            _hover = null;
            UpdateHoverCursor();
            Invalidate();
            ThreadPool.QueueUserWorkItem(delegate
            {
                QuotaService.UpdateInfo info = null;
                string err = null;
                try { info = QuotaService.CheckUpdate(); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        _updateChecking = false;
                        if (IsDisposed) return;
                        _updateInfo = info;
                        if (info != null && info.Newer)
                            _updateMsg = "发现新版本 v" + info.Tag + "，点击右侧按钮前往下载";
                        else if (info != null)
                            _updateMsg = "已是最新版本 ✓（v" + AppInfo.Version + "）";
                        else
                            _updateMsg = "检查失败：" + err;
                        LayoutAll(); // 按钮文案随状态变（检查中… / 下载新版本 vX），宽度需重测
                        Invalidate();
                    });
                }
                catch { /* 窗口已销毁，放弃回调 */ }
            });
        }

        private void TestAll()
        {
            if (_testing) return;
            var snapshot = new List<ProviderConfig>();
            foreach (var pr in _editList)
                if (pr.CanQuery) snapshot.Add(pr.Clone());
            if (snapshot.Count == 0)
            {
                StyledMsg.Show(this, "测试连接", "请先添加或开启已配置的供应商", P, _scale);
                return;
            }
            _testing = true;
            _hover = null;
            UpdateHoverCursor();
            Invalidate();
            ThreadPool.QueueUserWorkItem(delegate
            {
                var sb = new StringBuilder();
                foreach (var pr in snapshot)
                {
                    try
                    {
                        foreach (var st in QuotaService.FetchProvider(pr, true))
                        {
                            sb.AppendLine("【" + st.DisplayName + "】" + (st.Error == null ? "连接成功 ✓  " + st.Pools.Count + " 个额度池" : "失败 ✗  " + st.Error));
                            foreach (var q in st.Pools)
                                sb.AppendLine("   · " + q.Label + "：" + (q.IsMoney ? q.Currency + St.FmtMoney(q.Remaining) : "已用 " + q.Percent + "%"));
                        }
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
            // 单实例保护：避免重复托盘图标、刷新请求与配置写入冲突
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

