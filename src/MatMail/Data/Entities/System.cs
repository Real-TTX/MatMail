namespace MatMail.Data;

/// <summary>
/// A fact about the installation itself, not about a tenant: its id, the version of the program that ran last, the version of the
/// files in the data volume. Key and value, one row per key. These rows travel with every backup, so a restore knows what it got.
/// </summary>
public class SystemSetting : AuditedEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
