using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 词包分组选择面板 —— 可视化网格展示所有分组，支持选择/切换。
/// </summary>
public partial class WordGroupPanel : Control
{
    public static WordGroupPanel? Instance { get; private set; }

    private Label _titleLabel = null!;
    private Label _infoLabel = null!;
    private GridContainer _grid = null!;
    private Button _disableGroupBtn = null!;
    private Label _hintLabel = null!;

    private static readonly Color Gold = GameTheme.Gold;
    private static readonly Color White = GameTheme.Cream;
    private static readonly Color Grey = GameTheme.LightGray;
    private static readonly Color Green = GameTheme.Green;
    private static readonly Color Bg = GameTheme.DarkBg;

    public override void _Ready()
    {
        Instance = this;
        BuildUI();
        GameTheme.ApplyFontRecursive(this);
        Visible = false;
        ZIndex = 100;
        ProcessMode = ProcessModeEnum.Always;
    }

    private void BuildUI()
    {
        var overlay = new ColorRect
        {
            Color = GameTheme.Backdrop,
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        };
        AddChild(overlay);

        var center = new CenterContainer
        {
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        };
        AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(700, 0) };
        var style = new StyleBoxFlat
        {
            BgColor = Bg,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = Gold,
            ContentMarginTop = 24, ContentMarginBottom = 24,
            ContentMarginLeft = 32, ContentMarginRight = 32
        };
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 14);
        panel.AddChild(mainVBox);

        _titleLabel = GameTheme.MakeLabel("词包分组选择", 20, Gold, HorizontalAlignment.Center);
        mainVBox.AddChild(_titleLabel);

        _infoLabel = GameTheme.MakeLabel("", 14, Grey, HorizontalAlignment.Center);
        mainVBox.AddChild(_infoLabel);

        mainVBox.AddChild(new HSeparator());

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(640, 350) };
        mainVBox.AddChild(scroll);

        _grid = new GridContainer
        {
            Columns = 5,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _grid.AddThemeConstantOverride("h_separation", 8);
        _grid.AddThemeConstantOverride("v_separation", 8);
        scroll.AddChild(_grid);

        _hintLabel = GameTheme.MakeLabel("", 13, Grey, HorizontalAlignment.Center);
        mainVBox.AddChild(_hintLabel);

        // 按钮行
        var btnRow = new HBoxContainer();
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        btnRow.AddThemeConstantOverride("separation", 12);
        mainVBox.AddChild(btnRow);

        _disableGroupBtn = GameTheme.MakeButton("  取消分组（恢复全词库）  ", 14);
        _disableGroupBtn.Pressed += () =>
        {
            VocabManager.Instance.SelectGroup(-1);
            Refresh();
        };
        btnRow.AddChild(_disableGroupBtn);

        // 预览当前词包
        var previewBtn = GameTheme.MakeButton("  预览当前词包  ", 14);
        previewBtn.Pressed += () =>
        {
            var pool = VocabManager.Instance.ActiveGroupWordPool;
            if (pool is { Count: > 0 })
                QuizPanel.Instance?.ShowPoolPreview(
                    $"当前词包 {VocabManager.Instance.ActiveGroup?.Label} ({pool.Count} 词)", pool);
        };
        btnRow.AddChild(previewBtn);

        var closeBtn = new Button
        {
            Text = "  关闭  ",
            CustomMinimumSize = new Vector2(100, 40)
        };
        closeBtn.AddThemeColorOverride("font_color", Gold);
        closeBtn.AddThemeFontSizeOverride("font_size", 15);
        closeBtn.Pressed += () => Visible = false;
        btnRow.AddChild(closeBtn);
    }

    public void Refresh()
    {
        // 刷新统计（用图鉴标准）
        VocabManager.Instance.RefreshGroupStats();

        foreach (var child in _grid.GetChildren())
            child.QueueFree();

        var groups = VocabManager.Instance.WordGroups;
        var activeIdx = VocabManager.Instance.ActiveGroupIndex;

        if (groups.Count == 0)
        {
            _infoLabel.Text = "未启用分组记忆。请在设置中设定每组单词数量。";
            _hintLabel.Text = "";
            _disableGroupBtn.Visible = false;
            return;
        }

        var activeGroup = VocabManager.Instance.ActiveGroup;
        _infoLabel.Text = activeGroup is not null
            ? $"当前：{activeGroup.Label}（{activeGroup.RangeText}）  |  正确率：{activeGroup.AccuracyPercent:F0}%"
            : "当前：全词库（未选择分组）";

        _disableGroupBtn.Visible = activeGroup is not null;

        var threshold = VocabConfig.Instance.GroupMasteryThreshold;
        _hintLabel.Text = $"达标阈值：{threshold}%  |  共 {groups.Count} 组  |  {groups.Count(g => g.Completed)} 组已完成";

        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var isActive = i == activeIdx;
            var btn = new Button
            {
                Text = $"{g.Label}\n{g.AccuracyPercent:F0}%",
                CustomMinimumSize = new Vector2(110, 56),
                Alignment = HorizontalAlignment.Center,
                Disabled = g.Completed
            };

            if (isActive)
            {
                btn.AddThemeColorOverride("font_color", Gold);
                btn.Text += "\n[当前]";
            }
            else if (g.Completed)
            {
                btn.AddThemeColorOverride("font_color", Green);
                btn.Text += "\n✓";
            }

            btn.AddThemeFontSizeOverride("font_size", 13);

            var gi = i; // capture
            btn.Pressed += () =>
            {
                VocabManager.Instance.SelectGroup(gi);
                Refresh();
            };
            _grid.AddChild(btn);
        }

        Visible = true;
    }

    public static void Create()
    {
        var root = GameBridge.GetUIRoot();
        if (root is null) return;
        var panel = new WordGroupPanel
        {
            Name = "VocabSpireWordGroupPanel",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        };
        root.AddChild(panel);
    }
}
