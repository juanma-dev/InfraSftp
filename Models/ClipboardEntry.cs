namespace InfraSftp.Models;

// One item snapshot the user copied / cut. Captured by value so the entry
// stays valid even if the source tab is later closed or its listing refreshed.
// FileNode is held weak-ish: we only mutate its IsCut flag while the source
// listing is still showing the same instance — a refresh discards the link.
public sealed class ClipboardEntry
{
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public string SourcePath { get; init; } = "";  // absolute path to the item
    public bool IsRemote { get; init; }
    public string SourceTabId { get; init; } = "";

    // Live FileNode reference for visual cut state (dimmed row). Cleared by
    // the VM after the cut is consumed or the clipboard is overwritten.
    public FileNode? SourceNode { get; set; }
}

public enum ClipboardOperation
{
    None,
    Copy,
    Cut
}
