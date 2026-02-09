using HR;
using MintPlayer.Spark;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration);
builder.Services.AddScoped<SparkContext, HRContext>();
builder.Services.AddSparkMessaging();

builder.Services.AddSparkReplication(opt =>
{
    var section = builder.Configuration.GetSection("SparkReplication");
    opt.ModuleName = section["ModuleName"] ?? "HR";
    opt.ModuleUrl = section["ModuleUrl"] ?? "https://localhost:5002";
    opt.SparkModulesUrls = section.GetSection("SparkModulesUrls").Get<string[]>() ?? ["http://localhost:8080"];
    opt.SparkModulesDatabase = section["SparkModulesDatabase"] ?? "SparkModules";
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseSpark();
app.CreateSparkIndexes();
app.CreateSparkMessagingIndexes();
app.UseSparkReplication();

app.MapSpark();
app.MapSparkReplication();

app.Run();
