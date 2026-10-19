// Fetcher: turns a target URL into the list of items shown by "解析".
// Downloader: pulls the DASH streams and merges them with ffmpeg.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
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

        /// <summary>实时传输进度。回调发生在后台线程，调用方自己切到 UI 线程。</summary>
        public Action<TransferProgress> Transfer;

        private void Report(string stage, long got, long total, double speed)
        {
            Action<TransferProgress> cb = Transfer;
            if (cb == null) return;
            TransferProgress p = new TransferProgress();
            p.Stage = stage;
            p.Received = got;
            p.Total = total;
            p.BytesPerSecond = speed;
            try { cb(p); } catch { }
        }
        public volatile bool Cancelled;
        private Process _current;

        private void Note(string s) { if (Log != null) Log(s); }

        public void Cancel()
        {
            Cancelled = true;
            try { Process p = _current; if (p != null && !p.HasExited) p.Kill(); } catch { }
        }

        /// Returns true when the file is present (downloaded or already there).
        /// <summary>
        /// 下载一项。失败时重新取一次播放地址再试一遍 —— B 站 CDN 的镜像地址
        /// 偶尔直接 404（实测同一个视频 3 次里有 1 次），重取就能拿到可用地址。
        /// </summary>
        public bool DownloadOne(BiliSession s, DownloadItem it, Action<int> pct)
        {
            try
            {
                return DownloadOneAttempt(s, it, pct);
            }
            catch (DrmProtectedException)
            {
                // DRM 加密：换地址、重试都不会变。直接上抛，由批处理统一停下。
                throw;
            }
            catch (Exception)
            {
                if (Cancelled) throw;
                Note("    本次失败，重新获取播放地址后重试一次…");
                return DownloadOneAttempt(s, it, pct);
            }
        }

        private bool DownloadOneAttempt(BiliSession s, DownloadItem it, Action<int> pct)
        {
            if (Cancelled) return false;

            // 输出目录能不能写文件，要在花掉网络请求之前就说清楚。
            // （受控文件夹访问 / 杀软 / 权限这类问题会一直存在，重试也没用。）
            EnsureOutDirWritable();

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
            bool merged = false;
            bool drmHit = false;
            try
            {
                string vf = Path.Combine(tmp, "v.m4s");
                string af = Path.Combine(tmp, "a.m4s");
                Note("  " + it.Title + "  [" + picked.Label + "]");

                // 先下音频（通常几 MB）再下视频（4K 可能上百 MB）。
                // 顺序有讲究：如果是 DRM 加密课程，下完音频就能识别出来，
                // 不必先白下 117 MB 的 4K 视频再失败。
                bool hasAudio = !string.IsNullOrEmpty(picked.AudioUrl);
                if (hasAudio)
                {
                    FetchTo(picked.AudioUrl, af, it.Referer, "音频");
                    if (EncryptedMedia.IsEncrypted(af))
                    {
                        drmHit = true;
                        throw new DrmProtectedException(
                            "音频分片是 CENC 加密的（enca + pssh + senc），ffmpeg 没有解密密钥打不开");
                    }
                }

                FetchTo(picked.VideoUrl, vf, it.Referer, "视频");
                if (EncryptedMedia.IsEncrypted(vf))
                {
                    drmHit = true;
                    throw new DrmProtectedException(
                        "视频分片是 CENC 加密的（encv + pssh + senc），ffmpeg 没有解密密钥打不开");
                }

                // 分片没落盘就别再叫 ffmpeg 了：那只会得到 "No such file or directory"，
                // 把真正的原因（下载阶段就已经失败）盖掉。
                if (!File.Exists(vf) || new FileInfo(vf).Length == 0)
                    throw new Exception("视频分片没有下载成功（临时文件不存在或为空），"
                        + "失败发生在下载阶段，详见上面的报错");
                if (hasAudio && (!File.Exists(af) || new FileInfo(af).Length == 0))
                    throw new Exception("音频分片没有下载成功（临时文件不存在或为空），"
                        + "失败发生在下载阶段，详见上面的报错");

                // 合并（尤其 4K 大文件）要好几秒，进度条不能干等着不动
                Report("合并音视频", -1, -1, 0);
                Merge(vf, hasAudio ? af : null, outFile);
                merged = true;
                double mb = new FileInfo(outFile).Length / 1048576.0;
                Note("  ✓ 完成 " + it.Title + ".mp4  (" + mb.ToString("0.00") + " MB)");
                if (pct != null) pct(100);
                return true;
            }
            finally
            {
                // 只有合并成功才清理临时目录。失败时里面是已经下好的 v.m4s / a.m4s
                // （4K 单个分片就上百 MB），删掉的话整段都得重新下载。
                // 例外是 DRM：分片本来就是解不开的密文，留着只是垃圾。
                if (merged || drmHit)
                {
                    try { Directory.Delete(tmp, true); } catch { }
                    try
                    {
                        if (Directory.Exists(tmpRoot) && Directory.GetFileSystemEntries(tmpRoot).Length == 0)
                            Directory.Delete(tmpRoot);
                    }
                    catch { }
                }
                else if (Directory.Exists(tmp))
                {
                    Note("    （临时分片已保留，便于排查问题：" + tmp + "）");
                }
            }
        }

        /// <summary>
        /// 开下之前先确认输出目录真的能「写文件」。
        ///
        /// 有些机器上存在这种状态：能创建子目录，但创建文件被拒（Win32 错误 5
        /// Access is denied）——典型来源是 Windows Defender 的「受控文件夹访问」
        /// （勒索软件防护）、第三方杀毒的文件防护、或者该目录的权限不含写文件。
        /// 这种问题要下到一半才炸，报出来的又是 ffmpeg 的 "Permission denied"，
        /// 极难判断。先探一次，直接把结论和解决办法说清楚。
        /// </summary>
        private void EnsureOutDirWritable()
        {
            try
            {
                Directory.CreateDirectory(Settings.OutDir);
            }
            catch (Exception ex)
            {
                throw new Exception("无法创建输出目录：" + Settings.OutDir + "\r\n    " + ex.Message);
            }

            string probe = Path.Combine(Settings.OutDir, ".biliget-write-test.tmp");
            try
            {
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new Exception("输出目录无法写入文件：" + Settings.OutDir
                    + "\r\n    " + ex.Message
                    + "\r\n    这不是下载或合并的问题，是系统不让在这个目录里建文件。常见原因："
                    + "\r\n      · Windows Defender「受控文件夹访问」（勒索软件防护）拦截了这个程序"
                    + "\r\n      · 第三方杀毒软件的文件防护"
                    + "\r\n      · 该文件夹的权限不允许当前用户写文件"
                    + "\r\n    办法：换一个输出目录（例如自己新建 D:\\bili-dl），"
                    + "\r\n    或在「受控文件夹访问」里把本程序加入允许列表。");
            }
        }

        private void FetchTo(string url, string dest, string referer, string stage)
        {
            if (Cancelled) return;

            if (Settings.UseAria2c && !string.IsNullOrEmpty(Settings.Aria2cPath))
            {
                try
                {
                    FetchWithAria2c(url, dest, referer, stage);
                    return;
                }
                catch (Exception ex)
                {
                    if (Cancelled) return;
                    // aria2c 失败（CDN 403、连接数被限、网络抖动等）不让整个任务失败：
                    // 换内置下载器再试一次，它只用一个连接、走的请求头和界面版完全一致。
                    Note("    aria2c 失败，改用内置下载器重试：" + ex.Message);
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                }
            }

            FetchWithHttp(url, dest, referer, stage);
        }

        /// <summary>
        /// 用 aria2c 多线程下载；失败时抛异常，由 FetchTo 决定是否回退。
        ///
        /// 进度不解析 aria2c 的控制台输出：实测它在标准输出被重定向时
        /// 根本不打印摘要行，等拿到就已经下载完了。改为盯输出文件的体积
        /// （因此必须 --file-allocation=none，否则 aria2c 会预分配空间，
        /// 文件体积一上来就是满的，进度看着永远是 100%）。
        /// 总长度用一次 Range: bytes=0-0 的请求拿到，进度条才能显示百分比。
        /// </summary>
        private void FetchWithAria2c(string url, string dest, string referer, string stage)
        {
            string dir = Path.GetDirectoryName(dest);
            string name = Path.GetFileName(dest);
            long total = ProbeLength(url, referer);

            string args = "-x16 -s16 -j16 -k 5M --allow-overwrite=true --auto-file-renaming=false"
                + " --file-allocation=none"
                + " --max-tries=3 --retry-wait=2 --timeout=30 --connect-timeout=15"
                + " --console-log-level=warn --summary-interval=0"
                + " --header=" + Util.Quote("Referer: " + referer)
                + " --header=" + Util.Quote("Origin: https://www.bilibili.com")
                + " --header=" + Util.Quote("User-Agent: " + Settings.UserAgent)
                + " -d " + Util.Quote(dir) + " -o " + Util.Quote(name) + " " + Util.Quote(url);

            _ariaStop = false;
            double lastSpeed = 0;
            System.Threading.Thread monitor = new System.Threading.Thread(delegate ()
            {
                long lastSize = 0;
                long lastMs = Environment.TickCount;
                while (!_ariaStop)
                {
                    System.Threading.Thread.Sleep(300);
                    if (_ariaStop) break;

                    long size = 0;
                    try { if (File.Exists(dest)) size = new FileInfo(dest).Length; } catch { }
                    long now = Environment.TickCount;
                    long dt = Math.Max(1, now - lastMs);
                    if (size > lastSize)
                    {
                        double inst = (size - lastSize) * 1000.0 / dt;
                        lastSpeed = lastSpeed <= 0 ? inst : lastSpeed * 0.55 + inst * 0.45;
                        lastSize = size;
                        lastMs = now;
                    }
                    Report(stage, size, total, lastSpeed);
                }
            });
            monitor.IsBackground = true;
            monitor.Start();

            string outp;
            int code;
            try
            {
                code = Util.Run(Settings.Aria2cPath, args, out outp, delegate (Process p) { _current = p; });
            }
            finally
            {
                _current = null;
                _ariaStop = true;
            }

            if (code != 0 || !File.Exists(dest))
                throw new Exception("exit " + code + (outp.Length > 0 ? "，" + Aria2cReason(outp) : ""));

            long finalSize = 0;
            try { finalSize = new FileInfo(dest).Length; } catch { }
            Report(stage, finalSize, total > 0 ? total : finalSize, lastSpeed);
        }

        /// <summary>aria2c 体积监控线程的停止标志。</summary>
        private volatile bool _ariaStop;

        /// <summary>
        /// 用 Range: bytes=0-0 探一次总长度（服务器回 206 + Content-Range）。
        /// 只为了进度条的百分比；失败返回 -1，界面自动退化成滚动条。
        /// </summary>
        private long ProbeLength(string url, string referer)
        {
            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.TryAddWithoutValidation("Referer", referer);
                    req.Headers.TryAddWithoutValidation("User-Agent", Settings.UserAgent);
                    req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                    using (HttpResponseMessage resp = MediaClient.Instance.Send(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (resp.Content.Headers.ContentRange != null
                            && resp.Content.Headers.ContentRange.Length.HasValue
                            && resp.Content.Headers.ContentRange.Length.Value > 0)
                            return resp.Content.Headers.ContentRange.Length.Value;

                        if (resp.Content.Headers.ContentLength.HasValue
                            && resp.Content.Headers.ContentLength.Value > 0)
                            return resp.Content.Headers.ContentLength.Value;
                        return -1L;
                    }
                }
            }
            catch { return -1L; }
        }

        /// <summary>
        /// 挑出 aria2c 真正的原因行。
        ///
        /// aria2c 的失败信息长这样，夹在输出中间：
        ///     [ERROR] CUID#7 - Download aborted. URI=https://...
        ///     Exception: [AbstractCommand.cc:351] errorCode=22 URI=https://...
        ///       -> [HttpSkipResponseCommand.cc:240] errorCode=22 The response status is not successful. status=403
        /// 末尾才是 "Status Legend:"。所以直接取输出尾部只会拿到那句没用的图例。
        /// </summary>
        private static string Aria2cReason(string output)
        {
            string[] lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var picked = new List<string>();
            for (int i = 0; i < lines.Length && picked.Count < 3; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0) continue;
                if (s.IndexOf("Status Legend", StringComparison.OrdinalIgnoreCase) >= 0) break;
                bool interesting = s.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("errorCode", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("status=", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0;
                if (interesting) picked.Add(s);
            }
            if (picked.Count > 0)
            {
                string joined = string.Join(" | ", picked.ToArray());
                // 这种组合很有辨识度：本程序自己写这个目录没问题（预检能过），
                // 但 aria2c / ffmpeg 作为独立进程写不进去。最常见的原因是
                // Windows「受控文件夹访问」只给 BiliGetter.exe 放行了一次。
                if (joined.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0
                    || joined.IndexOf("errNum=5", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    joined += "\r\n    → 本程序自己往这个目录写文件是成功的，但 aria2c / ffmpeg 写不进去。"
                        + "\r\n      这是「工具不在受信任位置」而不是输出目录的问题：Windows 的"
                        + "\r\n      「受控文件夹访问」按可执行文件路径放行，主程序被放行了，"
                        + "\r\n      但放在别处的 aria2c.exe / ffmpeg.exe 没有。"
                        + "\r\n      解决一（最省事，已实测有效）：把 aria2c.exe 和 ffmpeg.exe 放到"
                        + "\r\n              BiliGetter.exe 同一个文件夹里，程序会自动优先用它们。"
                        + "\r\n      解决二：安全中心 → 病毒和威胁防护 → 勒索软件防护 → 受控文件夹访问 →"
                        + "\r\n              允许应用通过，逐个添加这两个 exe 的完整路径。"
                        + "\r\n      解决三：把输出目录换到不受保护的路径（例如 D:\\bili-dl）。";
                }
                return joined;
            }

            // 兜底：没有可识别的行，就取最前面几行非空内容
            var head = new List<string>();
            for (int i = 0; i < lines.Length && head.Count < 3; i++)
            {
                string s = lines[i].Trim();
                if (s.Length > 0 && s.IndexOf("Status Legend", StringComparison.OrdinalIgnoreCase) < 0) head.Add(s);
            }
            return head.Count > 0 ? string.Join(" | ", head.ToArray()) : "(aria2c 没有输出)";
        }

        /// <summary>内置下载器（HttpClient 单连接）。aria2c 未启用或失败时走这里。</summary>
        private void FetchWithHttp(string url, string dest, string referer, string stage)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("Referer", referer);
                req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
                req.Headers.TryAddWithoutValidation("User-Agent", Settings.UserAgent);

                // ResponseHeadersRead：拿到响应头就开始写文件，不把整个视频先读进内存。
                using (HttpResponseMessage resp = MediaClient.Instance.Send(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1L;
                    using (Stream st = resp.Content.ReadAsStream())
                    using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 131072))
                    {
                        byte[] buf = new byte[131072];
                        long done = 0;
                        long startMs = Environment.TickCount;
                        long lastMs = startMs;
                        long lastDone = 0;
                        double speed = 0;
                        int n;
                        while ((n = st.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (Cancelled) return;
                            fs.Write(buf, 0, n);
                            done += n;

                            // 每 250ms 报一次：单连接时速度要自己算，
                            // 用一段滑动平均，避免数字乱跳。
                            long now = Environment.TickCount;
                            if (now - lastMs >= 250)
                            {
                                double inst = (done - lastDone) * 1000.0 / Math.Max(1, now - lastMs);
                                speed = speed <= 0 ? inst : speed * 0.5 + inst * 0.5;
                                lastMs = now;
                                lastDone = done;
                                Report(stage, done, total, speed);
                            }
                        }
                        // 小文件可能在一个采样间隔内就传完，速度还是 0；
                        // 用整体平均速度兜底，免得界面显示成「—」。
                        if (speed <= 0)
                        {
                            long ms = Math.Max(1, Environment.TickCount - startMs);
                            speed = done * 1000.0 / ms;
                        }
                        Report(stage, done, total, speed);
                    }
                }
            }
        }

        private void Merge(string videoFile, string audioFile, string outFile)
        {
            if (string.IsNullOrEmpty(Settings.FfmpegPath))
                throw new Exception("找不到 ffmpeg，无法合并音视频");

            // 兜底：万一别处漏了加密检查，这里也要给出准确原因，
            // 而不是让用户看到 "error reading header" 后去猜是不是文件被占用。
            if (EncryptedMedia.IsEncrypted(videoFile) || EncryptedMedia.IsEncrypted(audioFile))
                throw new DrmProtectedException(
                    "分片是 CENC 加密的（encv / enca），ffmpeg 没有解密密钥，无法合并");

            string args = "-hide_banner -loglevel error -y -i " + Util.Quote(videoFile);
            if (audioFile != null) args += " -i " + Util.Quote(audioFile);
            args += " -c copy -movflags +faststart " + Util.Quote(outFile);

            string outp = "";
            int code = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                code = Util.Run(Settings.FfmpegPath, args, out outp, delegate (Process p) { _current = p; });
                _current = null;
                if (code == 0 && File.Exists(outFile)) return;

                // 第一次失败先别认：目标文件被播放器 / 杀毒软件扫描 / 另一个窗口
                // 占着时就是 EACCES(-13)。清掉目标（含只读属性）再试一次。
                if (attempt == 0)
                {
                    System.Threading.Thread.Sleep(600);
                    try
                    {
                        if (File.Exists(outFile))
                        {
                            File.SetAttributes(outFile, FileAttributes.Normal);
                            File.Delete(outFile);
                        }
                    }
                    catch { }
                }
            }

            throw new Exception("ffmpeg 合并失败 (exit " + code + ")"
                + (outp.Length > 0 ? ": " + Tail(outp) : "")
                + "\r\n    常见原因：目标文件正被播放器占用、杀毒软件正在扫描，"
                + "\r\n    或者同一门课开了两个窗口在同时下载。"
                + "\r\n    排除占用后重试即可（分片会重新下载）。");
        }

        private static string Tail(string s)
        {
            s = s.Trim();
            return s.Length <= 400 ? s : s.Substring(s.Length - 400);
        }
    }
}
