# SudokuGui Readme

## 1. 项目概览

本项目是一个 C# WPF 数独程序，核心引擎位于 `Core/SudokuEngine.cs`，UI 位于 `MainWindow.xaml` 与 `MainWindow.xaml.cs`。

核心能力包括：

- 题目生成（按 Easy/Medium/Hard/Expert）
- 唯一解校验
- 候选数计算
- 人类技巧风格难度分析与评分
- 完成校验

---

## 2. 评分模型（PuzzleScore）

引擎最终会返回 `GeneratedPuzzle`，其中包含：

- `Puzzle`：9x9 题面
- `Profile`：难度配置（目标区间）
- `Score`：评分结果

`PuzzleScore` 的字段说明：

- `Clues`：题面已给数字总数
- `DistributionPenalty`：行/列/宫分布惩罚
- `LogicSteps`：逻辑技巧总步数
- `NakedSingleSteps`：裸单步数
- `HiddenSingleSteps`：隐单步数
- `LockedCandidatesSteps`：锁定候选步数
- `NakedPairSteps`：裸对步数
- `HiddenPairSteps`：隐对步数
- `XWingSteps`：X-Wing 步数
- `SwordfishSteps`：Swordfish 步数
- `GuessCount`：搜索阶段猜测次数
- `MaxSearchDepth`：搜索最大深度
- `TechniqueWeight`：技巧权重总分（按可配置表累加）
- `TechniqueVariety`：技巧种类数（含 Guess）
- `ComboTransitions`：技巧切换次数（组合复杂度）
- `SymmetryBreaks`：题面中心对称破坏数
- `TotalScore`：总评分，越低越好
- `QualityBand`：评分档位（Excellent/Good/Acceptable/Rough）

### 2.1 总分组成

`TotalScore` 由以下部分叠加：

1. 已给数字数量偏离惩罚：`RangePenalty(Clues, MinClues, MaxClues, 6)`
2. 分布惩罚：`DistributionPenalty * 4`
3. 逻辑步数偏离惩罚：`RangePenalty(LogicSteps, TargetLogicMin, TargetLogicMax, 3)`
4. 猜测次数偏离惩罚：`RangePenalty(GuessCount, TargetGuessMin, TargetGuessMax, 14)`
5. 深度偏离惩罚：`RangePenalty(MaxSearchDepth, TargetDepthMin, TargetDepthMax, 10)`
6. 技巧权重偏离惩罚：`RangePenalty(TechniqueWeight, TargetTechniqueWeightMin, TargetTechniqueWeightMax, 2)`
7. 技巧复杂度惩罚：`ComputeTechniquePenalty(...)`
8. 对称性惩罚：若 `SymmetryBreaks > MaxSymmetryBreaks`，加 `(SymmetryBreaks - MaxSymmetryBreaks) * 2`

### 2.2 评分档位

- `TotalScore <= 8`：Excellent
- `<= 22`：Good
- `<= 40`：Acceptable
- `> 40`：Rough

---

## 3. 难度配置（DifficultyProfile）

每个难度定义了目标区间：

- clues 区间：`MinClues ~ MaxClues`
- 分布下限：每行/列/宫最少 givens
- 逻辑步数目标区间
- 猜测次数目标区间
- 深度目标区间
- 技巧权重目标区间
- 最大对称破坏量

> 这些参数是“目标分布”，并不是硬约束。最终通过评分最小化选出候选题面。

---

## 4. 生成流程细节（GeneratePuzzle）

### 4.1 主流程

1. 读取难度 profile
2. 多次尝试生成候选题（当前 Expert 尝试次数高于其他难度）
3. 对每个候选题做完整评分
4. 选取 `TotalScore` 最小的题
5. 提前终止条件：
   - `TotalScore == 0` 立即结束
   - Expert 放宽到较低分阈值时可提前结束

### 4.2 快速生成策略（GeneratePuzzleByDiggingFast）

当前所有难度都采用“先终盘，后挖洞”策略：

1. 先随机生成完整终盘 `GenerateSolvedGrid`
2. 按目标 clues 数量做挖洞
3. 每次挖洞后做唯一解检查（`CountSolutions(..., limit=2)`）
4. 挖洞完成后做分布补偿（必要时回填）
5. 可选做对称优化

### 4.3 对称挖洞

默认使用中心对称分组：

- 普通格：成对移除 `(r,c)` 与 `(8-r,8-c)`
- 中心格 `(4,4)`：单点移除

优势：

- 题面视觉更自然
- 更接近常见数独题库排布

---

## 5. 求解分析器（AnalyzeComplexity）

### 5.1 分析目标

不是只判断“可解”，而是估计“人类体感难度”。

分析器先运行逻辑技巧序列；如果仍未解完，再运行搜索估计猜测与深度。

### 5.2 候选状态

内部先把题面转换为候选集合网格 `HashSet<int>[9,9]`，每个格子记录可选数字。

当技巧应用后会更新候选集合，若某格候选变空则判定矛盾。

### 5.3 技巧执行顺序（循环）

每轮按以下优先级尝试一步：

1. Naked Single
2. Hidden Single
3. Locked Candidates
4. Naked Pair
5. Hidden Pair
6. X-Wing
7. Swordfish

若某一步成功，记录一次技巧日志并立即进入下一轮。

若所有已启用技巧都无法推进，逻辑阶段结束。

### 5.3.1 可配置技巧表

引擎内部维护 `TechniqueTuningTable`，每个技巧都包含：

- `Order`：技巧尝试顺序，数值越小越先尝试
- `Weight`：该技巧在 `TechniqueWeight` 中的权重
- `Enabled`：是否启用该技巧

可通过 `SudokuEngine.ConfigureTechnique(...)` 动态修改：

- 调整单个技巧的顺序
- 调整单个技巧的权重
- 开/关某个技巧

这使得不同难度风格可以共享同一套分析器，但拥有不同的“口味”。

### 5.4 技巧日志与组合复杂度

分析器逐步记录技巧序列，例如：

- `NakedSingle -> NakedSingle -> HiddenSingle -> LockedCandidates -> NakedPair`

从序列中计算：

- `TechniqueVariety`：出现过的技巧种类数（若进入搜索则再加入 Guess）
- `ComboTransitions`：相邻步骤的技巧切换次数（含逻辑到 Guess 的切换）

这两项用来衡量“技巧组合复杂度”，比只看 clues 更接近主观体感。

---

## 6. 已实现技巧细节

### 6.1 Naked Single（裸单）

条件：某格候选集仅剩 1 个数字。

动作：该格定值，并从同行/同列/同宫对等格候选中删掉该数字。

### 6.2 Hidden Single（隐单）

条件：在某个 unit（行/列/宫）内，某数字只出现在 1 个候选格。

动作：该格直接定为该数字。

### 6.3 Locked Candidates（锁定候选，Pointing/Claiming 基础）

条件：在一个 3x3 宫内，数字 d 的候选格都落在同一行（或同一列）。

动作：在该行（或列）的宫外区域删除 d。

### 6.4 Naked Pair（裸对）

条件：一个 unit 内存在两格，候选都恰好是同一对数字 `{a,b}`。

动作：unit 内其他格删除 `a,b`。

### 6.5 Hidden Pair（隐对）

条件：一个 unit 内数字 `a,b` 都只出现在同两格中（顺序可交换）。

动作：这两格候选收缩为 `{a,b}`。

### 6.6 X-Wing

条件：某数字在两行中分别只出现在同两列，或者在两列中分别只出现在同两行，形成一个 2x2 鱼结构。

动作：删除该数字在对应列（或行）的其他位置候选。

### 6.7 Swordfish

条件：某数字在三行中分别只出现在最多三列，且这三行的列集合并集正好为三列；列方向同理。

动作：删除该数字在对应列（或行）的其他位置候选。

---

## 7. 搜索指标（Guess/Depth）

逻辑技巧停滞后，将候选状态转回部分填充网格，并执行回溯求解估计：

- `GuessCount`：分支点候选数大于 1 时计入
- `MaxSearchDepth`：递归最深层级

在评分中这两项权重高，用于区分真正困难题。

---

## 8. 技巧惩罚策略（ComputeTechniquePenalty）

当前按难度分层做软约束：

### Easy

- 技巧种类过多会被罚
- 组合切换过频会被罚
- 出现 Locked/Pair 等复杂技巧会显著加罚
- 出现 X-Wing / Swordfish 会显著加罚

### Medium

- 需要一定技巧多样性
- 需要至少基础组合切换
- Pair 技巧过多会加罚
- 若 X-Wing / Swordfish 参与过深，会提高惩罚

### Hard / Expert

- 采用按难度分层的技巧表，Hard / Expert 会启用更偏高级的技巧顺序与权重
- 裸单/隐单出现过多会被罚，并且 Hard / Expert 对它们有更硬的上限
- LockedCandidates / NakedPair / HiddenPair 出现过多会被罚
- 若技巧种类或组合切换太少会被罚
- Expert 额外要求更高的技巧切换复杂度，并把 X-Wing / Swordfish 作为主要加分信号

换句话说，Hard / Expert 的目标不是“必须出现某个低阶技巧”，而是“尽量少依赖低阶技巧”。题面如果能被大量裸单或隐单直接推进，通常就会被判得更简单；而 Expert 里出现的 X-Wing / Swordfish 会显著拉高复杂度分数。

---

## 9. 设计取舍与性能说明

### 9.1 为什么不用“纯随机加数”

随机加数法在唯一解收敛上容易出现大量无效循环。

“先终盘后挖洞 + 唯一解检查”通常更稳定、更快。

### 9.2 为什么评分用软约束

真实难度是连续分布，硬阈值会导致大量候选题被丢弃。

用软惩罚可在速度与质量间折中，并可持续调参。

### 9.3 当前复杂度上限

技巧分析器是轻量版人类技巧集，重点覆盖主流中低到中高难度。

对于极端高难题，仍会落到搜索指标来区分。

---

## 10. 后续可扩展方向

可以继续在同一框架中接入：

- XY-Wing / XYZ-Wing
- Coloring / Chain
- ALS 相关高级技巧

建议扩展方式：

1. 每个技巧实现 `ApplyXXXStep`（单步推进）
2. 接入技巧序列日志
3. 给 `ComputeTechniquePenalty` 增加针对该技巧的难度权重
4. 用题库做离线校准（目标是“体感分层稳定”）
5. 通过 `TechniqueTuningTable` 调整技巧顺序和权重

---

## 11. 代码定位

评分与生成核心：

- `SudokuEngine.GeneratePuzzle`
- `SudokuEngine.RatePuzzle`
- `SudokuEngine.GeneratePuzzleByDiggingFast`
- `SudokuEngine.AnalyzeComplexity`
- `SudokuEngine.ComputeTechniquePenalty`

技巧实现：

- `ApplyNakedSingleStep`
- `ApplyHiddenSingleStep`
- `ApplyLockedCandidatesStep`
- `ApplyNakedPairStep`
- `ApplyHiddenPairStep`
- `ApplyXWingStep`
- `ApplySwordfishStep`

如果你要调“难度风格”，优先改：

1. `DifficultyProfile` 参数
2. `ComputeTechniquePenalty` 的权重
3. 生成尝试次数与提前终止阈值
4. `TechniqueTuningTable` 的顺序和单技巧权重
