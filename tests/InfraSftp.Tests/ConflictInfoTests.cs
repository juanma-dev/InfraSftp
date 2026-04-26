using InfraSftp.Models;

namespace InfraSftp.Tests;

// ConflictInfo is a pure observable record used by the paste/conflict modal.
// These tests cover the UI-derived flags and the TaskCompletionSource pattern
// the VM awaits to know which button the user clicked.
public class ConflictInfoTests
{
    [Fact]
    public void Default_Mode_Is_Transfer()
    {
        var info = new ConflictInfo();
        Assert.True(info.IsTransferMode);
        Assert.False(info.IsPasteMode);
    }

    [Fact]
    public void Mode_Switch_Flips_Visibility_Flags()
    {
        var info = new ConflictInfo { Mode = ConflictMode.Paste };
        Assert.False(info.IsTransferMode);
        Assert.True(info.IsPasteMode);
    }

    [Fact]
    public void Resolution_Defaults_To_Pending()
    {
        var info = new ConflictInfo();
        Assert.False(info.Resolution.Task.IsCompleted);
    }

    [Fact]
    public async Task Resolution_Completes_With_Replace()
    {
        var info = new ConflictInfo();
        info.Resolution.SetResult(ConflictResolution.Replace);
        Assert.Equal(ConflictResolution.Replace, await info.Resolution.Task);
    }

    [Fact]
    public async Task Resolution_Completes_With_Skip()
    {
        var info = new ConflictInfo();
        info.Resolution.SetResult(ConflictResolution.Skip);
        Assert.Equal(ConflictResolution.Skip, await info.Resolution.Task);
    }

    [Fact]
    public void File_Size_Text_Formats_Bytes()
    {
        var info = new ConflictInfo { IsFolder = false, SourceSize = 0 };
        Assert.Equal("0 B", info.SourceSizeText);
    }

    [Fact]
    public void File_Size_Text_Formats_Kilobytes_With_Two_Decimals()
    {
        // FormatBytes uses the current culture for the decimal separator, so
        // an es-ES test machine sees "1,5 KB" and an en-US machine "1.5 KB".
        // Mirror that here rather than hard-coding either form.
        var info = new ConflictInfo { IsFolder = false, SourceSize = 1536 };
        var expected = string.Format("{0:0.##} KB", 1.5);
        Assert.Equal(expected, info.SourceSizeText);
    }

    [Fact]
    public void Folder_Size_Shows_Item_Count_Not_Bytes()
    {
        var info = new ConflictInfo
        {
            IsFolder = true,
            SourceItemCount = 42,
            SourceSize = 9999, // ignored for folders
        };
        Assert.Equal("42 item(s)", info.SourceSizeText);
    }

    [Fact]
    public void Modified_Text_Renders_Em_Dash_For_Default_Date()
    {
        var info = new ConflictInfo(); // SourceModified left at default
        Assert.Equal("—", info.SourceModifiedText);
    }

    [Fact]
    public void Title_Reflects_File_Vs_Folder()
    {
        Assert.Equal("File already exists",   new ConflictInfo { IsFolder = false }.Title);
        Assert.Equal("Folder already exists", new ConflictInfo { IsFolder = true  }.Title);
    }
}
