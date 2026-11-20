using TermLume.Api;

if (args is ["--health-check"])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        using var response = await http.GetAsync("http://127.0.0.1:8080/healthz");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
    {
        Environment.ExitCode = 1;
    }
    return;
}

var migration = args.Contains("--migrate");
var builder = WebApplication.CreateBuilder(args.Where(argument => argument != "--migrate").ToArray());
var settingsIndex = Array.IndexOf(args, "--settings");
if (settingsIndex >= 0)
{
    if (settingsIndex + 1 >= args.Length) throw new InvalidOperationException("Missing settings path.");
    builder.Configuration.AddJsonFile(Path.GetFullPath(args[settingsIndex + 1]), optional: false, reloadOnChange: false);
}
builder.Configuration.AddEnvironmentVariables("TERMLUME_");
if (migration)
{
    var connection = builder.Configuration.GetConnectionString("Workspace")
        ?? throw new InvalidOperationException("Configure the server-side workspace database connection.");
    await DatabaseMigrator.ApplyAsync(connection);
    Console.WriteLine("Workspace database migrations applied.");
    return;
}

builder.Host.UseAutofac();
await builder.AddApplicationAsync<TermLumeApiModule>();
var app = builder.Build();
await app.InitializeApplicationAsync();
await app.RunAsync();
