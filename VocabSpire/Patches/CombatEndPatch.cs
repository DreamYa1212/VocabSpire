using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using VocabSpire.Services;
using VocabSpire.UI;

namespace VocabSpire.Patches;

/// <summary>
/// 战斗事件处理 —— 通过 CombatManager 事件订阅（非 Harmony 补丁）。
/// 战斗开始时清空本次战斗错题，战斗结束时显示错题总结。
/// </summary>
public static class CombatEndHandler
{
    public static void Subscribe()
    {
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        Log.Info("[VocabSpire] Combat event handlers subscribed.");
    }

    private static void OnCombatSetUp(CombatState _)
    {
        WrongAnswerTracker.Instance.ClearCombatAnswers();

        VocabManager.Instance.InitCombatFixedWordPool();

        // 战斗池预览
        if (VocabConfig.Instance.ShowPoolPreview)
        {
            var combatPool = VocabManager.Instance.GetCombatFixedWordPool();
            if (combatPool is { Count: > 0 })
            {
                var panel = QuizPanel.Instance;
                if (panel is not null)
                {
                    panel.PendingPoolPreviewTitle = $"本场战斗词池 ({combatPool.Count} 词)";
                    panel.PendingPoolPreviewWords = combatPool;
                    panel.CallDeferred(nameof(QuizPanel.ShowPendingPoolPreview));
                }
            }
        }

        Log.Info("[VocabSpire] Combat word pool initialized (if enabled).");
    }

    private static void OnCombatEnded(CombatRoom _)
    {
        if (!VocabConfig.Instance.Enabled) return;
        if (!VocabConfig.Instance.ShowCombatSummary) return;

        var records = WrongAnswerTracker.Instance.FlushCombatAnswers();
        if (records.Count == 0) return;

        var panel = WrongAnswerSummaryPanel.Instance;
        if (panel is null) return;

        Log.Info($"[VocabSpire] Showing combat summary: {records.Count} wrong answers.");
        panel.ShowSummary(records);
    }
}
