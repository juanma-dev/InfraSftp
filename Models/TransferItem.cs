using CommunityToolkit.Mvvm.ComponentModel;

namespace InfraSftp.Models;

public enum TransferDirection { Upload, Download, RemoteToRemote }
public enum TransferStatus { Queued, InProgress, Completed, Failed, Skipped }

public partial class TransferItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay), nameof(StatusIcon), nameof(StatusColor))]
    private TransferStatus _status = TransferStatus.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent), nameof(ProgressDisplay))]
    private long _transferredBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent), nameof(ProgressDisplay))]
    private long _totalBytes;

    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _destPath = "";
    [ObservableProperty] private TransferDirection _direction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private string? _errorMessage;

    public double ProgressPercent => TotalBytes > 0
        ? Math.Min(100.0, TransferredBytes * 100.0 / TotalBytes)
        : (Status == TransferStatus.Completed ? 100.0 : 0);

    public string ProgressDisplay
    {
        get
        {
            if (TotalBytes == 0) return "";
            return $"{FormatBytes(TransferredBytes)} / {FormatBytes(TotalBytes)}";
        }
    }

    public string StatusIcon => Status switch
    {
        TransferStatus.Queued => "⏳",
        TransferStatus.InProgress => Direction switch
        {
            TransferDirection.Upload => "⬆",
            TransferDirection.RemoteToRemote => "⇄",
            _ => "⬇"
        },
        TransferStatus.Completed => "✅",
        TransferStatus.Failed => "❌",
        TransferStatus.Skipped => "⏭",
        _ => ""
    };

    public string StatusColor => Status switch
    {
        TransferStatus.Completed => "#10B981",
        TransferStatus.Failed => "#EF4444",
        TransferStatus.Skipped => "#6B7280",
        TransferStatus.InProgress => "#3B82F6",
        _ => "#9CA3AF"
    };

    public string StatusDisplay => Status switch
    {
        TransferStatus.Queued => "Queued",
        TransferStatus.InProgress => $"{ProgressPercent:F0}%",
        TransferStatus.Completed => "Done",
        TransferStatus.Failed => ErrorMessage ?? "Failed",
        TransferStatus.Skipped => "Skipped (up-to-date)",
        _ => ""
    };

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}
