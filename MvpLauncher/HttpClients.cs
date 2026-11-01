using System;
using System.Net;
using System.Net.Http;

namespace MvpLauncher.Infrastructure
{
    public static class HttpClients
    {
        private static readonly Lazy<HttpClient> _shared = new(Create, isThreadSafe: true);

        public static HttpClient Shared => _shared.Value;

        private static HttpClient Create()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 16,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(15)
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(5),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("MvpLauncher/1.0.26");
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
            return client;
        }
    }
}