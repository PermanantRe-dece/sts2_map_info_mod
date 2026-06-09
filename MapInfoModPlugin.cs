using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

#nullable enable

namespace MapInfoMod;

/// <summary>
/// Mod 入口 — Harmony 补丁。
/// </summary>

// Patch 1: 注入 UI 面板到地图界面
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._Ready))]
public static class MapScreenReadyPatch
{
    private const string PanelNodeName = "MapInfoPanel";

    public static void Postfix(NMapScreen __instance)
    {
        Log.Info("[MapInfoMod] NMapScreen._Ready postfix invoked");

        foreach (var child in __instance.GetChildren())
        {
            if (child is Node node && node.Name == PanelNodeName)
            {
                Log.Info("[MapInfoMod] Panel already exists, skipping creation");
                return;
            }
        }

        try
        {
            Log.Info("[MapInfoMod] Creating MapInfoPanel...");
            var panel = new MapInfoPanel(__instance);
            panel.Name = PanelNodeName;
            panel.Visible = false;
            __instance.AddChild(panel);
            Log.Info("[MapInfoMod] Panel added as child of NMapScreen");

            __instance.Opened += () =>
            {
                if (ModConfig.VerboseLogging) Log.Info("[MapInfoMod] NMapScreen.Opened fired, refreshing panel...");
                if (GodotObject.IsInstanceValid(panel))
                    panel.RefreshAndShow();
                else
                    Log.Warn("[MapInfoMod] Panel instance is no longer valid!");
            };

            __instance.Closed += () =>
            {
                if (ModConfig.VerboseLogging) Log.Info("[MapInfoMod] NMapScreen.Closed fired, hiding panel");
                if (GodotObject.IsInstanceValid(panel))
                    panel.Hide();
            };

            Log.Info("[MapInfoMod] MapInfoPanel setup complete");
        }
        catch (System.Exception ex)
        {
            Log.Error($"[MapInfoMod] Exception in Postfix: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }
}

// Patch 2: 在 BuildRoomTypeBlacklist 记录触发原因
[HarmonyPatch(typeof(RunManager), nameof(RunManager.BuildRoomTypeBlacklist))]
public static class BlacklistPatch
{
    public static void Postfix(MapPointHistoryEntry? previousMapPointEntry, IReadOnlyCollection<MapPoint> nextMapPoints, HashSet<RoomType> __result)
    {
        if (ModConfig.VerboseLogging)
        {
            bool cond1 = previousMapPointEntry != null && previousMapPointEntry.HasRoomOfType(RoomType.Shop);
            bool cond2 = nextMapPoints.Count > 0 && nextMapPoints.All(p => p.PointType == MapPointType.Shop);
            var childTypes = string.Join(",", nextMapPoints.Select(p => p.PointType.ToString()));
            Log.Info($"[MapInfoMod] BLACKLIST: prevHasShop={cond1}, allChildrenShop={cond2} (children=[{childTypes}]), blacklistedShop={__result.Contains(RoomType.Shop)}");
        }
    }
}

// Patch 3: 在 UnknownMapPointOdds.Roll 前后记录状态（仅调试日志）
[HarmonyPatch(typeof(UnknownMapPointOdds), nameof(UnknownMapPointOdds.Roll))]
public static class UnknownMapPointRollPatch
{
    public static void Prefix(UnknownMapPointOdds __instance, IEnumerable<RoomType> blacklist, IRunState runState)
    {
        if (ModConfig.VerboseLogging)
        {
            var bl = string.Join(",", blacklist);
            Log.Info($"[MapInfoMod] ROLL BEFORE: M={__instance.MonsterOdds:F4} T={__instance.TreasureOdds:F4} S={__instance.ShopOdds:F4} E={__instance.EliteOdds:F4} Ev={__instance.EventOdds:F4} | blacklist=[{bl}]");
        }
    }

    public static void Postfix(UnknownMapPointOdds __instance, RoomType __result)
    {
        if (ModConfig.VerboseLogging)
            Log.Info($"[MapInfoMod] ROLL AFTER: result={__result} | M={__instance.MonsterOdds:F4} T={__instance.TreasureOdds:F4} S={__instance.ShopOdds:F4} E={__instance.EliteOdds:F4} Ev={__instance.EventOdds:F4}");
    }
}
