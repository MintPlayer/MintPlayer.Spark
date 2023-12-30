using Microsoft.AspNetCore.SpaServices.AngularCli;
using MintPlayer.AspNetCore.Hsts;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using MintPlayer.AspNetCore.SpaServices.Routing;
using MintPlayer.AspNetCore.SubDirectoryViews;
using WebMarkupMin.AspNetCore8;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddSpaStaticFiles(options =>
{
	options.RootPath = "ClientApp/dist";
});
builder.Services.AddSpaPrerenderingService<DemoApp.Services.SpaPrerenderingService>();
builder.Services.AddWebMarkupMin(options =>
{
	options.DisablePoweredByHttpHeaders = true;
	options.AllowMinificationInDevelopmentEnvironment = true;
	options.AllowCompressionInDevelopmentEnvironment = true;
	options.DisablePoweredByHttpHeaders = false;
}).AddHttpCompression(options =>
{
}).AddHtmlMinification(options =>
{
	options.MinificationSettings.RemoveEmptyAttributes = true;
	options.MinificationSettings.RemoveRedundantAttributes = true;
	options.MinificationSettings.RemoveHttpProtocolFromAttributes = true;
	options.MinificationSettings.RemoveHttpsProtocolFromAttributes = false;
	options.MinificationSettings.MinifyInlineJsCode = true;
	options.MinificationSettings.MinifyEmbeddedJsCode = true;
	options.MinificationSettings.MinifyEmbeddedJsonData = true;
	options.MinificationSettings.WhitespaceMinificationMode = WebMarkupMin.Core.WhitespaceMinificationMode.Aggressive;
});
builder.Services.AddScoped<DemoApp.Services.IWeatherForecastService, DemoApp.Services.WeatherForecastService>();
builder.Services.ConfigureViewsInSubfolder("Server");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.UseDeveloperExceptionPage();
}
else
{
	app.UseExceptionHandler("/Error");
}

// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
app.UseImprovedHsts();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseEndpoints(endpoints =>
{
	endpoints.MapControllerRoute(
		name: "default",
		pattern: "{controller}/{action=Index}/{id?}");
});

if (!app.Environment.IsDevelopment())
{
	app.UseSpaStaticFiles();
}

app.UseSpaImproved(spa =>
{
	spa.Options.SourcePath = "ClientApp";
	spa.UseSpaPrerendering(options =>
	{
		// Disable below line, and run "npm run build:ssr" or "npm run dev:ssr" manually for faster development.
		options.BootModuleBuilder = app.Environment.IsDevelopment() ? new AngularPrerendererBuilder(npmScript: "build:ssr") : null;
		options.BootModulePath = $"{spa.Options.SourcePath}/dist/ClientApp/server/main.js";
		options.ExcludeUrls = new[] { "/sockjs-node" };
	});

	app.UseWebMarkupMin();

	if (app.Environment.IsDevelopment())
	{
		spa.UseAngularCliServer(npmScript: "start");
	}
});

app.Run();
