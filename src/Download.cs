// Target resolution, the item list shown by "解析/fetch", quality selection and
// the actual download + ffmpeg merge.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace BiliGetter
{
    // ------------------------------------------------------------------ items
    public sealed class DownloadItem
    {
        public string Bvid = "";
        public string Aid = "";
        public string Cid = "";
        public string EpId = "";
        public bool Cheese;
        public string Title = "";        // shown and used as the file name
        public string Group = "";        // sub-folder under the output root
        public string Referer = "https://www.bilibili.com/";
        public string Duration = "";
        public bool Selected = true;
        public string QualityLabel = ""; // filled in by the quality probe / download
        public bool ChargingExclusive;   // view API: is_upower_exclusive (充电专属)
        public string Warn = "";         // pre-download note, shown in the 状态 column
    }

    internal enum TargetKind { Unknown, Video, Space, Season, Series, Cheese, Bangumi }

    internal sealed class Target
    {
        public TargetKind Kind = TargetKind.Unknown;
        public string Raw = "";
        public long Mid;
        public string Sid = "";
        public string Bvid = "";
        public string EpId = "";
        public string SeasonId = "";
    }

    // ------------------------------------------------------------------ helpers
    internal static class Util
    {
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "untitled";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool isBad = false;
                for (int j = 0; j < bad.Length; j++) if (bad[j] == c) { isBad = true; break; }
                if (c < 32) isBad = true;
                sb.Append(isBad ? '_' : c);
            }
            string r = sb.ToString().Trim().TrimEnd('.');
            if (r.Length > 80) r = r.Substring(0, 80);
            return r.Length == 0 ? "untitled" : r;
        }

        public static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

        /// Reads both pipes without deadlocking.  onStart gets the Process so the
        /// caller can kill a long-running child when the user hits Stop.
        public static int Run(string exe, string args, out string output, Action<Process> onStart)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // ffmpeg / aria2c 在 Windows 上都按 UTF-8 写日志。不显式指定的话，
            // .NET 会跟随控制台代码页去解码，中文路径就会变成「璇惧爞」这种乱码，
            // 报错信息也就没法看了。
            UTF8Encoding childEncoding = new UTF8Encoding(false);
            psi.StandardOutputEncoding = childEncoding;
            psi.StandardErrorEncoding = childEncoding;
            using (Process p = Process.Start(psi))
            {
                if (onStart != null) { try { onStart(p); } catch { } }
                StringBuilder err = new StringBuilder();
                Thread t = new Thread(delegate () { try { err.Append(p.StandardError.ReadToEnd()); } catch { } });
                t.Start();
                string outp = p.StandardOutput.ReadToEnd();
                t.Join();
                p.WaitForExit();
                output = (outp + "\n" + err).Trim();
                return p.ExitCode;
            }
        }

        public static string FormatDuration(long seconds)
        {
            if (seconds <= 0) return "";
            return string.Format("{0}:{1:00}:{2:00}", seconds / 3600, (seconds / 60) % 60, seconds % 60);
        }

        /// Explains *why* a video could not be played, using the view API's
        /// charging-exclusive flags.  Only called after a download already failed,
        /// so the extra request costs nothing in the normal path.
        public static string AccessHint(BiliSession s, DownloadItem it)
        {
            if (it.Cheese || string.IsNullOrEmpty(it.Bvid)) return "";
            try
            {
                object d = Json.Member(s.VideoInfo(it.Bvid), "data");
                bool exclusive = Json.Flag(d, false, "is_upower_exclusive");
                bool canPlay = Json.Flag(d, false, "is_upower_play");
                if (exclusive)
                {
                    return canPlay
                        ? "\r\n    → 这是「充电专属」视频。view 显示当前账号有权播放，但仍拿不到流；"
                          + "\r\n      请「退出登录」后重新扫码登录再试（可能是 Cookie 没带全或已失效）。"
                        : "\r\n    → 这是「充电专属」视频（is_upower_exclusive=true，is_upower_play=false）："
                          + "\r\n      当前登录账号还没有对这位 UP 充电，或者根本没有登录。"
                          + "\r\n      给该 UP 充电后，在充电有效期内本程序就能下载完整视频"
                          + "（用的是同一个接口、凭的是你账号的权限）。";
                }
                if (Json.Flag(d, false, "is_upower_preview"))
                    return "\r\n    → 这个视频对你目前只是预览状态（is_upower_preview=true）。";
            }
            catch { }
            return "";
        }
    }

    // --------------------------------------------------------------- qualities
    internal static class Quality
    {
        // Order = best first. 126 (Dolby) / 125 (HDR) are not simply "higher id".
        private static readonly int[] Ids = new int[] { 127, 126, 125, 120, 116, 112, 80, 74, 64, 32, 16, 6 };

        public static readonly string[] Names = new string[] {
            "最高可用", "1080P", "720P", "480P", "360P"
        };
        /// Maps the combo-box entry to the maximum quality id (0 = no cap).
        public static int CapForIndex(int i)
        {
            switch (i)
            {
                case 1: return 80;   // 1080P
                case 2: return 64;   // 720P
                case 3: return 32;   // 480P
                case 4: return 16;   // 360P
                default: return 0;   // no cap
            }
        }

        public static string Name(int id)
        {
            switch (id)
            {
                case 127: return "8K 超高清";
                case 126: return "杜比视界";
                case 125: return "HDR 真彩";
                case 120: return "4K 超清";
                case 116: return "1080P60";
                case 112: return "1080P+ 高码率";
                case 80: return "1080P 高清";
                case 74: return "720P60";
                case 64: return "720P 高清";
                case 32: return "480P 高清";
                case 16: return "360P 清晰";
                case 6: return "240P 流畅";
                default: return "id" + id;
            }
        }

        /// Descending list of acceptable ids for the chosen cap (0 = everything).
        public static List<int> Order(int cap)
        {
            var list = new List<int>();
            for (int i = 0; i < Ids.Length; i++) if (cap == 0 || Ids[i] <= cap) list.Add(Ids[i]);
            return list;
        }
    }

    internal sealed class PickedStreams
    {
        public string VideoUrl;
        public string AudioUrl;
        public int QualityId;
        public int Width, Height;
        public string VideoCodecs = "";
        public string AudioCodecs = "";
        public long VideoBandwidth;
        public string Label { get { return Quality.Name(this.QualityId) + " " + Width + "x" + Height + " " + VideoCodecs; } }
    }

    // ------------------------------------------------------------------ target
    internal static class TargetResolver
    {
        public static Target Resolve(string input)
        {
            var t = new Target();
            string s = (input ?? "").Trim();
            t.Raw = s;
            if (s.Length == 0) return t;

            Match m;
            m = Regex.Match(s, @"^mid:(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success) { t.Kind = TargetKind.Space; t.Mid = long.Parse(m.Groups[1].Value); return t; }
            m = Regex.Match(s, @"^BV[0-9A-Za-z]{10}$");
            if (m.Success) { t.Kind = TargetKind.Video; t.Bvid = s; return t; }
            m = Regex.Match(s, @"^av(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success) { t.Kind = TargetKind.Video; t.Bvid = s.ToLowerInvariant(); return t; }
            m = Regex.Match(s, @"^\d{3,}$");
            if (m.Success) { t.Kind = TargetKind.Space; t.Mid = long.Parse(s); return t; }

            bool hasSpaceHost = Regex.IsMatch(s, @"space\.bilibili\.com/(\d+)", RegexOptions.IgnoreCase);
            Match midM = Regex.Match(s, @"space\.bilibili\.com/(\d+)", RegexOptions.IgnoreCase);
            if (midM.Success) t.Mid = long.Parse(midM.Groups[1].Value);

            m = Regex.Match(s, @"space\.bilibili\.com/\d+/channel/collectiondetail\?[^#]*\bsid=(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { t.Kind = TargetKind.Season; t.Sid = m.Groups[1].Value; return t; }
            m = Regex.Match(s, @"space\.bilibili\.com/\d+/channel/seriesdetail\?[^#]*\bsid=(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { t.Kind = TargetKind.Series; t.Sid = m.Groups[1].Value; return t; }
            m = Regex.Match(s, @"bilibili\.com/cheese/play/(ss|ep)(\d+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                t.Kind = TargetKind.Cheese;
                if (m.Groups[1].Value.ToLowerInvariant() == "ss") t.SeasonId = m.Groups[2].Value;
                else t.EpId = m.Groups[2].Value;
                return t;
            }
            m = Regex.Match(s, @"bilibili\.com/video/(BV[0-9A-Za-z]{10}|av\d+)", RegexOptions.IgnoreCase);
            if (m.Success) { t.Kind = TargetKind.Video; t.Bvid = m.Groups[1].Value; return t; }
            m = Regex.Match(s, @"bilibili\.com/bangumi/play/(ep|ss)\d+", RegexOptions.IgnoreCase);
            if (m.Success) { t.Kind = TargetKind.Bangumi; return t; }
            if (hasSpaceHost) { t.Kind = TargetKind.Space; return t; }
            return t;
        }
    }

    // ------------------------------------------------------------ stream picker
    internal static class StreamPicker
    {
        public static PickedStreams Pick(Dictionary<string, object> playData, int cap)
        {
            object dashObj = Json.Member(playData, "dash");
            var dash = dashObj as Dictionary<string, object>;
            if (dash == null)
            {
                // B 站的一个坑：没有权限时接口不报错，而是把 DASH 换成旧的 durl 格式，
                // 同时 code 仍然是 0、message 仍然是 OK。必须自己识别，
                // 否则天真的下载器会「成功」保存一个预览片段或空文件。
                bool downgradedToDurl = Json.Member(playData, "durl") != null;
                throw new BiliException(-1, downgradedToDurl
                    ? "接口没有给出 DASH 流（只给了降级的 durl 格式）。这通常表示当前账号对这个视频没有观看权限："
                      + "充电专属视频未充电、大会员专享内容未开通会员、或没有登录。"
                    : "接口没有给出 DASH 流。");
            }

            List<object> videos = Json.Arr(dash, "video");
            if (videos.Count == 0) throw new BiliException(-1, "DASH 流列表为空（视频可能受限或需要登录）");

            List<int> order = Quality.Order(cap);
            Dictionary<string, object> best = null;
            int bestQuality = 0;
            for (int qi = 0; qi < order.Count && best == null; qi++)
            {
                int want = order[qi];
                var cand = new List<Dictionary<string, object>>();
                for (int i = 0; i < videos.Count; i++)
                {
                    var v = videos[i] as Dictionary<string, object>;
                    if (v == null) continue;
                    if ((int)Json.Num(v, 0, "id") == want) cand.Add(v);
                }
                if (cand.Count == 0) continue;
                // codec preference: avc, hevc, av1
                string[] pref = new string[] { "avc", "hev", "av0" };
                for (int pi = 0; pi < pref.Length && best == null; pi++)
                {
                    var cc = new List<Dictionary<string, object>>();
                    for (int i = 0; i < cand.Count; i++)
                    {
                        string codecs = Json.Str(cand[i], "codecs") ?? "";
                        if (codecs.StartsWith(pref[pi], StringComparison.OrdinalIgnoreCase)) cc.Add(cand[i]);
                    }
                    if (cc.Count > 0) best = MaxBandwidth(cc);
                }
                if (best == null) best = MaxBandwidth(cand);
                if (best != null) bestQuality = want;
            }
            if (best == null) throw new BiliException(-1, "没有匹配清晰度的视频流");

            var res = new PickedStreams();
            res.VideoUrl = Json.Str(best, "baseUrl");
            if (string.IsNullOrEmpty(res.VideoUrl)) res.VideoUrl = Json.Str(best, "base_url");
            res.QualityId = (int)Json.Num(best, bestQuality, "id");
            res.Width = (int)Json.Num(best, 0, "width");
            res.Height = (int)Json.Num(best, 0, "height");
            res.VideoCodecs = Json.Str(best, "codecs") ?? "";
            res.VideoBandwidth = Json.Num(best, 0, "bandwidth");

            // Audio: FLAC > Dolby > 30280 > 30232 > 30216
            Dictionary<string, object> aud = null; string audCodecs = "";
            var flac = Json.Path(dash, "flac", "audio") as Dictionary<string, object>;
            if (flac != null) { aud = flac; audCodecs = Json.Str(flac, "codecs") ?? "flac"; }
            if (aud == null)
            {
                var dolbyList = Json.Arr(dash, "dolby", "audio");
                if (dolbyList.Count > 0) { aud = dolbyList[0] as Dictionary<string, object>; if (aud != null) audCodecs = Json.Str(aud, "codecs") ?? "ec-3"; }
            }
            if (aud == null)
            {
                List<object> auds = Json.Arr(dash, "audio");
                int[] prefIds = new int[] { 30280, 30232, 30216 };
                for (int i = 0; i < prefIds.Length && aud == null; i++)
                {
                    var cand = new List<Dictionary<string, object>>();
                    for (int j = 0; j < auds.Count; j++)
                    {
                        var a = auds[j] as Dictionary<string, object>;
                        if (a == null) continue;
                        if ((int)Json.Num(a, 0, "id") == prefIds[i]) cand.Add(a);
                    }
                    if (cand.Count > 0) aud = MaxBandwidth(cand);
                }
                if (aud == null && auds.Count > 0) aud = MaxBandwidth(ToDicts(auds));
                if (aud != null) audCodecs = Json.Str(aud, "codecs") ?? "";
            }
            if (aud != null)
            {
                res.AudioUrl = Json.Str(aud, "baseUrl");
                if (string.IsNullOrEmpty(res.AudioUrl)) res.AudioUrl = Json.Str(aud, "base_url");
                res.AudioCodecs = audCodecs;
            }
            return res;
        }

        private static List<Dictionary<string, object>> ToDicts(List<object> l)
        {
            var r = new List<Dictionary<string, object>>();
            for (int i = 0; i < l.Count; i++) { var d = l[i] as Dictionary<string, object>; if (d != null) r.Add(d); }
            return r;
        }

        private static Dictionary<string, object> MaxBandwidth(List<Dictionary<string, object>> l)
        {
            Dictionary<string, object> best = null; long bw = -1;
            for (int i = 0; i < l.Count; i++)
            {
                long b = Json.Num(l[i], 0, "bandwidth");
                if (b > bw) { bw = b; best = l[i]; }
            }
            return best;
        }
    }
}
