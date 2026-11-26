# Optional Windows-only diagnostics for the isolated dotnet-watch fixture.
# Duplicate handles only for inspection; never close a source process's handle.
Add-Type @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static class SunfleckPdbLockProbe {
    [StructLayout(LayoutKind.Sequential)] struct HandleEntry {
        public IntPtr Object, ProcessId, Handle;
        public uint Access;
        public ushort BackTrace, Type;
        public uint Attributes, Reserved;
    }
    public sealed class Holder {
        public int ProcessId;
        public string Handle, Access, Path, ObjectAddress, Verification;
        public bool DeleteOpenSucceeded;
        public int DeleteOpenError;
        public List<string> MappedViews;
        public int MappingQueryError;
    }
    public sealed class Report {
        public string Utc, Path;
        public List<Holder> Holders = new List<Holder>();
        public List<int> InaccessibleProcesses = new List<int>();
        public bool WriteOpenSucceeded;
        public string WriteOpenError;
        public bool DeleteOpenSucceeded;
        public int DeleteOpenError;
    }
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int kind, IntPtr data, int size, out int needed);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool DuplicateHandle(IntPtr source, IntPtr handle, IntPtr target, out IntPtr copy, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr ReOpenFile(IntPtr original, uint access, uint share, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [StructLayout(LayoutKind.Sequential)] struct MemoryInformation {
        public UIntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError=true)] static extern UIntPtr VirtualQueryEx(IntPtr process, UIntPtr address, out MemoryInformation information, UIntPtr length);
    [DllImport("psapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetMappedFileNameW(IntPtr process, UIntPtr address, StringBuilder path, uint length);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetFinalPathNameByHandleW(IntPtr handle, StringBuilder path, uint size, uint flags);
    static List<string> MappedViews(int pid, IntPtr original, out int error) {
        error = 0;
        var views = new List<string>();
        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(original, path, (uint)path.Capacity, 2);
        if (length == 0 || length >= path.Capacity) { error = Marshal.GetLastWin32Error(); return views; }
        string expected = path.ToString();
        IntPtr process = OpenProcess(0x410, false, pid); // QUERY_INFORMATION | VM_READ, read-only
        if (process == IntPtr.Zero) { error = Marshal.GetLastWin32Error(); return views; }
        try {
            ulong address = 0;
            var allocations = new HashSet<ulong>();
            MemoryInformation memory;
            while (VirtualQueryEx(process, (UIntPtr)address, out memory, (UIntPtr)Marshal.SizeOf<MemoryInformation>()) != UIntPtr.Zero) {
                if (memory.State == 0x1000 && memory.Type == 0x40000) {
                    path.Clear();
                    length = GetMappedFileNameW(process, memory.BaseAddress, path, (uint)path.Capacity);
                    if (length > 0 && length < path.Capacity && String.Equals(expected, path.ToString(), StringComparison.OrdinalIgnoreCase))
                        allocations.Add(memory.AllocationBase.ToUInt64());
                }
                ulong next = memory.BaseAddress.ToUInt64() + memory.RegionSize.ToUInt64();
                if (next <= address) break;
                address = next;
            }
            foreach (ulong allocation in allocations) views.Add("0x" + allocation.ToString("X"));
            views.Sort(StringComparer.Ordinal);
            return views;
        } finally { CloseHandle(process); }
    }
    // Retain only identities already matched by absolute path in this probe process.
    // A deleted path may no longer resolve while its original file object stays open.
    static readonly Dictionary<string, string> identified = new Dictionary<string, string>();
    public static Report Capture(string path, int[] processIds) {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("The handle probe requires a 64-bit host.");
        var report = new Report { Utc = DateTime.UtcNow.ToString("o"), Path = System.IO.Path.GetFullPath(path) };
        var requested = new HashSet<int>(processIds);
        var processes = new Dictionary<int, IntPtr>();
        IntPtr buffer = IntPtr.Zero;
        try {
            foreach (int pid in requested) {
                IntPtr process = OpenProcess(0x40, false, pid); // PROCESS_DUP_HANDLE
                if (process == IntPtr.Zero) report.InaccessibleProcesses.Add(pid);
                else processes.Add(pid, process);
            }
            int size = 1024 * 1024, status, needed;
            do {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                status = NtQuerySystemInformation(64, buffer, size, out needed);
                if (status == unchecked((int)0xC0000004)) size = Math.Max(size * 2, needed + 65536);
                else if (status != 0) throw new Win32Exception("NtQuerySystemInformation: " + status.ToString("X8"));
            } while (status != 0);
            long count = Marshal.ReadInt64(buffer);
            int stride = Marshal.SizeOf<HandleEntry>();
            var text = new StringBuilder(32768);
            for (long index = 0; index < count; index++) {
                var entry = Marshal.PtrToStructure<HandleEntry>(IntPtr.Add(buffer, checked(16 + (int)index * stride)));
                if (!processes.TryGetValue(unchecked((int)entry.ProcessId.ToInt64()), out IntPtr process)) continue;
                if (!DuplicateHandle(process, entry.Handle, GetCurrentProcess(), out IntPtr copy, 0, false, 2)) continue;
                try {
                    // Pipes/devices can block path queries. Only inspect disk-file handles.
                    if (GetFileType(copy) != 1) continue;
                    text.Clear();
                    uint length = GetFinalPathNameByHandleW(copy, text, (uint)text.Capacity, 0);
                    string actual = length > 0 && length < text.Capacity ? text.ToString() : null;
                    if (actual != null && actual.StartsWith(@"\\?\")) actual = actual.Substring(4);
                    string key = entry.ProcessId + ":" + entry.Handle + ":" + entry.Object;
                    bool matched = String.Equals(actual, report.Path, StringComparison.OrdinalIgnoreCase);
                    if (matched) identified[key] = actual;
                    else if (actual != null || !identified.TryGetValue(key, out actual) || !String.Equals(actual, report.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    // Request DELETE access only, without setting delete disposition or renaming.
                    // This tests sharing compatibility on the original object, even if its path vanished.
                    IntPtr deleteAccess = ReOpenFile(copy, 0x10000, 7, 0);
                    int deleteError = deleteAccess == new IntPtr(-1) ? Marshal.GetLastWin32Error() : 0;
                    if (deleteAccess != new IntPtr(-1)) CloseHandle(deleteAccess);
                    int mappingError;
                    var mappings = MappedViews((int)entry.ProcessId.ToInt64(), copy, out mappingError);
                    report.Holders.Add(new Holder { ProcessId = (int)entry.ProcessId.ToInt64(), Handle = "0x" + entry.Handle.ToInt64().ToString("X"), Access = "0x" + entry.Access.ToString("X"), Path = actual, ObjectAddress = "0x" + entry.Object.ToInt64().ToString("X"), Verification = matched ? "AbsolutePath" : "PreviouslyMatchedProcessHandleAndFileObject", DeleteOpenSucceeded = deleteError == 0, DeleteOpenError = deleteError, MappedViews = mappings, MappingQueryError = mappingError });
                } finally { CloseHandle(copy); }
            }
        } finally {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            foreach (IntPtr process in processes.Values) CloseHandle(process);
        }
        // OPEN_EXISTING, no writes/truncation; share everything and dispose immediately.
        try {
            using (var stream = new FileStream(report.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
            report.WriteOpenSucceeded = true;
        } catch (IOException error) { report.WriteOpenError = error.HResult.ToString("X8") + ": " + error.Message; }
        catch (UnauthorizedAccessException error) { report.WriteOpenError = error.HResult.ToString("X8") + ": " + error.Message; }
        IntPtr pathDeleteAccess = CreateFileW(report.Path, 0x10000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        report.DeleteOpenError = pathDeleteAccess == new IntPtr(-1) ? Marshal.GetLastWin32Error() : 0;
        report.DeleteOpenSucceeded = report.DeleteOpenError == 0;
        if (pathDeleteAccess != new IntPtr(-1)) CloseHandle(pathDeleteAccess);
        return report;
    }
}
'@

function Save-PdbLockTrace([string]$Stage) {
    if (!$configuration.TracePdbLocks) { return }
    $pdb = Join-Path $configuration.Source 'obj/Debug/net10.0/HotReloadCounter.pdb'
    $candidates = @(Get-Process | Where-Object { $_.ProcessName -in @('dotnet', 'Code', 'pwsh', 'HotReloadCounter', 'VBCSCompiler') })
    $trace = [SunfleckPdbLockProbe]::Capture($pdb, [int[]]$candidates.Id)
    $holders = @($trace.Holders.ProcessId | Select-Object -Unique)
    $detailsErrors = @()
    $details = @(foreach ($holderId in $holders) {
        Get-CimInstance Win32_Process -Filter "ProcessId=$holderId" -ErrorAction SilentlyContinue -ErrorVariable +detailsErrors |
            Select-Object ProcessId, ParentProcessId, ExecutablePath, CommandLine, CreationDate
    })
    [ordered]@{
        Stage = $Stage; WatcherProcessId = $watcher.Id; DriverProcessId = $PID
        Applications = @($owned | ForEach-Object { @{ ProcessId = $_.Id; HasExited = $_.HasExited } })
        Probe = $trace; HolderProcesses = $details
        HolderProcessErrors = @($detailsErrors | ForEach-Object { $_.ToString() })
        Candidates = @($candidates | Select-Object Id, ProcessName)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory "pdb-lock-$Stage.json") -Encoding utf8
}

function Save-PdbLockLifetime {
    $initial = Get-Content -LiteralPath (Join-Path $directory 'pdb-lock-old-app-exited.json') -Raw | ConvertFrom-Json
    $holderIds = [int[]]@($initial.Probe.Holders.ProcessId | Select-Object -Unique)
    if (!$holderIds.Count) { return }
    $samples = [Collections.Generic.List[object]]::new()
    $samples.Add($initial.Probe)
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $sample = [SunfleckPdbLockProbe]::Capture($initial.Probe.Path, $holderIds)
        $samples.Add($sample)
        if (!$sample.Holders.Count) { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    $samples.ToArray() | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $directory 'pdb-lock-lifetime.json') -Encoding utf8
}
