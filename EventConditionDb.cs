using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

#nullable enable

namespace MapInfoMod;

/// <summary>
/// Mod 全局配置。
/// </summary>
internal static class ModConfig
{
    /// <summary>
    /// 详细日志开关。默认 false（仅错误和关键信息）。
    /// 排查问题时改为 true 可输出路线、遗物、概率细节。
    /// </summary>
    public static bool VerboseLogging { get; set; } = false;
}

/// <summary>
/// 事件条件判断工具：决定事件是否应该隐藏，以及是否应该置顶。
/// </summary>
public static class EventConditionDb
{
    /// <summary>
    /// 判断事件在 IsAllowed 失败时是否应该隐藏（不显示在列表中）。
    /// 章节/地图不匹配时隐藏；WarHistorianRepy 在无 LanternKey 时隐藏。
    /// </summary>
    public static bool ShouldHideWhenFailed(EventModel eventModel, IRunState runState)
    {
        int act = runState.CurrentActIndex;

        return eventModel.Id.Entry switch
        {
            // 纯章节限制
            "BRAIN_LEECH" or "ROOM_FULL_OF_CHEESE" => act >= 2,
            "DOLL_ROOM" => act != 1,
            "POTION_COURIER" or "SYMBIOTE" or "RELIC_TRADER"
                or "RANWID_THE_ELDER" or "FAKE_MERCHANT" => act == 0,

            // 混合条件：仅章节不匹配时隐藏
            "WELCOME_TO_WONGOS" => act != 1,
            "CRYSTAL_SPHERE" => act == 0,
            "STONE_OF_ALL_TIME" => act != 1,
            "THE_LEGENDS_WERE_TRUE" => act != 0,
            "TEA_MASTER" => act >= 2,   // 第三幕+不出现

            // WarHistorianRepy：仅在持有 LanternKey 时显示
            "WAR_HISTORIAN_REPY" => !HasLanternKey(runState),

            _ => false,
        };
    }

    /// <summary>
    /// 判断事件是否"实际可访问"。
    /// WarHistorianRepy 的 IsAllowed() 永远返回 false，
    /// 但 LanternKey 在第三幕通过 Hook 强制触发它，所以应视为可访问。
    /// </summary>
    public static bool IsEffectivelyAllowed(EventModel eventModel, IRunState runState)
    {
        try
        {
            if (eventModel.IsAllowed(runState))
                return true;
        }
        catch
        {
            // IsAllowed 调用失败，继续检查特殊规则
        }

        // LanternKey + Act 3 → ? 节点强制 Event → Event 强制 WarHistorianRepy
        if (eventModel.Id.Entry == "WAR_HISTORIAN_REPY"
            && runState.CurrentActIndex == 2
            && HasLanternKey(runState))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// WarHistorianRepy 特殊处理：是否应该置顶显示。
    /// </summary>
    public static bool ShouldForceFirst(EventModel eventModel, IRunState runState)
    {
        return eventModel.Id.Entry == "WAR_HISTORIAN_REPY" && HasLanternKey(runState);
    }

    internal static bool HasLanternKey(IRunState runState)
    {
        try
        {
            return runState.Players.Any(p => p.Deck.Cards.Any(c => c is LanternKey));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查是否有遗物/卡牌通过 Hook 强制 ? 节点始终出事件。
    /// LanternKey（Act 3）、GoldenCompass（获得时所在章节）。
    /// </summary>
    internal static bool IsUnknownForcedToEvent(IRunState runState)
    {
        try
        {
            // LanternKey + Act 3
            if (runState.CurrentActIndex == 2 && HasLanternKey(runState))
                return true;

            // GoldenCompass：在获得它的章节中强制 ? = Event
            foreach (var player in runState.Players)
            {
                foreach (var relic in player.Relics)
                {
                    if (relic is GoldenCompass compass && compass.GoldenPathAct == runState.CurrentActIndex)
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// JuzuBracelet（佛珠手链）：通过 Hook 移除 ? 节点的小怪类型。
    /// </summary>
    internal static bool IsMonsterRemovedFromUnknown(IRunState runState)
    {
        try
        {
            return runState.Players.Any(p => p.Relics.Any(r => r is JuzuBracelet));
        }
        catch
        {
            return false;
        }
    }
}
