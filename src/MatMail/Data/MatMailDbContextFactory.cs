using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatMail.Data;

/// <summary>Used by <c>dotnet ef</c> at design time (migrations); never at run time.</summary>
public sealed class MatMailDbContextFactory : IDesignTimeDbContextFactory<MatMailDbContext>
{
    public MatMailDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MatMailDbContext>()
            .UseNpgsql("Host=localhost;Database=matmail_design;Username=postgres;Password=design")
            .Options;
        return new MatMailDbContext(options, new CurrentUser());
    }
}
