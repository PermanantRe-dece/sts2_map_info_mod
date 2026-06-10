using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
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

    // ============ 事件选项显示配置 ============

    /// <summary>总是隐藏的选项名（锁定变体等）。</summary>
    private static readonly HashSet<string> _alwaysExcludeOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOCKED",       // Zen Weaver, EndlessConveyor
        "NO_OPTIONS",   // Self-Help Book
    };

    /// <summary>事件选项显示模式。</summary>
    private enum EventDisplayMode { Default, ShowTitleAndDescription }

    /// <summary>需要显示标题+描述的事件（代价在标题中）。</summary>
    private static readonly Dictionary<string, EventDisplayMode> _eventDisplayOverrides = new()
    {
        ["RANWID_THE_ELDER"] = EventDisplayMode.ShowTitleAndDescription,
    };

    /// <summary>不依赖 GameInfoOptions、使用手动描述的事件。</summary>
    private static readonly Dictionary<string, string> _manualDescriptionEvents = new()
    {
        ["RELIC_TRADER"] = "• 用你的一件遗物换取另一件遗物。",
        ["COLORFUL_PHILOSOPHERS"] = "• 从其他角色的卡池中选择3张牌。\n  （可选颜色取决于已解锁角色。）",
        ["SPIRALING_WHIRLPOOL"] = "• 观察螺旋：选择一张牌附魔 Spiral。\n• 饮用漩涡水：回复33%最大生命。",
    };

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
    private const float PanelMaxHeight = 560f;
    private const float PanelHeaderEstimate = 165f;
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
        _potionLabel = CreateLabel("药水掉落率: --", FontSizeNormal);
        _potionLabel.AddThemeColorOverride("font_color", _colorTitle);
        _mainContainer.AddChild(_potionLabel);

        // 分隔线
        var separator1 = new HSeparator();
        _mainContainer.AddChild(separator1);

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
                var eliteValue = (baseValue + 0.125f) * 100;
                _potionLabel.Text = $"药水掉率: {basePercent}%\n    精英战: {eliteValue:F1}%";

                if (ModConfig.VerboseLogging)
                    Log.Info($"[MapInfoMod] Potion: base={baseValue:F3}->{basePercent}%, elite={baseValue + 0.125f:F3}->{eliteValue:F1}%");
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
    /// 从 GameInfoOptions 中解析事件选项，按选项名分组。
    /// 返回 (选项名, 标题, 描述?) 列表。
    /// </summary>
    private static List<(string OptionName, string Title, string? Description)> ParseEventOptions(EventModel eventModel)
    {
        var result = new List<(string, string, string?)>();
        try
        {
            var locStrings = eventModel.GameInfoOptions.ToList();
            var titleMap = new Dictionary<string, string>();
            var descMap = new Dictionary<string, string?>();

            foreach (var locStr in locStrings)
            {
                var key = locStr.LocEntryKey;
                string? optionName = null;

                if (key.EndsWith(".title"))
                {
                    optionName = ExtractOptionName(key, ".title");
                    if (optionName != null)
                        titleMap[optionName] = locStr.GetFormattedText();
                }
                else if (key.EndsWith(".description"))
                {
                    optionName = ExtractOptionName(key, ".description");
                    if (optionName != null)
                        descMap[optionName] = locStr.GetFormattedText();
                }
            }

            // 过滤锁定变体：当同一选项的 _LOCKED 和非 _LOCKED 版本同时存在时，只保留非锁定版本
            const string lockedSuffix = "_LOCKED";
            var lockedKeys = titleMap.Keys
                .Where(k => k.EndsWith(lockedSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var lockedKey in lockedKeys)
            {
                var baseName = lockedKey.Substring(0, lockedKey.Length - lockedSuffix.Length);
                if (titleMap.ContainsKey(baseName))
                {
                    titleMap.Remove(lockedKey);
                    descMap.Remove(lockedKey);
                }
            }

            // 过滤排除名单中的选项（仅当还有其他选项保留时才移除）
            if (titleMap.Count > _alwaysExcludeOptions.Count)
            {
                foreach (var excl in _alwaysExcludeOptions)
                {
                    titleMap.Remove(excl);
                    descMap.Remove(excl);
                }
            }

            foreach (var kvp in titleMap)
            {
                descMap.TryGetValue(kvp.Key, out var desc);
                result.Add((kvp.Key, kvp.Value, desc));
            }

            // 排序：无数字后缀的选项在前，带 _数字 后缀的循环选项在后
            result.Sort((a, b) =>
            {
                int orderA = GetOptionOrderKey(a.Item1);
                int orderB = GetOptionOrderKey(b.Item1);
                int cmp = orderA.CompareTo(orderB);
                if (cmp != 0) return cmp;
                return string.Compare(a.Item1, b.Item1, StringComparison.Ordinal);
            });
        }
        catch (Exception ex)
        {
            if (ModConfig.VerboseLogging)
                Log.Warn($"[MapInfoMod] Failed to parse event options for {eventModel.Id.Entry}: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// 从 "xxx.pages.INITIAL.options.{name}.{suffix}" 格式的 key 中提取选项名。
    /// </summary>
    private static string? ExtractOptionName(string key, string suffix)
    {
        const string marker = ".pages.INITIAL.options.";
        int idx = key.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        int start = idx + marker.Length;
        int len = key.Length - start - suffix.Length;
        if (len <= 0) return null;
        return key.Substring(start, len);
    }

    /// <summary>
    /// 获取选项排序键。无数字后缀 → 0（优先），_N 后缀 → N+1（靠后）。
    /// </summary>
    private static int GetOptionOrderKey(string optionName)
    {
        var match = Regex.Match(optionName, @"_(\d+)$");
        return match.Success ? int.Parse(match.Groups[1].Value) + 1 : 0;
    }

    /// <summary>
    /// 为事件构建悬浮提示数据。
    /// </summary>
    private HoverTip BuildEventHoverTip(EventModel eventModel)
    {
        // 手动描述事件：不依赖 GameInfoOptions
        if (_manualDescriptionEvents.TryGetValue(eventModel.Id.Entry, out var manualDesc))
        {
            return new HoverTip(eventModel.Title, manualDesc);
        }

        // 检查显示模式
        bool showTitleAndDesc = _eventDisplayOverrides.TryGetValue(
            eventModel.Id.Entry, out var mode)
            && mode == EventDisplayMode.ShowTitleAndDescription;

        var options = ParseEventOptions(eventModel);

        string description;
        if (options.Count == 0)
        {
            description = "(无选项信息)";
        }
        else
        {
            var lines = new List<string>();
            foreach (var (_, title, desc) in options)
            {
                if (showTitleAndDesc)
                {
                    // 标题+描述模式：代价在标题中的事件（如 Ranwid the Elder）
                    var cleanedTitle = CleanDynamicVarPlaceholders(title);
                    var text = !string.IsNullOrEmpty(desc)
                        ? CleanDynamicVarPlaceholders(desc)
                        : null;
                    if (!string.IsNullOrEmpty(cleanedTitle))
                    {
                        lines.Add($"• {cleanedTitle}");
                        if (!string.IsNullOrEmpty(text))
                            lines.Add($"  {text}");
                    }
                    else if (!string.IsNullOrEmpty(text))
                    {
                        lines.Add($"• {text}");
                    }
                }
                else
                {
                    // 默认模式：只显示内容（描述），无描述时回退到标题
                    var text = !string.IsNullOrEmpty(desc) ? desc : title;
                    text = CleanDynamicVarPlaceholders(text);
                    if (!string.IsNullOrEmpty(text))
                        lines.Add($"• {text}");
                }
            }
            description = lines.Count > 0
                ? string.Join("\n", lines)
                : "(无选项信息)";
        }

        return new HoverTip(eventModel.Title, description);
    }

    /// <summary>
    /// 清理未解析的动态变量占位符，如 {IsMultiplayer:a|b}。
    /// 在有 | 分隔时取后半部分（单人模式默认）；无 | 时删除整个占位符。
    /// 正确处理嵌套花括号。
    /// </summary>
    private static string CleanDynamicVarPlaceholders(string text)
    {
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        bool inPlaceholder = false;
        bool hasPipe = false;
        var postPipe = new System.Text.StringBuilder();

        foreach (char c in text)
        {
            if (c == '{' && !inPlaceholder)
            {
                inPlaceholder = true;
                depth = 1;
                hasPipe = false;
                postPipe.Clear();
                continue;
            }

            if (inPlaceholder)
            {
                if (c == '{')
                {
                    depth++;
                    if (hasPipe)
                        postPipe.Append(c);
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        inPlaceholder = false;
                        if (hasPipe)
                            sb.Append(postPipe.ToString());
                        // 无 pipe → 整个占位符丢弃
                        continue;
                    }
                    if (hasPipe)
                        postPipe.Append(c);
                }
                else if (c == '|' && depth == 1 && !hasPipe)
                {
                    hasPipe = true;
                    postPipe.Clear(); // 丢弃前半部分（多人模式文本）
                }
                else if (hasPipe)
                {
                    postPipe.Append(c);
                }
                continue;
            }

            sb.Append(c);
        }

        // 清理多余空白
        var cleaned = Regex.Replace(sb.ToString(), @"\s+", " ");
        cleaned = cleaned.Trim();

        // 抑制未解析的数值 0 → ?
        cleaned = SuppressZeroValues(cleaned);

        // 修复残缺文本（空 StringVar 导致的 " ." 等）
        cleaned = Regex.Replace(cleaned, @"\s\.", ".");
        cleaned = Regex.Replace(cleaned, @"\.{2,}", ".");

        return cleaned;
    }

    /// <summary>
    /// 将文本中未解析的数值 0 替换为 ?。
    /// 先匹配英文本地化语境，再通用回退孤立的 0。
    /// </summary>
    private static string SuppressZeroValues(string text)
    {
        // 英文本地化语境：gain/lose/deal/take/heal/cost/pay 等后跟 0
        text = Regex.Replace(text,
            @"\b(gain|lose|deal|take|heal|costs?|pay|obtain|receive|restore)\s+0\b",
            "$1 ?", RegexOptions.IgnoreCase);
        // 0 后跟 gold/HP/Max HP/damage/block
        text = Regex.Replace(text,
            @"\b0\s+(gold|HP|Max HP|damage|block)\b",
            "? $1", RegexOptions.IgnoreCase);
        // 通用回退：空白符包围的孤立 0（跨语言）
        text = Regex.Replace(text, @"(?<=[\s>])0(?=[\s<$])", "?");
        return text;
    }

    /// <summary>
    /// 为单个事件创建一行 UI。
    /// </summary>
    private HBoxContainer CreateEventRow(EventModel eventModel, RunState runState)
    {
        var row = new HBoxContainer();
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.MouseFilter = MouseFilterEnum.Stop;

        // 获取状态
        bool isAllowed = EventConditionDb.IsEffectivelyAllowed(eventModel, runState);

        // 事件名称标签
        var nameLabel = CreateLabel(GetEventDisplayName(eventModel), FontSizeNormal);
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLabel.HorizontalAlignment = HorizontalAlignment.Left;
        nameLabel.SizeFlagsStretchRatio = 1f;
        nameLabel.MouseFilter = MouseFilterEnum.Ignore;

        // 根据状态设置颜色
        if (!isAllowed)
            nameLabel.AddThemeColorOverride("font_color", _colorConditionFailed);
        else
            nameLabel.AddThemeColorOverride("font_color", _colorAvailable);

        // 悬浮提示：显示事件选项内容
        var hoverTip = BuildEventHoverTip(eventModel);
        row.Connect(Control.SignalName.MouseEntered, Callable.From(() =>
        {
            NHoverTipSet.CreateAndShow(row, hoverTip, HoverTipAlignment.Right);
        }));
        row.Connect(Control.SignalName.MouseExited, Callable.From(() =>
        {
            NHoverTipSet.Remove(row);
        }));

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

        float totalHeight = Math.Min(PanelHeaderEstimate + contentHeight, PanelMaxHeight);
        Size = new Vector2(PanelWidth, totalHeight);

        // 更新滚动容器最小高度（留出标题区域空间）
        float scrollMax = PanelMaxHeight - PanelHeaderEstimate;
        _scrollContainer.CustomMinimumSize = new Vector2(0, Math.Min(contentHeight, scrollMax));
    }

    /// <summary>
    /// 隐藏面板并清理悬浮提示。
    /// </summary>
    public new void Hide()
    {
        // 清理事件列表中可能残留的悬浮提示
        foreach (var child in _eventList.GetChildren())
        {
            if (child is Control ctrl)
                NHoverTipSet.Remove(ctrl);
        }
        Visible = false;
    }
}
