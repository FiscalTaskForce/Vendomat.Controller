using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class RemoteCommandJournal(LocalDatabaseService database)
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> TryBeginAsync(Guid? commandId, string commandType, object? payload, CancellationToken cancellationToken = default)
    {
        if (commandId is null || commandId.Value == Guid.Empty)
        {
            return true;
        }

        await database.InitializeAsync();
        var key = commandId.Value.ToString("N");
        var existing = await database.Connection.Table<RemoteCommandLogEntity>()
            .Where(item => item.CommandId == key)
            .FirstOrDefaultAsync();
        if (existing is not null)
        {
            return false;
        }

        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        await database.Connection.InsertOrReplaceAsync(new RemoteCommandLogEntity
        {
            CommandId = key,
            CommandType = commandType,
            PayloadHash = ComputePayloadHash(payload),
            Status = "running",
            CreatedAtUtcTicks = nowTicks,
            UpdatedAtUtcTicks = nowTicks,
        });
        return true;
    }

    public async Task CompleteAsync(Guid? commandId, string? resultMessage, CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(commandId, "completed", resultMessage, cancellationToken);
    }

    public async Task FailAsync(Guid? commandId, string? resultMessage, CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(commandId, "failed", resultMessage, cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteCommandLogEntity>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var rows = await database.Connection.Table<RemoteCommandLogEntity>()
            .OrderByDescending(item => item.UpdatedAtUtcTicks)
            .ToListAsync();
        return rows;
    }

    private async Task UpdateStatusAsync(Guid? commandId, string status, string? resultMessage, CancellationToken cancellationToken)
    {
        if (commandId is null || commandId.Value == Guid.Empty)
        {
            return;
        }

        await database.InitializeAsync();
        var key = commandId.Value.ToString("N");
        var existing = await database.Connection.Table<RemoteCommandLogEntity>()
            .Where(item => item.CommandId == key)
            .FirstOrDefaultAsync();
        if (existing is null)
        {
            return;
        }

        existing.Status = status;
        existing.ResultMessage = resultMessage?.Trim() ?? string.Empty;
        existing.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        await database.Connection.InsertOrReplaceAsync(existing);
    }

    private string ComputePayloadHash(object? payload)
    {
        if (payload is null)
        {
            return string.Empty;
        }

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
