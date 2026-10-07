using MatMail.Messaging;
using Microsoft.EntityFrameworkCore;

namespace MatMail.MailServer.Imap;

/// <summary>
/// FETCH / UID FETCH. Flags and UIDs come from the snapshot, dates and sizes from the message rows, header items and ENVELOPE from the
/// stored header block; only body items load the message itself. ENVELOPE and BODYSTRUCTURE are cached in the content row.
/// </summary>
internal sealed partial class ImapSession
{
    private const int FetchBatchSize = 100;

    private async Task FetchAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        ImapSelection selection = RequireSelection();
        parser.ExpectSpace();
        SequenceSet set = parser.ReadSequenceSet();
        parser.ExpectSpace();
        ImapFetchRequest request = ImapFetchRequest.Parse(parser, command.IsUid);
        parser.ExpectEnd();

        List<int> indexes = command.IsUid ? selection.ResolveUidSet(set) : selection.ResolveSequenceSet(set);
        var outcome = new FetchOutcome();

        await using ImapWork work = OpenWork();
        foreach (int[] batch in indexes.Chunk(FetchBatchSize))
        {
            await FetchBatchAsync(work, selection, batch, request, outcome);
            if (_connection.PendingOutput >= ImapConnection.FlushThreshold)
            {
                await _connection.FlushAsync(_shutdown);
            }
        }

        if (outcome.Unavailable)
        {
            await SynchronizeAsync(work, command.IsUid);
            Tagged(command, "NO", "[UNAVAILABLE] Some message contents could not be loaded");
            return;
        }

        string prefix = outcome.Expunged ? "[EXPUNGEISSUED] Some messages were expunged; " : string.Empty;
        await CompleteAsync(command, prefix + command.DisplayName + " completed", work, allowExpunge: command.IsUid);
    }

    private async Task FetchBatchAsync(ImapWork work, ImapSelection selection, int[] batch, ImapFetchRequest request, FetchOutcome outcome)
    {
        List<ImapMessage> messages = batch.Select(i => selection.Messages[i]).Where(m => !m.IsExpunged).ToList();
        outcome.Expunged |= messages.Count < batch.Length;
        if (messages.Count == 0)
        {
            return;
        }

        long[] ids = messages.Select(m => m.Id).ToArray();
        Dictionary<long, FetchMetadata> metadata = request.NeedsMetadata ? await LoadMetadataAsync(work, ids) : new();
        Dictionary<long, FetchContent> contents = await LoadContentsAsync(work, ids, request);
        HashSet<long> newlySeen = request.SetsSeen && !selection.IsReadOnly ? await MarkSeenAsync(work, selection, messages) : new();

        var cacheUpdates = new FetchCacheUpdates();
        foreach (ImapMessage message in messages)
        {
            if (request.NeedsMetadata && !metadata.ContainsKey(message.Id))
            {
                // Removed in the meantime; the next synchronisation reports it.
                outcome.Expunged = true;
                continue;
            }

            var context = new FetchContext(work, message, metadata.GetValueOrDefault(message.Id), contents.GetValueOrDefault(message.Id), cacheUpdates, outcome);
            List<FetchValue> values = await BuildFetchValuesAsync(request, context);
            if (newlySeen.Contains(message.Id) && !request.Has(ImapFetchItemKind.Flags))
            {
                values.Add(new FetchValue("FLAGS " + ImapFlagNames.Format(message.Flags, message.Keywords)));
            }

            if (values.Count > 0)
            {
                await WriteFetchResponseAsync(selection.IndexOfUid(message.Uid) + 1, values);
            }
        }

        await cacheUpdates.SaveAsync(work, _context.Logger, _shutdown);
    }

    private async Task<List<FetchValue>> BuildFetchValuesAsync(ImapFetchRequest request, FetchContext context)
    {
        var values = new List<FetchValue>(request.Items.Count);
        foreach (ImapFetchItem item in request.Items)
        {
            FetchValue? value = item.Kind switch
            {
                ImapFetchItemKind.Uid => new FetchValue("UID " + context.Message.Uid),
                ImapFetchItemKind.Flags => new FetchValue("FLAGS " + ImapFlagNames.Format(context.Message.Flags, context.Message.Keywords)),
                ImapFetchItemKind.InternalDate => new FetchValue("INTERNALDATE " + ImapFormat.InternalDate(context.Metadata!.ReceivedDate)),
                ImapFetchItemKind.Size => new FetchValue("RFC822.SIZE " + context.Metadata!.Size),
                ImapFetchItemKind.Envelope => new FetchValue("ENVELOPE " + Envelope(context)),
                ImapFetchItemKind.BodyStructure => await BodyStructureAsync(context, extensible: true),
                ImapFetchItemKind.Body => await BodyStructureAsync(context, extensible: false),
                _ => await BodyDataAsync(item, context),
            };

            if (value is not null)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string Envelope(FetchContext context)
    {
        if (context.Content?.Envelope is { } cached)
        {
            return cached;
        }

        string envelope = ImapEnvelope.Build(context.Content?.HeaderBytes);
        context.CacheUpdates.Envelopes.Add((context.Message.Id, envelope));
        return envelope;
    }

    private async Task<FetchValue?> BodyStructureAsync(FetchContext context, bool extensible)
    {
        string name = extensible ? "BODYSTRUCTURE " : "BODY ";
        if (extensible && context.Content?.BodyStructure is { } cached)
        {
            return new FetchValue(name + cached);
        }

        ImapMessageStructure? structure = await LoadStructureAsync(context);
        if (structure is null)
        {
            return null;
        }

        string text = ImapBodyStructure.Build(structure, extensible);
        if (extensible)
        {
            context.CacheUpdates.BodyStructures.Add((context.Message.Id, text));
        }

        return new FetchValue(name + text);
    }

    /// <summary>BODY[...], BODY.PEEK[...], RFC822, RFC822.HEADER and RFC822.TEXT.</summary>
    private async Task<FetchValue?> BodyDataAsync(ImapFetchItem item, FetchContext context)
    {
        ReadOnlyMemory<byte>? data;
        if (item.NeedsHeader && context.Content?.HeaderBytes is { } header)
        {
            data = item.Kind == ImapFetchItemKind.Rfc822Header || item.Section!.Kind == ImapSectionKind.Header
                ? header
                : ImapMessageStructure.FilterHeader(header, item.Section.Fields, item.Section.Kind == ImapSectionKind.HeaderFields);
        }
        else
        {
            ImapMessageStructure? structure = await LoadStructureAsync(context);
            if (structure is null)
            {
                return null;
            }

            data = item.Kind switch
            {
                ImapFetchItemKind.Rfc822 => structure.Raw,
                ImapFetchItemKind.Rfc822Header => structure.GetSection(new ImapSection(Array.Empty<int>(), ImapSectionKind.Header, Array.Empty<string>())),
                ImapFetchItemKind.Rfc822Text => structure.GetSection(new ImapSection(Array.Empty<int>(), ImapSectionKind.Text, Array.Empty<string>())),
                _ => structure.GetSection(item.Section!),
            };
        }

        if (data is not { } bytes)
        {
            return new FetchValue(item.ResponseName + " NIL");
        }

        if (item.Origin is long origin)
        {
            int start = (int)Math.Min(origin, bytes.Length);
            int length = (int)Math.Min(item.Count ?? long.MaxValue, bytes.Length - start);
            bytes = bytes.Slice(start, length);
        }

        return new FetchValue(item.ResponseName + " ", bytes);
    }

    private async Task<ImapMessageStructure?> LoadStructureAsync(FetchContext context)
    {
        byte[]? raw = await _messageCache.GetRawAsync(context.Work, context.Message.Id, _shutdown);
        if (raw is null)
        {
            context.Outcome.Unavailable = true;
            return null;
        }

        return _messageCache.GetStructure(context.Message.Id, raw);
    }

    private async Task WriteFetchResponseAsync(int sequence, List<FetchValue> values)
    {
        _connection.Write($"* {sequence} FETCH (");
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                _connection.Write(" ");
            }

            FetchValue value = values[i];
            _connection.Write(value.Text);
            if (value.Literal is { } literal)
            {
                _connection.Write("{" + literal.Length + "}\r\n");
                await _connection.WriteAsync(literal, _shutdown);
            }
        }

        _connection.Write(")\r\n");
    }

    /// <summary>Sets \Seen on the messages that do not have it yet (fetching a body without PEEK) and returns their ids.</summary>
    private async Task<HashSet<long>> MarkSeenAsync(ImapWork work, ImapSelection selection, List<ImapMessage> messages)
    {
        List<ImapMessage> unseen = messages.Where(m => (m.Flags & ImapFlags.Seen) == 0).ToList();
        if (unseen.Count == 0)
        {
            return new HashSet<long>();
        }

        await work.Store.ChangeFlagsAsync(unseen.Select(m => m.Id), new FlagChange { IsRead = true }, _shutdown);
        foreach (ImapMessage message in unseen)
        {
            message.Flags |= ImapFlags.Seen;
            selection.ChangedFlags.Remove(message);
        }

        return unseen.Select(m => m.Id).ToHashSet();
    }

    private async Task<Dictionary<long, FetchMetadata>> LoadMetadataAsync(ImapWork work, long[] ids)
        => await work.Db.MailMessages.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new FetchMetadata(m.Id, m.ReceivedDate, m.SizeBytes))
            .ToDictionaryAsync(m => m.Id, _shutdown);

    /// <summary>The cached ENVELOPE/BODYSTRUCTURE strings and, where needed, the header block — never the raw message.</summary>
    private async Task<Dictionary<long, FetchContent>> LoadContentsAsync(ImapWork work, long[] ids, ImapFetchRequest request)
    {
        bool envelope = request.Has(ImapFetchItemKind.Envelope);
        bool bodyStructure = request.Has(ImapFetchItemKind.BodyStructure);
        bool header = request.NeedsHeader;
        if (!envelope && !bodyStructure && !header)
        {
            return new Dictionary<long, FetchContent>();
        }

        return await work.Db.MailMessageContents.AsNoTracking()
            .Where(c => ids.Contains(c.MessageId))
            .Select(c => new FetchContent(
                c.MessageId,
                envelope ? c.EnvelopeImap : null,
                bodyStructure ? c.BodyStructureImap : null,
                header || (envelope && c.EnvelopeImap == null) ? c.HeaderBytes : null))
            .ToDictionaryAsync(c => c.MessageId, _shutdown);
    }

    /// <summary>One data item of a FETCH response: text, optionally followed by a literal.</summary>
    private sealed record FetchValue(string Text, ReadOnlyMemory<byte>? Literal = null);

    private sealed record FetchMetadata(long Id, DateTime ReceivedDate, long Size);

    private sealed record FetchContent(long MessageId, string? Envelope, string? BodyStructure, byte[]? HeaderBytes);

    private sealed record FetchContext(ImapWork Work, ImapMessage Message, FetchMetadata? Metadata, FetchContent? Content, FetchCacheUpdates CacheUpdates, FetchOutcome Outcome);

    private sealed class FetchOutcome
    {
        public bool Expunged { get; set; }

        public bool Unavailable { get; set; }
    }

    /// <summary>ENVELOPE / BODYSTRUCTURE strings computed during a batch, written back to the content rows in one statement each.</summary>
    private sealed class FetchCacheUpdates
    {
        public List<(long Id, string Value)> Envelopes { get; } = new();

        public List<(long Id, string Value)> BodyStructures { get; } = new();

        public async Task SaveAsync(ImapWork work, ILogger logger, CancellationToken cancel)
        {
            try
            {
                if (Envelopes.Count > 0)
                {
                    long[] ids = Envelopes.Select(e => e.Id).ToArray();
                    string[] values = Envelopes.Select(e => e.Value).ToArray();
                    await work.Db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"MailMessageContent\" AS c SET \"EnvelopeImap\" = v.value FROM unnest({ids}, {values}) AS v(id, value) WHERE c.\"MessageId\" = v.id AND c.\"EnvelopeImap\" IS NULL",
                        cancel);
                }

                if (BodyStructures.Count > 0)
                {
                    long[] ids = BodyStructures.Select(e => e.Id).ToArray();
                    string[] values = BodyStructures.Select(e => e.Value).ToArray();
                    await work.Db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"MailMessageContent\" AS c SET \"BodyStructureImap\" = v.value FROM unnest({ids}, {values}) AS v(id, value) WHERE c.\"MessageId\" = v.id AND c.\"BodyStructureImap\" IS NULL",
                        cancel);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Only a cache: the values are computed again next time.
                logger.LogWarning(ex, "The IMAP envelope/body structure cache could not be written.");
            }
        }
    }
}
