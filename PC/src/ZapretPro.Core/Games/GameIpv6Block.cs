using System.Runtime.InteropServices;
using ZapretPro.Core.Dns;
using static ZapretPro.Core.Dns.WfpDnsGuard;

namespace ZapretPro.Core.Games;

/// <summary>
/// Не даёт играм из списка ходить по IPv6. Туннель VPN — только IPv4, а маршрут по IPv6 в него не поставить и
/// IPv6-соединение не порвать (SetTcpEntry умеет только IPv4). Без IPv6 игра сама уходит на IPv4, а его мы
/// ведём в VPN. Фильтр WFP в динамической сессии: исчезает, когда игра закрылась или программа умерла.
/// </summary>
public sealed class GameIpv6Block : IDisposable
{
    private static readonly Guid SubLayerKey = new("4b9e2a61-7c3d-4e8f-a1b2-5d6e7f809a10");

    private readonly object _lock = new();
    private IntPtr _engine;
    private HashSet<string> _apps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Поставить блок для этих exe (прежний снимается). Null — получилось, иначе причина.</summary>
    public string? Update(IReadOnlyCollection<string> exes)
    {
        lock (_lock)
        {
            var wanted = new HashSet<string>(exes.Where(File.Exists), StringComparer.OrdinalIgnoreCase);
            if (wanted.SetEquals(_apps) && (_engine != IntPtr.Zero || wanted.Count == 0))
                return null;
            Close();
            _apps = wanted;
            if (wanted.Count == 0)
                return null;
            try
            {
                return Open(wanted);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return e.Message;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Close();
            _apps.Clear();
        }
    }

    private void Close()
    {
        if (_engine == IntPtr.Zero)
            return;
        FwpmEngineClose0(_engine);
        _engine = IntPtr.Zero;
    }

    private string? Open(IReadOnlyCollection<string> exes)
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
            displayData = new FWPM_DISPLAY_DATA0 { name = Str("FiolKid игры"), description = Str("FiolKid: игры без IPv6") },
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
            foreach (var exe in exes)
            {
                if (FwpmGetAppIdFromFileName0(exe, out var appId) == 0)
                    appIds.Add(appId);
            }
            if (appIds.Count == 0)
                return "не удалось получить AppId игры";
            if ((error = FwpmTransactionBegin0(engine, 0)) != 0)
                return $"WFP: {Describe(error)}";

            var subLayer = new FWPM_SUBLAYER0
            {
                subLayerKey = SubLayerKey,
                displayData = new FWPM_DISPLAY_DATA0 { name = Str("FiolKid игры"), description = Str("FiolKid: игры без IPv6") },
                weight = 0x8000,
            };
            if ((error = FwpmSubLayerAdd0(engine, ref subLayer, IntPtr.Zero)) != 0)
            {
                FwpmTransactionAbort0(engine);
                return $"WFP: {Describe(error)}";
            }

            // Любое из exe игры, кроме соединений с самим компьютером (::1)
            var conditions = appIds.Select(id => Condition(ConditionAppId, MatchEqual, FwpByteBlob, id)).ToList();
            conditions.Add(Condition(ConditionFlags, 8 /* FWP_MATCH_FLAGS_NONE_SET */, FwpUint32, (IntPtr)ConditionFlagIsLoopback));
            var size = Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
            var array = Marshal.AllocHGlobal(size * conditions.Count);
            blocks.Add(array);
            for (var i = 0; i < conditions.Count; i++)
                Marshal.StructureToPtr(conditions[i], array + i * size, false);

            var filter = new FWPM_FILTER0
            {
                filterKey = Guid.NewGuid(),
                displayData = new FWPM_DISPLAY_DATA0 { name = Str("FiolKid: игра без IPv6"), description = Str("FiolKid: игра без IPv6") },
                layerKey = LayerConnectV6,
                subLayerKey = SubLayerKey,
                weight = new FWP_VALUE0 { type = FwpUint8, value = 5 },
                numFilterConditions = (uint)conditions.Count,
                filterCondition = array,
                action = new FWPM_ACTION0 { type = ActionBlock },
            };
            if ((error = FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _)) != 0)
            {
                FwpmTransactionAbort0(engine);
                return $"WFP: {Describe(error)}";
            }
            if ((error = FwpmTransactionCommit0(engine)) != 0)
                return $"WFP: {Describe(error)}";
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
}
