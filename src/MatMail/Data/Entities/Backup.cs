namespace MatMail.Data;

/// <summary>
/// A place backups are written to: a folder of the server (a mounted disk or share) or a folder on an SMB share (a NAS). These
/// rows belong to the installation, not to a tenant, and travel with every backup: a restored server goes on backing up to the same places.
/// </summary>
public class BackupTarget : AuditedEntity
{
    public string Name { get; set; } = string.Empty;
    public BackupTargetKind Kind { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Local: the folder (absolute path). SMB: the folder inside the share (empty = the root of the share).</summary>
    public string Path { get; set; } = string.Empty;

    // SMB only
    public string? Host { get; set; }
    public string? Share { get; set; }
    public string? Domain { get; set; }
    public string? Username { get; set; }

    /// <summary>The password, encrypted with the key ring of the installation (<c>SecretProtector</c>).</summary>
    public string? PasswordProtected { get; set; }

    // What the last connection test found.
    public DateTime? LastCheckDate { get; set; }
    public bool? LastCheckOk { get; set; }
    public string? LastCheckMessage { get; set; }
}

/// <summary>What is backed up when, where to, for how long it is kept and whether it is encrypted.</summary>
public class BackupPlan : AuditedEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public long TargetId { get; set; }
    public BackupTarget? Target { get; set; }

    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;

    /// <summary>Hourly: every this many hours (1 to 12).</summary>
    public int EveryHours { get; set; } = 6;

    /// <summary>Local time (the time zone of the installation) as minutes after midnight; hourly plans use its minute.</summary>
    public int MinuteOfDay { get; set; } = 3 * 60;

    /// <summary>Weekly: 0 = Sunday … 6 = Saturday.</summary>
    public int DayOfWeek { get; set; } = 1;

    /// <summary>Monthly: 1 to 28.</summary>
    public int DayOfMonth { get; set; } = 1;

    // What is kept: a backup stays when any of these keeps it.
    /// <summary>The newest this many.</summary>
    public int KeepLast { get; set; } = 7;

    /// <summary>The newest of each of the last this many days.</summary>
    public int KeepDaily { get; set; }

    /// <summary>The newest of each of the last this many weeks.</summary>
    public int KeepWeekly { get; set; } = 4;

    /// <summary>The newest of each of the last this many months.</summary>
    public int KeepMonthly { get; set; } = 6;

    public bool Encrypt { get; set; }

    /// <summary>The passphrase, encrypted with the key ring of the installation. Kept here so scheduled backups can be encrypted; the administrator must keep a copy elsewhere.</summary>
    public string? PassphraseProtected { get; set; }

    /// <summary>Read the finished backup back and compare it with its manifest before it is accepted.</summary>
    public bool Verify { get; set; } = true;

    /// <summary>Tell the administrators (a message in their mailbox) when a scheduled run fails.</summary>
    public bool NotifyOnFailure { get; set; } = true;

    // State
    public DateTime? NextRunDate { get; set; }
    public DateTime? LastRunDate { get; set; }
    public BackupRunStatus? LastStatus { get; set; }
    public int ConsecutiveFailures { get; set; }
}

/// <summary>One backup that was made (or tried): the history on the backup page.</summary>
public class BackupRun : AuditedEntity
{
    public long? PlanId { get; set; }
    public BackupPlan? Plan { get; set; }

    /// <summary>The names as they were then: the history outlives a plan or a target that is deleted.</summary>
    public string PlanName { get; set; } = string.Empty;

    public long? TargetId { get; set; }
    public string TargetName { get; set; } = string.Empty;

    public BackupRunKind Kind { get; set; }
    public BackupRunStatus Status { get; set; }
    public DateTime StartedDate { get; set; }
    public DateTime? FinishedDate { get; set; }

    public string? FileName { get; set; }
    public long? Bytes { get; set; }
    public bool Encrypted { get; set; }
    public int? Tables { get; set; }
    public long? Rows { get; set; }
    public int? Files { get; set; }

    /// <summary>How many older backups the retention rules removed from the target in this run.</summary>
    public int Pruned { get; set; }

    /// <summary>The error of a failed run.</summary>
    public string? Message { get; set; }

    public string? AppVersion { get; set; }
}
