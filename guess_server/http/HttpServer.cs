using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using guess_server.websocket;

namespace guess_server.http
{
    public static class HttpServer
    {
        private static readonly int ErrorRetryCounts = 5;
        private static readonly ConcurrentBag<WebSocket> Sockets = new ConcurrentBag<WebSocket>();
        
        public static void MapWebsocket(IApplicationBuilder app)
        {
            app.Use(Acceptor);
        }
        
        private static async Task Acceptor(HttpContext http, Func<Task> n)
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return;
            }
            string key = http.Request.Query["key"].ToString();
            if (String.IsNullOrWhiteSpace(key))
            {
                return;
            }
            var socket = await http.WebSockets.AcceptWebSocketAsync();
            SocketHandler.HandleWebsocket(key, socket);
        }
    }
}
