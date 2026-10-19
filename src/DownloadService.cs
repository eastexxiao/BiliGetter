// 下载服务：把 Downloader 的同步循环包装成可等待的任务，
// 并用 CancellationToken 代替原来散落各处的 Cancel() 调用。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BiliGetter
{
    internal sealed class DownloadService : IDownloadService
    {
        private readonly IBiliService _bili;

        public DownloadService(IBiliService bili) { _bili = bili; }

        public Task<DownloadBatchResult> RunAsync(
            IList<DownloadItem> items,
            DownloadSettings settings,
            IDownloadObserver observer,
            CancellationToken ct)
        {
            return Task.Run(delegate
            {
                DownloadBatchResult r = new DownloadBatchResult();

                Downloader d = new Downloader();
                d.Settings = settings;
                if (observer != null)
                {
                    d.Log = observer.Log;
                    d.Transfer = observer.Transferred;
                }

                // Downloader 自己管进程，取消要同时告诉它（杀子进程）和循环（尽快退出）
                using (ct.Register(delegate { try { d.Cancel(); } catch { } }))
                {
                    try
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (ct.IsCancellationRequested) { r.Cancelled = true; break; }

                            DownloadItem it = items[i];
                            if (observer != null) observer.ItemStarting(i + 1, items.Count, it);

                            bool ok = false;
                            try
                            {
                                ok = d.DownloadOne(_bili.Session, it, null);
                            }
                            catch (DrmProtectedException ex)
                            {
                                // 同一门课的所有分集是同一套加密，继续下一项只是白烧流量
                                r.DrmProtected = true;
                                r.Fail++;
                                if (observer != null)
                                {
                                    observer.Log("  ✗ " + it.Title + "：" + ex.Message);
                                    observer.ItemFinished(it, false);
                                }
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (observer != null) observer.Log("  ✗ " + it.Title + "：" + ex.Message);
                            }

                            if (ok) r.Ok++; else r.Fail++;
                            if (observer != null) observer.ItemFinished(it, ok);
                        }
                    }
                    finally
                    {
                        if (ct.IsCancellationRequested) r.Cancelled = true;
                    }
                }
                return r;
            });
        }
    }
}
