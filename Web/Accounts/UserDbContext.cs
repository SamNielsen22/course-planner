using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Web.Accounts;

/// <summary>Whether accounts are on, and whether Google sign-in is configured.</summary>
public sealed record AccountsFeature(bool Enabled, bool Google);

/// <summary>One saved schedule: the same keys a guest cookie holds, kept under the account.</summary>
public class SavedSchedule
{
    public string Id { get; set; } = "";           // short random id
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Term { get; set; }              // "Fall2026", chosen at creation
    public string Campus { get; set; } = "main";   // main, uac or online, chosen at creation
    public string Sections { get; set; } = "[]";   // JSON array of section keys
    public string Breaks { get; set; } = "[]";     // JSON array of Break
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The account database in Postgres: users and their saved schedules. The public pages never need it.</summary>
public class UserDbContext(DbContextOptions<UserDbContext> options)
    : IdentityDbContext<IdentityUser>(options), IDataProtectionKeyContext
{
    public DbSet<SavedSchedule> Schedules => Set<SavedSchedule>();

    /// <summary>
    /// The keys that sign the schedule and account cookies. They live here rather
    /// than on one machine's disk so every server shares them: a restart signs
    /// nobody out, and a visitor moved between servers stays signed in.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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

/// <summary>How `dotnet ef` builds the context: from user secrets, over the direct connection, which migrations need.</summary>
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
