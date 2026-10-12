using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ScreenBlank;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_MEMORY_COUNTERS
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_COMBINE_INFORMATION
    {
        public IntPtr Handle;
        public nuint PagesCombined;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint Count;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool K32EmptyWorkingSet(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref PROCESS_MEMORY_COUNTERS counters, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint K32GetProcessImageFileNameW(SafeProcessHandle process, StringBuilder name, uint size);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetSystemFileCacheSize(nuint minimum, nuint maximum, uint flags);

    // SystemMemoryListInformation accepts a four-byte SYSTEM_MEMORY_LIST_COMMAND.
    [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static extern int SetMemoryList(uint informationClass, ref uint command, uint length);

    [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static extern int CombineMemory(uint informationClass, ref MEMORY_COMBINE_INFORMATION information, uint length);

    [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static extern int ReconcileRegistry(uint informationClass, IntPtr information, uint length);

    [DllImport("ntdll.dll")]
    internal static extern uint RtlNtStatusToDosError(int status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TOKEN_PRIVILEGES privileges,
        uint bufferLength, out TOKEN_PRIVILEGES previous, out uint returnLength);

    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TOKEN_PRIVILEGES privileges,
        uint bufferLength, IntPtr previous, IntPtr returnLength);
}

internal static class MemoryCleanupNative
{
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "fontdrvhost", "dwm", "explorer", "svchost", "dllhost", "conhost",
        "sihost", "taskhostw", "ctfmon", "SearchIndexer", "MsMpEng", "NisSrv", "WmiPrvSE",
        "SecurityHealthService", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost",
        "TextInputHost", "backgroundTaskHost"
    };

    private static readonly HashSet<string> GameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "DeltaForceClient-Win64-Shipping", "ABInfinite", "ABInfinite-Win64-Shipping",
        "Marvel-Win64-Shipping", "Stalker2-Win64-Shipping", "helldivers2", "VALORANT",
        "VALORANT-Win64-Shipping", "cs2", "csgo", "r5apex", "TslGame", "cod", "ModernWarfare",
        "Overwatch", "Overwatch2", "FortniteClient-Win64-Shipping", "RainbowSix", "RainbowSix_BE",
        "EscapeFromTarkov", "bf1", "bfv", "bf2042", "TheDivision", "TheDivision2", "destiny2",
        "HuntGame", "starwarsbattlefrontii", "HaloInfinite", "Titanfall2", "left4dead2", "EDF5",
        "Borderlands3", "Borderlands4", "DOOMEternal", "DOOM", "farcry6", "SniperElite5",
        "DeepRockGalactic", "PlanetSide2", "MetroExodus", "Deathloop", "MCC-Win64-Shipping",
        "Crysis3", "re4", "LeagueClient", "League of Legends", "dota2", "HonorOfKings",
        "OnmyojiArena", "Vainglory", "StarRail", "GenshinImpact", "YuanShen", "ZenlessZoneZero",
        "WutheringWaves", "b1-Win64-Shipping", "eldenring", "Cyberpunk2077", "GTA5", "RDR2",
        "witcher3", "bg3", "bg3_dx11", "SkyrimSE", "TESV", "Fallout4", "Starfield", "ACOdyssey",
        "ACValhalla", "AC Syndicate", "ryujinx", "yuzu", "Palworld", "PathOfExile", "PathOfExile_x64",
        "Diablo IV", "Wow", "WowClassic", "ffxiv_dx11", "LostArk", "Warframe", "MonsterHunterWorld",
        "MonsterHunterRise", "MonsterHunterWilds", "sekiro", "GhostOfTsushima", "LiesOfP",
        "LordsOfTheFallen", "WoLong", "Dragon's Dogma 2", "FF7R", "ff16", "HogwartsLegacy",
        "AtomicHeart", "Remnant2", "nioh2", "DivinityOriginalSin2", "DiscoElysium", "KingdomCome",
        "Yakuza8", "DynastyWarriorsOrigins", "EldenRingNightreign", "NarakaBladepoint",
        "ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "dirt2", "eurotrucks2", "It Takes Two",
        "Overcooked2", "Terraria", "javaw", "java", "Minecraft.Windows", "StardewValley",
        "OxygenNotIncluded", "RimWorld", "Cities", "CivilizationVI", "warhammer3", "AgeOfEmpires4",
        "ThreeKingdoms", "FallGuys_client", "TS4_x64", "ChineseParents", "Taiwu", "TaleOfImmortal",
        "DysonSphereProgram", "GodOfWar", "GoWR", "MarvelsSpiderMan", "Spider-Man2",
        "HorizonZeroDawn", "HorizonForbiddenWest", "TheLastOfUs", "Uncharted4",
        "ShadowOfTheTombRaider", "StarWarsJediSurvivor", "StarWarsJediFallenOrder",
        "BatmanArkhamKnight", "DyingLight2", "Days Gone", "DeadSpace", "AlanWake2", "Control",
        "Dishonored2", "WatchDogs2", "re8", "SplitFiction", "DeathStranding", "ACShadows",
        "CivilizationVII", "Stellaris", "CK3", "Victoria3", "HOI4", "Cities2", "Frostpunk2",
        "Warhammer40KSpaceMarine2",
        "FlightSimulator", "ForzaMotorsport", "F1_24", "FarmingSimulator25", "TwoPointHospital", "AoE2",
        "warhammer2", "valheim", "SonsOfTheForest", "VRising", "SeaOfThieves", "RocketLeague",
        "DeadByDaylight", "IdentityV", "PartyAnimals", "AmongUs", "ShooterGame", "NMS",
        "HumanFallFlat", "Hades2", "VampireSurvivors", "CultOfTheLamb", "DaveTheDiver", "Balatro",
        "AnimalWell", "DeadCells", "HollowKnight", "Soulmask", "yysls", "InfinityNikki",
        "Snowbreak", "WarmSnow", "JX3", "nsh", "Pal7", "Pal7-Win64-Shipping",
        "TowerOfFantasy", "BrawlStars", "Gujian3", "Strinova", "NZM", "TGame",
        "CrossFire", "CF", "DNF", "DNFCHINA", "wuxia", "wuxia_client",
        "client", "GameApp", "BH3", "ZXSJ", "elementclient", "mhmain",
        "xyqsvc", "XQN", "xy2", "MapleStory", "DragonNest", "Aion",
        "BNB", "Mir2", "Woool", "KartRider", "TLBB", "codol",
        "aces", "aces_x64", "cso", "Asura", "hyxd", "tw2launch",
        "tw2", "MinecraftLauncher", "Sky", "re2", "re3", "re7",
        "SquadGame", "farcry5", "farCryPrimal", "JustCause4", "GhostRunner", "D2R",
        "DarkSouls3", "DarkSoulsII", "DARK SOULS REMASTERED", "CodeVein", "NieRAutomata", "Tales of Arise",
        "Scarlet Nexus", "DetroitBecomeHuman", "MassEffectLE", "DragonAgeInquisition", "GrimDawn", "P5R",
        "P3R", "Raft", "TheForest", "GreenHell", "Subnautica", "Satisfactory",
        "Factorio", "Phasmophobia", "ProjectZomboid", "ProjectZomboid64", "rust", "7DaysToDie",
        "CoreKeeper", "PlanetCoaster", "PlanetZoo", "JurassicWorldEvolution2", "XCOM2", "TheyAreBillions",
        "dontstarve", "dontstarvetogether", "KSP", "portal2", "hl2", "eu4",
        "Anno1800", "Tropico6", "DarkestDungeon", "Outlast2", "Hellblade2", "StellarBlade",
        "DevilMayCry5", "Sifu", "Prey", "WolfNewOrder", "SlayTheSpire", "isaac-ng",
        "isaac", "Celeste", "ori", "EnterTheGungeon", "starbound", "GoatSimulator",
        "MovingOut", "NFSUnbound", "NFSHeat", "NBA2K24", "eFootball", "HeroesOfTheStorm",
        "SC2", "FFXV", "Stray", "AlienIsolation", "Fallout76", "Portia",
        "Torchlight2", "Vindictus", "BlackDesert64", "LifeIsStrange", "APlagueTale", "KOFXV",
        "StreetFighter6", "MK11", "theHunter", "MiSideFull", "inZOI"
    };

    internal static MemorySnapshot ReadStatus()
    {
        NativeMethods.MEMORYSTATUSEX status = new() { Length = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(status.TotalPhys, status.AvailPhys);
    }

    internal static bool ShouldSkipProcess(int processId, string name, int callerProcessId)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        return processId == 0 || processId == Environment.ProcessId || processId == callerProcessId
            || ProtectedProcesses.Contains(stem) || GameProcesses.Contains(stem);
    }

    internal static MemoryCleanupResult Clean(MemoryCleanRequest request, CancellationToken cancellationToken = default)
    {
        if (!MemoryCleaner.ValidItems(request.Items)) return new(null, null, [], Error: "无效的内存清理项");
        MemorySnapshot before = ReadStatus();
        List<MemoryCleanStep> steps = [];
        int processed = 0, skipped = 0, failed = 0;
        long trimmedBytes = 0;
        foreach (MemoryCleanOption option in MemoryCleaner.Options)
        {
            if (!request.Items.HasFlag(option.Item)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string detail = "已完成";
                switch (option.Item)
                {
                    case MemoryCleanItems.Standby: PurgeMemoryList(4); break;
                    case MemoryCleanItems.LowPriorityStandby: PurgeMemoryList(5); break;
                    case MemoryCleanItems.Modified: PurgeMemoryList(3); break;
                    case MemoryCleanItems.FileCache:
                        using (PrivilegeScope.Enable("SeIncreaseQuotaPrivilege"))
                        {
                            // -1 flushes the cache; flags=0 preserves the existing hard-limit settings.
                            if (!NativeMethods.SetSystemFileCacheSize(nuint.MaxValue, nuint.MaxValue, 0))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                        break;
                    case MemoryCleanItems.Registry:
                        using (PrivilegeScope.Enable("SeProfileSingleProcessPrivilege"))
                            RequireNtSuccess(NativeMethods.ReconcileRegistry(155, IntPtr.Zero, 0));
                        break;
                    case MemoryCleanItems.Combined:
                        using (PrivilegeScope.Enable("SeProfileSingleProcessPrivilege"))
                        {
                            NativeMethods.MEMORY_COMBINE_INFORMATION information = new();
                            RequireNtSuccess(NativeMethods.CombineMemory(130, ref information,
                                (uint)Marshal.SizeOf<NativeMethods.MEMORY_COMBINE_INFORMATION>()));
                            detail = $"已合并 {information.PagesCombined} 个内存页";
                        }
                        break;
                    case MemoryCleanItems.WorkingSet:
                        foreach (Process process in Process.GetProcesses())
                        {
                            using (process)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int processId = process.Id;
                                if (ShouldSkipProcess(processId, "", request.CallerProcessId)) { skipped++; continue; }
                                using SafeProcessHandle handle = OpenForTrimming(processId);
                                if (handle.IsInvalid) { skipped++; continue; }
                                StringBuilder name = new(1024);
                                if (NativeMethods.K32GetProcessImageFileNameW(handle, name, (uint)name.Capacity) == 0
                                    || ShouldSkipProcess(processId, name.ToString(), request.CallerProcessId))
                                { skipped++; continue; }
                                if (TrimProcess(handle, out long bytes)) { processed++; trimmedBytes += bytes; }
                                else failed++;
                            }
                        }
                        detail = $"整理 {processed}，跳过 {skipped}，失败 {failed}";
                        if (failed > 0 || processed == 0)
                        {
                            steps.Add(new(option.Item, option.Name, false,
                                processed == 0 ? "没有可整理的进程；" + detail : detail));
                            continue;
                        }
                        break;
                }
                steps.Add(new(option.Item, option.Name, true, detail));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                steps.Add(new(option.Item, option.Name, false, error.Message));
            }
        }
        return new(before, ReadStatus(), steps.ToArray(), processed, skipped, failed, trimmedBytes);
    }

    internal static SafeProcessHandle OpenForTrimming(int processId) => NativeMethods.OpenProcess(
        0x0400 | 0x0100 | 0x0010, false, (uint)processId); // query information, set quota, VM read

    internal static bool TrimProcess(SafeProcessHandle handle, out long trimmedBytes)
    {
        uint size = (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS>();
        NativeMethods.PROCESS_MEMORY_COUNTERS before = new() { Size = size };
        trimmedBytes = 0;
        if (!NativeMethods.K32GetProcessMemoryInfo(handle, ref before, size)) return false;
        if (!NativeMethods.K32EmptyWorkingSet(handle)) return false;
        NativeMethods.PROCESS_MEMORY_COUNTERS after = new() { Size = size };
        if (NativeMethods.K32GetProcessMemoryInfo(handle, ref after, size) && before.WorkingSetSize > after.WorkingSetSize)
            trimmedBytes = (long)(before.WorkingSetSize - after.WorkingSetSize);
        return true;
    }

    private static void PurgeMemoryList(uint command)
    {
        using PrivilegeScope privilege = PrivilegeScope.Enable("SeProfileSingleProcessPrivilege");
        RequireNtSuccess(NativeMethods.SetMemoryList(80, ref command, sizeof(uint)));
    }

    private static void RequireNtSuccess(int status)
    {
        if (status < 0)
            throw new Win32Exception((int)NativeMethods.RtlNtStatusToDosError(status),
                $"{new Win32Exception((int)NativeMethods.RtlNtStatusToDosError(status)).Message}（NTSTATUS 0x{status:X8}）");
    }

    private sealed class PrivilegeScope(SafeAccessTokenHandle token, NativeMethods.TOKEN_PRIVILEGES previous) : IDisposable
    {
        public static PrivilegeScope Enable(string name)
        {
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 0x0020 | 0x0008, out SafeAccessTokenHandle token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!NativeMethods.LookupPrivilegeValue(null, name, out NativeMethods.LUID luid))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                NativeMethods.TOKEN_PRIVILEGES privileges = new() { Count = 1, Luid = luid, Attributes = 2 };
                bool adjusted = NativeMethods.AdjustTokenPrivileges(token, false, ref privileges,
                    (uint)Marshal.SizeOf<NativeMethods.TOKEN_PRIVILEGES>(), out NativeMethods.TOKEN_PRIVILEGES oldState, out _);
                int error = Marshal.GetLastWin32Error();
                if (!adjusted || error != 0) throw new Win32Exception(error);
                return new(token, oldState);
            }
            catch { token.Dispose(); throw; }
        }

        public void Dispose()
        {
            NativeMethods.TOKEN_PRIVILEGES state = previous;
            NativeMethods.RestoreTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
            token.Dispose();
        }
    }
}
