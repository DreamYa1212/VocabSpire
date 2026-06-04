using System;
using VocabSpire.Models;

namespace VocabSpire.Services;

/// <summary>
/// SM-2 调度器：根据用户评分动态调整每张卡片的难度系数、间隔和到期日。
/// 参考 Anki SM-2 算法实现。
/// </summary>
public static class SrsScheduler
{
    /// <summary>学习步长（分钟）：New/Relearning 状态下的强制短间隔。</summary>
    private static readonly double[] LearningStepMinutes = { 10.0, 60.0, 1440.0 }; // 10min → 1h → 1d

    /// <summary>最小难度系数。</summary>
    public const float MinEaseFactor = 1.3f;

    /// <summary>默认难度系数。</summary>
    public const float DefaultEaseFactor = 2.5f;

    /// <summary>
    /// 对一张卡片评分，更新其 SM-2 状态。
    /// 调用时机：用户在 QuizPanel 点击 Again/Hard/Good/Easy 之后。
    /// </summary>
    public static void Grade(WordEntry word, SrsGrade grade, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        switch (grade)
        {
            case SrsGrade.Again:
                HandleAgain(word, now);
                break;
            case SrsGrade.Hard:
            case SrsGrade.Good:
            case SrsGrade.Easy:
                HandlePass(word, grade, now);
                break;
        }

        word.LastReviewTicks = now.Ticks;
    }

    /// <summary>再次忘记：进入 Relearning 短周期。</summary>
    private static void HandleAgain(WordEntry word, DateTime now)
    {
        word.SrsState = SrsState.Relearning;
        word.LearningStepIndex = 0;
        word.Repetitions = 0;

        // 惩罚：降低难度系数（但不低于 1.3）
        word.EaseFactor = Math.Max(MinEaseFactor, word.EaseFactor - 0.2f);

        // 10 分钟后再次复习
        word.IntervalDays = 0;
        word.DueDateTicks = now.AddMinutes(LearningStepMinutes[0]).Ticks;
    }

    /// <summary>答对（Hard/Good/Easy）：走 SM-2 标准流程。</summary>
    private static void HandlePass(WordEntry word, SrsGrade grade, DateTime now)
    {
        var isLearning = word.SrsState is SrsState.New or SrsState.Learning or SrsState.Relearning;

        if (isLearning)
        {
            HandleLearningPass(word, grade, now);
        }
        else
        {
            HandleReviewPass(word, grade, now);
        }
    }

    /// <summary>New/Learning/Relearning 状态下答对：走学习步长。</summary>
    private static void HandleLearningPass(WordEntry word, SrsGrade grade, DateTime now)
    {
        var stepIdx = word.LearningStepIndex;

        // Easy 可以跳过剩余步长，直接进入 Review
        if (grade == SrsGrade.Easy && stepIdx < LearningStepMinutes.Length - 1)
        {
            // 跳到最后一步之后：进入 Review，给初始间隔
            word.SrsState = SrsState.Review;
            word.Repetitions = 1;
            word.IntervalDays = 1;
            word.EaseFactor = Math.Max(MinEaseFactor, word.EaseFactor + 0.15f);
            word.DueDateTicks = now.AddDays(1).Ticks;
            word.LearningStepIndex = 0;
            return;
        }

        // 正常推进学习步长
        if (stepIdx < LearningStepMinutes.Length - 1)
        {
            // 还有下一步
            word.LearningStepIndex = stepIdx + 1;
            word.SrsState = SrsState.Learning;
            word.DueDateTicks = now.AddMinutes(LearningStepMinutes[stepIdx + 1]).Ticks;
            word.IntervalDays = 0;
        }
        else
        {
            // 学完所有步长，进入 Review
            word.SrsState = SrsState.Review;
            word.LearningStepIndex = 0;
            word.Repetitions = 1;
            word.IntervalDays = 1;
            word.DueDateTicks = now.AddDays(1).Ticks;
        }

        // 轻微提升 EF
        word.EaseFactor = Math.Max(MinEaseFactor, word.EaseFactor + 0.1f);
    }

    /// <summary>Review 状态下答对：更新 EF 并按公式计算新间隔。</summary>
    private static void HandleReviewPass(WordEntry word, SrsGrade grade, DateTime now)
    {
        word.Repetitions++;

        // SM-2 EF 更新公式
        var gradeDelta = 3 - (int)grade; // Good=1, Hard=2, Easy=0（注意：公式用反向距离）
        // 实际上 SM-2 公式：EF' = EF + (0.1 - (3-q)*(0.08 + (3-q)*0.02))
        // 其中 q = grade (1=Hard, 2=Good 修正: SM-2 原始用 0-5，这里映射为 1-3)
        // 使用 q = grade 数值（Hard=1, Good=2, Easy=3）
        var q = (int)grade;
        word.EaseFactor = Math.Max(MinEaseFactor,
            word.EaseFactor + (0.1f - (3 - q) * (0.08f + (3 - q) * 0.02f)));

        // 计算新间隔
        if (word.Repetitions == 1)
            word.IntervalDays = 1;
        else if (word.Repetitions == 2)
            word.IntervalDays = 6;
        else
            word.IntervalDays = (int)Math.Round(word.IntervalDays * word.EaseFactor);

        word.DueDateTicks = now.AddDays(word.IntervalDays).Ticks;
    }

    /// <summary>
    /// 处理断签堆积：将过期卡片平滑分摊到未来几天。
    /// 调用时机：每次 GenerateQuiz 前检查所有到期卡片。
    /// </summary>
    public static void RescheduleOverdue(List<WordEntry> overdueWords, DateTime nowUtc, int spreadDays = 3)
    {
        if (overdueWords.Count == 0) return;

        // Again 词优先排在今天，其余按 overdue 天数权重分摊
        var today = nowUtc.Date;
        var rng = new Random();

        foreach (var word in overdueWords)
        {
            if (word.SrsState == SrsState.Relearning)
            {
                // Relearning 词紧急：排到今天
                word.DueDateTicks = today.AddHours(rng.Next(0, 24)).Ticks;
            }
            else
            {
                // Review 词分摊到今天 ~ spreadDays 天后
                var offsetDays = rng.Next(0, spreadDays + 1);
                word.DueDateTicks = today.AddDays(offsetDays).AddHours(rng.Next(0, 24)).Ticks;
            }
        }
    }

    /// <summary>
    /// 获取卡片调度优先级的排序键（数字越小越优先）。
    /// Again=0, Due=1, New=2。
    /// </summary>
    public static int GetPriority(WordEntry word, DateTime nowUtc)
    {
        return word.SrsState switch
        {
            SrsState.Relearning => 0,                                   // 最紧急
            SrsState.Learning when word.IsDue => 0,                     // 学习中到期
            SrsState.Review when word.IsDue => 1,                       // 复习到期
            SrsState.Learning => 2,                                     // 学习中未到期
            _ => 3                                                      // New / Review 未到期
        };
    }

    /// <summary>
    /// 判断一张卡片今天是否应该被调度。
    /// </summary>
    public static bool IsDueToday(WordEntry word, DateTime nowUtc)
    {
        if (word.SrsState == SrsState.New) return true; // 新词随时可学
        if (word.DueDateTicks == 0) return true;
        return new DateTime(word.DueDateTicks, DateTimeKind.Utc).Date <= nowUtc.Date;
    }
}
