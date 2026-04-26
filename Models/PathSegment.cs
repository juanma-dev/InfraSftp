namespace InfraSftp.Models;

/// <summary>
/// One clickable piece of a breadcrumb path bar.
/// <see cref="FullPath"/> is the target to navigate to when the segment
/// is clicked; <see cref="Name"/> is the label shown on the button.
/// </summary>
public class PathSegment
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsLast { get; set; }
}
