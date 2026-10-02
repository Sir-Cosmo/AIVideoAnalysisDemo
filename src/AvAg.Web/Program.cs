using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvAg.Pipeline.Services;
using AvAg.Web;
using AvAg.Web.Endpoints;
using Microsoft.AspNetCore.Http.Features;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);
const long MaxUpload = 2L * 1024 * 1024 * 1024; // 2 GB
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUpload);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxUpload);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services.AddSingleton(builder.Configuration.GetSection("AvAg").Get<WebSettings>() ?? new WebSettings());
// All AI implementations come from here. To add another AI, register it on the factory, e.g.
//   factory.TextGenerator.Register("my-llm", o => new MyTextGenerator(o.Url!, o.Model));
// and select it in appsettings.json: "Services": { "TextGenerator": { "Provider": "my-llm", ... } }.
builder.Services.AddSingleton(_ => AiServiceFactory.CreateDefault());
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<SidecarLauncher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SidecarLauncher>());
builder.Services.AddHostedService<JobJanitor>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapJobEndpoints();
app.MapArticleEndpoints();
app.Run();
