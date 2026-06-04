using System.Text.Json;
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

    // ── 本局固定词池（RunFixedWordCount > 0）──
    private List<WordEntry>? _runFixedWordPool;
    private bool _runFixedPoolInitialized;

    // ── 本场战斗固定词池（CombatFixedWordCount > 0）──
    private List<WordEntry>? _combatFixedWordPool;

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
    }

    public void SetActiveBank(string bankId)
    {
        _activeBank = _banks.FirstOrDefault(b => b.Id == bankId);
        if (_activeBank is not null)
        {
            VocabConfig.Instance.ActiveBankId = bankId;
            VocabConfig.Instance.Save();
            Log.Info($"[VocabSpire] Active bank: {_activeBank.Name}");
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
            _banks[existingIdx] = bank;
        }
        else
        {
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

        // ── 词池优先级：本场战斗固定池 > 本局固定池 > 完整词库 ──
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

        var runPool = GetRunFixedWordPool();
        if (runPool is { Count: >= 2 })
        {
            // 拼写复习模式：在本局固定池内过滤
            if (cfg.SpellingReviewOnly && tier >= 2 && modes.HasFlag(QuizModeFlags.SpellEnglish))
            {
                var reviewPool = runPool
                    .Where(w => _testedWordsThisRun.Contains(w.English.ToLowerInvariant()))
                    .ToList();
                if (reviewPool.Count >= 4)
                    return _quizGenerator.Generate(_activeBank, reviewPool, modes, cfg.OptionCount, tier);
            }
            return _quizGenerator.Generate(_activeBank, runPool, modes, cfg.OptionCount, tier);
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
    /// 同时处理断签堆积，每日新词限流。
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
        var words = _activeBank.Words;

        // ── 优先级队列 ──
        // 1. Relearning / Learning Due（最紧急：Again 词、学习中到期词）
        var relearningPool = words
            .Where(w => w.SrsState is SrsState.Relearning or SrsState.Learning && w.IsDue)
            .OrderBy(w => w.DueDateTicks)
            .ToList();

        if (relearningPool.Count >= 2)
            return _quizGenerator.Generate(_activeBank, relearningPool, modes, cfg.OptionCount, tier);

        // 2. Review Due（今天到期的复习词）
        var reviewDuePool = words
            .Where(w => w.SrsState == SrsState.Review && SrsScheduler.IsDueToday(w, now))
            .OrderBy(w => w.DueDateTicks)
            .ToList();

        if (reviewDuePool.Count >= 2)
            return _quizGenerator.Generate(_activeBank, reviewDuePool, modes, cfg.OptionCount, tier);

        // 3. New words（新词，每日限流）
        var newWords = words
            .Where(w => w.SrsState == SrsState.New)
            .ToList();

        if (newWords.Count > 0)
        {
            // 每日新词上限（默认 20）
            var maxNewPerDay = cfg.MaxNewWordsPerDay > 0 ? cfg.MaxNewWordsPerDay : 20;
            var todayNewCount = words.Count(w =>
                w.SrsState != SrsState.New && new DateTime(w.LastReviewTicks, DateTimeKind.Utc).Date == now.Date
                && w.Repetitions == 0); // Repetitions=0 表示首次学习

            if (todayNewCount < maxNewPerDay && newWords.Count >= 2)
                return _quizGenerator.Generate(_activeBank, newWords, modes, cfg.OptionCount, tier);

            // 今日新词已达上限，降级到 Review Due
            if (reviewDuePool.Count >= 2)
                return _quizGenerator.Generate(_activeBank, reviewDuePool, modes, cfg.OptionCount, tier);
        }

        // 4. 实在没有：任意未到期 Review 词（摊还）
        var anyReview = words
            .Where(w => w.SrsState == SrsState.Review)
            .OrderBy(w => w.DueDateTicks)
            .ToList();
        if (anyReview.Count >= 2)
            return _quizGenerator.Generate(_activeBank, anyReview, modes, cfg.OptionCount, tier);

        // 5. 回退：任意已有进度的词
        var anyProgress = words
            .Where(w => w.SrsState != SrsState.New)
            .OrderBy(w => w.DueDateTicks)
            .ToList();
        if (anyProgress.Count >= 2)
            return _quizGenerator.Generate(_activeBank, anyProgress, modes, cfg.OptionCount, tier);

        // 6. 最终回退：正常出题
        return _quizGenerator.Generate(_activeBank, modes, cfg.OptionCount, tier);
    }

    public void RecordAnswer(WordEntry word, bool correct)
    {
        if (correct)
        {
            word.CorrectCount++;
            word.Streak++;
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

        // 持久化单词进度
        SaveProgress();
    }

    // ── 单词进度持久化 ──

    private string ProgressFilePath
    {
        get
        {
            var modDir = Path.GetDirectoryName(typeof(VocabManager).Assembly.Location) ?? ".";
            return Path.Combine(modDir, "_word_progress.json");
        }
    }

    public void SaveProgress()
    {
        try
        {
            var data = new Dictionary<string, Dictionary<string, object>>();
            foreach (var bank in _banks)
            {
                foreach (var w in bank.Words)
                {
                    // 跳过无任何进度的词
                    if (w.CorrectCount == 0 && w.WrongCount == 0 && w.EnergyLost == 0
                        && w.SrsState == SrsState.New && w.EaseFactor == SrsScheduler.DefaultEaseFactor)
                        continue;

                    var key = w.English.ToLowerInvariant();
                    var entry = new Dictionary<string, object>
                    {
                        ["cc"] = w.CorrectCount,
                        ["wc"] = w.WrongCount,
                        ["el"] = w.EnergyLost,
                        ["st"] = w.Streak,
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
            }
            var json = System.Text.Json.JsonSerializer.Serialize(data,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProgressFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to save progress: {ex.Message}");
        }
    }

    public void LoadProgress()
    {
        try
        {
            if (!File.Exists(ProgressFilePath)) return;

            var json = File.ReadAllText(ProgressFilePath);
            // 尝试新格式 (Dictionary<string, object>)，失败则回退旧格式 (int[])
            try
            {
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, System.Text.Json.JsonElement>>>(json);
                if (data is null) return;

                foreach (var bank in _banks)
                {
                    foreach (var w in bank.Words)
                    {
                        var key = w.English.ToLowerInvariant();
                        if (!data.TryGetValue(key, out var entry)) continue;

                        w.CorrectCount = entry.TryGetValue("cc", out var cc) && cc.TryGetInt32(out var ccv) ? ccv : 0;
                        w.WrongCount = entry.TryGetValue("wc", out var wc) && wc.TryGetInt32(out var wcv) ? wcv : 0;
                        w.EnergyLost = entry.TryGetValue("el", out var el) && el.TryGetInt32(out var elv) ? elv : 0;
                        w.Streak = entry.TryGetValue("st", out var st) && st.TryGetInt32(out var stv) ? stv : 0;
                        // SM-2 fields (optional, default if missing for backward compat)
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
                }

                Log.Info($"[VocabSpire] Loaded SRS progress for {data.Count} words.");
            }
            catch (System.Text.Json.JsonException)
            {
                // 回退旧格式：int[] 兼容
                LoadProgressLegacy(json);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] Failed to load progress: {ex.Message}");
        }
    }

    /// <summary>兼容旧版 int[] 格式的进度加载。</summary>
    private void LoadProgressLegacy(string json)
    {
        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int[]>>(json);
        if (data is null) return;

        foreach (var bank in _banks)
        {
            foreach (var w in bank.Words)
            {
                var key = w.English.ToLowerInvariant();
                if (!data.TryGetValue(key, out var stats)) continue;
                w.CorrectCount = stats.Length > 0 ? stats[0] : 0;
                w.WrongCount = stats.Length > 1 ? stats[1] : 0;
                w.EnergyLost = stats.Length > 2 ? stats[2] : 0;
                w.Streak = stats.Length > 3 ? stats[3] : 0;
                // 旧格式无 SM-2 数据，保持默认值
            }
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
                RunQuizTracker.Instance.Reset(); // 新局开始，重置追踪
                _runFixedPoolInitialized = false;
                _runFixedWordPool = null;
                _combatFixedWordPool = null;
            }
            _wasInRun = inRun;
        }
        catch
        {
            // RunManager 不可用时忽略
        }
    }

    /// <summary>
    /// 获取（或初始化）本局固定词池。仅在 RunFixedWordCount > 0 时有效。
    /// </summary>
    public List<WordEntry>? GetRunFixedWordPool()
    {
        if (_activeBank is null) return null;
        var cfg = VocabConfig.Instance;
        if (cfg.RunFixedWordCount <= 0) return null;
        if (_activeBank.Words.Count <= cfg.RunFixedWordCount) return null;

        if (!_runFixedPoolInitialized)
        {
            var rng = new Random(
                (RunManager.Instance.DebugOnlyGetState()?.Rng?.StringSeed ?? Guid.NewGuid().ToString()).GetHashCode());
            _runFixedWordPool = _activeBank.Words
                .OrderBy(_ => rng.Next())
                .Take(cfg.RunFixedWordCount)
                .ToList();
            _runFixedPoolInitialized = true;
            Log.Info($"[VocabSpire] RunFixedWordPool initialized: {_runFixedWordPool.Count} words (from seed).");
        }

        return _runFixedWordPool;
    }

    /// <summary>
    /// 初始化本场战斗固定词池。
    /// 如果 RunFixedWordCount > 0：从本局固定池子集选取，CombatFixedWordCount 会被 clamp 到不超过本局池大小。
    /// 否则：直接从完整词库选取。
    /// 在战斗开始时调用。
    /// </summary>
    public void InitCombatFixedWordPool()
    {
        _combatFixedWordPool = null;
        if (_activeBank is null) return;
        var cfg = VocabConfig.Instance;
        if (cfg.CombatFixedWordCount <= 0) return;

        var runPool = GetRunFixedWordPool();
        if (runPool is not null)
        {
            // 两者都设置了：本场战斗数量不能超过本局固定词池大小
            var effectiveCount = Math.Min(cfg.CombatFixedWordCount, runPool.Count);
            if (effectiveCount < 2) return;

            var rng = new Random(Guid.NewGuid().GetHashCode());
            _combatFixedWordPool = runPool
                .OrderBy(_ => rng.Next())
                .Take(effectiveCount)
                .ToList();
            Log.Info($"[VocabSpire] CombatFixedWordPool initialized: {_combatFixedWordPool.Count} words (from run pool of {runPool.Count}).");
        }
        else
        {
            // 仅设置了本场战斗：从完整词库选取，不受限制
            if (_activeBank.Words.Count <= cfg.CombatFixedWordCount) return;

            var rng = new Random(Guid.NewGuid().GetHashCode());
            _combatFixedWordPool = _activeBank.Words
                .OrderBy(_ => rng.Next())
                .Take(cfg.CombatFixedWordCount)
                .ToList();
            Log.Info($"[VocabSpire] CombatFixedWordPool initialized: {_combatFixedWordPool.Count} words (from full bank).");
        }
    }

    /// <summary>
    /// 强制重新初始化本局固定词池（玩家手动重掷）。
    /// </summary>
    public void RerollRunFixedWordPool()
    {
        _runFixedPoolInitialized = false;
        _runFixedWordPool = null;
        GetRunFixedWordPool();
    }

    /// <summary>
    /// 强制重新初始化本场战斗固定词池（玩家手动重掷），沿用上一次 InitCombatFixedWordPool 的逻辑。
    /// </summary>
    public void RerollCombatFixedWordPool()
    {
        InitCombatFixedWordPool();
    }

    /// <summary>
    /// 获取本场战斗固定词池（如果有的话）。
    /// </summary>
    public List<WordEntry>? GetCombatFixedWordPool() => _combatFixedWordPool;

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
}
