using System.ComponentModel;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using InfraSftp.Models;

namespace InfraSftp.Tests;

// Hosts a headless Avalonia application so we can drive the conflict modal's
// dispatcher-bound flows without ever showing a window. The Avalonia.Headless
// session is created once per fixture and shared across all tests in the class.
//
// We use the bare Avalonia.Headless package (not Avalonia.Headless.XUnit) on
// purpose — the .XUnit add-on pulls xunit v3 and conflicts with the v2 baseline
// in this test project.
public class HeadlessConflictTests : IClassFixture<HeadlessAvaloniaFixture>
{
    private readonly HeadlessAvaloniaFixture _avalonia;
    public HeadlessConflictTests(HeadlessAvaloniaFixture avalonia) => _avalonia = avalonia;

    [Fact]
    public async Task Resolution_Round_Trip_Through_Dispatcher()
    {
        // Mirror the real flow: VM awaits info.Resolution.Task, the modal's
        // [RelayCommand] handler calls SetResult on the dispatcher.
        var info = new ConflictInfo { Mode = ConflictMode.Paste, Name = "report.txt" };

        await _avalonia.RunOnUI(() =>
        {
            Dispatcher.UIThread.Post(() => info.Resolution.SetResult(ConflictResolution.Replace));
        });

        var result = await info.Resolution.Task;
        Assert.Equal(ConflictResolution.Replace, result);
    }

    [Fact]
    public async Task Mode_Change_Raises_PropertyChanged_For_Derived_Flags()
    {
        var info = new ConflictInfo();
        var raised = new List<string>();
        ((INotifyPropertyChanged)info).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) raised.Add(e.PropertyName);
        };

        await _avalonia.RunOnUI(() => info.Mode = ConflictMode.Paste);

        Assert.Contains("Mode", raised);
        Assert.Contains(nameof(ConflictInfo.IsTransferMode), raised);
        Assert.Contains(nameof(ConflictInfo.IsPasteMode), raised);
    }

    [Fact]
    public async Task ApplyToAll_Survives_Resolution()
    {
        // Guard against a regression where the checkbox state would be cleared
        // synchronously with the button click. The VM reads ApplyToAll *before*
        // discarding the conflict (via _lastApplyToAll), so the flag must remain
        // observable post-resolution.
        var info = new ConflictInfo { Mode = ConflictMode.Paste, ApplyToAll = true };

        await _avalonia.RunOnUI(() => info.Resolution.SetResult(ConflictResolution.Skip));
        await info.Resolution.Task;

        Assert.True(info.ApplyToAll);
    }
}

// Boots a single headless Avalonia application for the lifetime of the test
// class. The IClassFixture<> machinery in xUnit ensures Setup runs once and
// Dispose runs after the last test in the class completes.
public sealed class HeadlessAvaloniaFixture : IDisposable
{
    private readonly HeadlessUnitTestSession _session;

    public HeadlessAvaloniaFixture()
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(App));
    }

    public Task RunOnUI(Action action) => _session.Dispatch(action, default);

    public void Dispose() => _session.Dispose();
}
