using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ThetaDesk.Data;

public class ThetaDeskDbContextFactory : IDesignTimeDbContextFactory<ThetaDeskDbContext>
{
    public ThetaDeskDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<ThetaDeskDbContext>()
            .UseNpgsql("Host=localhost;Database=thetadesk;Username=postgres;Password=postgres")
            .Options;
        return new ThetaDeskDbContext(opts);
    }
}
