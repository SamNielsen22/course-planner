using Dapper;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/courses", () =>
{
    var dbPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "data", "courseplanner.db"));
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    return conn.Query("SELECT id, subject, catalog_number, title, units FROM courses").ToList();
    
});

app.Run();
