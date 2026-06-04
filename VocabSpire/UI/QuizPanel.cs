using Godot;
using MegaCrit.Sts2.Core.Logging;
using VocabSpire.Models;
using VocabSpire.Services;

namespace VocabSpire.UI;

/// <summary>
/// 答题弹窗面板 —— 支持选择题（含多选）和拼写题。
/// 选择题部分委托给 ChoiceAnswerWidget；本类只负责题目展示、反馈、统计、确认。
/// </summary>
public partial class QuizPanel : Control
{
    public static QuizPanel? Instance { get; private set; }

    private Label _modeLabel = null!;
    private Label _promptLabel = null!;
    private Label _feedbackLabel = null!;
    private Label _statsLabel = null!;
    private ChoiceAnswerWidget _choiceWidget = null!;
    private HBoxContainer _spellingContainer = null!;
    private Label _spellingHintLabel = null!;
    private LineEdit _spellingInput = null!;
    private Button _spellingSubmitBtn = null!;
    private Button _confirmButton = null!;
    private HBoxContainer _listenContainer = null!;
    private Button _listenBtn = null!;
    private Button _listenPlayTop = null!;

    // ── SM-2 4 级评分按钮 ──
    private HBoxContainer _gradeContainer = null!;
    private Button _againBtn = null!;
    private Button _hardBtn = null!;
    private Button _goodBtn = null!;
    private Button _easyBtn = null!;

    private QuizQuestion? _currentQuestion;
    private Action<bool>? _onAnswered;
    private bool _answered;
    private bool _lastCorrect;
    private bool _graded; // 是否已评分
    private ulong _answeredAtMsec; // 防止 Enter 双触发

    private static readonly Color BgColor = GameTheme.DarkBg;
    private static readonly Color AccentGold = GameTheme.Gold;
    private static readonly Color CorrectGreen = GameTheme.Green;
    private static readonly Color WrongRed = GameTheme.Red;
    private static readonly Color TextWhite = GameTheme.Cream;
    private static readonly Color TextGrey = GameTheme.LightGray;

    public override void _Ready()
    {
        Instance = this;
        BuildUI();
        GameTheme.ApplyFontRecursive(this);
        Visible = false;
        ZIndex = 100;
        ProcessMode = ProcessModeEnum.Always;
        Log.Info("[VocabSpire] QuizPanel ready.");
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

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(620, 0) };
        var panelStyle = new StyleBoxFlat
        {
            BgColor = BgColor,
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = AccentGold,
            ContentMarginTop = 28, ContentMarginBottom = 28,
            ContentMarginLeft = 36, ContentMarginRight = 36
        };
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        center.AddChild(panel);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 18);
        panel.AddChild(mainVBox);

        // 标题栏
        var titleBar = new HBoxContainer();
        mainVBox.AddChild(titleBar);
        titleBar.AddChild(GameTheme.MakeLabel("VocabSpire 背单词", 15, AccentGold));
        titleBar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _modeLabel = GameTheme.MakeLabel("", 14, TextGrey);
        titleBar.AddChild(_modeLabel);

        mainVBox.AddChild(new HSeparator());

        // 题目
        _promptLabel = GameTheme.MakeLabel("", 30, TextWhite, HorizontalAlignment.Center);
        _promptLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        mainVBox.AddChild(_promptLabel);

        // 听力模式顶部播放按钮
        var listenTopCenter = new CenterContainer();
        mainVBox.AddChild(listenTopCenter);
        _listenPlayTop = GameTheme.MakeButton("  🔊  播放发音  ", 22, GameTheme.Gold);
        _listenPlayTop.CustomMinimumSize = new Vector2(260, 54);
        _listenPlayTop.Visible = false;
        _listenPlayTop.Pressed += OnListenPressed;
        listenTopCenter.AddChild(_listenPlayTop);

        // 选择题答题区（共享组件 —— 单选/多选共用、提交按钮内置）
        _choiceWidget = new ChoiceAnswerWidget { Visible = false };
        mainVBox.AddChild(_choiceWidget);

        // 拼写简单模式掩码提示（如 "c _ _ e"）
        _spellingHintLabel = GameTheme.MakeLabel("", 32, AccentGold, HorizontalAlignment.Center);
        _spellingHintLabel.AddThemeConstantOverride("outline_size", 0);
        _spellingHintLabel.Visible = false;
        mainVBox.AddChild(_spellingHintLabel);

        // 拼写输入区
        _spellingContainer = new HBoxContainer { Visible = false };
        _spellingContainer.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(_spellingContainer);

        _spellingInput = new LineEdit
        {
            PlaceholderText = "请输入英文单词...",
            CustomMinimumSize = new Vector2(400, 46),
            ProcessMode = ProcessModeEnum.Always
        };
        _spellingInput.AddThemeFontSizeOverride("font_size", 20);
        _spellingInput.TextSubmitted += _ => OnSpellingSubmit();
        _spellingContainer.AddChild(_spellingInput);

        _spellingSubmitBtn = new Button { Text = "  确认  " };
        _spellingSubmitBtn.AddThemeFontSizeOverride("font_size", 16);
        _spellingSubmitBtn.CustomMinimumSize = new Vector2(100, 46);
        _spellingSubmitBtn.Pressed += OnSpellingSubmit;
        _spellingContainer.AddChild(_spellingSubmitBtn);

        // 听力播放按钮（备用：题目区显示后再放一个）
        _listenContainer = new HBoxContainer { Visible = false };
        mainVBox.AddChild(_listenContainer);
        var listenCenter = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _listenContainer.AddChild(listenCenter);
        _listenBtn = GameTheme.MakeButton("  🔊  播放发音  ", 20, GameTheme.Gold);
        _listenBtn.CustomMinimumSize = new Vector2(240, 56);
        _listenBtn.Pressed += OnListenPressed;
        listenCenter.AddChild(_listenBtn);

        // 反馈
        _feedbackLabel = GameTheme.MakeLabel("", 20, TextWhite, HorizontalAlignment.Center);
        mainVBox.AddChild(_feedbackLabel);

        // 统计
        _statsLabel = GameTheme.MakeLabel("", 12, TextGrey, HorizontalAlignment.Center);
        mainVBox.AddChild(_statsLabel);

        // ── SM-2 四级评分按钮（答完题后显示）──
        _gradeContainer = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            Visible = false
        };
        _gradeContainer.AddThemeConstantOverride("separation", 12);
        mainVBox.AddChild(_gradeContainer);

        _againBtn = MakeGradeButton("🔁 Again\n又忘了", new Color(0.9f, 0.2f, 0.2f));
        _againBtn.Pressed += () => OnGradeSelected(SrsGrade.Again);
        _gradeContainer.AddChild(_againBtn);

        _hardBtn = MakeGradeButton("😰 Hard\n困难", new Color(0.9f, 0.55f, 0.15f));
        _hardBtn.Pressed += () => OnGradeSelected(SrsGrade.Hard);
        _gradeContainer.AddChild(_hardBtn);

        _goodBtn = MakeGradeButton("👍 Good\n一般", new Color(0.2f, 0.65f, 0.3f));
        _goodBtn.Pressed += () => OnGradeSelected(SrsGrade.Good);
        _gradeContainer.AddChild(_goodBtn);

        _easyBtn = MakeGradeButton("🚀 Easy\n容易", new Color(0.2f, 0.5f, 0.9f));
        _easyBtn.Pressed += () => OnGradeSelected(SrsGrade.Easy);
        _gradeContainer.AddChild(_easyBtn);

        // 继续按钮（评完分后显示）
        var confirmContainer = new CenterContainer();
        mainVBox.AddChild(confirmContainer);
        _confirmButton = new Button
        {
            Text = "  继续 (Enter)  ",
            CustomMinimumSize = new Vector2(200, 44),
            Visible = false
        };
        _confirmButton.AddThemeStyleboxOverride("normal", MakeGoldButtonStyle(0.2f));
        _confirmButton.AddThemeStyleboxOverride("hover", MakeGoldButtonStyle(0.35f));
        _confirmButton.AddThemeColorOverride("font_color", AccentGold);
        _confirmButton.AddThemeFontSizeOverride("font_size", 18);
        _confirmButton.Pressed += OnConfirmPressed;
        confirmContainer.AddChild(_confirmButton);
    }

    private static Button MakeGradeButton(string text, Color color)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(120, 64),
            Alignment = HorizontalAlignment.Center
        };
        var alpha = 0.25f;
        btn.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = new Color(color.R, color.G, color.B, alpha),
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = color,
            ContentMarginLeft = 14, ContentMarginRight = 14,
            ContentMarginTop = 10, ContentMarginBottom = 10
        });
        btn.AddThemeStyleboxOverride("hover", new StyleBoxFlat
        {
            BgColor = new Color(color.R, color.G, color.B, 0.45f),
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = color,
            ContentMarginLeft = 14, ContentMarginRight = 14,
            ContentMarginTop = 10, ContentMarginBottom = 10
        });
        btn.AddThemeColorOverride("font_color", color);
        btn.AddThemeFontSizeOverride("font_size", 14);
        return btn;
    }

    public void ShowQuiz(QuizQuestion question, Action<bool> onAnswered)
    {
        _currentQuestion = question;
        _onAnswered = onAnswered;
        _answered = false;
        _graded = false;
        _lastCorrect = false;

        var modeText = question.Mode switch
        {
            QuizModeFlags.EnglishToChinese => "英 → 中",
            QuizModeFlags.ChineseToEnglish => "中 → 英",
            QuizModeFlags.SpellEnglish => "中 → 英 (拼写)",
            QuizModeFlags.ListenToChinese => "🔊 听力模式",
            _ => ""
        };
        if (VocabConfig.Instance.EnableDifficultyScaling)
        {
            var tier = Math.Clamp(GameBridge.GetCurrentAct(), 1, 3);
            var tierName = tier switch { 1 => "基础", 2 => "进阶", _ => "挑战" };
            modeText += $"  [{tierName}]";
        }
        _modeLabel.Text = modeText;
        _promptLabel.Text = question.Prompt;

        _spellingContainer.Visible = question.IsSpelling;

        if (question.IsSpelling)
        {
            _choiceWidget.Hide();
            _listenContainer.Visible = false;
            _promptLabel.Visible = true;

            // 简单模式：显示中间挖空的掩码提示
            var hasHint = !string.IsNullOrEmpty(question.SpellingHint);
            _spellingHintLabel.Visible = hasHint;
            if (hasHint) _spellingHintLabel.Text = question.SpellingHint;

            // 朗读按钮（复用听力模式 TTS）：可选开关，不自动播放，由玩家点击
            _listenPlayTop.Visible = VocabConfig.Instance.SpellingPlayAudio;

            _spellingInput.Text = "";
            _spellingInput.Editable = true;
            _spellingSubmitBtn.Disabled = false;
            _spellingInput.CallDeferred(LineEdit.MethodName.GrabFocus);
        }
        else
        {
            // 选择题 / 听力题 —— 选项区交给共享组件
            _spellingHintLabel.Visible = false;
            _choiceWidget.ShowQuestion(question, OnChoiceAnswered);

            if (question.IsListening)
            {
                _listenContainer.Visible = false;
                _listenPlayTop.Visible = true;
                if (question.IsMultiSelect)
                {
                    _promptLabel.Visible = true;
                    _promptLabel.Text = "【多选题】";
                }
                else
                {
                    _promptLabel.Visible = false;
                }
                TtsService.Instance.Speak(question.TargetWord.English);
            }
            else
            {
                _promptLabel.Visible = true;
                // 英→中选择题：可选朗读按钮（复用听力 TTS，不自动播放，玩家点击才发音）。
                // 中→英不显示——题目是中文、答案才是英文，播放会直接读出答案。
                _listenPlayTop.Visible = question.Mode == QuizModeFlags.EnglishToChinese
                                         && VocabConfig.Instance.EnToCnPlayAudio;
            }
        }

        _feedbackLabel.Text = "";
        _confirmButton.Visible = false;
        UpdateStats();
        Visible = true;
    }

    // ── 选择题作答（由 ChoiceAnswerWidget 处理选项+提交，本处只做记账和反馈文案）──

    private void OnChoiceAnswered(bool correct, IReadOnlyCollection<int> selectedIndices)
    {
        if (_currentQuestion is null) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _lastCorrect = correct;

        var isMulti = _currentQuestion.IsMultiSelect;
        var correctText = isMulti
            ? string.Join(" | ", _currentQuestion.CorrectIndices.Select(i => _currentQuestion.Options[i]))
            : (_currentQuestion.CorrectIndex >= 0 ? _currentQuestion.Options[_currentQuestion.CorrectIndex] : "");
        var userText = selectedIndices.Count > 0
            ? string.Join("|", selectedIndices.Select(i => _currentQuestion.Options[i]))
            : "";

        if (correct)
        {
            ShowFeedback(true, null);
        }
        else
        {
            var extra = _currentQuestion.IsListening
                ? $"\n单词：{_currentQuestion.TargetWord.English}"
                : "";
            ShowFeedback(false, correctText + extra);

            // 错题详情
            var userDetail = !isMulti && selectedIndices.Count == 1
                ? (_currentQuestion.GetDetail(selectedIndices.First()) ?? "")
                : "";
            var correctDetail = !isMulti && _currentQuestion.CorrectIndex >= 0
                ? (_currentQuestion.GetDetail(_currentQuestion.CorrectIndex) ?? "")
                : _currentQuestion.TargetWord.English;
            RecordWrong(userText, correctText, userDetail, correctDetail);
        }

        RecordToRunTracker(correct, correct ? "" : userText, correctText);

        _listenContainer.Visible = false;
        _listenPlayTop.Visible = false;
        _promptLabel.Visible = true;
        if (_currentQuestion.IsListening)
            _promptLabel.Text = _currentQuestion.TargetWord.English;

        UpdateStats();

        // 显示 4 级评分按钮（等用户评分后才执行 SM-2 调度和关闭）
        ShowGradeButtons();
    }

    // ── 拼写题作答 ──

    private void OnSpellingSubmit()
    {
        if (_answered || _currentQuestion is null) return;

        var userInput = _spellingInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        _answered = true;
        _answeredAtMsec = Time.GetTicksMsec();
        _lastCorrect = _currentQuestion.CheckSpelling(userInput);

        _spellingInput.Editable = false;
        _spellingSubmitBtn.Disabled = true;
        _spellingHintLabel.Visible = false;
        _listenPlayTop.Visible = false;

        if (_lastCorrect)
        {
            ShowFeedback(true, null);
        }
        else
        {
            ShowFeedback(false, _currentQuestion.CorrectText);
            RecordWrong(userInput, _currentQuestion.CorrectText,
                "", _currentQuestion.TargetWord.Chinese);
        }
        RecordToRunTracker(_lastCorrect, userInput, _currentQuestion.CorrectText);

        UpdateStats();

        // 拼写题：正确默认 Good，用户仍可修改；错误默认 Again
        ShowGradeButtons();
    }

    // ── SM-2 评分 ──

    /// <summary>答完题后显示 4 级评分按钮。</summary>
    private void ShowGradeButtons()
    {
        _gradeContainer.Visible = true;
        _againBtn.Disabled = false;
        _hardBtn.Disabled = false;
        _goodBtn.Disabled = false;
        _easyBtn.Disabled = false;
        _confirmButton.Visible = false;
        _graded = false;
    }

    /// <summary>用户点击评分按钮。</summary>
    private void OnGradeSelected(SrsGrade grade)
    {
        if (_graded || _currentQuestion is null) return;

        _graded = true;
        _againBtn.Disabled = true;
        _hardBtn.Disabled = true;
        _goodBtn.Disabled = true;
        _easyBtn.Disabled = true;

        // 执行 SM-2 调度
        var word = _currentQuestion.TargetWord;
        var correct = grade != SrsGrade.Again;
        VocabManager.Instance.RecordAnswer(word, correct);
        SrsScheduler.Grade(word, grade);

        // 显示确认按钮
        _confirmButton.Visible = true;
    }

    // ── 反馈和错题记录 ──

    private void ShowFeedback(bool correct, string? correctAnswer)
    {
        if (correct)
        {
            _feedbackLabel.Text = "回答正确！";
            _feedbackLabel.AddThemeColorOverride("font_color", CorrectGreen);
        }
        else
        {
            _feedbackLabel.Text = $"回答错误！正确答案：{correctAnswer}";
            _feedbackLabel.AddThemeColorOverride("font_color", WrongRed);
        }
    }

    private void RecordWrong(string userAnswer, string correctAnswer,
        string userDetail = "", string correctDetail = "")
    {
        if (_currentQuestion is null) return;
        WrongAnswerTracker.Instance.RecordWrongAnswer(new WrongAnswerRecord(
            _currentQuestion.TargetWord,
            _currentQuestion.Mode,
            _currentQuestion.Prompt,
            userAnswer,
            correctAnswer,
            userDetail,
            correctDetail
        ));
    }

    private void RecordToRunTracker(bool correct, string userAnswer, string correctAnswer)
    {
        if (_currentQuestion is null) return;
        var energyCost = 0;
        if (!correct)
        {
            try { energyCost = _currentQuestion.TargetWord.EnergyLost; } catch { }
        }
        Services.RunQuizTracker.Instance.Record(new Models.RunQuizRecord
        {
            English = _currentQuestion.TargetWord.English,
            Chinese = _currentQuestion.TargetWord.Chinese,
            Mode = _currentQuestion.Mode.ToString(),
            Correct = correct,
            UserAnswer = userAnswer,
            CorrectAnswer = correctAnswer,
            EnergyCost = correct ? 0 : energyCost
        });
    }

    // ── 确认和关闭 ──

    private void OnConfirmPressed()
    {
        _confirmButton.Visible = false;
        CloseQuiz(_lastCorrect);
    }

    private void CloseQuiz(bool correct)
    {
        Visible = false;
        _gradeContainer.Visible = false;
        _onAnswered?.Invoke(correct);
        _currentQuestion = null;
        _onAnswered = null;
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        if (@event is not InputEventKey { Pressed: true } key) return;

        // 已评分但未继续：Enter 继续
        if (_graded)
        {
            if (VocabConfig.KeyMatches(key.Keycode, VocabConfig.Instance.ContinueKey))
            {
                if (Time.GetTicksMsec() - _answeredAtMsec > 500)
                {
                    OnConfirmPressed();
                }
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        // 已作答但未评分：1-4 对应 Again/Hard/Good/Easy
        if (_answered && !_graded)
        {
            var grade = key.Keycode switch
            {
                Key.Key1 => SrsGrade.Again,
                Key.Key2 => SrsGrade.Hard,
                Key.Key3 => SrsGrade.Good,
                Key.Key4 => SrsGrade.Easy,
                _ => (SrsGrade?)null
            };
            if (grade.HasValue)
            {
                OnGradeSelected(grade.Value);
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (_currentQuestion is null) return;

        // 拼写模式不拦截字母键
        if (_currentQuestion.IsSpelling) return;

        // 提交键触发提交（前提是已经选中）
        if (VocabConfig.KeyMatches(key.Keycode, VocabConfig.Instance.SubmitKey))
        {
            if (_choiceWidget.TrySubmit())
            {
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        // A-H / 1-8 → 切换选项
        var idx = key.Keycode switch
        {
            Key.A or Key.Key1 => 0,
            Key.B or Key.Key2 => 1,
            Key.C or Key.Key3 => 2,
            Key.D or Key.Key4 => 3,
            Key.E or Key.Key5 => 4,
            Key.F or Key.Key6 => 5,
            Key.G or Key.Key7 => 6,
            Key.H or Key.Key8 => 7,
            _ => -1
        };
        if (idx >= 0 && _choiceWidget.HandleKeyOption(idx))
        {
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnListenPressed()
    {
        if (_currentQuestion is null) return;
        TtsService.Instance.Speak(_currentQuestion.TargetWord.English);
    }

    private static StyleBoxFlat MakeGoldButtonStyle(float alpha)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(AccentGold.R, AccentGold.G, AccentGold.B, alpha),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = AccentGold,
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 8, ContentMarginBottom = 8
        };
    }

    private void UpdateStats()
    {
        var c = VocabConfig.Instance;
        var pct = c.TotalAnswered > 0 ? $"{c.OverallAccuracy:P0}" : "--";
        _statsLabel.Text = $"已答题：{c.TotalAnswered}  |  正确率：{pct}";
    }

    public static void Create()
    {
        var root = GameBridge.GetUIRoot();
        if (root is null) return;
        var panel = new QuizPanel
        {
            Name = "VocabSpireQuizPanel",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect
        };
        root.AddChild(panel);
    }
}
