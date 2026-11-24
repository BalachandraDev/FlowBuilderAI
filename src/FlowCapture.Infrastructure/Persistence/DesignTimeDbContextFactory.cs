using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FlowCapture.Infrastructure.Persistence;

/// <summary>Used only by "dotnet ef" to create migrations against SQL Server.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FLOWCAPTURE_DESIGN_CONNECTION")
                         ?? @"Server=(localdb)\MSSQLLocalDB;Database=FlowCaptureDesign;Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        return new AppDbContext(options);
    }
}
