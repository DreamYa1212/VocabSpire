using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using VocabSpire.Models;

namespace VocabSpire.Services;

public sealed class VocabManager
{
    public static VocabManager Instance { get; } = new();

    private readonly List<WordBank> _banks = new();
    private readonly QuizGenerator _quizGenerator = new();
    private WordBank? _activeBank;

    // ── 本局已测试词追踪（用于拼写复习模式）──
    private readonly HashSet<string> _testedWordsThisRun = new();
    private bool _wasInRun;

    // ── 本场战斗固定词池（CombatFixedWordCount > 0）──
    private List<WordEntry>? _combatFixedWordPool;

    // ── 分组记忆（GroupSize > 0）──
    private List<WordGroup> _wordGroups = new();
    private int _activeGroupIndex = -1;
    private List<WordEntry>? _activeGroupWordPool;
    private int[] _shuffledIndices = Array.Empty<int>();

    public IReadOnlyList<WordBank> Banks => _banks.AsReadOnly();
    public WordBank? ActiveBank => _activeBank;
    public bool HasActiveBank => _activeBank is { IsValid: true };

    private VocabManager() { }

    public string GetWordBanksDirectory()
    {
        var modDir = Path.GetDirectoryName(typeof(VocabManager).Assembly.Location) ?? ".";
        var wordbanksDir = Path.Combine(modDir, "wordbanks");

        if (!Directory.Exists(wordbanksDir))
        {
            Directory.CreateDirectory(wordbanksDir);
        }

        return wordbanksDir;
    }

    private static void AssignBankIdToWords(WordBank bank)
    {
        foreach (var w in bank.Words)
            w.BankId = bank.Id;
    }

    public void LoadAllBanks()
    {
        _banks.Clear();
        var dir = GetWordBanksDirectory();

        Log.Info($"[VocabSpire] Loading word banks from: {dir}");

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            if (Path.GetFileName(file).StartsWith("_")) continue;
            var bank = FileParser.ParseJson(file);
            if (bank is not null)
            {
                AssignBankIdToWords(bank);
                _banks.Add(bank);
                Log.Info($"[VocabSpire] Loaded: {bank.Name} ({bank.TotalWords} words)");
            }
        }

        foreach (var file in Directory.GetFiles(dir, "*.csv"))
        {
            if (Path.GetFileName(file).StartsWith("_")) continue;
            var bank = FileParser.ParseCsv(file);
            if (bank is not null)
            {
                AssignBankIdToWords(bank);
                _banks.Add(bank);
                Log.Info($"[VocabSpire] Loaded: {bank.Name} ({bank.TotalWords} words)");
            }
        }

        Log.Info($"[VocabSpire] Total word banks: {_banks.Count}");

        var activeBankId = VocabConfig.Instance.ActiveBankId;
        if (!string.IsNullOrEmpty(activeBankId))
        {
            SetActiveBank(activeBankId);
        }
        else if (_banks.Count > 0)
        {
            SetActiveBank(_banks[0].Id);
        }

        // 加载持久化的单词进度
        LoadProgress();

        // 初始化分组
        RegenerateGroups();
        LoadGroupProgress();

        // 恢复上次选中的分组
        var savedGroupIndex = VocabConfig.Instance.ActiveGroupIndex;
        if (savedGroupIndex >= 0 && savedGroupIndex < _wordGroups.Count)
            SelectGroup(savedGroupIndex);
    }

    public void SetActiveBank(string bankId)
    {
        _activeBank = _banks.FirstOrDefault(b => b.Id == bankId);
        if (_activeBank is not null)
        {
            VocabConfig.Instance.ActiveBankId = bankId;
            VocabConfig.Instance.Save();
            Log.Info($"[VocabSpire] Active bank: {_activeBank.Name}");

            // 重新初始化分组（基于新词库）
            RegenerateGroups();
            var savedGroupIndex = VocabConfig.Instance.ActiveGroupIndex;
            if (savedGroupIndex >= 0 && savedGroupIndex < _wordGroups.Count)
                SelectGroup(savedGroupIndex);

            // 如果在战斗中，重新初始化战斗词池（基于新词库）
            try
            {
                if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress)
                {
                    InitCombatFixedWordPool();
                    Log.Info($"[VocabSpire] Combat pool re-initialized for new bank: {_activeBank.Name}");
                }
            }
            catch
            {
                // CombatManager 不可用时忽略
            }
        }
    }

    public WordBank? ImportBank(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // apkg 特殊处理：解析为词条后序列化成 json 存入 wordbanks
        // （apkg 是二进制，LoadAllBanks 只扫描 json/csv，必须先转 json）
        if (ext == ".apkg")
            return ImportApkg(filePath);

        var bank = ext switch
        {
            ".json" => FileParser.ParseJson(filePath),
            ".csv" => FileParser.ParseCsv(filePath),
            _ => null
        };

        if (bank is null)
        {
            Log.Error($"[VocabSpire] Failed to import: {filePath}");
            return null;
        }

        var destPath = Path.Combine(GetWordBanksDirectory(), Path.GetFileName(filePath));
        if (!string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(filePath, destPath, overwrite: true);
        }

        var existingIdx = _banks.FindIndex(b => b.Id == bank.Id);
        if (existingIdx >= 0)
        {
            AssignBankIdToWords(bank);
            _banks[existingIdx] = bank;
        }
        else
        {
            AssignBankIdToWords(bank);
            _banks.Add(bank);
        }

        Log.Info($"[VocabSpire] Imported: {bank.Name} ({bank.TotalWords} words)");
        return bank;
    }

    /// <summary>
    /// 导入 Anki .apkg 词库：用纯托管 reader 解析后序列化成 VocabSpire json 存入 wordbanks 目录，
    /// 再按普通 json 加载（保证与其它词库行为一致，且下次启动可直接扫描到）。
    /// </summary>
    private WordBank? ImportApkg(string apkgPath)
    {
        try
        {
            var parsed = ApkgImporter.Import(apkgPath);

            // 多义项写成数组，单义项写成字符串，与现有词库 json 格式一致
            var dto = new
            {
                name = parsed.Name,
                description = parsed.Description,
                words = parsed.Words.Select(w => new
                {
                    english = w.English,
                    chinese = w.Definitions.Count > 1
                        ? (object)w.Definitions
                        : (w.Definitions.Count == 1 ? w.Definitions[0] : w.Chinese),
                    phonetic = w.Phonetic
                })
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            var jsonPath = Path.Combine(GetWordBanksDirectory(), parsed.Id + ".json");
            File.WriteAllText(jsonPath, json);

            var bank = FileParser.ParseJson(jsonPath) ?? parsed;
            var idx = _banks.FindIndex(b => b.Id == bank.Id);
            if (idx >= 0) _banks[idx] = bank; else _banks.Add(bank);

            Log.Info($"[VocabSpire] Imported apkg: {bank.Name} ({bank.TotalWords} words) -> {Path.GetFileName(jsonPath)}");
            return bank;
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] apkg import failed: {apkgPath} - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 导出词库模板文件到 wordbanks 目录。
    /// </summary>
    public string ExportTemplate()
    {
        var template = new
        {
            name = "我的词库",
            description = "在此填写词库描述",
            words = new object[]
            {
                new { english = "apple", chinese = "n. 苹果", phonetic = "/ˈæp.əl/" },
                new { english = "run", chinese = new[] { "v. 跑步", "vi. 运转", "n. 竞赛" }, phonetic = "/rʌn/" },
                new { english = "book", chinese = "n. 书; v. 预订", phonetic = "/bʊk/" }
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(template, options);

        var path = Path.Combine(GetWordBanksDirectory(), "_TEMPLATE.json");
        File.WriteAllText(path, json);
        Log.Info($"[VocabSpire] Template exported to: {path}");
        return path;
    }

    public QuizQuestion? GenerateQuiz()
    {
        if (_activeBank is null || !_activeBank.IsValid) return null;

        // 检测新局开始，清空已测试词记录
        DetectRunBoundary();

        // SRS 模式：使用 SM-2 优先级出题
        if (VocabConfig.Instance.EnableSrsMode)
            return GenerateQuizSrs();

        var tier = VocabConfig.Instance.EnableDifficultyScaling
            ? Math.Clamp(GameBridge.GetCurrentAct(), 1, 3)
            : 1;

        var cfg = VocabConfig.Instance;
        var modes = cfg.GetModesForAct(tier);

        // ── 词池优先级：分组池 > 本场战斗固定池 > 本局固定池 > 完整词库 ──
        if (_activeGroupWordPool is { Count: >= 2 })
            return _quizGenerator.Generate(_activeBank, _activeGroupWordPool, modes, cfg.OptionCount, tier);

        var combatPool = GetCombatFixedWordPool();
        if (combatPool is { Count: >= 2 })
        {
            // 战斗固定池直接使用（不叠加拼写复习/可选模式，因为池子已经足够小）
            // 但如果开启了拼写复习，仍在战斗池内过滤
            if (cfg.SpellingReviewOnly && tier >= 2 && modes.HasFlag(QuizModeFlags.SpellEnglish))
            {
                var reviewPool = combatPool
                    .Where(w => _testedWordsThisRun.Contains(w.English.ToLowerInvariant()))
                    .ToList();
                if (reviewPool.Count >= 4)
                    return _quizGenerator.Generate(_activeBank, reviewPool, modes, cfg.OptionCount, tier);
            }
            return _quizGenerator.Generate(_activeBank, combatPool, modes, cfg.OptionCount, tier);
        }

        // 拼写复习模式：Act2+ 且开启了"仅复习已测词"
        if (cfg.SpellingReviewOnly && tier >= 2 && modes.HasFlag(QuizModeFlags.SpellEnglish))
        {
            var reviewPool = GetReviewWordPool();
            if (reviewPool.Count >= 4)
            {
                return _quizGenerator.Generate(
                    _activeBank, reviewPool, modes,
                    cfg.OptionCount, tier);
            }
        }

        return _quizGenerator.Generate(
            _activeBank, modes, cfg.OptionCount, tier);
    }

    /// <summary>
    /// SRS 模式出题：按 SM-2 优先级（Relearning > Due Review > New）选词。
    /// 尊重固定词池约束（战斗池 > 本局池 > 全词库），仅在有效词池内做 SRS 调度。
    /// </summary>
    public QuizQuestion? GenerateQuizSrs()
    {
        if (_activeBank is null || !_activeBank.IsValid) return null;

        DetectRunBoundary();

        var cfg = VocabConfig.Instance;
        var tier = cfg.EnableDifficultyScaling
            ? Math.Clamp(GameBridge.GetCurrentAct(), 1, 3)
            : 1;
        var modes = cfg.GetModesForAct(tier);
        var now = DateTime.UtcNow;

        // ── 确定有效词池：分组池 > 战斗池 > 全词库 ──
        List<WordEntry>? effectivePool = null;

        if (_activeGroupWordPool is { Count: >= 2 })
            effectivePool = _activeGroupWordPool;
        else
        {
            var combatPool = GetCombatFixedWordPool();
            if (combatPool is { Count: >= 2 })
                effectivePool = combatPool;
        }

        // 拼写复习模式的过滤在 SRS 调度之后处理（优先级更高）
        var words = effectivePool ?? _activeBank.Words;

        // 排除已掌握的词（Mastered 状态不参与日常出题）
        var activeWords = words.Where(w => w.SrsState != SrsState.Mastered).ToList();
        if (activeWords.Count < 2)
            activeWords = words.ToList(); // 全是 Mastered 时回退

        // ── 优先级队列（仅在有效词池内）──
        // 1. Relearning / Learning Due（最紧急：Again 词、学习中到期词）
        var relearningPool = activeWords
            .Where(w => w.SrsState is SrsState.Relearning or SrsState.Learning && w.IsDue)
            .OrderBy(w => w.DueDateTicks)
            .ToList();

        if (relearningPool.Count >= 2)
            return GenerateFromPool(relearningPool, modes, cfg.OptionCount, tier, modes);

        // 2. Review Due（今天到期的复习词）
        var reviewDuePool = activeWords
            .Where(w => w.SrsState == SrsState.Review && SrsScheduler.IsDueToday(w, now))
            .OrderBy(w => w.DueDateTicks)
            .ToList();

        if (reviewDuePool.Count >= 2)
            return GenerateFromPool(reviewDuePool, modes, cfg.OptionCount, tier, modes);

        // 3. New words（新词，每日限流；固定词池模式下不限制新词上限）
        var newWords = activeWords
            .Where(w => w.SrsState == SrsState.New)
            .ToList();

        if (newWords.Count > 0)
        {
            if (effectivePool is not null)
            {
                // 固定词池模式：新词照出，不受每日上限约束（池子本身就够小）
                if (newWords.Count >= 2)
                    return GenerateFromPool(newWords, modes, cfg.OptionCount, tier, modes);
            }
            else
            {
                var maxNewPerDay = cfg.MaxNewWordsPerDay > 0 ? cfg.MaxNewWordsPerDay : 20;
                var todayNewCount = activeWords.Count(w =>
                    w.SrsState != SrsState.New && new DateTime(w.LastReviewTicks, DateTimeKind.Utc).Date == now.Date
                    && w.Repetitions == 0);

                if (todayNewCount < maxNewPerDay && newWords.Count >= 2)
                    return GenerateFromPool(newWords, modes, cfg.OptionCount, tier, modes);
            }

            if (reviewDuePool.Count >= 2)
                return GenerateFromPool(reviewDuePool, modes, cfg.OptionCount, tier, modes);
        }

        // 4. 实在没有：任意未到期 Review 词（摊还）
        var anyReview = activeWords
            .Where(w => w.SrsState == SrsState.Review)
            .OrderBy(w => w.DueDateTicks)
            .ToList();
        if (anyReview.Count >= 2)
            return GenerateFromPool(anyReview, modes, cfg.OptionCount, tier, modes);

        // 5. 回退：任意已有进度的词（非 New 非 Mastered）
        var anyProgress = activeWords
            .Where(w => w.SrsState != SrsState.New)
            .OrderBy(w => w.DueDateTicks)
            .ToList();
        if (anyProgress.Count >= 2)
            return GenerateFromPool(anyProgress, modes, cfg.OptionCount, tier, modes);

        // 6. 最终回退：正常出题（使用有效词池或完整词库）
        return GenerateFromPool(activeWords, modes, cfg.OptionCount, tier, modes);
    }

    /// <summary>
    /// 从指定词池生成题目。如果开启拼写复习模式且符合条件，在池内过滤已测词。
    /// </summary>
    private QuizQuestion? GenerateFromPool(List<WordEntry> pool, QuizModeFlags modes, int optionCount, int tier, QuizModeFlags globalModes)
    {
        var cfg = VocabConfig.Instance;
        if (_activeBank is null) return null;

        if (cfg.SpellingReviewOnly && tier >= 2 && globalModes.HasFlag(QuizModeFlags.SpellEnglish))
        {
            var reviewPool = pool
                .Where(w => _testedWordsThisRun.Contains(w.English.ToLowerInvariant()))
                .ToList();
            if (reviewPool.Count >= 4)
                return _quizGenerator.Generate(_activeBank, reviewPool, modes, optionCount, tier);
        }

        return _quizGenerator.Generate(_activeBank, pool, modes, optionCount, tier);
    }

    private string? _lastAnsweredWord;

    public void RecordAnswer(WordEntry word, bool correct)
    {
        Log.Info($"[VocabSpire] RecordAnswer: word='{word.English}' correct={correct} " +
                 $"cc_before={word.CorrectCount} bs_before={word.BestStreak} bankId={word.BankId}");
        _lastAnsweredWord = word.English.ToLowerInvariant();
        if (correct)
        {
            word.CorrectCount++;
            word.Streak++;
            if (word.Streak > word.BestStreak)
                word.BestStreak = word.Streak;
        }
        else
        {
            word.WrongCount++;
            word.Streak = 0; // 答错归零
        }

        _testedWordsThisRun.Add(word.English.ToLowerInvariant());

        VocabConfig.Instance.TotalAnswered++;
        if (correct) VocabConfig.Instance.TotalCorrect++;
        VocabConfig.Instance.Save();

        // 持久化单词进度（根据 WordEntry.BankId 定位正确的词库文件）
        var targetBank = _banks.FirstOrDefault(b => b.Id == word.BankId);
        if (targetBank is not null)
            SaveProgress(targetBank);
        else
            Log.Warn($"[VocabSpire] RecordAnswer: no bank found for BankId='{word.BankId}', skipping save.");

        // 分组达标检测
        CheckGroupMastered();

        // SRS 固定词池耗尽检测
        CheckPoolExhausted();
    }

    /// <summary>
    /// 检测固定词池是否耗尽。
    /// 战斗池耗尽 → 弹窗问从本局池还是全词库补；本局池耗尽 → 弹窗问是否重掷。
    /// </summary>
    private void CheckPoolExhausted()
    {
        if (!VocabConfig.Instance.EnableSrsMode) return;
        if (!VocabConfig.Instance.EnablePoolExhaustedPrompt) return;

        var now = DateTime.UtcNow;

        // 先检查战斗池（优先级高）
        var combatPool = GetCombatFixedWordPool();
        if (combatPool is { Count: >= 4 } && IsPoolExhausted(combatPool, now))
        {
            if (_poolExhaustedPromptShown) return;
            _poolExhaustedPromptShown = true;

            var correct = combatPool.Sum(w => w.CorrectCount);
            var total = combatPool.Sum(w => w.CorrectCount + w.WrongCount);
            var accuracy = total > 0 ? (float)correct / total * 100f : 0f;

            var hasRunPool = false;
            var panel = UI.QuizPanel.Instance;
            panel?.CallDeferred(nameof(UI.QuizPanel.ShowCombatPoolExhaustedPrompt),
                accuracy, combatPool.Count, hasRunPool);
            return;
        }
    }

    private static bool IsPoolExhausted(List<WordEntry> pool, DateTime now)
    {
        if (pool.Count < 2) return false;
        if (pool.Any(w => w.SrsState == SrsState.New)) return false;
        if (pool.Any(w => w.SrsState == SrsState.Relearning)) return false;
        if (pool.Any(w => w.SrsState == SrsState.Review && SrsScheduler.IsDueToday(w, now))) return false;

        var total = pool.Sum(w => w.CorrectCount + w.WrongCount);
        if (total == 0) return false;
        var correct = pool.Sum(w => w.CorrectCount);
        var accuracy = (float)correct / total * 100f;
        return accuracy >= VocabConfig.Instance.PoolExhaustedAccuracyThreshold;
    }

    private bool _poolExhaustedPromptShown;

    // ── 单词进度持久化 ──

    private string GetProgressFilePath(WordBank bank)
    {
        var modDir = Path.GetDirectoryName(typeof(VocabManager).Assembly.Location) ?? ".";
        return Path.Combine(modDir, $"_word_progress_{bank.Id}.json");
    }

    public void SaveProgress(WordBank bank)
    {
        try
        {
            var path = GetProgressFilePath(bank);
            Log.Info($"[VocabSpire] SaveProgress -> {path}");

            var data = new Dictionary<string, Dictionary<string, object>>();
            foreach (var w in bank.Words)
            {
                // 跳过无任何进度的词
                if (w.CorrectCount == 0 && w.WrongCount == 0 && w.EnergyLost == 0
                    && w.BestStreak == 0
                    && w.SrsState == SrsState.New && w.EaseFactor == SrsScheduler.DefaultEaseFactor)
                    continue;

                var key = w.English.ToLowerInvariant();
                var entry = new Dictionary<string, object>
                {
                    ["cc"] = w.CorrectCount,
                    ["wc"] = w.WrongCount,
                    ["el"] = w.EnergyLost,
                    ["st"] = w.Streak,
                    ["bs"] = w.BestStreak,
                    // SM-2 fields
                    ["srs"] = (int)w.SrsState,
                    ["ef"] = w.EaseFactor,
                    ["iv"] = w.IntervalDays,
                    ["rp"] = w.Repetitions,
                    ["dd"] = w.DueDateTicks,
                    ["lr"] = w.LastReviewTicks,
                    ["ls"] = w.LearningStepIndex
                };
                data[key] = entry;
            }
            var json = System.Text.Json.JsonSerializer.Serialize(data,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            // 验证：检查刚答过的词在文件中的实际内容
            try
            {
                var checkWord = _lastAnsweredWord;
                if (!string.IsNullOrEmpty(checkWord))
                {
                    var verifyJson = File.ReadAllText(path);
                    var search = "\"" + checkWord + "\"";
                    var idx = verifyJson.IndexOf(search);
                    if (idx >= 0)
                    {
                        var snippet = verifyJson.Substring(idx, Math.Min(150, (int)(verifyJson.Length - idx)));
                        Log.Info($"[VocabSpire] SaveProgress verify: {checkWord} in file -> {snippet.Replace("\n", "\\n")}");
                    }
                    else
                    {
                        Log.Warn($"[VocabSpire] SaveProgress verify: '{checkWord}' NOT FOUND in saved file!");
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to save progress: {ex.Message}");
        }
    }

    public void LoadProgress()
    {
        // 加载所有词库的进度，确保切词库时进度不丢失
        foreach (var bank in _banks)
        {
            try
            {
                var path = GetProgressFilePath(bank);
                if (!File.Exists(path)) continue;

                var json = File.ReadAllText(path);
                // 尝试新格式 (Dictionary<string, object>)，失败则回退旧格式 (int[])
                try
                {
                    var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, System.Text.Json.JsonElement>>>(json);
                    if (data is null) continue;

                    foreach (var w in bank.Words)
                    {
                        var key = w.English.ToLowerInvariant();
                        if (!data.TryGetValue(key, out var entry)) continue;

                        w.CorrectCount = entry.TryGetValue("cc", out var cc) && cc.TryGetInt32(out var ccv) ? ccv : 0;
                        w.WrongCount = entry.TryGetValue("wc", out var wc) && wc.TryGetInt32(out var wcv) ? wcv : 0;
                        w.EnergyLost = entry.TryGetValue("el", out var el) && el.TryGetInt32(out var elv) ? elv : 0;
                        w.Streak = entry.TryGetValue("st", out var st) && st.TryGetInt32(out var stv) ? stv : 0;
                        w.BestStreak = entry.TryGetValue("bs", out var bs) && bs.TryGetInt32(out var bsv) ? bsv : w.Streak;
                        w.SrsState = entry.TryGetValue("srs", out var srs) && srs.TryGetInt32(out var srsv)
                            ? (SrsState)srsv : SrsState.New;
                        w.EaseFactor = entry.TryGetValue("ef", out var ef) && ef.TryGetSingle(out var efv)
                            ? efv : SrsScheduler.DefaultEaseFactor;
                        w.IntervalDays = entry.TryGetValue("iv", out var iv) && iv.TryGetInt32(out var ivv) ? ivv : 0;
                        w.Repetitions = entry.TryGetValue("rp", out var rp) && rp.TryGetInt32(out var rpv) ? rpv : 0;
                        w.DueDateTicks = entry.TryGetValue("dd", out var dd) && dd.TryGetInt64(out var ddv) ? ddv : 0;
                        w.LastReviewTicks = entry.TryGetValue("lr", out var lr) && lr.TryGetInt64(out var lrv) ? lrv : 0;
                        w.LearningStepIndex = entry.TryGetValue("ls", out var ls) && ls.TryGetInt32(out var lsv) ? lsv : 0;
                    }

                    Log.Info($"[VocabSpire] Loaded progress for '{bank.Name}' ({data.Count} words).");
                }
                catch (System.Text.Json.JsonException)
                {
                    LoadProgressLegacy(json, bank);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[VocabSpire] Failed to load progress for '{bank.Name}': {ex.Message}");
            }
        }
    }

    /// <summary>兼容旧版 int[] 格式的进度加载。</summary>
    private void LoadProgressLegacy(string json, WordBank bank)
    {
        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int[]>>(json);
        if (data is null) return;

        foreach (var w in bank.Words)
        {
            var key = w.English.ToLowerInvariant();
            if (!data.TryGetValue(key, out var stats)) continue;
            w.CorrectCount = stats.Length > 0 ? stats[0] : 0;
            w.WrongCount = stats.Length > 1 ? stats[1] : 0;
            w.EnergyLost = stats.Length > 2 ? stats[2] : 0;
            w.Streak = stats.Length > 3 ? stats[3] : 0;
            w.BestStreak = w.Streak;
            // 旧格式无 SM-2 数据，保持默认值
        }

        Log.Info($"[VocabSpire] Loaded legacy progress for {data.Count} words.");
    }

    private void DetectRunBoundary()
    {
        try
        {
            var inRun = RunManager.Instance.IsInProgress;
            if (inRun && !_wasInRun)
            {
                _testedWordsThisRun.Clear();
                RunQuizTracker.Instance.Reset();
                _combatFixedWordPool = null;
                _poolExhaustedPromptShown = false;
                _groupMasteredPromptShown = false;

                // 分组预览（分组激活时显示当前分组信息）
                if (VocabConfig.Instance.ShowPoolPreview && _activeGroupWordPool is { Count: > 0 })
                {
                    var panel = UI.QuizPanel.Instance;
                    if (panel is not null)
                    {
                        panel.PendingPoolPreviewTitle = $"当前词包 {ActiveGroup?.Label} ({_activeGroupWordPool.Count} 词)";
                        panel.PendingPoolPreviewWords = _activeGroupWordPool;
                        panel.CallDeferred(nameof(UI.QuizPanel.ShowPendingPoolPreview));
                    }
                }
            }
            _wasInRun = inRun;
        }
        catch
        {
            // RunManager 不可用时忽略
        }
    }

    /// <summary>
    /// 初始化本场战斗固定词池。仅在分组模式开启时生效，从当前分组池中随机选取。
    /// </summary>
    public void InitCombatFixedWordPool()
    {
        _combatFixedWordPool = null;
        if (_activeBank is null) return;
        var cfg = VocabConfig.Instance;
        if (cfg.CombatFixedWordCount <= 0) return;
        // 仅在分组（词包）开启时使用固定词池
        if (_activeGroupWordPool is null) return;

        var sourcePool = _activeGroupWordPool;
        if (sourcePool.Count <= cfg.CombatFixedWordCount) return;

        var rng = new Random(Guid.NewGuid().GetHashCode());
        _combatFixedWordPool = sourcePool
            .OrderBy(_ => rng.Next())
            .Take(cfg.CombatFixedWordCount)
            .ToList();
        Log.Info($"[VocabSpire] CombatFixedWordPool initialized: {_combatFixedWordPool.Count} words.");
    }

    /// <summary>
    /// 强制重新初始化本场战斗固定词池（玩家手动重掷）。
    /// </summary>
    public void RerollCombatFixedWordPool()
    {
        InitCombatFixedWordPool();
    }

    /// <summary>
    /// 获取本场战斗固定词池（如果有的话）。
    /// </summary>
    public List<WordEntry>? GetCombatFixedWordPool() => _combatFixedWordPool;

    /// <summary>清空本场战斗固定词池，回退到本局池或全词库。</summary>
    public void ClearCombatFixedWordPool() => _combatFixedWordPool = null;

    /// <summary>
    /// 获取本局已测试过的词（用于拼写复习）。
    /// 若已测试词不足则回退到完整词库。
    /// </summary>
    private List<WordEntry> GetReviewWordPool()
    {
        if (_activeBank is null) return new();
        if (_testedWordsThisRun.Count < 4) return _activeBank.Words;

        var filtered = _activeBank.Words
            .Where(w => _testedWordsThisRun.Contains(w.English.ToLowerInvariant()))
            .ToList();

        return filtered.Count >= 4 ? filtered : _activeBank.Words;
    }

    // ── 分组记忆 ──

    /// <summary>获取分组列表（只读）。</summary>
    public IReadOnlyList<WordGroup> WordGroups => _wordGroups.AsReadOnly();

    /// <summary>当前激活的分组索引（-1 = 未激活/全部）。</summary>
    public int ActiveGroupIndex => _activeGroupIndex;

    /// <summary>当前激活的分组。</summary>
    public WordGroup? ActiveGroup => _activeGroupIndex >= 0 && _activeGroupIndex < _wordGroups.Count
        ? _wordGroups[_activeGroupIndex] : null;

    /// <summary>分组模式下当前有效的词池（即当前分组的词列表）。</summary>
    public List<WordEntry>? ActiveGroupWordPool => _activeGroupWordPool;

    /// <summary>
    /// 根据 GroupSize 重新切分词库，并同步各组的进度统计。
    /// </summary>
    public void RegenerateGroups()
    {
        _wordGroups.Clear();
        _activeGroupIndex = -1;
        _activeGroupWordPool = null;
        if (_activeBank is null) return;

        var groupSize = VocabConfig.Instance.GroupSize;
        if (groupSize <= 0) return;

        var words = _activeBank.Words;

        // 打乱索引（用户可设置种子，否则用词库名确定性哈希保证重启后不变）
        var seed = VocabConfig.Instance.GroupShuffleSeed;
        if (seed == 0) seed = DeterministicHash(_activeBank.Id);
        var shuffleRng = new Random(seed);
        _shuffledIndices = Enumerable.Range(0, words.Count).OrderBy(_ => shuffleRng.Next()).ToArray();

        var totalGroups = (int)Math.Ceiling((double)words.Count / groupSize);

        for (var g = 0; g < totalGroups; g++)
        {
            var start = g * groupSize;
            var end = Math.Min(start + groupSize, words.Count);
            var group = new WordGroup
            {
                Index = g,
                StartIndex = start,
                EndIndex = end
            };

            // 同步进度（使用 BestStreak 判断是否已掌握：一旦掌握就不会因答错降级）
            var masteryThreshold = VocabConfig.Instance.MasteryStreak;
            for (var i = start; i < end; i++)
            {
                var w = words[_shuffledIndices.Length > i ? _shuffledIndices[i] : i];
                group.CorrectCount += w.CorrectCount;
                group.WrongCount += w.WrongCount;
                if (w.BestStreak >= masteryThreshold)
                    group.MasteredCount++;
                else if (w.CorrectCount + w.WrongCount > 0)
                    group.LearningCount++;
                else
                    group.LockedCount++;
            }

            _wordGroups.Add(group);
        }

        Log.Info($"[VocabSpire] Regenerated {totalGroups} word groups (size={groupSize}, total={words.Count}).");
    }

    /// <summary>选择激活分组（-1 = 取消分组，恢复全词库）。</summary>
    public void SelectGroup(int groupIndex)
    {
        if (groupIndex < 0 || groupIndex >= _wordGroups.Count)
        {
            _activeGroupIndex = -1;
            _activeGroupWordPool = null;
            VocabConfig.Instance.ActiveGroupIndex = -1;
            VocabConfig.Instance.Save();
            Log.Info("[VocabSpire] Group mode disabled (full bank).");
            return;
        }

        _activeGroupIndex = groupIndex;
        var group = _wordGroups[groupIndex];
        _activeGroupWordPool = _shuffledIndices
            .Skip(group.StartIndex)
            .Take(group.Count)
            .Select(i => _activeBank!.Words[i])
            .ToList();

        VocabConfig.Instance.ActiveGroupIndex = groupIndex;
        VocabConfig.Instance.Save();
        Log.Info($"[VocabSpire] Active group: {group.Label} ({group.RangeText}, {group.Count} words).");

        // 如果在战斗中，重新初始化战斗词池（基于新词组）
        try
        {
            if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress)
            {
                InitCombatFixedWordPool();
                Log.Info($"[VocabSpire] Combat pool re-initialized for new group: {group.Label}");
            }
        }
        catch { }
    }

    /// <summary>刷新所有分组的进度统计，使用图鉴标准（Streak >= MasteryStreak）。</summary>
    public void RefreshGroupStats()
    {
        if (_activeBank is null) return;
        if (_shuffledIndices.Length == 0) return;
        var masteryThreshold = VocabConfig.Instance.MasteryStreak;
        foreach (var group in _wordGroups)
        {
            group.CorrectCount = 0;
            group.WrongCount = 0;
            group.MasteredCount = 0;
            group.LearningCount = 0;
            group.LockedCount = 0;
            for (var i = group.StartIndex; i < group.EndIndex; i++)
            {
                var w = _activeBank.Words[_shuffledIndices.Length > i ? _shuffledIndices[i] : i];
                group.CorrectCount += w.CorrectCount;
                group.WrongCount += w.WrongCount;
                if (w.BestStreak >= masteryThreshold)
                    group.MasteredCount++;
                else if (w.CorrectCount + w.WrongCount > 0)
                    group.LearningCount++;
                else
                    group.LockedCount++;
            }
        }
    }

    /// <summary>标记分组为已完成。</summary>
    public void MarkGroupCompleted(int groupIndex)
    {
        if (groupIndex >= 0 && groupIndex < _wordGroups.Count)
        {
            _wordGroups[groupIndex].Completed = true;
            SaveGroupProgress();
        }
    }

    /// <summary>分组进度持久化。</summary>
    public void SaveGroupProgress()
    {
        try
        {
            var modDir = Path.GetDirectoryName(typeof(VocabManager).Assembly.Location) ?? ".";
            var path = Path.Combine(modDir, "_group_progress.json");
            var data = _wordGroups.Select(g => new
            {
                index = g.Index,
                completed = g.Completed
            });
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to save group progress: {ex.Message}");
        }
    }

    public void LoadGroupProgress()
    {
        try
        {
            var modDir = Path.GetDirectoryName(typeof(VocabManager).Assembly.Location) ?? ".";
            var path = Path.Combine(modDir, "_group_progress.json");
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<List<GroupProgressEntry>>(json);
            if (data is null) return;

            foreach (var entry in data)
            {
                var group = _wordGroups.FirstOrDefault(g => g.Index == entry.Index);
                if (group is not null)
                    group.Completed = entry.Completed;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to load group progress: {ex.Message}");
        }
    }

    private sealed class GroupProgressEntry
    {
        public int Index { get; set; }
        public bool Completed { get; set; }
    }

    /// <summary>检查当前分组是否达标（与词汇图鉴标准统一：Streak >= MasteryStreak），达标时弹窗。</summary>
    private void CheckGroupMastered()
    {
        var group = ActiveGroup;
        if (group is null || group.Completed) return;
        if (_groupMasteredPromptShown) return;
        if (_activeGroupWordPool is null) return;

        var masteryThreshold = VocabConfig.Instance.MasteryStreak;

        // 条件 1：组内所有词都有学习记录（至少答过一次）
        var hasUntouched = _activeGroupWordPool.Any(w => w.CorrectCount + w.WrongCount == 0);
        if (hasUntouched) return;

        // 条件 2：已掌握词数（Streak >= MasteryStreak）比例达标
        var total = _activeGroupWordPool.Count;
        var mastered = _activeGroupWordPool.Count(w => w.Streak >= masteryThreshold);
        var masteredPct = (float)mastered / total * 100f;
        var threshold = VocabConfig.Instance.GroupMasteryThreshold;
        if (masteredPct < threshold) return;

        _groupMasteredPromptShown = true;
        UI.QuizPanel.Instance?.CallDeferred(
            nameof(UI.QuizPanel.ShowGroupMasteredPrompt),
            group.Label,
            masteredPct,
            threshold);
    }
    private bool _groupMasteredPromptShown;

    /// <summary>确定性哈希（跨进程稳定，不受 .NET string.GetHashCode 随机化影响）。</summary>
    private static int DeterministicHash(string s)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in s) hash = hash * 31 + c;
            return hash;
        }
    }
}
