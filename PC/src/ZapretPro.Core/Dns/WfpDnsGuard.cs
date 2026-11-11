using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZapretPro.Core.Dns;

/// <summary>Не даёт программам спрашивать DNS мимо нашего, пока работает VPN.</summary>
public interface IDnsLeakGuard
{
    bool Enabled { get; }

    /// <summary>Включить. Null — получилось, иначе причина, почему нет.</summary>
    string? Enable(IReadOnlyList<string> allowedApps);

    void Disable();
}

/// <summary>
/// Фильтры Windows Filtering Platform: соединения на порт 53 (DNS) запрещены всем, кроме самой программы
/// (её DNS пересылает вопросы провайдеру), её VPN-программ и адресов 127.0.0.1 / ::1. Так Windows не может
/// спросить DNS провайдера мимо нашего, а сами DNS у адаптеров мы не трогаем.
/// Сессия WFP «динамическая»: все фильтры исчезают, как только программа закрыла сессию или умерла
/// (упала, сняли задачу, выключили компьютер) — застрять нечему. Так же делают WireGuard и Mullvad.
/// </summary>
public sealed class WfpDnsGuard : IDnsLeakGuard
{
    // Ключи постоянные: зависшую программу сторож может убрать по ним (RemoveLeftovers)
    internal static readonly Guid SubLayerKey = new("7a0f3c1e-52b4-4f1d-9c6e-0d3f8a6b2e41");
    internal static readonly Guid[] FilterKeys =
    [
        new("7a0f3c1e-52b4-4f1d-9c6e-0d3f8a6b2e42"), // v4: программы
        new("7a0f3c1e-52b4-4f1d-9c6e-0d3f8a6b2e43"), // v4: 127.0.0.1
        new("7a0f3c1e-52b4-4f1d-9c6e-0d3f8a6b2e44"), // v4: остальным нельзя
        new("7a0f3c1e-52b4-4f1d-9c6e-0d3f8a6b2e45"), // v6: программы
        new("7a0f3c1e-52b4-4f1d-9c6e-0d3f8a6b2e46"), // v6: ::1
        new("7a0f3c1e-52b4-4f1d-9c6e-0d3f8a6b2e47"), // v6: остальным нельзя
    ];

    internal static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    internal static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    internal static readonly Guid ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    internal static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    internal static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");

    internal const uint FwpUint8 = 1, FwpUint16 = 2, FwpUint32 = 3, FwpByteBlob = 12;
    internal const uint MatchEqual = 0, MatchFlagsAllSet = 6;
    internal const uint ConditionFlagIsLoopback = 0x1;
    internal const uint ActionBlock = 0x1001, ActionPermit = 0x1002;
    internal const uint SessionFlagDynamic = 0x1;
    internal const uint RpcAuthnWinNt = 10;

    private readonly object _lock = new();
    private IntPtr _engine;

    public bool Enabled
    {
        get
        {
            lock (_lock)
                return _engine != IntPtr.Zero;
        }
    }

    public string? Enable(IReadOnlyList<string> allowedApps)
    {
        lock (_lock)
        {
            if (_engine != IntPtr.Zero)
                return null;
            try
            {
                return EnableCore(allowedApps);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return $"в Windows нет WFP ({e.Message})";
            }
        }
    }

    public void Disable()
    {
        lock (_lock)
        {
            if (_engine == IntPtr.Zero)
                return;
            // Динамическая сессия: закрыли — Windows сама убрала все фильтры и подслой
            FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Убрать фильтры чужого процесса программы — когда она зависла и её DNS не отвечает (зовёт сторож).
    /// Умерший процесс убирать не нужно: его фильтры уже исчезли сами.
    /// </summary>
    public static bool RemoveLeftovers()
    {
        try
        {
            var session = new FWPM_SESSION0();
            if (FwpmEngineOpen0(IntPtr.Zero, RpcAuthnWinNt, IntPtr.Zero, ref session, out var engine) != 0)
                return false;
            try
            {
                var any = false;
                foreach (var key in FilterKeys)
                {
                    var k = key;
                    any |= FwpmFilterDeleteByKey0(engine, ref k) == 0;
                }
                var sub = SubLayerKey;
                FwpmSubLayerDeleteByKey0(engine, ref sub);
                return any;
            }
            finally
            {
                FwpmEngineClose0(engine);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private string? EnableCore(IReadOnlyList<string> allowedApps)
    {
        var strings = new List<IntPtr>();
        var blocks = new List<IntPtr>();
        var appIds = new List<IntPtr>();
        IntPtr Str(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            strings.Add(p);
            return p;
        }

        var session = new FWPM_SESSION0
        {
            displayData = new FWPM_DISPLAY_DATA0 { name = Str("FiolKid"), description = Str("FiolKid: DNS только через программу") },
            flags = SessionFlagDynamic,
        };
        var error = FwpmEngineOpen0(IntPtr.Zero, RpcAuthnWinNt, IntPtr.Zero, ref session, out var engine);
        if (error != 0)
        {
            FreeAll(strings, blocks, appIds);
            return $"WFP не открылся: {Describe(error)}";
        }

        var committed = false;
        try
        {
            foreach (var app in allowedApps.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (FwpmGetAppIdFromFileName0(app, out var appId) == 0)
                    appIds.Add(appId);
            }
            if (appIds.Count == 0)
                return "не удалось получить AppId программы";

            if ((error = FwpmTransactionBegin0(engine, 0)) != 0)
                return $"WFP: транзакция не началась: {Describe(error)}";

            var subLayer = new FWPM_SUBLAYER0
            {
                subLayerKey = SubLayerKey,
                displayData = new FWPM_DISPLAY_DATA0 { name = Str("FiolKid DNS"), description = Str("FiolKid: DNS только через программу") },
                weight = 0x8000,
            };
            if ((error = FwpmSubLayerAdd0(engine, ref subLayer, IntPtr.Zero)) != 0)
                return Abort(engine, $"WFP: подслой не добавился: {Describe(error)}");

            var layers = new[] { LayerConnectV4, LayerConnectV6 };
            for (var i = 0; i < layers.Length; i++)
            {
                var port = Condition(ConditionRemotePort, MatchEqual, FwpUint16, 53);

                // Программа и её VPN: несколько условий одного поля — «любое из»
                var apps = new List<FWPM_FILTER_CONDITION0> { port };
                apps.AddRange(appIds.Select(id => Condition(ConditionAppId, MatchEqual, FwpByteBlob, id)));
                error = AddFilter(engine, FilterKeys[i * 3], layers[i], "FiolKid DNS: программа", ActionPermit, 10, apps, Str, blocks);
                if (error != 0)
                    return Abort(engine, $"WFP: фильтр не добавился: {Describe(error)}");

                // Наш DNS на 127.0.0.1 / ::1 — его спрашивает служба DNS Windows
                var loopback = new List<FWPM_FILTER_CONDITION0>
                    { port, Condition(ConditionFlags, MatchFlagsAllSet, FwpUint32, (IntPtr)ConditionFlagIsLoopback) };
                error = AddFilter(engine, FilterKeys[i * 3 + 1], layers[i], "FiolKid DNS: 127.0.0.1", ActionPermit, 9, loopback, Str, blocks);
                if (error != 0)
                    return Abort(engine, $"WFP: фильтр не добавился: {Describe(error)}");

                error = AddFilter(engine, FilterKeys[i * 3 + 2], layers[i], "FiolKid DNS: мимо программы нельзя", ActionBlock, 1, [port], Str, blocks);
                if (error != 0)
                    return Abort(engine, $"WFP: фильтр не добавился: {Describe(error)}");
            }

            if ((error = FwpmTransactionCommit0(engine)) != 0)
                return Abort(engine, $"WFP: транзакция не сохранилась: {Describe(error)}");
            committed = true;
            _engine = engine;
            return null;
        }
        finally
        {
            if (!committed)
                FwpmEngineClose0(engine);
            FreeAll(strings, blocks, appIds);
        }
    }

    private static string Abort(IntPtr engine, string message)
    {
        FwpmTransactionAbort0(engine);
        return message;
    }

    private static uint AddFilter(IntPtr engine, Guid key, Guid layer, string name, uint action, byte weight,
        List<FWPM_FILTER_CONDITION0> conditions, Func<string, IntPtr> str, List<IntPtr> blocks)
    {
        var size = Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
        var array = Marshal.AllocHGlobal(size * conditions.Count);
        blocks.Add(array);
        for (var i = 0; i < conditions.Count; i++)
            Marshal.StructureToPtr(conditions[i], array + i * size, false);

        var filter = new FWPM_FILTER0
        {
            filterKey = key,
            displayData = new FWPM_DISPLAY_DATA0 { name = str(name), description = str(name) },
            layerKey = layer,
            subLayerKey = SubLayerKey,
            weight = new FWP_VALUE0 { type = FwpUint8, value = weight },
            numFilterConditions = (uint)conditions.Count,
            filterCondition = array,
            action = new FWPM_ACTION0 { type = action },
        };
        return FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _);
    }

    internal static FWPM_FILTER_CONDITION0 Condition(Guid field, uint match, uint type, IntPtr value) => new()
    {
        fieldKey = field,
        matchType = match,
        conditionValue = new FWP_VALUE0 { type = type, value = value },
    };

    internal static void FreeAll(List<IntPtr> strings, List<IntPtr> blocks, List<IntPtr> appIds)
    {
        foreach (var p in strings)
            Marshal.FreeHGlobal(p);
        foreach (var p in blocks)
            Marshal.FreeHGlobal(p);
        foreach (var p in appIds)
        {
            var copy = p;
            FwpmFreeMemory0(ref copy);
        }
        strings.Clear();
        blocks.Clear();
        appIds.Clear();
    }

    internal static string Describe(uint error) => error switch
    {
        0x80320009 => "фильтр уже есть (FWP_E_ALREADY_EXISTS)",
        0x5 => "нет прав администратора",
        0x6D9 => "служба «Базовый модуль фильтрации» (BFE) не запущена",
        _ => $"0x{error:X8} {new Win32Exception(unchecked((int)error)).Message}",
    };

    // Раскладка — как в fwpmtypes.h для x64 (проверяется тестом)
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_DISPLAY_DATA0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_VALUE0
    {
        public uint type;
        public IntPtr value; // объединение: число лежит в младших байтах, остальное — указатель
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_SESSION0
    {
        public Guid sessionKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public uint processId;
        public IntPtr sid;
        public IntPtr username;
        public int kernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_FILTER_CONDITION0
    {
        public Guid fieldKey;
        public uint matchType;
        public FWP_VALUE0 conditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_ACTION0
    {
        public uint type;
        public Guid filterType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_FILTER0
    {
        public Guid filterKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FWP_VALUE0 weight;
        public uint numFilterConditions;
        public IntPtr filterCondition;
        public FWPM_ACTION0 action;
        public ulong rawContext; // объединение rawContext / providerContextKey (GUID) — 16 байт с выравниванием 8
        public ulong rawContextHigh;
        public IntPtr reserved;
        public ulong filterId;
        public FWP_VALUE0 effectiveWeight;
    }

    [DllImport("fwpuclnt.dll")]
    internal static extern uint FwpmEngineOpen0(IntPtr serverName, uint authnService, IntPtr authIdentity, ref FWPM_SESSION0 session,
        out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    internal static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    internal static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport("fwpuclnt.dll")]
    internal static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    internal static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    internal static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    internal static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    internal static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);

    [DllImport("fwpuclnt.dll")]
    internal static extern uint FwpmFilterDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    internal static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll")]
    internal static extern void FwpmFreeMemory0(ref IntPtr p);
}
