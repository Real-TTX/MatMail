namespace MatMail.Controls;

/// <summary>What the head of a page needs: its title and the product name of the tenant, the accent colour of the tenant (when it applies) and whether the page is the mail client.</summary>
public sealed record HeadModel(string Title, string ProductName, string? AccentColor = null, bool Mail = false);
