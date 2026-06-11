namespace VocabSpire.Models;

/// <summary>SM-2 记忆状态。</summary>
public enum SrsState
{
    New,
    Learning,
    Review,
    Relearning,
    Mastered  // 已掌握：间隔超过退休阈值，不再日常出题
}

/// <summary>SM-2 评分等级。</summary>
public enum SrsGrade
{
    Again = 0,  // 完全忘记
    Hard = 1,  // 困难（勉强想起）
    Good = 2,  // 一般（正常回忆）
    Easy = 3   // 容易（毫不费力）
}

public sealed class WordEntry
{
    public string English { get; init; } = "";
    public string Chinese { get; init; } = "";
    public string Phonetic { get; init; } = "";
    public List<string> Definitions { get; init; } = new();

    /// <summary>所属词库 Id，用于 SaveProgress 定位正确的进度文件。</summary>
    public string BankId { get; set; } = "";

    public bool HasMultipleDefinitions => Definitions.Count > 1;
    public bool HasPhonetic => !string.IsNullOrWhiteSpace(Phonetic);

    public int CorrectCount { get; set; }
    public int WrongCount { get; set; }

    /// <summary>当前连续答对次数（答错归零）。</summary>
    public int Streak { get; set; }

    /// <summary>史上最高连续答对次数（不随答错归零）。用于判断是否已掌握。</summary>
    public int BestStreak { get; set; }

    /// <summary>因答错该词损失的总能量。</summary>
    public int EnergyLost { get; set; }

    //! ── SM-2 算法字段 ──
    //!该功能因为与 Streak 机制有冲突，功能未完善，不推荐使用。
    //TODO: 未来如果要完善 SM-2 功能，需要重新设计与 Streak 的关系，或者将两套机制分开管理。
    /// <summary>SM-2 记忆状态。</summary>
    public SrsState SrsState { get; set; } = SrsState.New;

    /// <summary>难度系数 (Ease Factor)。默认 2.5，最小 1.3。</summary>
    public float EaseFactor { get; set; } = 2.5f;

    /// <summary>复习间隔（天）。</summary>
    public int IntervalDays { get; set; }

    /// <summary>连续答对次数（SM-2 的 n，与 Streak 不同：只有 Good/Easy 才递增）。</summary>
    public int Repetitions { get; set; }

    /// <summary>下次到期日期（UTC ticks）。0 表示即时待复习。</summary>
    public long DueDateTicks { get; set; }

    /// <summary>上次复习日期（UTC ticks）。0 表示从未复习。</summary>
    public long LastReviewTicks { get; set; }

    /// <summary>当前是否到期。</summary>
    public bool IsDue => DueDateTicks == 0
        || new DateTime(DueDateTicks, DateTimeKind.Utc) <= DateTime.UtcNow;

    /// <summary>学习步长计数器（0-based，最大 = LearningSteps.Length - 1）。</summary>
    public int LearningStepIndex { get; set; }

    // ── 间隔重复调度状态（v2.7 记忆引擎）──
    /// <summary>掌握盒 0-5：0=生词/刚答错，5=已牢固。决定下次复习间隔。</summary>
    public int Box { get; set; }

    /// <summary>下次该复习的全局序号（GlobalTick）。学习阶段(Box&lt;3)用：tick 到达即「到期」。</summary>
    public long DueTick { get; set; }

    /// <summary>上次遇到该词的真实时间（Unix 秒）。毕业词(Box≥3)按真实天数判断「搁久了该复习」。</summary>
    public long LastSeenDate { get; set; }

    /// <summary>是否已答过（区分 新词 / 学习中 / 已掌握）。</summary>
    public bool Seen => CorrectCount + WrongCount > 0;

    public float Accuracy => (CorrectCount + WrongCount) == 0
        ? 0f
        : (float)CorrectCount / (CorrectCount + WrongCount);

}
