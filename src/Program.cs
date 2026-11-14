var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("api/v2/status", () => "ooo ooo aaa aaa");

app.Run();
