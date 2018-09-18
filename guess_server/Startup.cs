using System;
using guess_server.db;
using guess_server.http;
using guess_server.log;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace guess_server
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContext<GuessDbContext>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory
        )
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
            }
            
            loggerFactory.AddConsole(LogLevel.Information);
            loggerFactory.AddDebug((category, logLevel) => (
                !category.Contains("Microsoft") &&
                !category.Contains("System") &&
                !category.Contains("ToDoApi") &&
                logLevel > LogLevel.Information
            ));
            Log.InitInstance(loggerFactory);
            var logger = Log.CeateLogger(Log.DefaultLogger);
            app.UseStaticFiles();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5) });
            app.Map("/room", HttpServer.MapWebsocket);
            logger.LogInformation("Server started");
        }
    }
}