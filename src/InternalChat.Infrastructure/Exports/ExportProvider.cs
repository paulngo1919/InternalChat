using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.Infrastructure.Exports;

internal sealed class ExportProvider : IExportProvider
{
    private readonly ChatDbContext _dbContext;

    public ExportProvider(ChatDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Stream> ExportConversationAsync(Guid conversationId, DateOnly? fromDate, DateOnly? toDate, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId);

        if (fromDate.HasValue)
        {
            var fromDto = new DateTimeOffset(fromDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(m => m.SentAt >= fromDto);
        }
        if (toDate.HasValue)
        {
            var toDto = new DateTimeOffset(toDate.Value.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
            query = query.Where(m => m.SentAt <= toDto);
        }

        var messages = await query.OrderBy(m => m.SentAt).ToListAsync(cancellationToken).ConfigureAwait(false);

        var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return stream;
    }

    public async Task<Stream> ExportEmployeeAsync(Guid employeeId, DateOnly? fromDate, DateOnly? toDate, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Messages.AsNoTracking().Where(m => m.AuthorId == employeeId);

        if (fromDate.HasValue)
        {
            var fromDto = new DateTimeOffset(fromDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(m => m.SentAt >= fromDto);
        }
        if (toDate.HasValue)
        {
            var toDto = new DateTimeOffset(toDate.Value.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
            query = query.Where(m => m.SentAt <= toDto);
        }

        var messages = await query.OrderBy(m => m.SentAt).ToListAsync(cancellationToken).ConfigureAwait(false);

        var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return stream;
    }
}
