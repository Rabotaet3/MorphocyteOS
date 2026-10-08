using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private sealed record RuleEditState(DomainRule[] Rules, string[] Folders, bool Dirty);
    private readonly List<RuleEditState> _undoRules = new(), _redoRules = new();
    private RuleEditState? _historyState;
    private DomainRule[] _appliedRules = Array.Empty<DomainRule>();
    private bool _restoringRuleHistory;

    private RuleEditState CaptureRuleEditState(bool dirty) => new(_rules.Select(rule => rule.Copy()).ToArray(), _folderOrder.ToArray(), dirty);
    private static string EditIdentity(DomainRule rule) => rule.Kind + "\u001f" + rule.Value;
    private static string EditValue(DomainRule rule) => string.Join("\u001f", rule.Kind, rule.Value, rule.Route, rule.Extra, rule.Enabled, rule.Folder);

    private void ResetRuleHistory()
    {
        _undoRules.Clear(); _redoRules.Clear();
        _historyState = CaptureRuleEditState(_dirty);
        _appliedRules = _document is null ? Array.Empty<DomainRule>() : MergeRuleUiState(_document.Rules,
            _settings.GetRuleState(_document.Path)?.Rules ?? _document.Rules).Select(rule => rule.Copy()).ToArray();
        RefreshRuleHistoryControls();
    }

    private void TrackRuleEdit(bool dirty)
    {
        if (_restoringRuleHistory || _loading) return;
        var next = CaptureRuleEditState(dirty);
        if (_historyState is { } prior && (!prior.Rules.Select(EditValue).SequenceEqual(next.Rules.Select(EditValue))
            || !prior.Folders.SequenceEqual(next.Folders)))
        {
            _undoRules.Add(prior);
            // Bound both the number of edits and the memory occupied by very large rule lists.
            while (_undoRules.Count > 50 || _undoRules.Count > 1 && _undoRules.Sum(state => state.Rules.Length) > 100000)
                _undoRules.RemoveAt(0);
            _redoRules.Clear();
        }
        _historyState = next;
        RefreshRuleHistoryControls();
    }

    internal async Task UndoRuleEditAsync(bool redo = false)
    {
        await RunExclusiveAsync(async () =>
        {
            var from = redo ? _redoRules : _undoRules;
            var to = redo ? _undoRules : _redoRules;
            if (from.Count == 0 || _historyState is null || _document is null) return;
            var state = from[^1];
            var previous = CaptureRuleEditState(_dirty);
            var previousDraft = _settings.GetDraft(_document.Path);
            var hadFolderOrder = _settings.ProfileFolderOrders.TryGetValue(_document.Path, out var previousOrder);
            var previousDraftSaved = _draftSaved;
            _restoringRuleHistory = true;
            try
            {
                CancelRuleDrag(); _selectionAnchorRule = null;
                _folderOrder.Clear(); _folderOrder.AddRange(state.Folders);
                ReplaceRules(state.Rules);
                SetDirty(state.Dirty);
                if (_dirty) await SaveDraftAsync();
                else
                {
                    _settings.RemoveDraft(_document.Path);
                    _settings.PutFolderOrder(_document.Path, _folderOrder);
                    await SaveSettingsAsync();
                }
                from.RemoveAt(from.Count - 1); to.Add(previous);
                _historyState = CaptureRuleEditState(_dirty);
                SetLog(redo ? "Изменение правил повторено." : "Изменение правил отменено.");
            }
            catch
            {
                _folderOrder.Clear(); _folderOrder.AddRange(previous.Folders);
                ReplaceRules(previous.Rules);
                if (previousDraft is null) _settings.RemoveDraft(_document.Path);
                else _settings.ProfileDrafts[_document.Path] = previousDraft;
                if (hadFolderOrder) _settings.ProfileFolderOrders[_document.Path] = previousOrder!;
                else _settings.ProfileFolderOrders.Remove(_document.Path);
                _draftSaved = previousDraftSaved; SetDirty(previous.Dirty);
                throw;
            }
            finally { _restoringRuleHistory = false; RefreshRuleHistoryControls(); }
        });
    }

    private async void RedoRules_Click(object sender, RoutedEventArgs e) => await UndoRuleEditAsync(redo: true);

    private bool HandleRuleHistoryKey(KeyEventArgs e)
    {
        if (_busy || _closing || _dialogBackdropVisible || _dragActive || !RoutesPage.IsVisible
            || Keyboard.FocusedElement is TextBoxBase || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return false;
        var redo = e.Key == Key.Y || e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key != Key.Z && e.Key != Key.Y) return false;
        e.Handled = true;
        _ = UndoRuleEditAsync(redo);
        return true;
    }

    private void RefreshRuleHistoryControls()
    {
        if (UndoButton is null || RedoRulesButton is null || ChangesSummaryText is null) return;
        UndoButton.IsEnabled = _undoRules.Count > 0 && !_busy && !_closing;
        RedoRulesButton.IsEnabled = _redoRules.Count > 0 && !_busy && !_closing;
        var old = _appliedRules.GroupBy(EditIdentity).ToDictionary(group => group.Key, group => new Queue<DomainRule>(group));
        var added = 0; var changed = 0;
        foreach (var rule in _rules)
        {
            if (!old.TryGetValue(EditIdentity(rule), out var group) || group.Count == 0) { added++; continue; }
            if (EditValue(group.Dequeue()) != EditValue(rule)) changed++;
        }
        var removed = old.Values.Sum(group => group.Count);
        ChangesSummaryText.Text = added + removed + changed == 0 ? "Есть неприменённые изменения"
            : $"Добавлено: {added} · Удалено: {removed} · Изменено: {changed}";
        ChangesSummaryText.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
    }
}
