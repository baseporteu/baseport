using Microsoft.EntityFrameworkCore;

namespace Baseport;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseSqlite(connectionString).AddInterceptors(new SqlitePragmas(), new RecordChangeInterceptor());

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, System.Data.Common.DbConnection connection) =>
        options.UseSqlite(connection).AddInterceptors(new SqlitePragmas(), new RecordChangeInterceptor());

    public static AppDbContext Open(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        Configure(options, connectionString);
        return new AppDbContext(options.Options);
    }

    public DbSet<TableDefinition> Tables => Set<TableDefinition>();
    public DbSet<FieldDefinition> Fields => Set<FieldDefinition>();
    public DbSet<FormConfig> FormConfigs => Set<FormConfig>();
    public DbSet<ActionDef> Actions => Set<ActionDef>();
    public DbSet<PendingActionRun> PendingActionRuns => Set<PendingActionRun>();
    public DbSet<Record> Records => Set<Record>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SavedQuery> SavedQueries => Set<SavedQuery>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<JobConfig> JobConfigs => Set<JobConfig>();
    public DbSet<OidcProvider> OidcProviders => Set<OidcProvider>();

    public Task<AppSettings?> SettingsAsync() =>
        AppSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder.Entity<TableDefinition>().ToTable("_tables");
        modelBuilder.Entity<FieldDefinition>().ToTable("_fields");
        modelBuilder.Entity<Record>().ToTable("_records");
        modelBuilder.Entity<FormConfig>().ToTable("_forms");
        modelBuilder.Entity<ActionDef>().ToTable("_actions");
        modelBuilder.Entity<PendingActionRun>().ToTable("_action_runs");
        modelBuilder.Entity<UserAccount>().ToTable("_users");
        modelBuilder.Entity<UserSession>().ToTable("_user_sessions");
        modelBuilder.Entity<AuditLog>().ToTable("_audit_log");
        modelBuilder.Entity<SavedQuery>().ToTable("_queries");
        modelBuilder.Entity<AppSettings>().ToTable("_settings");
        modelBuilder.Entity<JobConfig>().ToTable("_jobs");
        modelBuilder.Entity<OidcProvider>().ToTable("_oidc_providers");

        modelBuilder.Entity<TableDefinition>()
            .HasMany(t => t.Fields)
            .WithOne()
            .HasForeignKey(f => f.TableId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FormConfig>()
            .HasOne<TableDefinition>()
            .WithMany()
            .HasForeignKey(f => f.TableId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ActionDef>()
            .HasOne<TableDefinition>()
            .WithMany()
            .HasForeignKey(a => a.TableId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PendingActionRun>()
            .HasIndex(r => new { r.Status, r.NextAttemptAt });

        modelBuilder.Entity<Record>()
            .Property(r => r.UpdatedAt)
            .IsConcurrencyToken();

        modelBuilder.Entity<Record>()
            .HasOne<TableDefinition>()
            .WithMany()
            .HasForeignKey(r => r.TableId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<JobConfig>().HasKey(j => j.Key);

        foreach (var entity in new[]
                 {
                     modelBuilder.Entity<TableDefinition>().Metadata,
                     modelBuilder.Entity<FieldDefinition>().Metadata,
                     modelBuilder.Entity<FormConfig>().Metadata,
                     modelBuilder.Entity<ActionDef>().Metadata,
                     modelBuilder.Entity<PendingActionRun>().Metadata,
                     modelBuilder.Entity<Record>().Metadata,
                     modelBuilder.Entity<UserAccount>().Metadata,
                     modelBuilder.Entity<UserSession>().Metadata,
                     modelBuilder.Entity<SavedQuery>().Metadata,
                     modelBuilder.Entity<AuditLog>().Metadata,
                     modelBuilder.Entity<OidcProvider>().Metadata
                 })
        {
            entity.FindProperty(nameof(TableDefinition.Id))!.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }

        modelBuilder.Entity<Record>()
            .HasIndex(r => new { r.TableId, r.CreatedAt, r.Id });

        modelBuilder.Entity<UserAccount>().HasIndex(u => u.Username).IsUnique();

        modelBuilder.Entity<UserAccount>()
            .HasIndex(u => u.ApiTokenHash)
            .IsUnique()
            .HasFilter("\"ApiTokenHash\" <> ''");

        modelBuilder.Entity<OidcProvider>().HasIndex(p => p.Slug).IsUnique();

        modelBuilder.Entity<UserAccount>()
            .HasIndex(u => new { u.OidcProviderId, u.OidcSubject })
            .IsUnique()
            .HasFilter("\"OidcSubject\" <> ''");

        modelBuilder.Entity<UserSession>().HasIndex(s => s.RefreshTokenHash).IsUnique();
        modelBuilder.Entity<UserSession>().HasIndex(s => s.UserId);

        modelBuilder.Entity<UserSession>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
