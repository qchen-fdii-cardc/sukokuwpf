using System;
using System.Collections.Generic;
using System.Linq;

namespace SudokuGui.Core;

public enum DifficultyLevel
{
    Easy,
    Medium,
    Hard,
    Expert
}

public enum SolverTechnique
{
    NakedSingle,
    HiddenSingle,
    LockedCandidates,
    NakedPair,
    HiddenPair,
    XWing,
    Swordfish,
    Guess
}

public readonly record struct TechniqueTuning(int Order, int Weight, bool Enabled);

public readonly record struct TechniquePolicy(
    int MaxNakedSingles,
    int MaxHiddenSingles,
    int MaxLockedCandidates,
    int MaxNakedPairs,
    int MaxHiddenPairs,
    int MinXWingSteps,
    int MinSwordfishSteps,
    int AdvancedBonusWeight,
    int LowTechniqueHardPenalty);

public readonly record struct DifficultyProfile(
    string Name,
    int MinClues,
    int MaxClues,
    int MinCluesPerRow,
    int MinCluesPerCol,
    int MinCluesPerBox,
    int TargetLogicMin,
    int TargetLogicMax,
    int TargetGuessMin,
    int TargetGuessMax,
    int TargetDepthMin,
    int TargetDepthMax,
    int TargetTechniqueWeightMin,
    int TargetTechniqueWeightMax,
    int MaxSymmetryBreaks);

public readonly record struct PuzzleScore(
    int Clues,
    int DistributionPenalty,
    int LogicSteps,
    int NakedSingleSteps,
    int HiddenSingleSteps,
    int LockedCandidatesSteps,
    int NakedPairSteps,
    int HiddenPairSteps,
    int XWingSteps,
    int SwordfishSteps,
    int GuessCount,
    int MaxSearchDepth,
    int TechniqueWeight,
    int TechniqueVariety,
    int ComboTransitions,
    int SymmetryBreaks,
    int TotalScore,
    string QualityBand);

public readonly record struct GeneratedPuzzle(int[,] Puzzle, DifficultyProfile Profile, PuzzleScore Score);

public sealed class SudokuEngine
{
    public const int Size = 9;

    private readonly Random _rng;
    private readonly Dictionary<DifficultyLevel, Dictionary<SolverTechnique, TechniqueTuning>> _techniqueTuningByDifficulty;
    private readonly Dictionary<DifficultyLevel, TechniquePolicy> _techniquePolicyByDifficulty;

    public SudokuEngine(Random? rng = null)
    {
        _rng = rng ?? new Random();
        _techniqueTuningByDifficulty = BuildDefaultTechniqueTuningByDifficulty();
        _techniquePolicyByDifficulty = BuildDefaultTechniquePolicies();
    }

    public IReadOnlyDictionary<SolverTechnique, TechniqueTuning> GetTechniqueTuningTable(DifficultyLevel level) => _techniqueTuningByDifficulty[level];

    public IReadOnlyDictionary<DifficultyLevel, IReadOnlyDictionary<SolverTechnique, TechniqueTuning>> TechniqueTuningTablesByDifficulty =>
        _techniqueTuningByDifficulty.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<SolverTechnique, TechniqueTuning>)kv.Value);

    public void ConfigureTechnique(DifficultyLevel level, SolverTechnique technique, int? order = null, int? weight = null, bool? enabled = null)
    {
        TechniqueTuning current = _techniqueTuningByDifficulty[level][technique];
        _techniqueTuningByDifficulty[level][technique] = new TechniqueTuning(
            Order: order ?? current.Order,
            Weight: weight ?? current.Weight,
            Enabled: enabled ?? current.Enabled);
    }

    public void ConfigureTechnique(SolverTechnique technique, int? order = null, int? weight = null, bool? enabled = null)
    {
        foreach (DifficultyLevel level in Enum.GetValues<DifficultyLevel>())
        {
            ConfigureTechnique(level, technique, order, weight, enabled);
        }
    }

    public GeneratedPuzzle GeneratePuzzle(DifficultyLevel level)
    {
        DifficultyProfile profile = GetDifficultyProfile(level);
        int maxAttempts = level == DifficultyLevel.Expert ? 48 : 36;

        int[,]? bestPuzzle = null;
        PuzzleScore bestScore = default;
        int bestObjective = int.MaxValue;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int[,] puzzle = GeneratePuzzleByDiggingFast(profile, enforceSymmetry: true);

            // Stage 3: score by solver complexity and choose best candidate.
            PuzzleScore score = RatePuzzle(puzzle, profile, level);
            if (score.TotalScore < bestObjective)
            {
                bestObjective = score.TotalScore;
                bestScore = score;
                bestPuzzle = CloneGrid(puzzle);
            }

            if (score.TotalScore == 0 || (level == DifficultyLevel.Expert && score.TotalScore <= 12))
            {
                break;
            }
        }

        if (bestPuzzle is null)
        {
            int[,] fallback = GeneratePuzzleByDiggingFast(profile, enforceSymmetry: true);
            bestPuzzle = fallback;
            bestScore = RatePuzzle(fallback, profile, level);
        }

        return new GeneratedPuzzle(bestPuzzle, profile, bestScore);
    }

    public PuzzleScore RatePuzzle(int[,] puzzle, DifficultyProfile profile)
    {
        DifficultyLevel level = DifficultyLevelFromProfile(profile.Name);
        return RatePuzzle(puzzle, profile, level);
    }

    public PuzzleScore RatePuzzle(int[,] puzzle, DifficultyLevel level)
    {
        DifficultyProfile profile = GetDifficultyProfile(level);
        return RatePuzzle(puzzle, profile, level);
    }

    private PuzzleScore RatePuzzle(int[,] puzzle, DifficultyProfile profile, DifficultyLevel level)
    {
        int clues = CountClues(puzzle);
        int distributionPenalty = ComputeDistributionPenalty(puzzle, profile);
        SolverComplexity complexity = AnalyzeComplexity(puzzle, level);
        int symmetryBreaks = CountSymmetryBreaks(puzzle);
        int techniquePenalty = ComputeTechniquePenalty(profile, level, complexity);
        int advancedBonus = ComputeAdvancedTechniqueBonus(level, complexity);

        int totalScore = 0;
        totalScore += RangePenalty(clues, profile.MinClues, profile.MaxClues, 6);
        totalScore += distributionPenalty * 4;
        totalScore += RangePenalty(complexity.LogicSteps, profile.TargetLogicMin, profile.TargetLogicMax, 3);
        totalScore += RangePenalty(complexity.GuessCount, profile.TargetGuessMin, profile.TargetGuessMax, 14);
        totalScore += RangePenalty(complexity.MaxDepth, profile.TargetDepthMin, profile.TargetDepthMax, 10);
        totalScore += RangePenalty(complexity.TechniqueWeight, profile.TargetTechniqueWeightMin, profile.TargetTechniqueWeightMax, 2);
        totalScore += techniquePenalty;
        totalScore -= advancedBonus;

        if (totalScore < 0)
        {
            totalScore = 0;
        }

        if (symmetryBreaks > profile.MaxSymmetryBreaks)
        {
            totalScore += (symmetryBreaks - profile.MaxSymmetryBreaks) * 2;
        }

        string band = totalScore switch
        {
            <= 8 => "Excellent",
            <= 22 => "Good",
            <= 40 => "Acceptable",
            _ => "Rough"
        };

        return new PuzzleScore(
            Clues: clues,
            DistributionPenalty: distributionPenalty,
            LogicSteps: complexity.LogicSteps,
            NakedSingleSteps: complexity.NakedSingles,
            HiddenSingleSteps: complexity.HiddenSingles,
            LockedCandidatesSteps: complexity.LockedCandidates,
            NakedPairSteps: complexity.NakedPairs,
            HiddenPairSteps: complexity.HiddenPairs,
            XWingSteps: complexity.XWings,
            SwordfishSteps: complexity.Swordfish,
            GuessCount: complexity.GuessCount,
            MaxSearchDepth: complexity.MaxDepth,
            TechniqueWeight: complexity.TechniqueWeight,
            TechniqueVariety: complexity.TechniqueVariety,
            ComboTransitions: complexity.ComboTransitions,
            SymmetryBreaks: symmetryBreaks,
            TotalScore: totalScore,
            QualityBand: band);
    }

    public HashSet<int> GetCandidates(int[,] grid, int row, int col)
    {
        var result = new HashSet<int>();
        if (grid[row, col] != 0)
        {
            return result;
        }

        for (int num = 1; num <= 9; num++)
        {
            if (IsSafe(grid, row, col, num))
            {
                result.Add(num);
            }
        }

        return result;
    }

    public bool IsValidPlacement(int[,] grid, int row, int col, int num, bool ignoreSelf)
    {
        for (int x = 0; x < Size; x++)
        {
            if (x != col && grid[row, x] == num)
            {
                return false;
            }
            if (x != row && grid[x, col] == num)
            {
                return false;
            }
        }

        int startRow = row - row % 3;
        int startCol = col - col % 3;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                int rr = startRow + i;
                int cc = startCol + j;
                if (ignoreSelf && rr == row && cc == col)
                {
                    continue;
                }
                if (grid[rr, cc] == num)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public bool IsCompletedAndValid(int[,] grid)
    {
        for (int row = 0; row < Size; row++)
        {
            for (int col = 0; col < Size; col++)
            {
                int value = grid[row, col];
                if (value == 0)
                {
                    return false;
                }

                if (!IsValidPlacement(grid, row, col, value, ignoreSelf: true))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public int CountClues(int[,] puzzle)
    {
        int clues = 0;
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (puzzle[r, c] != 0)
                {
                    clues++;
                }
            }
        }

        return clues;
    }

    public DifficultyProfile GetDifficultyProfile(DifficultyLevel level)
    {
        return level switch
        {
            DifficultyLevel.Easy => new DifficultyProfile(
                Name: "Easy",
                MinClues: 40,
                MaxClues: 50,
                MinCluesPerRow: 3,
                MinCluesPerCol: 3,
                MinCluesPerBox: 3,
                TargetLogicMin: 30,
                TargetLogicMax: 120,
                TargetGuessMin: 0,
                TargetGuessMax: 0,
                TargetDepthMin: 0,
                TargetDepthMax: 0,
                TargetTechniqueWeightMin: 8,
                TargetTechniqueWeightMax: 120,
                MaxSymmetryBreaks: 16),

            DifficultyLevel.Medium => new DifficultyProfile(
                Name: "Medium",
                MinClues: 33,
                MaxClues: 39,
                MinCluesPerRow: 2,
                MinCluesPerCol: 2,
                MinCluesPerBox: 2,
                TargetLogicMin: 40,
                TargetLogicMax: 140,
                TargetGuessMin: 0,
                TargetGuessMax: 2,
                TargetDepthMin: 0,
                TargetDepthMax: 2,
                TargetTechniqueWeightMin: 40,
                TargetTechniqueWeightMax: 230,
                MaxSymmetryBreaks: 22),

            DifficultyLevel.Hard => new DifficultyProfile(
                Name: "Hard",
                MinClues: 28,
                MaxClues: 32,
                MinCluesPerRow: 1,
                MinCluesPerCol: 1,
                MinCluesPerBox: 1,
                TargetLogicMin: 50,
                TargetLogicMax: 180,
                TargetGuessMin: 1,
                TargetGuessMax: 8,
                TargetDepthMin: 1,
                TargetDepthMax: 5,
                TargetTechniqueWeightMin: 80,
                TargetTechniqueWeightMax: 380,
                MaxSymmetryBreaks: 30),

            DifficultyLevel.Expert => new DifficultyProfile(
                Name: "Expert",
                MinClues: 24,
                MaxClues: 27,
                MinCluesPerRow: 1,
                MinCluesPerCol: 1,
                MinCluesPerBox: 1,
                TargetLogicMin: 60,
                TargetLogicMax: 220,
                TargetGuessMin: 3,
                TargetGuessMax: 20,
                TargetDepthMin: 2,
                TargetDepthMax: 9,
                TargetTechniqueWeightMin: 120,
                TargetTechniqueWeightMax: 700,
                MaxSymmetryBreaks: 40),

            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };
    }

    private int[,] GeneratePuzzleByDiggingFast(DifficultyProfile profile, bool enforceSymmetry)
    {
        int[,] solution = GenerateSolvedGrid();
        int[,] puzzle = CloneGrid(solution);

        int targetClues = _rng.Next(profile.MinClues, profile.MaxClues + 1);
        var removalGroups = enforceSymmetry
            ? BuildSymmetricRemovalGroups()
            : BuildSingleRemovalGroups();
        ShuffleInPlace(removalGroups);

        foreach (var group in removalGroups)
        {
            if (CountClues(puzzle) <= targetClues)
            {
                break;
            }

            if (group.Count == 1)
            {
                var (r, c) = group[0];
                if (puzzle[r, c] == 0)
                {
                    continue;
                }

                if (CountClues(puzzle) - 1 < profile.MinClues)
                {
                    continue;
                }

                int old = puzzle[r, c];
                puzzle[r, c] = 0;
                if (!HasUniqueSolution(puzzle))
                {
                    puzzle[r, c] = old;
                }

                continue;
            }

            var (r1, c1) = group[0];
            var (r2, c2) = group[1];
            if (puzzle[r1, c1] == 0 && puzzle[r2, c2] == 0)
            {
                continue;
            }

            int removeCount = (puzzle[r1, c1] != 0 ? 1 : 0) + (puzzle[r2, c2] != 0 ? 1 : 0);
            if (CountClues(puzzle) - removeCount < profile.MinClues)
            {
                continue;
            }

            int old1 = puzzle[r1, c1];
            int old2 = puzzle[r2, c2];
            puzzle[r1, c1] = 0;
            puzzle[r2, c2] = 0;

            if (!HasUniqueSolution(puzzle))
            {
                puzzle[r1, c1] = old1;
                puzzle[r2, c2] = old2;
            }
        }

        ImproveDistributionByAddingClues(puzzle, solution, profile);
        if (enforceSymmetry)
        {
            ImproveSymmetryByAddingPairs(puzzle, solution, profile);
        }

        return puzzle;
    }

    private int[,] GenerateSolvedGrid()
    {
        var grid = new int[Size, Size];
        if (!FillGridRandom(grid))
        {
            throw new InvalidOperationException("Failed to generate solved Sudoku grid.");
        }

        return grid;
    }

    private bool FillGridRandom(int[,] grid)
    {
        if (!FindFirstEmpty(grid, out int row, out int col))
        {
            return true;
        }

        List<int> candidates = GetCandidateList(grid, row, col);
        ShuffleInPlace(candidates);
        foreach (int value in candidates)
        {
            grid[row, col] = value;
            if (FillGridRandom(grid))
            {
                return true;
            }

            grid[row, col] = 0;
        }

        return false;
    }

    private static bool HasUniqueSolution(int[,] grid)
    {
        int[,] probe = CloneGrid(grid);
        int solutionCount = 0;
        CountSolutions(probe, ref solutionCount, 2);
        return solutionCount == 1;
    }

    private static List<List<(int row, int col)>> BuildSymmetricRemovalGroups()
    {
        var groups = new List<List<(int row, int col)>>();
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                int rr = Size - 1 - r;
                int cc = Size - 1 - c;
                if (r > rr || (r == rr && c > cc))
                {
                    continue;
                }

                if (r == rr && c == cc)
                {
                    groups.Add(new List<(int row, int col)> { (r, c) });
                }
                else
                {
                    groups.Add(new List<(int row, int col)> { (r, c), (rr, cc) });
                }
            }
        }

        return groups;
    }

    private static List<List<(int row, int col)>> BuildSingleRemovalGroups()
    {
        var groups = new List<List<(int row, int col)>>();
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                groups.Add(new List<(int row, int col)> { (r, c) });
            }
        }

        return groups;
    }

    private void ShuffleInPlace<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private void ImproveDistributionByAddingClues(int[,] puzzle, int[,] solution, DifficultyProfile profile)
    {
        for (int pass = 0; pass < 10; pass++)
        {
            if (MeetsDistribution(puzzle, profile) && MeetsClueRange(puzzle, profile))
            {
                return;
            }

            CountDistribution(puzzle, out int[] rowClues, out int[] colClues, out int[] boxClues);

            var candidatesToFill = new List<(int row, int col, int priority)>();
            for (int r = 0; r < Size; r++)
            {
                for (int c = 0; c < Size; c++)
                {
                    if (puzzle[r, c] != 0)
                    {
                        continue;
                    }

                    int box = (r / 3) * 3 + (c / 3);
                    int priority = 0;
                    if (rowClues[r] < profile.MinCluesPerRow)
                    {
                        priority += 2;
                    }
                    if (colClues[c] < profile.MinCluesPerCol)
                    {
                        priority += 2;
                    }
                    if (boxClues[box] < profile.MinCluesPerBox)
                    {
                        priority += 2;
                    }

                    if (priority > 0)
                    {
                        candidatesToFill.Add((r, c, priority));
                    }
                }
            }

            if (candidatesToFill.Count == 0)
            {
                break;
            }

            foreach (var candidate in candidatesToFill.OrderByDescending(x => x.priority).ThenBy(_ => _rng.Next()))
            {
                if (CountClues(puzzle) >= profile.MaxClues)
                {
                    return;
                }

                puzzle[candidate.row, candidate.col] = solution[candidate.row, candidate.col];
            }
        }
    }

    private void ImproveSymmetryByAddingPairs(int[,] puzzle, int[,] solution, DifficultyProfile profile)
    {
        int clues = CountClues(puzzle);
        if (clues >= profile.MaxClues)
        {
            return;
        }

        for (int pass = 0; pass < 8; pass++)
        {
            int breaksBefore = CountSymmetryBreaks(puzzle);
            if (breaksBefore <= profile.MaxSymmetryBreaks)
            {
                return;
            }

            var mismatches = new List<(int row, int col)>();
            for (int r = 0; r < Size; r++)
            {
                for (int c = 0; c < Size; c++)
                {
                    int rr = Size - 1 - r;
                    int cc = Size - 1 - c;
                    bool a = puzzle[r, c] != 0;
                    bool b = puzzle[rr, cc] != 0;
                    if (a != b && !a)
                    {
                        mismatches.Add((r, c));
                    }
                }
            }

            if (mismatches.Count == 0)
            {
                return;
            }

            foreach (var (r, c) in mismatches.OrderBy(_ => _rng.Next()))
            {
                if (CountClues(puzzle) >= profile.MaxClues)
                {
                    return;
                }

                puzzle[r, c] = solution[r, c];
            }
        }
    }

    private SolverComplexity AnalyzeComplexity(int[,] puzzle, DifficultyLevel level)
    {
        HashSet<int>[,] values = BuildCandidateState(puzzle);
        int logicSteps = 0;
        var techniqueCounts = Enum.GetValues<SolverTechnique>()
            .ToDictionary(t => t, _ => 0);
        var techniqueSequence = new List<SolverTechnique>();
        SolverTechnique[] orderedTechniques = _techniqueTuningByDifficulty[level]
            .Where(kv => kv.Value.Enabled && kv.Key != SolverTechnique.Guess)
            .OrderBy(kv => kv.Value.Order)
            .Select(kv => kv.Key)
            .ToArray();

        while (true)
        {
            if (TryApplyOneTechniqueStep(values, orderedTechniques, out SolverTechnique applied))
            {
                logicSteps++;
                techniqueCounts[applied]++;
                techniqueSequence.Add(applied);
                if (HasContradiction(values))
                {
                    break;
                }

                continue;
            }

            break;
        }

        int[,] reducedGrid = CandidateStateToGrid(values);

        if (IsSolved(reducedGrid))
        {
            int varietySolved = CountTechniqueVariety(techniqueSequence, guessCount: 0);
            int transitionsSolved = CountTransitions(techniqueSequence, guessCount: 0);
            int techniqueWeightSolved = ComputeTechniqueWeight(level, techniqueCounts, guessCount: 0);
            return new SolverComplexity(
                logicSteps,
                techniqueCounts[SolverTechnique.NakedSingle],
                techniqueCounts[SolverTechnique.HiddenSingle],
                techniqueCounts[SolverTechnique.LockedCandidates],
                techniqueCounts[SolverTechnique.NakedPair],
                techniqueCounts[SolverTechnique.HiddenPair],
                techniqueCounts[SolverTechnique.XWing],
                techniqueCounts[SolverTechnique.Swordfish],
                GuessCount: 0,
                MaxDepth: 0,
                techniqueWeightSolved,
                varietySolved,
                transitionsSolved);
        }

        int guessCount = 0;
        int maxDepth = 0;
        SolveWithSearch(reducedGrid, depth: 0, ref guessCount, ref maxDepth);
        int variety = CountTechniqueVariety(techniqueSequence, guessCount);
        int transitions = CountTransitions(techniqueSequence, guessCount);
        int techniqueWeight = ComputeTechniqueWeight(level, techniqueCounts, guessCount);
        return new SolverComplexity(
            logicSteps,
            techniqueCounts[SolverTechnique.NakedSingle],
            techniqueCounts[SolverTechnique.HiddenSingle],
            techniqueCounts[SolverTechnique.LockedCandidates],
            techniqueCounts[SolverTechnique.NakedPair],
            techniqueCounts[SolverTechnique.HiddenPair],
            techniqueCounts[SolverTechnique.XWing],
            techniqueCounts[SolverTechnique.Swordfish],
            guessCount,
            maxDepth,
            techniqueWeight,
            variety,
            transitions);
    }

    private bool TryApplyOneTechniqueStep(HashSet<int>[,] values, SolverTechnique[] orderedTechniques, out SolverTechnique applied)
    {
        foreach (SolverTechnique technique in orderedTechniques)
        {
            bool changed = technique switch
            {
                SolverTechnique.NakedSingle => ApplyNakedSingleStep(values),
                SolverTechnique.HiddenSingle => ApplyHiddenSingleStep(values),
                SolverTechnique.LockedCandidates => ApplyLockedCandidatesStep(values),
                SolverTechnique.NakedPair => ApplyNakedPairStep(values),
                SolverTechnique.HiddenPair => ApplyHiddenPairStep(values),
                SolverTechnique.XWing => ApplyXWingStep(values),
                SolverTechnique.Swordfish => ApplySwordfishStep(values),
                _ => false
            };

            if (changed)
            {
                applied = technique;
                return true;
            }
        }

        applied = SolverTechnique.NakedSingle;
        return false;
    }

    private static HashSet<int>[,] BuildCandidateState(int[,] puzzle)
    {
        var values = new HashSet<int>[Size, Size];
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (puzzle[r, c] != 0)
                {
                    values[r, c] = new HashSet<int> { puzzle[r, c] };
                    continue;
                }

                var candidates = new HashSet<int>();
                for (int num = 1; num <= 9; num++)
                {
                    if (IsSafe(puzzle, r, c, num))
                    {
                        candidates.Add(num);
                    }
                }

                values[r, c] = candidates;
            }
        }

        return values;
    }

    private static int[,] CandidateStateToGrid(HashSet<int>[,] values)
    {
        var grid = new int[Size, Size];
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (values[r, c].Count == 1)
                {
                    grid[r, c] = values[r, c].First();
                }
            }
        }

        return grid;
    }

    private static bool HasContradiction(HashSet<int>[,] values)
    {
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (values[r, c].Count == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ApplyNakedSingleStep(HashSet<int>[,] values)
    {
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (values[r, c].Count != 1)
                {
                    continue;
                }

                int v = values[r, c].First();
                if (EliminateFromPeers(values, r, c, v))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool EliminateFromPeers(HashSet<int>[,] values, int row, int col, int value)
    {
        for (int c = 0; c < Size; c++)
        {
            if (c != col && values[row, c].Count > 1 && values[row, c].Remove(value))
            {
                return true;
            }
        }

        for (int r = 0; r < Size; r++)
        {
            if (r != row && values[r, col].Count > 1 && values[r, col].Remove(value))
            {
                return true;
            }
        }

        int sr = (row / 3) * 3;
        int sc = (col / 3) * 3;
        for (int dr = 0; dr < 3; dr++)
        {
            for (int dc = 0; dc < 3; dc++)
            {
                int rr = sr + dr;
                int cc = sc + dc;
                if ((rr != row || cc != col) && values[rr, cc].Count > 1 && values[rr, cc].Remove(value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ApplyHiddenSingleStep(HashSet<int>[,] values)
    {
        for (int r = 0; r < Size; r++)
        {
            if (ApplyHiddenSingleInCells(values, GetRowCells(r)))
            {
                return true;
            }
        }

        for (int c = 0; c < Size; c++)
        {
            if (ApplyHiddenSingleInCells(values, GetColCells(c)))
            {
                return true;
            }
        }

        for (int br = 0; br < 3; br++)
        {
            for (int bc = 0; bc < 3; bc++)
            {
                if (ApplyHiddenSingleInCells(values, GetBoxCells(br * 3, bc * 3)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ApplyHiddenSingleInCells(HashSet<int>[,] values, List<(int row, int col)> cells)
    {
        for (int num = 1; num <= 9; num++)
        {
            int count = 0;
            int hitRow = -1;
            int hitCol = -1;
            foreach (var (row, col) in cells)
            {
                if (values[row, col].Contains(num))
                {
                    count++;
                    hitRow = row;
                    hitCol = col;
                }
            }

            if (count == 1 && values[hitRow, hitCol].Count > 1)
            {
                values[hitRow, hitCol].Clear();
                values[hitRow, hitCol].Add(num);
                return true;
            }
        }

        return false;
    }

    private static bool ApplyLockedCandidatesStep(HashSet<int>[,] values)
    {
        for (int br = 0; br < 3; br++)
        {
            for (int bc = 0; bc < 3; bc++)
            {
                int sr = br * 3;
                int sc = bc * 3;
                for (int num = 1; num <= 9; num++)
                {
                    var hits = new List<(int row, int col)>();
                    for (int dr = 0; dr < 3; dr++)
                    {
                        for (int dc = 0; dc < 3; dc++)
                        {
                            int r = sr + dr;
                            int c = sc + dc;
                            if (values[r, c].Count > 1 && values[r, c].Contains(num))
                            {
                                hits.Add((r, c));
                            }
                        }
                    }

                    if (hits.Count < 2)
                    {
                        continue;
                    }

                    bool sameRow = hits.All(h => h.row == hits[0].row);
                    if (sameRow)
                    {
                        int row = hits[0].row;
                        for (int c = 0; c < Size; c++)
                        {
                            if (c >= sc && c < sc + 3)
                            {
                                continue;
                            }

                            if (values[row, c].Count > 1 && values[row, c].Remove(num))
                            {
                                return true;
                            }
                        }
                    }

                    bool sameCol = hits.All(h => h.col == hits[0].col);
                    if (sameCol)
                    {
                        int col = hits[0].col;
                        for (int r = 0; r < Size; r++)
                        {
                            if (r >= sr && r < sr + 3)
                            {
                                continue;
                            }

                            if (values[r, col].Count > 1 && values[r, col].Remove(num))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool ApplyNakedPairStep(HashSet<int>[,] values)
    {
        for (int r = 0; r < Size; r++)
        {
            if (ApplyNakedPairInUnit(values, GetRowCells(r)))
            {
                return true;
            }
        }

        for (int c = 0; c < Size; c++)
        {
            if (ApplyNakedPairInUnit(values, GetColCells(c)))
            {
                return true;
            }
        }

        for (int br = 0; br < 3; br++)
        {
            for (int bc = 0; bc < 3; bc++)
            {
                if (ApplyNakedPairInUnit(values, GetBoxCells(br * 3, bc * 3)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ApplyNakedPairInUnit(HashSet<int>[,] values, List<(int row, int col)> cells)
    {
        var pairCells = cells.Where(c => values[c.row, c.col].Count == 2).ToList();
        for (int i = 0; i < pairCells.Count; i++)
        {
            for (int j = i + 1; j < pairCells.Count; j++)
            {
                var a = pairCells[i];
                var b = pairCells[j];
                if (!values[a.row, a.col].SetEquals(values[b.row, b.col]))
                {
                    continue;
                }

                int[] pair = values[a.row, a.col].ToArray();
                foreach (var (row, col) in cells)
                {
                    if ((row == a.row && col == a.col) || (row == b.row && col == b.col))
                    {
                        continue;
                    }

                    if (values[row, col].Count <= 1)
                    {
                        continue;
                    }

                    if (values[row, col].Remove(pair[0]) | values[row, col].Remove(pair[1]))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ApplyHiddenPairStep(HashSet<int>[,] values)
    {
        for (int r = 0; r < Size; r++)
        {
            if (ApplyHiddenPairInUnit(values, GetRowCells(r)))
            {
                return true;
            }
        }

        for (int c = 0; c < Size; c++)
        {
            if (ApplyHiddenPairInUnit(values, GetColCells(c)))
            {
                return true;
            }
        }

        for (int br = 0; br < 3; br++)
        {
            for (int bc = 0; bc < 3; bc++)
            {
                if (ApplyHiddenPairInUnit(values, GetBoxCells(br * 3, bc * 3)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ApplyHiddenPairInUnit(HashSet<int>[,] values, List<(int row, int col)> cells)
    {
        var byDigit = new Dictionary<int, List<(int row, int col)>>();
        for (int d = 1; d <= 9; d++)
        {
            byDigit[d] = new List<(int row, int col)>();
        }

        foreach (var (row, col) in cells)
        {
            if (values[row, col].Count <= 1)
            {
                continue;
            }

            foreach (int d in values[row, col])
            {
                byDigit[d].Add((row, col));
            }
        }

        for (int d1 = 1; d1 <= 8; d1++)
        {
            if (byDigit[d1].Count != 2)
            {
                continue;
            }

            for (int d2 = d1 + 1; d2 <= 9; d2++)
            {
                if (byDigit[d2].Count != 2)
                {
                    continue;
                }

                var a1 = byDigit[d1][0];
                var a2 = byDigit[d1][1];
                var b1 = byDigit[d2][0];
                var b2 = byDigit[d2][1];
                bool sameCells =
                    (a1 == b1 && a2 == b2) ||
                    (a1 == b2 && a2 == b1);

                if (!sameCells)
                {
                    continue;
                }

                var pairSet = new HashSet<int> { d1, d2 };
                if (!values[a1.row, a1.col].SetEquals(pairSet))
                {
                    values[a1.row, a1.col].IntersectWith(pairSet);
                    return true;
                }

                if (!values[a2.row, a2.col].SetEquals(pairSet))
                {
                    values[a2.row, a2.col].IntersectWith(pairSet);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ApplyXWingStep(HashSet<int>[,] values)
    {
        for (int digit = 1; digit <= 9; digit++)
        {
            if (ApplyFishPatternStep(values, digit, 2))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ApplySwordfishStep(HashSet<int>[,] values)
    {
        for (int digit = 1; digit <= 9; digit++)
        {
            if (ApplyFishPatternStep(values, digit, 3))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ApplyFishPatternStep(HashSet<int>[,] values, int digit, int size)
    {
        var rowCandidates = new List<(int index, List<int> positions)>();
        for (int row = 0; row < Size; row++)
        {
            var cols = new List<int>();
            for (int col = 0; col < Size; col++)
            {
                if (values[row, col].Contains(digit))
                {
                    cols.Add(col);
                }
            }

            if (cols.Count >= 2 && cols.Count <= size)
            {
                rowCandidates.Add((row, cols));
            }
        }

        if (ApplyFishOnRows(values, digit, size, rowCandidates))
        {
            return true;
        }

        var colCandidates = new List<(int index, List<int> positions)>();
        for (int col = 0; col < Size; col++)
        {
            var rows = new List<int>();
            for (int row = 0; row < Size; row++)
            {
                if (values[row, col].Contains(digit))
                {
                    rows.Add(row);
                }
            }

            if (rows.Count >= 2 && rows.Count <= size)
            {
                colCandidates.Add((col, rows));
            }
        }

        return ApplyFishOnCols(values, digit, size, colCandidates);
    }

    private static bool ApplyFishOnRows(HashSet<int>[,] values, int digit, int size, List<(int index, List<int> positions)> candidates)
    {
        if (candidates.Count < size)
        {
            return false;
        }

        foreach (var combo in Combinations(candidates, size))
        {
            var unionCols = new HashSet<int>(combo.SelectMany(x => x.positions));
            if (unionCols.Count != size)
            {
                continue;
            }

            bool changed = false;
            foreach (int row in Enumerable.Range(0, Size).Except(combo.Select(x => x.index)))
            {
                foreach (int col in unionCols)
                {
                    if (values[row, col].Count > 1 && values[row, col].Remove(digit))
                    {
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ApplyFishOnCols(HashSet<int>[,] values, int digit, int size, List<(int index, List<int> positions)> candidates)
    {
        if (candidates.Count < size)
        {
            return false;
        }

        foreach (var combo in Combinations(candidates, size))
        {
            var unionRows = new HashSet<int>(combo.SelectMany(x => x.positions));
            if (unionRows.Count != size)
            {
                continue;
            }

            bool changed = false;
            foreach (int col in Enumerable.Range(0, Size).Except(combo.Select(x => x.index)))
            {
                foreach (int row in unionRows)
                {
                    if (values[row, col].Count > 1 && values[row, col].Remove(digit))
                    {
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<List<T>> Combinations<T>(List<T> source, int choose)
    {
        return CombinationsCore(source, choose, 0, new List<T>());
    }

    private static IEnumerable<List<T>> CombinationsCore<T>(List<T> source, int choose, int start, List<T> prefix)
    {
        if (prefix.Count == choose)
        {
            yield return new List<T>(prefix);
            yield break;
        }

        for (int i = start; i < source.Count; i++)
        {
            prefix.Add(source[i]);
            foreach (var combo in CombinationsCore(source, choose, i + 1, prefix))
            {
                yield return combo;
            }

            prefix.RemoveAt(prefix.Count - 1);
        }
    }

    private int ComputeTechniqueWeight(DifficultyLevel level, Dictionary<SolverTechnique, int> counts, int guessCount)
    {
        int total = 0;
        IReadOnlyDictionary<SolverTechnique, TechniqueTuning> tuningTable = _techniqueTuningByDifficulty[level];
        foreach (var kv in counts)
        {
            TechniqueTuning tuning = tuningTable[kv.Key];
            total += kv.Value * tuning.Weight;
        }

        if (guessCount > 0)
        {
            total += guessCount * tuningTable[SolverTechnique.Guess].Weight;
        }

        return total;
    }

    private static int CountTechniqueVariety(List<SolverTechnique> sequence, int guessCount)
    {
        var kinds = new HashSet<SolverTechnique>(sequence);
        if (guessCount > 0)
        {
            kinds.Add(SolverTechnique.Guess);
        }

        return kinds.Count;
    }

    private static int CountTransitions(List<SolverTechnique> sequence, int guessCount)
    {
        int transitions = 0;
        for (int i = 1; i < sequence.Count; i++)
        {
            if (sequence[i] != sequence[i - 1])
            {
                transitions++;
            }
        }

        if (guessCount > 0 && sequence.Count > 0)
        {
            transitions++;
        }

        return transitions;
    }

    private int ComputeTechniquePenalty(DifficultyProfile profile, DifficultyLevel level, SolverComplexity complexity)
    {
        int penalty = 0;
        bool isEasy = profile.Name == "Easy";
        bool isMedium = profile.Name == "Medium";
        bool isHard = profile.Name == "Hard";
        bool isExpert = profile.Name == "Expert";
        TechniquePolicy policy = _techniquePolicyByDifficulty[level];

        if (isEasy)
        {
            if (complexity.TechniqueVariety > 2)
            {
                penalty += (complexity.TechniqueVariety - 2) * 6;
            }
            if (complexity.ComboTransitions > 1)
            {
                penalty += (complexity.ComboTransitions - 1) * 3;
            }
            if (complexity.LockedCandidates + complexity.NakedPairs + complexity.HiddenPairs > 0)
            {
                penalty += 14;
            }
            if (complexity.XWings + complexity.Swordfish > 0)
            {
                penalty += 24;
            }
        }

        if (isMedium)
        {
            if (complexity.TechniqueVariety < 2)
            {
                penalty += (2 - complexity.TechniqueVariety) * 8;
            }
            if (complexity.ComboTransitions == 0)
            {
                penalty += 4;
            }
            if (complexity.NakedPairs + complexity.HiddenPairs > 2)
            {
                penalty += (complexity.NakedPairs + complexity.HiddenPairs - 2) * 3;
            }
            if (complexity.Swordfish > 0)
            {
                penalty += complexity.Swordfish * 8;
            }
        }

        if (isHard || isExpert)
        {
            if (complexity.TechniqueVariety < 2)
            {
                penalty += 10;
            }
            if (complexity.ComboTransitions == 0)
            {
                penalty += 8;
            }
            penalty += HardCapPenalty(complexity.NakedSingles, policy.MaxNakedSingles, policy.LowTechniqueHardPenalty);
            penalty += HardCapPenalty(complexity.HiddenSingles, policy.MaxHiddenSingles, policy.LowTechniqueHardPenalty);
            penalty += HardCapPenalty(complexity.LockedCandidates, policy.MaxLockedCandidates, policy.LowTechniqueHardPenalty);
            penalty += HardCapPenalty(complexity.NakedPairs, policy.MaxNakedPairs, policy.LowTechniqueHardPenalty);
            penalty += HardCapPenalty(complexity.HiddenPairs, policy.MaxHiddenPairs, policy.LowTechniqueHardPenalty);

            penalty += complexity.NakedSingles * (isExpert ? 16 : 12);
            penalty += complexity.HiddenSingles * (isExpert ? 12 : 8);
            penalty += complexity.LockedCandidates * (isExpert ? 7 : 5);
            penalty += complexity.NakedPairs * (isExpert ? 6 : 4);
            penalty += complexity.HiddenPairs * (isExpert ? 6 : 4);
        }

        if (isExpert)
        {
            if (complexity.NakedSingles > policy.MaxNakedSingles || complexity.HiddenSingles > policy.MaxHiddenSingles)
            {
                penalty += 20;
            }
            if (complexity.ComboTransitions < 2)
            {
                penalty += 6;
            }
            if (complexity.XWings < policy.MinXWingSteps)
            {
                penalty += 12;
            }
            if (complexity.Swordfish < policy.MinSwordfishSteps)
            {
                penalty += 14;
            }
        }

        return penalty;
    }

    private static int HardCapPenalty(int actual, int cap, int hardPenalty)
    {
        if (actual <= cap)
        {
            return 0;
        }

        return (actual - cap) * hardPenalty;
    }

    private int ComputeAdvancedTechniqueBonus(DifficultyLevel level, SolverComplexity complexity)
    {
        if (level != DifficultyLevel.Hard && level != DifficultyLevel.Expert)
        {
            return 0;
        }

        TechniquePolicy policy = _techniquePolicyByDifficulty[level];
        int bonus = 0;
        bonus += complexity.XWings * policy.AdvancedBonusWeight;
        bonus += complexity.Swordfish * (policy.AdvancedBonusWeight + 8);
        return bonus;
    }

    private static DifficultyLevel DifficultyLevelFromProfile(string profileName)
    {
        return profileName switch
        {
            "Easy" => DifficultyLevel.Easy,
            "Medium" => DifficultyLevel.Medium,
            "Hard" => DifficultyLevel.Hard,
            "Expert" => DifficultyLevel.Expert,
            _ => DifficultyLevel.Medium
        };
    }

    private bool SolveWithSearch(int[,] grid, int depth, ref int guessCount, ref int maxDepth)
    {
        if (!FindFirstEmpty(grid, out int row, out int col))
        {
            return true;
        }

        List<int> candidates = GetCandidateList(grid, row, col);
        if (candidates.Count == 0)
        {
            return false;
        }

        if (candidates.Count > 1)
        {
            guessCount++;
            if (depth > maxDepth)
            {
                maxDepth = depth;
            }
        }

        foreach (int value in candidates)
        {
            grid[row, col] = value;
            if (SolveWithSearch(grid, depth + 1, ref guessCount, ref maxDepth))
            {
                return true;
            }
            grid[row, col] = 0;
        }

        return false;
    }

    private int ApplyNakedSingles(int[,] grid)
    {
        int applied = 0;
        bool progress;

        do
        {
            progress = false;
            for (int row = 0; row < Size; row++)
            {
                for (int col = 0; col < Size; col++)
                {
                    if (grid[row, col] != 0)
                    {
                        continue;
                    }

                    List<int> candidates = GetCandidateList(grid, row, col);
                    if (candidates.Count == 1)
                    {
                        grid[row, col] = candidates[0];
                        applied++;
                        progress = true;
                    }
                }
            }
        } while (progress);

        return applied;
    }

    private int ApplyHiddenSingles(int[,] grid)
    {
        int applied = 0;
        bool progress;

        do
        {
            progress = false;

            for (int row = 0; row < Size; row++)
            {
                applied += ApplyHiddenSingleToUnit(grid, GetRowCells(row), ref progress);
            }

            for (int col = 0; col < Size; col++)
            {
                applied += ApplyHiddenSingleToUnit(grid, GetColCells(col), ref progress);
            }

            for (int boxRow = 0; boxRow < 3; boxRow++)
            {
                for (int boxCol = 0; boxCol < 3; boxCol++)
                {
                    applied += ApplyHiddenSingleToUnit(grid, GetBoxCells(boxRow * 3, boxCol * 3), ref progress);
                }
            }
        } while (progress);

        return applied;
    }

    private int ApplyHiddenSingleToUnit(int[,] grid, List<(int row, int col)> cells, ref bool progress)
    {
        int applied = 0;

        for (int num = 1; num <= 9; num++)
        {
            var possible = new List<(int row, int col)>();
            foreach (var (row, col) in cells)
            {
                if (grid[row, col] == 0 && IsSafe(grid, row, col, num))
                {
                    possible.Add((row, col));
                }
            }

            if (possible.Count == 1)
            {
                var (r, c) = possible[0];
                grid[r, c] = num;
                applied++;
                progress = true;
            }
        }

        return applied;
    }

    private static List<(int row, int col)> GetRowCells(int row)
    {
        var cells = new List<(int row, int col)>(9);
        for (int col = 0; col < Size; col++)
        {
            cells.Add((row, col));
        }

        return cells;
    }

    private static List<(int row, int col)> GetColCells(int col)
    {
        var cells = new List<(int row, int col)>(9);
        for (int row = 0; row < Size; row++)
        {
            cells.Add((row, col));
        }

        return cells;
    }

    private static List<(int row, int col)> GetBoxCells(int startRow, int startCol)
    {
        var cells = new List<(int row, int col)>(9);
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                cells.Add((startRow + i, startCol + j));
            }
        }

        return cells;
    }

    private int ComputeDistributionPenalty(int[,] puzzle, DifficultyProfile profile)
    {
        CountDistribution(puzzle, out int[] rowClues, out int[] colClues, out int[] boxClues);

        int penalty = 0;
        penalty += rowClues.Where(x => x < profile.MinCluesPerRow).Sum(x => profile.MinCluesPerRow - x);
        penalty += colClues.Where(x => x < profile.MinCluesPerCol).Sum(x => profile.MinCluesPerCol - x);
        penalty += boxClues.Where(x => x < profile.MinCluesPerBox).Sum(x => profile.MinCluesPerBox - x);
        return penalty;
    }

    private static int RangePenalty(int value, int min, int max, int weight)
    {
        if (value < min)
        {
            return (min - value) * weight;
        }

        if (value > max)
        {
            return (value - max) * weight;
        }

        return 0;
    }

    private int CountSymmetryBreaks(int[,] puzzle)
    {
        int breaks = 0;
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                int rr = Size - 1 - r;
                int cc = Size - 1 - c;
                bool a = puzzle[r, c] != 0;
                bool b = puzzle[rr, cc] != 0;
                if (a != b)
                {
                    breaks++;
                }
            }
        }

        return breaks / 2;
    }

    private static bool IsSolved(int[,] grid)
    {
        for (int row = 0; row < Size; row++)
        {
            for (int col = 0; col < Size; col++)
            {
                if (grid[row, col] == 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool MeetsDistribution(int[,] puzzle, DifficultyProfile profile)
    {
        CountDistribution(puzzle, out int[] rowClues, out int[] colClues, out int[] boxClues);
        return rowClues.All(x => x >= profile.MinCluesPerRow)
            && colClues.All(x => x >= profile.MinCluesPerCol)
            && boxClues.All(x => x >= profile.MinCluesPerBox);
    }

    private bool MeetsClueRange(int[,] puzzle, DifficultyProfile profile)
    {
        int clues = CountClues(puzzle);
        return clues >= profile.MinClues && clues <= profile.MaxClues;
    }

    private static void CountDistribution(int[,] puzzle, out int[] rowClues, out int[] colClues, out int[] boxClues)
    {
        rowClues = new int[Size];
        colClues = new int[Size];
        boxClues = new int[Size];

        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (puzzle[r, c] == 0)
                {
                    continue;
                }

                rowClues[r]++;
                colClues[c]++;
                int box = (r / 3) * 3 + (c / 3);
                boxClues[box]++;
            }
        }
    }

    private static List<int> GetCandidateList(int[,] grid, int row, int col)
    {
        var candidates = new List<int>(9);
        for (int num = 1; num <= 9; num++)
        {
            if (IsSafe(grid, row, col, num))
            {
                candidates.Add(num);
            }
        }

        return candidates;
    }

    private static bool IsSafe(int[,] grid, int row, int col, int num)
    {
        for (int x = 0; x < Size; x++)
        {
            if (grid[row, x] == num || grid[x, col] == num)
            {
                return false;
            }
        }

        int startRow = row - row % 3;
        int startCol = col - col % 3;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (grid[startRow + i, startCol + j] == num)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void CountSolutions(int[,] grid, ref int solutionCount, int limit)
    {
        if (solutionCount >= limit)
        {
            return;
        }

        if (!FindFirstEmpty(grid, out int row, out int col))
        {
            solutionCount++;
            return;
        }

        List<int> values = GetCandidateList(grid, row, col);
        foreach (int num in values)
        {
            grid[row, col] = num;
            CountSolutions(grid, ref solutionCount, limit);
            grid[row, col] = 0;

            if (solutionCount >= limit)
            {
                return;
            }
        }
    }

    private static bool FindFirstEmpty(int[,] grid, out int row, out int col)
    {
        int bestCount = int.MaxValue;
        row = -1;
        col = -1;

        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (grid[r, c] != 0)
                {
                    continue;
                }

                int candidateCount = 0;
                for (int n = 1; n <= 9; n++)
                {
                    if (IsSafe(grid, r, c, n))
                    {
                        candidateCount++;
                    }
                }

                if (candidateCount < bestCount)
                {
                    bestCount = candidateCount;
                    row = r;
                    col = c;

                    if (bestCount <= 1)
                    {
                        return true;
                    }
                }
            }
        }

        return row != -1;
    }

    private static bool TrySolveFirst(int[,] puzzle, out int[,] solution)
    {
        solution = CloneGrid(puzzle);
        return SolveFirst(solution);
    }

    private static bool SolveFirst(int[,] grid)
    {
        if (!FindFirstEmpty(grid, out int row, out int col))
        {
            return true;
        }

        List<int> candidates = GetCandidateList(grid, row, col);
        foreach (int num in candidates)
        {
            grid[row, col] = num;
            if (SolveFirst(grid))
            {
                return true;
            }

            grid[row, col] = 0;
        }

        return false;
    }

    private static int[,] CloneGrid(int[,] source)
    {
        var copy = new int[Size, Size];
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                copy[r, c] = source[r, c];
            }
        }

        return copy;
    }

    private readonly record struct SolverComplexity(
        int LogicSteps,
        int NakedSingles,
        int HiddenSingles,
        int LockedCandidates,
        int NakedPairs,
        int HiddenPairs,
        int XWings,
        int Swordfish,
        int GuessCount,
        int MaxDepth,
        int TechniqueWeight,
        int TechniqueVariety,
        int ComboTransitions);

    private static Dictionary<DifficultyLevel, Dictionary<SolverTechnique, TechniqueTuning>> BuildDefaultTechniqueTuningByDifficulty()
    {
        return new Dictionary<DifficultyLevel, Dictionary<SolverTechnique, TechniqueTuning>>
        {
            [DifficultyLevel.Easy] = new Dictionary<SolverTechnique, TechniqueTuning>
            {
                [SolverTechnique.NakedSingle] = new TechniqueTuning(1, 1, true),
                [SolverTechnique.HiddenSingle] = new TechniqueTuning(2, 2, true),
                [SolverTechnique.LockedCandidates] = new TechniqueTuning(3, 4, true),
                [SolverTechnique.NakedPair] = new TechniqueTuning(4, 6, true),
                [SolverTechnique.HiddenPair] = new TechniqueTuning(5, 8, true),
                [SolverTechnique.XWing] = new TechniqueTuning(90, 18, false),
                [SolverTechnique.Swordfish] = new TechniqueTuning(100, 26, false),
                [SolverTechnique.Guess] = new TechniqueTuning(110, 34, true)
            },
            [DifficultyLevel.Medium] = new Dictionary<SolverTechnique, TechniqueTuning>
            {
                [SolverTechnique.NakedSingle] = new TechniqueTuning(1, 1, true),
                [SolverTechnique.HiddenSingle] = new TechniqueTuning(2, 2, true),
                [SolverTechnique.LockedCandidates] = new TechniqueTuning(3, 4, true),
                [SolverTechnique.NakedPair] = new TechniqueTuning(4, 7, true),
                [SolverTechnique.HiddenPair] = new TechniqueTuning(5, 9, true),
                [SolverTechnique.XWing] = new TechniqueTuning(6, 15, true),
                [SolverTechnique.Swordfish] = new TechniqueTuning(7, 24, false),
                [SolverTechnique.Guess] = new TechniqueTuning(100, 30, true)
            },
            [DifficultyLevel.Hard] = new Dictionary<SolverTechnique, TechniqueTuning>
            {
                [SolverTechnique.NakedSingle] = new TechniqueTuning(1, 1, true),
                [SolverTechnique.HiddenSingle] = new TechniqueTuning(2, 2, true),
                [SolverTechnique.LockedCandidates] = new TechniqueTuning(3, 5, true),
                [SolverTechnique.NakedPair] = new TechniqueTuning(4, 8, true),
                [SolverTechnique.HiddenPair] = new TechniqueTuning(5, 10, true),
                [SolverTechnique.XWing] = new TechniqueTuning(6, 20, true),
                [SolverTechnique.Swordfish] = new TechniqueTuning(7, 30, true),
                [SolverTechnique.Guess] = new TechniqueTuning(100, 36, true)
            },
            [DifficultyLevel.Expert] = new Dictionary<SolverTechnique, TechniqueTuning>
            {
                [SolverTechnique.NakedSingle] = new TechniqueTuning(1, 1, true),
                [SolverTechnique.HiddenSingle] = new TechniqueTuning(2, 2, true),
                [SolverTechnique.LockedCandidates] = new TechniqueTuning(3, 5, true),
                [SolverTechnique.NakedPair] = new TechniqueTuning(4, 8, true),
                [SolverTechnique.HiddenPair] = new TechniqueTuning(5, 10, true),
                [SolverTechnique.XWing] = new TechniqueTuning(6, 28, true),
                [SolverTechnique.Swordfish] = new TechniqueTuning(7, 40, true),
                [SolverTechnique.Guess] = new TechniqueTuning(100, 40, true)
            }
        };
    }

    private static Dictionary<DifficultyLevel, TechniquePolicy> BuildDefaultTechniquePolicies()
    {
        return new Dictionary<DifficultyLevel, TechniquePolicy>
        {
            [DifficultyLevel.Easy] = new TechniquePolicy(
                MaxNakedSingles: 999,
                MaxHiddenSingles: 999,
                MaxLockedCandidates: 999,
                MaxNakedPairs: 999,
                MaxHiddenPairs: 999,
                MinXWingSteps: 999,
                MinSwordfishSteps: 999,
                AdvancedBonusWeight: 0,
                LowTechniqueHardPenalty: 0),
            [DifficultyLevel.Medium] = new TechniquePolicy(
                MaxNakedSingles: 999,
                MaxHiddenSingles: 999,
                MaxLockedCandidates: 6,
                MaxNakedPairs: 5,
                MaxHiddenPairs: 5,
                MinXWingSteps: 0,
                MinSwordfishSteps: 0,
                AdvancedBonusWeight: 0,
                LowTechniqueHardPenalty: 2),
            [DifficultyLevel.Hard] = new TechniquePolicy(
                MaxNakedSingles: 2,
                MaxHiddenSingles: 7,
                MaxLockedCandidates: 4,
                MaxNakedPairs: 3,
                MaxHiddenPairs: 3,
                MinXWingSteps: 0,
                MinSwordfishSteps: 0,
                AdvancedBonusWeight: 8,
                LowTechniqueHardPenalty: 12),
            [DifficultyLevel.Expert] = new TechniquePolicy(
                MaxNakedSingles: 1,
                MaxHiddenSingles: 4,
                MaxLockedCandidates: 2,
                MaxNakedPairs: 2,
                MaxHiddenPairs: 2,
                MinXWingSteps: 1,
                MinSwordfishSteps: 1,
                AdvancedBonusWeight: 16,
                LowTechniqueHardPenalty: 16)
        };
    }
}
