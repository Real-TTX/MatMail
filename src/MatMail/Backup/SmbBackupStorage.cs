using System.Net;
using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace MatMail.Backup;

/// <summary>Where and how to reach a share. The password is in the clear here (the stored one is decrypted by the factory).</summary>
public sealed record SmbTargetOptions(string Host, string Share, string Folder, string? Domain, string? Username, string? Password);

/// <summary>
/// A folder on an SMB share, reached by the program itself over SMB 2/3 (port 445) with SMBLibrary: no mount and no extra rights in the
/// container. A connection is made per operation. An upload is written under a name ending in <c>.partial</c> and renamed when it is
/// complete and has the size of the local file, so a backup that is not whole never has the name of one.
/// </summary>
public sealed class SmbBackupStorage(SmbTargetOptions options) : IBackupStorage
{
    private const int ResponseTimeoutMilliseconds = 60_000;
    private const int MaxChunkBytes = 1024 * 1024;

    public string? LocalFolder => null;

    public Task<StorageCheck> CheckAsync(CancellationToken cancel) => Task.Run(
        () =>
        {
            try
            {
                using Session session = Connect();
                EnsureFolder(session);

                string probe = Remote(".matmail-probe-" + Guid.NewGuid().ToString("N")[..8]);
                CheckStatus(session.Store.CreateFile(out object handle, out _, probe, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null), "write a test file");
                try
                {
                    session.Store.WriteFile(out _, handle, 0, "probe"u8.ToArray());
                    session.Store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
                }
                finally
                {
                    session.CloseQuietly(handle);
                }

                return new StorageCheck(true, $"Connected to {options.Host}, share “{options.Share}”, folder “{FolderPath()}”: files can be written.", FreeSpace(session));
            }
            catch (BackupStorageException ex)
            {
                return new StorageCheck(false, ex.Message, null);
            }
        },
        cancel);

    /// <summary>The shares of a server (without the administrative ones), for the choice in the target form.</summary>
    public static Task<IReadOnlyList<string>> ListSharesAsync(string host, string? domain, string? username, string? password, CancellationToken cancel = default)
        => Task.Run<IReadOnlyList<string>>(
            () =>
            {
                SMB2Client client = Login(new SmbTargetOptions(host, string.Empty, string.Empty, domain, username, password));
                try
                {
                    List<string>? shares = client.ListShares(out NTStatus status);
                    if (status != NTStatus.STATUS_SUCCESS || shares is null)
                    {
                        throw new BackupStorageException($"The shares of {host} cannot be listed: {Describe(status)}.");
                    }

                    return shares.Where(s => !s.EndsWith('$')).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                }
                finally
                {
                    Close(client);
                }
            },
            cancel);

    public Task<IReadOnlyList<RemoteBackupFile>> ListAsync(CancellationToken cancel) => Task.Run<IReadOnlyList<RemoteBackupFile>>(
        () =>
        {
            using Session session = Connect();
            string folder = FolderRemote();
            NTStatus opened = session.Store.CreateFile(out object handle, out _, folder, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            if (opened is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
            {
                return [];   // nothing has been written there yet
            }

            CheckStatus(opened, "open the folder");
            try
            {
                NTStatus status = session.Store.QueryDirectory(out List<QueryDirectoryFileInformation>? entries, handle, "*", FileInformationClass.FileDirectoryInformation);
                if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_NO_MORE_FILES))
                {
                    CheckStatus(status, "list the folder");
                }

                var files = new List<RemoteBackupFile>();
                foreach (FileDirectoryInformation entry in (entries ?? []).OfType<FileDirectoryInformation>())
                {
                    cancel.ThrowIfCancellationRequested();
                    if (entry.FileName is "." or ".." || entry.FileAttributes.HasFlag(FileAttributes.Directory))
                    {
                        continue;
                    }

                    files.Add(new RemoteBackupFile(entry.FileName, entry.EndOfFile, DateTime.SpecifyKind(entry.LastWriteTime, DateTimeKind.Utc)));
                }

                return files;
            }
            finally
            {
                session.CloseQuietly(handle);
            }
        },
        cancel);

    public Task UploadAsync(string localPath, string name, IProgress<long>? progress, CancellationToken cancel) => Task.Run(
        () =>
        {
            BackupStorageNames.Require(name);
            using Session session = Connect();
            EnsureFolder(session);

            string final = Remote(name);
            string partial = Remote(name + ".partial");
            if (Exists(session, final))
            {
                throw new BackupStorageException($"“{name}” exists already on the share.");
            }

            CheckStatus(
                session.Store.CreateFile(out object handle, out _, partial, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null),
                "create the file");

            bool complete = false;
            try
            {
                using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
                int chunk = (int)Math.Clamp(session.Client.MaxWriteSize, 4096, MaxChunkBytes);
                byte[] buffer = new byte[chunk];
                long offset = 0;
                int read;
                while ((read = input.Read(buffer, 0, chunk)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    offset = Write(session, handle, offset, buffer, read);
                    progress?.Report(offset);
                }

                session.Store.GetFileInformation(out FileInformation info, handle, FileInformationClass.FileStandardInformation);
                if (info is FileStandardInformation standard && standard.EndOfFile != input.Length)
                {
                    throw new BackupStorageException($"The share holds {standard.EndOfFile} bytes of “{name}”, the backup has {input.Length}: the transfer is not complete.");
                }

                // the same handle renames it: from now on the file has the name of a backup
                CheckStatus(session.Store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = final, ReplaceIfExists = false }), "name the file");
                complete = true;
            }
            finally
            {
                if (!complete)
                {
                    try
                    {
                        session.Store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
                    }
                    catch (Exception)
                    {
                        // the connection is gone with the file's handle: the share removes what is not closed
                    }
                }

                session.CloseQuietly(handle);
            }
        },
        cancel);

    /// <summary>Writes <paramref name="count"/> bytes of the buffer at the offset; a server may take less than it is given, so it goes on until all is written.</summary>
    private static long Write(Session session, object handle, long offset, byte[] buffer, int count)
    {
        int written = 0;
        while (written < count)
        {
            byte[] slice = new byte[count - written];
            Buffer.BlockCopy(buffer, written, slice, 0, slice.Length);
            CheckStatus(session.Store.WriteFile(out int bytes, handle, offset + written, slice), "write to the share");
            if (bytes <= 0)
            {
                throw new BackupStorageException("The share accepted no data.");
            }

            written += bytes;
        }

        return offset + count;
    }

    public Task DownloadAsync(string name, string localPath, IProgress<long>? progress, CancellationToken cancel) => Task.Run(
        () =>
        {
            BackupStorageNames.Require(name);
            using Session session = Connect();
            NTStatus opened = session.Store.CreateFile(out object handle, out _, Remote(name), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            if (opened is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
            {
                throw new BackupStorageException($"“{name}” is not on the share.");
            }

            CheckStatus(opened, "open the file");
            try
            {
                session.Store.GetFileInformation(out FileInformation info, handle, FileInformationClass.FileStandardInformation);
                long length = info is FileStandardInformation standard ? standard.EndOfFile : 0;

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
                using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024);
                int chunk = (int)Math.Clamp(session.Client.MaxReadSize, 4096, MaxChunkBytes);
                long offset = 0;
                while (offset < length)
                {
                    cancel.ThrowIfCancellationRequested();

                    // never ask for more than is there: some servers end the connection for that
                    int wanted = (int)Math.Min(chunk, length - offset);
                    NTStatus status = session.Store.ReadFile(out byte[]? data, handle, offset, wanted);
                    if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
                    {
                        break;
                    }

                    CheckStatus(status, "read from the share");
                    output.Write(data, 0, data.Length);
                    offset += data.Length;
                    progress?.Report(offset);
                }

                if (offset != length)
                {
                    throw new BackupStorageException($"Only {offset} of {length} bytes of “{name}” could be read.");
                }
            }
            finally
            {
                session.CloseQuietly(handle);
            }
        },
        cancel);

    public Task<Stream> OpenReadAsync(string name, CancellationToken cancel) => Task.Run<Stream>(
        () =>
        {
            BackupStorageNames.Require(name);
            Session session = Connect();
            try
            {
                NTStatus opened = session.Store.CreateFile(out object handle, out _, Remote(name), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                if (opened is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
                {
                    throw new BackupStorageException($"“{name}” is not on the share.");
                }

                CheckStatus(opened, "open the file");
                session.Store.GetFileInformation(out FileInformation info, handle, FileInformationClass.FileStandardInformation);
                long length = info is FileStandardInformation standard ? standard.EndOfFile : 0;
                return new SmbReadStream(session, handle, length, (int)Math.Clamp(session.Client.MaxReadSize, 4096, MaxChunkBytes));
            }
            catch
            {
                session.Dispose();
                throw;
            }
        },
        cancel);

    /// <summary>A file of the share read bit by bit; closing it closes the connection.</summary>
    private sealed class SmbReadStream(Session session, object handle, long length, int chunk) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // never ask for more than is there: some servers end the connection for that
            long remaining = length - _position;
            if (remaining <= 0 || count == 0)
            {
                return 0;
            }

            int wanted = (int)Math.Min(Math.Min(count, chunk), remaining);
            NTStatus status = session.Store.ReadFile(out byte[]? data, handle, _position, wanted);
            if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
            {
                return 0;
            }

            CheckStatus(status, "read from the share");
            Buffer.BlockCopy(data, 0, buffer, offset, data.Length);
            _position += data.Length;
            return data.Length;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                session.CloseQuietly(handle);
                session.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public Task DeleteAsync(string name, CancellationToken cancel) => Task.Run(
        () =>
        {
            BackupStorageNames.Require(name);
            using Session session = Connect();
            NTStatus opened = session.Store.CreateFile(out object handle, out _, Remote(name), AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            if (opened is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
            {
                return;   // gone already
            }

            CheckStatus(opened, "open the file");
            try
            {
                CheckStatus(session.Store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }), "delete the file");
            }
            finally
            {
                session.CloseQuietly(handle);
            }
        },
        cancel);

    public void Dispose()
    {
    }

    // ---------------------------------------------------------------------------------------------
    // Paths: inside the share with backslashes and no leading one
    // ---------------------------------------------------------------------------------------------

    private string FolderPath() => string.Join('\\', options.Folder.Split('/', '\\').Where(s => s.Length > 0));

    private string FolderRemote() => FolderPath();

    private string Remote(string name)
    {
        string folder = FolderPath();
        return folder.Length == 0 ? name : folder + "\\" + name;
    }

    private void EnsureFolder(Session session)
    {
        string current = string.Empty;
        foreach (string segment in FolderPath().Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new BackupStorageException("The folder must not contain “.” or “..”.");
            }

            current = current.Length == 0 ? segment : current + "\\" + segment;
            if (Exists(session, current))
            {
                continue;
            }

            CheckStatus(session.Store.CreateFile(out object handle, out _, current, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Directory, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, null), $"create the folder “{current}”");
            session.CloseQuietly(handle);
        }
    }

    private static bool Exists(Session session, string remote)
    {
        NTStatus status = session.Store.CreateFile(out object handle, out _, remote, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            return false;
        }

        session.CloseQuietly(handle);
        return true;
    }

    private static long? FreeSpace(Session session)
    {
        NTStatus status = session.Store.GetFileSystemInformation(out FileSystemInformation info, FileSystemInformationClass.FileFsFullSizeInformation);
        if (status != NTStatus.STATUS_SUCCESS || info is not FileFsFullSizeInformation size)
        {
            return null;
        }

        return size.CallerAvailableAllocationUnits * size.SectorsPerAllocationUnit * size.BytesPerSector;
    }

    // ---------------------------------------------------------------------------------------------
    // Connection
    // ---------------------------------------------------------------------------------------------

    private sealed class Session(SMB2Client client, ISMBFileStore store) : IDisposable
    {
        public SMB2Client Client { get; } = client;
        public ISMBFileStore Store { get; } = store;

        /// <summary>Closes a handle without failing on a connection that is gone (and the handle with it).</summary>
        public void CloseQuietly(object handle)
        {
            try
            {
                Store.CloseFile(handle);
            }
            catch (Exception)
            {
                // the connection is gone, so is the handle
            }
        }

        public void Dispose()
        {
            try
            {
                Store.Disconnect();
            }
            catch (Exception)
            {
                // the connection is at its end anyway
            }

            SmbBackupStorage.Close(Client);
        }
    }

    private Session Connect()
    {
        if (string.IsNullOrWhiteSpace(options.Share))
        {
            throw new BackupStorageException("The share is missing.");
        }

        SMB2Client client = Login(options);
        ISMBFileStore? store = client.TreeConnect(options.Share, out NTStatus status);
        if (status != NTStatus.STATUS_SUCCESS || store is null)
        {
            Close(client);
            throw new BackupStorageException($"The share “{options.Share}” on {options.Host} cannot be opened: {Describe(status)}.");
        }

        return new Session(client, store);
    }

    private static SMB2Client Login(SmbTargetOptions target)
    {
        if (string.IsNullOrWhiteSpace(target.Host))
        {
            throw new BackupStorageException("The server is missing.");
        }

        IPAddress address;
        try
        {
            address = IPAddress.TryParse(target.Host, out IPAddress? parsed)
                ? parsed
                : Dns.GetHostAddresses(target.Host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                  ?? throw new BackupStorageException($"The server {target.Host} is not known.");
        }
        catch (SocketException ex)
        {
            throw new BackupStorageException($"The server {target.Host} is not known: {ex.Message}", ex);
        }

        var client = new SMB2Client(ResponseTimeoutMilliseconds);
        bool connected;
        try
        {
            connected = client.Connect(address, SMBTransportType.DirectTCPTransport);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
            throw new BackupStorageException($"No connection to {target.Host} (SMB, port 445): {ex.Message}", ex);
        }

        if (!connected)
        {
            throw new BackupStorageException($"No connection to {target.Host} (SMB, port 445): is the server on, and does it offer SMB 2 or 3?");
        }

        NTStatus login = client.Login(target.Domain ?? string.Empty, target.Username ?? string.Empty, target.Password ?? string.Empty);
        if (login != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new BackupStorageException($"The sign-in to {target.Host} failed: {Describe(login)}.");
        }

        return client;
    }

    private static void Close(SMB2Client client)
    {
        try
        {
            client.Logoff();
        }
        catch (Exception)
        {
            // the connection is at its end anyway
        }

        try
        {
            client.Disconnect();
        }
        catch (Exception)
        {
            // dito
        }
    }

    private static void CheckStatus(NTStatus status, string doing)
    {
        if (status is NTStatus.STATUS_SUCCESS or NTStatus.STATUS_END_OF_FILE)
        {
            return;
        }

        throw new BackupStorageException($"The share could not {doing}: {Describe(status)}.");
    }

    private static string Describe(NTStatus status) => status switch
    {
        NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_WRONG_PASSWORD => "the user name or the password is wrong",
        NTStatus.STATUS_ACCOUNT_DISABLED => "the account is disabled",
        NTStatus.STATUS_ACCOUNT_LOCKED_OUT => "the account is locked",
        NTStatus.STATUS_PASSWORD_EXPIRED => "the password has expired",
        NTStatus.STATUS_ACCESS_DENIED => "access denied: the user may not do that there",
        NTStatus.STATUS_BAD_NETWORK_NAME => "there is no share of that name",
        NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE => "not found",
        NTStatus.STATUS_OBJECT_NAME_COLLISION => "it exists already",
        NTStatus.STATUS_DISK_FULL => "the share is full",
        NTStatus.STATUS_SHARING_VIOLATION => "the file is in use",
        NTStatus.STATUS_NOT_SUPPORTED => "the server does not support that",
        _ => status.ToString(),
    };
}
