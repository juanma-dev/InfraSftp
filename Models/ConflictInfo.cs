using CommunityToolkit.Mvvm.ComponentModel;

namespace InfraSftp.Models;

/// <summary>
/// What the user picked when a destination already exists.
/// Cancel aborts the *whole* batch; Skip moves on to the next item.
/// </summary>
public enum ConflictResolution
{
    Cancel,
    Replace,
    Skip,
    Rename
}

public enum ConflictMode
{
    /// <summary>Drag/transfer flow — legacy two-button modal (Copy / Cancel).</summary>
    Transfer,
    /// <summary>Paste flow — Replace / Skip / Rename / Cancel + Apply-to-all.</summary>
    Paste
}

/// <summary>
/// Snapshot of source+destination metadata shown to the user when a transfer
/// or paste target already exists. The VM awaits <see cref="Resolution"/>;
/// the modal's buttons set the result.
/// </summary>
public partial class ConflictInfo : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isFolder;
    [ObservableProperty] private ConflictMode _mode = ConflictMode.Transfer;

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private long _sourceSize;
    [ObservableProperty] private DateTime _sourceModified;
    [ObservableProperty] private int _sourceItemCount; // folders only

    [ObservableProperty] private string _destPath = "";
    [ObservableProperty] private long _destSize;
    [ObservableProperty] private DateTime _destModified;
    [ObservableProperty] private int _destItemCount; // folders only

    // Suggested non-colliding name for the "Rename" action, computed by the
    // VM before opening the modal (e.g. "report - Copia.txt").
    [ObservableProperty] private string _suggestedNewName = "";

    // Paste-mode only: when checked, the chosen resolution applies to every
    // remaining conflict in the batch without re-prompting.
    [ObservableProperty] private bool _applyToAll;

    // ── Mode-derived flags for XAML visibility binding ────────
    public bool IsTransferMode => Mode == ConflictMode.Transfer;
    public bool IsPasteMode    => Mode == ConflictMode.Paste;

    partial void OnModeChanged(ConflictMode value)
    {
        OnPropertyChanged(nameof(IsTransferMode));
        OnPropertyChanged(nameof(IsPasteMode));
    }

    // ── Display helpers ───────────────────────────────────────
    public string SourceSizeText   => IsFolder ? $"{SourceItemCount} item(s)" : FormatBytes(SourceSize);
    public string DestSizeText     => IsFolder ? $"{DestItemCount} item(s)"   : FormatBytes(DestSize);
    public string SourceModifiedText => SourceModified == default ? "—" : SourceModified.ToString("yyyy-MM-dd HH:mm");
    public string DestModifiedText   => DestModified   == default ? "—" : DestModified  .ToString("yyyy-MM-dd HH:mm");

    public string Title   => IsFolder ? "Folder already exists" : "File already exists";
    public string Message => IsFolder
        ? $"A folder named \"{Name}\" already exists at the destination. Replacing will merge contents (files with the same name are overwritten)."
        : $"A file named \"{Name}\" already exists at the destination. Choose how to resolve.";

    // Resolution is the new, richer outcome. Transfer-mode callers translate
    // Replace→true / Cancel→false themselves.
    public TaskCompletionSource<ConflictResolution> Resolution { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }
}
