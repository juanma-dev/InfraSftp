using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfraSftp.Models;
using InfraSftp.Services;

namespace InfraSftp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ProfileService _profileService = new();
    private readonly SettingsService _settingsService = new();
    private readonly KnownHostsService _knownHosts = new();
    private readonly Dictionary<string, SftpService> _connections = new();
    private AppSettings _settings = new();

    // Transfer queue — caps parallel network IO so the UI stays responsive and
    // the SFTP server is not flooded. Items beyond the cap show as "Queued" in
    // the Transfers panel and start as soon as a slot frees up. Tune the cap
    // based on link quality; 3 is a sensible default for typical broadband.
    private const int MaxParallelTransfers = 3;
    private readonly System.Threading.SemaphoreSlim _transferGate = new(MaxParallelTransfers, MaxParallelTransfers);

    // ── Observable State ───────────────────────────────────────
    [ObservableProperty] private ObservableCollection<Profile> _profiles = new();
    [ObservableProperty] private ObservableCollection<Profile> _filteredProfiles = new();
    [ObservableProperty] private string _profileSearchText = "";
    [ObservableProperty] private ObservableCollection<TabItem> _leftTabs = new();
    [ObservableProperty] private ObservableCollection<TabItem> _rightTabs = new();

    // Collection-Count-derived bools: we bind IsVisible to these for the empty-state
    // placeholders shown inside each panel's tab strip. CollectionChanged is wired
    // once in the constructor (LeftTabs / RightTabs are never reassigned).
    public bool HasLeftTabs  => LeftTabs.Count  > 0;
    public bool HasRightTabs => RightTabs.Count > 0;
    public bool HasNoLeftTabs  => LeftTabs.Count  == 0;
    public bool HasNoRightTabs => RightTabs.Count == 0;
    [ObservableProperty] private TabItem? _activeLeftTab;
    [ObservableProperty] private TabItem? _activeRightTab;
    [ObservableProperty] private ObservableCollection<string> _logMessages = new();
    [ObservableProperty] private ObservableCollection<TransferItem> _transfers = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogTab), nameof(IsTransfersTab))]
    private string _bottomTab = "Log"; // "Log" or "Transfers"
    public bool IsLogTab => BottomTab == "Log";
    public bool IsTransfersTab => BottomTab == "Transfers";

    // ── Modal state ────────────────────────────────────────────
    [ObservableProperty] private bool _isModalOpen;
    [ObservableProperty] private bool _isAboutOpen;
    [ObservableProperty] private bool _isShortcutsOpen;
    [ObservableProperty] private bool _isPasswordPromptOpen;
    [ObservableProperty] private string _modalMode = "create"; // "create" or "edit"
    [ObservableProperty] private string _modalName = "";
    [ObservableProperty] private string _modalHost = "";
    [ObservableProperty] private int _modalPort = 22;
    [ObservableProperty] private string _modalUser = "";
    [ObservableProperty] private string _modalPass = "";
    [ObservableProperty] private string _modalKeyPath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModalIsPasswordAuth), nameof(ModalIsKeyAuth))]
    private AuthMethod _modalAuthMethod = AuthMethod.Password;

    public bool ModalIsPasswordAuth => ModalAuthMethod == AuthMethod.Password;
    public bool ModalIsKeyAuth => ModalAuthMethod == AuthMethod.PrivateKey;

    [RelayCommand] private void SelectAuthPassword() => ModalAuthMethod = AuthMethod.Password;
    [RelayCommand] private void SelectAuthKey()      => ModalAuthMethod = AuthMethod.PrivateKey;

    [ObservableProperty] private Profile? _editingProfile;
    [ObservableProperty] private Profile? _passwordPromptProfile;
    [ObservableProperty] private string _passwordPromptPass = "";

    // ── Host key (TOFU) modal state ────────────────────────────
    // Two-flavoured prompt: "unknown" (first connect, neutral) and "mismatch"
    // (fingerprint changed, alarming red). The modal stashes everything the
    // accept handler needs to retry the connect after the user confirms.
    [ObservableProperty] private bool _isHostKeyPromptOpen;
    [ObservableProperty] private bool _hostKeyPromptIsMismatch;
    [ObservableProperty] private string _hostKeyPromptHost = "";
    [ObservableProperty] private int _hostKeyPromptPort;
    [ObservableProperty] private string _hostKeyPromptAlgorithm = "";
    [ObservableProperty] private string _hostKeyPromptFingerprint = "";
    [ObservableProperty] private string _hostKeyPromptStoredFingerprint = "";
    [ObservableProperty] private Profile? _hostKeyPromptProfile;
    [ObservableProperty] private string _hostKeyPromptPassword = "";
    [ObservableProperty] private bool _hostKeyPromptIsReconnect;
    [ObservableProperty] private TabItem? _hostKeyPromptReconnectTab;

    // ── Rename modal state ─────────────────────────────────────
    [ObservableProperty] private bool _isRenameOpen;
    [ObservableProperty] private string _renameNewName = "";
    [ObservableProperty] private FileNode? _renameTarget;
    [ObservableProperty] private TabItem? _renameTab;

    // ── Create Folder modal state ──────────────────────────────
    [ObservableProperty] private bool _isCreateFolderOpen;
    [ObservableProperty] private string _createFolderName = "";
    [ObservableProperty] private TabItem? _createFolderTab;

    // ── Delete Confirm modal state ─────────────────────────────
    [ObservableProperty] private bool _isDeleteConfirmOpen;
    [ObservableProperty] private string _deleteConfirmMessage = "";
    private List<FileNode>? _pendingDeleteBatch;
    private TabItem? _pendingDeleteTab;

    // ── Conflict modal state ───────────────────────────────────
    // DoTransfer awaits PendingConflict.Decision; Confirm/Cancel commands
    // complete it with true/false respectively.
    [ObservableProperty] private bool _isConflictOpen;
    [ObservableProperty] private ConflictInfo? _pendingConflict;

    // ── Subtree search modal state ─────────────────────────────
    // A simple Find dialog rooted at the focused remote tab's current path.
    // Hits stream into SearchResults as the SFTP walk progresses; the user
    // double-clicks a result to navigate the panel to its parent directory.
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private bool _isSearchRunning;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _searchRoot = "";
    [ObservableProperty] private string _searchPanel = "";
    [ObservableProperty] private string _searchStatus = "";
    [ObservableProperty] private ObservableCollection<SearchHit> _searchResults = new();
    private CancellationTokenSource? _searchCts;
    private TabItem? _searchSourceTab;
    private const int SearchHitLimit = 1000;

    // ── Path bar (breadcrumbs + edit mode) ─────────────────────
    // Edit mode lives per-panel (left/right) rather than per-tab so
    // switching tabs always starts in breadcrumbs view. Edit text is
    // seeded from the tab's current path when editing begins.
    [ObservableProperty] private bool _isLeftPathEditing;
    [ObservableProperty] private bool _isRightPathEditing;
    [ObservableProperty] private string _leftPathEditText = "";
    [ObservableProperty] private string _rightPathEditText = "";

    // ── Clipboard state ────────────────────────────────────────
    // Multi-item clipboard. _clipboardEntries snapshots the source items at
    // copy/cut time so the entries stay valid even if the source listing is
    // refreshed later. ClipboardOperation distinguishes copy (preserve source)
    // from cut (move semantics — source removed after success).
    private List<ClipboardEntry> _clipboardEntries = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClipboardEntries))]
    private ClipboardOperation _clipboardOperation = ClipboardOperation.None;

    public bool HasClipboardEntries => _clipboardEntries.Count > 0
                                       && ClipboardOperation != ClipboardOperation.None;

    // ── Settings modal state ───────────────────────────────────
    // CurrentTheme is bound directly to the three theme cards in the
    // settings modal. OnCurrentThemeChanged persists + applies immediately,
    // so picking a theme updates the UI without needing a Save button.
    [ObservableProperty] private bool _isSettingsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeDark), nameof(IsThemeLight), nameof(IsThemeSystem))]
    private AppTheme _currentTheme = AppTheme.Dark;

    public bool IsThemeDark   => CurrentTheme == AppTheme.Dark;
    public bool IsThemeLight  => CurrentTheme == AppTheme.Light;
    public bool IsThemeSystem => CurrentTheme == AppTheme.System;

    partial void OnCurrentThemeChanged(AppTheme value)
    {
        _settings.Theme = value;
        _settingsService.Save(_settings);
        SettingsService.ApplyTheme(value);
    }

    // Hidden-file visibility. Persisted to settings.json. Toggling re-lists
    // the active tab in each panel so the change is visible immediately.
    [ObservableProperty] private bool _showHidden;

    partial void OnShowHiddenChanged(bool value)
    {
        _settings.ShowHidden = value;
        _settingsService.Save(_settings);
        _ = RefreshAllPanelsAsync();
    }

    // When true, recursive transfers re-copy every file regardless of the
    // size+mtime match — useful when the user knows the destination is stale
    // even though it looks fresh (e.g. content edited via tooling that
    // back-dated the timestamp). Each connected SftpService reads this flag
    // through its ForceTransferProvider lambda so the toggle takes effect
    // without reconnecting.
    [ObservableProperty] private bool _forceTransfer;

    partial void OnForceTransferChanged(bool value)
    {
        _settings.ForceTransfer = value;
        _settingsService.Save(_settings);
    }

    [RelayCommand]
    private void ToggleForceTransfer() => ForceTransfer = !ForceTransfer;

    private async Task RefreshAllPanelsAsync()
    {
        if (ActiveLeftTab != null) await NavigateAsync(ActiveLeftTab, ActiveLeftTab.Path);
        if (ActiveRightTab != null) await NavigateAsync(ActiveRightTab, ActiveRightTab.Path);
    }

    [RelayCommand]
    private void ToggleHidden() => ShowHidden = !ShowHidden;

    // ── Menu ───────────────────────────────────────────────────
    [ObservableProperty] private bool _isMenuOpen;

    // ── Loading ────────────────────────────────────────────────
    [ObservableProperty] private bool _isLoading;

    // ── Focused panel (for keyboard shortcuts) ─────────────────
    // "Left" or "Right" — updated by GotFocus handlers on the two panel Borders.
    [ObservableProperty] private string _focusedPanel = "Left";

    public MainWindowViewModel()
    {
        LeftTabs.CollectionChanged  += (_, _) => { OnPropertyChanged(nameof(HasLeftTabs));  OnPropertyChanged(nameof(HasNoLeftTabs)); };
        RightTabs.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasRightTabs)); OnPropertyChanged(nameof(HasNoRightTabs)); };

        // Load + apply settings before any UI renders so the correct theme
        // is in place from the first frame (no flash of dark on light boot).
        // Setting CurrentTheme triggers OnCurrentThemeChanged which applies +
        // re-saves — the re-save on boot is a harmless no-op.
        _settings = _settingsService.Load();
        CurrentTheme = _settings.Theme;
        // Use the backing field to avoid triggering the change handler
        // (which would refresh panels that don't exist yet on boot).
        _showHidden = _settings.ShowHidden;
        _forceTransfer = _settings.ForceTransfer;

        // Load saved profiles
        var saved = _profileService.LoadProfiles();
        Profiles = new ObservableCollection<Profile>(saved);
        RefilterProfiles();

        // Restore tabs from the previous session if any. First-run / no saved
        // state ⇒ seed the same default Local PC tab the app has always opened.
        var restored = RestoreTabsFromSettings();
        if (!restored)
        {
            var localTab = new TabItem
            {
                Id = "local",
                Name = "Local PC",
                IsRemote = false,
                Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            LeftTabs.Add(localTab);
            ActiveLeftTab = localTab;
            _ = RefreshLocalFilesAsync(localTab);
        }
        else
        {
            // Reconnect remote tabs in the background so the UI doesn't block
            // on N concurrent SSH handshakes. Failed reconnects leave the tab
            // disconnected — the click-to-reconnect path picks them back up.
            _ = Task.Run(async () =>
            {
                await Task.Delay(150); // let the UI settle first
                try { await TryReconnectDisconnectedTabsAsync(); } catch { /* logged inside */ }
            });
        }

        Log("InfraSftp started. Ready.");
    }

    // Replays AppSettings.LeftTabs / RightTabs into the live tab collections.
    // Returns true if anything was restored (caller skips default seeding).
    private bool RestoreTabsFromSettings()
    {
        var profileById = Profiles.ToDictionary(p => p.Id);
        bool any = false;

        TabItem? Materialize(TabState s)
        {
            if (!s.IsRemote)
            {
                // Local tabs are static-ish: keep "local" as the canonical id
                // and revert to the home directory if the saved path no longer
                // exists (drive ejected, folder deleted).
                var path = Directory.Exists(s.Path)
                    ? s.Path
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return new TabItem
                {
                    Id = string.IsNullOrEmpty(s.Id) ? "local" : s.Id,
                    Name = string.IsNullOrEmpty(s.Name) ? "Local PC" : s.Name,
                    IsRemote = false,
                    Path = path
                };
            }
            // Remote tab — only valid if the corresponding profile still exists.
            // Profile may have been deleted between sessions; drop the tab.
            if (!profileById.TryGetValue(s.Id, out var profile)) return null;
            return new TabItem
            {
                Id = profile.Id,
                Name = profile.Name,
                IsRemote = true,
                Path = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                IsConnected = false,
                Sftp = null
            };
        }

        foreach (var s in _settings.LeftTabs)
        {
            var t = Materialize(s);
            if (t != null) { LeftTabs.Add(t); any = true; }
        }
        foreach (var s in _settings.RightTabs)
        {
            var t = Materialize(s);
            if (t != null) { RightTabs.Add(t); any = true; }
        }

        if (!any) return false;

        // Restore active tab; default to first if the saved id is gone.
        ActiveLeftTab  = LeftTabs.FirstOrDefault(t => t.Id == _settings.ActiveLeftTabId)  ?? LeftTabs.FirstOrDefault();
        ActiveRightTab = RightTabs.FirstOrDefault(t => t.Id == _settings.ActiveRightTabId) ?? RightTabs.FirstOrDefault();

        // Refresh local listings now (no network required); remote listings
        // will populate when the background reconnect completes.
        foreach (var t in LeftTabs.Concat(RightTabs).Where(t => !t.IsRemote))
            _ = RefreshLocalFilesAsync(t);

        return true;
    }

    // Captures the current tab layout into the settings struct. Caller (the
    // window close hook) is responsible for actually writing settings.json.
    public void CaptureTabsToSettings(AppSettings target)
    {
        target.LeftTabs  = LeftTabs.Select(SnapshotTab).ToList();
        target.RightTabs = RightTabs.Select(SnapshotTab).ToList();
        target.ActiveLeftTabId  = ActiveLeftTab?.Id;
        target.ActiveRightTabId = ActiveRightTab?.Id;
    }

    private static TabState SnapshotTab(TabItem t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        IsRemote = t.IsRemote,
        Path = t.Path
    };

    public AppSettings Settings => _settings;

    public void Log(string msg)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogMessages.Add($"[{timestamp}] {msg}");
    }

    // ── Profile Management ─────────────────────────────────────
    [RelayCommand]
    private void OpenCreateModal()
    {
        ModalMode = "create";
        ModalName = "";
        ModalHost = "";
        ModalPort = 22;
        ModalUser = "";
        ModalPass = "";
        ModalKeyPath = "";
        ModalAuthMethod = AuthMethod.Password;
        EditingProfile = null;
        IsModalOpen = true;
    }

    [RelayCommand]
    private void OpenEditModal(Profile? profile)
    {
        if (profile == null) return;
        ModalMode = "edit";
        ModalName = profile.Name;
        ModalHost = profile.Host;
        ModalPort = profile.Port;
        ModalUser = profile.User;
        ModalPass = _profileService.GetPassword(profile.Host, profile.User) ?? "";
        ModalKeyPath = profile.PrivateKeyPath;
        ModalAuthMethod = profile.AuthMethod;
        EditingProfile = profile;
        IsModalOpen = true;
    }

    [RelayCommand]
    private void CloseModal() => IsModalOpen = false;

    [RelayCommand]
    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(ModalHost) || string.IsNullOrWhiteSpace(ModalUser))
        {
            Log("⚠ Host and Username are required.");
            return;
        }

        var name = string.IsNullOrWhiteSpace(ModalName) ? ModalHost : ModalName;

        if (ModalMode == "create")
        {
            var profile = new Profile
            {
                Name = name,
                Host = ModalHost,
                Port = ModalPort,
                User = ModalUser,
                AuthMethod = ModalAuthMethod,
                PrivateKeyPath = ModalKeyPath ?? ""
            };
            Profiles.Add(profile);
        }
        else if (EditingProfile != null)
        {
            EditingProfile.Name = name;
            EditingProfile.Host = ModalHost;
            EditingProfile.Port = ModalPort;
            EditingProfile.User = ModalUser;
            EditingProfile.AuthMethod = ModalAuthMethod;
            EditingProfile.PrivateKeyPath = ModalKeyPath ?? "";
        }

        // Persist the secret in the vault. For password auth this is the
        // password; for key auth it's the (possibly empty) passphrase. We
        // intentionally save an empty string for unprotected keys so the
        // reconnect path doesn't think the secret is "missing".
        if (ModalAuthMethod == AuthMethod.PrivateKey)
            _profileService.SavePassword(ModalHost, ModalUser, ModalPass ?? "");
        else if (!string.IsNullOrEmpty(ModalPass))
            _profileService.SavePassword(ModalHost, ModalUser, ModalPass);

        _profileService.SaveProfiles(Profiles.ToList());
        Log($"✓ Profile \"{name}\" saved.");
        // Force refresh
        Profiles = new ObservableCollection<Profile>(Profiles);
        RefilterProfiles();
        IsModalOpen = false;
    }

    [RelayCommand]
    private async Task SaveAndConnect()
    {
        SaveProfile();
        var profile = ModalMode == "create"
            ? Profiles.LastOrDefault()
            : EditingProfile;
        if (profile != null)
            await ConnectToProfileAsync(profile);
    }

    [RelayCommand]
    private void DeleteProfile(Profile? profile)
    {
        if (profile == null) return;
        Profiles.Remove(profile);
        _profileService.SaveProfiles(Profiles.ToList());
        _profileService.DeletePassword(profile.Host, profile.User);
        RefilterProfiles();
        Log($"🗑 Profile \"{profile.Name}\" deleted.");
    }

    // ── Profile search ─────────────────────────────────────────
    partial void OnProfileSearchTextChanged(string value) => RefilterProfiles();

    [RelayCommand]
    private void ClearProfileSearch() => ProfileSearchText = "";

    private void RefilterProfiles()
    {
        var q = (ProfileSearchText ?? "").Trim();
        IEnumerable<Profile> src = Profiles;
        if (q.Length > 0)
        {
            src = Profiles.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Host.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.User.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        FilteredProfiles = new ObservableCollection<Profile>(src);
    }

    // ── Profile Import / Export ────────────────────────────────
    // The Profile model carries no password (those live in the encrypted vault
    // only), so exported JSON is naturally password-free. Callers come from the
    // code-behind after the Avalonia file picker resolves a target stream.
    public async Task ExportProfilesAsync(Stream target)
    {
        var json = _profileService.ExportProfiles(Profiles.ToList());
        await using var writer = new StreamWriter(target);
        await writer.WriteAsync(json);
        Log($"📤 Exported {Profiles.Count} profile(s).");
    }

    public async Task ImportProfilesAsync(Stream source)
    {
        using var reader = new StreamReader(source);
        var json = await reader.ReadToEndAsync();

        List<Profile> imported;
        try { imported = _profileService.ParseImportedProfiles(json); }
        catch (Exception ex) { Log($"⚠ Import failed: {ex.Message}"); return; }

        int added = 0, skipped = 0;
        foreach (var p in imported)
        {
            if (string.IsNullOrWhiteSpace(p.Host) || string.IsNullOrWhiteSpace(p.User)) { skipped++; continue; }

            // Dedup by Host+User+Port — don't trust Id across machines.
            if (Profiles.Any(ex =>
                    ex.Host.Equals(p.Host, StringComparison.OrdinalIgnoreCase) &&
                    ex.User.Equals(p.User, StringComparison.OrdinalIgnoreCase) &&
                    ex.Port == p.Port))
            { skipped++; continue; }

            p.Id = Guid.NewGuid().ToString("N")[..8];
            Profiles.Add(p);
            added++;
        }

        _profileService.SaveProfiles(Profiles.ToList());
        RefilterProfiles();
        Log($"📥 Imported {added} profile(s). Skipped {skipped} duplicate/invalid.");
    }

    // ── Connection ─────────────────────────────────────────────
    [RelayCommand]
    private async Task ConnectToProfileAsync(Profile? profile)
    {
        if (profile == null) return;

        // Vault holds the password for AuthMethod.Password and the optional
        // passphrase for AuthMethod.PrivateKey. An unprotected key has an
        // empty passphrase, which is a valid stored value.
        var secret = _profileService.GetPassword(profile.Host, profile.User);

        if (profile.AuthMethod == AuthMethod.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
            {
                Log($"⚠ Profile '{profile.Name}' uses key auth but no key file is set.");
                return;
            }
            if (!System.IO.File.Exists(profile.PrivateKeyPath))
            {
                Log($"⚠ Private key not found: {profile.PrivateKeyPath}");
                return;
            }
            // First attempt with whatever passphrase we have (possibly empty).
            // DoConnect will catch a PassPhrase exception and re-prompt.
            await DoConnect(profile, secret ?? "");
            return;
        }

        if (string.IsNullOrEmpty(secret))
        {
            PasswordPromptProfile = profile;
            PasswordPromptPass = "";
            IsPasswordPromptOpen = true;
            return;
        }

        await DoConnect(profile, secret);
    }

    [RelayCommand]
    private void CancelPasswordPrompt() => IsPasswordPromptOpen = false;

    [RelayCommand]
    private async Task ConnectWithPasswordPrompt()
    {
        if (PasswordPromptProfile == null) return;
        var p = PasswordPromptProfile;
        var pw = PasswordPromptPass;
        IsPasswordPromptOpen = false;

        // Save password for next time
        _profileService.SavePassword(p.Host, p.User, pw);
        _profileService.SaveProfiles(Profiles.ToList());
        Log($"✓ Password saved for {p.User}@{p.Host}");

        await DoConnect(p, pw);
    }

    private async Task DoConnect(Profile profile, string secret, bool acceptNewHostKey = false)
    {
        if (_connections.ContainsKey(profile.Id))
        {
            Log($"Already connected to {profile.Name}");
            return;
        }

        IsLoading = true;
        try
        {
            var sftp = new SftpService(_knownHosts);
            sftp.OnLog += Log;
            sftp.OnTransferProgress += OnTransferProgress;
            sftp.ForceTransferProvider = () => ForceTransfer;
            var profileIdLocal = profile.Id;
            sftp.OnDisconnected += () => OnSessionDropped(profileIdLocal);

            if (profile.AuthMethod == AuthMethod.PrivateKey)
                await sftp.ConnectWithKeyAsync(profile.Host, profile.Port, profile.User,
                    profile.PrivateKeyPath, secret, acceptNewHostKey);
            else
                await sftp.ConnectAsync(profile.Host, profile.Port, profile.User, secret, acceptNewHostKey);

            _connections[profile.Id] = sftp;
            profile.IsConnected = true;

            var tab = new TabItem
            {
                Id = profile.Id,
                Name = profile.Name,
                IsRemote = true,
                Path = sftp.WorkingDirectory,
                Sftp = sftp,
                IsConnected = true
            };

            RightTabs.Add(tab);
            ActiveRightTab = tab;

            await RefreshRemoteFilesAsync(tab);
        }
        catch (UnknownHostKeyException ex)
        {
            OpenHostKeyPrompt(profile, secret, ex.Algorithm, ex.FingerprintSha256,
                isMismatch: false, storedFingerprint: "", isReconnect: false, reconnectTab: null);
        }
        catch (HostKeyMismatchException ex)
        {
            OpenHostKeyPrompt(profile, secret, ex.Algorithm, ex.ReceivedFingerprintSha256,
                isMismatch: true, storedFingerprint: ex.StoredFingerprintSha256,
                isReconnect: false, reconnectTab: null);
        }
        catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
        {
            // Encrypted private key with no/empty passphrase — prompt the user.
            Log("🔑 Private key is passphrase-protected.");
            PasswordPromptProfile = profile;
            PasswordPromptPass = "";
            IsPasswordPromptOpen = true;
        }
        catch (Renci.SshNet.Common.SshAuthenticationException ex) when (profile.AuthMethod == AuthMethod.PrivateKey)
        {
            // Wrong passphrase or unauthorised key. Re-prompt — same modal, the
            // user may have forgotten the passphrase or be using the wrong key.
            Log($"❌ Key authentication failed: {ex.Message}");
            _profileService.DeletePassword(profile.Host, profile.User);
            PasswordPromptProfile = profile;
            PasswordPromptPass = "";
            IsPasswordPromptOpen = true;
        }
        catch (Exception ex)
        {
            Log($"❌ Connection failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OpenHostKeyPrompt(Profile profile, string password,
        string algorithm, string fingerprint,
        bool isMismatch, string storedFingerprint,
        bool isReconnect, TabItem? reconnectTab)
    {
        HostKeyPromptProfile = profile;
        HostKeyPromptPassword = password;
        HostKeyPromptHost = profile.Host;
        HostKeyPromptPort = profile.Port;
        HostKeyPromptAlgorithm = algorithm;
        HostKeyPromptFingerprint = fingerprint;
        HostKeyPromptStoredFingerprint = storedFingerprint;
        HostKeyPromptIsMismatch = isMismatch;
        HostKeyPromptIsReconnect = isReconnect;
        HostKeyPromptReconnectTab = reconnectTab;
        IsHostKeyPromptOpen = true;

        if (isMismatch)
            Log($"⚠ Host key MISMATCH for {profile.Host}:{profile.Port} — possible MITM. Awaiting user decision.");
        else
            Log($"🔍 Unknown host key for {profile.Host}:{profile.Port}. Awaiting user decision.");
    }

    [RelayCommand]
    private void CancelHostKey()
    {
        IsHostKeyPromptOpen = false;
        Log("✋ Host key not trusted. Connection cancelled.");
        HostKeyPromptProfile = null;
        HostKeyPromptPassword = "";
    }

    [RelayCommand]
    private async Task AcceptHostKey()
    {
        if (HostKeyPromptProfile == null) return;
        var profile = HostKeyPromptProfile;
        var password = HostKeyPromptPassword;
        var isMismatch = HostKeyPromptIsMismatch;
        var fingerprint = HostKeyPromptFingerprint;
        var algorithm = HostKeyPromptAlgorithm;
        var isReconnect = HostKeyPromptIsReconnect;
        var reconnectTab = HostKeyPromptReconnectTab;

        IsHostKeyPromptOpen = false;
        HostKeyPromptProfile = null;
        HostKeyPromptPassword = "";

        // For a fingerprint change we overwrite the stored anchor explicitly so
        // the next handshake matches without needing the acceptNewHostKey flag.
        // For a brand-new host we let the SftpService persist on its own when
        // acceptNewHostKey: true.
        if (isMismatch)
            _knownHosts.Trust(profile.Host, profile.Port, algorithm, fingerprint);

        if (isReconnect && reconnectTab != null)
            await DoReconnect(reconnectTab, profile, password, acceptNewHostKey: !isMismatch);
        else
            await DoConnect(profile, password, acceptNewHostKey: !isMismatch);
    }

    [RelayCommand]
    private void DisconnectTab(TabItem? tab)
    {
        if (tab == null || tab.Id == "local") return;

        if (_connections.TryGetValue(tab.Id, out var sftp))
        {
            sftp.Disconnect();
            sftp.Dispose();
            _connections.Remove(tab.Id);
            var p = Profiles.FirstOrDefault(p => p.Id == tab.Id);
            if (p != null) p.IsConnected = false;
        }

        LeftTabs.Remove(tab);
        RightTabs.Remove(tab);

        if (ActiveLeftTab == tab) ActiveLeftTab = LeftTabs.FirstOrDefault();
        if (ActiveRightTab == tab) ActiveRightTab = RightTabs.FirstOrDefault();

        Log($"Disconnected from {tab.Name}");
    }

    [RelayCommand]
    private void DisconnectAll()
    {
        foreach (var kvp in _connections)
        {
            kvp.Value.Disconnect();
            kvp.Value.Dispose();
        }
        _connections.Clear();
        foreach (var p in Profiles) p.IsConnected = false;

        var remoteTabs = LeftTabs.Concat(RightTabs).Where(t => t.IsRemote).ToList();
        foreach (var t in remoteTabs) { LeftTabs.Remove(t); RightTabs.Remove(t); }

        ActiveLeftTab = LeftTabs.FirstOrDefault();
        ActiveRightTab = RightTabs.FirstOrDefault();
        Log("All connections closed.");
    }

    // ── Auto-reconnect on idle drops ───────────────────────────
    // Tracks profile IDs whose reconnect attempt is in flight, so rapid
    // clicks don't spawn parallel reconnections against the same server.
    private readonly HashSet<string> _reconnecting = new();

    // Called from SftpService.OnDisconnected (background thread). Marshals
    // to UI thread and flips the relevant tabs/profile to the "offline" dot.
    private void OnSessionDropped(string profileId)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var p = Profiles.FirstOrDefault(x => x.Id == profileId);
            if (p != null) p.IsConnected = false;
            foreach (var t in LeftTabs.Concat(RightTabs).Where(t => t.Id == profileId))
                t.IsConnected = false;
            Log($"⚠ Session to {p?.Name ?? profileId} dropped. Click the window to reconnect.");
        });
    }

    /// <summary>
    /// Scans every remote tab and triggers a reconnect for any that are
    /// marked as disconnected. Safe to call from any click handler — a
    /// dedup set prevents piling up concurrent reconnect attempts.
    /// </summary>
    public async Task TryReconnectDisconnectedTabsAsync()
    {
        var tabs = LeftTabs.Concat(RightTabs)
            .Where(t => t.IsRemote && !t.IsConnected)
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .ToList();

        foreach (var tab in tabs)
        {
            if (_reconnecting.Contains(tab.Id)) continue;
            _reconnecting.Add(tab.Id);
            try { await ReconnectTabAsync(tab); }
            finally { _reconnecting.Remove(tab.Id); }
        }
    }

    private async Task ReconnectTabAsync(TabItem tab)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == tab.Id);
        if (profile == null) return;

        var secret = _profileService.GetPassword(profile.Host, profile.User);

        if (profile.AuthMethod == AuthMethod.Password && string.IsNullOrEmpty(secret))
        {
            Log($"⚠ Cannot reconnect to {profile.Name}: password not in vault");
            return;
        }

        // For key auth, an empty/missing passphrase is valid (unprotected key).
        // Wrong passphrase will surface in DoReconnect's catch block.
        Log($"↻ Reconnecting to {profile.Name}…");
        await DoReconnect(tab, profile, secret ?? "", acceptNewHostKey: false);
    }

    private async Task DoReconnect(TabItem tab, Profile profile, string secret, bool acceptNewHostKey)
    {
        // Tear down whatever was left of the old session before creating a new one.
        if (_connections.TryGetValue(profile.Id, out var old))
        {
            try { old.Disconnect(); old.Dispose(); } catch { /* ignore */ }
            _connections.Remove(profile.Id);
        }

        try
        {
            var sftp = new SftpService(_knownHosts);
            sftp.OnLog += Log;
            sftp.OnTransferProgress += OnTransferProgress;
            sftp.ForceTransferProvider = () => ForceTransfer;
            var profileIdLocal = profile.Id;
            sftp.OnDisconnected += () => OnSessionDropped(profileIdLocal);

            if (profile.AuthMethod == AuthMethod.PrivateKey)
                await sftp.ConnectWithKeyAsync(profile.Host, profile.Port, profile.User,
                    profile.PrivateKeyPath, secret, acceptNewHostKey);
            else
                await sftp.ConnectAsync(profile.Host, profile.Port, profile.User, secret, acceptNewHostKey);

            _connections[profile.Id] = sftp;

            // Apply the new session to every tab that references this profile —
            // the user may have split the profile across both panels.
            var savedPath = tab.Path;
            foreach (var t in LeftTabs.Concat(RightTabs).Where(t => t.Id == profile.Id))
            {
                t.Sftp = sftp;
                t.IsConnected = true;
            }
            profile.IsConnected = true;

            // Reopen the same directory the user was in. If that path no
            // longer exists on the remote side, fall back to the server's
            // working directory.
            try { await NavigateAsync(tab, savedPath); }
            catch { await NavigateAsync(tab, sftp.WorkingDirectory); }

            Log($"✓ Reconnected to {profile.Name}");
        }
        catch (UnknownHostKeyException ex)
        {
            OpenHostKeyPrompt(profile, secret, ex.Algorithm, ex.FingerprintSha256,
                isMismatch: false, storedFingerprint: "", isReconnect: true, reconnectTab: tab);
        }
        catch (HostKeyMismatchException ex)
        {
            OpenHostKeyPrompt(profile, secret, ex.Algorithm, ex.ReceivedFingerprintSha256,
                isMismatch: true, storedFingerprint: ex.StoredFingerprintSha256,
                isReconnect: true, reconnectTab: tab);
        }
        catch (Exception ex)
        {
            Log($"❌ Reconnect failed: {ex.Message}");
        }
    }

    // ── Menu / About ───────────────────────────────────────────
    [RelayCommand]
    private void CloseMenu() => IsMenuOpen = false;

    [RelayCommand]
    private void MenuNewConnection()
    {
        IsMenuOpen = false;
        OpenCreateModalCommand.Execute(null);
    }

    [RelayCommand]
    private void MenuDisconnectAll()
    {
        IsMenuOpen = false;
        DisconnectAllCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenAbout()
    {
        IsMenuOpen = false;
        IsAboutOpen = true;
    }

    [RelayCommand]
    private void CloseAbout() => IsAboutOpen = false;

    [RelayCommand]
    private void OpenShortcuts()
    {
        IsMenuOpen = false;
        IsShortcutsOpen = true;
    }

    [RelayCommand]
    private void CloseShortcuts() => IsShortcutsOpen = false;

    [RelayCommand]
    private void OpenSettings()
    {
        IsMenuOpen = false;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void PickTheme(string? name)
    {
        if (Enum.TryParse<AppTheme>(name, out var t)) CurrentTheme = t;
    }

    [RelayCommand]
    private async Task MenuRefreshAll()
    {
        IsMenuOpen = false;
        await RefreshAllPanels();
    }

    // ── Keyboard shortcut helpers ──────────────────────────────
    [RelayCommand]
    private async Task RefreshActivePanel()
    {
        var tab = FocusedPanel == "Right" ? ActiveRightTab : ActiveLeftTab;
        if (tab == null) return;
        if (tab.IsRemote) await RefreshRemoteFilesAsync(tab);
        else await RefreshLocalFilesAsync(tab);
    }

    [RelayCommand]
    private void CloseActiveTab()
    {
        var tab = FocusedPanel == "Right" ? ActiveRightTab : ActiveLeftTab;
        if (tab == null || tab.Id == "local") return;
        DisconnectTabCommand.Execute(tab);
    }

    // ── File Browsing ──────────────────────────────────────────
    public async Task NavigateAsync(TabItem tab, string path)
    {
        tab.Path = path;
        if (tab.IsRemote)
            await RefreshRemoteFilesAsync(tab);
        else
            await RefreshLocalFilesAsync(tab);
    }

    public async Task OpenItemAsync(TabItem tab, FileNode file)
    {
        if (file.Name == "..")
        {
            var parent = tab.IsRemote
                ? GetParentPath(tab.Path, '/')
                : Path.GetDirectoryName(tab.Path) ?? tab.Path;
            await NavigateAsync(tab, parent);
            return;
        }

        if (file.IsDirectory)
        {
            var sep = tab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
            var newPath = tab.Path.TrimEnd('/', '\\') + sep + file.Name;
            await NavigateAsync(tab, newPath);
        }
    }

    private async Task RefreshLocalFilesAsync(TabItem tab)
    {
        try
        {
            await Task.Run(() =>
            {
                var nodes = new List<FileNode>();
                if (tab.Path != Path.GetPathRoot(tab.Path))
                    nodes.Add(new FileNode { Name = "..", IsDirectory = true, IsLocalFile = true });

                var di = new DirectoryInfo(tab.Path);
                bool showHidden = ShowHidden;
                foreach (var d in di.GetDirectories())
                {
                    try
                    {
                        if (!showHidden && IsLocalHidden(d)) continue;
                        nodes.Add(new FileNode { Name = d.Name, IsDirectory = true, LastModified = d.LastWriteTime, IsLocalFile = true });
                    }
                    catch { /* access denied */ }
                }
                foreach (var f in di.GetFiles())
                {
                    try
                    {
                        if (!showHidden && IsLocalHidden(f)) continue;
                        nodes.Add(new FileNode { Name = f.Name, Size = f.Length, LastModified = f.LastWriteTime, IsLocalFile = true });
                    }
                    catch { /* access denied */ }
                }

                var sorted = ApplySort(nodes, tab);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    tab.Files = new ObservableCollection<FileNode>(sorted);
                });
            });
        }
        catch (Exception ex)
        {
            Log($"❌ Error listing {tab.Path}: {ex.Message}");
        }
    }

    // A file/directory is considered hidden if it carries the Hidden attribute
    // OR starts with a dot (Unix convention — matters when the user mounted a
    // network share from a Unix host).
    private static bool IsLocalHidden(FileSystemInfo info)
    {
        if (info.Name.StartsWith(".")) return true;
        try { return (info.Attributes & FileAttributes.Hidden) != 0; }
        catch { return false; }
    }

    private async Task RefreshRemoteFilesAsync(TabItem tab)
    {
        if (tab.Sftp == null) return;
        try
        {
            var nodes = await tab.Sftp.ListDirectoryAsync(tab.Path);
            if (!ShowHidden)
                nodes = nodes.Where(n => n.Name == ".." || !n.Name.StartsWith(".")).ToList();
            var sorted = ApplySort(nodes, tab);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                tab.Files = new ObservableCollection<FileNode>(sorted);
            });
        }
        catch (Exception ex)
        {
            Log($"❌ Error listing {tab.Path}: {ex.Message}");
        }
    }

    // ── Sorting ────────────────────────────────────────────────
    // "..", then directories, then files. Sort order within each bucket follows
    // tab.SortColumn + tab.SortDescending. Name sort is case-insensitive.
    private static List<FileNode> ApplySort(IEnumerable<FileNode> nodes, TabItem tab)
    {
        var parent = nodes.Where(n => n.Name == "..").ToList();
        var rest   = nodes.Where(n => n.Name != "..").ToList();
        var dirs   = rest.Where(n => n.IsDirectory);
        var files  = rest.Where(n => !n.IsDirectory);

        var result = new List<FileNode>(rest.Count + parent.Count);
        result.AddRange(parent);
        result.AddRange(SortBucket(dirs, tab));
        result.AddRange(SortBucket(files, tab));
        return result;
    }

    private static IEnumerable<FileNode> SortBucket(IEnumerable<FileNode> src, TabItem tab)
    {
        return tab.SortColumn switch
        {
            FileSortColumn.Size     => tab.SortDescending ? src.OrderByDescending(n => n.Size).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                                                         : src.OrderBy(n => n.Size).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
            FileSortColumn.Modified => tab.SortDescending ? src.OrderByDescending(n => n.LastModified).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                                                         : src.OrderBy(n => n.LastModified).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
            _                       => tab.SortDescending ? src.OrderByDescending(n => n.Name, StringComparer.OrdinalIgnoreCase)
                                                         : src.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void ToggleSort(TabItem tab, FileSortColumn col)
    {
        if (tab.SortColumn == col) tab.SortDescending = !tab.SortDescending;
        else { tab.SortColumn = col; tab.SortDescending = false; }
    }

    private static void ReapplySort(TabItem tab)
    {
        var sorted = ApplySort(tab.Files, tab);
        tab.Files = new ObservableCollection<FileNode>(sorted);
    }

    [RelayCommand]
    private void SortLeft(string? column)
    {
        if (ActiveLeftTab == null || column == null) return;
        ToggleSort(ActiveLeftTab, ParseCol(column));
        ReapplySort(ActiveLeftTab);
    }

    [RelayCommand]
    private void SortRight(string? column)
    {
        if (ActiveRightTab == null || column == null) return;
        ToggleSort(ActiveRightTab, ParseCol(column));
        ReapplySort(ActiveRightTab);
    }

    private static FileSortColumn ParseCol(string s) => s switch
    {
        "Size"     => FileSortColumn.Size,
        "Modified" => FileSortColumn.Modified,
        _          => FileSortColumn.Name
    };

    [RelayCommand]
    private async Task RefreshAllPanels()
    {
        if (ActiveLeftTab != null)
        {
            if (ActiveLeftTab.IsRemote) await RefreshRemoteFilesAsync(ActiveLeftTab);
            else await RefreshLocalFilesAsync(ActiveLeftTab);
        }
        if (ActiveRightTab != null)
        {
            if (ActiveRightTab.IsRemote) await RefreshRemoteFilesAsync(ActiveRightTab);
            else await RefreshLocalFilesAsync(ActiveRightTab);
        }
        Log("🔄 Panels refreshed.");
    }

    // ── Cross-panel drag transfer ──────────────────────────────
    // Triggered when the user drops a selection from one panel onto the other
    // panel's empty area. Same semantics as a per-row Transfer command, just
    // batched — every entry goes through DoTransfer with the conflict prompt.
    public async Task TransferDraggedFilesAsync(IReadOnlyList<FileNode> files, string sourcePanel, string destPanel)
    {
        if (files.Count == 0) return;
        var sourceTab = sourcePanel == "Left" ? ActiveLeftTab : ActiveRightTab;
        var destTab   = destPanel   == "Left" ? ActiveLeftTab : ActiveRightTab;
        if (sourceTab == null || destTab == null)
        {
            Log("⚠ Drag-transfer requires both panels to have an active tab.");
            return;
        }
        if (ReferenceEquals(sourceTab, destTab)) return; // same tab — no-op.

        if (files.Count > 1) Log($"📦 Drag-transferring {files.Count} items…");

        foreach (var f in files)
        {
            if (f.Name == "..") continue;
            await DoTransfer(f, sourceTab, destTab);
        }
    }

    // Drop on a folder row in the *other* panel: same as TransferDraggedFiles
    // but the destination tab is treated as if the user had navigated into
    // `targetSubFolder` first. We mutate destTab.Path temporarily — DoTransfer
    // reads it to compute dstPath. Restored at the end so the user's view is
    // not yanked into a folder they didn't navigate to.
    public async Task TransferDraggedFilesToFolderAsync(
        IReadOnlyList<FileNode> files, string sourcePanel, string destPanel, string targetSubFolder)
    {
        if (files.Count == 0) return;
        var sourceTab = sourcePanel == "Left" ? ActiveLeftTab : ActiveRightTab;
        var destTab   = destPanel   == "Left" ? ActiveLeftTab : ActiveRightTab;
        if (sourceTab == null || destTab == null) return;
        if (ReferenceEquals(sourceTab, destTab)) return;

        var sep = destTab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var savedPath = destTab.Path;
        var subPath = targetSubFolder == ".."
            ? GetParentPath(savedPath, destTab.IsRemote)
            : savedPath.TrimEnd('/', '\\') + sep + targetSubFolder;
        if (subPath == null) return;

        try
        {
            destTab.Path = subPath;
            if (files.Count > 1) Log($"📦 Drag-transferring {files.Count} items into {targetSubFolder}…");
            foreach (var f in files)
            {
                if (f.Name == "..") continue;
                await DoTransfer(f, sourceTab, destTab);
            }
        }
        finally
        {
            destTab.Path = savedPath;
        }
    }

    // Drop on a folder row in the *same* panel: rename inside the connection
    // (server-side mv for SFTP, File.Move/Directory.Move locally). Much faster
    // than a copy because no bytes cross the wire.
    public async Task MoveFilesToFolderAsync(string panel, IReadOnlyList<FileNode> files, string targetSubFolder)
    {
        if (files.Count == 0) return;
        var tab = panel == "Left" ? ActiveLeftTab : ActiveRightTab;
        if (tab == null) return;

        var sep = tab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var basePath = tab.Path.TrimEnd('/', '\\');
        var destFolder = targetSubFolder == ".."
            ? GetParentPath(tab.Path, tab.IsRemote)
            : basePath + sep + targetSubFolder;
        if (destFolder == null) return;

        if (files.Count > 1) Log($"➡ Moving {files.Count} items into {targetSubFolder}…");
        var moved = 0;
        foreach (var f in files)
        {
            if (f.Name == "..") continue;
            var src = basePath + sep + f.Name;
            var dst = destFolder.TrimEnd('/', '\\') + sep + f.Name;
            if (src == dst) continue;
            try
            {
                if (tab.IsRemote && tab.Sftp != null)
                {
                    await tab.Sftp.RenameRemoteAsync(src, dst);
                }
                else
                {
                    await Task.Run(() =>
                    {
                        if (f.IsDirectory) Directory.Move(src, dst);
                        else File.Move(src, dst, overwrite: false);
                    });
                }
                Log($"➡ Moved: {f.Name} → {targetSubFolder}/");
                moved++;
            }
            catch (Exception ex)
            {
                Log($"❌ Move failed for {f.Name}: {ex.Message}");
            }
        }

        if (moved > 0)
        {
            if (tab.IsRemote) await RefreshRemoteFilesAsync(tab);
            else await RefreshLocalFilesAsync(tab);
        }
    }

    // Returns the parent of a remote-ish or local-ish path, or null if the
    // given path has no parent (root).
    private static string? GetParentPath(string path, bool isRemote)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (isRemote)
        {
            if (path == "/") return null;
            var trimmed = path.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            if (idx <= 0) return "/";
            return trimmed.Substring(0, idx);
        }
        try
        {
            var p = System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(p) ? null : p;
        }
        catch { return null; }
    }

    // ── Multi-select shortcuts (Ctrl+A / Escape) ───────────────
    [RelayCommand]
    private void SelectAllInActivePanel()
    {
        var tab = FocusedPanel == "Right" ? ActiveRightTab : ActiveLeftTab;
        if (tab == null) return;
        foreach (var f in tab.VisibleFiles)
            if (f.Name != "..") f.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelectionInActivePanel()
    {
        var tab = FocusedPanel == "Right" ? ActiveRightTab : ActiveLeftTab;
        if (tab == null) return;
        foreach (var f in tab.VisibleFiles) f.IsSelected = false;
        if (FocusedPanel == "Right") _rightAnchorIndex = -1;
        else _leftAnchorIndex = -1;
    }

    // ── Multi-select state ─────────────────────────────────────
    // Anchor index per panel — the row a Shift+click range selects from.
    // -1 means "no anchor yet"; the next plain or Ctrl+click sets it.
    // Indices reference the tab's VisibleFiles (what the user actually sees).
    private int _leftAnchorIndex = -1;
    private int _rightAnchorIndex = -1;

    // Click selector. `panel` is "Left" or "Right". `mods` carries the live key
    // modifiers from the pointer event so the VM can tell plain/Ctrl/Shift apart.
    public void HandleFileRowClick(string panel, FileNode file, Avalonia.Input.KeyModifiers mods)
    {
        var tab = panel == "Left" ? ActiveLeftTab : ActiveRightTab;
        if (tab == null) return;
        var list = tab.VisibleFiles;
        var idx = list.IndexOf(file);
        if (idx < 0) return;

        // ".." is navigation-only — never participates in selection.
        if (file.Name == "..") return;

        var ctrl  = (mods & Avalonia.Input.KeyModifiers.Control) != 0;
        var shift = (mods & Avalonia.Input.KeyModifiers.Shift) != 0;
        ref var anchor = ref (panel == "Left" ? ref _leftAnchorIndex : ref _rightAnchorIndex);

        if (shift && anchor >= 0 && anchor < list.Count)
        {
            // Range select from anchor → idx (inclusive). Wipe anything outside
            // the range so Shift+click feels like a fresh range, not additive.
            var lo = Math.Min(anchor, idx);
            var hi = Math.Max(anchor, idx);
            for (var i = 0; i < list.Count; i++)
            {
                var f = list[i];
                if (f.Name == "..") { f.IsSelected = false; continue; }
                f.IsSelected = i >= lo && i <= hi;
            }
            // Anchor unchanged — repeated Shift+clicks pivot off the same start.
            return;
        }

        if (ctrl)
        {
            file.IsSelected = !file.IsSelected;
            anchor = idx;
            return;
        }

        // Plain click: clear all then select this row, set new anchor.
        foreach (var f in list) f.IsSelected = false;
        file.IsSelected = true;
        anchor = idx;
    }

    // Pulls every selected non-".." entry out of a panel. Used by bulk transfer
    // and (eventually) bulk delete. Falls back to a single-item list when the
    // user clicked a row without ever using the selection model.
    public List<FileNode> GetSelection(string panel, FileNode? clickedFallback = null)
    {
        var tab = panel == "Left" ? ActiveLeftTab : ActiveRightTab;
        if (tab == null) return new();
        var sel = tab.VisibleFiles.Where(f => f.IsSelected && f.Name != "..").ToList();
        if (sel.Count == 0 && clickedFallback != null && clickedFallback.Name != "..")
            sel.Add(clickedFallback);
        return sel;
    }

    // ── Folder size (on demand, #13) ───────────────────────────
    // Recursively sums every file under a folder. Local walks happen via
    // System.IO; remote walks via SftpService. The folder's ComputedSize is
    // set to -1 (= "…" placeholder) immediately so the user sees feedback,
    // then to the real total when the walk completes. Errors fall back to 0.
    [RelayCommand]
    private async Task ComputeFolderSizeAsync(FileNode? file)
    {
        if (file == null || !file.IsDirectory || file.Name == "..") return;

        TabItem? tab = null;
        if (ActiveLeftTab?.Files?.Contains(file) == true) tab = ActiveLeftTab;
        else if (ActiveRightTab?.Files?.Contains(file) == true) tab = ActiveRightTab;
        if (tab == null) return;

        // Already computing or done: ignore re-clicks.
        if (file.ComputedSize == -1) return;

        var sep = tab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var fullPath = tab.Path.TrimEnd('/', '\\') + sep + file.Name;

        file.ComputedSize = -1;
        try
        {
            long total;
            if (tab.IsRemote)
            {
                if (tab.Sftp == null) throw new InvalidOperationException("Not connected.");
                total = await tab.Sftp.ComputeRemoteFolderSizeAsync(fullPath);
            }
            else
            {
                total = await Task.Run(() => SumLocalFolderSize(fullPath));
            }
            file.ComputedSize = total;
            Log($"📏 {file.Name}: {FormatSize(total)}");
        }
        catch (Exception ex)
        {
            file.ComputedSize = null;
            Log($"⚠ Size walk failed for {file.Name}: {ex.Message}");
        }
    }

    // Shared helper for the local walk. Permission errors on a subdirectory
    // skip that subtree rather than aborting the whole sum.
    private static long SumLocalFolderSize(string path)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try { total += new FileInfo(f).Length; } catch { /* file vanished */ }
                }
                foreach (var d in Directory.EnumerateDirectories(dir)) stack.Push(d);
            }
            catch { /* unreadable directory — skip */ }
        }
        return total;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ── Clipboard: Copy / Cut / Paste ──────────────────────────
    // Modern Explorer parity:
    //   • Multi-select aware — Copy/Cut on a selected row captures the whole
    //     selection; otherwise just the clicked row.
    //   • Copy preserves the source; Cut moves it (cross-tab cut = transfer +
    //     delete source). The cut visual (dim) lives on the source FileNode.
    //   • Paste targets are the active panel directory or a clicked folder row;
    //     an empty-area context menu triggers the panel-level paste.
    //   • Conflicts resolved per-item (or "Apply to all") with Replace / Skip /
    //     Rename. Same-folder paste of a copy auto-suffixes "- Copia" silently.

    [RelayCommand]
    private void CopyToClipboard(FileNode? file) => CaptureClipboard(file, ClipboardOperation.Copy);

    [RelayCommand]
    private void CutToClipboard(FileNode? file)  => CaptureClipboard(file, ClipboardOperation.Cut);

    // Ctrl+C / Ctrl+X bindings: act on the focused panel's selection (or last
    // single-selected row). No FileNode argument because the keystroke fires
    // at the window level, not from a row.
    [RelayCommand]
    private void CopyActiveSelection() => CaptureFromActivePanel(ClipboardOperation.Copy);

    [RelayCommand]
    private void CutActiveSelection()  => CaptureFromActivePanel(ClipboardOperation.Cut);

    private void CaptureFromActivePanel(ClipboardOperation op)
    {
        var panel = FocusedPanel == "Right" ? "Right" : "Left";
        var tab = panel == "Right" ? ActiveRightTab : ActiveLeftTab;
        if (tab == null) return;
        var batch = GetSelection(panel);
        if (batch.Count == 0) return;
        CaptureClipboardBatch(batch, tab, op);
    }

    // Wraps the single-row context-menu commands. If the clicked file is part
    // of a multi-selection, the whole selection is captured; otherwise only
    // the clicked row is. Mirrors how Explorer handles right-click on selected
    // vs unselected rows.
    private void CaptureClipboard(FileNode? file, ClipboardOperation op)
    {
        if (file == null || file.Name == "..") return;
        var tab = FindTabFor(file);
        if (tab == null) return;

        var panel = ReferenceEquals(tab, ActiveLeftTab) ? "Left" : "Right";
        var batch = file.IsSelected ? GetSelection(panel, file) : new List<FileNode> { file };
        CaptureClipboardBatch(batch, tab, op);
    }

    private void CaptureClipboardBatch(IList<FileNode> batch, TabItem tab, ClipboardOperation op)
    {
        if (batch.Count == 0) return;

        // Clear the previous cut visual before overwriting the clipboard so
        // the user never sees stale "ghost" rows from an abandoned cut.
        ClearClipboardCutVisual();

        var sep = tab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var basePath = tab.Path.TrimEnd('/', '\\');

        _clipboardEntries = batch.Where(f => f.Name != "..")
            .Select(f => new ClipboardEntry
            {
                Name = f.Name,
                IsDirectory = f.IsDirectory,
                Size = f.Size,
                SourcePath = basePath + sep + f.Name,
                IsRemote = tab.IsRemote,
                SourceTabId = tab.Id,
                SourceNode = f
            })
            .ToList();
        ClipboardOperation = op;
        OnPropertyChanged(nameof(HasClipboardEntries));

        if (op == ClipboardOperation.Cut)
            foreach (var e in _clipboardEntries)
                if (e.SourceNode != null) e.SourceNode.IsCut = true;

        var verb = op == ClipboardOperation.Cut ? "Cut" : "Copied";
        var glyph = op == ClipboardOperation.Cut ? "✂" : "📋";
        var label = _clipboardEntries.Count == 1
            ? _clipboardEntries[0].Name
            : $"{_clipboardEntries.Count} items";
        Log($"{glyph} {verb}: {label}");
    }

    private void ClearClipboardCutVisual()
    {
        foreach (var e in _clipboardEntries)
            if (e.SourceNode != null) e.SourceNode.IsCut = false;
    }

    private void ClearClipboard()
    {
        ClearClipboardCutVisual();
        _clipboardEntries = new();
        ClipboardOperation = ClipboardOperation.None;
        OnPropertyChanged(nameof(HasClipboardEntries));
    }

    // Ctrl+V — paste into the focused panel's active tab.
    [RelayCommand]
    private async Task PasteToActivePanelAsync()
    {
        var panel = FocusedPanel == "Right" ? "Right" : "Left";
        await PasteToPanelAsync(panel);
    }

    // Wired to the empty-area context menu and the Ctrl+V binding.
    [RelayCommand]
    private async Task PasteToPanelAsync(string? panel)
    {
        if (!HasClipboardEntries) return;
        var p = panel == "Right" ? "Right" : "Left";
        var destTab = p == "Right" ? ActiveRightTab : ActiveLeftTab;
        if (destTab == null) { Log("⚠ Paste: no active tab in this panel."); return; }
        await PasteEntriesAsync(destTab, destTab.Path);
    }

    // Wired to "Paste into folder" on a folder row's context menu.
    [RelayCommand]
    private async Task PasteIntoFolderAsync(FileNode? destFolder)
    {
        if (destFolder == null || !destFolder.IsDirectory) return;
        if (!HasClipboardEntries) return;

        var destTab = FindTabFor(destFolder);
        if (destTab == null) return;

        var sep = destTab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var basePath = destTab.Path.TrimEnd('/', '\\');
        string destPath;
        if (destFolder.Name == "..")
        {
            destPath = GetParentPath(destTab.Path, destTab.IsRemote) ?? destTab.Path;
        }
        else
        {
            destPath = basePath + sep + destFolder.Name;
        }

        await PasteEntriesAsync(destTab, destPath);
    }

    // Core paste pipeline. Handles each entry in the clipboard with conflict
    // resolution; optimises in-place rename/copy when source and destination
    // share the same connection; falls back to DoTransfer for cross-tab paste.
    private async Task PasteEntriesAsync(TabItem destTab, string destDirPath)
    {
        if (_clipboardEntries.Count == 0) return;
        var entries = _clipboardEntries.ToList();
        var op = ClipboardOperation;

        var sep = destTab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var trimmedDest = destDirPath.TrimEnd('/', '\\');
        if (trimmedDest.Length == 0) trimmedDest = sep;

        if (entries.Count > 1)
        {
            var verb = op == ClipboardOperation.Cut ? "Moving" : "Pasting";
            Log($"📋 {verb} {entries.Count} item(s) → {destDirPath}");
        }

        // Sticky resolution selected via "Apply to all" — null means "ask".
        ConflictResolution? blanket = null;
        bool anyChange = false;
        var sourceTabsToRefresh = new HashSet<string>();
        var entriesToRemoveFromClipboard = new List<ClipboardEntry>();

        foreach (var entry in entries)
        {
            // Source tab still alive? Cut-from-closed-tab is not supported —
            // we fall back to skipping the entry rather than crashing.
            var sourceTab = ResolveTabById(entry.SourceTabId, entry.IsRemote);
            if (sourceTab == null)
            {
                Log($"⚠ Source tab unavailable; skipping {entry.Name}.");
                continue;
            }

            var sameConnection = entry.IsRemote == destTab.IsRemote
                                 && entry.SourceTabId == destTab.Id
                                 && (!entry.IsRemote || (sourceTab.Sftp != null && destTab.Sftp != null));

            var srcPath = entry.SourcePath;
            var srcDir = ParentOf(srcPath, entry.IsRemote);
            bool sameFolderCopy = sameConnection
                                  && op == ClipboardOperation.Copy
                                  && PathsEqual(srcDir, trimmedDest, entry.IsRemote);

            // Decide the destination name. Same-folder Copy always gets the
            // "- Copia" auto-suffix; otherwise we ask only when the target name
            // already exists.
            string targetName = entry.Name;
            string destPath = trimmedDest + sep + targetName;
            bool isCutInPlace = sameConnection
                                && op == ClipboardOperation.Cut
                                && PathsEqual(srcDir, trimmedDest, entry.IsRemote);
            if (isCutInPlace)
            {
                // Cutting an item back into its own folder is a no-op.
                continue;
            }

            if (sameFolderCopy)
            {
                targetName = await SuggestCopyNameAsync(destTab, trimmedDest, entry.Name, entry.IsDirectory);
                destPath = trimmedDest + sep + targetName;
            }
            else
            {
                // Conflict check: only when the destination already exists.
                bool exists = await PathExistsAsync(destTab, destPath);
                if (exists)
                {
                    ConflictResolution resolution;
                    if (blanket.HasValue)
                    {
                        resolution = blanket.Value;
                    }
                    else
                    {
                        resolution = await AskPasteConflictAsync(entry, srcPath, destPath, sourceTab, destTab);
                        if (_lastApplyToAll) blanket = resolution;
                    }

                    if (resolution == ConflictResolution.Cancel)
                    {
                        Log("✋ Paste cancelled.");
                        break;
                    }
                    if (resolution == ConflictResolution.Skip) continue;
                    if (resolution == ConflictResolution.Rename)
                    {
                        targetName = await SuggestCopyNameAsync(destTab, trimmedDest, entry.Name, entry.IsDirectory);
                        destPath = trimmedDest + sep + targetName;
                    }
                    // Replace just falls through with the original destPath.
                }
            }

            // Execute the move/copy.
            try
            {
                if (sameConnection)
                {
                    if (op == ClipboardOperation.Cut)
                    {
                        // Server-side / OS-level rename — fast, no bytes copied.
                        // Replace into existing target requires deleting first
                        // because rename-over isn't reliable on SFTP or NTFS.
                        await EnsureNoCollisionAsync(destTab, destPath, entry.IsDirectory);
                        await RenameSameConnectionAsync(destTab, srcPath, destPath, entry.IsDirectory);
                    }
                    else // Copy
                    {
                        if (entry.IsRemote && destTab.Sftp != null)
                        {
                            await EnsureNoCollisionAsync(destTab, destPath, entry.IsDirectory);
                            await destTab.Sftp.CopyRemoteAsync(srcPath, destPath);
                        }
                        else
                        {
                            await EnsureNoCollisionAsync(destTab, destPath, entry.IsDirectory);
                            await Task.Run(() =>
                            {
                                if (entry.IsDirectory) CopyDirectoryLocal(srcPath, destPath);
                                else File.Copy(srcPath, destPath, overwrite: false);
                            });
                        }
                    }
                    Log(op == ClipboardOperation.Cut
                        ? $"➡ Moved \"{entry.Name}\" → {destDirPath}"
                        : $"📋 Copied \"{entry.Name}\" → {destDirPath}{(targetName != entry.Name ? $" (as {targetName})" : "")}");
                }
                else
                {
                    // Cross-connection paste — synthesize a transfer through the
                    // existing engine. Use temporary tab snapshots so DoTransfer
                    // computes the right paths without disturbing the live tabs.
                    var tempSrc = new TabItem
                    {
                        Id = sourceTab.Id, Name = sourceTab.Name,
                        Path = ParentOf(srcPath, entry.IsRemote) ?? sourceTab.Path,
                        IsRemote = entry.IsRemote, Sftp = sourceTab.Sftp
                    };
                    var tempDst = new TabItem
                    {
                        Id = destTab.Id, Name = destTab.Name,
                        Path = trimmedDest,
                        IsRemote = destTab.IsRemote, Sftp = destTab.Sftp
                    };
                    var fileSnapshot = new FileNode
                    {
                        Name = targetName == entry.Name ? entry.Name : targetName,
                        IsDirectory = entry.IsDirectory,
                        Size = entry.Size
                    };
                    // If the renamed target differs from source name we need to
                    // first transfer with original name then rename — but the
                    // simpler path is to transfer using the source name and rely
                    // on EnsureNoCollision having cleared anything blocking. For
                    // user-chosen rename, transfer the source bytes then rename.
                    if (targetName != entry.Name)
                    {
                        var stagingPath = trimmedDest + sep + entry.Name;
                        await EnsureNoCollisionAsync(destTab, stagingPath, entry.IsDirectory);
                        var origName = new FileNode { Name = entry.Name, IsDirectory = entry.IsDirectory, Size = entry.Size };
                        await DoTransfer(origName, tempSrc, tempDst);
                        // Rename happens after the transfer worker finishes — fire-and-forget
                        // is wrong here because the user expects a stable name. Instead, we
                        // schedule the rename once the destination listing reflects the new
                        // file. Cheap implementation: poll briefly. Robust: refresh + rename
                        // after the panel refresh fires. We use a tiny retry loop.
                        _ = Task.Run(async () =>
                        {
                            for (var i = 0; i < 50; i++)
                            {
                                await Task.Delay(200);
                                if (await PathExistsAsync(destTab, stagingPath))
                                {
                                    try
                                    {
                                        await RenameSameConnectionAsync(destTab, stagingPath, destPath, entry.IsDirectory);
                                        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshTabAsync(destTab));
                                    }
                                    catch (Exception ex) { Log($"⚠ Auto-rename after transfer failed: {ex.Message}"); }
                                    break;
                                }
                            }
                        });
                    }
                    else
                    {
                        await EnsureNoCollisionAsync(destTab, destPath, entry.IsDirectory);
                        await DoTransfer(fileSnapshot, tempSrc, tempDst);
                    }

                    if (op == ClipboardOperation.Cut)
                    {
                        // Cross-connection cut: schedule the source delete after
                        // the transfer settles. Same retry pattern.
                        _ = Task.Run(async () =>
                        {
                            for (var i = 0; i < 100; i++)
                            {
                                await Task.Delay(300);
                                if (await PathExistsAsync(destTab, destPath))
                                {
                                    try
                                    {
                                        if (entry.IsRemote && sourceTab.Sftp != null)
                                            await sourceTab.Sftp.DeleteRemoteAsync(srcPath, entry.IsDirectory);
                                        else
                                            await Task.Run(() =>
                                            {
                                                if (entry.IsDirectory) Directory.Delete(srcPath, true);
                                                else File.Delete(srcPath);
                                            });
                                        Log($"🗑 Removed source after move: {entry.Name}");
                                        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshTabAsync(sourceTab));
                                    }
                                    catch (Exception ex) { Log($"⚠ Source cleanup after move failed: {ex.Message}"); }
                                    break;
                                }
                            }
                        });
                    }
                }

                anyChange = true;
                sourceTabsToRefresh.Add(sourceTab.Id);
                if (op == ClipboardOperation.Cut) entriesToRemoveFromClipboard.Add(entry);
            }
            catch (Exception ex)
            {
                Log($"❌ Paste failed for {entry.Name}: {ex.Message}");
            }
        }

        // Refresh destination panel + any source tabs that lost items.
        if (anyChange)
        {
            await RefreshTabAsync(destTab);
            foreach (var id in sourceTabsToRefresh)
            {
                var tab = LeftTabs.Concat(RightTabs).FirstOrDefault(t => t.Id == id);
                if (tab != null && !ReferenceEquals(tab, destTab))
                    await RefreshTabAsync(tab);
            }
        }

        // Cut consumes the clipboard once successful; Copy persists so the
        // user can paste again, mirroring Explorer.
        if (op == ClipboardOperation.Cut)
        {
            if (entriesToRemoveFromClipboard.Count == _clipboardEntries.Count)
            {
                ClearClipboard();
            }
            else
            {
                foreach (var e in entriesToRemoveFromClipboard)
                {
                    if (e.SourceNode != null) e.SourceNode.IsCut = false;
                    _clipboardEntries.Remove(e);
                }
                if (_clipboardEntries.Count == 0) ClearClipboard();
                else OnPropertyChanged(nameof(HasClipboardEntries));
            }
        }
    }

    // ── Paste support helpers ─────────────────────────────────
    private async Task<ConflictResolution> AskPasteConflictAsync(
        ClipboardEntry entry, string srcPath, string dstPath,
        TabItem sourceTab, TabItem destTab)
    {
        long destSize = 0;
        DateTime destModified = default;
        int destItemCount = 0;
        if (destTab.IsRemote && destTab.Sftp != null)
        {
            var stat = await destTab.Sftp.StatRemoteAsync(dstPath);
            destSize = stat.Length; destModified = stat.LastWriteTime;
            if (stat.Exists && stat.IsDirectory)
                destItemCount = await destTab.Sftp.CountRemoteEntriesAsync(dstPath);
        }
        else if (Directory.Exists(dstPath))
        {
            try
            {
                var di = new DirectoryInfo(dstPath);
                destModified = di.LastWriteTime;
                destItemCount = di.EnumerateFileSystemInfos().Count();
            }
            catch { }
        }
        else if (File.Exists(dstPath))
        {
            try { var fi = new FileInfo(dstPath); destSize = fi.Length; destModified = fi.LastWriteTime; }
            catch { }
        }

        long srcSize = entry.Size;
        DateTime srcModified = default;
        int srcItemCount = 0;
        if (entry.IsRemote && sourceTab.Sftp != null)
        {
            var stat = await sourceTab.Sftp.StatRemoteAsync(srcPath);
            srcSize = stat.Length; srcModified = stat.LastWriteTime;
            if (entry.IsDirectory) srcItemCount = await sourceTab.Sftp.CountRemoteEntriesAsync(srcPath);
        }
        else
        {
            try
            {
                if (entry.IsDirectory)
                {
                    var di = new DirectoryInfo(srcPath);
                    srcModified = di.LastWriteTime;
                    srcItemCount = di.EnumerateFileSystemInfos().Count();
                }
                else
                {
                    var fi = new FileInfo(srcPath);
                    srcSize = fi.Length; srcModified = fi.LastWriteTime;
                }
            }
            catch { }
        }

        var suggested = await SuggestCopyNameAsync(destTab, ParentOf(dstPath, destTab.IsRemote) ?? destTab.Path, entry.Name, entry.IsDirectory);

        var info = new ConflictInfo
        {
            Mode = ConflictMode.Paste,
            Name = entry.Name,
            IsFolder = entry.IsDirectory,
            SourcePath = srcPath,
            SourceSize = srcSize,
            SourceModified = srcModified,
            SourceItemCount = srcItemCount,
            DestPath = dstPath,
            DestSize = destSize,
            DestModified = destModified,
            DestItemCount = destItemCount,
            SuggestedNewName = suggested
        };
        PendingConflict = info;
        IsConflictOpen = true;
        var resolution = await info.Resolution.Task;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConflictOpen = false;
            // Keep PendingConflict around momentarily so the caller can read
            // ApplyToAll; the caller clears it after reading.
        });
        var apply = info.ApplyToAll;
        // Drop reference now that we've read it.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (PendingConflict == info) PendingConflict = null; });
        // Encode "apply to all" in PendingConflict was already read above; we
        // expose it via the info object that the caller still holds via the
        // shared field — but we already cleared it. Re-attach for caller read.
        // Simpler: store on a side-channel:
        _lastApplyToAll = apply;
        return resolution;
    }

    // Side-channel for AskPasteConflictAsync → PasteEntriesAsync. Caller reads
    // immediately after the await so cross-iteration races aren't possible.
    private bool _lastApplyToAll;

    // Generates the next free "{name} - Copia[ ({n})]{ext}" sibling name in
    // the destination directory. Mirrors Windows Explorer's behaviour of
    // re-appending suffixes for repeated same-folder pastes.
    private async Task<string> SuggestCopyNameAsync(TabItem destTab, string destDir, string originalName, bool isDirectory)
    {
        var sep = destTab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var basePath = destDir.TrimEnd('/', '\\');
        if (basePath.Length == 0) basePath = sep;

        // Split name into stem + extension. Folders never get an extension split.
        string stem, ext;
        if (isDirectory)
        {
            stem = originalName;
            ext = "";
        }
        else
        {
            ext = Path.GetExtension(originalName);
            stem = ext.Length > 0 ? originalName.Substring(0, originalName.Length - ext.Length) : originalName;
        }

        // Strip an existing trailing " - Copia" / " - Copia (n)" so cascading
        // pastes don't keep growing ("a - Copia - Copia - Copia.txt").
        var baseStem = StripCopySuffix(stem);

        for (int n = 1; n <= 999; n++)
        {
            var candidate = n == 1
                ? $"{baseStem} - Copia{ext}"
                : $"{baseStem} - Copia ({n}){ext}";
            var fullPath = basePath + sep + candidate;
            if (!await PathExistsAsync(destTab, fullPath)) return candidate;
        }
        // Fallback: timestamp suffix. Vanishingly unlikely.
        return $"{baseStem} - Copia ({DateTime.Now:yyyyMMdd-HHmmss}){ext}";
    }

    internal static string StripCopySuffix(string stem)
    {
        // Matches " - Copia" or " - Copia (NN)" at end of stem (case-insensitive).
        var m = System.Text.RegularExpressions.Regex.Match(stem,
            @"\s-\sCopia(\s\(\d+\))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? stem.Substring(0, m.Index) : stem;
    }

    private async Task<bool> PathExistsAsync(TabItem tab, string path)
    {
        if (tab.IsRemote && tab.Sftp != null)
        {
            var stat = await tab.Sftp.StatRemoteAsync(path);
            return stat.Exists;
        }
        return File.Exists(path) || Directory.Exists(path);
    }

    private async Task EnsureNoCollisionAsync(TabItem destTab, string destPath, bool isDirectory)
    {
        // Blanket-replace path: if anything is at the destination, delete it
        // first. Caller has already gathered the user's consent (Replace).
        if (destTab.IsRemote && destTab.Sftp != null)
        {
            var stat = await destTab.Sftp.StatRemoteAsync(destPath);
            if (stat.Exists) await destTab.Sftp.DeleteRemoteAsync(destPath, stat.IsDirectory);
        }
        else
        {
            if (Directory.Exists(destPath)) await Task.Run(() => Directory.Delete(destPath, true));
            else if (File.Exists(destPath)) await Task.Run(() => File.Delete(destPath));
        }
    }

    private async Task RenameSameConnectionAsync(TabItem destTab, string srcPath, string dstPath, bool isDirectory)
    {
        if (destTab.IsRemote && destTab.Sftp != null)
        {
            await destTab.Sftp.RenameRemoteAsync(srcPath, dstPath);
        }
        else
        {
            await Task.Run(() =>
            {
                if (isDirectory) Directory.Move(srcPath, dstPath);
                else File.Move(srcPath, dstPath);
            });
        }
    }

    // Recursive local copy. System.IO has no direct equivalent, so we walk.
    private static void CopyDirectoryLocal(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string? ParentOf(string path, bool isRemote)
        => GetParentPath(path, isRemote);

    private static bool PathsEqual(string? a, string? b, bool isRemote)
    {
        if (a == null || b == null) return false;
        var na = a.TrimEnd('/', '\\');
        var nb = b.TrimEnd('/', '\\');
        return isRemote
            ? string.Equals(na, nb, StringComparison.Ordinal)
            : string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    private TabItem? ResolveTabById(string id, bool isRemote)
        => LeftTabs.Concat(RightTabs).FirstOrDefault(t => t.Id == id && t.IsRemote == isRemote);

    private Task RefreshTabAsync(TabItem tab)
        => tab.IsRemote ? RefreshRemoteFilesAsync(tab) : RefreshLocalFilesAsync(tab);

    [RelayCommand]
    private void OpenCreateFolder(string panel)
    {
        var tab = panel == "Left" ? ActiveLeftTab : ActiveRightTab;
        if (tab == null) return;

        CreateFolderTab = tab;
        CreateFolderName = "New Folder";
        IsCreateFolderOpen = true;
    }

    [RelayCommand]
    private void CancelCreateFolder()
    {
        IsCreateFolderOpen = false;
        CreateFolderTab = null;
        CreateFolderName = "";
    }

    [RelayCommand]
    private async Task ConfirmCreateFolder()
    {
        if (CreateFolderTab == null) { CancelCreateFolder(); return; }
        var tab = CreateFolderTab;
        var newName = (CreateFolderName ?? "").Trim();
        IsCreateFolderOpen = false;

        if (string.IsNullOrEmpty(newName) || newName.Contains('/') || newName.Contains('\\'))
        {
            Log("⚠ Invalid folder name.");
            return;
        }

        try
        {
            if (tab.IsRemote)
            {
                var newPath = tab.Path.TrimEnd('/') + "/" + newName;
                await tab.Sftp!.CreateRemoteDirectoryAsync(newPath);
                await RefreshRemoteFilesAsync(tab);
            }
            else
            {
                var newPath = Path.Combine(tab.Path, newName);
                Directory.CreateDirectory(newPath);
                await RefreshLocalFilesAsync(tab);
            }
            Log($"✓ Created folder: {newName}");
        }
        catch (Exception ex)
        {
            Log($"❌ Create folder failed: {ex.Message}");
        }
    }

    // ── Transfers & Actions ────────────────────────────────────
    [RelayCommand]
    private async Task FileActionAsync(FileNode? file)
    {
        if (file == null) return;

        // Find which tab has this file
        TabItem? tab = null;
        if (ActiveLeftTab?.Files?.Contains(file) == true) tab = ActiveLeftTab;
        else if (ActiveRightTab?.Files?.Contains(file) == true) tab = ActiveRightTab;

        if (tab == null) return;

        if (file.IsDirectory || file.Name == "..")
        {
            await OpenItemAsync(tab, file);
        }
        else
        {
            await TransferFileAsync(file);
        }
    }

    [RelayCommand]
    private async Task TransferFileAsync(FileNode? file)
    {
        if (file == null || file.Name == "..") return;

        // Determine source and destination panels by which tab owns the file.
        TabItem? sourceTab = null;
        TabItem? destTab = null;
        string panel = "";

        if (ActiveLeftTab?.Files?.Contains(file) == true)
        {
            sourceTab = ActiveLeftTab;
            destTab = ActiveRightTab;
            panel = "Left";
        }
        else if (ActiveRightTab?.Files?.Contains(file) == true)
        {
            sourceTab = ActiveRightTab;
            destTab = ActiveLeftTab;
            panel = "Right";
        }

        if (sourceTab == null || destTab == null)
        {
            Log("⚠ Select source and destination panels.");
            return;
        }

        // Multi-select expansion: if the clicked file is part of the panel's
        // selection, transfer everything selected. Otherwise operate on the
        // single file (mirrors Windows Explorer right-click semantics).
        var batch = file.IsSelected ? GetSelection(panel, file) : new List<FileNode> { file };
        if (batch.Count > 1)
            Log($"📦 Transferring {batch.Count} items…");

        foreach (var f in batch)
            await DoTransfer(f, sourceTab, destTab);
    }

    // Two-phase transfer:
    //   1. Synchronous-ish setup — paths, conflict prompt (FIFO per user),
    //      append a TransferItem in Queued state to the UI list.
    //   2. Background work — gated by _transferGate. Multiple items beyond the
    //      cap show as "Queued" until a slot frees. The caller awaits DoTransfer
    //      only for phase 1, so a foreach over a multi-select queues everything
    //      back-to-back without serializing the network work.
    public async Task DoTransfer(FileNode file, TabItem sourceTab, TabItem destTab)
    {
        var sep = sourceTab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var srcPath = sourceTab.Path.TrimEnd('/', '\\') + sep + file.Name;

        var dSep = destTab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var dstPath = destTab.Path.TrimEnd('/', '\\') + dSep + file.Name;

        bool isR2R = sourceTab.IsRemote && destTab.IsRemote
                     && sourceTab.Sftp != null && destTab.Sftp != null;

        SftpService? sftp = null;
        if (!isR2R)
        {
            sftp = sourceTab.Sftp ?? destTab.Sftp;
            if (sftp == null)
            {
                Log("⚠ No SFTP connection available for transfer.");
                return;
            }
        }

        var proceed = await CheckConflictAsync(file, srcPath, dstPath, sourceTab, destTab);
        if (!proceed)
        {
            Log($"✋ Transfer cancelled: {file.Name}");
            return;
        }

        var item = new TransferItem
        {
            FileName = file.Name,
            SourcePath = srcPath,
            DestPath = dstPath,
            TotalBytes = file.Size,
            Direction = isR2R ? TransferDirection.RemoteToRemote
                      : sourceTab.IsRemote ? TransferDirection.Download
                      : TransferDirection.Upload,
            Status = TransferStatus.Queued
        };

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Transfers.Add(item);
            BottomTab = "Transfers";
        });

        // Detach the actual IO so the queue can fill faster than the network
        // drains. The semaphore is what keeps the parallel count bounded.
        _ = Task.Run(() => RunTransferWorkAsync(item, file, sourceTab, destTab, sftp, isR2R, srcPath, dstPath));
    }

    private async Task RunTransferWorkAsync(TransferItem item, FileNode file,
        TabItem sourceTab, TabItem destTab, SftpService? sftp, bool isR2R,
        string srcPath, string dstPath)
    {
        await _transferGate.WaitAsync();
        try
        {
            if (file.IsDirectory)
            {
                Log($"📦 Transferring folder: {file.Name}");
                if (isR2R)
                {
                    await sourceTab.Sftp!.PipeDirectoryToRemoteAsync(
                        srcPath, destTab.Sftp!, dstPath,
                        (name, status) =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                item.Status = status;
                                item.FileName = name;
                            });
                        });
                }
                else
                {
                    bool isUpload = !sourceTab.IsRemote;
                    await sftp!.TransferRecursiveAsync(srcPath, dstPath, isUpload,
                        (name, status) =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                item.Status = status;
                                item.FileName = name;
                            });
                        });
                }
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => item.Status = TransferStatus.InProgress);
                if (isR2R)
                    await sourceTab.Sftp!.PipeFileToRemoteAsync(srcPath, destTab.Sftp!, dstPath);
                else if (sourceTab.IsRemote)
                    await sftp!.DownloadFileAsync(srcPath, dstPath);
                else
                    await sftp!.UploadFileAsync(srcPath, dstPath);
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                item.Status = TransferStatus.Completed;
                item.TransferredBytes = item.TotalBytes;
            });
            Log($"✅ Transfer complete: {file.Name}");

            if (destTab.IsRemote) await RefreshRemoteFilesAsync(destTab);
            else await RefreshLocalFilesAsync(destTab);
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                item.Status = TransferStatus.Failed;
                item.ErrorMessage = ex.Message;
            });
            Log($"❌ Transfer failed: {ex.Message}");
        }
        finally
        {
            _transferGate.Release();
        }
    }

    // ── Conflict detection ─────────────────────────────────────
    // Returns true if the caller should proceed with the transfer, false if
    // the user cancelled. No conflict (destination missing) ⇒ returns true
    // without opening the modal.
    private async Task<bool> CheckConflictAsync(FileNode file, string srcPath, string dstPath,
        TabItem sourceTab, TabItem destTab)
    {
        // Gather destination metadata first — if nothing is there, no conflict.
        long destSize = 0;
        DateTime destModified = default;
        int destItemCount = 0;
        bool destExists;
        bool destIsDir;

        if (destTab.IsRemote && destTab.Sftp != null)
        {
            var stat = await destTab.Sftp.StatRemoteAsync(dstPath);
            destExists = stat.Exists;
            destIsDir  = stat.IsDirectory;
            destSize   = stat.Length;
            destModified = stat.LastWriteTime;
            if (destExists && destIsDir)
                destItemCount = await destTab.Sftp.CountRemoteEntriesAsync(dstPath);
        }
        else
        {
            if (Directory.Exists(dstPath))
            {
                destExists = true;
                destIsDir = true;
                try
                {
                    var di = new DirectoryInfo(dstPath);
                    destModified = di.LastWriteTime;
                    destItemCount = di.EnumerateFileSystemInfos().Count();
                }
                catch { /* access denied — leave zeros */ }
            }
            else if (File.Exists(dstPath))
            {
                destExists = true;
                destIsDir = false;
                try
                {
                    var fi = new FileInfo(dstPath);
                    destSize = fi.Length;
                    destModified = fi.LastWriteTime;
                }
                catch { /* ignore */ }
            }
            else
            {
                destExists = false;
                destIsDir = false;
            }
        }

        if (!destExists) return true;

        // Gather source metadata. For folders we count one level of entries on
        // the source so the modal can show a comparable "N item(s)" number.
        long srcSize = 0;
        DateTime srcModified = default;
        int srcItemCount = 0;

        if (sourceTab.IsRemote && sourceTab.Sftp != null)
        {
            var stat = await sourceTab.Sftp.StatRemoteAsync(srcPath);
            srcSize = stat.Length;
            srcModified = stat.LastWriteTime;
            if (file.IsDirectory)
                srcItemCount = await sourceTab.Sftp.CountRemoteEntriesAsync(srcPath);
        }
        else
        {
            try
            {
                if (file.IsDirectory)
                {
                    var di = new DirectoryInfo(srcPath);
                    srcModified = di.LastWriteTime;
                    srcItemCount = di.EnumerateFileSystemInfos().Count();
                }
                else
                {
                    var fi = new FileInfo(srcPath);
                    srcSize = fi.Length;
                    srcModified = fi.LastWriteTime;
                }
            }
            catch { /* leave zeros */ }
        }

        var info = new ConflictInfo
        {
            Mode = ConflictMode.Transfer,
            Name = file.Name,
            IsFolder = file.IsDirectory,
            SourcePath = srcPath,
            SourceSize = srcSize,
            SourceModified = srcModified,
            SourceItemCount = srcItemCount,
            DestPath = dstPath,
            DestSize = destSize,
            DestModified = destModified,
            DestItemCount = destItemCount
        };

        PendingConflict = info;
        IsConflictOpen = true;

        var resolution = await info.Resolution.Task;

        // Close on the UI thread — the awaited TCS may resume on a worker.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConflictOpen = false;
            PendingConflict = null;
        });

        // Transfer flow only honours Replace; Skip/Rename are paste-only.
        return resolution == ConflictResolution.Replace;
    }

    // ── Conflict modal commands ───────────────────────────────
    // Modal exposes one subset of buttons depending on PendingConflict.Mode;
    // these commands are wired one-per-button and resolve the TCS accordingly.
    [RelayCommand]
    private void ResolveConflictReplace() => PendingConflict?.Resolution.TrySetResult(ConflictResolution.Replace);

    [RelayCommand]
    private void ResolveConflictSkip()    => PendingConflict?.Resolution.TrySetResult(ConflictResolution.Skip);

    [RelayCommand]
    private void ResolveConflictRename()  => PendingConflict?.Resolution.TrySetResult(ConflictResolution.Rename);

    [RelayCommand]
    private void ResolveConflictCancel()  => PendingConflict?.Resolution.TrySetResult(ConflictResolution.Cancel);

    private void OnTransferProgress(string fileName, long transferred, long total)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var item = Transfers.LastOrDefault(t => t.FileName == fileName);
            if (item != null)
            {
                item.TransferredBytes = transferred;
                item.TotalBytes = total;
            }
        });
    }

    // ── Delete ─────────────────────────────────────────────────
    private TabItem? FindTabFor(FileNode file)
    {
        if (ActiveLeftTab?.Files?.Contains(file) == true) return ActiveLeftTab;
        if (ActiveRightTab?.Files?.Contains(file) == true) return ActiveRightTab;
        return null;
    }

    [RelayCommand]
    private void DeleteFile(FileNode? file)
    {
        if (file == null || file.Name == "..") return;
        var tab = FindTabFor(file);
        if (tab == null) return;

        var panel = ReferenceEquals(tab, ActiveLeftTab) ? "Left" : "Right";
        var batch = file.IsSelected ? GetSelection(panel, file) : new List<FileNode> { file };
        
        _pendingDeleteBatch = batch;
        _pendingDeleteTab = tab;

        if (batch.Count == 1)
        {
            DeleteConfirmMessage = $"¿Estás seguro que deseas eliminar '{batch[0].Name}' permanentemente?";
        }
        else
        {
            DeleteConfirmMessage = $"¿Estás seguro que deseas eliminar estos {batch.Count} elementos permanentemente?";
        }
        
        IsDeleteConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteConfirmOpen = false;
        _pendingDeleteBatch = null;
        _pendingDeleteTab = null;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsDeleteConfirmOpen = false;
        var batch = _pendingDeleteBatch;
        var tab = _pendingDeleteTab;
        
        _pendingDeleteBatch = null;
        _pendingDeleteTab = null;

        if (batch == null || tab == null) return;

        if (batch.Count > 1)
            Log($"🗑 Deleting {batch.Count} items…");

        var sep = tab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var anyDeleted = false;
        foreach (var f in batch)
        {
            var fullPath = tab.Path.TrimEnd('/', '\\') + sep + f.Name;
            try
            {
                if (tab.IsRemote && tab.Sftp != null)
                {
                    await tab.Sftp.DeleteRemoteAsync(fullPath, f.IsDirectory);
                }
                else
                {
                    await Task.Run(() =>
                    {
                        if (f.IsDirectory) Directory.Delete(fullPath, true);
                        else File.Delete(fullPath);
                    });
                }
                Log($"🗑 Deleted: {f.Name}");
                anyDeleted = true;
            }
            catch (Exception ex)
            {
                Log($"❌ Delete failed for {f.Name}: {ex.Message}");
            }
        }

        // Refresh once at the end rather than after every deletion — avoids the
        // listing flickering through N intermediate states for big selections.
        if (anyDeleted)
        {
            if (tab.IsRemote) await RefreshRemoteFilesAsync(tab);
            else await RefreshLocalFilesAsync(tab);
        }
    }

    // ── Rename ─────────────────────────────────────────────────
    [RelayCommand]
    private void OpenRename(FileNode? file)
    {
        if (file == null || file.Name == "..") return;
        var tab = FindTabFor(file);
        if (tab == null) return;

        RenameTarget = file;
        RenameTab = tab;
        RenameNewName = file.Name;
        IsRenameOpen = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenameOpen = false;
        RenameTarget = null;
        RenameTab = null;
        RenameNewName = "";
    }

    [RelayCommand]
    private async Task ConfirmRename()
    {
        if (RenameTarget == null || RenameTab == null) { CancelRename(); return; }
        var file = RenameTarget;
        var tab = RenameTab;
        var newName = (RenameNewName ?? "").Trim();
        IsRenameOpen = false;

        if (string.IsNullOrEmpty(newName) || newName == file.Name || newName.Contains('/') || newName.Contains('\\'))
        {
            Log("⚠ Invalid new name.");
            return;
        }

        var sep = tab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        var oldPath = tab.Path.TrimEnd('/', '\\') + sep + file.Name;
        var newPath = tab.Path.TrimEnd('/', '\\') + sep + newName;

        try
        {
            if (tab.IsRemote && tab.Sftp != null)
            {
                await tab.Sftp.RenameRemoteAsync(oldPath, newPath);
            }
            else
            {
                await Task.Run(() =>
                {
                    if (file.IsDirectory) Directory.Move(oldPath, newPath);
                    else File.Move(oldPath, newPath);
                });
            }

            Log($"✏ Renamed \"{file.Name}\" → \"{newName}\"");
            if (tab.IsRemote) await RefreshRemoteFilesAsync(tab);
            else await RefreshLocalFilesAsync(tab);
        }
        catch (Exception ex)
        {
            Log($"❌ Rename failed: {ex.Message}");
        }
        finally
        {
            RenameTarget = null;
            RenameTab = null;
            RenameNewName = "";
        }
    }

    // ── Breadcrumb navigation + edit mode ──────────────────────
    [RelayCommand]
    private async Task NavigateLeftToPath(string? target)
    {
        if (ActiveLeftTab == null || string.IsNullOrEmpty(target)) return;
        if (ActiveLeftTab.Path == target) return;
        await NavigateAsync(ActiveLeftTab, target);
    }

    [RelayCommand]
    private async Task NavigateRightToPath(string? target)
    {
        if (ActiveRightTab == null || string.IsNullOrEmpty(target)) return;
        if (ActiveRightTab.Path == target) return;
        await NavigateAsync(ActiveRightTab, target);
    }

    [RelayCommand]
    private void BeginEditLeftPath()
    {
        if (ActiveLeftTab == null) return;
        LeftPathEditText = ActiveLeftTab.Path;
        IsLeftPathEditing = true;
    }

    [RelayCommand]
    private void BeginEditRightPath()
    {
        if (ActiveRightTab == null) return;
        RightPathEditText = ActiveRightTab.Path;
        IsRightPathEditing = true;
    }

    [RelayCommand]
    private async Task CommitLeftPath()
    {
        IsLeftPathEditing = false;
        if (ActiveLeftTab == null) return;
        var target = (LeftPathEditText ?? "").Trim();
        if (string.IsNullOrEmpty(target) || target == ActiveLeftTab.Path) return;
        await NavigateAsync(ActiveLeftTab, target);
    }

    [RelayCommand]
    private async Task CommitRightPath()
    {
        IsRightPathEditing = false;
        if (ActiveRightTab == null) return;
        var target = (RightPathEditText ?? "").Trim();
        if (string.IsNullOrEmpty(target) || target == ActiveRightTab.Path) return;
        await NavigateAsync(ActiveRightTab, target);
    }

    [RelayCommand]
    private void CancelLeftPath()  => IsLeftPathEditing = false;

    [RelayCommand]
    private void CancelRightPath() => IsRightPathEditing = false;

    // ── Filter (per panel, toggled with Ctrl+F) ────────────────
    // The filter lives on TabItem so each tab remembers its own query.
    // Ctrl+F operates on the focused panel's active tab; Escape / close
    // clears the query and hides the bar.
    [RelayCommand]
    private void ToggleActiveFilter()
    {
        var tab = FocusedPanel == "Right" ? ActiveRightTab : ActiveLeftTab;
        if (tab == null) return;
        tab.IsFilterActive = !tab.IsFilterActive;
        if (!tab.IsFilterActive) tab.FilterText = "";
    }

    [RelayCommand]
    private void CloseLeftFilter()
    {
        if (ActiveLeftTab == null) return;
        ActiveLeftTab.IsFilterActive = false;
        ActiveLeftTab.FilterText = "";
    }

    [RelayCommand]
    private void CloseRightFilter()
    {
        if (ActiveRightTab == null) return;
        ActiveRightTab.IsFilterActive = false;
        ActiveRightTab.FilterText = "";
    }

    // ── Subtree search (recursive remote find) ─────────────────
    // Opens the Find modal anchored at the focused panel's remote tab.
    // Pre-fills the query from the panel's existing filter so a user typing
    // in the local-filter bar can promote that query into a recursive search
    // with one click, without re-typing.
    [RelayCommand]
    private void OpenSubtreeSearch()
    {
        var panel = FocusedPanel == "Right" ? "Right" : "Left";
        var tab = panel == "Right" ? ActiveRightTab : ActiveLeftTab;
        if (tab == null || !tab.IsRemote || tab.Sftp == null || !tab.IsConnected)
        {
            Log("⚠ Subtree search needs an active remote tab.");
            return;
        }

        _searchSourceTab = tab;
        SearchPanel = panel;
        SearchRoot = tab.Path;
        SearchQuery = string.IsNullOrWhiteSpace(tab.FilterText) ? SearchQuery : tab.FilterText;
        SearchResults.Clear();
        SearchStatus = "";
        IsSearchRunning = false;
        IsSearchOpen = true;
    }

    [RelayCommand]
    private void CloseSubtreeSearch()
    {
        try { _searchCts?.Cancel(); } catch { /* token already disposed */ }
        IsSearchRunning = false;
        IsSearchOpen = false;
    }

    [RelayCommand]
    private async Task RunSubtreeSearchAsync()
    {
        var tab = _searchSourceTab;
        if (tab?.Sftp == null) { CloseSubtreeSearch(); return; }
        var query = (SearchQuery ?? "").Trim();
        if (query.Length == 0) { SearchStatus = "Enter a query."; return; }

        // Cancel any in-flight search before starting a new one.
        try { _searchCts?.Cancel(); } catch { }
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        SearchResults.Clear();
        SearchStatus = "Searching…";
        IsSearchRunning = true;

        var root = SearchRoot;
        var sftp = tab.Sftp;

        try
        {
            await sftp.SearchSubtreeAsync(
                root, query,
                onHit: hit => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SearchResults.Add(hit);
                    SearchStatus = $"{SearchResults.Count} match{(SearchResults.Count == 1 ? "" : "es")}…";
                }),
                onProgress: dir => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!IsSearchRunning) return;
                    var trimmed = dir.Length > 60 ? "…" + dir.Substring(dir.Length - 59) : dir;
                    SearchStatus = $"{SearchResults.Count} match{(SearchResults.Count == 1 ? "" : "es")} · scanning {trimmed}";
                }),
                max: SearchHitLimit,
                ct: ct);

            var capped = SearchResults.Count >= SearchHitLimit ? " (limit reached)" : "";
            SearchStatus = $"Done — {SearchResults.Count} match{(SearchResults.Count == 1 ? "" : "es")}{capped}.";
        }
        catch (OperationCanceledException)
        {
            SearchStatus = $"Cancelled — {SearchResults.Count} match{(SearchResults.Count == 1 ? "" : "es")} so far.";
        }
        catch (Exception ex)
        {
            SearchStatus = $"Error: {ex.Message}";
            Log($"⚠ Subtree search failed: {ex.Message}");
        }
        finally
        {
            IsSearchRunning = false;
        }
    }

    [RelayCommand]
    private void CancelSubtreeSearch()
    {
        try { _searchCts?.Cancel(); } catch { }
    }

    // Double-clicking a search hit closes the modal and navigates the originating
    // panel to the hit's parent directory. The hit's row stays selected via the
    // existing IsSelected mechanism only after the directory listing arrives,
    // which is too racy to wire here — for now we just navigate.
    [RelayCommand]
    private async Task OpenSearchHitAsync(SearchHit? hit)
    {
        if (hit == null || _searchSourceTab == null) return;
        var tab = _searchSourceTab;

        var fullPath = hit.FullPath;
        var parent = hit.IsDirectory
            ? fullPath
            : GetParentPath(fullPath, '/');

        CloseSubtreeSearch();

        try { await NavigateAsync(tab, parent); }
        catch (Exception ex) { Log($"⚠ Could not open {parent}: {ex.Message}"); }
    }

    // ── Tab Selection (click to activate in current panel) ─────
    [RelayCommand]
    private void SelectLeftTab(TabItem? tab)
    {
        if (tab != null && LeftTabs.Contains(tab)) ActiveLeftTab = tab;
    }

    [RelayCommand]
    private void SelectRightTab(TabItem? tab)
    {
        if (tab != null && RightTabs.Contains(tab)) ActiveRightTab = tab;
    }

    // ── Move Tab ───────────────────────────────────────────────
    [RelayCommand]
    private void MoveTabToLeft(TabItem? tab)
    {
        if (tab == null || !tab.IsRemote) return;
        if (RightTabs.Contains(tab))
        {
            RightTabs.Remove(tab);
            LeftTabs.Add(tab);
            ActiveLeftTab = tab;
            if (ActiveRightTab == tab) ActiveRightTab = RightTabs.FirstOrDefault();
            Log($"↔ Moved \"{tab.Name}\" to left panel");
        }
    }

    [RelayCommand]
    private void MoveTabToRight(TabItem? tab)
    {
        if (tab == null || !tab.IsRemote) return;
        if (LeftTabs.Contains(tab))
        {
            LeftTabs.Remove(tab);
            RightTabs.Add(tab);
            ActiveRightTab = tab;
            if (ActiveLeftTab == tab) ActiveLeftTab = LeftTabs.FirstOrDefault();
            Log($"↔ Moved \"{tab.Name}\" to right panel");
        }
    }


    // ── Helpers ─────────────────────────────────────────────────
    private static string GetParentPath(string path, char sep)
    {
        var trimmed = path.TrimEnd(sep);
        var idx = trimmed.LastIndexOf(sep);
        return idx > 0 ? trimmed[..idx] : sep.ToString();
    }
}

// ── Tab Model ──────────────────────────────────────────────────
public enum FileSortColumn { Name, Size, Modified }

public partial class TabItem : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isRemote;
    [ObservableProperty] private string _path = "";

    // Live SSH session state for this tab. True while the SftpClient is
    // healthy; flipped false when the server drops the connection so the UI
    // can show a disconnected indicator and the auto-reconnect flow can kick
    // in. Local tabs never set this.
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private ObservableCollection<FileNode> _files = new();

    // Files projected through FilterText. When the filter is empty VisibleFiles
    // mirrors Files. The XAML binds to VisibleFiles so filter changes are cheap
    // (rebuild a single collection; no per-row visibility toggling).
    [ObservableProperty] private ObservableCollection<FileNode> _visibleFiles = new();
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isFilterActive;

    partial void OnFilesChanged(ObservableCollection<FileNode> value) => RebuildVisibleFiles();
    partial void OnFilterTextChanged(string value) => RebuildVisibleFiles();

    public void RebuildVisibleFiles()
    {
        var q = (FilterText ?? "").Trim();
        if (q.Length == 0)
        {
            VisibleFiles = new ObservableCollection<FileNode>(Files);
            return;
        }
        // ".." is always kept so the user can navigate up even while filtering.
        VisibleFiles = new ObservableCollection<FileNode>(
            Files.Where(f => f.Name == ".." ||
                             f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    // Breadcrumb segments derived from Path. Rebuilt on every Path change so
    // the path bar stays in sync with navigation from any source (clicks,
    // direct typing, "..", initial load).
    [ObservableProperty] private ObservableCollection<PathSegment> _segments = new();

    partial void OnPathChanged(string value) => BuildSegments();

    private void BuildSegments()
    {
        Segments.Clear();
        if (string.IsNullOrEmpty(Path)) return;

        if (IsRemote)
        {
            Segments.Add(new PathSegment { Name = "/", FullPath = "/", IsLast = Path == "/" });
            if (Path == "/") return;

            var parts = Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cumul = "";
            for (int i = 0; i < parts.Length; i++)
            {
                cumul += "/" + parts[i];
                Segments.Add(new PathSegment
                {
                    Name = parts[i],
                    FullPath = cumul,
                    IsLast = i == parts.Length - 1
                });
            }
        }
        else
        {
            var sep = System.IO.Path.DirectorySeparatorChar;
            var parts = Path.Split(sep, StringSplitOptions.RemoveEmptyEntries);
            var cumul = "";
            for (int i = 0; i < parts.Length; i++)
            {
                // First segment on Windows is typically a drive like "C:" —
                // the actual navigable full path is "C:\" (with trailing sep).
                cumul = i == 0 ? parts[i] + sep : cumul.TrimEnd(sep) + sep + parts[i];
                Segments.Add(new PathSegment
                {
                    Name = parts[i],
                    FullPath = cumul,
                    IsLast = i == parts.Length - 1
                });
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph), nameof(SizeSortGlyph), nameof(ModifiedSortGlyph))]
    private FileSortColumn _sortColumn = FileSortColumn.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph), nameof(SizeSortGlyph), nameof(ModifiedSortGlyph))]
    private bool _sortDescending;

    public string NameSortGlyph     => SortColumn == FileSortColumn.Name     ? (SortDescending ? " ↓" : " ↑") : "";
    public string SizeSortGlyph     => SortColumn == FileSortColumn.Size     ? (SortDescending ? " ↓" : " ↑") : "";
    public string ModifiedSortGlyph => SortColumn == FileSortColumn.Modified ? (SortDescending ? " ↓" : " ↑") : "";

    public SftpService? Sftp { get; set; }
}
