using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

#nullable enable

namespace MapInfoMod;

/// <summary>
/// 地图信息面板：显示当前章节事件池和药水掉落概率。
/// 作为 NMapScreen 的子节点添加到地图界面左侧。
/// </summary>
public class MapInfoPanel : Control
{
    // ============ 颜色常量 ============
    private static readonly Color _colorAvailable = new Color(1f, 1f, 1f, 1f);
    private static readonly Color _colorConditionFailed = new Color(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Color _colorPanelBg = new Color(0.05f, 0.05f, 0.08f, 0.82f);
    private static readonly Color _colorTitle = new Color(1f, 0.85f, 0.3f, 1f);

    // ============ UI 节点 ============
    private VBoxContainer _mainContainer = null!;
    private Label _potionLabel = null!;
    private Label _unknownLabel = null!;
    private Label _eventTitleLabel = null!;
    private ScrollContainer _scrollContainer = null!;
    private VBoxContainer _eventList = null!;
    private Font? _font;

    // ============ 数据 ============
    private readonly NMapScreen _mapScreen;
    private static FieldInfo? _runStateField;
    private int _refreshCount;
    private int _lastKnownUnknownCount;

    // ============ 尺寸常量 ============
    private const float PanelWidth = 220f;
    private const float PanelMarginLeft = 30f;
    private const float PanelMarginTop = 160f;
    private const int FontSizeNormal = 17;
    private const int FontSizeTitle = 20;

    /// <summary>
    /// 获取 NMapScreen 的私有 _runState 字段（缓存反射结果）。
    /// </summary>
    private static FieldInfo RunStateField
    {
        get
        {
            _runStateField ??= typeof(NMapScreen).GetField("_runState",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            return _runStateField;
        }
    }

    public MapInfoPanel(NMapScreen mapScreen)
    {
        _mapScreen = mapScreen;
        Name = "MapInfoPanel";

        // 加载字体
        try
        {
            _font = ResourceLoader.Load<FontFile>("res://fonts/kreon_bold.ttf");
        }
        catch
        {
            _font = ThemeDB.FallbackFont;
        }

        SetupUI();
    }

    /// <summary>
    /// 构建 UI 结构。
    /// </summary>
    private void SetupUI()
    {
        // 面板自身设置 — 锚定到左上角
        SetAnchorsPreset(LayoutPreset.TopLeft);
        OffsetLeft = PanelMarginLeft;
        OffsetRight = PanelMarginLeft + PanelWidth;
        OffsetTop = PanelMarginTop;
        OffsetBottom = 100f;

        // 背景色块
        var bg = new ColorRect();
        bg.Name = "Background";
        bg.Color = _colorPanelBg;
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // 内边距容器
        var marginContainer = new MarginContainer();
        marginContainer.Name = "MarginContainer";
        marginContainer.SetAnchorsPreset(LayoutPreset.FullRect);
        marginContainer.AddThemeConstantOverride("margin_left", 8);
        marginContainer.AddThemeConstantOverride("margin_top", 8);
        marginContainer.AddThemeConstantOverride("margin_right", 8);
        marginContainer.AddThemeConstantOverride("margin_bottom", 8);
        AddChild(marginContainer);

        // 主容器
        _mainContainer = new VBoxContainer();
        _mainContainer.Name = "MainContainer";
        _mainContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _mainContainer.AddThemeConstantOverride("separation", 4);
        marginContainer.AddChild(_mainContainer);

        // === 药水概率标签 ===
        _potionLabel = CreateLabel("药水掉落率: --", FontSizeTitle);
        _potionLabel.AddThemeColorOverride("font_color", _colorTitle);
        _mainContainer.AddChild(_potionLabel);

        // === 未知节点概率 ===
        _unknownLabel = CreateLabel("?节点: --", FontSizeNormal);
        _unknownLabel.AddThemeColorOverride("font_color", _colorTitle);
        _mainContainer.AddChild(_unknownLabel);

        // 分隔线
        var separator = new HSeparator();
        _mainContainer.AddChild(separator);

        // === 事件池标题 ===
        _eventTitleLabel = CreateLabel("当前事件池", FontSizeTitle - 2);
        _eventTitleLabel.AddThemeColorOverride("font_color", _colorTitle);
        _mainContainer.AddChild(_eventTitleLabel);

        // === 可滚动事件列表 ===
        _scrollContainer = new ScrollContainer();
        _scrollContainer.Name = "EventScroll";
        _scrollContainer.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        _scrollContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scrollContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _mainContainer.AddChild(_scrollContainer);

        _eventList = new VBoxContainer();
        _eventList.Name = "EventList";
        _eventList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _eventList.AddThemeConstantOverride("separation", 2);
        _scrollContainer.AddChild(_eventList);
    }

    /// <summary>
    /// 创建一个预设样式的 Label。
    /// </summary>
    private Label CreateLabel(string text, int fontSize)
    {
        var label = new Label();
        label.Text = text;
        if (_font != null)
        {
            label.AddThemeFontOverride("font", _font);
        }
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.HorizontalAlignment = HorizontalAlignment.Left;
        return label;
    }

    /// <summary>
    /// 刷新数据并显示面板。
    /// </summary>
    public void RefreshAndShow()
    {
        _refreshCount++;
        try
        {
            if (ModConfig.VerboseLogging) Log.Info($"[MapInfoMod] === Refresh #{_refreshCount} ===");

            // 通过反射获取 RunState
            var runState = (RunState?)RunStateField.GetValue(_mapScreen);
            if (runState == null)
            {
                Log.Warn("[MapInfoMod] _runState is null — Initialize() may not have been called yet");
                _potionLabel.Text = "药水掉落率: (无数据)";
                Visible = true;
                return;
            }

            if (ModConfig.VerboseLogging)
            {
                var history = runState.MapPointHistory.SelectMany(l => l).ToList();
                int currentUnknownCount = history.Count(p => p.MapPointType == MapPointType.Unknown);
                var route = string.Join(" → ", history.Select(h =>
                {
                    var rooms = string.Join("+", h.Rooms.Select(r => r.RoomType.ToString()));
                    return $"{h.MapPointType}({rooms})";
                }));
                Log.Info($"[MapInfoMod] Act={runState.CurrentActIndex}, Unknown visited={currentUnknownCount} (Δ={currentUnknownCount - _lastKnownUnknownCount}), Route: {route}");
                _lastKnownUnknownCount = currentUnknownCount;

                try
                {
                    var relicNames = string.Join(", ", runState.Players[0].Relics.Select(r => r.Id.Entry));
                    Log.Info($"[MapInfoMod] Player relics ({runState.Players[0].Relics.Count}): {relicNames}");
                }
                catch (Exception ex)
                {
                    Log.Info($"[MapInfoMod] Could not list relics: {ex.Message}");
                }
            }

            // 更新药水概率
            UpdatePotionChance(runState);

            // 更新未知节点概率
            UpdateUnknownOdds(runState);

            // 更新事件列表
            UpdateEventList(runState);

            // 延迟计算面板高度（等待布局系统完成）
            Callable.From(UpdatePanelHeight).CallDeferred();

            Visible = true;
            if (ModConfig.VerboseLogging) Log.Info("[MapInfoMod] Panel refreshed and visible");
        }
        catch (Exception ex)
        {
            Log.Error($"[MapInfoMod] Exception in RefreshAndShow: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            _potionLabel.Text = $"错误: {ex.Message}";
            Visible = true;
        }
    }

    /// <summary>
    /// 更新药水掉落概率显示（基础 / 精英两行，白野兽雕像时显示 100%）。
    /// </summary>
    private void UpdatePotionChance(RunState runState)
    {
        try
        {
            if (runState.Players.Count > 0)
            {
                var player = runState.Players[0];

                // 白野兽雕像 → 100%
                if (EventConditionDb.HasWhiteBeastStatue(runState))
                {
                    _potionLabel.Text = "药水掉率: 100%";
                    if (ModConfig.VerboseLogging) Log.Info("[MapInfoMod] Potion chance: 100% (WhiteBeastStatue)");
                    return;
                }

                var baseValue = player.PlayerOdds.PotionReward.CurrentValue;
                var basePercent = (int)Math.Round(baseValue * 100);
                var elitePercent = (int)Math.Round((baseValue + 0.125f) * 100);
                _potionLabel.Text = $"药水掉率: {basePercent}%\n  精英战: {elitePercent}%";

                if (ModConfig.VerboseLogging)
                    Log.Info($"[MapInfoMod] Potion: base={baseValue:F3}->{basePercent}%, elite={baseValue + 0.125f:F3}->{elitePercent}%");
            }
            else
            {
                _potionLabel.Text = "药水掉率: (无玩家)";
                Log.Warn("[MapInfoMod] No players in runState");
            }
        }
        catch (Exception ex)
        {
            _potionLabel.Text = "药水掉率: (无法获取)";
            Log.Error($"[MapInfoMod] Exception getting potion chance: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新未知节点概率显示。
    /// </summary>
    private void UpdateUnknownOdds(RunState runState)
    {
        try
        {
            var odds = runState.Odds.UnknownMapPoint;

            // LanternKey / GoldenCompass: Hook 强制 roomTypes={Event}
            if (EventConditionDb.IsUnknownForcedToEvent(runState))
            {
                _unknownLabel.Text = "?节点: 事件100%";
                if (ModConfig.VerboseLogging) Log.Info("[MapInfoMod] Unknown odds: forced Event (LanternKey/GoldenCompass)");
                return;
            }

            // 主动计算下一次 ? 节点的黑名单
            // 条件1 用 CurrentMapPointHistoryEntry（前一节点的房型）
            // 条件2 用 ? 节点自己的子节点（而非当前位置的子节点）
            // _siblingPointTypeRestrictions 保证最多一个 ? 子节点
            var unknownChild = runState.CurrentMapPoint?.Children
                .FirstOrDefault(c => c.PointType == MapPointType.Unknown);
            var targetChildren = unknownChild?.Children
                ?? new HashSet<MapPoint>();
            var blacklist = MegaCrit.Sts2.Core.Runs.RunManager.BuildRoomTypeBlacklist(
                runState.CurrentMapPointHistoryEntry, targetChildren);

            bool shopBL = blacklist.Contains(RoomType.Shop);
            bool monsterDisabled = EventConditionDb.IsMonsterRemovedFromUnknown(runState);

            float m = monsterDisabled ? 0f : odds.MonsterOdds;
            float t = odds.TreasureOdds;
            float s = shopBL ? 0f : odds.ShopOdds;
            float nonBLSum = (m >= 0f ? m : 0f) + (t >= 0f ? t : 0f) + (s >= 0f ? s : 0f);
            float ev = Math.Max(0f, 1f - nonBLSum);

            if (ModConfig.VerboseLogging)
                Log.Info($"[MapInfoMod] Unknown odds: M={odds.MonsterOdds:F4} T={odds.TreasureOdds:F4} S={odds.ShopOdds:F4} EvRaw={odds.EventOdds:F4} | blacklist=[{string.Join(",", blacklist)}] monsterDisabled={monsterDisabled} | effective: M={m*100:F0}% T={t*100:F0}% S={s*100:F0}% Ev={ev*100:F0}%");

            var parts1 = new List<string>();
            var parts2 = new List<string>();
            parts1.Add($"事件{ev * 100:F0}%");
            if (m >= 0f) parts1.Add(monsterDisabled ? $"小怪0%" : $"小怪{m * 100:F0}%");
            if (t >= 0f) parts2.Add($"宝箱{t * 100:F0}%");
            if (s >= 0f) parts2.Add(shopBL ? $"商店0%" : $"商店{s * 100:F0}%");

            var line1 = string.Join(" ", parts1);
            var line2 = string.Join(" ", parts2);
            _unknownLabel.Text = "?节点: " + line1 + (line2.Length > 0 ? "\n" + new string(' ', 12) + line2 : "");
        }
        catch (Exception ex)
        {
            _unknownLabel.Text = "?节点: (无法获取)";
            Log.Error($"[MapInfoMod] Exception getting unknown odds: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 清空并重建事件列表。
    /// </summary>
    private void UpdateEventList(RunState runState)
    {
        // 清空现有子节点
        foreach (var child in _eventList.GetChildren())
        {
            child.QueueFree();
        }

        // 获取当前章节事件池
        var actEvents = runState.Act.AllEvents;
        var sharedEvents = ModelDb.AllSharedEvents;
        var allEvents = actEvents.Concat(sharedEvents).Distinct().ToList();

        // 过滤：纯章节限制且 IsAllowed 失败的事件直接隐藏
        var visibleEvents = new List<EventModel>();
        foreach (var ev in allEvents)
        {
            // 已访问过的事件不显示
            if (runState.VisitedEventIds.Contains(ev.Id))
                continue;

            bool allowed = EventConditionDb.IsEffectivelyAllowed(ev, runState);
            if (!allowed && EventConditionDb.ShouldHideWhenFailed(ev, runState))
                continue;
            visibleEvents.Add(ev);
        }

        // WarHistorianRepy 特殊处理：持有 LanternKey 时置顶
        var forceFirst = visibleEvents.FirstOrDefault(ev => EventConditionDb.ShouldForceFirst(ev, runState));
        if (forceFirst != null)
        {
            visibleEvents.Remove(forceFirst);
            visibleEvents.Insert(0, forceFirst);
        }

        if (ModConfig.VerboseLogging)
            Log.Info($"[MapInfoMod] Event list: {visibleEvents.Count} shown / {allEvents.Count} total (act={actEvents.Count()}, shared={sharedEvents.Count()})");

        // 按状态排序：可触发 > 条件不满足，每组内按名称排序
        // WarHistorianRepy 始终在最前
        visibleEvents.Sort((a, b) =>
        {
            bool repyA = EventConditionDb.ShouldForceFirst(a, runState);
            bool repyB = EventConditionDb.ShouldForceFirst(b, runState);
            if (repyA && !repyB) return -1;
            if (!repyA && repyB) return 1;

            bool allowedA = EventConditionDb.IsEffectivelyAllowed(a, runState);
            bool allowedB = EventConditionDb.IsEffectivelyAllowed(b, runState);
            if (allowedA && !allowedB) return -1;
            if (!allowedA && allowedB) return 1;

            return string.Compare(GetEventDisplayName(a), GetEventDisplayName(b), StringComparison.Ordinal);
        });

        // 创建事件行 + 统计
        int countAvailable = 0;
        int countFailed = 0;
        foreach (var eventModel in visibleEvents)
        {
            var row = CreateEventRow(eventModel, runState);
            _eventList.AddChild(row);

            bool allowed = EventConditionDb.IsEffectivelyAllowed(eventModel, runState);
            if (allowed)
                countAvailable++;
            else
                countFailed++;
        }

        _eventTitleLabel.Text = $"事件池 {countAvailable}/{visibleEvents.Count} ({countFailed}未满足)";

        if (ModConfig.VerboseLogging)
            Log.Info($"[MapInfoMod] Created {visibleEvents.Count} event rows (available={countAvailable}, failed={countFailed})");
    }

    /// <summary>
    /// 为单个事件创建一行 UI。
    /// </summary>
    private HBoxContainer CreateEventRow(EventModel eventModel, RunState runState)
    {
        var row = new HBoxContainer();
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // 获取状态
        bool isAllowed = EventConditionDb.IsEffectivelyAllowed(eventModel, runState);

        // 事件名称标签
        var nameLabel = CreateLabel(GetEventDisplayName(eventModel), FontSizeNormal);
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLabel.HorizontalAlignment = HorizontalAlignment.Left;
        nameLabel.SizeFlagsStretchRatio = 1f;

        // 根据状态设置颜色
        if (!isAllowed)
            nameLabel.AddThemeColorOverride("font_color", _colorConditionFailed);
        else
            nameLabel.AddThemeColorOverride("font_color", _colorAvailable);

        row.AddChild(nameLabel);

        return row;
    }

    /// <summary>
    /// 获取事件的显示名称（本地化）。
    /// </summary>
    private static string GetEventDisplayName(EventModel eventModel)
    {
        try
        {
            var formatted = eventModel.Title.GetFormattedText();
            if (!string.IsNullOrEmpty(formatted))
                return formatted;
        }
        catch
        {
            // 本地化失败，使用 ID entry
        }
        return eventModel.Id.Entry;
    }

    /// <summary>
    /// 根据内容更新面板高度。
    /// </summary>
    private void UpdatePanelHeight()
    {
        // 计算总高度：标题区域 + 事件列表内容
        float contentHeight = 0;
        foreach (var child in _eventList.GetChildren())
        {
            if (child is Control ctrl && !ctrl.IsQueuedForDeletion())
                contentHeight += ctrl.Size.Y + 4; // 4 = separation
        }

        float totalHeight = 100f + Math.Min(contentHeight, 600f);
        Size = new Vector2(PanelWidth, totalHeight);

        // 更新滚动容器最小高度
        _scrollContainer.CustomMinimumSize = new Vector2(0, Math.Min(contentHeight, 560f));
    }

    /// <summary>
    /// 隐藏面板。
    /// </summary>
    public new void Hide()
    {
        Visible = false;
    }
}
