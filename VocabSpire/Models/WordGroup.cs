namespace VocabSpire.Models;

/// <summary>
/// 词包分组：将词库按固定大小切片，每组独立追踪进度。
/// </summary>
public sealed class WordGroup
{
    /// <summary>组号（0-based）。G1 = 0。</summary>
    public int Index { get; init; }

    /// <summary>组内第一个词在词库中的起始索引。</summary>
    public int StartIndex { get; init; }

    /// <summary>组内最后一个词在词库中的结束索引（不含）。</summary>
    public int EndIndex { get; init; }

    /// <summary>组内单词数。</summary>
    public int Count => EndIndex - StartIndex;

    /// <summary>显示名称：G1, G2, ...</summary>
    public string Label => $"G{Index + 1}";

    /// <summary>预览文本：显示范围。</summary>
    public string RangeText => $"第 {StartIndex + 1}-{EndIndex} 词";

    /// <summary>组内正确率——使用 Streak 标准，与图鉴一致。已掌握词数 / 总词数。</summary>
    public float AccuracyPercent => Count > 0 ? (float)MasteredCount / Count * 100f : 0f;

    /// <summary>组内累计正确数。</summary>
    public int CorrectCount { get; set; }

    /// <summary>组内累计错误数。</summary>
    public int WrongCount { get; set; }

    /// <summary>是否已被用户标记为完成。</summary>
    public bool Completed { get; set; }

    /// <summary>组内达到已掌握标准（Streak >= MasteryStreak）的词数。</summary>
    public int MasteredCount { get; set; }

    /// <summary>组内正在学习中（有记录但未掌握）的词数。</summary>
    public int LearningCount { get; set; }

    /// <summary>组内未学习过的词数。</summary>
    public int LockedCount { get; set; }
}
