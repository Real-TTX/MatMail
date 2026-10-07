using System.Text.Json;

namespace MatMail.Messaging;

public sealed record StagedAttachment(string Id, string FileName, string ContentType, long Size);

/// <summary>
/// Attachments of a message that is still being written: uploaded right away (like in Gmail), kept in the data volume for a day,
/// and picked up when the message is sent or saved as a draft. Every file belongs to the user who uploaded it.
/// </summary>
public sealed class AttachmentStaging
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly string _root;

    public AttachmentStaging() : this(Path.Combine(AppInfo.DataDir, "tmp", "attachments"))
    {
    }

    public AttachmentStaging(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<StagedAttachment> SaveAsync(long userId, string fileName, string contentType, Stream content, long maxBytes, CancellationToken cancel = default)
    {
        string id = Guid.NewGuid().ToString("N");
        string directory = UserDirectory(userId);
        Directory.CreateDirectory(directory);
        string dataPath = Path.Combine(directory, id + ".bin");

        long size;
        await using (FileStream file = File.Create(dataPath))
        {
            await content.CopyToAsync(file, cancel);
            size = file.Length;
        }

        if (size > maxBytes)
        {
            File.Delete(dataPath);
            throw new InvalidOperationException("The attachment is too large.");
        }

        string safeName = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName);
        var info = new StagedAttachment(id, safeName, string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, size);
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(info), cancel);
        return info;
    }

    public StagedAttachment? Find(long userId, string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        string metaPath = Path.Combine(UserDirectory(userId), id + ".json");
        return File.Exists(metaPath) ? JsonSerializer.Deserialize<StagedAttachment>(File.ReadAllText(metaPath)) : null;
    }

    public Stream? OpenRead(long userId, string id)
    {
        string path = Path.Combine(UserDirectory(userId), id + ".bin");
        return IsValidId(id) && File.Exists(path) ? File.OpenRead(path) : null;
    }

    public void Delete(long userId, string id)
    {
        if (!IsValidId(id))
        {
            return;
        }

        string directory = UserDirectory(userId);
        TryDelete(Path.Combine(directory, id + ".bin"));
        TryDelete(Path.Combine(directory, id + ".json"));
    }

    /// <summary>Removes uploads nobody picked up (called by the maintenance service).</summary>
    public int CleanUp()
    {
        int removed = 0;
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > Lifetime)
            {
                TryDelete(file);
                removed++;
            }
        }

        return removed;
    }

    private string UserDirectory(long userId) => Path.Combine(_root, userId.ToString());

    private static bool IsValidId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleaned up with the next sweep.
        }
    }
}
