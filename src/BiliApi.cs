// HTTP session (cookies + WBI signing) and the Bilibili endpoints.
// Every endpoint and parameter here was verified live before being written down;
// see the notes on each method for the quirks that matter.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace BiliGetter
{
    internal sealed class BiliException : Exception
    {
        public int Code;
        public BiliException(int code, string message) : base(message) { Code = code; }
    }

    public sealed class BiliSession
    {
        public readonly CookieContainer Cookies = new CookieContainer();
        public string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        public string MixinKey;
        public long Mid;
        public string UName = "";
        public bool IsLogin;

        private static readonly int[] MixinTab = new int[] {
            46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13,
            37,48,7,16,24,55,40,61,26,17,0,1,60,51,30,4,22,25,54,21,56,59,6,63,57,62,11,36,20,34,44,52
        };

        /// <summary>
        /// 整个会话共用一个 HttpClient：连接复用、Cookie 由 handler 统一管理。
        /// 用 SocketsHttpHandler 而非默认 handler，是为了显式控制连接池与解压行为。
        /// .NET 10 上 TLS 版本由系统协商，不再需要 ServicePointManager。
        /// </summary>
        private readonly HttpClient _http;

        public BiliSession()
        {
            SocketsHttpHandler handler = new SocketsHttpHandler();
            handler.CookieContainer = Cookies;
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            handler.ConnectTimeout = TimeSpan.FromSeconds(15);
            handler.MaxConnectionsPerServer = 16;
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
            handler.AllowAutoRedirect = true;

            _http = new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(30);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        }

        // ------------------------------------------------------------------ http
        private string GetTextOnce(string url, string referer)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrEmpty(referer))
                    req.Headers.TryAddWithoutValidation("Referer", referer);
                req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");

                // Send() 是同步 API（.NET 5+）：本程序的调用点大多在后台线程上同步执行，
                // 用它比 async/await 更简单，也不会在 UI 线程上死锁。
                using (HttpResponseMessage resp = _http.Send(req))
                {
                    resp.EnsureSuccessStatusCode();
                    // HttpContent 没有同步的 ReadAsByteArray()，只有异步版本；
                    // 这里用同步的 ReadAsStream() 自己拷到 MemoryStream，保持全同步。
                    using (Stream st = resp.Content.ReadAsStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        st.CopyTo(ms);
                        // Force UTF-8: bilibili's pugv endpoints omit the charset, and
                        // decoding by the declared charset would mangle Chinese.
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
        }

        public string GetText(string url, string referer)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try { return GetTextOnce(url, referer); }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 3) System.Threading.Thread.Sleep(1100 * attempt);
                }
            }
            throw last;
        }

        public Dictionary<string, object> GetJson(string url, string referer)
        {
            string text = GetText(url, referer);
            object o = Json.Parse(text);
            var d = o as Dictionary<string, object>;
            if (d == null) throw new BiliException(-1, "接口返回的不是 JSON 对象");
            return d;
        }

        /// Throws a friendly BiliException when the API reports a non-zero code.
        public static Dictionary<string, object> Ok(Dictionary<string, object> r, string what)
        {
            long code = Json.Num(r, -1, "code");
            if (code != 0)
            {
                string msg = Json.Str(r, "message");
                if (code == -352) msg = "触发 B 站风控（-352 风控校验失败），需要完整浏览器 Cookie";
                else if (code == -101) msg = "账号未登录";
                else if (code == -403)
                {
                    // -403 的含义是「没有观看权限」，不是签名算错。
                    msg = "没有观看权限（-403）：付费内容未购买 / 大会员专享 / 有权限或地域限制";
                    if (what != null && what.IndexOf("课程") >= 0)
                        msg += "。若这是付费课程，需要先购买该课程（或在有效期内）才能下载";
                }
                throw new BiliException((int)code, what + "失败: " + code + " " + msg);
            }
            return r;
        }

        // ------------------------------------------------------------------ wbi
        private static string Md5Hex(string s)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string MakeMixinKey(string imgKey, string subKey)
        {
            string raw = imgKey + subKey;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < MixinTab.Length; i++) sb.Append(raw[MixinTab[i]]);
            return sb.ToString().Substring(0, 32);
        }

        /// Sorts params by key, strips !'()* from values, appends wts and w_rid.
        public string WbiQuery(SortedDictionary<string, string> par)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in par)
            {
                if (sb.Length > 0) sb.Append('&');
                string v = kv.Value ?? "";
                v = v.Replace("!", "").Replace("'", "").Replace("(", "").Replace(")", "").Replace("*", "");
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(v));
            }
            long wts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            string signed = sb.ToString() + "&wts=" + wts.ToString(CultureInfo.InvariantCulture);
            return signed + "&w_rid=" + Md5Hex(signed + MixinKey);
        }

        // -------------------------------------------------------------- session
        public void AddCookie(string name, string value)
        {
            try { Cookies.Add(new Uri("https://www.bilibili.com"), new Cookie(name, value, "/", ".bilibili.com")); }
            catch { }
        }

        /// Enumerates the jar across every bilibili host we talk to.  .NET
        /// Framework has no CookieContainer.GetAllCookies(), and host-only
        /// cookies set by passport.* would be missed if we only asked www.*.
        public string DumpCookies()
        {
            var seen = new Dictionary<string, string>();
            string[] hosts = new string[] {
                "https://www.bilibili.com", "https://api.bilibili.com",
                "https://passport.bilibili.com", "https://space.bilibili.com",
                "https://account.bilibili.com", "https://api.live.bilibili.com"
            };
            foreach (string h in hosts)
            {
                try
                {
                    foreach (Cookie c in Cookies.GetCookies(new Uri(h)))
                        if (!seen.ContainsKey(c.Name)) seen[c.Name] = c.Value;
                }
                catch { }
            }
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in seen)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        public void LoadCookies(string text)
        {
            if (text == null) return;
            foreach (string part in text.Split(';'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                if (eq < 1) continue;
                AddCookie(p.Substring(0, eq).Trim(), p.Substring(eq + 1).Trim());
            }
        }

        /// Fetches WBI keys, device cookies, and the login state.
        public void Init()
        {
            Dictionary<string, object> nav = GetJson("https://api.bilibili.com/x/web-interface/nav", "https://www.bilibili.com/");
            object wbi = Json.Path(nav, "data", "wbi_img");
            string imgUrl = Json.Str(wbi, "img_url");
            string subUrl = Json.Str(wbi, "sub_url");
            if (imgUrl != null && subUrl != null)
                MixinKey = MakeMixinKey(Path.GetFileNameWithoutExtension(imgUrl), Path.GetFileNameWithoutExtension(subUrl));
            IsLogin = Json.Flag(nav, false, "data", "isLogin");
            if (IsLogin)
            {
                Mid = Json.Num(nav, 0, "data", "mid");
                UName = Json.Str(nav, "data", "uname") ?? "";
            }
            else { Mid = 0; UName = ""; }

            if (FindCookie("buvid3") == null)
            {
                try
                {
                    Dictionary<string, object> spi = GetJson("https://api.bilibili.com/x/frontend/finger/spi", "https://www.bilibili.com/");
                    string b3 = Json.Str(spi, "data", "b_3"), b4 = Json.Str(spi, "data", "b_4");
                    if (b3 != null) AddCookie("buvid3", b3);
                    if (b4 != null) AddCookie("buvid4", b4);
                    AddCookie("b_nut", ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString());
                }
                catch { }
            }
        }

        private string FindCookie(string name)
        {
            foreach (Cookie c in Cookies.GetCookies(new Uri("https://www.bilibili.com")))
                if (c.Name == name) return c.Value;
            return null;
        }

        // ------------------------------------------------------------ endpoints
        public Dictionary<string, object> VideoInfo(string id)
        {
            string q = id.StartsWith("av", StringComparison.OrdinalIgnoreCase)
                ? "aid=" + id.Substring(2) : "bvid=" + id;
            return Ok(GetJson("https://api.bilibili.com/x/web-interface/view?" + q, "https://www.bilibili.com/"), "获取视频信息");
        }

        public Dictionary<string, object> UserCard(long mid)
        {
            return Ok(GetJson("https://api.bilibili.com/x/web-interface/card?mid=" + mid, "https://space.bilibili.com/" + mid), "获取 UP 信息");
        }

        /// UP's 合集/系列. NOTE: page_size must be <= 20 or the API answers -400.
        public Dictionary<string, object> UpCollections(long mid)
        {
            var seasons = new List<object>();
            var series = new List<object>();
            for (int pn = 1; pn <= 50; pn++)
            {
                string url = "https://api.bilibili.com/x/polymer/web-space/seasons_series_list?mid=" + mid
                    + "&page_num=" + pn + "&page_size=20&web_location=333.1387";
                Dictionary<string, object> r;
                try { r = Ok(GetJson(url, "https://www.bilibili.com/"), "获取合集列表"); }
                catch (BiliException) { break; }
                List<object> sList = Json.Arr(r, "data", "items_lists", "seasons_list");
                List<object> rList = Json.Arr(r, "data", "items_lists", "series_list");
                for (int i = 0; i < sList.Count; i++)
                {
                    object meta = Json.Member(sList[i], "meta");
                    if (meta != null && Json.Num(meta, 0, "total") > 0) seasons.Add(sList[i]);
                }
                for (int i = 0; i < rList.Count; i++) if (Json.Member(rList[i], "meta") != null) series.Add(rList[i]);
                if (sList.Count < 20 && rList.Count < 20) break;
            }
            var res = new Dictionary<string, object>();
            res["seasons"] = seasons;
            res["series"] = series;
            return res;
        }

        public List<object> SeasonArchives(long mid, string seasonId)
        {
            var all = new List<object>();
            for (int pn = 1; pn <= 100; pn++)
            {
                string url = "https://api.bilibili.com/x/polymer/web-space/seasons_archives_list?mid=" + mid
                    + "&season_id=" + seasonId + "&page_num=" + pn + "&page_size=30&sort_reverse=false";
                Dictionary<string, object> r;
                try { r = Ok(GetJson(url, "https://www.bilibili.com/"), "获取合集内容"); }
                catch (BiliException) { break; }
                List<object> a = Json.Arr(r, "data", "archives");
                all.AddRange(a);
                if (a.Count < 30) break;
            }
            return all;
        }

        public List<object> SeriesArchives(long mid, string seriesId)
        {
            var all = new List<object>();
            for (int pn = 1; pn <= 100; pn++)
            {
                string url = "https://api.bilibili.com/x/series/archives?mid=" + mid + "&series_id=" + seriesId
                    + "&only_normal=true&sort=desc&pn=" + pn + "&ps=30";
                Dictionary<string, object> r;
                try { r = Ok(GetJson(url, "https://www.bilibili.com/"), "获取系列内容"); }
                catch (BiliException) { break; }
                List<object> a = Json.Arr(r, "data", "archives");
                all.AddRange(a);
                if (a.Count < 30) break;
            }
            return all;
        }

        /// UP's uploads. This is the one endpoint behind bilibili's risk control:
        /// without a full browser cookie jar it answers -352 (or the WAF answers 412).
        public List<object> SpaceVideos(long mid)
        {
            var all = new List<object>();
            if (MixinKey == null) return all;
            for (int pn = 1; pn <= 200; pn++)
            {
                var par = new SortedDictionary<string, string>();
                par["mid"] = mid.ToString();
                par["ps"] = "30";
                par["pn"] = pn.ToString();
                par["order"] = "pubdate";
                par["tid"] = "0";
                par["keyword"] = "";
                par["platform"] = "web";
                par["web_location"] = "1550101";
                Dictionary<string, object> r = Ok(GetJson("https://api.bilibili.com/x/space/wbi/arc/search?" + WbiQuery(par),
                    "https://space.bilibili.com/" + mid), "获取投稿列表");
                List<object> vl = Json.Arr(r, "data", "list", "vlist");
                all.AddRange(vl);
                if (vl.Count < 30) break;
            }
            return all;
        }

        public Dictionary<string, object> PlayUrl(string bvid, string cid, string aid)
        {
            var par = new SortedDictionary<string, string>();
            if (!string.IsNullOrEmpty(bvid)) par["bvid"] = bvid; else par["avid"] = aid;
            par["cid"] = cid;
            par["qn"] = "127";
            par["fnval"] = "4048";   // DASH + Dolby + 8K etc.
            par["fnver"] = "0";
            par["fourk"] = "1";
            par["platform"] = "pc";
            par["high_quality"] = "1";
            return Ok(GetJson("https://api.bilibili.com/x/player/wbi/playurl?" + WbiQuery(par),
                "https://www.bilibili.com/video/" + bvid), "获取播放地址");
        }

        public Dictionary<string, object> CheesePlayUrl(string aid, string cid, string epId)
        {
            var par = new SortedDictionary<string, string>();
            par["avid"] = aid; par["cid"] = cid; par["ep_id"] = epId;
            par["qn"] = "127"; par["fnval"] = "4048"; par["fnver"] = "0"; par["fourk"] = "1";
            par["platform"] = "pc"; par["high_quality"] = "1";
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in par)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(kv.Key).Append('=').Append(Uri.EscapeDataString(kv.Value));
            }
            return Ok(GetJson("https://api.bilibili.com/pugv/player/web/playurl?" + sb, "https://www.bilibili.com/cheese/"), "获取课程播放地址");
        }

        public Dictionary<string, object> CheeseSeason(string seasonId, string epId)
        {
            string q = !string.IsNullOrEmpty(seasonId) ? "season_id=" + seasonId : "ep_id=" + epId;
            return Ok(GetJson("https://api.bilibili.com/pugv/view/web/season?" + q, "https://www.bilibili.com/cheese/"), "获取课程信息");
        }

        // ----------------------------------------------------------------- login
        public Dictionary<string, object> QrGenerate()
        {
            return Ok(GetJson("https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header",
                "https://www.bilibili.com/"), "获取二维码");
        }

        /// data.code: 0 ok, 86038 expired, 86090 scanned-not-confirmed, 86101 not scanned.
        public Dictionary<string, object> QrPoll(string key)
        {
            return GetJson("https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key="
                + Uri.EscapeDataString(key) + "&source=main-fe-header", "https://www.bilibili.com/");
        }
    }
}
