using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IOPath = System.IO.Path;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SudokuGui.Core;

namespace SudokuGui;

public partial class MainWindow : Window
{
    private const int N = SudokuEngine.Size;
    private readonly SudokuEngine _engine = new();
    private readonly int[,] _puzzle = new int[N, N];
    private readonly int[,] _current = new int[N, N];
    private readonly CellVisual[,] _cellViews = new CellVisual[N, N];
    private readonly Stack<MoveRecord> _undoStack = new();
    private readonly Stack<MoveRecord> _redoStack = new();
    private readonly Dictionary<int, Button> _numberButtons = new();

    private readonly Brush _givenBrush = Brushes.Black;
    private readonly Brush _validBrush = Brushes.ForestGreen;
    private readonly Brush _invalidBrush = Brushes.IndianRed;
    private readonly Brush _candidateBrush = new SolidColorBrush(Color.FromRgb(90, 90, 100));
    private readonly Brush _selectedBackground = new SolidColorBrush(Color.FromRgb(236, 244, 255));
    private readonly Brush _normalBackground = Brushes.White;
    private readonly Brush _sameNumberCircleBrush = new SolidColorBrush(Color.FromRgb(66, 133, 244));
    private readonly Brush _numberButtonEnabledBackground = new SolidColorBrush(Color.FromRgb(22, 117, 201));
    private readonly Brush _numberButtonEnabledForeground = Brushes.White;
    private readonly Brush _numberButtonEnabledBorder = new SolidColorBrush(Color.FromRgb(16, 84, 145));
    private readonly Brush _numberButtonDisabledBackground = new SolidColorBrush(Color.FromRgb(220, 224, 232));
    private readonly Brush _numberButtonDisabledForeground = new SolidColorBrush(Color.FromRgb(118, 124, 136));
    private readonly Brush _numberButtonDisabledBorder = new SolidColorBrush(Color.FromRgb(186, 192, 203));

    private (int row, int col)? _selectedCell;
    private int _highlightedDigit;
    private DifficultyLevel _currentDifficulty = DifficultyLevel.Expert;
    private bool _isGameComplete;
    private bool _showCandidates = true;
    private bool _boardInitialized;
    private SnapshotState? _snapshot;

    public MainWindow()
    {
        InitializeComponent();
        BuildBoardUi();
        BuildNumberPad();
        GenerateAndLoadPuzzle();
    }

    private void BuildBoardUi()
    {
        BoardGrid.RowDefinitions.Clear();
        BoardGrid.ColumnDefinitions.Clear();

        for (int i = 0; i < N; i++)
        {
            BoardGrid.RowDefinitions.Add(new RowDefinition());
            BoardGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (int row = 0; row < N; row++)
        {
            for (int col = 0; col < N; col++)
            {
                var border = new Border
                {
                    BorderBrush = Brushes.Black,
                    BorderThickness = ComputeCellBorderThickness(row, col),
                    Background = _normalBackground
                };

                var contentGrid = new Grid();

                var candidateGrid = new UniformGrid
                {
                    Rows = 3,
                    Columns = 3,
                    Margin = new Thickness(2)
                };

                var candidateTexts = new TextBlock[9];
                for (int i = 0; i < 9; i++)
                {
                    var candidateText = new TextBlock
                    {
                        Text = string.Empty,
                        Foreground = _candidateBrush,
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    candidateTexts[i] = candidateText;
                    candidateGrid.Children.Add(candidateText);
                }

                var valueText = new TextBlock
                {
                    FontSize = 31,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = string.Empty
                };

                var sameNumberCircle = new Ellipse
                {
                    Stroke = _sameNumberCircleBrush,
                    StrokeThickness = 2.4,
                    Margin = new Thickness(5),
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false
                };

                contentGrid.Children.Add(candidateGrid);
                contentGrid.Children.Add(valueText);
                contentGrid.Children.Add(sameNumberCircle);
                border.Child = contentGrid;

                Grid.SetRow(border, row);
                Grid.SetColumn(border, col);
                BoardGrid.Children.Add(border);

                int capturedRow = row;
                int capturedCol = col;
                border.MouseLeftButtonDown += (_, _) => SelectCell(capturedRow, capturedCol);

                _cellViews[row, col] = new CellVisual(border, valueText, candidateGrid, candidateTexts, sameNumberCircle);
            }
        }

        _boardInitialized = true;
    }

    private void BuildNumberPad()
    {
        NumberPadPanel.Children.Clear();
        for (int n = 1; n <= 9; n++)
        {
            int captured = n;
            var btn = new Button
            {
                Content = n.ToString(),
                Width = 52,
                Height = 52,
                Margin = new Thickness(6),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Background = _numberButtonEnabledBackground,
                Foreground = _numberButtonEnabledForeground,
                BorderBrush = _numberButtonEnabledBorder,
                BorderThickness = new Thickness(1)
            };

            btn.Click += (_, _) => ApplyInputToSelectedCell(captured);
            btn.Loaded += (_, _) => btn.Clip = new EllipseGeometry(new Point(btn.Width / 2, btn.Height / 2), btn.Width / 2, btn.Height / 2);

            NumberPadPanel.Children.Add(btn);
            _numberButtons[captured] = btn;
        }
    }

    private void SelectCell(int row, int col)
    {
        int value = _current[row, col];
        bool isGiven = _puzzle[row, col] != 0;

        // Filled cells control same-number highlight switching.
        if (value != 0)
        {
            if (_highlightedDigit != value)
            {
                _highlightedDigit = value;
            }

            // Given cells are read-only: only update highlight.
            if (isGiven)
            {
                if (_selectedCell.HasValue)
                {
                    var (oldRow, oldCol) = _selectedCell.Value;
                    _cellViews[oldRow, oldCol].Border.Background = _normalBackground;
                }

                _selectedCell = null;
                RefreshBoard();
                return;
            }
        }

        if (_selectedCell.HasValue)
        {
            var (oldRow, oldCol) = _selectedCell.Value;
            _cellViews[oldRow, oldCol].Border.Background = _normalBackground;
        }

        _selectedCell = (row, col);
        _cellViews[row, col].Border.Background = _selectedBackground;
        RefreshBoard();
    }

    private void ApplyInputToSelectedCell(int value)
    {
        if (!_selectedCell.HasValue)
        {
            return;
        }

        var (row, col) = _selectedCell.Value;
        if (_puzzle[row, col] != 0)
        {
            return;
        }

        if (value >= 1 && value <= 9)
        {
            int used = CountDigitUsage(value);
            if (used >= 9 && _current[row, col] != value)
            {
                StatusTextBlock.Text = $"Digit {value} already used 9 times.";
                return;
            }
        }

        ApplyUserMove(row, col, value);
    }

    private void ClearSelectedCell()
    {
        if (!_selectedCell.HasValue)
        {
            return;
        }

        var (row, col) = _selectedCell.Value;
        if (_puzzle[row, col] != 0)
        {
            return;
        }

        ApplyUserMove(row, col, 0);
    }

    private void GenerateAndLoadPuzzle()
    {
        GeneratedPuzzle generated = _engine.GeneratePuzzle(_currentDifficulty);
        int[,] puzzle = generated.Puzzle;

        for (int row = 0; row < N; row++)
        {
            for (int col = 0; col < N; col++)
            {
                _puzzle[row, col] = puzzle[row, col];
                _current[row, col] = puzzle[row, col];
            }
        }

        _selectedCell = null;
        _undoStack.Clear();
        _redoStack.Clear();
        _isGameComplete = false;
        _highlightedDigit = 0;
        StatusTextBlock.Text =
            $"Difficulty: {generated.Profile.Name} | clues: {generated.Score.Clues} | score: {generated.Score.TotalScore} ({generated.Score.QualityBand})";
        RefreshBoard();
    }

    private void RefreshBoard()
    {
        for (int row = 0; row < N; row++)
        {
            for (int col = 0; col < N; col++)
            {
                var view = _cellViews[row, col];
                int value = _current[row, col];

                if (_selectedCell.HasValue && _selectedCell.Value == (row, col))
                {
                    view.Border.Background = _selectedBackground;
                }
                else
                {
                    view.Border.Background = _normalBackground;
                }

                view.SameNumberCircle.Visibility = _highlightedDigit != 0 && value == _highlightedDigit
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (value != 0)
                {
                    view.ValueText.Visibility = Visibility.Visible;
                    view.CandidateGrid.Visibility = Visibility.Collapsed;
                    view.ValueText.Text = value.ToString();

                    if (_puzzle[row, col] != 0)
                    {
                        view.ValueText.Foreground = _givenBrush;
                    }
                    else
                    {
                        bool valid = _engine.IsValidPlacement(_current, row, col, value, ignoreSelf: true);
                        view.ValueText.Foreground = valid ? _validBrush : _invalidBrush;
                    }
                }
                else
                {
                    view.ValueText.Visibility = Visibility.Collapsed;
                    view.CandidateGrid.Visibility = _showCandidates ? Visibility.Visible : Visibility.Collapsed;
                    view.ValueText.Text = string.Empty;

                    if (_showCandidates)
                    {
                        HashSet<int> candidates = _engine.GetCandidates(_current, row, col);
                        for (int i = 0; i < 9; i++)
                        {
                            view.CandidateTexts[i].Text = candidates.Contains(i + 1) ? (i + 1).ToString() : string.Empty;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 9; i++)
                        {
                            view.CandidateTexts[i].Text = string.Empty;
                        }
                    }
                }
            }
        }

        if (!_isGameComplete && _engine.IsCompletedAndValid(_current))
        {
            _isGameComplete = true;
            StatusTextBlock.Text = "Victory! Puzzle solved.";
            MessageBox.Show("恭喜，数独已完成！", "Victory", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        UpdateNumberPadState();
    }

    private static Thickness ComputeCellBorderThickness(int row, int col)
    {
        double left = col % 3 == 0 ? 2.2 : 0.6;
        double top = row % 3 == 0 ? 2.2 : 0.6;
        double right = col == N - 1 ? 2.2 : 0.6;
        double bottom = row == N - 1 ? 2.2 : 0.6;
        return new Thickness(left, top, right, bottom);
    }

    private void ApplyUserMove(int row, int col, int newValue)
    {
        if (_isGameComplete)
        {
            return;
        }

        int oldValue = _current[row, col];
        if (oldValue == newValue)
        {
            return;
        }

        _current[row, col] = newValue;

        if (_highlightedDigit != 0 && newValue != 0)
        {
            if (newValue != _highlightedDigit)
            {
                // New input differs from highlighted number: clear all circles.
                _highlightedDigit = 0;
            }
            // If newValue == _highlightedDigit, keep highlight and let RefreshBoard update circles.
        }

        _undoStack.Push(new MoveRecord(row, col, oldValue, newValue));
        _redoStack.Clear();

        StatusTextBlock.Text = $"Difficulty: {_currentDifficulty} | clues: {_engine.CountClues(_puzzle)} | undo: {_undoStack.Count}";
        RefreshBoard();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.Z)
            {
                Undo();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Y)
            {
                Redo();
                e.Handled = true;
                return;
            }
        }

        if (!_selectedCell.HasValue)
        {
            return;
        }

        int number = KeyToNumber(e.Key);
        if (number is >= 1 and <= 9)
        {
            ApplyInputToSelectedCell(number);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Back or Key.Delete or Key.D0 or Key.NumPad0)
        {
            ClearSelectedCell();
            e.Handled = true;
        }
    }

    private static int KeyToNumber(Key key)
    {
        return key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => 0
        };
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearSelectedCell();
    }

    private void NewPuzzleButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateAndLoadPuzzle();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = SaveGameToTimestampFile();
            StatusTextBlock.Text = $"Saved: {IOPath.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Save failed: {ex.Message}";
        }
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Load Sudoku Save",
                InitialDirectory = AppContext.BaseDirectory,
                Filter = "Sudoku Save (*.json)|*.json|All Files (*.*)|*.*",
                FileName = "sudoku_save_"
            };

            bool? selected = dialog.ShowDialog(this);
            if (selected != true || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                return;
            }

            LoadGameFromFile(dialog.FileName);
            StatusTextBlock.Text = $"Loaded: {IOPath.GetFileName(dialog.FileName)}";
            RefreshBoard();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Load failed: {ex.Message}";
        }
    }

    private void SnapButton_Click(object sender, RoutedEventArgs e)
    {
        _snapshot = CreateSnapshotFromCurrent();
        StatusTextBlock.Text = "Snapshot captured.";
    }

    private void RestoreSnapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            StatusTextBlock.Text = "No snapshot to restore.";
            return;
        }

        ApplySnapshot(_snapshot.Value);
        StatusTextBlock.Text = "Snapshot restored.";
        RefreshBoard();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        Undo();
    }

    private void RedoButton_Click(object sender, RoutedEventArgs e)
    {
        Redo();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        MoveRecord move = _undoStack.Pop();
        _current[move.Row, move.Col] = move.OldValue;
        _redoStack.Push(move);

        _isGameComplete = false;
        StatusTextBlock.Text = $"Difficulty: {_currentDifficulty} | clues: {_engine.CountClues(_puzzle)} | undo: {_undoStack.Count}";
        RefreshBoard();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        MoveRecord move = _redoStack.Pop();
        _current[move.Row, move.Col] = move.NewValue;
        _undoStack.Push(move);

        StatusTextBlock.Text = $"Difficulty: {_currentDifficulty} | clues: {_engine.CountClues(_puzzle)} | undo: {_undoStack.Count}";
        RefreshBoard();
    }

    private void DifficultyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DifficultyComboBox.SelectedItem is not ComboBoxItem item || item.Content is not string content)
        {
            return;
        }

        _currentDifficulty = content switch
        {
            "Easy" => DifficultyLevel.Easy,
            "Medium" => DifficultyLevel.Medium,
            "Hard" => DifficultyLevel.Hard,
            "Expert" => DifficultyLevel.Expert,
            _ => DifficultyLevel.Medium
        };
    }

    private void UpdateDifficultyComboSelection()
    {
        DifficultyComboBox.SelectedIndex = _currentDifficulty switch
        {
            DifficultyLevel.Easy => 0,
            DifficultyLevel.Medium => 1,
            DifficultyLevel.Hard => 2,
            DifficultyLevel.Expert => 3,
            _ => 3
        };
    }

    private void ShowCandidatesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _showCandidates = sender is CheckBox cb ? cb.IsChecked == true : true;

        if (!_boardInitialized)
        {
            return;
        }

        RefreshBoard();
    }

    private void ShowCandidatesToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCandidatesCheckBox.IsChecked = !(ShowCandidatesCheckBox.IsChecked == true);
    }

    private void UpdateNumberPadState()
    {
        for (int digit = 1; digit <= 9; digit++)
        {
            if (_numberButtons.TryGetValue(digit, out Button? btn))
            {
                bool enabled = CountDigitUsage(digit) < 9;
                btn.IsEnabled = enabled;
                btn.Background = enabled ? _numberButtonEnabledBackground : _numberButtonDisabledBackground;
                btn.Foreground = enabled ? _numberButtonEnabledForeground : _numberButtonDisabledForeground;
                btn.BorderBrush = enabled ? _numberButtonEnabledBorder : _numberButtonDisabledBorder;
                btn.Opacity = enabled ? 1.0 : 0.55;
            }
        }
    }

    private int CountDigitUsage(int digit)
    {
        int count = 0;
        for (int row = 0; row < N; row++)
        {
            for (int col = 0; col < N; col++)
            {
                if (_current[row, col] == digit)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private string SaveGameToTimestampFile()
    {
        string fileName = BuildUniqueSaveFileName();
        string fullPath = IOPath.Combine(AppContext.BaseDirectory, fileName);
        var save = new SaveFileData
        {
            Timestamp = DateTime.Now,
            Difficulty = _currentDifficulty.ToString(),
            Puzzle = FlattenGrid(_puzzle),
            Current = FlattenGrid(_current)
        };

        string json = JsonSerializer.Serialize(save, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(fullPath, json);
        return fullPath;
    }

    private string? GetLatestSavePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] files = Directory.GetFiles(baseDir, "sudoku_save_*.json", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            return null;
        }

        return files.OrderByDescending(IOPath.GetFileName).FirstOrDefault();
    }

    private void LoadGameFromFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        SaveFileData? save = JsonSerializer.Deserialize<SaveFileData>(json);
        if (save is null)
        {
            throw new InvalidOperationException("Saved data is empty.");
        }

        RestoreGridFromFlat(save.Puzzle, _puzzle);
        RestoreGridFromFlat(save.Current, _current);
        _currentDifficulty = ParseDifficulty(save.Difficulty);
        UpdateDifficultyComboSelection();
        _selectedCell = null;
        _highlightedDigit = 0;
        _isGameComplete = false;
        _undoStack.Clear();
        _redoStack.Clear();
    }

    private SnapshotState CreateSnapshotFromCurrent()
    {
        return new SnapshotState(
            Puzzle: FlattenGrid(_puzzle),
            Current: FlattenGrid(_current),
            Difficulty: _currentDifficulty,
            IsGameComplete: _isGameComplete,
            HighlightedDigit: _highlightedDigit,
            SelectedRow: _selectedCell?.row,
            SelectedCol: _selectedCell?.col);
    }

    private void ApplySnapshot(SnapshotState snapshot)
    {
        RestoreGridFromFlat(snapshot.Puzzle, _puzzle);
        RestoreGridFromFlat(snapshot.Current, _current);
        _currentDifficulty = snapshot.Difficulty;
        UpdateDifficultyComboSelection();
        _isGameComplete = snapshot.IsGameComplete;
        _highlightedDigit = snapshot.HighlightedDigit;
        _selectedCell = snapshot.SelectedRow.HasValue && snapshot.SelectedCol.HasValue
            ? (snapshot.SelectedRow.Value, snapshot.SelectedCol.Value)
            : null;
        _undoStack.Clear();
        _redoStack.Clear();
    }

    private static string BuildUniqueSaveFileName()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string fileName = $"sudoku_save_{stamp}.json";
        string fullPath = IOPath.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(fullPath))
        {
            return fileName;
        }

        string suffix = Guid.NewGuid().ToString("N")[..8];
        return $"sudoku_save_{stamp}_{suffix}.json";
    }

    private static int[] FlattenGrid(int[,] grid)
    {
        var result = new int[N * N];
        int k = 0;
        for (int row = 0; row < N; row++)
        {
            for (int col = 0; col < N; col++)
            {
                result[k++] = grid[row, col];
            }
        }

        return result;
    }

    private static void RestoreGridFromFlat(int[] source, int[,] target)
    {
        if (source.Length != N * N)
        {
            throw new InvalidOperationException("Invalid grid length in save file.");
        }

        int k = 0;
        for (int row = 0; row < N; row++)
        {
            for (int col = 0; col < N; col++)
            {
                target[row, col] = source[k++];
            }
        }
    }

    private static DifficultyLevel ParseDifficulty(string? value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out DifficultyLevel level))
        {
            return level;
        }

        return DifficultyLevel.Expert;
    }

    private readonly record struct MoveRecord(int Row, int Col, int OldValue, int NewValue);

    private readonly record struct SnapshotState(
        int[] Puzzle,
        int[] Current,
        DifficultyLevel Difficulty,
        bool IsGameComplete,
        int HighlightedDigit,
        int? SelectedRow,
        int? SelectedCol);

    private sealed class SaveFileData
    {
        public DateTime Timestamp { get; set; }

        public string Difficulty { get; set; } = DifficultyLevel.Expert.ToString();

        public int[] Puzzle { get; set; } = Array.Empty<int>();

        public int[] Current { get; set; } = Array.Empty<int>();
    }

    private sealed class CellVisual
    {
        public CellVisual(Border border, TextBlock valueText, UniformGrid candidateGrid, TextBlock[] candidateTexts, Ellipse sameNumberCircle)
        {
            Border = border;
            ValueText = valueText;
            CandidateGrid = candidateGrid;
            CandidateTexts = candidateTexts;
            SameNumberCircle = sameNumberCircle;
        }

        public Border Border { get; }

        public TextBlock ValueText { get; }

        public UniformGrid CandidateGrid { get; }

        public TextBlock[] CandidateTexts { get; }

        public Ellipse SameNumberCircle { get; }
    }
}