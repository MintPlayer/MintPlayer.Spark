# MintPlayer.Spark.Controllers

MVC controller support for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark).

An application's own controllers normally sit beside Spark rather than inside it: they authenticate
and authorize, but nothing Spark configures is scoped to them, so the antiforgery gate never fires on
them and there is no way to authorize an action against the same `security.json` right the Spark
pipeline checks.

```csharp
builder.Services.AddSpark(spark =>
{
    spark.AddControllers();
    spark.UseControllers();

    spark.AddAntiforgeryProtection(a =>
    {
        a.PathPrefixes = ["/spark", "/connect", "/api"];
        a.RequireAntiforgery = true;
    });
});

app.UseRouting();
app.UseSpark();
app.MapSpark();      // mounts the controllers too
```

Do not also call `endpoints.MapControllers()`. Analyzer **SPARK010** reports it: at runtime the call
leaves no trace Spark can inspect, so the diagnostic is the only place it can be caught.

Authorize an action against a Spark right — `[SparkAuthorize]` ships in
`MintPlayer.Spark.Authorization`, because it is the package that owns `security.json`:

```csharp
[HttpPost("tokens")]
[SparkAuthorize("New", nameof(UploadToken))]
public async Task<IActionResult> Create() { … }
```
