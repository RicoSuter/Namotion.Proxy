using Namotion.Interceptor.SampleBlazor.Components;
using Namotion.Interceptor.SampleBlazor.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.SampleBlazor
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services
                .AddRazorComponents()
                .AddInteractiveServerComponents();

            // Add trackable game
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithReadPropertyRecorder();

            builder.Services
                .AddSingleton<Game>(_ => new Game(context))
                .AddScoped<Player>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
