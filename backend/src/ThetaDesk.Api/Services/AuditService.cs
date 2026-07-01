using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThetaDesk.Data;
using ThetaDesk.Domain.Entities;

namespace ThetaDesk.Api.Services;

public class AuditService(ThetaDeskDbContext db)
{
    public async Task LogAsync(Guid fundId, string actor, string action, object? before, object? after, CancellationToken ct = default)
    {
        var lastHash = await db.AuditLog
            .Where(a => a.FundId == fundId)
            .OrderByDescending(a => a.AtUtc)
            .Select(a => a.HashPrev)
            .FirstOrDefaultAsync(ct);

        var beforeJson = before != null ? JsonSerializer.Serialize(before) : null;
        var afterJson = after != null ? JsonSerializer.Serialize(after) : null;
        var content = $"{actor}|{action}|{beforeJson}|{afterJson}|{lastHash}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

        db.AuditLog.Add(new AuditEntry
        {
            FundId = fundId,
            Actor = actor,
            Action = action,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            HashPrev = hash
        });
        await db.SaveChangesAsync(ct);
    }
}
