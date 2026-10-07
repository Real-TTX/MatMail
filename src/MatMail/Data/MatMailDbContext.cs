using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MatMail.Data;

public class MatMailDbContext : DbContext
{
    private readonly CurrentUser _current;

    public MatMailDbContext(DbContextOptions<MatMailDbContext> options, CurrentUser current) : base(options)
    {
        _current = current;
    }

    /// <summary>The tenant filter applied to every <see cref="ITenantEntity"/> query (null = no restriction).</summary>
    public long? CurrentTenantId => _current.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantBranding> TenantBrandings => Set<TenantBranding>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserTotp> UserTotps => Set<UserTotp>();
    public DbSet<UserRecoveryCode> UserRecoveryCodes => Set<UserRecoveryCode>();
    public DbSet<AppPassword> AppPasswords => Set<AppPassword>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Domain> Domains => Set<Domain>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<MailboxAlias> MailboxAliases => Set<MailboxAlias>();
    public DbSet<MailboxPermission> MailboxPermissions => Set<MailboxPermission>();
    public DbSet<MailFolder> MailFolders => Set<MailFolder>();
    public DbSet<MailMessage> MailMessages => Set<MailMessage>();
    public DbSet<MailMessageContent> MailMessageContents => Set<MailMessageContent>();
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();
    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();
    public DbSet<MailAccountFolderState> MailAccountFolderStates => Set<MailAccountFolderState>();
    public DbSet<RemoteMessageState> RemoteMessageStates => Set<RemoteMessageState>();
    public DbSet<Signature> Signatures => Set<Signature>();
    public DbSet<MailTemplate> MailTemplates => Set<MailTemplate>();
    public DbSet<RelayRule> RelayRules => Set<RelayRule>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // The tenant filter on User/Role/... makes some required navigations "optional" for EF; that is intended.
        options.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        ConfigureIdentity(model);
        ConfigureMailboxes(model);
        ConfigureMessages(model);
        ConfigureAccounts(model);
        ConfigureSettings(model);

        foreach (IMutableEntityType entity in model.Model.GetEntityTypes())
        {
            // Tables are named like the entity: PascalCase, singular.
            entity.SetTableName(entity.ClrType.Name);

            ApplyEnumStringConversion(entity);
            ApplyTenantRules(model, entity);
        }
    }

    private static void ConfigureIdentity(ModelBuilder model)
    {
        model.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(40);
        });

        model.Entity<TenantBranding>(e =>
        {
            e.HasIndex(x => x.TenantId).IsUnique();
            e.HasIndex(x => x.LogoToken);
            e.Property(x => x.BrandName).HasMaxLength(100);
            e.Property(x => x.Website).HasMaxLength(300);
            e.Property(x => x.AccentColor).HasMaxLength(7);
            e.Property(x => x.LogoContentType).HasMaxLength(100);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<User>(e =>
        {
            e.HasIndex(x => x.LoginName).IsUnique();
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.LoginName).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Salutation).HasMaxLength(50);
            e.Property(x => x.Title).HasMaxLength(100);
            e.Property(x => x.FirstName).HasMaxLength(150);
            e.Property(x => x.LastName).HasMaxLength(150);
            e.Property(x => x.Department).HasMaxLength(150);
            e.Property(x => x.Mobile).HasMaxLength(50);
            e.Property(x => x.Fax).HasMaxLength(50);
            e.Property(x => x.TextSize).HasMaxLength(20);
            e.Property(x => x.Density).HasMaxLength(20);
            e.Property(x => x.TimeZone).HasMaxLength(100);

            // Existing users keep seeing the previews they have always seen.
            e.Property(x => x.ShowPreviews).HasDefaultValue(true);
            e.HasMany(x => x.UserRoles).WithOne(x => x.User).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserSession>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresDate);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserTotp>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.Secret).HasMaxLength(400);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserRecoveryCode>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.CodeHash).HasMaxLength(200);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<AppPassword>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Prefix });
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Prefix).HasMaxLength(8);
            e.Property(x => x.SecretHash).HasMaxLength(200);
            e.Property(x => x.LastUsedIp).HasMaxLength(64);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Role>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
        });

        model.Entity<UserRole>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMailboxes(ModelBuilder model)
    {
        model.Entity<Domain>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(253);
            e.HasOne(x => x.CatchAllMailbox).WithMany().HasForeignKey(x => x.CatchAllMailboxId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<Mailbox>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Type });
            e.HasIndex(x => x.OwnerUserId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Aliases).WithOne(x => x.Mailbox).HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Permissions).WithOne(x => x.Mailbox).HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Folders).WithOne(x => x.Mailbox).HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<MailboxAlias>(e =>
        {
            e.HasIndex(x => x.Address).IsUnique();
            e.HasIndex(x => x.MailboxId);
            e.Property(x => x.Address).HasMaxLength(320);
            e.Ignore(x => x.IsCatchAll);
            e.HasOne(x => x.SendAccount).WithMany().HasForeignKey(x => x.SendAccountId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<MailboxPermission>(e =>
        {
            e.HasIndex(x => new { x.MailboxId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<MailFolder>(e =>
        {
            // NULLS NOT DISTINCT: two root folders with the same name are a duplicate too.
            e.HasIndex(x => new { x.MailboxId, x.ParentId, x.Name }).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.Name).HasMaxLength(255);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMessages(ModelBuilder model)
    {
        model.Entity<MailMessage>(e =>
        {
            e.HasIndex(x => new { x.FolderId, x.Uid }).IsUnique();
            e.HasIndex(x => new { x.MailboxId, x.FolderId, x.ReceivedDate });
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.MessageIdHeader);
            e.HasIndex(x => x.ThreadKey);
            e.HasIndex(x => x.SourceAccountId);
            e.Property(x => x.Subject).HasMaxLength(1000);
            e.Property(x => x.FromName).HasMaxLength(500);
            e.Property(x => x.FromAddress).HasMaxLength(320);
            e.Property(x => x.Preview).HasMaxLength(400);
            e.HasOne(x => x.Folder).WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Content).WithOne(x => x.Message).HasForeignKey<MailMessageContent>(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MailAccount>().WithMany().HasForeignKey(x => x.SourceAccountId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<MailMessageContent>(e =>
        {
            e.HasIndex(x => x.MessageId).IsUnique();
        });

        model.Entity<OutboundMessage>(e =>
        {
            e.HasIndex(x => new { x.Status, x.NextAttemptDate });
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Subject).HasMaxLength(1000);
            e.HasOne(x => x.MailAccount).WithMany().HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureAccounts(ModelBuilder model)
    {
        model.Entity<MailAccount>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(320);
            e.HasOne(x => x.TargetMailbox).WithMany().HasForeignKey(x => x.TargetMailboxId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<MailAccountFolderState>(e =>
        {
            e.HasIndex(x => new { x.MailAccountId, x.RemoteFolder }).IsUnique();
            e.HasOne(x => x.MailAccount).WithMany().HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<RemoteMessageState>(e =>
        {
            e.HasIndex(x => new { x.MailAccountId, x.RemoteFolder, x.RemoteUid }).IsUnique();
            e.HasOne(x => x.MailAccount).WithMany().HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureSettings(ModelBuilder model)
    {
        model.Entity<Signature>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<RelayRule>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.SendAccount).WithMany().HasForeignKey(x => x.SendAccountId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<MailTemplate>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne<Mailbox>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            // A template that was meant for one smart-host rule must not start to apply to every rule when that rule is deleted.
            e.HasOne<RelayRule>().WithMany().HasForeignKey(x => x.RelayRuleId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ActivityLog>(e =>
        {
            e.HasIndex(x => x.CreateDate);
            e.HasIndex(x => new { x.Category, x.Level });
            e.Property(x => x.Message).HasMaxLength(2000);
        });
    }

    /// <summary>Every enum column is stored by name.</summary>
    private static void ApplyEnumStringConversion(IMutableEntityType entity)
    {
        foreach (IMutableProperty property in entity.GetProperties())
        {
            Type type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (!type.IsEnum)
            {
                continue;
            }

            Type converter = typeof(EnumToStringConverter<>).MakeGenericType(type);
            property.SetValueConverter((ValueConverter)Activator.CreateInstance(converter)!);
            property.SetMaxLength(40);
        }
    }

    /// <summary>Tenant foreign key + the global filter that keeps every query inside the current tenant.</summary>
    private void ApplyTenantRules(ModelBuilder model, IMutableEntityType entity)
    {
        if (!typeof(ITenantEntity).IsAssignableFrom(entity.ClrType))
        {
            return;
        }

        model.Entity(entity.ClrType)
            .HasOne(typeof(Tenant)).WithMany().HasForeignKey(nameof(ITenantEntity.TenantId)).OnDelete(DeleteBehavior.Cascade);

        TenantFilterMethod.MakeGenericMethod(entity.ClrType).Invoke(this, new object[] { model });
    }

    private static readonly System.Reflection.MethodInfo TenantFilterMethod =
        typeof(MatMailDbContext).GetMethod(nameof(SetTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    private void SetTenantFilter<T>(ModelBuilder model) where T : class, ITenantEntity
        => model.Entity<T>().HasQueryFilter(e => CurrentTenantId == null || e.TenantId == CurrentTenantId);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditAndTenantRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditAndTenantRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Fills CreateDate/UpdateDate/…UserId and the tenant of new rows, and refuses to write rows of another tenant.
    /// </summary>
    private void ApplyAuditAndTenantRules()
    {
        DateTime now = DateTime.UtcNow;
        long? userId = _current.UserId;
        long? tenantId = CurrentTenantId;

        foreach (var entry in ChangeTracker.Entries<AuditedEntity>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            AuditedEntity entity = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                if (entity.CreateDate == default)
                {
                    entity.CreateDate = now;
                }

                entity.CreateUserId ??= userId;
                entity.UpdateUserId ??= entity.CreateUserId;
            }
            else
            {
                entity.UpdateUserId = userId;
            }

            entity.UpdateDate = now;

            if (entity is ITenantEntity tenantRow && tenantId is long restrictedTo)
            {
                if (tenantRow.TenantId == 0 && entry.State == EntityState.Added)
                {
                    tenantRow.TenantId = restrictedTo;
                }
                else if (tenantRow.TenantId != restrictedTo)
                {
                    throw new InvalidOperationException(
                        $"Refusing to write {entity.GetType().Name} of tenant {tenantRow.TenantId} while working in tenant {restrictedTo}.");
                }
            }
        }
    }
}
