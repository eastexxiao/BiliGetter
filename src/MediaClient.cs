// 媒体流（DASH 音视频）下载专用的 HttpClient。
// CDN 地址本身已带签名，不需要会话 Cookie，只需要 Referer / User-Agent。
using System;
using System.Net;
using System.Net.Http;

namespace BiliGetter
{
    internal static class MediaClient
    {
        public static readonly HttpClient Instance = Create();

        private static HttpClient Create()
        {
            SocketsHttpHandler handler = new SocketsHttpHandler();
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            handler.ConnectTimeout = TimeSpan.FromSeconds(20);
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);

            HttpClient client = new HttpClient(handler);
            // 整体超时给得很宽松：4K/8K 大文件可能要下很久。
            // 真正的中断由 Downloader.Cancelled（取消）和读取循环来管，而不是靠超时。
            client.Timeout = TimeSpan.FromMinutes(30);
            return client;
        }
    }
}
