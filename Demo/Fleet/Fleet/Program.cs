using Fleet;
using MintPlayer.Spark;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration);
builder.Services.AddScoped<SparkContext, FleetContext>();
builder.Services.AddSparkMessaging();

builder.Services.AddSparkReplication(opt =>
{
    var section = builder.Configuration.GetSection("SparkReplication");
    opt.ModuleName = section["ModuleName"] ?? "Fleet";
    opt.ModuleUrl = section["ModuleUrl"] ?? "https://localhost:5001";
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
