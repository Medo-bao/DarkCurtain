using ScreenBlank;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

internal static partial class Tests
{
    private static void VerifyFunctionalFixes()
    {
        MemoryCase(VerifyCleanupHelperCancellation, "Cancelling cleanup stops the live helper after the parent pipe closes");
        MemoryCase(VerifyIncompleteHelperRequest, "A helper exits when its caller disconnects before finishing the request");
        MemoryCase(VerifyMissingBrightnessSnapshot, "Missing or failed brightness capture never dims the backlight");
        MemoryCase(VerifyBrightnessRestorationRetry, "Failed brightness restoration retains the original value for a later retry");
        MemoryCase(VerifySettingsWriteFailure, "A real settings write failure returns its error while valid settings remain readable");
        MemoryCase(VerifyStartupWriteFailure, "Startup registration errors are returned to the settings UI instead of being hidden");
    }

    private static void VerifyCleanupHelperCancellation()
    {
        using CancellationTokenSource cancel = new();
        TaskCompletionSource<(Process Observer, Task<string?> Ready)> launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MemoryCleanupResult> cleanup = MemoryCleanupProcess.RunAsync(new(MemoryCleanItems.Default, Environment.ProcessId), cancel.Token,
            startProcess: start =>
            {
                start.UseShellExecute = false;
                start.Verb = "";
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.ArgumentList[0] = "--memory-helper-watch";
                Process child = Process.Start(start)!;
                Process observer = Process.GetProcessById(child.Id);
                _ = observer.SafeHandle;
                launched.SetResult((observer, child.StandardOutput.ReadLineAsync()));
                return child;
            });
        PumpMemoryUntil(() => launched.Task.IsCompleted);
        var probe = launched.Task.GetAwaiter().GetResult();
        using Process observer = probe.Observer;
        try
        {
            PumpMemoryUntil(() => probe.Ready.IsCompleted);
            MemoryRequire(probe.Ready.GetAwaiter().GetResult() == "running", "Helper cleanup did not start");
            cancel.Cancel();
            PumpMemoryUntil(() => cleanup.IsCompleted);
            bool cancelled = false;
            try { cleanup.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            MemoryRequire(cancelled && observer.WaitForExit(3000) && observer.ExitCode == 1,
                "Helper continued working after its caller cancelled cleanup");
        }
        finally
        {
            cancel.Cancel();
            if (!observer.HasExited) { observer.Kill(); observer.WaitForExit(); }
        }
    }

    private static void VerifyIncompleteHelperRequest()
    {
        string pipeName = "ScreenBlank_Memory_" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        ProcessStartInfo start = new(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--memory-helper-watch");
        start.ArgumentList.Add(pipeName);
        using Process child = Process.Start(start)!;
        try
        {
            Task connected = server.WaitForConnectionAsync();
            PumpMemoryUntil(() => connected.IsCompleted);
            connected.GetAwaiter().GetResult();
            server.WriteByte((byte)'{');
            server.Flush();
            server.Dispose();
            MemoryRequire(child.WaitForExit(3000) && child.ExitCode == 1, "Helper waited indefinitely for an abandoned request");
        }
        finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
    }

    private static void VerifyMissingBrightnessSnapshot()
    {
        foreach (bool failRead in new[] { false, true })
        {
            int writes = 0, restores = 0;
            GammaDim dim = new(
                readBrightness: () => failRead ? throw new InvalidOperationException("brightness unavailable") : Array.Empty<byte>(),
                setBacklight: (_, _) => writes++, restoreBacklight: _ => restores++);
            dim.CaptureAndDimBacklight();
            dim.Restore();
            MemoryRequire(writes == 0 && restores == 0, "An unsaved backlight was changed");
        }
    }

    private static void VerifyBrightnessRestorationRetry()
    {
        int reads = 0, restores = 0;
        List<byte> changes = [];
        GammaDim dim = new(
            readBrightness: () => { reads++; return new byte[] { 73 }; },
            setBacklight: (brightness, _) => changes.Add(brightness),
            restoreBacklight: original =>
            {
                if (++restores == 1) throw new InvalidOperationException("temporary restore failure");
                changes.Add(original[0]);
            });
        dim.CaptureAndDimBacklight();
        bool failed = false;
        try { dim.Restore(); } catch (InvalidOperationException) { failed = true; }
        dim.Restore();
        dim.Restore();
        MemoryRequire(failed && reads == 1 && restores == 2 && changes.SequenceEqual(new byte[] { 1, 73 }),
            "Brightness restore failure discarded or replaced the original snapshot");
    }

    private static void VerifySettingsWriteFailure()
    {
        string artifacts = Path.GetFullPath("tests/artifacts");
        Directory.CreateDirectory(artifacts);
        string settingsPath = Path.Combine(artifacts, $"settings-save-{Environment.ProcessId}.json");
        Settings settings = new() { MemoryCleanupItems = MemoryCleanItems.WorkingSet };
        string? success = SettingsStore.Save(settings, settingsPath);
        Settings restored = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath))!;
        MemoryRequire(success is null && restored.MemoryCleanupItems == settings.MemoryCleanupItems,
            "Successful settings persistence failed");
        string blocker = Path.Combine(artifacts, $"settings-save-blocker-{Environment.ProcessId}");
        File.WriteAllText(blocker, "blocks directory creation");
        string? failure = SettingsStore.Save(settings, Path.Combine(blocker, "settings.json"));
        MemoryRequire(!string.IsNullOrWhiteSpace(failure) && File.ReadAllText(settingsPath).Contains("MemoryCleanupItems"),
            "Failed settings persistence was hidden or corrupted the prior settings");
    }

    private static void VerifyStartupWriteFailure()
    {
        bool enabled = false;
        string? success = StartupManager.Apply(true, value => enabled = value);
        string? failure = StartupManager.Apply(true, _ => throw new UnauthorizedAccessException("startup registration denied"));
        MemoryRequire(success is null && enabled && failure == "startup registration denied",
            "Startup registration failure was hidden");
    }

    private static void VerifyReleaseHelperWithRunningApp(string executable)
    {
        IntPtr originalWindow = NativeMethods.FindWindow(null, "暗黑天幕");
        NativeMethods.GetWindowThreadProcessId(originalWindow, out uint originalOwner);
        bool originallyVisible = IsWindowVisible(originalWindow);
        bool originallyMinimized = IsIconic(originalWindow);
        MemoryRequire(originalWindow != IntPtr.Zero && originalOwner != 0, "Running app could not be identified");

        MemoryCleanupResult InvokeHelper(MemoryCleanRequest request)
        {
            Task<MemoryCleanupResult> pending = MemoryCleanupProcess.RunAsync(request, startProcess: start =>
            {
                start.FileName = executable;
                start.UseShellExecute = false;
                start.Verb = "";
                start.CreateNoWindow = true;
                return Process.Start(start);
            });
            PumpMemoryUntil(() => pending.IsCompleted);
            return pending.GetAwaiter().GetResult();
        }

        MemoryCleanupResult empty = InvokeHelper(new(MemoryCleanItems.None, Environment.ProcessId));
        Check(!empty.Success && empty.Error?.Contains("无效") == true,
            "Published helper rejects an empty cleanup request while the existing app is running");
        MemoryCleanupResult invalid = InvokeHelper(new((MemoryCleanItems)128, Environment.ProcessId));
        Check(!invalid.Success && invalid.Error?.Contains("无效") == true,
            "Published helper rejects unsupported cleanup flags");
        MemoryCleanupResult missing = InvokeHelper(null!);
        Check(!missing.Success && missing.Error?.Contains("请求无效") == true,
            "Published helper rejects a missing cleanup request");

        bool RawRequestRejected(string? request)
        {
            string pipeName = "ScreenBlank_Memory_" + Guid.NewGuid().ToString("N");
            using NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            ProcessStartInfo start = new(executable)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(MemoryCleanupProcess.Argument);
            start.ArgumentList.Add(pipeName);
            using Process child = Process.Start(start)!;
            try
            {
                Task connected = server.WaitForConnectionAsync();
                PumpMemoryUntil(() => connected.IsCompleted);
                connected.GetAwaiter().GetResult();
                if (request is not null)
                {
                    using StreamWriter writer = new(server, leaveOpen: true) { AutoFlush = true };
                    writer.WriteLine(request);
                }
                else server.Dispose();
                return child.WaitForExit(3000) && child.ExitCode == 1;
            }
            finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
        }
        Check(RawRequestRejected("{"), "Published helper rejects malformed request data without starting cleanup");
        Check(RawRequestRejected(null), "Published helper exits when its caller disconnects before sending a request");

        ProcessStartInfo incomplete = new(executable)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        incomplete.ArgumentList.Add(MemoryCleanupProcess.Argument);
        using (Process child = Process.Start(incomplete)!)
        {
            try { Check(child.WaitForExit(3000) && child.ExitCode == 1, "Published helper rejects incomplete command arguments"); }
            finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
        }
        NativeMethods.GetWindowThreadProcessId(originalWindow, out uint finalOwner);
        Check(finalOwner == originalOwner && IsWindowVisible(originalWindow) == originallyVisible
            && IsIconic(originalWindow) == originallyMinimized,
            "Published helper checks preserve the running app and its window state");
    }
}
