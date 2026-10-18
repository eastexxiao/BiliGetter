// Fetcher: turns a target URL into the list of items shown by "解析".
// Downloader: pulls the DASH streams and merges them with ffmpeg.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace BiliGetter
{
    internal sealed class Fetcher
    {
        private readonly BiliSession _s;
        public Fetcher(BiliSession s) { _s = s; }

        public List<DownloadItem> Build(Target t, Action<string> log)
        {
            switch (t.Kind)
            {
                case TargetKind.Video: return BuildVideo(t, log);
                case TargetKind.Space: return BuildSpace(t, log);
                case TargetKind.Season: return BuildSeason(t, log);
                case TargetKind.Series: return BuildSeries(t, log);
                case TargetKind.Cheese: return BuildCheese(t, log);
                case TargetKind.Bangumi:
                    throw new BiliException(-1, "番剧/影视（pgc）没有实现，请用自带的 Source\\BBDown.exe");
                default:
                    throw new BiliException(-1, "无法识别的地址。支持：BV号 / 视频链接 / UP主页 / 合集链接 / 系列链接 / 课堂链接");
            }
        }

        // ------------------------------------------------------------- one video
        private List<DownloadItem> BuildVideo(Target t, Action<string> log)
        {
            var items = new List<DownloadItem>();
            Dictionary<string, object> info = _s.VideoInfo(t.Bvid);
            object data = Json.Member(info, "data");
            string bvid = Json.Str(data, "bvid");
            string title = Json.Str(data, "title");
            string upName = Json.Str(data, "owner", "name") ?? ("UP" + Json.Num(data, 0, "owner", "mid"));
            string seasonTitle = Json.Str(data, "ugc_season", "title");
            long duration = Json.Num(data, 0, "duration");
            // 充电专属：view 用 is_upower_exclusive 标记视频本身，用 is_upower_play
            // 告诉当前账号到底有没有权限（未充电时为 false）。
            bool upower = Json.Flag(data, false, "is_upower_exclusive");
            bool upowerPlay = Json.Flag(data, false, "is_upower_play");
            if (upower && log != null)
                log("注意：这是「充电专属」视频，当前账号" + (upowerPlay ? "有" : "没有")
                    + "观看权限（is_upower_play=" + (upowerPlay ? "true" : "false") + "）");

            string group = Util.Sanitize(upName);
            if (!string.IsNullOrEmpty(seasonTitle)) group = Path.Combine(group, Util.Sanitize(seasonTitle));

            List<object> pages = Json.Arr(data, "pages");
            bool multi = pages.Count > 1;
            if (log != null) log("视频：" + title + "   UP：" + upName + "   分P：" + pages.Count);

            for (int i = 0; i < pages.Count; i++)
            {
                var it = new DownloadItem();
                it.Bvid = bvid;
                it.Cid = Json.Str(pages[i], "cid");
                it.Aid = Json.Str(data, "aid");
                it.Referer = "https://www.bilibili.com/video/" + bvid;
                it.Group = group;
                it.Duration = Util.FormatDuration(Json.Num(pages[i], duration, "duration"));
                if (upower)
                {
                    it.ChargingExclusive = true;
                    it.Warn = upowerPlay ? "充电专属(有权)" : "充电专属(未充电)";
                }
                if (multi)
                {
                    long pageNo = Json.Num(pages[i], i + 1, "page");
                    it.Title = "[P" + pageNo.ToString("00") + "]" + Util.Sanitize(Json.Str(pages[i], "part"));
                }
                else it.Title = Util.Sanitize(title);
                items.Add(it);
            }
            return items;
        }

        // ------------------------------------------------------------- UP space
        private List<DownloadItem> BuildSpace(Target t, Action<string> log)
        {
            var items = new List<DownloadItem>();
            string upName;
            try { upName = Json.Str(_s.UserCard(t.Mid), "data", "card", "name") ?? ("UP" + t.Mid); }
            catch (Exception) { upName = "UP" + t.Mid; }
            upName = Util.Sanitize(upName);
            if (log != null) log("UP 主：" + upName + " (mid=" + t.Mid + ")");

            var seen = new Dictionary<string, bool>();

            Dictionary<string, object> cols = _s.UpCollections(t.Mid);
            List<object> seasons = Json.Arr(cols, "seasons");
            List<object> series = Json.Arr(cols, "series");
            if (log != null) log("发现 合集 " + seasons.Count + " 个 / 系列 " + series.Count + " 个");

            for (int i = 0; i < seasons.Count; i++)
            {
                object meta = Json.Member(seasons[i], "meta");
                string sid = Json.Str(meta, "season_id");
                string name = Json.Str(meta, "name") ?? ("合集" + sid);
                if (log != null) log("合集「" + name + "」...");
                AddArchives(items, seen, t.Mid, sid, name, upName, true, log);
            }
            for (int i = 0; i < series.Count; i++)
            {
                object meta = Json.Member(series[i], "meta");
                string sid = Json.Str(meta, "series_id");
                string name = Json.Str(meta, "name") ?? ("系列" + sid);
                if (log != null) log("系列「" + name + "」...");
                AddArchives(items, seen, t.Mid, sid, name, upName, false, log);
            }

            // Loose uploads.  Needs a full browser cookie jar; -352 is expected
            // otherwise and must not abort the whole fetch.
            try
            {
                List<object> vids = _s.SpaceVideos(t.Mid);
                int added = 0;
                for (int i = 0; i < vids.Count; i++)
                {
                    string bvid = Json.Str(vids[i], "bvid");
                    if (bvid == null || seen.ContainsKey(bvid)) continue;
                    seen[bvid] = true;
                    var it = new DownloadItem();
                    it.Bvid = bvid;
                    it.Aid = Json.Str(vids[i], "aid");
                    it.Title = Util.Sanitize(Json.Str(vids[i], "title"));
                    it.Group = upName;
                    it.Referer = "https://www.bilibili.com/video/" + bvid;
                    items.Add(it);
                    added++;
                }
                if (log != null) log("散装投稿 " + added + " 个");
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log("拿不到散装投稿：" + ex.Message);
                    log("→ 合集和系列已经全部列出来了（通常已覆盖绝大多数内容）。");
                    log("→ 想要散装投稿，请在浏览器登录 B 站后，用开发者工具复制完整 Cookie，");
                    log("   保存到本程序目录的 bili-cookies.txt（一行 name=value; name=value; ...），再重新解析。");
                }
            }
            return items;
        }

        private void AddArchives(List<DownloadItem> items, Dictionary<string, bool> seen, long mid,
            string sid, string name, string upName, bool isSeason, Action<string> log)
        {
            List<object> arr;
            try { arr = isSeason ? _s.SeasonArchives(mid, sid) : _s.SeriesArchives(mid, sid); }
            catch (Exception ex) { if (log != null) log("  跳过「" + name + "」：" + ex.Message); return; }
            string group = Path.Combine(upName, Util.Sanitize(name));
            for (int i = 0; i < arr.Count; i++)
            {
                string bvid = Json.Str(arr[i], "bvid");
                if (bvid == null) continue;
                if (seen.ContainsKey(bvid)) continue;
                seen[bvid] = true;
                var it = new DownloadItem();
                it.Bvid = bvid;
                it.Aid = Json.Str(arr[i], "aid");
                it.Title = "[P" + (i + 1).ToString("000") + "]" + Util.Sanitize(Json.Str(arr[i], "title"));
                it.Group = group;
                it.Referer = "https://www.bilibili.com/video/" + bvid;
                items.Add(it);
            }
            if (log != null) log("  → " + arr.Count + " 个视频");
        }

        // --------------------------------------------------------------- season
        private List<DownloadItem> BuildSeason(Target t, Action<string> log)
        {
            var items = new List<DownloadItem>();
            string upName = "UP" + t.Mid;
            try { upName = Json.Str(_s.UserCard(t.Mid), "data", "card", "name") ?? upName; } catch { }
            string name = "collection_" + t.Sid;
            try
            {
                Dictionary<string, object> cols = _s.UpCollections(t.Mid);
                List<object> seasons = Json.Arr(cols, "seasons");
                for (int i = 0; i < seasons.Count; i++)
                {
                    object meta = Json.Member(seasons[i], "meta");
                    if (Json.Str(meta, "season_id") == t.Sid) { name = Json.Str(meta, "name") ?? name; break; }
                }
            }
            catch { }
            var seen = new Dictionary<string, bool>();
            AddArchives(items, seen, t.Mid, t.Sid, name, Util.Sanitize(upName), true, log);
            return items;
        }

        private List<DownloadItem> BuildSeries(Target t, Action<string> log)
        {
            var items = new List<DownloadItem>();
            string upName = "UP" + t.Mid;
            try { upName = Json.Str(_s.UserCard(t.Mid), "data", "card", "name") ?? upName; } catch { }
            string name = "series_" + t.Sid;
            try
            {
                Dictionary<string, object> cols = _s.UpCollections(t.Mid);
                List<object> series = Json.Arr(cols, "series");
                for (int i = 0; i < series.Count; i++)
                {
                    object meta = Json.Member(series[i], "meta");
                    if (Json.Str(meta, "series_id") == t.Sid) { name = Json.Str(meta, "name") ?? name; break; }
                }
            }
            catch { }
            var seen = new Dictionary<string, bool>();
            AddArchives(items, seen, t.Mid, t.Sid, name, Util.Sanitize(upName), false, log);
            return items;
        }

        // --------------------------------------------------------------- cheese
        private List<DownloadItem> BuildCheese(Target t, Action<string> log)
        {
            var items = new List<DownloadItem>();
            Dictionary<string, object> r = _s.CheeseSeason(t.SeasonId, t.EpId);
            object data = Json.Member(r, "data");
            string title = Json.Str(data, "title") ?? "课程";
            if (log != null) log("课程「" + title + "」");

            string group = Path.Combine("课堂", Util.Sanitize(title));
            List<object> eps = Json.Arr(data, "episodes");
            // The API does not return episodes in order.
            var ordered = new List<KeyValuePair<long, object>>();
            for (int i = 0; i < eps.Count; i++) ordered.Add(new KeyValuePair<long, object>(Json.Num(eps[i], i + 1, "index"), eps[i]));
            ordered.Sort(delegate (KeyValuePair<long, object> a, KeyValuePair<long, object> b) { return a.Key.CompareTo(b.Key); });

            for (int i = 0; i < ordered.Count; i++)
            {
                object e = ordered[i].Value;
                string aid = Json.Str(e, "aid");
                if (string.IsNullOrEmpty(aid) || aid == "0")
                {
                    if (log != null) log("  跳过无视频源的章节：" + Json.Str(e, "title"));
                    continue;
                }
                var it = new DownloadItem();
                it.Cheese = true;
                it.Aid = aid;
                it.Cid = Json.Str(e, "cid");
                it.EpId = Json.Str(e, "id");
                it.Title = "[P" + ordered[i].Key.ToString("00") + "]" + Util.Sanitize(Json.Str(e, "title"));
                it.Group = group;
                it.Referer = "https://www.bilibili.com/cheese/";
                it.Duration = Util.FormatDuration(Json.Num(e, 0, "duration"));
                items.Add(it);
            }
            return items;
        }
    }

    // ----------------------------------------------------------------- settings
    internal sealed class DownloadSettings
    {
        public string OutDir = "";
        public string FfmpegPath = "";
        public string Aria2cPath = "";
        public bool UseAria2c;
        public int QualityCap;
        public string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    }

    // --------------------------------------------------------------- downloader
    internal sealed class Downloader
    {
        public DownloadSettings Settings = new DownloadSettings();
        public Action<string> Log;
        public volatile bool Cancelled;
        private Process _current;

        private void Note(string s) { if (Log != null) Log(s); }

        public void Cancel()
        {
            Cancelled = true;
            try { Process p = _current; if (p != null && !p.HasExited) p.Kill(); } catch { }
        }

        /// Returns true when the file is present (downloaded or already there).
        public bool DownloadOne(BiliSession s, DownloadItem it, Action<int> pct)
        {
            if (Cancelled) return false;

            if (string.IsNullOrEmpty(it.Cid))
            {
                Dictionary<string, object> info = s.VideoInfo(it.Bvid);
                it.Cid = Json.Str(info, "data", "cid");
            }
            if (string.IsNullOrEmpty(it.Cid))
            {
                Note("跳过（拿不到 cid）：" + it.Title);
                return false;
            }

            PickedStreams picked;
            try
            {
                Dictionary<string, object> play = it.Cheese
                    ? s.CheesePlayUrl(it.Aid, it.Cid, it.EpId)
                    : s.PlayUrl(it.Bvid, it.Cid, it.Aid);
                object dataObj = Json.Member(play, "data");
                var data = dataObj as Dictionary<string, object>;
                if (data == null) throw new BiliException(-1, "播放地址为空");
                picked = StreamPicker.Pick(data, Settings.QualityCap);
            }
            catch (Exception ex)
            {
                // On failure only, spend one more call to find out *why* and say so
                // concretely (charging-exclusive is the common cause).
                throw new Exception(ex.Message + Util.AccessHint(s, it));
            }
            it.QualityLabel = picked.Label;
            if (Cancelled) return false;

            string dir = Path.Combine(Settings.OutDir, it.Group);
            Directory.CreateDirectory(dir);
            string outFile = Path.Combine(dir, it.Title + ".mp4");
            if (File.Exists(outFile) && new FileInfo(outFile).Length > 0)
            {
                Note("已存在，跳过：" + it.Title);
                return true;
            }

            // Temp files live next to the output: guaranteed writable (we write
            // the .mp4 there anyway), same volume, and no dependency on %TEMP%.
            string tmpRoot = Path.Combine(Settings.OutDir, ".biliget-tmp");
            string tmp = Path.Combine(tmpRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string vf = Path.Combine(tmp, "v.m4s");
                string af = Path.Combine(tmp, "a.m4s");
                Note("  " + it.Title + "  [" + picked.Label + "]");
                FetchTo(picked.VideoUrl, vf, it.Referer, pct);
                bool hasAudio = !string.IsNullOrEmpty(picked.AudioUrl);
                if (hasAudio) FetchTo(picked.AudioUrl, af, it.Referer, null);
                Merge(vf, hasAudio ? af : null, outFile);
                double mb = new FileInfo(outFile).Length / 1048576.0;
                Note("  ✓ 完成 " + it.Title + ".mp4  (" + mb.ToString("0.00") + " MB)");
                return true;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
                try
                {
                    if (Directory.Exists(tmpRoot) && Directory.GetFileSystemEntries(tmpRoot).Length == 0)
                        Directory.Delete(tmpRoot);
                }
                catch { }
            }
        }

        private void FetchTo(string url, string dest, string referer, Action<int> pct)
        {
            if (Cancelled) return;
            if (Settings.UseAria2c && !string.IsNullOrEmpty(Settings.Aria2cPath))
            {
                string dir = Path.GetDirectoryName(dest);
                string name = Path.GetFileName(dest);
                string args = "-x16 -s16 -j16 -k 5M --allow-overwrite=true --auto-file-renaming=false"
                    + " --console-log-level=warn --summary-interval=0"
                    + " --header=" + Util.Quote("Referer: " + referer)
                    + " --header=" + Util.Quote("User-Agent: " + Settings.UserAgent)
                    + " -d " + Util.Quote(dir) + " -o " + Util.Quote(name) + " " + Util.Quote(url);
                string outp;
                int code = Util.Run(Settings.Aria2cPath, args, out outp, delegate (Process p) { _current = p; });
                _current = null;
                if (code != 0 || !File.Exists(dest))
                    throw new Exception("aria2c 下载失败 (exit " + code + ")" + (outp.Length > 0 ? ": " + Tail(outp) : ""));
                if (pct != null) pct(100);
                return;
            }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = Settings.UserAgent;
            req.Referer = referer;
            req.Headers["Origin"] = "https://www.bilibili.com";
            req.Timeout = 60000;
            req.ReadWriteTimeout = 120000;
            req.KeepAlive = false;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream st = resp.GetResponseStream())
            using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 131072))
            {
                byte[] buf = new byte[131072];
                long total = resp.ContentLength;
                long done = 0;
                int n;
                while ((n = st.Read(buf, 0, buf.Length)) > 0)
                {
                    if (Cancelled) return;
                    fs.Write(buf, 0, n);
                    done += n;
                    if (pct != null && total > 0) pct((int)(done * 100 / total));
                }
            }
            if (pct != null) pct(100);
        }

        private void Merge(string videoFile, string audioFile, string outFile)
        {
            if (string.IsNullOrEmpty(Settings.FfmpegPath))
                throw new Exception("找不到 ffmpeg，无法合并音视频");
            string args = "-hide_banner -loglevel error -y -i " + Util.Quote(videoFile);
            if (audioFile != null) args += " -i " + Util.Quote(audioFile);
            args += " -c copy -movflags +faststart " + Util.Quote(outFile);
            string outp;
            int code = Util.Run(Settings.FfmpegPath, args, out outp, delegate (Process p) { _current = p; });
            _current = null;
            if (code != 0 || !File.Exists(outFile))
                throw new Exception("ffmpeg 合并失败 (exit " + code + ")" + (outp.Length > 0 ? ": " + Tail(outp) : ""));
        }

        private static string Tail(string s)
        {
            s = s.Trim();
            return s.Length <= 400 ? s : s.Substring(s.Length - 400);
        }
    }
}
