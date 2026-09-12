using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Web.Accounts;

/// <summary>
/// Whether accounts are switched on (a UserData connection string is
/// configured), and whether Google sign-in is - the only way in, so without
/// it the nav offers nothing.
/// </summary>
public sealed record AccountsFeature(bool Enabled, bool Google);

/// <summary>
/// One of a signed-in student's schedules: the same keys the guest cookie
/// holds, kept under their account so it follows them between devices and
/// outlives the cookie. A student keeps as many as they like; the builder
/// edits whichever one is open.
/// </summary>
public class SavedSchedule
{
    public string Id { get; set; } = "";           // short random id; the open-schedule cookie names it
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Term { get; set; }              // "Fall2026": chosen at creation, so an empty schedule still has one
    public string Campus { get; set; } = "main";   // main, uac or online: chosen at creation; one schedule is one campus
    public string Sections { get; set; } = "[]";   // JSON array of "Term|Subject|Number|Section"
    public string Breaks { get; set; } = "[]";     // JSON array of Break
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Everything the site stores about people, in Postgres: Identity's users and
/// their saved schedules. Nothing about courses lives here - that is the
/// catalogue, a file replaced whole after each scrape. If this database is
/// unreachable the public pages still serve; only signing in fails.
/// </summary>
public class UserDbContext(DbContextOptions<UserDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<SavedSchedule> Schedules => Set<SavedSchedule>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<SavedSchedule>(e =>
        {
            e.ToTable("schedules");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.UserId);
        });
    }
}

/// <summary>
/// How `dotnet ef` builds the context at design time: from user-secrets,
/// preferring the direct (unpooled) connection - migrations take locks that a
/// connection pooler does not carry.
/// </summary>
public class UserDbContextFactory : IDesignTimeDbContextFactory<UserDbContext>
{
    public UserDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<UserDbContextFactory>()
            .AddEnvironmentVariables()
            .Build();
        var connection = configuration.GetConnectionString("UserDataDirect")
                         ?? configuration.GetConnectionString("UserData")
                         ?? throw new InvalidOperationException("No UserData connection string is configured.");
        return new UserDbContext(new DbContextOptionsBuilder<UserDbContext>().UseNpgsql(connection).Options);
    }
}
