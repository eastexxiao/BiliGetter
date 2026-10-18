// 账号 / 解析服务：包住 BiliSession 与 Fetcher，对外只暴露目标级别的操作。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BiliGetter
{
    internal sealed class BiliService : IBiliService
    {
        private readonly BiliSession _s = new BiliSession();

        public bool IsLogin { get { return _s.IsLogin; } }
        public string UserName { get { return _s.UName; } }
        public long Mid { get { return _s.Mid; } }
        public BiliSession Session { get { return _s; } }

        public void Init() { _s.Init(); }

        public void LoadCookiesFrom(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            _s.LoadCookies(File.ReadAllText(path, Encoding.UTF8));
        }

        public void SaveCookiesTo(string path)
        {
            File.WriteAllText(path, _s.DumpCookies(), new UTF8Encoding(false));
        }

        public void ClearCookies(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public Dictionary<string, object> QrGenerate() { return _s.QrGenerate(); }
        public Dictionary<string, object> QrPoll(string key) { return _s.QrPoll(key); }

        public Target Resolve(string input) { return TargetResolver.Resolve(input); }

        public List<DownloadItem> Build(Target target, Action<string> log)
        {
            Fetcher f = new Fetcher(_s);
            return f.Build(target, log);
        }

        public QualityProbe ProbeQuality(DownloadItem first, int qualityCap)
        {
            if (string.IsNullOrEmpty(first.Cid))
            {
                Dictionary<string, object> info = _s.VideoInfo(first.Bvid);
                first.Cid = Json.Str(info, "data", "cid");
            }

            Dictionary<string, object> play = first.Cheese
                ? _s.CheesePlayUrl(first.Aid, first.Cid, first.EpId)
                : _s.PlayUrl(first.Bvid, first.Cid, first.Aid);

            Dictionary<string, object> data = Json.Member(play, "data") as Dictionary<string, object>;
            if (data == null) return null;

            PickedStreams p = StreamPicker.Pick(data, qualityCap);
            QualityProbe q = new QualityProbe();
            q.Label = p.Label;
            q.AudioCodecs = p.AudioCodecs;
            return q;
        }
    }
}
