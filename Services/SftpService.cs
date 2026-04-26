using Renci.SshNet;
using Renci.SshNet.Sftp;
using InfraSftp.Models;

namespace InfraSftp.Services;

/// <summary>
/// Wraps SSH.NET to provide SFTP operations: connect, list, upload, download, delete.
/// Each instance manages one SSH session.
/// </summary>
public class SftpService : IDisposable
{
    private SftpClient? _sftp;
    private SshClient? _ssh;
    private readonly KnownHostsService _knownHosts;

    // Host-key verification scratch state, written by OnHostKeyReceived inside
    // SSH.NET's handshake thread and read by ConnectAsync after Connect() throws.
    // We don't try to decide host-key trust through async user prompts; instead
    // we fail the first connect, surface a typed exception, and let the caller
    // re-attempt with acceptNewHostKey=true once the user has consented.
    private string? _connectingHost;
    private int _connectingPort;
    private bool _acceptNewHostKey;
    private string? _receivedFingerprint;
    private string? _receivedAlgorithm;
    private string? _storedFingerprint;
    private bool _hostKeyRejected;

    public bool IsConnected => _sftp?.IsConnected == true;
    public string? ConnectionId { get; private set; }
    public string WorkingDirectory => _sftp?.WorkingDirectory ?? "/";

    // Skip-decision shared by every recursive transfer flavour. The VM points
    // this at AppSettings.ForceTransfer (re-evaluated on every call so the
    // user can toggle it mid-session). When the flag is true, the heuristic
    // returns false unconditionally and every file is re-transferred.
    public Func<bool> ForceTransferProvider { get; set; } = () => false;

    // rsync-default tolerance for mtime comparisons. Absorbs filesystem
    // timestamp-resolution mismatches (FAT 2s, NTFS ~100ns, ext4 1ns).
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    public event Action<string>? OnLog;
    public event Action<string, long, long>? OnTransferProgress; // fileName, transferred, total
    // Fires when SSH.NET reports an error or the session drops. The VM uses
    // this to flip the tab's IsConnected to false so the UI reflects the drop.
    public event Action? OnDisconnected;

    // True ⇒ destination is up-to-date, the file should be skipped.
    // False ⇒ transfer it.
    //
    // rsync-style: same size AND mtime within ±2s.
    // Tolerance is essential because storing a file across filesystems with
    // different timestamp resolutions otherwise produces a perpetual "needs
    // transfer" verdict (NTFS→ext4 round-trips can shave sub-second precision).
    //
    // Public so the same logic can be exercised from unit tests.
    public bool ShouldSkipTransfer(long srcSize, DateTime srcMtime,
                                   long dstSize, DateTime dstMtime)
    {
        if (ForceTransferProvider()) return false;
        if (srcSize != dstSize) return false;

        // mtime == default sentinel means "unknown" — fall back to size-only
        // so platforms that don't expose mtime (very rare) still benefit from
        // the skip.
        if (srcMtime == default || dstMtime == default) return true;

        var delta = (srcMtime - dstMtime).Duration();
        return delta <= MtimeTolerance;
    }

    public SftpService(KnownHostsService knownHosts)
    {
        _knownHosts = knownHosts;
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    // ── Connect ────────────────────────────────────────────────
    public Task ConnectAsync(string host, int port, string user, string password,
        bool acceptNewHostKey = false)
    {
        var auth = new PasswordAuthenticationMethod(user, password);
        return ConnectInternalAsync(host, port, user, auth, acceptNewHostKey);
    }

    /// <summary>
    /// Connects using an OpenSSH-format private key file. <paramref name="passphrase"/>
    /// may be null/empty for unprotected keys. Throws SshPassPhraseNullOrEmptyException
    /// when the key requires a passphrase that wasn't provided — callers should catch
    /// that and re-prompt the user.
    /// </summary>
    public Task ConnectWithKeyAsync(string host, int port, string user,
        string privateKeyPath, string? passphrase, bool acceptNewHostKey = false)
    {
        if (!File.Exists(privateKeyPath))
            throw new FileNotFoundException($"Private key not found: {privateKeyPath}", privateKeyPath);

        // PrivateKeyFile loads + decrypts the key in-memory; if the key is
        // passphrase-protected and we don't have one, this throws and the
        // VM converts it into a passphrase prompt.
        var pkf = string.IsNullOrEmpty(passphrase)
            ? new PrivateKeyFile(privateKeyPath)
            : new PrivateKeyFile(privateKeyPath, passphrase);

        var auth = new PrivateKeyAuthenticationMethod(user, pkf);
        return ConnectInternalAsync(host, port, user, auth, acceptNewHostKey);
    }

    private async Task ConnectInternalAsync(string host, int port, string user,
        AuthenticationMethod auth, bool acceptNewHostKey)
    {
        _connectingHost = host;
        _connectingPort = port;
        _acceptNewHostKey = acceptNewHostKey;
        _receivedFingerprint = null;
        _receivedAlgorithm = null;
        _storedFingerprint = null;
        _hostKeyRejected = false;

        try
        {
            await Task.Run(() =>
            {
                var connInfo = new ConnectionInfo(host, port, user, auth);
                connInfo.Timeout = TimeSpan.FromSeconds(15);

                _ssh = new SshClient(connInfo);
                _ssh.HostKeyReceived += OnHostKeyReceived;
                _ssh.Connect();
                // KeepAlive pings keep NAT / firewall entries alive so idle
                // sessions don't get silently reaped by the middle boxes.
                _ssh.KeepAliveInterval = TimeSpan.FromSeconds(30);
                _ssh.ErrorOccurred += OnSshError;

                _sftp = new SftpClient(connInfo);
                _sftp.HostKeyReceived += OnHostKeyReceived;
                _sftp.Connect();
                _sftp.KeepAliveInterval = TimeSpan.FromSeconds(30);
                _sftp.ErrorOccurred += OnSshError;

                ConnectionId = $"{user}@{host}:{port}";
                Log($"✓ Connected to {ConnectionId}");
            });
        }
        catch (Exception)
        {
            // If Connect() failed because we explicitly rejected the host key,
            // surface a typed exception so the VM can show the right modal.
            // Otherwise (auth failure, network, etc.) propagate the original.
            if (_hostKeyRejected)
            {
                // Best-effort cleanup: SSH.NET may leave half-initialised clients.
                try { _sftp?.Dispose(); } catch { }
                try { _ssh?.Dispose(); } catch { }
                _sftp = null; _ssh = null;

                if (_storedFingerprint != null && _receivedFingerprint != null)
                    throw new HostKeyMismatchException(host, port,
                        _receivedAlgorithm ?? "", _receivedFingerprint, _storedFingerprint);

                if (_receivedFingerprint != null)
                    throw new UnknownHostKeyException(host, port,
                        _receivedAlgorithm ?? "", _receivedFingerprint);
            }
            throw;
        }
    }

    // SSH.NET fires this on its handshake thread for both the SshClient and the
    // SftpClient. We make a synchronous trust decision against the local store;
    // any user interaction happens between connect attempts, never inside this
    // handler (blocking it would stall the SSH handshake until timeout).
    private void OnHostKeyReceived(object? sender, Renci.SshNet.Common.HostKeyEventArgs e)
    {
        var fp = KnownHostsService.ComputeSha256Fingerprint(e.HostKey);
        _receivedFingerprint = fp;
        _receivedAlgorithm = e.HostKeyName;

        var stored = _knownHosts.Lookup(_connectingHost!, _connectingPort);
        if (stored == null)
        {
            if (_acceptNewHostKey)
            {
                _knownHosts.Trust(_connectingHost!, _connectingPort, e.HostKeyName, fp);
                e.CanTrust = true;
                Log($"🔑 Host key trusted (TOFU): {_connectingHost}:{_connectingPort} SHA256:{fp}");
            }
            else
            {
                _hostKeyRejected = true;
                e.CanTrust = false;
            }
        }
        else if (!string.Equals(stored.FingerprintSha256, fp, StringComparison.Ordinal))
        {
            _storedFingerprint = stored.FingerprintSha256;
            _hostKeyRejected = true;
            e.CanTrust = false;
        }
        else
        {
            e.CanTrust = true;
        }
    }

    /// <summary>
    /// Replace the trusted host key for host:port. Used after the user explicitly
    /// confirms a fingerprint change in the mismatch modal. Caller is responsible
    /// for re-issuing ConnectAsync afterwards.
    /// </summary>
    public void TrustHostKey(string host, int port, string algorithm, string fingerprintSha256)
        => _knownHosts.Trust(host, port, algorithm, fingerprintSha256);

    private void OnSshError(object? sender, Renci.SshNet.Common.ExceptionEventArgs e)
    {
        Log($"⚠ Session error: {e.Exception.Message}");
        OnDisconnected?.Invoke();
    }

    public void Disconnect()
    {
        _sftp?.Disconnect();
        _sftp?.Dispose();
        _ssh?.Disconnect();
        _ssh?.Dispose();
        _sftp = null;
        _ssh = null;
        Log("Disconnected.");
    }

    // ── List Directory ─────────────────────────────────────────
    public async Task<List<FileNode>> ListDirectoryAsync(string path)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");

        return await Task.Run(() =>
        {
            var entries = _sftp.ListDirectory(path);
            var nodes = new List<FileNode>();

            foreach (var e in entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name))
            {
                if (e.Name == ".") continue;
                nodes.Add(new FileNode
                {
                    Name = e.Name,
                    IsDirectory = e.IsDirectory,
                    Size = e.IsDirectory ? 0 : e.Length,
                    LastModified = e.LastWriteTime
                });
            }
            return nodes;
        });
    }

    // ── Upload Single File ─────────────────────────────────────
    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");

        await Task.Run(() =>
        {
            using var fs = File.OpenRead(localPath);
            var total = fs.Length;
            var fileName = Path.GetFileName(localPath);

            _sftp.UploadFile(fs, remotePath, (uploaded) =>
            {
                OnTransferProgress?.Invoke(fileName, (long)uploaded, total);
            });
        }, ct);
    }

    // ── Download Single File ───────────────────────────────────
    public async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");

        await Task.Run(() =>
        {
            var remoteFile = _sftp.Get(remotePath);
            var total = remoteFile.Length;
            var fileName = Path.GetFileName(remotePath);

            using var fs = File.Create(localPath);
            _sftp.DownloadFile(remotePath, fs, (downloaded) =>
            {
                OnTransferProgress?.Invoke(fileName, (long)downloaded, total);
            });
        }, ct);
    }

    // ── Recursive Transfer (rsync-like) ────────────────────────
    public async Task TransferRecursiveAsync(
        string sourcePath, string destPath,
        bool isUpload, // true = local→remote, false = remote→local
        Action<string, TransferStatus>? onFileStatus = null,
        CancellationToken ct = default)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");

        await Task.Run(() =>
        {
            if (isUpload)
                UploadRecursive(sourcePath, destPath, onFileStatus, ct);
            else
                DownloadRecursive(sourcePath, destPath, onFileStatus, ct);
        }, ct);
    }

    private void UploadRecursive(string localDir, string remoteDir,
        Action<string, TransferStatus>? onStatus, CancellationToken ct)
    {
        // Ensure remote directory exists
        try { _sftp!.CreateDirectory(remoteDir); }
        catch { /* already exists */ }

        foreach (var file in Directory.GetFiles(localDir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var remotePath = $"{remoteDir}/{name}";
            var localInfo = new FileInfo(file);

            // rsync-like: skip if same size AND mtime within tolerance.
            try
            {
                var remoteFile = _sftp!.Get(remotePath);
                if (!remoteFile.IsDirectory && ShouldSkipTransfer(
                        localInfo.Length, localInfo.LastWriteTime,
                        remoteFile.Length, remoteFile.LastWriteTime))
                {
                    onStatus?.Invoke(name, TransferStatus.Skipped);
                    Log($"  ⏭ {name} (up-to-date)");
                    continue;
                }
            }
            catch { /* file doesn't exist remotely, proceed */ }

            onStatus?.Invoke(name, TransferStatus.InProgress);
            using var fs = File.OpenRead(file);
            var total = fs.Length;
            _sftp!.UploadFile(fs, remotePath, (uploaded) =>
                OnTransferProgress?.Invoke(name, (long)uploaded, total));
            // Preserve mtime so the next pass can short-circuit. SSH.NET's
            // SetLastWriteTime is a wrapper over the SFTP SETSTAT op; some
            // servers reject it for non-owners — swallow silently.
            try { _sftp.SetLastWriteTime(remotePath, localInfo.LastWriteTime); }
            catch { /* server denied SETSTAT, not fatal */ }
            onStatus?.Invoke(name, TransferStatus.Completed);
            Log($"  ✓ {name}");
        }

        foreach (var dir in Directory.GetDirectories(localDir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            UploadRecursive(dir, $"{remoteDir}/{name}", onStatus, ct);
        }
    }

    private void DownloadRecursive(string remoteDir, string localDir,
        Action<string, TransferStatus>? onStatus, CancellationToken ct)
    {
        Directory.CreateDirectory(localDir);

        foreach (var entry in _sftp!.ListDirectory(remoteDir))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name == "." || entry.Name == "..") continue;

            var remotePath = $"{remoteDir}/{entry.Name}";
            var localPath = Path.Combine(localDir, entry.Name);

            if (entry.IsDirectory)
            {
                DownloadRecursive(remotePath, localPath, onStatus, ct);
            }
            else
            {
                // rsync-like: skip if same size AND mtime within tolerance.
                if (File.Exists(localPath))
                {
                    var localInfo = new FileInfo(localPath);
                    if (ShouldSkipTransfer(
                            entry.Length, entry.LastWriteTime,
                            localInfo.Length, localInfo.LastWriteTime))
                    {
                        onStatus?.Invoke(entry.Name, TransferStatus.Skipped);
                        Log($"  ⏭ {entry.Name} (up-to-date)");
                        continue;
                    }
                }

                onStatus?.Invoke(entry.Name, TransferStatus.InProgress);
                using (var fs = File.Create(localPath))
                {
                    _sftp.DownloadFile(remotePath, fs, (downloaded) =>
                        OnTransferProgress?.Invoke(entry.Name, (long)downloaded, entry.Length));
                }
                // Stamp local mtime to match remote so the next pass can
                // short-circuit. Best-effort: AV scanners sometimes hold the
                // file briefly post-create, so swallow.
                try { File.SetLastWriteTime(localPath, entry.LastWriteTime); }
                catch { /* not fatal */ }
                onStatus?.Invoke(entry.Name, TransferStatus.Completed);
                Log($"  ✓ {entry.Name}");
            }
        }
    }

    // ── Remote → Remote (stream pipeline) ──────────────────────
    // Streams bytes from this service's SFTP session directly into another
    // service's SFTP session without buffering the full file locally. The
    // client acts as a pass-through: UploadFile reads the source SftpFileStream
    // chunk-by-chunk and writes each chunk to the destination as it arrives.
    // Memory footprint stays bounded to SSH.NET's internal read/write buffers.
    public async Task PipeFileToRemoteAsync(
        string srcPath, SftpService dest, string dstPath,
        CancellationToken ct = default)
    {
        if (_sftp == null) throw new InvalidOperationException("Source not connected");
        if (dest._sftp == null) throw new InvalidOperationException("Destination not connected");

        await Task.Run(() =>
        {
            var name = Path.GetFileName(srcPath.TrimEnd('/'));
            long total;
            try { total = _sftp.Get(srcPath).Length; }
            catch { total = 0; }

            ct.ThrowIfCancellationRequested();
            using var src = _sftp.OpenRead(srcPath);
            try
            {
                dest._sftp.UploadFile(src, dstPath, uploaded =>
                    OnTransferProgress?.Invoke(name, (long)uploaded, total));
            }
            catch
            {
                // Best-effort cleanup of a partial destination file so a retry
                // doesn't see half-written bytes as "already there".
                try { dest._sftp.DeleteFile(dstPath); } catch { /* ignore */ }
                throw;
            }
        }, ct);
    }

    // Recursive remote → remote folder transfer. Mirrors the size-skip rule
    // used by UploadRecursive / DownloadRecursive so behaviour is consistent
    // across all three flavours of recursive transfer.
    public async Task PipeDirectoryToRemoteAsync(
        string srcDir, SftpService dest, string dstDir,
        Action<string, TransferStatus>? onStatus = null,
        CancellationToken ct = default)
    {
        if (_sftp == null) throw new InvalidOperationException("Source not connected");
        if (dest._sftp == null) throw new InvalidOperationException("Destination not connected");

        await Task.Run(() => PipeDirRecursive(srcDir, dest, dstDir, onStatus, ct), ct);
    }

    private void PipeDirRecursive(string srcDir, SftpService dest, string dstDir,
        Action<string, TransferStatus>? onStatus, CancellationToken ct)
    {
        try { dest._sftp!.CreateDirectory(dstDir); }
        catch { /* already exists */ }

        foreach (var entry in _sftp!.ListDirectory(srcDir))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name == "." || entry.Name == "..") continue;

            var srcPath = $"{srcDir.TrimEnd('/')}/{entry.Name}";
            var dstPath = $"{dstDir.TrimEnd('/')}/{entry.Name}";

            if (entry.IsDirectory)
            {
                PipeDirRecursive(srcPath, dest, dstPath, onStatus, ct);
                continue;
            }

            // rsync-like: skip if destination exists with same size + mtime.
            try
            {
                var destFile = dest._sftp!.Get(dstPath);
                if (!destFile.IsDirectory && ShouldSkipTransfer(
                        entry.Length, entry.LastWriteTime,
                        destFile.Length, destFile.LastWriteTime))
                {
                    onStatus?.Invoke(entry.Name, TransferStatus.Skipped);
                    Log($"  ⏭ {entry.Name} (up-to-date)");
                    continue;
                }
            }
            catch { /* not present remotely, proceed */ }

            onStatus?.Invoke(entry.Name, TransferStatus.InProgress);
            using (var src = _sftp.OpenRead(srcPath))
            {
                try
                {
                    dest._sftp!.UploadFile(src, dstPath, uploaded =>
                        OnTransferProgress?.Invoke(entry.Name, (long)uploaded, entry.Length));
                }
                catch
                {
                    try { dest._sftp!.DeleteFile(dstPath); } catch { /* ignore */ }
                    throw;
                }
            }
            // Mirror source mtime to destination so subsequent passes skip.
            try { dest._sftp!.SetLastWriteTime(dstPath, entry.LastWriteTime); }
            catch { /* server denied SETSTAT, not fatal */ }
            onStatus?.Invoke(entry.Name, TransferStatus.Completed);
            Log($"  ✓ {entry.Name}");
        }
    }

    // ── Existence / Stat ───────────────────────────────────────
    // Returns (exists, isDir, length, lastWriteTime). length and lastWriteTime
    // are zero when the entry doesn't exist. SSH.NET throws when calling Get()
    // on a missing path — we catch that and treat it as "doesn't exist" so the
    // caller doesn't have to deal with the exception shape.
    public async Task<(bool Exists, bool IsDirectory, long Length, DateTime LastWriteTime)> StatRemoteAsync(string path)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");

        return await Task.Run<(bool, bool, long, DateTime)>(() =>
        {
            try
            {
                var info = _sftp.Get(path);
                return (true, info.IsDirectory, info.IsDirectory ? 0 : info.Length, info.LastWriteTime);
            }
            catch
            {
                return (false, false, 0, default);
            }
        });
    }

    // Non-recursive item count for a remote directory (excludes "." and "..").
    public async Task<int> CountRemoteEntriesAsync(string path)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");

        return await Task.Run(() =>
        {
            try
            {
                return _sftp.ListDirectory(path).Count(e => e.Name != "." && e.Name != "..");
            }
            catch { return 0; }
        });
    }

    // ── Recursive Folder Size ──────────────────────────────────
    // Walks the remote tree under <paramref name="path"/> and sums file sizes.
    // Permission errors on individual subdirectories are swallowed so a single
    // forbidden subtree doesn't kill the count for the rest of the folder.
    public async Task<long> ComputeRemoteFolderSizeAsync(string path, CancellationToken ct = default)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");

        return await Task.Run(() =>
        {
            long total = 0;
            var queue = new Queue<string>();
            queue.Enqueue(path.TrimEnd('/'));

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = queue.Dequeue();

                IEnumerable<ISftpFile> entries;
                try { entries = _sftp.ListDirectory(string.IsNullOrEmpty(dir) ? "/" : dir); }
                catch { continue; }

                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (e.Name == "." || e.Name == "..") continue;
                    var full = string.IsNullOrEmpty(dir) ? "/" + e.Name : dir + "/" + e.Name;
                    if (e.IsDirectory) queue.Enqueue(full);
                    else total += e.Length;
                }
            }
            return total;
        }, ct);
    }

    // ── Recursive Subtree Search ───────────────────────────────
    // BFS over the remote tree from <paramref name="root"/>, invoking
    // <paramref name="onHit"/> on every entry whose basename contains
    // <paramref name="query"/> (case-insensitive). Streaming so the UI can
    // render results as they trickle in over slow links.
    //
    //  - max:  hard cap on the number of hits emitted (defence against
    //          a query like "" on a huge tree). Once reached the walk
    //          stops cleanly.
    //  - onProgress: invoked with the directory currently being scanned,
    //          so the caller can show "Searching /home/user/projects…".
    //  - Failures listing a single directory (permission denied, dead
    //          symlink loop) are logged and skipped, never aborting the
    //          whole search.
    public async Task SearchSubtreeAsync(
        string root, string query,
        Action<SearchHit> onHit,
        Action<string>? onProgress = null,
        int max = 1000,
        CancellationToken ct = default)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");
        if (string.IsNullOrWhiteSpace(query)) return;

        await Task.Run(() =>
        {
            var queue = new Queue<string>();
            queue.Enqueue(root.TrimEnd('/'));
            var hits = 0;

            while (queue.Count > 0 && hits < max)
            {
                ct.ThrowIfCancellationRequested();
                var dir = queue.Dequeue();
                onProgress?.Invoke(dir);

                IEnumerable<ISftpFile> entries;
                try { entries = _sftp.ListDirectory(string.IsNullOrEmpty(dir) ? "/" : dir); }
                catch (Exception ex)
                {
                    Log($"  ⚠ skip {dir}: {ex.Message}");
                    continue;
                }

                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (e.Name == "." || e.Name == "..") continue;

                    var path = string.IsNullOrEmpty(dir) ? "/" + e.Name : dir + "/" + e.Name;

                    if (e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        var rel = path.StartsWith(root, StringComparison.Ordinal)
                            ? path.Substring(root.Length).TrimStart('/')
                            : path;
                        onHit(new SearchHit(
                            rel.Length == 0 ? e.Name : rel,
                            path,
                            e.IsDirectory,
                            e.IsDirectory ? 0 : e.Length,
                            e.LastWriteTime));
                        if (++hits >= max) return;
                    }

                    if (e.IsDirectory) queue.Enqueue(path);
                }
            }
        }, ct);
    }

    // ── Delete ─────────────────────────────────────────────────
    public async Task DeleteRemoteAsync(string path, bool isDir)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");

        await Task.Run(() =>
        {
            if (isDir)
                DeleteDirectoryRecursive(path);
            else
                _sftp.DeleteFile(path);
        });
    }

    private void DeleteDirectoryRecursive(string path)
    {
        foreach (var entry in _sftp!.ListDirectory(path))
        {
            if (entry.Name == "." || entry.Name == "..") continue;
            if (entry.IsDirectory)
                DeleteDirectoryRecursive(entry.FullName);
            else
                _sftp.DeleteFile(entry.FullName);
        }
        _sftp.DeleteDirectory(path);
    }

    // ── Create Directory ───────────────────────────────────────
    public async Task CreateRemoteDirectoryAsync(string path)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");
        await Task.Run(() => _sftp.CreateDirectory(path));
    }

    // ── Rename ─────────────────────────────────────────────────
    public async Task RenameRemoteAsync(string oldPath, string newPath)
    {
        if (_sftp == null) throw new InvalidOperationException("Not connected");
        await Task.Run(() => _sftp.RenameFile(oldPath, newPath));
    }

    public async Task CopyRemoteAsync(string srcPath, string dstPath)
    {
        if (_ssh == null || !_ssh.IsConnected) throw new InvalidOperationException("Not connected via SSH.");
        
        await Task.Run(() =>
        {
            var escapedSrc = srcPath.Replace("'", "'\\''");
            var escapedDst = dstPath.Replace("'", "'\\''");
            var cmdText = $"cp -r '{escapedSrc}' '{escapedDst}'";
            
            using var cmd = _ssh.CreateCommand(cmdText);
            cmd.Execute();
            if (cmd.ExitStatus != 0)
            {
                throw new Exception($"Remote copy failed: {cmd.Error}");
            }
        });
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}

// One match emitted by SearchSubtreeAsync. RelativePath is what the UI shows
// (e.g. "docs/notes.txt"); FullPath is the absolute remote path used for
// transfers and navigation.
public sealed record SearchHit(
    string RelativePath,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTime);
